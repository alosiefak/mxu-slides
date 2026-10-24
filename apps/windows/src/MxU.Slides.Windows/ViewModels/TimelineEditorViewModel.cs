using MxU.Slides.Audio.Library;
using MxU.Slides.Audio.Timeline;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Infrastructure;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// The timeline editor's state and commands: the open song's lanes, what is selected, the
/// zoom and scroll, drags in progress, and every edit (move, trim, fade, split, add, delete,
/// undo…). It follows the song open in the main window. The rules themselves are Core's
/// (<see cref="TimelineEditing"/>, <see cref="TimelineEditorLayout"/>, <see cref="TimelineClips"/>);
/// this class applies them to the library and keeps an undo history. The window
/// (Views/TimelineEditorWindow) draws it and passes it the mouse and keys; the self-test
/// drives it directly. Port of PresentationTimelineLanes and PresentationTimelineContext
/// in Sources/PresentationTimelineView.swift. Use it on the UI thread.
/// </summary>
public sealed partial class TimelineEditorViewModel : IDisposable
{
    private readonly MainViewModel main;
    private readonly TimelineEditHistory history = new();
    private readonly SynchronizationContext? ui;

    // The clipboard is shared by every song (copy from one, paste into another), as on the Mac.
    private static List<TimelineCue> clipboard = [];

    // Folding the tracks applies to every song (the Mac keeps it in its user defaults).
    private static bool tracksExpanded;

    private int seenRevision = -1;
    private string mapKey = "";
    private bool refreshQueued;
    private bool wasPlaying;
    private bool wasRecording;
    private bool disposed;

    public TimelineEditorViewModel(MainViewModel main)
    {
        this.main = main;
        ui = SynchronizationContext.Current;
        Waveforms = new TimelineWaveformStore(Path.Combine(AppPaths.LocalRoot, "Waveforms"), ui);
        Waveforms.Changed += () => Redraw?.Invoke();
        main.TimelineBarUpdated += OnTimelineBarUpdated;
        main.Library.Changed += OnLibraryChanged;
        Refresh();
    }

    public MainViewModel Main => main;

    /// <summary>Works out music and stem waveforms off the UI thread.</summary>
    public TimelineWaveformStore Waveforms { get; }

    /// <summary>The rows or cards changed: lay the label column out again and redraw the lanes.</summary>
    public event Action? LayoutChanged;

    /// <summary>Something on the lanes changed but not the rows (a drag, the selection, a waveform arriving).</summary>
    public event Action? Redraw;

    /// <summary>The playhead moved (30 times a second while playing).</summary>
    public event Action? PlayheadMoved;

    /// <summary>The toolbar's state changed (selection, transport, undo).</summary>
    public event Action? StateChanged;

    /// <summary>A message for the person editing ("Stop recording first").</summary>
    public event Action<string>? Message;

    // MARK: The song

    /// <summary>The timeline's item id: the service item, or "presentation:&lt;id&gt;" for a song opened from the library.</summary>
    public string? ItemId { get; private set; }

    /// <summary>The song, fresh from the library.</summary>
    public Presentation? Song { get; private set; }

    public string? ArrangementId { get; private set; }

    public bool HasSong => Song is not null;

    /// <summary>The song's timeline for its arrangement (null until the first cue is added or recorded).</summary>
    public PresentationTimeline? Timeline { get; private set; }

    public IReadOnlyList<TimelineCue> Cues => Timeline?.Cues ?? [];

    /// <summary>The song's sections as they play in its arrangement (slide cues point into these).</summary>
    public IReadOnlyList<TimelinePlayedBlock> Blocks { get; private set; } = [];

    /// <summary>The service item the song was opened from (null when opened from the library: no transition or timecode order then).</summary>
    public ServiceItem? ServiceItem => main.TimelineServiceItem(ItemId);

    public string Title => Song?.Name ?? "No song open";

    // MARK: Transport

    private TimelinePlayer Player => main.Timeline;

    /// <summary>The timeline player is on this song (its playhead is ours).</summary>
    public bool IsCurrent => ItemId is { } id && Player.IsCurrent(id) && Player.PresentationId == Song?.Id;

    public bool IsPlaying => IsCurrent && Player.Playing;

    public bool IsRecording => IsCurrent && Player.Recording;

    /// <summary>Edits wait while a take records (the take writes the timeline as it goes).</summary>
    public bool CanEdit => HasSong && !IsRecording;

    public double Playhead => IsCurrent ? Player.PlayheadTime : 0;

    /// <summary>Where new cues go: at the playhead (on the frame grid).</summary>
    public double InsertionTime => ItemId is { } id ? Player.InsertionTime(id) : 0;

    public void PlayOrPause() => main.TimelinePlayPauseCommand.Execute(null);

    public void GoToStart() => main.TimelineRewindCommand.Execute(null);

    public void RecordOrStop() => main.TimelineRecordCommand.Execute(null);

    /// <summary>Throws the take away (Esc while recording).</summary>
    public void DiscardTake() => Player.DiscardTake();

    public void Seek(double seconds)
    {
        if (!HasSong) return;
        main.SeekTimeline(Math.Max(0, seconds));
        PlayheadMoved?.Invoke();
    }

    /// <summary>The bar's readout: the playhead as timecode from the song's start ("01:00:12:04").</summary>
    public string PlayheadTimecode
    {
        get
        {
            var start = TimecodeStart ?? Timeline?.TimecodeStart ?? 0;
            return PresentationTimelines.Timecode(start + (int)Math.Floor(Playhead * PresentationTimelines.FramesPerSecond));
        }
    }

