using MxU.Slides.Audio.Library;
using MxU.Slides.Audio.Timeline;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Threading;

namespace MxU.Slides.Windows.ViewModels;

// The timeline editor's song-level settings: the song's transition to the next service item,
// its timecode start, its section map, and its stems (multitrack) with the church's track list.
public sealed partial class TimelineEditorViewModel
{
    // MARK: The transition to the next item

    /// <summary>How this song hands over to the next service item (null = stop at the end).</summary>
    public SongTransition? Transition => ServiceItem?.Transition;

    /// <summary>
    /// The handover as the lanes draw it, and the next item's name: null when the song stops
    /// at its end, has no timeline, or wasn't opened from a service. Port of
    /// PresentationTimelineContext.transition.
    /// </summary>
    public (SongTransitions.Plan Plan, string? NextName)? TransitionPlan
    {
        get
        {
            if (Timeline is not { } timeline || ServiceItem is not { } item || SongTransitions.KindOf(item.Transition) == SongTransitions.Kind.Stop)
                return null;
            var host = (ITimelineHost)main;
            var next = TimelinePlayer.NextFireable(host.FireableItems(), item.Id);
            (PresentationTimeline Timeline, IReadOnlyList<TimelinePlayedBlock> Blocks)? nextSong = null;
            if (next is { ItemKind: ServiceItemKind.Presentation } && main.Library.Get<Presentation>(next.RefId) is { } deck
                && PresentationTimelines.Timeline(next.ArrangementId, deck) is { } nextTimeline)
                nextSong = (nextTimeline, PresentationTimelines.Blocks(deck, next.ArrangementId, host.EmptyRepeatsOnce));
            if (SongTransitions.PlanFor(item.Transition, timeline, Blocks, nextSong) is not { } plan) return null;
            return (plan, next is null ? null : host.Name(next.RefId) ?? next.Name);
        }
    }

    /// <summary>The plan as drawn: while its handle is dragged, at the dragged length.</summary>
    public (SongTransitions.Plan Plan, string? NextName)? ShownTransition =>
        TransitionPlan is { } shown && LiveTransition is { } seconds && SongTransitions.PlanFor(shown.Plan.Kind, shown.Plan.End, seconds) is { } dragged
            ? (dragged, shown.NextName)
            : TransitionPlan;

    /// <summary>Sets the song's transition (on its service item) and re-arms the player.</summary>
    public void SetTransition(SongTransition? transition)
    {
        if (main.SelectedService is not { } service || ServiceItem is not { } item
            || service.Items.FirstOrDefault(i => i.Id == item.Id) is not { } stored)
            return;
        stored.Transition = transition;
        main.SaveService(service);
        Player.RefreshTransition();
        Redraw?.Invoke();
        StateChanged?.Invoke();
    }

    /// <summary>The transition handle let go: the same kind, at the dragged length.</summary>
    public void SetTransitionSeconds(double seconds)
    {
        if (Transition is { } transition) SetTransition(new SongTransition { Kind = transition.Kind, Seconds = seconds });
    }

    /// <summary>The handover armed while this song plays ("Crossfade into Way Maker in 0:12"), if any.</summary>
    public TimelinePlayer.PendingGo? PendingTransition => IsCurrent && Player.Pending is { } go && go.ItemId == ItemId ? go : null;

    /// <summary>Cancel Transition: this song plays to its end and stops; nothing starts by itself.</summary>
    public void CancelTransition()
    {
        Player.CancelGo();
        StateChanged?.Invoke();
    }

    /// <summary>The row the transition curves are drawn on with their handle: the tracks when they show, else the music lane.</summary>
    public TimelineRow? TransitionRow =>
        Layout.Rows.FirstOrDefault(r => r.Kind == TimelineRowKind.Tracks)
        ?? Layout.Rows.FirstOrDefault(r => r.Lane == TimelineLane.Audio);

    /// <summary>The transition handle's centre, in content points.</summary>
    public (double X, double Y)? TransitionHandle
    {
        get
        {
            if (ShownTransition is not { } shown || TransitionRow is not { } row) return null;
            var plan = shown.Plan;
            return (Layout.Map.XOfTime(Math.Min(plan.NextAt, plan.FadeFrom ?? plan.NextAt)), row.Top + 3);
        }
    }

