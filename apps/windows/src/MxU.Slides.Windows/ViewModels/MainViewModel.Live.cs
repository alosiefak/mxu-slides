using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Outputs;
using MxU.Slides.Core.Scene;
using MxU.Slides.Core.Threading;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.Outputs;
using MxU.Slides.Windows.Rendering;
using SceneTransition = MxU.Slides.Windows.Rendering.SceneTransition;

namespace MxU.Slides.Windows.ViewModels;

public sealed partial class MainViewModel
{
    /// <summary>Where the live slide came from: a service item (or none) and its position in the arranged slides.</summary>
    public sealed record LiveContext(string? ItemId, string PresentationId, string? ArrangementId, int Occurrence);

    /// <summary>One step of the run order: a slide of a presentation item, or a media item.</summary>
    private sealed record Position(ServiceItem Item, Presentation? Presentation, string? ArrangementId, int Occurrence, MediaItem? Media);

    public LiveContext? Live { get; private set; }

    [ObservableProperty]
    public partial string LiveTitle { get; set; } = "Nothing live";

    [ObservableProperty]
    public partial string LiveDetail { get; set; } = "";

    [ObservableProperty]
    public partial bool CanRestoreLive { get; set; }

    /// <summary>Show mode: a click takes a slide live. Edit mode: a click selects it for editing.</summary>
    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    partial void OnIsEditingChanged(bool value)
    {
        if (value && SelectedSlide is null) SelectedSlide = Slides.FirstOrDefault();
    }

    [RelayCommand]
    private void SlideClicked(SlideTileViewModel? tile)
    {
        if (tile is null) return;
        if (IsEditing)
        {
            SelectedSlide = tile;
            return;
        }
        GoLive(tile);
    }

    [RelayCommand]
    private void GoLive(SlideTileViewModel? tile)
    {
        if (tile is null || CurrentPresentation is null) return;
        Fire(CurrentPresentation, SelectedArrangement?.Id, tile.Index, CurrentContextItemId);
    }

    /// <summary>Set while a "skip builds" advance fires, so the slide arrives with its automatic builds already played.</summary>
    private bool fireSettled;

    /// <param name="runActions">False when a timeline jumps (lands on a slide without running its actions, as on the Mac).</param>
    private void Fire(Presentation presentation, string? arrangementId, int occurrence, string? itemId, bool runActions = true)
    {
        SettlePending();
        var arranged = SlideSceneBuilder.ArrangedSlides(presentation, arrangementId);
        if (occurrence < 0 || occurrence >= arranged.Count) return;
        var slide = arranged[occurrence];

        // The hand-off, as on the Mac: a look that goes away with this slide (its preset drops it, the
        // slide needs another of its designs, or the slide is blank) plays its Outs first, while every
        // other output stays as it is; then the slide lands.
        var handoff = Show.Handoff(slide, IncomingLooks(slide, itemId, runActions));
        if (handoff.Wait > 0)
        {
            // Nothing advances by itself while the slide waits to land (the Mac's deferring).
            AutoAdvance.HandingOff();
            Show.DismissLooks(handoff.Leaving, ShowState.HostClock());
            RenderOutputs(SceneTransition.Cut, refreshOnly: true);
            var settled = fireSettled;
            Defer(handoff.Wait, () =>
            {
                fireSettled = settled;
                try { Fire(presentation, arrangementId, occurrence, itemId, runActions); }
                finally { fireSettled = false; }
            });
            return;
        }

        // The slide's own theme (or none for a Blank slide), else the presentation's, as on the Mac.
        var theme = ThemeFor(slide, presentation);
        // A slide (or its service item) can switch the output preset first, as on the Mac.
        if (runActions) Actions.ApplyPresetSwitches(slide, itemId);
        Show.Fire(slide, presentation, theme, arrangementId, arrivesSettled: fireSettled);
        Live = new LiveContext(itemId, presentation.Id, arrangementId, occurrence);
        // Each layer changes the Mac's way: the slide's own transition, else its presentation's, else Settings'.
        RenderOutputs(SceneTransition.Automatic);
        UpdateLiveLabels(presentation, slide, occurrence, arranged.Count);
        Session.LivePresentationId = presentation.Id;
        Session.LiveSlideId = slide.Id;
        Session.LiveArrangementId = arrangementId;
        Session.Save();
        CanRestoreLive = false;
        // A timeline being recorded turns the slide into a cue at the playhead.
        timeline?.NoteSlideLanded(TimelineItemId(itemId, presentation.Id), occurrence);
        // Then the slide's own actions: fire media, an overlay, a message, a timer, a combo…
        if (runActions) Actions.SlideDidFire(slide, itemId);
        // Its timer (if it advances by itself) takes over, and the service clock notes its row.
        ShowFlowSlideLanded(slide, presentation, itemId);
    }

