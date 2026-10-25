using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MxU.Slides.Production.Devices;

namespace MxU.Slides.Production.Obs;

/// <summary>obs-websocket v5's sign-in sum.</summary>
public static class ObsAuth
{
    /// <summary>
    /// The answer to OBS's challenge. OBS never sees the password itself:
    /// <c>secret = base64(SHA-256(password + salt))</c>, then
    /// <c>answer = base64(SHA-256(secret + challenge))</c>. The salt is fixed for the password; the
    /// challenge is new for every connection, so an answer overheard once is no use again.
    /// </summary>
    public static string Response(string password, string salt, string challenge)
    {
        var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }
}

/// <summary>
/// OBS Studio, through obs-websocket v5 (built into OBS 28 and later: Tools › WebSocket Server
/// Settings). Messages are JSON, each with an "op" (what kind of message) and a "d" (its data):
/// <list type="number">
/// <item>OBS says Hello (op 0), with a challenge and salt when it has a password.</item>
/// <item>We Identify (op 1) with the answer (<see cref="ObsAuth"/>) and the events we want.</item>
/// <item>OBS says Identified (op 2), or closes with code 4009 when the password is wrong.</item>
/// <item>Then we send Requests (op 6: "SetCurrentProgramScene"…) each with an id, and OBS
/// answers each (op 7) with the same id; it also sends Events (op 5) when things change.</item>
/// </list>
/// One thread reads (and reconnects quietly when OBS closes, every 2 s at first, then less
/// often); commands run on the device's queue, each waiting up to 2 s for its answer. An OBS
/// that doesn't answer in time has stopped working (frozen, or its PC gone without a word):
/// the connection is dropped, so the commands after it are skipped at once rather than each
/// waiting out its own 2 s, and the reader signs in again when it can.
/// </summary>
public sealed class ObsClient : ProductionDevice
{
    internal TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(2);

    internal TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(2);

    internal TimeSpan FirstRetry { get; set; } = TimeSpan.FromSeconds(2);

    // The events asked for: Scenes (4), Transitions (16), Outputs (64), SceneItems (128), Ui (1024).
    private const int EventSubscriptions = 4 | 16 | 64 | 128 | 1024;

    private sealed class Pending
    {
        public ManualResetEventSlim Done { get; } = new(false);
        public JsonObject? Reply { get; set; }
    }

    private readonly string? password;
    private readonly ConcurrentDictionary<string, Pending> pending = new();
    private readonly ManualResetEventSlim stopping = new(false);
    private readonly Lock gate = new();
    private volatile MiniWebSocket? socket;
    private Thread? reader;
    private int refreshQueued;

    // Whether OBS has answered a request on this connection, and whether we dropped it for not answering.
    private volatile bool answered;
    private volatile bool stalled;

    private List<string> scenes = [];
    private List<string> transitions = [];
    private Dictionary<string, IReadOnlyList<string>> sceneSources = [];
    private string? programScene;
    private string? previewScene;
    private bool studioMode;
    private bool streaming;
    private bool recording;

    /// <param name="password">OBS's server password (from <see cref="DeviceSecrets"/>), or null when it has none.</param>
    public ObsClient(DeviceSettings settings, string? password) : base(settings) => this.password = password;

    private string Where => $"{Settings.Host}:{Settings.EffectivePort.ToString(CultureInfo.InvariantCulture)}";

    public string? ProgramScene
    {
        get
        {
            lock (gate) return programScene;
        }
    }

    public bool StudioMode
    {
        get
        {
            lock (gate) return studioMode;
        }
    }

    public override DeviceCatalog Catalog
    {
        get
        {
            lock (gate) return DeviceCatalog.Empty with { Scenes = [.. scenes], Transitions = [.. transitions], SceneSources = new Dictionary<string, IReadOnlyList<string>>(sceneSources) };
        }
    }

    public override void Start()
    {
        if (reader is not null) return;
        SetStatus(DeviceState.Connecting, $"Connecting to {Where}…");
        reader = new Thread(Run) { IsBackground = true, Name = $"MxU OBS: {Name}", Priority = ThreadPriority.AboveNormal };
        reader.Start();
    }