    private SongTransitions.Plan? TransitionHandleAt(double x, double y) =>
        TransitionHandle is { } handle && Math.Abs(handle.X - x) <= 10 && Math.Abs(handle.Y - y) <= 10 ? TransitionPlan?.Plan : null;

    // MARK: Timecode start

    /// <summary>This song's start in the service's timecode (by service order, when the service runs that way).</summary>
    public int? TimecodeStart
    {
        get
        {
            if (main.SelectedService is not { } service || ServiceItem is null || ItemId is not { } id) return null;
            return TimecodeFollower.Starts(service, RunOfShow(service), main.Library.Get<Presentation>).TryGetValue(id, out var start) ? start : null;
        }
    }

    private static IReadOnlyList<ServiceItem> RunOfShow(Service service) => service.Items.Where(i => i.HiddenInPresenter != true).ToList();

    /// <summary>The service gives its songs starts by run order (01:00, 02:00, …) instead of each song's saved one.</summary>
    public bool ByOrder => ServiceItem is not null && main.SelectedService?.TimecodeByOrder == true;

    public void SetByOrder(bool on)
    {
        if (main.SelectedService is not { } service || ServiceItem is null) return;
        service.TimecodeByOrder = on ? true : null;
        main.SaveService(service);
        StateChanged?.Invoke();
    }

    /// <summary>The other songs in the service with a timeline, and where each sits on the timecode line.</summary>
    public IReadOnlyList<(PresentationTimelines.Window Window, string Name)> OtherWindows()
    {
        if (main.SelectedService is not { } service) return [];
        var run = RunOfShow(service);
        var starts = TimecodeFollower.Starts(service, run, main.Library.Get<Presentation>);
        var windows = new List<(PresentationTimelines.Window, string)>();
        foreach (var other in run)
        {
            if (other.ItemKind != ServiceItemKind.Presentation || other.Id == ItemId || main.Library.Get<Presentation>(other.RefId) is not { } deck
                || PresentationTimelines.Timeline(other.ArrangementId, deck) is not { } timeline)
                continue;
            var length = PresentationTimelines.Frames(PresentationTimelines.Length(timeline));
            windows.Add((new PresentationTimelines.Window(other.Id, starts.TryGetValue(other.Id, out var s) ? s : timeline.TimecodeStart, length), other.Name));
        }
        return windows;
    }

    /// <summary>This song's place on the timecode line, starting at <paramref name="start"/> (default: where it starts now).</summary>
    public PresentationTimelines.Window? WindowAt(int? start = null) =>
        Timeline is { } timeline
            ? new PresentationTimelines.Window(ItemId ?? "", start ?? TimecodeStart ?? timeline.TimecodeStart,
                PresentationTimelines.Frames(PresentationTimelines.Length(timeline)))
            : null;

    /// <summary>The songs this one's timecode overlaps (a timecode there would play both).</summary>
    public IReadOnlyList<(PresentationTimelines.Window Window, string Name)> Clashes(int? start = null) =>
        WindowAt(start) is { } mine ? OtherWindows().Where(o => mine.Overlaps(o.Window)).ToList() : [];

    /// <summary>The first free hour on the timecode line for a song this long.</summary>
    public int SuggestedStart(double length) =>
        PresentationTimelines.SuggestedStart(PresentationTimelines.Frames(length), OtherWindows().Select(w => w.Window), ItemId ?? "");

    public void SetTimecodeStart(int frames) => EditTimeline("Timecode Start", timeline =>
    {
        timeline.TimecodeStart = Math.Max(0, frames);
        return timeline;
    });

    public void SetFollowsTimecode(bool on) => EditTimeline(on ? "Play from Timecode" : "Stop Playing from Timecode", timeline =>
    {
        timeline.TimecodeEnabled = on;
        return timeline;
    });

    // MARK: Section map

    /// <summary>Where each section is in the song's recording (made from its stems, or found with a MultiTracks download).</summary>
    public SongMap? SongMap => Song?.SongMap;

    /// <summary>A map needs audio to point into: stems, or a music clip.</summary>
    public bool CanMakeSongMap => TimelineTracks.Stems(Cues).Count > 0 || Cues.Any(TimelineClips.IsClip);

    /// <summary>The played sections the map has no audio for ("Chorus (4th time)").</summary>
    public IReadOnlyList<string> MissingSections()
    {
        if (SongMap is not { } map || Song is null) return [];
        var names = new Dictionary<string, string>();
        foreach (var section in Song.Sections ?? []) names.TryAdd(section.Id, section.Name);
        return SongMaps.Missing(map, Blocks, names);
    }