    private void UpdateLiveLabels(Presentation presentation, Slide slide, int occurrence, int count)
    {
        var group = GroupNames.For(slide, presentation);
        LiveTitle = presentation.Name;
        LiveDetail = $"{(group is null ? "Slide" : group)} · {occurrence + 1} of {count}"
            + (Show.SlideAnimationStep is { } build ? $" · build {build.Consumed} of {build.Total}" : "");
        MarkLiveTiles();
    }

    private void MarkLiveTiles()
    {
        var livePresentation = Live?.PresentationId;
        foreach (var tile in Slides)
            tile.IsLive = Live is not null && CurrentPresentation?.Id == livePresentation
                && Live.ArrangementId == SelectedArrangement?.Id && tile.Index == Live.Occurrence && Show.Live is not null;
        foreach (var row in ServiceItems)
            row.IsLive = IsMusicRow(row.Item) ? IsMusicLive(row.Item)
                : Live?.ItemId == row.Item.Id && Show.Live is not null
                  || row.Item.ItemKind == ServiceItemKind.Media && Show.LiveMedia.Values.Any(m => m.MediaId == row.Item.RefId);
        foreach (var tile in MediaItems)
            tile.IsLive = Show.LiveMedia.Values.Any(m => m.MediaId == tile.Item.Id);
        MarkLiveOverlays();
    }

    [RelayCommand]
    private void Next() => Step(+1);

    /// <summary>Alt+→ (Option+→ on the Mac): straight to the next slide, skipping the live slide's remaining builds.</summary>
    [RelayCommand]
    private void NextSkippingBuilds() => Step(+1, settled: true);

    [RelayCommand]
    private void Previous() => Step(-1);

    /// <summary>
    /// Moves through the whole service like the Mac app. A slide with builds
    /// takes a press per click group (and one for its exit) before moving on;
    /// Previous takes them back. Past the last slide of one item it goes into
    /// the next item (media items fire their media). Outside a service it moves
    /// within the live presentation. False when there was nowhere to go.
    /// </summary>
    private bool Step(int direction, bool settled = false)
    {
        if (settled && direction > 0)
        {
            fireSettled = true;
            try { return Advance(direction, skipBuilds: true); }
            finally { fireSettled = false; }
        }
        return Advance(direction, skipBuilds: false);
    }

    private bool Advance(int direction, bool skipBuilds)
    {
        if (!skipBuilds && (direction > 0 ? Show.AdvanceStep() : Show.UnadvanceStep()))
        {
            RenderOutputs(SceneTransition.Cut, refreshOnly: true);
            UpdateBuildLabel();
            // A slide that advances by itself starts counting once its last build has played.
            AutoAdvance.StepsChanged();
            return true;
        }
        if (Live is null || Show.Live is null && Live.ItemId is null)
        {
            // Nothing live yet: start the open presentation (or the selected slide).
            if (CurrentPresentation is { } open && Slides.Count > 0)
            {
                var start = direction > 0 ? SelectedSlide?.Index ?? 0 : Slides.Count - 1;
                Fire(open, SelectedArrangement?.Id, start, CurrentContextItemId);
                return true;
            }
            return false;
        }

        if (Live.ItemId is { } itemId && SelectedService is { } service && service.Items.Any(i => i.Id == itemId))
        {
            var positions = Positions(service);
            var index = positions.FindIndex(p => p.Item.Id == itemId && (p.Presentation is null || p.Occurrence == Live.Occurrence));
            if (index < 0) return false;
            var next = index + direction;
            if (next < 0 || next >= positions.Count)
            {
                Notice = direction > 0 ? "That's the end of the service." : "That's the start of the service.";
                return false;
            }
            FirePosition(positions[next]);
            return true;
        }

        if (Library.Get<Presentation>(Live.PresentationId) is { } presentation)
        {
            var count = SlideSceneBuilder.ArrangedSlides(presentation, Live.ArrangementId).Count;
            var target = Live.Occurrence + direction;
            if (target >= 0 && target < count)
            {
                Fire(presentation, Live.ArrangementId, target, null);
                return true;
            }
        }
        return false;
    }