    // MARK: Commands

    /// <summary>Switches the program scene; with a transition (and a length in ms) that transition is used.</summary>
    public Task<bool> SetProgramScene(string scene, string? transition = null, int? durationMs = null) =>
        Enqueue($"Scene: {scene}", () => UsingTransition(transition, durationMs)
            && Request("SetCurrentProgramScene", new JsonObject { ["sceneName"] = scene }) is not null);

    /// <summary>Puts a scene on preview (studio mode must be on in OBS).</summary>
    public Task<bool> SetPreviewScene(string scene) =>
        Enqueue($"Preview scene: {scene}", () => Request("SetCurrentPreviewScene", new JsonObject { ["sceneName"] = scene }) is not null);

    /// <summary>Studio mode: takes preview to program with a transition (the current one, or the one named).</summary>
    public Task<bool> Transition(string? transition = null, int? durationMs = null) =>
        Enqueue("Transition", () => UsingTransition(transition, durationMs) && Request("TriggerStudioModeTransition") is not null);

    public Task<bool> SetStreaming(bool start) =>
        Enqueue(start ? "Start streaming" : "Stop streaming", () => Request(start ? "StartStream" : "StopStream") is not null);

    public Task<bool> SetRecording(bool start) =>
        Enqueue(start ? "Start recording" : "Stop recording", () => Request(start ? "StartRecord" : "StopRecord") is not null);

    /// <summary>Shows or hides a source in a scene (OBS's eye icon). The source is found by name.</summary>
    public Task<bool> SetSourceVisible(string scene, string source, bool visible) =>
        Enqueue($"{(visible ? "Show" : "Hide")} {source}", () =>
        {
            var found = Request("GetSceneItemId", new JsonObject { ["sceneName"] = scene, ["sourceName"] = source });
            if (found?["sceneItemId"]?.GetValue<int>() is not { } itemId) return false;
            return Request("SetSceneItemEnabled", new JsonObject
            {
                ["sceneName"] = scene, ["sceneItemId"] = itemId, ["sceneItemEnabled"] = visible,
            }) is not null;
        });

    private bool UsingTransition(string? transition, int? durationMs)
    {
        if (!string.IsNullOrWhiteSpace(transition)
            && Request("SetCurrentSceneTransition", new JsonObject { ["transitionName"] = transition }) is null)
            return false;
        if (durationMs is > 0
            && Request("SetCurrentSceneTransitionDuration", new JsonObject { ["transitionDuration"] = Math.Clamp(durationMs.Value, 50, 20000) }) is null)
            return false;
        return true;
    }

