using System.Text.Json;
using System.Text.Json.Serialization;
using MxU.Slides.Core.Media;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.VideoIO.Common;
using MxU.Slides.VideoIO.DeckLink;
using MxU.Slides.VideoIO.Ndi;

namespace MxU.Slides.VideoIO.Capture;

/// <summary>Makes the live input for an inventory entry. The real one opens hardware; tests pass fakes.</summary>
public interface IVideoInputFactory
{
    /// <summary>A camera or capture card by <see cref="CameraDevice.Id"/>, at the operator's rate or automatic.</summary>
    IVideoInputSource Camera(string deviceId, string name, double? frameRate);

    /// <summary>The first NDI source whose name contains <paramref name="sourceNameContaining"/>.</summary>
    IVideoInputSource Ndi(string sourceNameContaining, string name);

    /// <summary>A DeckLink card's input, shared with anything else using that card.</summary>
    IVideoInputSource DeckLink(long persistentId, string name);
}

/// <summary>The hardware factory: <see cref="CameraInput"/>, <see cref="NdiInputSource"/>, <see cref="DeckLinkCaptureHub"/>.</summary>
public sealed class VideoInputFactory : IVideoInputFactory
{
    public IVideoInputSource Camera(string deviceId, string name, double? frameRate)
    {
        var camera = new CameraInput(deviceId, name, frameRate);
        camera.Start();
        return camera;
    }

    public IVideoInputSource Ndi(string sourceNameContaining, string name) => NdiInputSource.Open(sourceNameContaining, name);

    public IVideoInputSource DeckLink(long persistentId, string name) => DeckLinkCaptureHub.Shared.Acquire(persistentId, name);
}

/// <summary>
/// The show's named video inputs ("Pulpit cam", "Stage NDI"), each pointing at a
/// device: a camera, an NDI source or a DeckLink input. Slides refer to an input by its
/// <see cref="Entry.EngineId"/>, so re-pointing "Pulpit cam" at another camera updates
/// every slide that uses it. Once <see cref="WarmUp"/> is called, assigned inputs run all
/// the time (switching to one on a slide is instant), each with its own lip-sync delay.
/// <para>
/// Once a second it reconciles cameras with what's plugged in (an unplugged camera is
/// suspended and restarted on replug, at most every five seconds), reopens silent
/// DeckLink inputs, and works out which inputs are actually delivering
/// (<see cref="ConnectedChanged"/>). Entries are saved to a JSON file the app chooses.
/// </para>
/// Thread-safe; events are raised on worker threads, so marshal to the UI thread.
/// Port of the logic in the Mac's Sources/VideoInputInventory.swift (UserDefaults and the
/// MediaEngine registry there; a JSON file and <see cref="Source"/>/<see cref="LatestFrame"/> here).
/// </summary>
public sealed class VideoInputInventory : IDisposable
{
    public const string EngineIdPrefix = "input::item::";

    /// <summary>A camera that came back is started at most this often (seconds), so a flaky cable can't thrash it.</summary>
    internal const double HotplugRetrySeconds = 5;

