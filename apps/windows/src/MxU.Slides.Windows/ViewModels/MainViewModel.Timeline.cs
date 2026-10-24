using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MxU.Slides.Audio;
using MxU.Slides.Audio.Control;
using MxU.Slides.Audio.Library;
using MxU.Slides.Audio.Multitrack;
using MxU.Slides.Audio.Timeline;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Core.Stage;
using MxU.Slides.LocalApi;
using MxU.Slides.Windows.Infrastructure;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// Song timelines: a presentation can carry a timeline that fires its slides (and media,
/// overlays, music and actions) at set times in the song. The <see cref="TimelinePlayer"/>
/// (ported from the Mac) plays and records them; this is its view of the app
/// (<see cref="ITimelineHost"/>) and the timeline bar under the slides. Timecode in (LTC
/// from a sound input, or MTC over MIDI) can drive it through a <see cref="TimecodeFollower"/>.
/// </summary>
public sealed partial class MainViewModel : ITimelineHost, ITimecodeHost, ITimecodeInputs
{
    /// <summary>A presentation opened from the library (not a service) still needs an item id for its timeline.</summary>
    private const string LibraryTimelinePrefix = "presentation:";

    private TimelinePlayer? timeline;
    private TimelineMusic? timelineMusic;
    private LazyTracksPlayer? tracks;
    private TimecodeFollower? timecode;
    private TimecodeDisplay? publishedTimecode;
    private (string PresentationId, TimelinesUndo Undo)? lastTake;
    private (string State, string? Song, string? Rate, bool Follows, bool HasInput)? lastTimecodeStatus;

    /// <summary>Plays and records song timelines (created on the UI thread, which its ticks come back to).</summary>
    public TimelinePlayer Timeline => timeline ??= CreateTimeline();

    private TimelinePlayer CreateTimeline()
    {
        timelineMusic = new TimelineMusic(Audio);
        tracks = new LazyTracksPlayer(TrackRoutingFor);
        var created = new TimelinePlayer(this, this, new LibraryStoreActions(Library, () => SelectedService), tracks, ui: SynchronizationContext.Current);
        created.Noted += (crumb, detail) => Log.Info($"Timeline: {crumb} {detail}");
        created.Changed += UpdateTimelineBar;
        return created;
    }

    /// <summary>Timecode in (created with the timeline player it drives).</summary>
    public TimecodeFollower Timecode => timecode ??= CreateTimecode();

    private TimecodeFollower CreateTimecode()
    {
        var created = new TimecodeFollower(this, this, Timeline, Settings.Timecode, ui: SynchronizationContext.Current);
        created.SettingsChanged += () =>
        {
            Settings.Timecode = created.Settings;
            Settings.Save();
        };
        created.Noted += (crumb, detail) => Log.Info($"Timecode: {crumb} {detail}");
        created.Changed += OnTimecodeChanged;
        return created;
    }

    /// <summary>At launch: opens the timecode input this PC used last time, if it has one.</summary>
    private void StartTimecode()
    {
        if (Settings.Timecode is null) return;
        Timecode.Start();
    }

    /// <summary>On quit: closes the timecode input and stops any timeline and stems.</summary>
    public void StopTimeline()
    {
        timecode?.StopInput();
        timeline?.Stop();
        tracks?.Dispose();
    }

    private static string TimelineItemId(string? itemId, string presentationId) => itemId ?? LibraryTimelinePrefix + presentationId;

    private static string? ServiceItemOf(string? timelineItemId) =>
        timelineItemId?.StartsWith(LibraryTimelinePrefix, StringComparison.Ordinal) == true ? null : timelineItemId;

    // MARK: The timeline bar

    /// <summary>The bar was opened by hand (it also shows by itself when the song has a timeline).</summary>
    [ObservableProperty]
    public partial bool TimelineBarOpen { get; set; }

    [ObservableProperty]
    public partial bool ShowsTimelineBar { get; set; }

    [ObservableProperty]
    public partial bool TimelinePlaying { get; set; }

    [ObservableProperty]
    public partial bool TimelineRecording { get; set; }

    [ObservableProperty]
    public partial bool HasTimeline { get; set; }

    [ObservableProperty]
    public partial double TimelinePosition { get; set; }

    [ObservableProperty]
    public partial double TimelineLength { get; set; } = 1;

    [ObservableProperty]
    public partial string TimelineClock { get; set; } = "0:00.0";