    /// <summary>"Build 2 of 3" beside the live slide while it has presses left.</summary>
    private void UpdateBuildLabel()
    {
        if (Show.Live is not { } live || live.Presentation is not { } presentation || Live is null) return;
        var count = SlideSceneBuilder.ArrangedSlides(presentation, Live.ArrangementId).Count;
        UpdateLiveLabels(presentation, live.Slide, Live.Occurrence, count);
    }

    private List<Position> Positions(Service service)
    {
        var positions = new List<Position>();
        foreach (var item in service.Items.Where(i => i.HiddenInPresenter != true))
        {
            switch (item.ItemKind)
            {
                case ServiceItemKind.Presentation when Library.Get<Presentation>(item.RefId) is { } p:
                    var arrangementId = item.ArrangementId ?? p.DefaultArrangementId;
                    var count = SlideSceneBuilder.ArrangedSlides(p, arrangementId).Count;
                    for (var i = 0; i < count; i++) positions.Add(new Position(item, p, arrangementId, i, null));
                    break;
                case ServiceItemKind.Media when Library.Get<MediaItem>(item.RefId) is { } m:
                    positions.Add(new Position(item, null, null, 0, m));
                    break;
                // Music rows (walk-in music, an offering playlist) are a stop too: arriving plays them.
                case ServiceItemKind.Audio when Library.Get<AudioItem>(item.RefId) is not null:
                case ServiceItemKind.Playlist when Library.Get<Playlist>(item.RefId) is not null:
                    positions.Add(new Position(item, null, null, 0, null));
                    break;
            }
        }
        return positions;
    }

    private void FirePosition(Position position)
    {
        // Follow along in the slide grid so the operator sees what is live.
        if (SelectedServiceItem?.Item.Id != position.Item.Id)
        {
            var row = ServiceItems.FirstOrDefault(r => r.Item.Id == position.Item.Id);
            if (row is not null) SelectedServiceItem = row;
        }
        if (position.Presentation is null)
        {
            FireRow(position.Item);
            return;
        }
        Fire(position.Presentation, position.ArrangementId, position.Occurrence, position.Item.Id);
    }

    [RelayCommand]
    private void RestoreLive()
    {
        CanRestoreLive = false;
        if (Library.Get<Presentation>(Session.LivePresentationId) is not { } p) return;
        var arranged = SlideSceneBuilder.ArrangedSlides(p, Session.LiveArrangementId);
        var index = arranged.FindIndex(s => s.Id == Session.LiveSlideId);
        if (index >= 0) Fire(p, Session.LiveArrangementId, index, Session.ServiceItemId);
    }

    // MARK: Clearing (F1–F6, as on the Mac)

    /// <summary>
    /// F1: everything the Clear All checklist ticks goes (by default all but Inputs, as on the Mac:
    /// see <see cref="ClearAllScope"/>), and any slide-action steps still waiting on a delay are cancelled.
    /// </summary>
    [RelayCommand]
    private void ClearAll()
    {
        Actions.CancelPendingDelayedActions();
        var swept = ClearAllScope.Swept(ClearAllSkips);
        // A timeline would put its next slide straight back up; music and stems only go when Music is ticked.
        if (swept.Contains(ShowFunction.Slides)) timeline?.Stop();
        if (TracksClear.StopsTracks(swept)) timeline?.ClearTracks();
        if (swept.Contains(ShowFunction.Audio)) audio?.Stop();
        // As on the Mac, Clear All leaves every layer to its room default (no media item's own exit).
        nextRenderClearsAll = true;
        Clear(() => Show.ClearAll(swept));
        AutoAdvance.CancelMedia();
        UpdateNowPlaying();
    }