    /// <summary>What the main window's timeline bar says (next slide in…, recording…, following timecode).</summary>
    public string Status => main.TimelineStatus;

    // MARK: View: zoom, rows, scroll

    /// <summary>The zoom picked (points per second); null = fit the song's first 45 seconds to the view.</summary>
    public double? PointsPerSecond { get; private set; }

    /// <summary>How wide the lanes are on screen (the view tells us).</summary>
    public double Viewport { get; private set; } = 800;

    /// <summary>How far the lanes are scrolled, in points.</summary>
    public double ScrollX { get; private set; }

    /// <summary>The music and track rows' height (the other lanes stay 26 points).</summary>
    public double RowHeight { get; private set; } = TimelineEditorLayout.DefaultRowHeight;

    /// <summary>A track row's own size (dragged at its bottom edge), as a multiple of the row height.</summary>
    private readonly Dictionary<string, double> trackScale = new(StringComparer.Ordinal);

    public double DisplayLength => TimelineEditing.DisplayLength(Timeline);

    public double Zoom => PointsPerSecond ?? TimelineEditing.StartingZoom(DisplayLength, Viewport);

    public TimelineEditorLayout Layout { get; private set; } = new([], [], TimelineEditing.Map(30, 12));

    /// <summary>The lanes' full width at this zoom.</summary>
    public double ContentWidth => Layout.Map.Width;

    public void SetViewport(double width)
    {
        if (width <= 0 || Math.Abs(width - Viewport) < 0.5) return;
        Viewport = width;
        Relayout();
    }

    /// <summary>Zooms, keeping the time at <paramref name="anchorX"/> (a view x; default the middle) where it is.</summary>
    public void SetZoom(double pointsPerSecond, double? anchorX = null)
    {
        var at = anchorX ?? Viewport / 2;
        var time = (ScrollX + at) / Zoom;
        PointsPerSecond = TimelineZoom.Clamped(pointsPerSecond, TimelineZoom.SongRange);
        Relayout();
        ScrollTo(time * Zoom - at);
    }

    public void ZoomIn() => SetZoom(TimelineZoom.ZoomedIn(Zoom, TimelineZoom.SongRange));

    public void ZoomOut() => SetZoom(TimelineZoom.ZoomedOut(Zoom, TimelineZoom.SongRange));

    /// <summary>Fit: the whole song across the view.</summary>
    public void ZoomToFit()
    {
        PointsPerSecond = TimelineZoom.Fit(DisplayLength, Viewport, reserve: 0, TimelineZoom.SongRange) ?? PointsPerSecond;
        Relayout();
        ScrollTo(0);
    }

    public void SetRowHeight(double height)
    {
        RowHeight = Math.Clamp(Math.Round(height), TimelineEditorLayout.RowHeights.Lower, TimelineEditorLayout.RowHeights.Upper);
        Relayout();
    }

    public void ScrollTo(double x)
    {
        var clamped = Math.Clamp(x, 0, Math.Max(ContentWidth - Viewport, 0));
        if (Math.Abs(clamped - ScrollX) < 0.01) return;
        ScrollX = clamped;
        Redraw?.Invoke();
        PlayheadMoved?.Invoke();
    }

    /// <summary>A track row's height (its own scale times the row height).</summary>
    public double TrackHeight(string channelId) => TimelineEditorLayout.TrackHeight(RowHeight, trackScale.TryGetValue(channelId, out var s) ? s : null);

    /// <summary>A track row was dragged at its bottom edge (null puts it back to match the others).</summary>
    public void SetTrackScale(string channelId, double? scale)
    {
        if (scale is { } value) trackScale[channelId] = value;
        else trackScale.Remove(channelId);
        Relayout();
    }

    // MARK: Which rows show

    private readonly Dictionary<string, HashSet<TimelineLane>> addedLanes = new(StringComparer.Ordinal);
    private readonly HashSet<string> addedTracks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> addedTrackRows = new(StringComparer.Ordinal);

    private HashSet<TimelineLane> AddedLanes => ItemId is { } id ? addedLanes.GetValueOrDefault(id) ?? [] : [];

    private HashSet<string> AddedTrackRows => ItemId is { } id ? addedTrackRows.GetValueOrDefault(id) ?? [] : [];

    public IReadOnlyList<TimelineLane> VisibleLanes => TimelineEditing.VisibleLanes(Cues, AddedLanes);

    /// <summary>Lanes that could be added (the "+ Add a layer" menu).</summary>
    public IReadOnlyList<TimelineLane> HiddenLanes => TimelineLanes.All.Where(l => !VisibleLanes.Contains(l)).ToList();

    public void AddLane(TimelineLane lane)
    {
        if (ItemId is not { } id) return;
        if (!addedLanes.TryGetValue(id, out var lanes)) addedLanes[id] = lanes = [];
        lanes.Add(lane);
        Relayout();
    }

    /// <summary>The church has tracks, the song has stems, or tracks were added to it: the tracks rows show.</summary>
    public bool ShowsTracks =>
        TimelineTracks.HasTracks(main.TrackChannels) || TimelineTracks.Stems(Cues).Count > 0 || (ItemId is { } id && addedTracks.Contains(id));

    public void AddTracks()
    {
        if (ItemId is { } id) addedTracks.Add(id);
        Relayout();
    }