    /// <summary>
    /// Sends one request and waits for its answer: the answer's data, or null when OBS isn't
    /// connected, said no (the reason goes in the log), or didn't answer in time.
    /// Call it on the device's queue, never on the reader thread (which delivers the answer).
    /// </summary>
    private JsonObject? Request(string type, JsonObject? data = null)
    {
        if (socket is not { } ws)
        {
            NotConnected(type);
            return null;
        }
        var id = Guid.NewGuid().ToString("N");
        var waiting = new Pending();
        pending[id] = waiting;
        try
        {
            var message = new JsonObject
            {
                ["op"] = 6,
                ["d"] = new JsonObject { ["requestType"] = type, ["requestId"] = id, ["requestData"] = data ?? new JsonObject() },
            };
            ws.SendText(message.ToJsonString());
            if (!waiting.Done.Wait(RequestTimeout))
            {
                Stalled(ws, type);
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The connection went while sending (the reader notices too, and reconnects).
            Note($"{type}: {ex.Message}");
            return null;
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
        var reply = waiting.Reply;
        if (reply?["requestStatus"] is not JsonObject status) return null;
        answered = true;
        if (status["result"]?.GetValue<bool>() != true)
        {
            Note($"{type}: OBS said no ({status["comment"]?.GetValue<string>() ?? "code " + status["code"]})");
            return null;
        }
        return reply["responseData"] as JsonObject ?? [];
    }

    /// <summary>
    /// OBS took a request and didn't answer it in time: drop the connection. The commands queued
    /// after this one then find no connection and are skipped at once, and the reader waits its
    /// turn before signing in again (an OBS that signs in but never answers mustn't make the light flicker).
    /// </summary>
    private void Stalled(MiniWebSocket ws, string type)
    {
        Note($"{type}: OBS didn't answer in time; reconnecting");
        if (socket != ws) return;
        stalled = true;
        socket = null;
        SetStatus(DeviceState.Problem, "OBS stopped answering; trying again…");
        ws.Dispose();
    }

    // MARK: The reader thread

    private void Run()
    {
        var retry = TimeSpan.Zero;
        while (!IsDisposed)
        {
            if (retry > TimeSpan.Zero && stopping.Wait(retry)) return;
            retry = retry == TimeSpan.Zero ? FirstRetry : TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, TimeSpan.FromSeconds(10).Ticks));
            answered = false;
            stalled = false;
            MiniWebSocket? ws = null;
            try
            {
                ws = MiniWebSocket.Connect(Settings.Host, Settings.EffectivePort, ConnectTimeout);
                if (!SignIn(ws)) continue;
                ws.ReadTimeout = TimeSpan.Zero;
                socket = ws;
                SetStatus(DeviceState.Online, "Signed in");
                Note("connected");
                QueueRefresh();
                while (!IsDisposed && ws.ReceiveText() is { } text) Handle(text);
                if (!IsDisposed) SetStatus(DeviceState.Problem, $"OBS closed the connection; trying again…");
            }
            catch (Exception ex)
            {
                // Whatever went wrong (no answer, a dropped line, a garbled message), the thread
                // must live on to try again: a dead reader would leave OBS dead until a restart.
                if (IsDisposed) return;
                if (stalled) continue;   // dropped for not answering: the light already says so
                if (Status.State != DeviceState.Problem) Note($"connection: {ex.Message}");
                SetStatus(DeviceState.Problem, $"No answer from {Where}");
            }
            finally
            {
                socket = null;
                foreach (var waiting in pending.Values) waiting.Done.Set();
                ws?.Dispose();
                // A connection OBS worked on, now closed (OBS restarting): try again at once. One it
                // never answered on, or one dropped for not answering, waits its turn.
                if (answered && !stalled) retry = TimeSpan.Zero;
            }
        }
    }

    /// <summary>Hello, Identify, Identified. False (with the light red) when OBS wants a password we haven't got right.</summary>
    private bool SignIn(MiniWebSocket ws)
    {
        var hello = JsonNode.Parse(ws.ReceiveText() ?? throw new IOException("OBS closed before saying hello"))?.AsObject();
        if (hello?["op"]?.GetValue<int>() != 0) throw new IOException("Not obs-websocket 5 (no hello)");
        var identify = new JsonObject { ["rpcVersion"] = 1, ["eventSubscriptions"] = EventSubscriptions };
        if (hello["d"]?["authentication"] is JsonObject auth)
        {
            if (string.IsNullOrEmpty(password))
            {
                SetStatus(DeviceState.Problem, "OBS asks for a password: add it in Settings › Production");
                return false;
            }
            identify["authentication"] = ObsAuth.Response(password, auth["salt"]!.GetValue<string>(), auth["challenge"]!.GetValue<string>());
        }
        ws.SendText(new JsonObject { ["op"] = 1, ["d"] = identify }.ToJsonString());
        var answer = ws.ReceiveText();
        if (answer is null)
        {
            // 4009 is obs-websocket's "authentication failed".
            SetStatus(DeviceState.Problem, ws.CloseCode == 4009 ? "OBS refused the password" : $"OBS closed the connection ({ws.CloseCode})");
            return false;
        }
        if (JsonNode.Parse(answer)?["op"]?.GetValue<int>() != 2) throw new IOException("OBS didn't confirm the sign-in");
        return true;
    }

    private void Handle(string text)
    {
        if (JsonNode.Parse(text) is not JsonObject message || message["d"] is not JsonObject d) return;
        switch (message["op"]?.GetValue<int>())
        {
            case 7: // a request's answer
                if (d["requestId"]?.GetValue<string>() is { } id && pending.TryGetValue(id, out var waiting))
                {
                    waiting.Reply = d;
                    waiting.Done.Set();
                }
                break;
            case 5: // an event
                OnEvent(d["eventType"]?.GetValue<string>(), d["eventData"] as JsonObject);
                break;
        }
    }