    // Slides and overlays with Out animations play them on the way off (Clear All always cuts).
    [RelayCommand]
    private void ClearSlide() => Clear(() => Show.Clear(ShowFunction.Slides, ShowState.HostClock()));

    [RelayCommand]
    private void ClearMedia() => Clear(() => Show.Clear(ShowFunction.Media));

    [RelayCommand]
    private void ClearOverlays() => Clear(() => Show.Clear(ShowFunction.Overlays, ShowState.HostClock()));

    [RelayCommand]
    private void ClearAudio()
    {
        timeline?.ClearTracks();
        audio?.Stop();
        Clear(() => Show.Clear(ShowFunction.Audio));
        UpdateNowPlaying();
    }

    [RelayCommand]
    private void ClearAlerts() => Clear(() => Show.Clear(ShowFunction.Alerts));

    /// <param name="settle">False for the ticker tidying up after Outs finish (that never cuts through a waiting hand-off).</param>
    private void Clear(Action clear, bool settle = true)
    {
        // A clear cuts through a waiting hand-off, as on the Mac.
        if (settle) SettlePending();
        clear();
        if (Show.Live is null)
        {
            LiveTitle = Show.IsEmpty ? "Nothing live" : "Media only";
            LiveDetail = "";
            // The slide went (cleared, or its Outs finished): its timer goes with it.
            AutoAdvance.Cancel();
        }
        RenderOutputs(DefaultTransition());
        MarkLiveTiles();
    }

    // MARK: Rendering the outputs

    /// <summary>
    /// How the outputs change for a cue: each layer the Mac's way (see <see cref="LiveTransitions"/>):
    /// the cue's own transition, else its presentation's, else the room default from Settings.
    /// </summary>
    private static SceneTransition DefaultTransition() => SceneTransition.Automatic;

    /// <summary>
    /// The room defaults from Settings › Transitions, as on the Mac: what a slide does when
    /// neither it nor its presentation names a transition, and what a background or video does
    /// when it doesn't name one.
    /// </summary>
    private RoomTransitions RoomDefaults() =>
        new(RoomTransition(Settings.Transition, Settings.TransitionSeconds), RoomTransition(Settings.MediaTransition, Settings.MediaTransitionSeconds));

    private static Transition? RoomTransition(OutputTransition kind, double seconds) => kind switch
    {
        OutputTransition.Cut => null,
        OutputTransition.FadeThroughBlack => new Transition { TransitionKind = TransitionKind.FadeBlack, DurationSeconds = seconds * 2 },
        _ => new Transition { TransitionKind = TransitionKind.Dissolve, DurationSeconds = seconds },
    };

    /// <summary>Long text turns its pages the way slides change (ServiceControls.apply on the Mac).</summary>
    private Paging.Turn PageTurn() => Settings.Transition switch
    {
        OutputTransition.Cut => Paging.Turn.Cut,
        OutputTransition.FadeThroughBlack => Paging.Turn.Following(TransitionKind.FadeBlack, Settings.TransitionSeconds * 2),
        _ => Paging.Turn.Following(TransitionKind.Dissolve, Settings.TransitionSeconds),
    };

    // What the outputs were last given, so the next push knows which cues changed.
    private LiveCues renderedCues = LiveCues.Nothing;
    private bool nextRenderClearsAll;

    /// <summary>
    /// The layer transitions for this push. An explicit cut (not a refresh) cuts every layer;
    /// anything else follows the Mac's rules, comparing what's live now with what the outputs last showed.
    /// </summary>
    private SceneTransition LayerTransitions(SceneTransition requested, bool refreshOnly)
    {
        var before = renderedCues;
        var after = LiveCues.Of(Show);
        renderedCues = after;
        var clearAll = nextRenderClearsAll;
        nextRenderClearsAll = false;
        if (requested.Layers is not null) return requested;
        if (requested.Style == TransitionStyle.Cut && !refreshOnly) return SceneTransition.PerLayer(LiveTransitions.AllCut);
        return SceneTransition.PerLayer(LiveTransitions.For(before, after, id => Library.Get<MediaItem>(id)?.Transition, RoomDefaults(), clearAll));
    }