    public bool TracksExpanded
    {
        get => tracksExpanded;
        set
        {
            tracksExpanded = value;
            Relayout();
        }
    }

    /// <summary>The track rows this song shows: channels it has stems on or that were added to it.</summary>
    public IReadOnlyList<TrackChannel> TrackRows => TimelineTracks.Rows(main.TrackChannels, Cues, AddedTrackRows);

    /// <summary>The church's tracks this song doesn't show yet ("Add an Existing Track").</summary>
    public IReadOnlyList<TrackChannel> AddableTracks => main.TrackChannels.Where(c => !TrackRows.Any(r => r.Id == c.Id)).ToList();

    public void AddTrackRow(string channelId)
    {
        if (ItemId is not { } id) return;
        addedTracks.Add(id);
        if (!addedTrackRows.TryGetValue(id, out var rows)) addedTrackRows[id] = rows = [];
        rows.Add(channelId);
        tracksExpanded = true;
        Relayout();
    }

    /// <summary>"Remove from This Song": a track row with no stems, added by hand.</summary>
    public bool CanRemoveTrackRow(string channelId) =>
        AddedTrackRows.Contains(channelId) && !TimelineTracks.Stems(Cues).Any(s => s.TrackChannelId() == channelId);

    public void RemoveTrackRow(string channelId)
    {
        if (ItemId is { } id && addedTrackRows.TryGetValue(id, out var rows)) rows.Remove(channelId);
        Relayout();
    }

    /// <summary>The stems whose sound the folded bar's waveform shows: the music ones (not click and guide).</summary>
    public IReadOnlyList<TimelineCue> MusicStems(IReadOnlyList<TimelineCue> stems)
    {
        var cueOnly = main.TrackChannels.Where(c => c.TrackRole() != TimelineTracks.Role.Music).Select(c => c.Id).ToHashSet();
        var music = stems.Where(s => !cueOnly.Contains(s.TrackChannelId() ?? "")).ToList();
        return music.Count == 0 ? stems : music;
    }

    // MARK: Refreshing

    /// <summary>Reads the song again and lays the lanes out.</summary>
    public void Refresh()
    {
        if (disposed) return;
        var target = main.TimelineEditorTarget;
        var songChanged = target?.ItemId != ItemId || target?.Presentation.Id != Song?.Id || target?.ArrangementId != ArrangementId;
        ItemId = target?.ItemId;
        ArrangementId = target?.ArrangementId;
        Song = target is { } t ? main.Library.Get<Presentation>(t.Presentation.Id) ?? t.Presentation : null;
        if (songChanged) ResetForSong();
        Timeline = Song is null ? null : PresentationTimelines.Timeline(ArrangementId, Song);
        Blocks = Song is null ? [] : PresentationTimelines.Blocks(Song, ArrangementId, ((ITimelineHost)main).EmptyRepeatsOnce);
        Selection.RemoveWhere(id => !id.StartsWith(TimelineTracks.FoldedPrefix, StringComparison.Ordinal) && !Cues.Any(c => c.Id == id));
        seenRevision = main.TimelineRevision;
        Relayout();
        LayOutFromMapIfChanged();
        wasPlaying = IsPlaying;
        wasRecording = IsRecording;
        StateChanged?.Invoke();
    }

    private void ResetForSong()
    {
        Selection.Clear();
        Range = null;
        cardDrag = null;
        fadeDrag = null;
        LiveFade = null;
        transitionDrag = null;
        LiveTransition = null;
        rangeDrag = null;
        scrubbing = false;
        PointsPerSecond = null;
        ScrollX = 0;
        mapKey = "";
    }

    /// <summary>Lays the rows and cards out again for the cues, lanes and zoom.</summary>
    public void Relayout()
    {
        var rows = TimelineEditorLayout.RowsFor(
            VisibleLanes, RowHeight, ShowsTracks, TracksExpanded, TrackRows.Select(c => c.Id).ToList(), TrackHeight);
        Layout = TimelineEditorLayout.Build(rows, Cues, TimelineEditing.Map(DisplayLength, Zoom), TracksExpanded, MusicStems);
        ScrollX = Math.Clamp(ScrollX, 0, Math.Max(ContentWidth - Viewport, 0));
        LayoutChanged?.Invoke();
    }

    // A song with a section map lays its timeline out for the arrangement when the map or
    // the arrangement changes (the Mac's .task(id: mapKey)). The player saves it only if it changed.
    private void LayOutFromMapIfChanged()
    {
        if (Song?.SongMap is not { } map)
        {
            mapKey = "";
            return;
        }
        var key = string.Join(",", Blocks.Select(b => b.SectionId ?? "-")) + "|"
            + string.Join(",", map.Sections.Select(s => $"{s.Id}@{s.Start}>{s.SectionId}"));
        if (key == mapKey) return;
        mapKey = key;
        if (!IsRecording) Player.LayOutFromMap(Song.Id, ArrangementId);
    }

    private void OnTimelineBarUpdated()
    {
        if (disposed) return;
        var target = main.TimelineEditorTarget;
        if (target?.ItemId != ItemId || target?.Presentation.Id != Song?.Id || target?.ArrangementId != ArrangementId
            || main.TimelineRevision != seenRevision)
        {
            Refresh();
            return;
        }
        if (IsPlaying && cardDrag is null && rangeDrag is null)
        {
            // Keep the playhead in view while it plays (scroll only when it runs off).
            var x = Layout.Map.XOfTime(Playhead);
            if (TimelineFollow.Scroll(x, new ClosedRange(ScrollX, ScrollX + Viewport), ContentWidth) is { } scroll) ScrollTo(scroll);
        }
        PlayheadMoved?.Invoke();
        if (IsPlaying != wasPlaying || IsRecording != wasRecording)
        {
            wasPlaying = IsPlaying;
            wasRecording = IsRecording;
            StateChanged?.Invoke();
        }
    }