    private void OnEvent(string? type, JsonObject? data)
    {
        lock (gate)
        {
            switch (type)
            {
                case "CurrentProgramSceneChanged":
                    programScene = data?["sceneName"]?.GetValue<string>();
                    break;
                case "CurrentPreviewSceneChanged":
                    previewScene = data?["sceneName"]?.GetValue<string>();
                    break;
                case "StudioModeStateChanged":
                    studioMode = data?["studioModeEnabled"]?.GetValue<bool>() == true;
                    break;
                case "StreamStateChanged":
                    streaming = data?["outputActive"]?.GetValue<bool>() == true;
                    break;
                case "RecordStateChanged":
                    recording = data?["outputActive"]?.GetValue<bool>() == true;
                    break;
                case "SceneListChanged" or "SceneCreated" or "SceneRemoved" or "SceneNameChanged" or "SceneItemCreated" or "SceneItemRemoved"
                    or "SceneTransitionCreated" or "SceneTransitionRemoved" or "SceneTransitionNameChanged":
                    QueueRefresh();
                    return;
                default:
                    return;
            }
        }
        ShowOnline();
    }

    /// <summary>Reads OBS's scenes, sources and transitions again (once, however many changes asked).</summary>
    private void QueueRefresh()
    {
        if (Interlocked.Exchange(ref refreshQueued, 1) == 1) return;
        _ = Enqueue("Read OBS's scenes", () =>
        {
            Interlocked.Exchange(ref refreshQueued, 0);
            var list = Request("GetSceneList");
            if (list is null && socket is null) return false;   // gone (or stopped answering): the next sign-in reads them
            var transitionList = Request("GetSceneTransitionList");
            var studio = Request("GetStudioModeEnabled");
            var stream = Request("GetStreamStatus");
            var record = Request("GetRecordStatus");
            // OBS lists scenes bottom-up (sceneIndex 0 is the last in its window): turn them round.
            var names = (list?["scenes"] as JsonArray ?? [])
                .OfType<JsonObject>()
                .OrderByDescending(s => s["sceneIndex"]?.GetValue<int>() ?? 0)
                .Select(s => s["sceneName"]?.GetValue<string>())
                .OfType<string>()
                .ToList();
            var sources = new Dictionary<string, IReadOnlyList<string>>();
            foreach (var scene in names.Take(60))
            {
                var items = Request("GetSceneItemList", new JsonObject { ["sceneName"] = scene });
                sources[scene] = [.. (items?["sceneItems"] as JsonArray ?? []).OfType<JsonObject>().Select(i => i["sourceName"]?.GetValue<string>()).OfType<string>()];
            }
            lock (gate)
            {
                scenes = names;
                sceneSources = sources;
                transitions = [.. (transitionList?["transitions"] as JsonArray ?? []).OfType<JsonObject>()
                    .Select(t => t["transitionName"]?.GetValue<string>()).OfType<string>()];
                programScene = list?["currentProgramSceneName"]?.GetValue<string>() ?? programScene;
                previewScene = list?["currentPreviewSceneName"]?.GetValue<string>() ?? previewScene;
                studioMode = studio?["studioModeEnabled"]?.GetValue<bool>() ?? studioMode;
                streaming = stream?["outputActive"]?.GetValue<bool>() ?? streaming;
                recording = record?["outputActive"]?.GetValue<bool>() ?? recording;
            }
            ShowOnline();
            return list is not null;
        });
    }

    private void ShowOnline()
    {
        if (socket is null) return;
        string detail;
        lock (gate)
        {
            var parts = new List<string> { programScene is { } scene ? $"Program: {scene}" : "Signed in" };
            if (studioMode) parts.Add("studio mode");
            if (streaming) parts.Add("streaming");
            if (recording) parts.Add("recording");
            detail = string.Join(" · ", parts);
        }
        SetStatus(DeviceState.Online, detail);
    }

    public override void Dispose()
    {
        base.Dispose();
        stopping.Set();
        socket?.Dispose();
    }
}