    [ObservableProperty]
    public partial string TimelineStatus { get; set; } = "";

    /// <summary>The playhead slider is being dragged: the ticks don't move it meanwhile.</summary>
    public bool TimelineScrubbing { get; set; }

    partial void OnTimelineBarOpenChanged(bool value) => UpdateTimelineBar();

    /// <summary>The song the bar is for: the open presentation, as opened (service item and arrangement).</summary>
    private (string ItemId, Presentation Presentation, string? ArrangementId)? TimelineTarget =>
        CurrentPresentation is { } p ? (TimelineItemId(CurrentContextItemId, p.Id), p, SelectedArrangement?.Id) : null;

    /// <summary>The open song's timeline (for its arrangement), if it has one.</summary>
    public PresentationTimeline? CurrentTimeline =>
        CurrentPresentation is { } p ? PresentationTimelines.Timeline(SelectedArrangement?.Id, p) : null;

    private void UpdateTimelineBar()
    {
        try
        {
            UpdateTimelineBarState();
        }
        finally
        {
            TimelineBarUpdated?.Invoke();
        }
    }

    private void UpdateTimelineBarState()
    {
        if (TimelineTarget is not { } target)
        {
            ShowsTimelineBar = false;
            return;
        }
        var saved = PresentationTimelines.Timeline(target.ArrangementId, target.Presentation);
        var player = timeline;
        var current = player is not null && player.IsCurrent(target.ItemId) && player.PresentationId == target.Presentation.Id;
        var playing = current && player!.Playing;
        // A song whose timeline has music: open the sound device now (in the background), so
        // when it plays, the first clip starts on cue instead of holding up the slides after it.
        if (!playing && saved?.Cues.Any(c => c.Kind() == TimelineLane.Audio) == true) Audio.Warm();
        var recording = current && player!.Recording;
        HasTimeline = saved is not null;
        ShowsTimelineBar = TimelineBarOpen || saved is not null || playing || recording;
        TimelinePlaying = playing;
        TimelineRecording = recording;
        var position = current ? player!.PlayheadTime : 0;
        var length = saved is null ? 0 : PresentationTimelines.Length(saved);
        TimelineLength = Math.Max(Math.Max(length, position), 1);
        if (!TimelineScrubbing) TimelinePosition = position;
        TimelineClock = $"{PresentationTimelines.PlayheadClock(position)} / {PresentationTimelines.PlayheadClock(length)}";
        TimelineStatus = TimelineStatusText(target.ItemId, saved, current ? player : null);
    }

    private string TimelineStatusText(string itemId, PresentationTimeline? saved, TimelinePlayer? player)
    {
        if (player?.TimecodeRecording is { Started: false }) return "Waiting for timecode to reach this song…";
        if (player?.Recording == true) return "Recording: click each slide as the song plays. Press Record again when it ends.";
        if (timecode?.SongItemId == itemId && timecode.IsLocked) return "Following timecode";
        if (player?.Pending is { } go)
            return $"{go.Kind} into {go.NextName ?? "the next item"} in {Countdown(go.FiresAt)}";
        if (player?.Playing == true && player.NextCue is { } next) return $"Next slide in {Countdown(next.FiresAt)}";
        if (saved is null) return "No timeline yet. Press Record, then click the slides as the song plays.";
        if (saved.TimecodeEnabled) return $"Follows timecode from {PresentationTimelines.Timecode(saved.TimecodeStart)}";
        if (!saved.Cues.Any(c => c.Kind() == TimelineLane.Audio)) return "Add the song's music from ⋯ so the timeline plays it.";
        return "";
    }

    private static string Countdown(DateTimeOffset at)
    {
        var seconds = Math.Max(0, (at - DateTimeOffset.Now).TotalSeconds);
        return TimeSpan.FromSeconds(Math.Ceiling(seconds)).ToString(@"m\:ss");
    }

    [RelayCommand]
    private void TimelinePlayPause()
    {
        if (TimelineTarget is not { } target) return;
        if (Timeline.IsCurrent(target.ItemId) && Timeline.Playing)
        {
            Timeline.Stop();
            return;
        }
        Timeline.Play(target.ItemId, target.Presentation, target.ArrangementId);
    }

    /// <summary>Stops and puts the playhead back to the start.</summary>
    [RelayCommand]
    private void TimelineRewind()
    {
        if (TimelineTarget is not { } target) return;
        Timeline.Stop();
        Timeline.Seek(0, target.ItemId, target.Presentation, target.ArrangementId);
        UpdateTimelineBar();
    }