    private void OnLibraryChanged(Type type, string id)
    {
        if (type == typeof(Presentation) && id != Song?.Id) return;
        if (type != typeof(Presentation) && type != typeof(AudioItem) && type != typeof(TrackSet) && type != typeof(TrackChannelList)
            && type != typeof(MediaItem) && type != typeof(Overlay) && type != typeof(AlertPreset) && type != typeof(Service))
            return;
        // Saves can come from a background thread (an import), and several at once: refresh once, on the UI thread.
        if (refreshQueued) return;
        refreshQueued = true;
        void Run()
        {
            refreshQueued = false;
            Refresh();
        }
        if (ui is not null) ui.Post(_ => Run(), null);
        else Run();
    }

    // MARK: Editing

    /// <summary>
    /// Every edit goes through here: the song as it was goes into the undo history, the change
    /// is made on a copy of its timeline (a new one if it has none yet), the cues are put in
    /// time order and it is saved. Port of PresentationTimelineContext.edit.
    /// </summary>
    public bool EditTimeline(string label, Func<PresentationTimeline, PresentationTimeline> change)
    {
        if (!HasSong) return false;
        if (IsRecording)
        {
            Message?.Invoke("Stop recording first: the take is writing this timeline as it plays.");
            return false;
        }
        if (main.Library.Get<Presentation>(Song!.Id) is not { } deck) return false;
        PresentationTimeline changed;
        try
        {
            var timeline = PresentationTimelines.Timeline(ArrangementId, deck)?.Copy() ?? NewTimeline(deck);
            changed = change(timeline);
            changed.Cues = changed.Cues.OrderBy(c => c.At).ToList();
        }
        catch (Exception error)
        {
            // An edit that fails changes nothing (the song is saved only after it succeeds).
            Log.Error($"Timeline edit \"{label}\" failed", error);
            Message?.Invoke($"Couldn't {label.ToLowerInvariant()}: {error.Message}");
            return false;
        }
        // The song as it was goes into the history before the save replaces its timelines.
        history.Record(label, deck);
        Player.Save(changed, deck.Id, label);
        Refresh();
        return true;
    }

    /// <summary>An edit of the cues alone.</summary>
    public bool Edit(string label, Func<List<TimelineCue>, List<TimelineCue>> change) =>
        EditTimeline(label, timeline =>
        {
            timeline.Cues = change([.. timeline.Cues]);
            return timeline;
        });

    private PresentationTimeline NewTimeline(Presentation deck) => new()
    {
        Id = TimelineEditing.NewId(),
        ArrangementId = string.IsNullOrEmpty(ArrangementId) ? null : ArrangementId,
        TimecodeEnabled = false,
        TimecodeStart = SuggestedStart(0),
        Cues = [],
    };

    // MARK: Undo

    public bool CanUndo => Song is { } song && history.CanUndo(song.Id);

    public bool CanRedo => Song is { } song && history.CanRedo(song.Id);

    public string UndoTitle => Song is { } song && history.UndoLabel(song.Id) is { } label ? $"Undo {label}" : "Undo";

    public string RedoTitle => Song is { } song && history.RedoLabel(song.Id) is { } label ? $"Redo {label}" : "Redo";

    public void Undo() => Restore(history.Undo);

    public void Redo() => Restore(history.Redo);

    private void Restore(Func<Presentation, TimelineEditHistory.Snapshot?> step)
    {
        if (Song is null || IsRecording || main.Library.Get<Presentation>(Song.Id) is not { } deck) return;
        if (step(deck) is not { } snapshot) return;
        TimelineEditHistory.Apply(snapshot, deck);
        mapKey = "";
        main.SaveTimelineSong(deck);
        Selection.Clear();
        Range = null;
        Refresh();
    }

    // MARK: Selection and range

    /// <summary>The selected cues' ids (the folded stems bar's id is "folded:…").</summary>
    public HashSet<string> Selection { get; private set; } = [];

    /// <summary>A stretch of time dragged across the lanes (copy it, or ripple-delete it).</summary>
    public ClosedRange? Range { get; private set; }

    public void ClearRange()
    {
        Range = null;
        Redraw?.Invoke();
        StateChanged?.Invoke();
    }

    public void Select(IEnumerable<string> ids)
    {
        Selection = [.. ids];
        Range = null;
        Redraw?.Invoke();
        StateChanged?.Invoke();
    }

    /// <summary>The selected cues (real ones: the folded bar stands for its stems).</summary>
    public IReadOnlyList<TimelineCue> SelectedCues => Cues.Where(c => Selection.Contains(c.Id)).ToList();

    /// <summary>The cue the keyboard and the toolbar act on.</summary>
    public TimelineCue? Picked => TimelineEditing.Picked(Cues, Selection);

    // MARK: Pointer (the lanes view and the self-test both drive these)

    private TimelineCardDrag? cardDrag;
    private bool dragToggles;
    private (ClipFadeGeometry.Handle Handle, TimelineCue Clip, double PressX, bool Moved)? fadeDrag;
    private (SongTransitions.Plan Plan, double PressX, double SavedX)? transitionDrag;
    private (double PressX, double Start, bool Engaged)? rangeDrag;
    private bool scrubbing;

