using System.Globalization;
using System.Net.Sockets;
using System.Xml;
using System.Xml.Linq;
using MxU.Slides.Production.Devices;

namespace MxU.Slides.Production.Vmix;

/// <summary>
/// vMix, through its web API (vMix's Settings › Web Controller; port 8088). Every command is
/// one GET: <c>http://host:8088/api/?Function=Cut</c>, with the function's settings added
/// (<c>&amp;Input=3</c>, <c>&amp;Duration=1000</c>). An input can be named by its number, its title
/// or its key. Plain <c>/api</c> answers with vMix's state as XML: its version, the inputs, what
/// is on preview and active, whether it streams or records.
/// <para>
/// HTTP has no connection to watch, so a poll thread asks for the state every 3 s and sets the
/// status light from the answer (and the inputs the pickers offer). A command while vMix
/// doesn't answer is skipped at once rather than waiting out a time limit. A command vMix
/// doesn't answer in time counts the same way: the ones after it are skipped at once until
/// the poll hears from vMix again, rather than each waiting its own 2 s.
/// </para>
/// </summary>
public sealed class VmixClient : ProductionDevice
{
    internal TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(2);

    internal TimeSpan PollEvery { get; set; } = TimeSpan.FromSeconds(3);

    private readonly string? password;
    private readonly ManualResetEventSlim stopping = new(false);
    private readonly Lock gate = new();
    private Thread? poller;
    private volatile bool online;
    private List<CatalogEntry> inputs = [];

    public VmixClient(DeviceSettings settings, string? password) : base(settings) => this.password = password;

    private string Where => $"{Settings.Host}:{Settings.EffectivePort.ToString(CultureInfo.InvariantCulture)}";

    public override DeviceCatalog Catalog
    {
        get
        {
            lock (gate) return DeviceCatalog.Empty with { Inputs = [.. inputs] };
        }
    }

    public override void Start()
    {
        if (poller is not null) return;
        SetStatus(DeviceState.Connecting, $"Connecting to {Where}…");
        poller = new Thread(Poll) { IsBackground = true, Name = $"MxU vMix: {Name}" };
        poller.Start();
    }

    // MARK: Commands

    /// <summary>The API path for a function and its settings (empty settings are left out). Pure, for the tests.</summary>
    public static string FunctionPath(string function, params (string Name, string? Value)[] settings) =>
        "/api/?Function=" + Uri.EscapeDataString(function)
        + string.Concat(settings.Where(s => !string.IsNullOrWhiteSpace(s.Value)).Select(s => $"&{s.Name}={Uri.EscapeDataString(s.Value!.Trim())}"));

    /// <summary>Cut: preview to output at once (or straight to <paramref name="input"/>).</summary>
    public Task<bool> Cut(string? input = null) => Call("Cut", FunctionPath("Cut", ("Input", input)));

    /// <summary>Fade: preview to output over <paramref name="durationMs"/> (vMix's own length when null).</summary>
    public Task<bool> Fade(string? input = null, int? durationMs = null) =>
        Call("Fade", FunctionPath("Fade", ("Input", input), ("Duration", durationMs is > 0 ? Ms(durationMs.Value) : null)));

    /// <summary>Overlay channel 1 to 4: brings an input in (the preview's, when none is named) or takes it out.</summary>
    public Task<bool> Overlay(int channel, string? input, bool on)
    {
        var name = $"OverlayInput{Math.Clamp(channel, 1, 4).ToString(CultureInfo.InvariantCulture)}{(on ? "In" : "Out")}";
        return Call($"Overlay {channel} {(on ? "in" : "out")}", on ? FunctionPath(name, ("Input", input)) : FunctionPath(name));
    }

    public Task<bool> Streaming(bool start) => Call(start ? "Start streaming" : "Stop streaming", FunctionPath(start ? "StartStreaming" : "StopStreaming"));

    public Task<bool> Recording(bool start) => Call(start ? "Start recording" : "Stop recording", FunctionPath(start ? "StartRecording" : "StopRecording"));

    public Task<bool> Preview(string input) => Call($"Preview: {input}", FunctionPath("PreviewInput", ("Input", input)));

    public Task<bool> Active(string input) => Call($"Output: {input}", FunctionPath("ActiveInput", ("Input", input)));

    private static string Ms(int value) => value.ToString(CultureInfo.InvariantCulture);

    private Task<bool> Call(string what, string path) => Enqueue(what, () =>
    {
        if (!online) return NotConnected(what);
        MiniHttp.Response reply;
        try
        {
            reply = Get(path);
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException)
        {
            online = false;
            Note($"{what}: {ex.Message}");
            SetStatus(DeviceState.Problem, $"No answer from {Where}; trying again…");
            return false;
        }
        if (reply.Status == 200)
        {
            Note($"sent {what}");
            return true;
        }
        Note($"{what}: vMix answered {reply.Status} {reply.Body.Trim()}");
        if (reply.Status == 401) SetStatus(DeviceState.Problem, "vMix asks for a user name and password");
        return false;
    });

    private MiniHttp.Response Get(string path) =>
        MiniHttp.Get(Settings.Host, Settings.EffectivePort, path, Settings.User, password, RequestTimeout);

    // MARK: Status

    private void Poll()
    {
        while (!IsDisposed)
        {
            try
            {
                var reply = Get("/api");
                if (reply.Status == 401)
                {
                    online = false;
                    SetStatus(DeviceState.Problem, "vMix asks for a user name and password");
                }
                else if (reply.Status != 200)
                {
                    online = false;
                    SetStatus(DeviceState.Problem, $"vMix answered {reply.Status}");
                }
                else
                {
                    var state = ReadState(reply.Body);
                    lock (gate) inputs = [.. state.Inputs];
                    online = true;
                    SetStatus(DeviceState.Online, state.Summary);
                }
            }
            catch (Exception ex)
            {
                // Whatever went wrong, the poll lives on: vMix may be starting, or restarting.
                if (online || Status.State != DeviceState.Problem) Note($"no answer: {ex.Message}");
                online = false;
                SetStatus(DeviceState.Problem, $"No answer from {Where}");
            }
            if (stopping.Wait(PollEvery)) return;
        }
    }

    /// <summary>What vMix's state XML says: its inputs, and a one-line summary for the status light.</summary>
    public sealed record VmixState(IReadOnlyList<CatalogEntry> Inputs, string Summary);

    public static VmixState ReadState(string xml)
    {
        var root = XDocument.Parse(xml).Root ?? throw new XmlException("Empty vMix state");
        var list = root.Element("inputs")?.Elements("input")
            .Select(i => (Number: int.TryParse((string?)i.Attribute("number"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0,
                Title: (string?)i.Attribute("title") ?? i.Value))
            .Where(i => i.Number > 0)
            .Select(i => new CatalogEntry(i.Number, i.Title))
            .ToList() ?? [];
        string? Title(string element) =>
            int.TryParse(root.Element(element)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                ? list.FirstOrDefault(i => i.Number == number)?.Name ?? $"input {number}"
                : null;
        var parts = new List<string> { $"vMix {root.Element("version")?.Value}".Trim() };
        if (Title("active") is { } active) parts.Add($"Output: {active}");
        if (string.Equals(root.Element("streaming")?.Value, "True", StringComparison.OrdinalIgnoreCase)) parts.Add("streaming");
        if (string.Equals(root.Element("recording")?.Value, "True", StringComparison.OrdinalIgnoreCase)) parts.Add("recording");
        return new VmixState(list, string.Join(" · ", parts));
    }

    public override void Dispose()
    {
        base.Dispose();
        stopping.Set();
    }
}