    /// <summary>Saves the song's section map (null removes it) and lays the timeline out from it.</summary>
    public void WriteSongMap(SongMap? map, string label)
    {
        if (!CanEdit)
        {
            if (IsRecording) Message?.Invoke("Stop recording first.");
            return;
        }
        if (main.Library.Get<Presentation>(Song!.Id) is not { } deck) return;
        history.Record(label, deck);
        deck.SongMap = map;
        main.SaveTimelineSong(deck);
        Player.LayOutFromMap(deck.Id, ArrangementId);
        mapKey = "";
        Refresh();
    }

    /// <summary>"Make Section Map": a map of this timeline's audio as it is now, with no markers yet.</summary>
    public void MakeSongMap() => WriteSongMap(new SongMap { Recording = Cues.Select(c => c.Copy()).ToList(), Sections = [] }, "Make Section Map");

    public void EditSection(string sectionId, Action<AudioSection> change)
    {
        if (SongMap is not { } current) return;
        var map = current.Copy();
        var index = map.Sections.FindIndex(s => s.Id == sectionId);
        if (index < 0) return;
        var section = map.Sections[index].Copy();
        change(section);
        map.Sections[index] = section;
        map.Sections = map.Sections.OrderBy(s => s.Start).ToList();
        WriteSongMap(map, "Edit Section Map");
    }

    /// <summary>A frame later or earlier.</summary>
    public void NudgeSection(string sectionId, double seconds) =>
        EditSection(sectionId, section => section.Start = Math.Max(0, PresentationTimelines.SnappedToFrame(section.Start + seconds)));

    /// <summary>A new section starting where the playhead is in the recording.</summary>
    public void AddMarker()
    {
        if (SongMap is not { } map || Song is null) return;
        var start = SongMaps.RecordingTime(InsertionTime, map, Timeline?.MapLayout);
        WriteSongMap(SongMaps.Adding("Section", start, map, Song.Sections ?? []), "Add Marker");
    }

    public void RemoveMarker(string sectionId)
    {
        if (SongMap is not { } current) return;
        var map = current.Copy();
        map.Sections = map.Sections.Where(s => s.Id != sectionId).ToList();
        WriteSongMap(map, "Remove Marker");
    }

    /// <summary>Removes the map; the song's timelines stay as they are now.</summary>
    public void RemoveSongMap() => WriteSongMap(null, "Remove Section Map");

    // MARK: Tracks

    /// <summary>The church's tracks, in playback order.</summary>
    public IReadOnlyList<TrackChannel> Channels => main.TrackChannels;

    /// <summary>Adds a track by name (in its family's place in the list); null for a blank name.</summary>
    public TrackChannel? AddChannel(string name)
    {
        if (name.Trim().Length == 0) return null;
        TrackChannel? made = null;
        main.UpdateTrackChannels(list => made = TrackChannelEditing.Adding(name, list));
        Relayout();
        return made;
    }

    /// <summary>"New Empty Track": a track (and its row on this song) to drop one file onto.</summary>
    public void MakeTrack(string name)
    {
        if (AddChannel(name) is { } made) AddTrackRow(made.Id);
    }

    public void EditChannel(string id, Action<TrackChannel> change)
    {
        main.UpdateTrackChannels(list =>
        {
            var index = list.FindIndex(c => c.Id == id);
            if (index >= 0) change(list[index]);
        });
        Relayout();
    }

