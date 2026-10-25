using MxU.Slides.Core.Library;
using MxU.Slides.Core.Threading;
using MxU.Slides.Production.Actions;
using MxU.Slides.Production.Atem;
using MxU.Slides.Production.Devices;
using MxU.Slides.Production.Dmx;
using MxU.Slides.Production.Lighting;
using MxU.Slides.Production.Obs;
using MxU.Slides.Production.Vmix;

namespace MxU.Slides.Production;

/// <summary>
/// Everything production in one place: this PC's devices (made from production.json and kept
/// connected in the background), the lights (the library's looks, the controller and the DMX
/// engine), and the production steps (stored per owner, and run when their owner runs).
/// <para>
/// The app makes one at start and gives its <see cref="Runner"/> to the action router. A step
/// is handed to its device and the hub returns at once; nothing here ever waits on a device.
/// A step for a device that isn't set up on this PC is skipped with a line in the log, so a
/// library made in a room with an ATEM still runs, without the camera cuts, on a laptop at home.
/// </para>
/// </summary>
public sealed class ProductionHub : IDisposable
{
    private readonly WindowsExtras extras;
    private readonly string settingsPath;
    private readonly Lock gate = new();
    private readonly WorkQueue background = new("MxU production setup");
    private ProductionSettings settings;
    private List<ProductionDevice> devices = [];
    private bool started;

    /// <param name="extras">The library's Windows extras (the steps and the looks live there).</param>
    /// <param name="settingsPath">This PC's production.json.</param>
    /// <param name="secrets">Where device passwords are kept, encrypted.</param>
    /// <param name="clock">Times the steps' delays (tests pass their own).</param>
    /// <param name="lightClock">Seconds for the lighting fades (tests pass their own).</param>
    public ProductionHub(WindowsExtras extras, string settingsPath, DeviceSecrets secrets, TimeProvider? clock = null, Func<double>? lightClock = null)
    {
        this.extras = extras;
        this.settingsPath = settingsPath;
        Secrets = secrets;
        settings = ProductionSettings.Load(settingsPath);
        Steps = new ProductionSteps(extras);
        Runner = new ProductionRunner(Steps, step => _ = PerformAsync(step), clock);
        Lighting = new LightingController(lightClock);
        Dmx = new DmxEngine(Lighting);
        Dmx.Failed += ex => Note($"DMX frame failed: {ex.Message}");
        extras.Changed += (kind, owner) =>
        {
            if (kind == LightingShow.ExtrasKind && owner == LightingShow.ExtrasOwner) Lighting.Load(LightingShow.Load(extras));
        };
    }

    public ProductionSteps Steps { get; }

    /// <summary>Runs steps when their owners run: the action router's <c>ExtraSteps</c>.</summary>
    public ProductionRunner Runner { get; }

    public LightingController Lighting { get; }

    public DmxEngine Dmx { get; }

    public DeviceSecrets Secrets { get; }

    /// <summary>The devices were added, changed or removed (a new list). Raised on the thread that changed them.</summary>
    public event Action? DevicesChanged;

    /// <summary>A device's status light changed. Raised on a device's thread.</summary>
    public event Action<ProductionDevice>? StatusChanged;

    /// <summary>A line for the log.</summary>
    public event Action<string>? Noted;

    /// <summary>Every device set up on this PC, turned on or off (copies).</summary>
    public IReadOnlyList<DeviceSettings> Setups
    {
        get
        {
            lock (gate) return [.. settings.Devices.Select(d => d.Copy())];
        }
    }

    /// <summary>The devices running now (the turned-on ones).</summary>
    public IReadOnlyList<ProductionDevice> Devices
    {
        get
        {
            lock (gate) return [.. devices];
        }
    }

    public ProductionDevice? Device(string id)
    {
        lock (gate) return devices.FirstOrDefault(d => d.Id == id);
    }

    /// <summary>A device's light: Off when it's turned off (or gone).</summary>
    public DeviceStatus StatusOf(string deviceId) => Device(deviceId)?.Status ?? DeviceStatus.Off;

    /// <summary>Connects every turned-on device and loads the lighting, in the background. Returns at once.</summary>
    public void Start()
    {
        lock (gate)
        {
            if (started) return;
            started = true;
        }
        Lighting.Load(LightingShow.Load(extras));
        Rebuild();
        background.Async(Steps.Preload);
    }

    // MARK: Devices (Settings › Production)