    /// <summary>
    /// Renders the audience and stage scenes to the outputs and the operator previews. The audience
    /// scene is built once as designed and once per look a screen shows (each screen picks its own),
    /// and every view changes layer by layer.
    /// </summary>
    public void RenderOutputs(SceneTransition transition, bool refreshOnly = false)
    {
        try
        {
            WireLooks();
            Show.Pager ??= TextLayout.Pager;
            Show.PageTurn = PageTurn();
            var looks = SyncLooks();
            var audience = Show.AnimatedScene(resolveLinkedText: ResolveLinkedText);
            var lookScenes = new Dictionary<string, Core.Animation.AnimatedScene>(StringComparer.Ordinal);
            foreach (var (key, look) in looks)
                lookScenes[key] = Show.AnimatedScene(resolveLinkedText: ResolveLinkedText, slideThemeOverride: look);
            var push = LayerTransitions(transition, refreshOnly);
            Outputs?.Present(OutputRole.Audience, audience, push, lookScenes);
            AudiencePreview?.Present(audience, push);
            Program?.Present(audience, push);
            // Recordings and streams of one screen: after the outputs, which they copy (looks included).
            ScreenFeeds?.Show(OutputRole.Audience, audience, refreshOnly ? null : push);
            RenderStage(refreshOnly ? SceneTransition.Cut : transition, refreshOnly);
            EnsureTicking();
        }
        catch (Exception ex)
        {
            Log.Error("Rendering the outputs failed", ex);
            Problem = $"Couldn't draw the output: {ex.Message}";
        }
    }

    private DispatcherTimer? ticker;