    /// <summary>
    /// One named input. <see cref="Kind"/> and <see cref="SourceId"/> say which device:
    /// a camera's device id, an NDI name fragment, or a DeckLink persistent id (as text).
    /// </summary>
    public sealed record Entry(
        string Id, string Name, CaptureSourceKind? Kind = null, string? SourceId = null,
        string? AudioInputId = null, int? DelayFrames = null, double? FrameRate = null)
    {
        [JsonIgnore] public string EngineId => EngineIdPrefix + Id;

        [JsonIgnore] public bool IsAssigned => Kind is not null && !string.IsNullOrEmpty(SourceId);
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly Lock gate = new();
    private readonly string storePath;
    private readonly IVideoInputFactory factory;
    private readonly Func<string, bool?> cameraPresent;
    private readonly CameraPresence? presence;
    private readonly LiveInputDelay delay = new();
    private readonly TimeSpan pollInterval;
    private readonly List<Entry> entries = [];
    private readonly Dictionary<string, IVideoInputSource> running = [];
    private readonly Dictionary<string, double> hotplugStartAttempts = [];
    private IReadOnlyList<string> lastConnected = [];
    private Timer? connectivityTimer;
    private bool warmedUp;

    /// <param name="storePath">The JSON file entries are kept in, e.g. AppPaths.DataRoot\video-inputs.json.</param>
    public VideoInputInventory(string storePath) : this(storePath, new VideoInputFactory(), null)
    {
    }

    /// <param name="cameraPresent">Is this camera plugged in? null = don't know yet. Defaults to a device watcher.</param>
    /// <param name="pollInterval">How often the checks run (tests turn them off with Timeout.InfiniteTimeSpan).</param>
    internal VideoInputInventory(string storePath, IVideoInputFactory factory, Func<string, bool?>? cameraPresent, TimeSpan? pollInterval = null)
    {
        this.storePath = storePath;
        this.factory = factory;
        this.pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
        if (cameraPresent is null)
        {
            presence = new CameraPresence();
            this.cameraPresent = id => presence.IsReady ? presence.IsPresent(id) : null;
        }
        else
        {
            this.cameraPresent = cameraPresent;
        }
        if (Load(storePath) is { } stored)
        {
            entries.AddRange(stored);
            ShareDeviceRates();
            Persist();
        }
    }

    public IReadOnlyList<Entry> Entries
    {
        get { lock (gate) return [.. entries]; }
    }

    /// <summary>The ids of the inputs delivering right now, sorted.</summary>
    public IReadOnlyList<string> Connected
    {
        get { lock (gate) return lastConnected; }
    }

    /// <summary>Raised after any entry was added, changed or removed.</summary>
    public event Action? EntriesChanged;

    /// <summary>Raised when the set of delivering inputs changes; carries their ids, sorted.</summary>
    public event Action<IReadOnlyList<string>>? ConnectedChanged;

    public Entry? EntryFor(string id)
    {
        lock (gate) return entries.FirstOrDefault(e => e.Id == id);
    }

    public Entry? EntryForEngineId(string engineId) =>
        engineId.StartsWith(EngineIdPrefix, StringComparison.Ordinal) ? EntryFor(engineId[EngineIdPrefix.Length..]) : null;

    public bool IsInventoried(string engineId) => EntryForEngineId(engineId) is not null;

    /// <summary>The running input behind a slide's engine id, or null if it isn't running.</summary>
    public IVideoInputSource? Source(string engineId)
    {
        lock (gate) return EntryForEngineIdLocked(engineId) is { } entry ? running.GetValueOrDefault(entry.Id) : null;
    }

    /// <summary>What the Video Input layer should draw for <paramref name="engineId"/> now: the newest frame, held back by the input's delay.</summary>
    public VideoFrame? LatestFrame(string engineId)
    {
        var frame = Source(engineId)?.LatestFrame();
        return frame is null ? null : delay.Delayed(frame, engineId);
    }

    /// <summary>Starts every assigned input and the once-a-second checks. Call once, when the app is ready to show inputs.</summary>
    public void WarmUp()
    {
        lock (gate)
        {
            if (warmedUp) return;
            warmedUp = true;
            foreach (var entry in entries.Where(e => e.IsAssigned).ToList()) StartLocked(entry);
            connectivityTimer = new Timer(_ => PollConnectivity(ShowState.HostClock()), null, pollInterval, pollInterval);
        }
        if (presence is not null) presence.Changed += () => ReconcileCameraHotplug(ShowState.HostClock());
    }

    public Entry Create(string name, CaptureSourceKind? kind = null, string? sourceId = null)
    {
        var entry = new Entry(NewId(), name, kind, sourceId);
        lock (gate)
        {
            entries.Add(entry);
            Persist();
            if (entry.IsAssigned) StartLocked(entry);
        }
        EntriesChanged?.Invoke();
        return entry;
    }

    /// <summary>Adds an entry with a known id (from an imported show). False if that id already exists.</summary>
    public bool ImportEntry(string id, string name, CaptureSourceKind? kind, string? sourceId)
    {
        lock (gate)
        {
            if (entries.Any(e => e.Id == id)) return false;
            var entry = new Entry(id, name, kind, sourceId);
            entries.Add(entry);
            Persist();
            if (entry.IsAssigned) StartLocked(entry);
        }
        EntriesChanged?.Invoke();
        return true;
    }

    public void Rename(string id, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) return;
        if (!Update(id, entry => entry with { Name = trimmed })) return;
        EntriesChanged?.Invoke();
    }