    /// <summary>The card drag in progress (the view draws the dragged cards where it says).</summary>
    public TimelineCardDrag? CardDrag => cardDrag;

    /// <summary>A fade being dragged: the view draws it in place of the saved one.</summary>
    public (string CueId, bool Out, ClipFade Fade)? LiveFade { get; private set; }

    /// <summary>A transition length being dragged.</summary>
    public double? LiveTransition { get; private set; }

    /// <summary>True while any press is held on the lanes (the view keeps the mouse captured).</summary>
    public bool IsPressing => cardDrag is not null || fadeDrag is not null || transitionDrag is not null || rangeDrag is not null || scrubbing;

    /// <summary>
    /// A press on the lanes at a content point (x from the song's start, y from the top of the
    /// ruler): the ruler moves the playhead, a fade or transition handle starts that drag, a
    /// card starts a card press, empty lane starts a range. Returns false when it hit nothing.
    /// </summary>
    public bool PointerPressed(double x, double y, bool toggle = false, int clicks = 1)
    {
        if (!HasSong) return false;
        if (TimelineEditorLayout.InRuler(y))
        {
            scrubbing = true;
            if (!IsPlaying) Seek(Layout.TimeAt(x));
            return true;
        }
        if (CanEdit && TransitionHandleAt(x, y) is { } plan)
        {
            transitionDrag = (plan, x, Layout.Map.XOfTime(Math.Min(plan.NextAt, plan.FadeFrom ?? plan.NextAt)));
            return true;
        }
        if (CanEdit && FadeHandleAt(x, y) is { } handle && Cues.FirstOrDefault(c => c.Id == handle.CueId) is { } clip)
        {
            fadeDrag = (handle, clip, x, false);
            return true;
        }
        if (Layout.CardAt(x, y) is { } hit)
        {
            if (clicks >= 2)
            {
                DoubleClicked(hit.Card.Cue);
                return true;
            }
            cardDrag = new TimelineCardDrag(hit, x);
            dragToggles = toggle;
            return true;
        }
        if (Layout.RowAt(y) is not null)
        {
            rangeDrag = (x, Layout.TimeAt(x), false);
            return true;
        }
        return false;
    }

    /// <summary>The pointer moved with a press held. <paramref name="free"/> (Alt held) drags without snapping.</summary>
    public void PointerMoved(double x, double y, bool free = false)
    {
        if (scrubbing)
        {
            if (!IsPlaying) Seek(Layout.TimeAt(x));
            return;
        }
        if (cardDrag is { } drag)
        {
            if (!CanEdit) return;
            if (drag.MovedTo(x, free ? null : SnapFor(drag.Card)))
            {
                Selection = TimelineEditing.Engaged(Selection, drag.Card.Cue.Id);
                StateChanged?.Invoke();
            }
            Redraw?.Invoke();
            return;
        }
        if (fadeDrag is { } fade)
        {
            var dragged = ClipFadeGeometry.Dragged(fade.Handle, (x - fade.PressX) / Zoom, fade.Clip);
            LiveFade = (fade.Handle.CueId, fade.Handle.Out, dragged);
            fadeDrag = fade with { Moved = true };
            Redraw?.Invoke();
            return;
        }
        if (transitionDrag is { } transition)
        {
            var time = Layout.TimeAt(transition.SavedX + (x - transition.PressX));
            LiveTransition = TimelineEditing.ClampedTransition(transition.Plan.End - time, transition.Plan.End);
            Redraw?.Invoke();
            return;
        }
        if (rangeDrag is { } range && Math.Abs(x - range.PressX) > TimelineBlockEdit.EngageDistance)
        {
            Range = TimelineEditing.Range(range.Start, Layout.TimeAt(x));
            Selection.Clear();
            rangeDrag = range with { Engaged = true };
            Redraw?.Invoke();
            StateChanged?.Invoke();
        }
    }

    /// <summary>The press let go: a card click selects, a card drag moves or trims, a fade or transition drag is saved.</summary>
    public void PointerReleased(double x, double y, bool free = false)
    {
        if (scrubbing)
        {
            scrubbing = false;
            if (IsPlaying) Seek(Layout.TimeAt(x));
            return;
        }
        if (cardDrag is { } drag)
        {
            cardDrag = null;
            var cue = drag.Card.Cue;
            var (mode, dx) = drag.Released(x, free || !CanEdit ? null : SnapFor(drag.Card));
            Selection = TimelineEditing.Released(Selection, cue.Id, mode, dragToggles);
            Range = null;
            if (mode is { } moved && dx != 0 && CanEdit)
            {
                Move(cue, moved, dx / Zoom, "Move Cue", Layout.Map.Locate(drag.Card.X + drag.Card.Width)?.Time);
                return;
            }
            Redraw?.Invoke();
            StateChanged?.Invoke();
            return;
        }
        if (fadeDrag is { } fade)
        {
            fadeDrag = null;
            var live = LiveFade;
            LiveFade = null;
            if (fade.Moved && live is { } set) Edit("Change Fade", cues => TimelineEditing.WithFadeSet(cues, set.CueId, set.Out, set.Fade));
            else Redraw?.Invoke();
            return;
        }
        if (transitionDrag is not null)
        {
            transitionDrag = null;
            var seconds = LiveTransition;
            LiveTransition = null;
            if (seconds is { } length) SetTransitionSeconds(length);
            Redraw?.Invoke();
            return;
        }
        if (rangeDrag is { } range)
        {
            rangeDrag = null;
            if (!range.Engaged)
            {
                Range = null;
                Selection.Clear();
            }
            Redraw?.Invoke();
            StateChanged?.Invoke();
        }
    }