    /// <summary>Clocks and timers on screen re-render each second (no transition).</summary>
    private void EnsureTicking()
    {
        if (ticker is not null) return;
        ticker = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(250) };
        var lastSecond = -1L;
        ticker.Tick += (_, _) =>
        {
            var second = DateTimeOffset.Now.ToUnixTimeSeconds();
            TickTimers();
            // A slide or overlay playing its Outs leaves once they finish.
            if (Show.HasPendingExits && Show.SweepFinishedExits())
            {
                if (Show.Live is null) Clear(() => { }, settle: false);
                else RenderOutputs(SceneTransition.Cut, refreshOnly: true);
                MarkLiveOverlays();
            }
            if (second == lastSecond) return;
            lastSecond = second;
            if (!NeedsTicking()) return;
            RenderOutputs(SceneTransition.Cut, refreshOnly: true);
        };
        ticker.Start();
    }

    // MARK: Looks (each screen's theme)

    private OutputManager? looksWiredTo;

    private bool ThemeExists(string themeId) => Library.Get<Theme>(themeId) is not null;

    /// <summary>
    /// Once the outputs exist: the Look picker's theme list, rendering again when a screen's look
    /// changes, and the output preset that was active last time (applied at launch, as on the Mac).
    /// </summary>
    private void WireLooks()
    {
        if (Outputs is not { } outputs || ReferenceEquals(looksWiredTo, outputs)) return;
        looksWiredTo = outputs;
        outputs.Themes = () => [.. Library.All<Theme>().OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)];
        outputs.LooksChanged += () => RenderOutputs(SceneTransition.Cut);
        if (Settings.ActiveOutputPresetId is { } presetId && Library.Get<OutputPreset>(presetId) is { } preset)
            OutputPresets.Apply(outputs.Config, preset, ThemeExists);
    }

    /// <summary>Looks as themes, scoped to their folder or design; a look whose theme has gone is left out (that screen shows the slide as designed).</summary>
    private Dictionary<string, Theme> ResolveLooks(IReadOnlyDictionary<string, SlideThemeLook>? looks)
    {
        var themes = new Dictionary<string, Theme>(StringComparer.Ordinal);
        foreach (var (key, look) in looks ?? new Dictionary<string, SlideThemeLook>())
            if (Library.Get<Theme>(look.ThemeId) is { } theme)
                themes[key] = theme.ScopedToSlideFolder(look.Folder).ScopedToSlideId(look.SlideId);
        return themes;
    }

    /// <summary>The looks some screen shows now, handed to the show (which counts their builds) in the Mac's order.</summary>
    private Dictionary<string, Theme> SyncLooks()
    {
        var looks = ResolveLooks(Outputs?.Config.ActiveLooks);
        Show.SlideThemeOverrides = [.. looks.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value)];
        return looks;
    }

    /// <summary>The looks that will show once this slide fires (it, or its service item, may switch the output preset).</summary>
    private IReadOnlyList<Theme> IncomingLooks(Slide slide, string? itemId, bool runActions)
    {
        if (Outputs is not { } outputs) return Show.SlideThemeOverrides;
        var routed = slide;
        if (!runActions)
        {
            routed = MxuJson.Clone(slide);
            routed.Actions = null;
        }
        var presetId = Actions.PredictedPresetId(routed, itemId);
        var looks = presetId == Settings.ActiveOutputPresetId
            ? outputs.Config.ActiveLooks
            : outputs.Config.LooksUnder(OutputPresets.Routing(presetId is null ? null : Library.Get<OutputPreset>(presetId), ThemeExists));
        return [.. ResolveLooks(looks).Values];
    }

    /// <summary>
    /// Makes an output preset (or none) active. Looks it drops play their Outs first while the slide
    /// stays up, then the outputs switch (the Mac's switchPreset). A slide or a clear in the meantime
    /// switches straight away.
    /// </summary>
    public void ActivateOutputPreset(OutputPreset? preset)
    {
        if (Outputs is not { } outputs) return;
        SettlePending();
        void Activate()
        {
            OutputPresets.Apply(outputs.Config, preset, ThemeExists);
            Settings.ActiveOutputPresetId = preset?.Id;
            Settings.Save();
            RenderOutputs(SceneTransition.Cut);
        }
        var incoming = ResolveLooks(outputs.Config.LooksUnder(OutputPresets.Routing(preset, ThemeExists))).Values.ToList();
        var change = Show.LookSwitch(incoming);
        if (change.Wait <= 0)
        {
            Activate();
            return;
        }
        Show.DismissLooks(change.Leaving, ShowState.HostClock());
        RenderOutputs(SceneTransition.Cut, refreshOnly: true);
        pendingSwitch = Activate;
        Defer(change.Wait, () =>
        {
            if (pendingSwitch is not { } activate) return;
            pendingSwitch = null;
            activate();
        });
    }

    // MARK: Waiting hand-offs

    private ITimer? pendingTimer;
    private int pendingGeneration;
    private Action? pendingSwitch;

    /// <summary>
    /// Runs <paramref name="then"/> on the UI thread after a look's Outs, timed by the show clock
    /// (never the shared thread pool). A later fire or clear cancels it (<see cref="SettlePending"/>).
    /// </summary>
    private void Defer(double seconds, Action then)
    {
        var generation = ++pendingGeneration;
        var dispatcher = Dispatcher.CurrentDispatcher;
        pendingTimer?.Dispose();
        pendingTimer = ShowTimeProvider.Shared.CreateTimer(_ => dispatcher.BeginInvoke(() =>
        {
            if (generation != pendingGeneration) return;
            pendingTimer?.Dispose();
            pendingTimer = null;
            try { then(); }
            catch (Exception ex) { Log.Error("A slide waiting for a look's Outs couldn't go live", ex); }
        }), null, TimeSpan.FromSeconds(seconds), Timeout.InfiniteTimeSpan);
    }

    /// <summary>A fire or a clear cuts through a waiting hand-off; a waiting preset switch happens now (as on the Mac).</summary>
    private void SettlePending()
    {
        if (pendingTimer is not null) Log.Info("Live: a waiting hand-off was cut through by this fire or clear");
        pendingTimer?.Dispose();
        pendingTimer = null;
        pendingGeneration++;
        if (pendingSwitch is { } activate)
        {
            pendingSwitch = null;
            activate();
        }
    }
}