    /// <summary>Points an input at another device (its old one stops; a camera's chosen rate is forgotten).</summary>
    public void AssignDevice(string id, CaptureSourceKind? kind, string? sourceId)
    {
        lock (gate)
        {
            var index = entries.FindIndex(e => e.Id == id);
            if (index < 0) return;
            StopLocked(entries[index]);
            entries[index] = entries[index] with { Kind = kind, SourceId = sourceId, FrameRate = null };
            ShareDeviceRates();
            Persist();
            if (entries[index].IsAssigned) StartLocked(entries[index]);
        }
        EntriesChanged?.Invoke();
    }

    public void AssignAudioInput(string id, string? audioInputId)
    {
        if (Update(id, entry => entry with { AudioInputId = audioInputId })) EntriesChanged?.Invoke();
    }

    /// <summary>Sets the lip-sync delay in frames (0…<see cref="LiveInputDelay.MaxFrames"/>). Takes effect at once.</summary>
    public void SetDelay(string id, int frames)
    {
        var clamped = Math.Clamp(frames, 0, LiveInputDelay.MaxFrames);
        string? name;
        lock (gate)
        {
            var index = entries.FindIndex(e => e.Id == id);
            if (index < 0) return;
            entries[index] = entries[index] with { DelayFrames = clamped == 0 ? null : clamped };
            Persist();
            delay.SetDelay(entries[index].EngineId, clamped);
            name = entries[index].Name;
        }
        VideoIOLog.Note("input.camera", $"delay {clamped}f: {name}");
        EntriesChanged?.Invoke();
    }

    /// <summary>
    /// Sets a camera's frame rate (null = automatic). Every input on the same camera gets
    /// it too, since the device can only run one format; they all restart.
    /// </summary>
    public void SetFrameRate(string id, double? rate)
    {
        lock (gate)
        {
            var index = entries.FindIndex(e => e.Id == id);
            if (index < 0) return;
            var mates = DeviceMates(entries[index]);
            foreach (var mate in mates)
            {
                StopLocked(entries[mate]);
                entries[mate] = entries[mate] with { FrameRate = rate };
            }
            Persist();
            foreach (var mate in mates.Where(m => entries[m].IsAssigned)) StartLocked(entries[mate]);
        }
        EntriesChanged?.Invoke();
    }

    public void Remove(string id)
    {
        lock (gate)
        {
            var entry = entries.FirstOrDefault(e => e.Id == id);
            if (entry is null) return;
            entries.Remove(entry);
            Persist();
            StopLocked(entry);
        }
        EntriesChanged?.Invoke();
    }

    /// <summary>A slide wants this input: start it if it isn't running (e.g. it failed earlier).</summary>
    public void StartIfNeeded(string engineId)
    {
        lock (gate)
        {
            var entry = EntryForEngineIdLocked(engineId);
            if (entry is null || !entry.IsAssigned)
            {
                VideoIOLog.Note("input.camera", $"unknown or unassigned input: {engineId}");
                return;
            }
            StartLocked(entry);
        }
    }