    /// <summary>The press was lost (the window lost the mouse): drop it without changing anything.</summary>
    public void PointerCancelled()
    {
        cardDrag = null;
        fadeDrag = null;
        LiveFade = null;
        transitionDrag = null;
        LiveTransition = null;
        rangeDrag = null;
        scrubbing = false;
        Redraw?.Invoke();
    }

    /// <summary>A card drag's snapping: onto the other cues' edges and the playhead (the Mac's snap closure).</summary>
    private Func<TimelineBlockEdit.Mode, double, double> SnapFor(TimelineCard card) => (mode, dx) =>
        TimelineEditing.SnappedDelta(dx, card.Cue, Cues, mode, Selection, Layout.Map, card.X, card.Width, IsCurrent ? InsertionTime : null);

    /// <summary>Whether a card is drawn moved by the drag in progress (the dragged card and, for a move, the others selected with it).</summary>
    public bool MovesWithDrag(TimelineCard card)
    {
        if (cardDrag is not { Mode: TimelineBlockEdit.Mode.Move } drag) return false;
        if (!Selection.Contains(drag.Card.Cue.Id)) return false;
        if (Selection.Contains(card.Cue.Id)) return true;
        // Stems that start with a dragged stem move with it.
        return TimelineEditing.Together(drag.Card.Cue, Cues, Selection).Contains(card.Cue.Id);
    }

    private void DoubleClicked(TimelineCue cue)
    {
        if (cue.Kind() == TimelineLane.Actions && CanEdit) EditActionsRequested?.Invoke(cue);
        else Seek(cue.At);
    }

    /// <summary>The window opens the actions editor for an actions cue (a double-click, or Edit Actions…).</summary>
    public event Action<TimelineCue>? EditActionsRequested;

    // MARK: Moving, nudging, deleting

    /// <summary>Moves (or stretches or trims) a cue and what moves with it. Port of the Mac's move(_:mode:by:label:openEnd:).</summary>
    public void Move(TimelineCue cue, TimelineBlockEdit.Mode mode, double seconds, string label = "Move Cue", double? openEnd = null)
    {
        var selection = Selection;
        Edit(label, cues => TimelineEditing.Moved(cues, cue, mode, seconds, selection, openEnd));
    }

    /// <summary>A frame or a second earlier or later.</summary>
    public void Nudge(double seconds)
    {
        if (Picked is { } cue) Move(cue, TimelineBlockEdit.Mode.Move, seconds, "Nudge Cue");
    }

    /// <summary>Types a cue's time (timecode from the song's start).</summary>
    public void SetCueTime(TimelineCue cue, double seconds) => Move(cue, TimelineBlockEdit.Mode.Move, seconds - cue.At, "Set Cue Time");

    public void Delete(TimelineCue cue)
    {
        var selection = Selection;
        if (Edit(TimelineEditing.DeleteLabel(cue, selection), cues => TimelineEditing.Deleted(cues, cue, selection))) Select([]);
    }

    /// <summary>Delete with nothing named: the picked cue (or nothing when a range is shown).</summary>
    public void DeleteSelected()
    {
        if (Range is null && Picked is { } cue) Delete(cue);
    }

    public void RemoveStems(TimelineCue stem)
    {
        if (Edit("Remove Stems", cues => TimelineEditing.RemovingStems(cues, stem))) Select([]);
    }

    // MARK: Split, copy, paste, ripple delete

    public bool HasClipboard => clipboard.Count > 0;

    /// <summary>Ctrl+B: splits the selection (or every clip) at the playhead.</summary>
    public void SplitAtPlayhead() => Split(TimelineEditing.SplitTargets(Cues, Selection));

    public void Split(IReadOnlySet<string> ids)
    {
        var at = InsertionTime;
        Edit("Split", cues => TimelineClips.Splitting(ids, at, cues));
    }

    /// <summary>Ctrl+C: the range, or the selection (or the cues named).</summary>
    public void Copy(IReadOnlySet<string>? ids = null)
    {
        var copied = TimelineEditing.Copied(Cues, Selection, Range, ids);
        if (copied.Count == 0) return;
        clipboard = copied;
        StateChanged?.Invoke();
    }

    /// <summary>Ctrl+V: the clipboard at the playhead, each with a new id; the pasted cues are selected.</summary>
    public void Paste()
    {
        if (clipboard.Count == 0) return;
        var pasted = TimelineClips.Pasted(clipboard, InsertionTime);
        if (Edit("Paste", cues => [.. cues, .. pasted])) Select(pasted.Select(c => c.Id));
    }

    /// <summary>Shift+Delete: takes the range out of every lane; everything after it moves up.</summary>
    public void RippleDelete()
    {
        if (Range is not { } range) return;
        if (EditTimeline("Ripple Delete", timeline => TimelineClips.RippleDeleted(timeline, range))) Select([]);
    }

    // MARK: Fades and crossfades

    /// <summary>A music item's usable length (its out point less its in point): the spare audio a crossfade can use.</summary>
    public double? WindowLength(string audioId) =>
        main.Library.Get<AudioItem>(audioId) is { } item ? ((item.OutPoint ?? item.DurationSeconds) ?? 0) - (item.InPoint ?? 0) : null;

