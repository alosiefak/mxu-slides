using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Tests.Automation;

/// <summary>
/// A clock tests step by hand. Wall time starts at <c>start</c>; host time (timestamps) at
/// zero; timers fire, in order, as <see cref="Advance"/> passes them, on the test's thread.
/// </summary>
internal sealed class ManualClock(DateTimeOffset start, TimeZoneInfo? zone = null) : TimeProvider
{
    private readonly List<ManualTimer> timers = [];

    /// <summary>Seconds since the clock started.</summary>
    public double Elapsed { get; private set; }

    public DateTimeOffset Now => start.AddTicks((long)Math.Round(Elapsed * TimeSpan.TicksPerSecond));

    public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();

    public override TimeZoneInfo LocalTimeZone => zone ?? TimeZoneInfo.Utc;

    public override long TimestampFrequency => 1_000_000_000;

    public override long GetTimestamp() => (long)Math.Round(Elapsed * TimestampFrequency);

    /// <summary>Host time in seconds, as the automation classes read it.</summary>
    public double HostSeconds => GetTimestamp() / (double)TimestampFrequency;

    public int ActiveTimers => timers.Count(t => t.Due is not null);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves time on, firing each timer that comes due on the way.</summary>
    public void Advance(double seconds)
    {
        var end = Elapsed + seconds;
        for (var guard = 0; guard < 1_000_000; guard++)
        {
            var next = timers.Where(t => t.Due is { } due && due <= end + 1e-12).MinBy(t => t.Due);
            if (next is null) break;
            Elapsed = Math.Max(Elapsed, next.Due!.Value);
            next.Fire();
        }
        Elapsed = end;
    }

    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        public double? Due { get; private set; }
        private double? period;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.Elapsed + dueTime.TotalSeconds;
            this.period = period == Timeout.InfiniteTimeSpan || period == TimeSpan.Zero ? null : period.TotalSeconds;
            return true;
        }

        public void Fire()
        {
            Due = period is { } every ? Due + every : null;
            callback(state);
        }

        public void Dispose()
        {
            Due = null;
            clock.timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>A show that records what it was asked to do, as short strings ("clearLayer:slide").</summary>
internal sealed class RecordingShow : IShowActions
{
    public List<string> Calls { get; } = [];

    public string? ActiveOutputPresetId { get; set; }

    public IReadOnlyList<MidiOutDevice> MidiOutDevices { get; set; } = [];

    public bool HasConfidenceMonitors { get; set; } = true;

    /// <summary>Called when a slide is fired (a test can feed it back to the router).</summary>
    public Action<Slide, Presentation>? OnFireSlide { get; set; }

    public void ClearLayer(LayerKind layer) => Calls.Add($"clearLayer:{layer.Raw()}");
    public void ClearAll(IReadOnlySet<LayerKind> protecting) =>
        Calls.Add($"clearAll:{string.Join(",", protecting.Select(l => l.Raw()).Order(StringComparer.Ordinal))}");
    public void ClearAudio() => Calls.Add("clearAudio");
    public void SwitchOutputPreset(string presetId)
    {
        Calls.Add($"preset:{presetId}");
        ActiveOutputPresetId = presetId;
    }
    public void FireMedia(MediaItem item) => Calls.Add($"media:{item.Id}");
    public void FireAlert(AlertPreset preset) => Calls.Add($"alert:{preset.Id}");
    public void DismissAlert() => Calls.Add("dismissAlert");
    public void FireOverlay(Overlay overlay) => Calls.Add($"overlay:{overlay.Id}");
    public void DismissOverlay(string overlayId) => Calls.Add($"dismissOverlay:{overlayId}");
    public void DismissAllOverlays() => Calls.Add("dismissOverlays");
    public void FireLiveInput(string liveInputId, LayerKind layer) => Calls.Add($"liveInput:{liveInputId}@{layer.Raw()}");
    public void FireLiveInputSource(CaptureSourceKind kind, string sourceId, LayerKind layer) => Calls.Add($"liveSource:{kind}:{sourceId}@{layer.Raw()}");
    public void SetAudioInputEnabled(string audioInputId, bool enabled) => Calls.Add($"audioInput:{audioInputId}={enabled}");
    public void FireSlide(Slide slide, Presentation presentation)
    {
        Calls.Add($"slide:{presentation.Id}/{slide.Id}");
        OnFireSlide?.Invoke(slide, presentation);
    }
    public void FireAudio(AudioItem item, bool? repeatOverride) => Calls.Add($"audio:{item.Id}:{repeatOverride}");
    public void FireAudioPlaylist(Playlist playlist, string? startAtEntryId, bool? repeatOverride) =>
        Calls.Add($"playlist:{playlist.Id}:{startAtEntryId}:{repeatOverride}");
    public void StartTimer(string timerId) => Calls.Add($"timerStart:{timerId}");
    public void PauseTimer(string timerId) => Calls.Add($"timerPause:{timerId}");
    public void ResetTimer(string timerId) => Calls.Add($"timerReset:{timerId}");
    public void ConfigureTimer(string timerId, TimerMode? mode, double? durationSeconds, int? hour, int? minute) =>
        Calls.Add($"timerConfigure:{timerId}:{mode}:{durationSeconds}:{hour}:{minute}");
    public void SendMidi(MidiOutMessage message, string? destinationId) =>
        Calls.Add($"midi:{Convert.ToHexString(message.Bytes())}->{destinationId ?? "settings"}");
    public void StartCapture(string presetId) => Calls.Add($"captureStart:{presetId}");
    public void StopCapture(string presetId) => Calls.Add($"captureStop:{presetId}");
    public void StopAllCapture() => Calls.Add("captureStopAll");
    public void SetConfidenceLayout(string? layoutId, Guid? screenId) => Calls.Add($"confidence:{layoutId}:{screenId}");
}

/// <summary>A library held in memory.</summary>
internal sealed class MemoryLibrary : IActionLibrary
{
    public Dictionary<string, object> Documents { get; } = [];

    public Dictionary<string, string> ItemPresets { get; } = [];

    public List<string> Used { get; } = [];

    public MemoryLibrary Add(params object[] documents)
    {
        foreach (var document in documents) Documents[((ILibraryDocument)document).Id] = document;
        return this;
    }

    private T? Get<T>(string id) where T : class => Documents.GetValueOrDefault(id) as T;

    public ActionCombo? ActionCombo(string id) => Get<ActionCombo>(id);
    public IReadOnlyList<ActionCombo> ActionCombos() => Documents.Values.OfType<ActionCombo>().ToList();
    public MediaItem? Media(string id) => Get<MediaItem>(id);
    public AlertPreset? AlertPreset(string id) => Get<AlertPreset>(id);
    public Presentation? Presentation(string id) => Get<Presentation>(id);
    public Overlay? Overlay(string id) => Get<Overlay>(id);
    public AudioItem? Audio(string id) => Get<AudioItem>(id);
    public Playlist? Playlist(string id) => Get<Playlist>(id);
    public StreamRecordPreset? StreamPreset(string id) => Get<StreamRecordPreset>(id);
    public OutputPreset? OutputPreset(string id) => Get<OutputPreset>(id);
    public string? Name(string id) => (Documents.GetValueOrDefault(id) as ILibraryDocument)?.Name;
    public string? ServiceItemOutputPresetId(string contextId) => ItemPresets.GetValueOrDefault(contextId);
    public void MarkUsed(string id) => Used.Add(id);
}

/// <summary>Builders for the documents the tests use.</summary>
internal static class Make
{
    public static TimelineCue Cue(
        string id, string lane, double at, double? duration = null, string? mediaId = null, double? inPoint = null,
        string? overlayId = null, string? alertId = null, string? liveInputId = null, string? sectionId = null,
        int? sectionOccurrence = null, string? slideId = null, List<SlideAction>? actions = null) => new()
        {
            Id = id, Lane = lane, At = at, Duration = duration, MediaId = mediaId, InPoint = inPoint, OverlayId = overlayId,
            AlertId = alertId, LiveInputId = liveInputId, SectionId = sectionId, SectionOccurrence = sectionOccurrence,
            SlideId = slideId, Actions = actions,
        };

    public static PresentationTimeline Timeline(
        string id, bool timecodeEnabled = false, int timecodeStart = 0, double? length = null, List<TimelineCue>? cues = null,
        string? arrangementId = null) => new()
        {
            Id = id, ArrangementId = arrangementId, TimecodeEnabled = timecodeEnabled, TimecodeStart = timecodeStart,
            Length = length, Cues = cues ?? [],
        };

    public static SlideAction Action(string id, SlideActionKind kind, Action<SlideAction>? fill = null)
    {
        var action = new SlideAction { Id = id, Kind = kind };
        fill?.Invoke(action);
        return action;
    }

    public static ClipFade Fade(double seconds, string? shape = null, string? placement = null, double? midpoint = null) =>
        new() { Seconds = seconds, Shape = shape, Placement = placement, Midpoint = midpoint };

    public static MediaItem Media(string id, MediaKind kind = MediaKind.Video, MediaClassification classification = MediaClassification.Foreground) => new()
    {
        Id = id, Name = id, MediaKind = kind, Classification = classification, FileHash = "", FileName = id + ".mov",
        FileStatus = MediaFileStatus.Ready, StatusDetail = "", Tags = [], Favorite = false, Collections = [], Loops = false,
    };

    public static Presentation Deck(string id, params (string Id, string? Section)[] slides) => new()
    {
        Id = id, Name = id, PresentationKind = PresentationKind.Song, ThemeId = "",
        Slides = slides.Select(s => new Slide { Id = s.Id, Name = s.Id, Objects = [], SectionId = s.Section }).ToList(),
    };

    public static bool Same<T>(T? a, T? b) => MxuJson.StructurallyEqual(a, b);

    /// <summary>A counter for newId parameters ("n1", "n2", …).</summary>
    public static Func<string> Ids(string prefix = "n")
    {
        var count = 0;
        return () => prefix + ++count;
    }
}