    /// <summary>Starts a take (slides fired while it runs become cues), or ends it.</summary>
    [RelayCommand]
    private void TimelineRecord()
    {
        if (TimelineTarget is not { } target) return;
        if (Timeline.Recording)
        {
            Timeline.Stop();
            Notice = "Timeline recorded";
            return;
        }
        var start = target.Presentation.Timelines?.FirstOrDefault()?.TimecodeStart ?? 3600 * PresentationTimelines.FramesPerSecond;
        Timeline.Record(target.ItemId, target.Presentation, target.ArrangementId, start);
    }

    /// <summary>Moves the playhead (from the bar's slider).</summary>
    public void SeekTimeline(double seconds)
    {
        if (TimelineTarget is not { } target) return;
        Timeline.Seek(Math.Max(0, seconds), target.ItemId, target.Presentation, target.ArrangementId);
        UpdateTimelineBar();
    }

    /// <summary>Puts a song from the Music library on the timeline's music lane, from the start (replacing any music there).</summary>
    public void SetTimelineSong(AudioItem item)
    {
        if (TimelineTarget is not { } target) return;
        var edited = EditableTimeline(target.Presentation, target.ArrangementId);
        var length = (item.OutPoint ?? item.DurationSeconds) - (item.InPoint ?? 0);
        edited.Cues =
        [
            .. edited.Cues.Where(c => c.Kind() != TimelineLane.Audio),
            new TimelineCue
            {
                Id = Core.Library.LibraryStore.NewId(), Lane = TimelineLane.Audio.Raw(), At = 0,
                Duration = length > 0 ? length : null, MediaId = item.Id,
            },
        ];
        Timeline.Save(edited, target.Presentation.Id, "Add Music");
        Notice = $"\"{item.Name}\" plays with this song's timeline";
    }

    /// <summary>Turns following timecode on or off for the open song's timeline.</summary>
    public void SetTimelineTimecode(bool enabled, int? startFrames = null)
    {
        if (TimelineTarget is not { } target) return;
        var edited = EditableTimeline(target.Presentation, target.ArrangementId);
        edited.TimecodeEnabled = enabled;
        if (startFrames is { } frames) edited.TimecodeStart = Math.Max(0, frames);
        Timeline.Save(edited, target.Presentation.Id, "Timecode");
    }

    /// <summary>Deletes the open song's timeline (for its arrangement).</summary>
    public void ClearTimeline()
    {
        if (TimelineTarget is not { } target || CurrentTimeline is not { } existing) return;
        if (Timeline.IsCurrent(target.ItemId)) Timeline.Stop();
        var before = target.Presentation.Timelines?.ToList();
        var kept = target.Presentation.Timelines?.Where(t => t.Id != existing.Id).ToList();
        ((ITimelineHost)this).SaveTimelines(target.Presentation.Id, kept is { Count: > 0 } ? kept : null, "Clear Timeline");
        lastTake = (target.Presentation.Id, new TimelinesUndo(before));
        Notice = "Timeline cleared (⋯ › Undo puts it back)";
    }

    public bool CanUndoTimeline => lastTake is not null;

    /// <summary>Puts the timelines back as they were before the last take (or clear).</summary>
    public void UndoTimeline()
    {
        if (lastTake is not { } undo) return;
        lastTake = null;
        if (timeline?.Recording == true) timeline.Stop();
        ((ITimelineHost)this).SaveTimelines(undo.PresentationId, undo.Undo.Timelines, null);
        Notice = "Timeline put back";
    }

    private static PresentationTimeline EditableTimeline(Presentation presentation, string? arrangementId) =>
        PresentationTimelines.Timeline(arrangementId, presentation)?.Copy() ?? new PresentationTimeline
        {
            Id = Core.Library.LibraryStore.NewId(),
            ArrangementId = string.IsNullOrEmpty(arrangementId) ? null : arrangementId,
            TimecodeEnabled = false,
            TimecodeStart = presentation.Timelines?.FirstOrDefault()?.TimecodeStart ?? 3600 * PresentationTimelines.FramesPerSecond,
            Cues = [],
        };

    // MARK: The timeline editor

    /// <summary>
    /// Goes up by one each time a song's timelines (or section map) are saved, so the timeline
    /// editor knows to lay its lanes out again rather than only move its playhead.
    /// </summary>
    public int TimelineRevision { get; private set; }

