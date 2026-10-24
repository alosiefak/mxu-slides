using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.Outputs;
using MxU.Slides.Windows.Rendering;

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
        var arranged = SlideSceneBuilder.ArrangedSlides(presentation, arrangementId);
        if (occurrence < 0 || occurrence >= arranged.Count) return;
        var slide = arranged[occurrence];
        var theme = Library.Get<Theme>(presentation.ThemeId);
        var wasLive = Show.Live is not null;
        // A slide (or its service item) can switch the output preset first, as on the Mac.
        if (runActions) Actions.ApplyPresetSwitches(slide, itemId);
        Show.Fire(slide, presentation, theme, arrangementId, arrivesSettled: fireSettled);
        Live = new LiveContext(itemId, presentation.Id, arrangementId, occurrence);
        RenderOutputs(TransitionFor(slide, presentation, wasLive));
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
            row.IsLive = Live?.ItemId == row.Item.Id && Show.Live is not null
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
            var index = positions.FindIndex(p => p.Item.Id == itemId && (p.Media is not null || p.Occurrence == Live.Occurrence));
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
        if (position.Media is { } media)
        {
            FireMedia(media);
            Live = new LiveContext(position.Item.Id, Live?.PresentationId ?? "", null, 0);
            MarkLiveTiles();
            return;
        }
        Fire(position.Presentation!, position.ArrangementId, position.Occurrence, position.Item.Id);
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

    /// <summary>F1: everything off, and any slide-action steps still waiting on a delay are cancelled.</summary>
    [RelayCommand]
    private void ClearAll()
    {
        Actions.CancelPendingDelayedActions();
        timeline?.Stop();
        audio?.Stop();
        Clear(() => Show.ClearAll());
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

    private void Clear(Action clear)
    {
        clear();
        if (Show.Live is null)
        {
            LiveTitle = Show.IsEmpty ? "Nothing live" : "Media only";
            LiveDetail = "";
        }
        RenderOutputs(DefaultTransition());
        MarkLiveTiles();
    }

    // MARK: Rendering the outputs

    private SceneTransition DefaultTransition() => Settings.Transition switch
    {
        OutputTransition.Cut => SceneTransition.Cut,
        OutputTransition.FadeThroughBlack => new SceneTransition(TransitionStyle.FadeThroughColor, Settings.TransitionSeconds * 2, Colors.Black),
        _ => new SceneTransition(TransitionStyle.Dissolve, Settings.TransitionSeconds),
    };

    /// <summary>The slide's transition, else the presentation's, else the default from Settings.</summary>
    private SceneTransition TransitionFor(Slide slide, Presentation presentation, bool wasLive)
    {
        if ((slide.Transition ?? presentation.Transition) is not { } transition) return DefaultTransition();
        var seconds = transition.DurationSeconds ?? Settings.TransitionSeconds;
        return transition.TransitionKind switch
        {
            TransitionKind.Cut => SceneTransition.Cut,
            TransitionKind.FadeBlack => new SceneTransition(TransitionStyle.FadeThroughColor, seconds, Colors.Black),
            TransitionKind.FadeWhite => new SceneTransition(TransitionStyle.FadeThroughColor, seconds, Colors.White),
            TransitionKind.FadeColor => new SceneTransition(TransitionStyle.FadeThroughColor, seconds,
                Painting.ToColor(ColorHex.Color(transition.ColorHex) ?? SceneColor.Black)),
            _ => new SceneTransition(TransitionStyle.Dissolve, seconds),
        };
    }

    /// <summary>Renders the audience and stage scenes to the outputs and the operator previews.</summary>
    public void RenderOutputs(SceneTransition transition, bool refreshOnly = false)
    {
        try
        {
            var audience = Show.AnimatedScene(resolveLinkedText: ResolveLinkedText);
            if (refreshOnly)
            {
                Outputs?.Update(OutputRole.Audience, audience);
                AudiencePreview?.Update(audience);
                Program?.Update(audience);
            }
            else
            {
                Outputs?.Present(OutputRole.Audience, audience, transition);
                AudiencePreview?.Present(audience, transition);
                Program?.Present(audience, transition);
            }
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
                if (Show.Live is null) Clear(() => { });
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
}