    /// <summary>Stops the running NDI inputs so the NDI runtime can restart; returns them for <see cref="RewarmAfterRuntimeSwap"/>.</summary>
    public IReadOnlyList<Entry> RestNdiForRuntimeSwap()
    {
        lock (gate)
        {
            var resting = entries.Where(e => e.Kind == CaptureSourceKind.Ndi && running.ContainsKey(e.Id)).ToList();
            foreach (var entry in resting) StopLocked(entry);
            return resting;
        }
    }

    public void RewarmAfterRuntimeSwap(IEnumerable<Entry> rested)
    {
        lock (gate)
            foreach (var entry in rested)
                if (entries.FirstOrDefault(e => e.Id == entry.Id) is { } current) StartLocked(current);
    }

    public void Dispose()
    {
        lock (gate)
        {
            connectivityTimer?.Dispose();
            connectivityTimer = null;
            foreach (var entry in entries.Where(e => running.ContainsKey(e.Id)).ToList()) StopLocked(entry);
            warmedUp = false;
        }
        presence?.Dispose();
    }

    /// <summary>The once-a-second check (public to tests through InternalsVisibleTo).</summary>
    internal void PollConnectivity(double now)
    {
        ReconcileCameraHotplug(now);
        IVideoInputSource[] direct;
        lock (gate) direct = [.. running.Values];
        foreach (var source in direct)
            if (source is DeckLinkCaptureLease lease) lease.Capture.CheckLiveness(now);

        IReadOnlyList<string> connected;
        lock (gate)
        {
            var delivering = new List<string>();
            foreach (var entry in entries)
            {
                if (!running.TryGetValue(entry.Id, out var source)) continue;
                var live = entry.Kind switch
                {
                    CaptureSourceKind.Ndi => source.State.IsReceiving,
                    CaptureSourceKind.Camera or CaptureSourceKind.Decklink => source.FrameAge(now) is < 2,
                    _ => false,
                };
                if (live) delivering.Add(entry.Id);
            }
            delivering.Sort(StringComparer.Ordinal);
            if (delivering.SequenceEqual(lastConnected)) return;
            lastConnected = connected = delivering;
        }
        ConnectedChanged?.Invoke(connected);
    }

    /// <summary>Suspends cameras that were unplugged and restarts ones that came back (port of reconcileCameraHotplug).</summary>
    internal void ReconcileCameraHotplug(double now)
    {
        lock (gate)
        {
            foreach (var entry in entries.Where(e => e.Kind == CaptureSourceKind.Camera && e.IsAssigned).ToList())
            {
                if (cameraPresent(entry.SourceId!) is not { } present) continue;
                switch (InputHotplug.Decide(present, running.ContainsKey(entry.Id)))
                {
                    case HotplugAction.Suspend:
                        StopLocked(entry);
                        hotplugStartAttempts.Remove(entry.Id);
                        VideoIOLog.Note("input.camera", $"device unplugged: {entry.Name} — holding for replug");
                        break;
                    case HotplugAction.Start:
                        if (hotplugStartAttempts.TryGetValue(entry.Id, out var last) && now - last < HotplugRetrySeconds) break;
                        hotplugStartAttempts[entry.Id] = now;
                        StartLocked(entry);
                        break;
                }
            }
        }
    }

    private Entry? EntryForEngineIdLocked(string engineId) =>
        engineId.StartsWith(EngineIdPrefix, StringComparison.Ordinal) ? entries.FirstOrDefault(e => e.Id == engineId[EngineIdPrefix.Length..]) : null;