    public void RenameChannel(string id, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length > 0) EditChannel(id, c => c.Name = trimmed);
    }

    public void SetChannelRole(string id, TimelineTracks.Role role) =>
        EditChannel(id, c => c.Role = role == TimelineTracks.Role.Music ? null : role.Raw());

    public void SetChannelMono(string id, bool mono) => EditChannel(id, c => c.Mono = mono ? true : null);

    /// <summary>"Move Stems To": every stem on one row goes to another track, and the song's track set remembers it.</summary>
    public void MoveStems(string from, string to)
    {
        var stems = TimelineTracks.Stems(Cues).Where(s => s.TrackChannelId() == from).ToList();
        if (!Edit("Move Stems", cues => TimelineEditing.MovingStems(cues, from, to))) return;
        var moved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var stem in stems)
            if (stem.MediaId is { } id) moved.TryAdd(id, to);
        Remember(moved);
        if (ItemId is { } item && addedTrackRows.TryGetValue(item, out var rows)) rows.Remove(from);
        Relayout();
    }

    // Each track set holding these stems remembers the tracks they went on.
    private void Remember(IReadOnlyDictionary<string, string> channelsByStem)
    {
        if (channelsByStem.Count == 0) return;
        foreach (var set in main.Library.All<TrackSet>())
            if (StemImporter.Remember(set, channelsByStem)) main.Library.Save(set);
    }

    public bool IsMuted(string channelId) => main.TrackOutputs.Channels.TryGetValue(channelId, out var mix) && mix.Muted;

    public bool IsSoloed(string channelId) => main.TrackOutputs.Channels.TryGetValue(channelId, out var mix) && mix.Solo;

    /// <summary>The M button on a track row (this computer only).</summary>
    public void ToggleMute(string channelId)
    {
        var mix = main.TrackOutputs.Mix(channelId);
        mix.Muted = !mix.Muted;
        main.SaveTrackOutputs();
        LayoutChanged?.Invoke();
    }

    /// <summary>The S button: hear only soloed music tracks (click and guide stay).</summary>
    public void ToggleSolo(string channelId)
    {
        var mix = main.TrackOutputs.Mix(channelId);
        mix.Solo = !mix.Solo;
        main.SaveTrackOutputs();
        LayoutChanged?.Invoke();
    }

    // MARK: Importing stems

    // Copying and hashing big stem files takes a while: on its own thread, never the UI's
    // (and not .NET's shared pool, which the show's timing must never wait behind).
    private WorkQueue? imports;

    /// <summary>Stems are being copied into the library.</summary>
    public bool Importing { get; private set; }

    /// <summary>
    /// Asks the window which tracks the files that match none go on (the Mac's "files match
    /// no track" sheet). Null answer = cancelled. Without a window, the suggested new tracks are made.
    /// </summary>
    public Func<TimelineTracks.StemPlan, IReadOnlyList<TrackChannel>, TimelineTracks.StemResolution?>? ResolveStems { get; set; }

    /// <summary>The import finished (the self-test waits for it).</summary>
    public event Action? ImportFinished;

    /// <summary>
    /// "Import Audio": the files (or a song's folder) at <paramref name="at"/>, each on the
    /// track its name matches (new tracks made as needed). One file dropped on a track row goes
    /// on that row (<paramref name="channel"/>). A MultiTracks download's section markers come too.
    /// </summary>
    public void ImportStems(IReadOnlyList<string> paths, double at, string? channel = null)
    {
        if (!CanEdit || Song is not { } song || paths.Count == 0) return;
        var joining = StemImporter.JoiningSet(paths, Cues, main.Library.All<TrackSet>());
        var single = paths.Count == 1 && !Directory.Exists(paths[0]);
        var store = main.Library;
        var name = song.Name;
        at = PresentationTimelines.SnappedToFrame(Math.Max(0, at));
        Importing = true;
        StateChanged?.Invoke();
        imports ??= new WorkQueue("MxU stem import", ThreadPriority.BelowNormal);
        imports.Async(() =>
        {
            SongMapImport? map = null;
            (TrackSet Set, List<TimelineStemImport> Stems)? made = null;
            Exception? failure = null;
            try
            {
                map = single && channel is not null ? null : SongMapImport.Find(paths);
                made = new StemImporter(store).ImportTrackSet(paths, name, joining);
            }
            catch (Exception error)
            {
                failure = error;
            }
            OnUi(() =>
            {
                Importing = false;
                if (failure is not null) Message?.Invoke($"Couldn't import the audio: {failure.Message}");
                else if (made is not { } imported) Message?.Invoke("No audio files there to import.");
                else PlaceStems(imported.Stems, at, imported.Stems.Count == 1 ? channel : null, imported.Set.Id, map);
                StateChanged?.Invoke();
                ImportFinished?.Invoke();
            });
        });
    }

    /// <summary>The library's track sets (a song's stems from one download), for "Add a Track Set".</summary>
    public IReadOnlyList<TrackSet> TrackSetChoices() => main.Library.All<TrackSet>();

    /// <summary>A track set at the playhead: every stem lands on its track (the Mac's drop of a set from the library).</summary>
    public void AddTrackSet(TrackSet set)
    {
        if (!CanEdit) return;
        var stems = set.Stems.Select(stem => new TimelineStemImport(
            stem.Name, stem.AudioId, main.Library.Get<AudioItem>(stem.AudioId)?.DurationSeconds, ChannelId: stem.ChannelId)).ToList();
        PlaceStems(stems, InsertionTime, set: set.Id);
    }

    /// <summary>Plans where each stem goes; asks about files that match no track, then lands them. Port of placeStems.</summary>
    public void PlaceStems(IReadOnlyList<TimelineStemImport> stems, double at, string? channel = null, string? set = null, SongMapImport? map = null)
    {
        if (stems.Count == 0) return;
        var channels = main.TrackChannels;
        var plan = TimelineTracks.Plan(stems, channels, channel);
        TimelineTracks.StemResolution? resolution = plan.Unmatched.Count == 0 || channels.Count == 0 || ResolveStems is null
            ? TimelineTracks.Resolve(plan)
            : ResolveStems(plan, channels);
        if (resolution is null) return;
        Land(stems, at, resolution, set, map);
    }

    private void Land(IReadOnlyList<TimelineStemImport> stems, double at, TimelineTracks.StemResolution resolution, string? set, SongMapImport? map)
    {
        if (resolution.Made.Count > 0 || resolution.Remembered.Count > 0)
            main.UpdateTrackChannels(list =>
            {
                var added = TimelineTracks.Adding(resolution, list);
                list.Clear();
                list.AddRange(added);
            });
        Edit("Import Audio", cues =>
        {
            var into = cues.ToList();
            TimelineTracks.Place(stems, at, resolution.Assignments, into);
            return into;
        });
        if (set is not null && main.Library.Get<TrackSet>(set) is { } trackSet)
        {
            var landed = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var stem in stems)
                if (resolution.Assignments.TryGetValue(stem.FileName, out var id)) landed.TryAdd(stem.AudioId, id);
            if (StemImporter.Remember(trackSet, landed)) main.Library.Save(trackSet);
        }
        if (map is not null) MakeMap(map, at);
        main.ApplyTrackRouting();
    }

    /// <summary>A song map from a MultiTracks download's markers, starting where its stems landed. Port of makeMap.</summary>
    private void MakeMap(SongMapImport found, double at)
    {
        if (Song is null || main.Library.Get<Presentation>(Song.Id) is not { } deck
            || PresentationTimelines.Timeline(ArrangementId, deck)?.Cues is not { } recording)
            return;
        var map = new SongMap { Recording = recording.Select(c => c.Copy()).ToList(), Sections = [], Source = "multitracks" };
        var sections = SongMaps.Sections(found.Set, found.Bpm, offset: at, recordingEnd: SongMaps.RecordingEnd(map));
        map.Sections = SongMaps.Linked(sections, deck.Sections ?? []);
        WriteSongMap(map, "Import Audio");
    }

    /// <summary>Audio files dropped on the Music lane: into the library, then onto the lane one after another.</summary>
    public void ImportMusic(IReadOnlyList<string> paths, double at)
    {
        if (!CanEdit || paths.Count == 0) return;
        var store = main.Library;
        at = PresentationTimelines.SnappedToFrame(Math.Max(0, at));
        Importing = true;
        StateChanged?.Invoke();
        imports ??= new WorkQueue("MxU stem import", ThreadPriority.BelowNormal);
        imports.Async(() =>
        {
            var items = new AudioImporter(store).ImportFiles(paths).Select(r => r.Item).OfType<AudioItem>().ToList();
            OnUi(() =>
            {
                Importing = false;
                var cues = new List<TimelineCue>();
                var next = at;
                foreach (var item in items)
                {
                    var cue = TimelineEditing.MusicCue(item, next);
                    cues.Add(cue);
                    next = PresentationTimelines.SnappedToFrame(next + (cue.Duration ?? 10));
                }
                if (cues.Count == 0) Message?.Invoke("No audio files there to import.");
                else if (Edit("Add to Timeline", existing => [.. existing, .. cues])) Select(cues.Select(c => c.Id));
                StateChanged?.Invoke();
                ImportFinished?.Invoke();
            });
        });
    }

    private void OnUi(Action action)
    {
        if (ui is not null) ui.Post(_ => action(), null);
        else action();
    }
}