    /// <summary>Raised after the timeline bar is brought up to date: when the song changes, a timeline is saved, and 30 times a second while one plays.</summary>
    public event Action? TimelineBarUpdated;

    /// <summary>The song the timeline editor shows: the open one, as opened (its service item and arrangement).</summary>
    public (string ItemId, Presentation Presentation, string? ArrangementId)? TimelineEditorTarget => TimelineTarget;

    /// <summary>The service item behind a timeline's item id (null for a song opened from the library).</summary>
    public ServiceItem? TimelineServiceItem(string? timelineItemId) =>
        ServiceItemOf(timelineItemId) is { } id ? SelectedService?.Items.FirstOrDefault(i => i.Id == id) : null;

    /// <summary>Saves a song the timeline editor changed (its timelines and section map) and brings the bar up to date.</summary>
    public void SaveTimelineSong(Presentation presentation)
    {
        Library.Save(presentation);
        TimelineRevision++;
        UpdateTimelineBar();
    }

    // MARK: Tracks (stems) on this computer

    private TrackOutputSettings? trackOutputs;

    /// <summary>This computer's tracks setup: the interface, each family's output, each track's fader, mute and solo.</summary>
    public TrackOutputSettings TrackOutputs => trackOutputs ??= TrackRouting.Load(Path.Combine(AppPaths.DataRoot, TrackRouting.FileName));

    /// <summary>Saves the tracks setup and sends the new routing to the stems player.</summary>
    public void SaveTrackOutputs()
    {
        try
        {
            TrackRouting.Save(TrackOutputs, Path.Combine(AppPaths.DataRoot, TrackRouting.FileName));
        }
        catch (Exception error)
        {
            Log.Error("Couldn't save the tracks setup", error);
        }
        ApplyTrackRouting();
    }

    /// <summary>The church's tracks (click, guide, keys...), in playback order.</summary>
    public IReadOnlyList<TrackChannel> TrackChannels => Library.Get<TrackChannelList>(TrackChannelList.WellKnownId)?.Channels ?? [];

    /// <summary>Changes the track list (made the first time it's needed) and sends the new routing to the stems player.</summary>
    public void UpdateTrackChannels(Action<List<TrackChannel>> change)
    {
        var list = Library.Get<TrackChannelList>(TrackChannelList.WellKnownId)
            ?? new TrackChannelList { Id = TrackChannelList.WellKnownId, Channels = [] };
        var channels = list.Channels.Select(c => c.Copy()).ToList();
        change(channels);
        list.Channels = channels;
        Library.Save(list);
        ApplyTrackRouting();
    }

    /// <summary>Sends the tracks setup to the stems player (if it is running yet; it picks it up when it starts).</summary>
    public void ApplyTrackRouting() => tracks?.Refresh();

    /// <summary>
    /// The interface and mixer the stems play through: the device chosen in the tracks setup
    /// (else the Windows default) and <see cref="TrackRouting.Build"/> for the church's tracks
    /// plus any channel a song has stems on that is no longer in the list.
    /// </summary>
    private (AudioOutputDevice? Device, MultitrackRouting Routing) TrackRoutingFor(IEnumerable<string> songChannels)
    {
        var settings = TrackOutputs;
        var devices = AudioDeviceList.OutputDevices();
        var device = settings.DeviceUid is { } uid ? devices.FirstOrDefault(d => d.Uid == uid) : null;
        var channels = device?.ChannelCount ?? AudioDeviceList.DefaultOutputDevice()?.ChannelCount ?? 2;
        return (device, TrackRouting.Build(TrackChannels, settings, channels, songChannels));
    }

    // MARK: ITimelineHost

    Presentation? ITimelineHost.Presentation(string id) => Library.Get<Presentation>(id);

    void ITimelineHost.SaveTimelines(string presentationId, List<PresentationTimeline>? timelines, string? undoLabel, TimelinesUndo? undoTo)
    {
        if (Library.Get<Presentation>(presentationId) is not { } presentation) return;
        if (undoLabel is not null && undoTo is not null) lastTake = (presentationId, undoTo);
        presentation.Timelines = timelines;
        Library.Save(presentation);
        TimelineRevision++;
        UpdateTimelineBar();
    }

    ServiceItem? ITimelineHost.CurrentServiceItem(string itemId) => SelectedService?.Items.FirstOrDefault(i => i.Id == itemId);