    public void SetFade(TimelineCue clip, bool @out, TimelineClips.Shape? shape) =>
        Edit(shape is null ? "Remove Fade" : "Fade", cues => TimelineEditing.WithFade(cues, clip.Id, @out, shape));

    public void SetEdgeFades(TimelineCue clip, bool on) => Edit("Edge Fades", cues => TimelineEditing.WithEdgeFades(cues, clip.Id, on));

    public void Crossfade(TimelineCue a, TimelineCue b, TimelineClips.Shape shape) =>
        Edit("Crossfade", cues => TimelineEditing.Crossfading(cues, a, b, shape) ?? cues);

    public void CrossfadeAtCut(TimelineCue left, TimelineCue right, TimelineClips.Shape shape) =>
        Edit("Crossfade", cues => TimelineEditing.CrossfadingAtCut(cues, left, right, shape, WindowLength) ?? cues);

    public void PlaceCrossfade(TimelineCue a, TimelineCue b, TimelineClips.Placement placement) =>
        Edit("Crossfade", cues => TimelineEditing.Replacing(cues, a, b, placement, WindowLength) ?? cues);

    public void RemoveCrossfade(TimelineCue a, TimelineCue b) =>
        Edit("Remove Crossfade", cues => TimelineEditing.Uncrossfading(cues, a, b) ?? cues);

    /// <summary>The crossfade a pair would get at its cut (for the menu's label), or null when there's no spare audio.</summary>
    public (TimelineCue Left, TimelineCue Right, TimelineClips.Placement Placement)? CrossfadeAtCutPreview(
        TimelineCue left, TimelineCue right, TimelineClips.Placement? placement = null) =>
        TimelineEditing.Placed(left, right, placement, Cues, WindowLength);

    /// <summary>The fade handles on a row's clips (none on a clip being dragged).</summary>
    public List<ClipFadeGeometry.Handle> FadeHandles(TimelineRow row)
    {
        var handles = new List<ClipFadeGeometry.Handle>();
        if (!row.HoldsAudio) return handles;
        foreach (var card in Layout.Cards.Where(c => c.Row == row && TimelineClips.IsClip(c.Cue) && !TimelineEditing.IsFolded(c.Cue)))
        {
            if (cardDrag?.Card.Cue.Id == card.Cue.Id && cardDrag.IsDragging) continue;
            handles.AddRange(ClipFadeGeometry.Handles(card.Cue, Cues, LiveFadeFor));
        }
        return handles;
    }

    private ClipFade? LiveFadeFor(string id, bool @out) => LiveFade is { } live && live.CueId == id && live.Out == @out ? live.Fade : null;

    private ClipFadeGeometry.Handle? FadeHandleAt(double x, double y)
    {
        if (Layout.RowAt(y) is not { HoldsAudio: true } row) return null;
        return ClipFadeGeometry.HandleAt(FadeHandles(row), x, y, Layout.Map.XOfTime, row.Top, row.Height);
    }

    // MARK: Adding cues

    /// <summary>Adds a cue (a slide cue that lands on another steps clear of it) and selects it.</summary>
    public void Add(TimelineCue cue, string label = "Add to Timeline")
    {
        if (Edit(label, cues => TimelineEditing.Adding(cues, cue))) Select([cue.Id]);
    }

    /// <summary>The song's slides in played order, for the Slides lane's + menu: "3 · Chorus — Amazing grace…".</summary>
    public IReadOnlyList<(int Occurrence, string Label)> SlideChoices()
    {
        if (Song is null) return [];
        var choices = new List<(int, string)>();
        var index = 0;
        foreach (var block in Blocks)
        {
            var section = Song.Sections?.FirstOrDefault(s => s.Id == block.SectionId)?.Name;
            foreach (var slideId in block.SlideIds)
            {
                var slide = Song.Slides.FirstOrDefault(s => s.Id == slideId);
                var words = slide is null ? "" : PresentationTimelines.LeadingWords(Core.Stage.ConfidenceSceneBuilder.LyricText(slide), 180);
                var label = $"{index + 1}" + (section is null ? "" : $" · {section}") + (words.Length > 0 ? $" — {words}" : "");
                choices.Add((index, label));
                index++;
            }
        }
        return choices;
    }

    public void AddSlide(int occurrence)
    {
        if (TimelineEditing.SlideCue(occurrence, Blocks, InsertionTime) is { } cue) Add(cue);
    }

    public void AddMusic(AudioItem item) => Add(TimelineEditing.MusicCue(item, InsertionTime));

    public void AddMedia(MediaItem media) => Add(TimelineEditing.MediaCue(media, InsertionTime));

    public void AddOverlay(Overlay overlay) => Add(TimelineEditing.OverlayCue(overlay.Id, InsertionTime));

    public void AddAlert(AlertPreset alert) => Add(TimelineEditing.AlertCue(alert.Id, InsertionTime));

    public void AddLiveInput(string inputId) => Add(TimelineEditing.LiveInputCue(inputId, InsertionTime));

    public void AddActions(IEnumerable<SlideAction> actions) => Add(TimelineEditing.ActionsCue(actions, InsertionTime), "Add Action");

    /// <summary>Saves an actions cue's actions (from the actions editor).</summary>
    public void SetActions(string cueId, List<SlideAction> actions) =>
        Edit("Timeline Actions", cues => TimelineEditing.Changing(cues, cueId, cue =>
        {
            cue.Actions = actions;
            return cue;
        }));