    /// <summary>Adds or changes a device and saves production.json; a changed device reconnects.</summary>
    /// <param name="password">A new password, "" to remove it, or null to keep the one stored.</param>
    public void SaveDevice(DeviceSettings device, string? password = null)
    {
        if (password is not null && device.TakesPassword) Secrets.Set(device.Id, password);
        lock (gate)
        {
            var index = settings.Devices.FindIndex(d => d.Id == device.Id);
            if (index >= 0) settings.Devices[index] = device.Copy();
            else settings.Devices.Add(device.Copy());
            SaveSettings();
        }
        Rebuild();
    }

    public void RemoveDevice(string deviceId)
    {
        lock (gate)
        {
            settings.Devices.RemoveAll(d => d.Id == deviceId);
            SaveSettings();
        }
        Secrets.Remove(deviceId);
        Rebuild();
    }

    private void SaveSettings()
    {
        try { settings.Save(settingsPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Note($"couldn't save production.json: {ex.Message}"); }
    }

    /// <summary>Makes the running devices match the settings: new ones start, changed ones restart, removed ones stop.</summary>
    private void Rebuild()
    {
        List<ProductionDevice> stopping;
        List<ProductionDevice> starting = [];
        lock (gate)
        {
            if (!started) return;
            var wanted = settings.Devices.Where(d => d.Enabled).ToList();
            var keep = devices.Where(d => wanted.Any(w => Core.Json.MxuJson.StructurallyEqual(w, d.Settings))).ToList();
            stopping = [.. devices.Except(keep)];
            var next = new List<ProductionDevice>();
            foreach (var want in wanted)
            {
                if (keep.FirstOrDefault(k => k.Id == want.Id) is { } same)
                {
                    next.Add(same);
                    continue;
                }
                var made = Make(want.Copy());
                made.StatusChanged += device => StatusChanged?.Invoke(device);
                made.Noted += (device, line) => Note($"{device.Name}: {line}");
                next.Add(made);
                starting.Add(made);
            }
            devices = next;
        }
        foreach (var gone in stopping)
        {
            if (gone is DmxOutput dmx) dmx.Terminate(dmx.Settings.EffectiveUniverses.ToList());
            gone.Dispose();
        }
        foreach (var device in starting) device.Start();
        Dmx.SetOutputs([.. Devices.OfType<DmxOutput>()]);
        DevicesChanged?.Invoke();
    }

    private ProductionDevice Make(DeviceSettings device) => device.Kind switch
    {
        DeviceKind.Atem => new AtemSwitcher(device),
        DeviceKind.Obs => new ObsClient(device, Secrets.Get(device.Id)),
        DeviceKind.Vmix => new VmixClient(device, Secrets.Get(device.Id)),
        _ => new DmxOutput(device, SacnSourceId(), $"MxU Slides ({Environment.MachineName})"),
    };

    /// <summary>This PC's sACN source id, made and saved the first time it's needed. Called with the lock held.</summary>
    private Guid SacnSourceId()
    {
        if (settings.SacnSourceId is { } known) return known;
        settings.SacnSourceId = Guid.NewGuid();
        SaveSettings();
        return settings.SacnSourceId.Value;
    }

    // MARK: Steps

    /// <summary>Runs one step now. The task says whether the device did it (tests and the Local API wait; the show doesn't).</summary>
    public Task<bool> PerformAsync(ProductionAction step)
    {
        if (step.Kind is not { } kind)
        {
            Note($"skipped a step of a kind this version doesn't know ({step.KindName})");
            return Task.FromResult(false);
        }
        switch (kind)
        {
            case ProductionKind.LightsLook:
                return Lit(step.LookId is { } look && Lighting.FireLook(look, step.FadeSeconds, step.OnTop == true), "the look is missing");
            case ProductionKind.LightsRelease:
                return Lit(step.LookId is { } released && Lighting.ReleaseLook(released, step.FadeSeconds), "the look is missing");
            case ProductionKind.LightsCue:
                return Lit(Lighting.Go(step.CueId), step.CueId is null ? "the cue list is at its end" : "the cue is missing");
            case ProductionKind.LightsBlackout:
                Lighting.SetBlackout(step.On);
                return Task.FromResult(true);
            case ProductionKind.LightsMaster:
                Lighting.SetGrandMaster(step.Level ?? 100, step.FadeSeconds ?? 0);
                return Task.FromResult(true);
            case ProductionKind.LightsOff:
                Lighting.AllOff(step.FadeSeconds ?? 0);
                return Task.FromResult(true);
        }
        var wanted = ProductionAction.DeviceFor(kind)!.Value;
        if (Find(step.DeviceId, wanted) is not { } device)
        {
            var kindName = global::MxU.Slides.Production.Devices.DeviceSettings.KindName(wanted);
            Note($"skipped {ProductionEditing.DeviceWord(wanted)} step: no {kindName} is set up and turned on, on this PC");
            return Task.FromResult(false);
        }
        int Me() => step.MixEffect ?? 1;
        string Text(string? value) => value?.Trim() ?? "";
        return (kind, device) switch
        {
            (ProductionKind.AtemProgram, AtemSwitcher atem) => atem.Program(Me(), step.Source ?? 1),
            (ProductionKind.AtemPreview, AtemSwitcher atem) => atem.Preview(Me(), step.Source ?? 1),
            (ProductionKind.AtemCut, AtemSwitcher atem) => atem.Cut(Me()),
            (ProductionKind.AtemAuto, AtemSwitcher atem) => atem.Auto(Me()),
            (ProductionKind.AtemFadeToBlack, AtemSwitcher atem) => atem.FadeToBlack(Me()),
            (ProductionKind.AtemMacro, AtemSwitcher atem) => atem.RunMacro(step.Number ?? 1),
            (ProductionKind.AtemAux, AtemSwitcher atem) => atem.Aux(step.Number ?? 1, step.Source ?? 1),
            (ProductionKind.AtemUpstreamKey, AtemSwitcher atem) => atem.UpstreamKey(Me(), step.Number ?? 1, step.On ?? true),
            (ProductionKind.AtemDownstreamKey, AtemSwitcher atem) => atem.DownstreamKey(step.Number ?? 1, step.On ?? true),
            (ProductionKind.ObsProgramScene, ObsClient obs) => obs.SetProgramScene(Text(step.Scene), step.Transition, step.DurationMs),
            (ProductionKind.ObsPreviewScene, ObsClient obs) => obs.SetPreviewScene(Text(step.Scene)),
            (ProductionKind.ObsTransition, ObsClient obs) => obs.Transition(step.Transition, step.DurationMs),
            (ProductionKind.ObsStreaming, ObsClient obs) => obs.SetStreaming(step.On ?? true),
            (ProductionKind.ObsRecording, ObsClient obs) => obs.SetRecording(step.On ?? true),
            (ProductionKind.ObsSourceVisible, ObsClient obs) => obs.SetSourceVisible(Text(step.Scene), Text(step.SceneItem), step.On ?? true),
            (ProductionKind.VmixCut, VmixClient vmix) => vmix.Cut(step.Input),
            (ProductionKind.VmixFade, VmixClient vmix) => vmix.Fade(step.Input, step.DurationMs),
            (ProductionKind.VmixOverlay, VmixClient vmix) => vmix.Overlay(step.Number ?? 1, step.Input, step.On ?? true),
            (ProductionKind.VmixStreaming, VmixClient vmix) => vmix.Streaming(step.On ?? true),
            (ProductionKind.VmixRecording, VmixClient vmix) => vmix.Recording(step.On ?? true),
            (ProductionKind.VmixPreview, VmixClient vmix) => vmix.Preview(Text(step.Input)),
            (ProductionKind.VmixActive, VmixClient vmix) => vmix.Active(Text(step.Input)),
            _ => Task.FromResult(false),
        };
    }

    private Task<bool> Lit(bool done, string otherwise)
    {
        if (!done) Note($"skipped a lighting step: {otherwise}");
        return Task.FromResult(done);
    }

    /// <summary>The device a step names, if it's running; else the first running device of its kind.</summary>
    private ProductionDevice? Find(string? deviceId, DeviceKind kind)
    {
        lock (gate)
        {
            return (deviceId is null ? null : devices.FirstOrDefault(d => d.Id == deviceId && d.Kind == kind))
                ?? devices.FirstOrDefault(d => d.Kind == kind);
        }
    }

    /// <summary>What the editors' pickers offer: the devices, what each reports, the library's looks.</summary>
    public ProductionContext Context()
    {
        var infos = Setups.Select(d => new DeviceInfo(d.Id, d.Name, d.Kind)).ToList();
        return new ProductionContext(infos, (id, kind) => Find(id, kind)?.Catalog ?? DeviceCatalog.Empty, Lighting.Show);
    }

    // MARK: Lighting

    /// <summary>Saves the library's looks and cues (the controller picks the change up through the extras store).</summary>
    public void SaveLighting(LightingShow show) => show.Save(extras);

    private void Note(string line)
    {
        try { Noted?.Invoke(line); }
        catch { }
    }

    public void Dispose()
    {
        List<ProductionDevice> all;
        lock (gate)
        {
            all = devices;
            devices = [];
        }
        var lit = Lighting.IsActive ? Lighting.Frame().Keys.ToList() : [];
        foreach (var device in all)
        {
            if (device is DmxOutput dmx) dmx.Terminate(lit);
            device.Dispose();
        }
        Dmx.Dispose();
        background.Dispose();
    }
}