    private void StartLocked(Entry entry)
    {
        if (!warmedUp || entry.Kind is not { } kind || string.IsNullOrEmpty(entry.SourceId) || running.ContainsKey(entry.Id)) return;
        IVideoInputSource? source = null;
        try
        {
            switch (kind)
            {
                case CaptureSourceKind.Camera:
                    if (cameraPresent(entry.SourceId) is false)
                    {
                        VideoIOLog.Note("input.camera", $"assigned device missing: {entry.Name}");
                        return;
                    }
                    source = factory.Camera(entry.SourceId, entry.Name, entry.FrameRate);
                    VideoIOLog.Note("input.camera", $"warm: {entry.Name}");
                    break;
                case CaptureSourceKind.Ndi:
                    source = factory.Ndi(entry.SourceId, entry.Name);
                    break;
                case CaptureSourceKind.Decklink:
                    if (!long.TryParse(entry.SourceId, out var persistentId))
                    {
                        VideoIOLog.Note("input.decklink", $"bad device id for {entry.Name}: {entry.SourceId}");
                        return;
                    }
                    source = factory.DeckLink(persistentId, entry.Name);
                    VideoIOLog.Note("input.decklink", $"warm: {entry.Name}");
                    break;
            }
        }
        catch (Exception ex)
        {
            VideoIOLog.Note("input.camera", $"warm-up failed: {entry.Name}: {ex.Message}");
            return;
        }
        if (source is null) return;
        running[entry.Id] = source;
        delay.SetDelay(entry.EngineId, entry.DelayFrames ?? 0);
    }

    private void StopLocked(Entry entry)
    {
        if (!running.Remove(entry.Id, out var source)) return;
        delay.Clear(entry.EngineId);
        try { source.Dispose(); }
        catch (Exception ex) { VideoIOLog.Note("input.camera", $"stop failed: {entry.Name}: {ex.Message}"); }
    }

    private bool Update(string id, Func<Entry, Entry> change)
    {
        lock (gate)
        {
            var index = entries.FindIndex(e => e.Id == id);
            if (index < 0) return false;
            entries[index] = change(entries[index]);
            Persist();
            return true;
        }
    }

    /// <summary>The entry itself plus every other camera entry on the same device.</summary>
    private List<int> DeviceMates(Entry entry) =>
        [.. Enumerable.Range(0, entries.Count).Where(i => entries[i].Id == entry.Id
            || (entry.Kind == CaptureSourceKind.Camera && entries[i].Kind == CaptureSourceKind.Camera && entries[i].SourceId == entry.SourceId))];

    private void ShareDeviceRates()
    {
        var shared = CaptureFormatChoice.SharedRates(
            entries.Where(e => e.Kind == CaptureSourceKind.Camera).Select(e => (e.SourceId ?? "", e.FrameRate)));
        for (var i = 0; i < entries.Count; i++)
            if (entries[i].Kind == CaptureSourceKind.Camera)
                entries[i] = entries[i] with { FrameRate = shared.TryGetValue(entries[i].SourceId ?? "", out var rate) ? rate : null };
    }

    /// <summary>Writes the entries (atomically: temp file, then rename). A failed save is noted, never thrown.</summary>
    private void Persist()
    {
        try
        {
            if (Path.GetDirectoryName(storePath) is { Length: > 0 } folder) Directory.CreateDirectory(folder);
            var temporary = storePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(entries, Json));
            File.Move(temporary, storePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            VideoIOLog.Note("input.camera", $"inventory save failed: {ex.Message}");
        }
    }

    /// <summary>Reads saved entries. Like the Mac's decoder, an entry without an id gets a new one; one without a name is skipped.</summary>
    internal static List<Entry>? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var stored = JsonSerializer.Deserialize<List<StoredEntry>>(File.ReadAllText(path), Json);
            return stored?.Where(e => e.Name is not null)
                .Select(e => new Entry(e.Id ?? NewId(), e.Name!, e.Kind, e.SourceId, e.AudioInputId, e.DelayFrames, e.FrameRate))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            VideoIOLog.Note("input.camera", $"inventory load failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>A new id in the Mac's UUID().uuidString form (upper case), so ids look the same on both apps.</summary>
    private static string NewId() => Guid.NewGuid().ToString().ToUpperInvariant();

    private sealed record StoredEntry(
        string? Id, string? Name, CaptureSourceKind? Kind, string? SourceId, string? AudioInputId, int? DelayFrames, double? FrameRate);
}