    IReadOnlyList<ServiceItem> ITimelineHost.FireableItems() =>
        SelectedService?.Items.Where(i => i.ItemKind is ServiceItemKind.Presentation or ServiceItemKind.Media && i.HiddenInPresenter != true).ToList() ?? [];

    string? ITimelineHost.Name(string refId) =>
        Library.Get<Presentation>(refId)?.Name ?? Library.Get<MediaItem>(refId)?.Name ?? Library.Get<AudioItem>(refId)?.Name;

    void ITimelineHost.FireSlide(Slide slide, Presentation presentation, string? arrangementId, string? contextId, int occurrence, bool runSlideActions) =>
        Fire(presentation, arrangementId, occurrence, ServiceItemOf(contextId), runSlideActions);

    void ITimelineHost.FireServiceItem(ServiceItem item)
    {
        if (ServiceItems.FirstOrDefault(r => r.Item.Id == item.Id) is { } row) FireServiceItem(row);
    }

    void ITimelineHost.RunActions(IReadOnlyList<SlideAction> actions) => Actions.Execute(actions);

    IReadOnlySet<string> ITimelineHost.Live
    {
        get
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var media in Show.LiveMedia.Values)
                ids.Add(media.MediaId.StartsWith(VideoIO.Capture.VideoInputInventory.EngineIdPrefix, StringComparison.Ordinal)
                    ? media.MediaId[VideoIO.Capture.VideoInputInventory.EngineIdPrefix.Length..]
                    : media.MediaId);
            foreach (var overlay in Show.LiveOverlays) ids.Add(overlay.Id);
            foreach (var player in audio?.Players ?? [])
                if (player.IsPlaying && player.SingleItemId is { } item) ids.Add(item);
            if (Show.HasAlert && liveAlertPresetId is { } alert) ids.Add(alert);
            return ids;
        }
    }

    string? ITimelineHost.LiveAlertId => Show.HasAlert ? liveAlertPresetId : null;

    LayerKind? ITimelineHost.LayerShowing(string contentId, bool liveInput)
    {
        var id = liveInput ? LiveInputCueId(contentId) : contentId;
        foreach (var (layer, media) in Show.LiveMedia)
            if (media.MediaId == id) return layer;
        return null;
    }

    string? ITimelineHost.AudioFilePath(AudioItem item) => AudioImporter.FilePath(Library, item);

    ITimelineMusic? ITimelineHost.Music => timelineMusic;

    void ITimelineHost.OpenTimelineCard(string itemId)
    {
        if (ServiceItems.FirstOrDefault(r => r.Item.Id == itemId) is { } row && SelectedServiceItem != row) SelectedServiceItem = row;
    }

    // MARK: ITimecodeHost

    Service? ITimecodeHost.CurrentService => SelectedService;

    IReadOnlyList<ServiceItem> ITimecodeHost.RunOfShow(Service service) =>
        service.Items.Where(i => i.HiddenInPresenter != true).ToList();

    Presentation? ITimecodeHost.Presentation(string id) => Library.Get<Presentation>(id);

    TimecodeDisplay? ITimecodeHost.PublishedTimecode
    {
        get => publishedTimecode;
        set => publishedTimecode = value;
    }

    string? ITimecodeHost.LiveContextId => Live?.ItemId;

    void ITimecodeHost.RevealLive()
    {
        if (Live?.ItemId is { } itemId && ServiceItems.FirstOrDefault(r => r.Item.Id == itemId) is { } row && SelectedServiceItem != row)
            SelectedServiceItem = row;
    }

    private void OnTimecodeChanged()
    {
        if (timecode is not { } follower) return;
        var status = follower.Status();
        var key = (status.State, status.ServiceItemId, status.Rate, status.Follows, status.HasInput);
        if (lastTimecodeStatus != key)
        {
            lastTimecodeStatus = key;
            PublishApi(ApiTopic.Timecode);
            UpdateTimelineBar();
        }
    }

    ApiTimecodeStatus IApiBridge.TimecodeStatus()
    {
        var s = Timecode.Status();
        return new ApiTimecodeStatus(
            s.HasInput, s.Source, s.Follows, s.State, s.Label, s.Rate, s.Reverse, s.OffsetFrames, s.ServiceItemId,
            s.Anchor is { } a ? new ApiTimecodeAnchor(a.Seconds, a.AnchoredAt, a.Running, a.Reverse) : null,
            s.Timeline is { } t ? new ApiTimecodeTimeline(t.ServiceItemId, t.Position, t.AnchoredAt, t.Running, t.Length) : null);
    }

    // MARK: ITimecodeInputs

    /// <summary>LTC: one channel of a sound input, decoded as it arrives (the capture thread).</summary>
    ITimecodeReader ITimecodeInputs.OpenLtc(string deviceId, int channel)
    {
        var hub = new AudioInputHub(deviceId, _ => (1f, false));
        var token = Guid.Empty;
        LtcReader? reader = null;
        reader = new LtcReader(
            onStop: () =>
            {
                hub.RemoveTap(token);
                hub.Dispose();
            },
            onChannel: c => hub.UpdateTap(token, new InputChannelSelection.Mono(c)));
        token = hub.AddTap(new InputChannelSelection.Mono(channel),
            (buffer, at) => reader.Read(buffer.Data[0].AsSpan(0, buffer.FrameLength), buffer.SampleRate, at), trimmed: false);
        return reader;
    }

    /// <summary>MTC: the bytes from one MIDI input (the MIDI listener opens while timecode needs it).</summary>
    ITimecodeReader ITimecodeInputs.OpenMtc(string sourceId)
    {
        MtcReader? reader = null;
        void Heard(string device, byte[] bytes, double at)
        {
            if (device == sourceId) reader?.Read(bytes, at);
        }
        reader = new MtcReader(onStop: () =>
        {
            MidiIn.Message -= Heard;
            timecodeWantsMidi = false;
            ApplyMidiSettings();
        });
        MidiIn.Message += Heard;
        timecodeWantsMidi = true;
        ApplyMidiSettings();
        return reader;
    }

    /// <summary>
    /// The multitrack (stems) player, made the first time a timeline has stems to play, so
    /// songs without stems never open a second sound stream.
    /// </summary>
    /// <param name="routing">The interface and mixer for the stems, given the channels of the songs about to play.</param>
    private sealed class LazyTracksPlayer(Func<IEnumerable<string>, (AudioOutputDevice? Device, MultitrackRouting Routing)> routing) : ITracksPlayer, IDisposable
    {
        private LocalTracksPlayer? player;

        // The channels of the songs it has been asked to play (so a track taken out of the list still sounds).
        private readonly HashSet<string> songChannels = new(StringComparer.Ordinal);

        private LocalTracksPlayer Player
        {
            get
            {
                if (player is not null) return player;
                player = new LocalTracksPlayer(new MultitrackEngine());
                Apply();
                return player;
            }
        }

        /// <summary>Sends the current interface and mixer again (the tracks setup or the track list changed).</summary>
        public void Refresh()
        {
            if (player is not null) Apply();
        }

        private void Apply()
        {
            if (player is null) return;
            var (device, mixer) = routing(songChannels);
            player.Attach(device);
            player.SetRouting(mixer);
        }

        // Before a song plays, the mixer must know its tracks: a stem on a channel it doesn't know plays silently.
        private LocalTracksPlayer For(params MultitrackSong?[] songs)
        {
            var added = false;
            foreach (var stem in songs.OfType<MultitrackSong>().SelectMany(s => s.Stems)) added |= songChannels.Add(stem.ChannelId);
            var existed = player is not null;
            var ready = Player;
            if (added && existed) Apply();
            return ready;
        }

        public string? LatestSongId => player?.LatestSongId;

        public bool Reattaching => false;

        public double? Position(string songId) => player?.Position(songId);

        public void Play(MultitrackSong song, double from) => For(song).Play(song, from);

        public void Crossfade(MultitrackSong? to, double from, double seconds)
        {
            if (to is not null || player is not null) For(to).Crossfade(to, from, seconds);
        }

        public void Stop() => player?.Stop();

        public void Seek(double position) => player?.Seek(position);

        public void Chase(MultitrackSong song, double position, double hostTime) => For(song).Chase(song, position, hostTime);

        public void Transition(MultitrackTransition? transition)
        {
            if (transition is not null || player is not null) For(transition?.Next).Transition(transition);
        }

        public void CueAhead(IReadOnlyList<MultitrackSong> songs)
        {
            if (songs.Count > 0) Player.CueAhead(songs);
        }

        public void Dispose()
        {
            player?.Stop();
            player?.AfterPending(() => player.Engine.Dispose());
            player?.Dispose();
        }
    }
}