    /// <summary>Music for the Music lane's + menu: the library's music, not the stems of track sets.</summary>
    public IReadOnlyList<AudioItem> MusicChoices()
    {
        var stems = TrackSets.StemIds(main.Library.All<TrackSet>());
        return main.Library.All<AudioItem>().Where(a => !stems.Contains(a.Id)).ToList();
    }

    /// <summary>Media for a media lane's + menu: the items that fire to that layer.</summary>
    public IReadOnlyList<MediaItem> MediaChoices(TimelineLane lane) =>
        main.Library.All<MediaItem>().Where(m => TimelineEditing.MediaLane(m) == lane).ToList();

    // MARK: Labels and colours

    private TimelineNames? names;

    /// <summary>The library's names for cue labels.</summary>
    public TimelineNames Names => names ??= new TimelineNames(
        id => main.Library.Get<Overlay>(id)?.Name,
        id => main.Library.Get<AlertPreset>(id)?.Name,
        id => main.Inputs.EntryFor(id)?.Name,
        id => main.Library.Get<AudioItem>(id)?.Name,
        id => main.Library.Get<MediaItem>(id)?.Name);

    public string Label(TimelineCue cue) => Song is null ? "" : TimelineLabels.Label(cue, Song, Blocks, Names);

    public string SlideWords(TimelineCue cue, double room) => Song is null ? "" : TimelineLabels.SlideWords(cue, Song, Blocks, room);

    /// <summary>A slide cue whose slide is no longer in the arrangement (it won't fire; drawn red).</summary>
    public bool IsOrphan(TimelineCue cue) => cue.Kind() == TimelineLane.Slide && PresentationTimelines.Occurrence(cue, Blocks) is null;

    /// <summary>A section's colour ("#RRGGBBAA"), for slide cues and the sections bar.</summary>
    public string? SectionColor(string? sectionId)
    {
        if (Song?.Sections?.FirstOrDefault(s => s.Id == sectionId) is not { } section) return null;
        return Core.Library.StarterContent.GroupColor(section.Name, section.ColorHex);
    }

    public string? SectionName(string? sectionId) => Song?.Sections?.FirstOrDefault(s => s.Id == sectionId)?.Name;

    /// <summary>The song's sections laid along the timeline (from each section's first slide cue to the next).</summary>
    public IReadOnlyList<PresentationTimelines.SectionSpan> SectionSpans =>
        Timeline is null ? [] : PresentationTimelines.SectionSpans(Timeline.Cues, Blocks, DisplayLength);

    /// <summary>A track channel, for a stem card's colour.</summary>
    public TrackChannel? Channel(string? id) => main.TrackChannels.FirstOrDefault(c => c.Id == id);

    /// <summary>Where a clip's waveform comes from: each cue's file, placed and trimmed as it plays.</summary>
    public IReadOnlyList<TimelineWaveformStore.Source> WaveSources(IEnumerable<TimelineCue> group)
    {
        var sources = new List<TimelineWaveformStore.Source>();
        foreach (var cue in group)
        {
            if (cue.MediaId is not { } id || main.Library.Get<AudioItem>(id) is not { } item) continue;
            // The cue's own in point counts too, so a trimmed or split clip shows the audio it plays.
            sources.Add(new TimelineWaveformStore.Source(
                item.FileHash, AudioImporter.FilePath(main.Library, item), cue.At, (item.InPoint ?? 0) + (cue.InPoint ?? 0), cue.Duration));
        }
        return sources;
    }

    // MARK: Keys

    /// <summary>
    /// A key on the lanes (from the window when no text box has the focus). Returns true when
    /// it did something. Ctrl+Z and Ctrl+Y undo and redo; Space plays.
    /// </summary>
    public bool Key(string key, bool control, bool shift, bool alt)
    {
        if (!HasSong) return false;
        if (control && !alt && key == "Z")
        {
            if (shift) Redo();
            else Undo();
            return true;
        }
        if (control && !alt && !shift && key == "Y")
        {
            Redo();
            return true;
        }
        if (!control && !alt && !shift && key == "Space")
        {
            PlayOrPause();
            return true;
        }
        if (!control && !alt && !shift && key == "Home")
        {
            Seek(0);
            return true;
        }
        if (TimelineEditing.VerbFor(key, control, shift, alt) is not { } verb) return false;
        if (verb is TimelineEditing.KeyVerb.Discard)
        {
            if (!IsRecording) return false;
            DiscardTake();
            return true;
        }
        if (!CanEdit) return false;
        switch (verb)
        {
            case TimelineEditing.KeyVerb.Split:
                SplitAtPlayhead();
                break;
            case TimelineEditing.KeyVerb.Copy:
                Copy();
                break;
            case TimelineEditing.KeyVerb.Paste:
                Paste();
                break;
            case TimelineEditing.KeyVerb.Delete:
                if (Range is not null || Picked is null) return false;
                DeleteSelected();
                break;
            case TimelineEditing.KeyVerb.RippleDelete:
                if (Range is null) return false;
                RippleDelete();
                break;
            case TimelineEditing.KeyVerb.Nudge nudge:
                if (Picked is null) return false;
                Nudge(nudge.Seconds);
                break;
        }
        return true;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        main.TimelineBarUpdated -= OnTimelineBarUpdated;
        main.Library.Changed -= OnLibraryChanged;
        Waveforms.Dispose();
        imports?.Dispose();
    }
}
