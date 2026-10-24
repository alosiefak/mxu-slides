using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Outputs;
using MxU.Slides.Core.Scene;
using MxU.Slides.Core.Stage;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.Outputs;
using MxU.Slides.Windows.Rendering;
using AnimatedScene = MxU.Slides.Core.Animation.AnimatedScene;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>One confidence screen in the Stage panel: its name and the layout it shows (null: the built-in one).</summary>
public sealed record StageScreenRow(Guid Id, string Name, string? LayoutId);

/// <summary>
/// The stage display (confidence monitors): each stage screen's own layout, what those layouts
/// read (the live and next slide, timers, the alert…), and the stage message. Each screen's
/// layout is kept in settings.json under the Mac's name for the same map
/// (<see cref="ConfidenceScreenLayouts.SettingsKey"/>); the Stage panel (Views/Operator/StagePanel.cs) sets it.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Every stage layout in the library (the Stage panel's choices), Classic first.</summary>
    public ObservableCollection<ConfidenceLayout> StageLayouts { get; } = [];

    /// <summary>Raised when a stage screen's layout, the layouts, or the stage screens change: the Stage panel redraws.</summary>
    public event Action? StageLayoutsChanged;

    // Which layout each stage screen shows. Null until the screens exist, when it is read from
    // settings (or, the first time, made from the one layout every stage screen used to share).
    private ConfidenceScreenLayouts? stageLayouts;

    /// <summary>The stage screen the Stage panel's preview shows (null: the first one).</summary>
    [ObservableProperty]
    public partial Guid? StagePreviewScreenId { get; set; }

    partial void OnStagePreviewScreenIdChanged(Guid? value) => RenderStage(SceneTransition.Cut, refreshOnly: false);

    private void InitializeStage()
    {
        InitializeTimers();
        InitializeOperator();
        RefreshStageLayouts();
    }

    private void RefreshStageLayouts()
    {
        StageLayouts.Clear();
        foreach (var layout in Library.All<ConfidenceLayout>().OrderBy(l => l.Name != "Classic")) StageLayouts.Add(layout);
        // A deleted layout's screens fall back to the built-in one, as on the Mac.
        if (stageLayouts?.Prune(id => Library.Get<ConfidenceLayout>(id) is not null) == true) SaveStageLayouts();
        StageLayoutsChanged?.Invoke();
        RenderStage(SceneTransition.Cut, refreshOnly: false);
    }

    /// <summary>The confidence screens in the setup, in order.</summary>
    public List<PlaceholderScreen> StageScreens() =>
        Outputs?.Config is { } config ? [.. config.Screens.Where(s => config.Role(s.Id) == ScreenRole.Confidence)] : [];

    /// <summary>The Stage panel's rows: each confidence screen and its layout.</summary>
    public List<StageScreenRow> StageScreenRows()
    {
        var layouts = StageAssignments();
        return [.. StageScreens().Select(s => new StageScreenRow(s.Id, s.Name, layouts?.LayoutId(s.Id)))];
    }

    /// <summary>
    /// The map, made once the screens exist. Before Phase 5 every stage screen showed the one layout
    /// in <see cref="AppSettings.StageLayoutId"/> (Classic unless changed), so the first time each
    /// confidence screen starts on that one: nothing on stage changes.
    /// </summary>
    private ConfidenceScreenLayouts? StageAssignments()
    {
        if (stageLayouts is not null) return stageLayouts;
        if (Outputs is null) return null;
        if (Settings.ConfidenceScreenLayouts is { } stored)
        {
            stageLayouts = ConfidenceScreenLayouts.FromStored(stored);
        }
        else
        {
            var shared = Library.Get<ConfidenceLayout>(Settings.StageLayoutId)?.Id ?? StageLayouts.FirstOrDefault()?.Id;
            stageLayouts = ConfidenceScreenLayouts.Migrated(shared, StageScreens().Select(s => s.Id));
            SaveStageLayouts();
        }
        return stageLayouts;
    }

    private void SaveStageLayouts()
    {
        if (stageLayouts is null) return;
        Settings.ConfidenceScreenLayouts = stageLayouts.ToStored();
        Settings.Save();
    }

    // One copy of the built-in layout: drawing only reads it (ConfidenceSceneBuilder.BuiltInLayout copies on every read).
    private static readonly ConfidenceLayout BuiltInStageLayout = ConfidenceSceneBuilder.BuiltInLayout;

    /// <summary>The layout a stage screen shows: its own choice, else the built-in layout.</summary>
    public ConfidenceLayout StageLayoutFor(Guid screenId) =>
        Library.Get<ConfidenceLayout>(StageAssignments()?.LayoutId(screenId)) ?? BuiltInStageLayout;

    /// <summary>Chooses a stage screen's layout (null: the built-in one), from the Stage panel or an import.</summary>
    public void SetStageLayout(Guid screenId, string? layoutId)
    {
        if (StageAssignments() is not { } layouts || !layouts.SetLayout(layoutId, screenId)) return;
        SaveStageLayouts();
        StageLayoutsChanged?.Invoke();
        RenderStage(SceneTransition.Cut, refreshOnly: false);
    }

    /// <summary>The "Set Confidence Layout" action: one screen, or every confidence screen. An unknown layout is ignored, as on the Mac.</summary>
    private void ApplyConfidenceLayout(string? layoutId, Guid? screenId)
    {
        if (StageAssignments() is not { } layouts) return;
        if (!layouts.Apply(layoutId, screenId, StageScreens().Select(s => s.Id), id => Library.Get<ConfidenceLayout>(id) is not null))
        {
            if (!string.IsNullOrEmpty(layoutId) && Library.Get<ConfidenceLayout>(layoutId) is null) Log.Info($"Actions: no confidence layout {layoutId}");
            return;
        }
        SaveStageLayouts();
        StageLayoutsChanged?.Invoke();
        RenderStage(SceneTransition.Cut, refreshOnly: false);
    }

    // MARK: The stage message (the Mac's Confidence module "Stage Message")

    /// <summary>The message box's text.</summary>
    [ObservableProperty]
    public partial string AlertText { get; set; } = "";

    [ObservableProperty]
    public partial bool AlertOnStage { get; set; } = true;

    /// <summary>Windows: the stage message can go to the audience too (the old Message box could).</summary>
    [ObservableProperty]
    public partial bool AlertOnAudience { get; set; }

    /// <summary>The id the stage message goes up under, as on the Mac.</summary>
    public const string StageMessageId = "stage-message";

    /// <summary>Send: the typed message, steady, to the stage (and the audience when ticked).</summary>
    [RelayCommand]
    private void ShowAlert()
    {
        var text = AlertText.Trim();
        if (text.Length == 0) return;
        var target = AlertOnAudience && AlertOnStage ? AlertTarget.Both : AlertOnAudience ? AlertTarget.Audience : AlertTarget.Confidence;
        FireAlert(StageMessageId, text, AlertBehavior.Persist, target);
    }

    /// <summary>"On stage: …" under the box while an alert shows on the confidence monitors.</summary>
    public string? StageMessageStatus => Show.LiveAlert is { ShowsOnConfidence: true } live ? $"On stage: {live.Message}" : null;

    // MARK: What the stage display knows

    /// <summary>Everything linked text can show: live and next slide, timers, the alert, service position.</summary>
    private ConfidenceInfo BuildConfidenceInfo()
    {
        SlideText? current = null, next = null, last = null;
        SlidePosition? position = null;
        string? currentItem = null, nextItem = null, group = null, presentationName = null;
        // "Confidence NEXT skips blank slides" (on unless turned off, as on the Mac).
        var skippingBlanks = Settings.NextSkipsBlanks;

        if (Show.Live is { } live)
        {
            current = ConfidenceSceneBuilder.SlideText(live.Slide, live.Presentation);
            presentationName = live.Presentation?.Name;
            if (live.Presentation is { } p)
            {
                group = GroupNames.For(live.Slide, p);
                var arranged = SlideSceneBuilder.ArrangedSlides(p, live.ArrangementId);
                var index = Live?.Occurrence ?? arranged.FindIndex(s => s.Id == live.Slide.Id);
                position = new SlidePosition(index + 1, arranged.Count);
                var upcoming = arranged.Skip(index + 1).ToList();
                if (ConfidenceSceneBuilder.NextSlide(upcoming, skippingBlanks) is { } within)
                    next = ConfidenceSceneBuilder.SlideText(within, p);
            }
        }
        if (Show.Last is { } previous) last = ConfidenceSceneBuilder.SlideText(previous.Slide, previous.Presentation);

        if (SelectedService is { } service && Live?.ItemId is { } itemId)
        {
            var items = service.Items.Where(i => i.ItemKind != ServiceItemKind.Header && i.HiddenInPresenter != true).ToList();
            var index = items.FindIndex(i => i.Id == itemId);
            currentItem = index >= 0 ? items[index].Name : null;
            nextItem = index >= 0 && index + 1 < items.Count ? items[index + 1].Name : null;
            // At the end of a song, the stage shows the next song's first lyric.
            if (next is null && index >= 0 && index + 1 < items.Count
                && Library.Get<Presentation>(items[index + 1].RefId) is { } following)
            {
                var slides = SlideSceneBuilder.ArrangedSlides(following, items[index + 1].ArrangementId ?? following.DefaultArrangementId);
                if (ConfidenceSceneBuilder.NextSlide(slides, skippingBlanks) is { } first)
                    next = ConfidenceSceneBuilder.SlideText(first, following);
            }
        }

        return new ConfidenceInfo
        {
            Current = current,
            Next = next,
            Last = last,
            Alert = Show.LiveAlert,
            AlertVisible = Show.AlertVisible,
            Timers = Timers.Timers,
            VideoCountdown = LiveVideoCountdown(),
            Timecode = publishedTimecode,
            SlidePosition = position,
            CurrentItemName = currentItem ?? presentationName,
            NextItemName = nextItem,
            CurrentGroupName = group,
            CurrentPresentationName = presentationName,
            NextServiceTitle = SelectedService?.Name,
        };
    }

    /// <summary>
    /// Time left on the live foreground video, for the stage's video countdown: the transport
    /// strip's countdown target, as on the Mac (a background loop never counts down here).
    /// </summary>
    private VideoCountdown? LiveVideoCountdown() => TransportCountdown();

    private List<SlideObject> ResolveLinkedText(IReadOnlyList<SlideObject> objects)
    {
        if (!objects.Any(o => o.TextLink is not null)) return objects.ToList();
        return LinkedText.ResolvedObjects(objects, BuildConfidenceInfo(), DateTimeOffset.Now);
    }

    /// <summary>
    /// A stage scene: the layout's objects with live text filled in, plus the alert band (unless the
    /// layout has its own stage-message box, or a flashing alert is on its off beat). With no layout,
    /// what the first stage screen shows (the built-in layout when there is none).
    /// </summary>
    public RenderScene StageScene(ConfidenceLayout? layout = null) => StageScene(layout ?? PreviewStageLayout(), BuildConfidenceInfo(), DateTimeOffset.Now);

    private static RenderScene StageScene(ConfidenceLayout layout, ConfidenceInfo info, DateTimeOffset now)
    {
        var canvas = new SceneSize(layout.CanvasWidth ?? 1920, layout.CanvasHeight ?? 1080);
        var scene = new RenderScene(canvas);
        var resolved = LinkedText.ResolvedObjects(layout.Objects, info, now);
        SlideSceneBuilder.AddObjects(scene, resolved.Where(o => VisibilityRules.IsShown(o, resolved, info, now)).ToList(),
            LayerKind.Slide, canvas, $"stage-{layout.Id}-");
        if (info.Alert is { ShowsOnConfidence: true } alert && info.AlertVisible && !layout.HasStageMessageObject())
            foreach (var item in AlertBanner.Items(LinkedText.ResolvedAlertMessage(alert.Message, info, now), canvas, "stage"))
                scene.AddItem(item, LayerKind.Alerts);
        return scene;
    }

    /// <summary>The layout the Stage panel's preview shows: its chosen stage screen's, else the first stage screen's, else the built-in one.</summary>
    private ConfidenceLayout PreviewStageLayout()
    {
        var screens = StageScreens();
        var screen = screens.FirstOrDefault(s => s.Id == StagePreviewScreenId) ?? screens.FirstOrDefault();
        return screen is null ? BuiltInStageLayout : StageLayoutFor(screen.Id);
    }

    /// <summary>
    /// Draws every stage screen with its own layout (screens sharing a layout share one scene),
    /// the Stage panel's preview, recordings of a stage screen, and any operator preview showing a
    /// stage screen or a layout.
    /// </summary>
    private void RenderStage(SceneTransition transition, bool refreshOnly)
    {
        if (Outputs?.IsShowing(OutputRole.Stage) != true && StagePreview is null && !PreviewsWantStage()) return;
        var info = BuildConfidenceInfo();
        var now = DateTimeOffset.Now;
        var byLayout = new Dictionary<string, RenderScene>(StringComparer.Ordinal);
        RenderScene SceneOf(ConfidenceLayout layout)
        {
            if (!byLayout.TryGetValue(layout.Id, out var scene)) byLayout[layout.Id] = scene = StageScene(layout, info, now);
            return scene;
        }

        var perScreen = new Dictionary<Guid, AnimatedScene>();
        foreach (var screen in StageScreens()) perScreen[screen.Id] = AnimatedScene.From(SceneOf(StageLayoutFor(screen.Id)));
        var preview = SceneOf(PreviewStageLayout());
        Outputs?.PresentStage(perScreen, AnimatedScene.From(preview), refreshOnly);
        if (refreshOnly) StagePreview?.Update(preview);
        else StagePreview?.Present(preview, SceneTransition.Cut);
        ScreenFeeds?.Show(OutputRole.Stage, preview, refreshOnly ? null : SceneTransition.Cut);
        ShowStagePreviews(layoutId => Library.Get<ConfidenceLayout>(layoutId) is { } layout ? SceneOf(layout) : null, refreshOnly);
    }

    /// <summary>Clocks and timers change every second; anything else only changes when a cue fires.</summary>
    private bool NeedsTicking() =>
        Outputs?.IsShowing(OutputRole.Stage) == true || StagePreview is not null || PreviewsWantStage()
        || Show.Live?.Slide.Objects.Any(o => o.TextLink is { } link && LinkedText.IsTimeVarying(link)) == true
        || Show.LiveOverlays.Any(o => o.Objects.Any(x => x.TextLink is { } link && LinkedText.IsTimeVarying(link)))
        || Show.Live?.Theme?.Slides?.Any(s => s.Objects.Any(x => x.TextLink is { } link && LinkedText.IsTimeVarying(link))) == true
        // A look a screen shows can carry its own clock or timer.
        || Show.Live is not null && Show.SlideThemeOverrides.Any(t => t.Slides?.Any(s => s.Objects.Any(x => x.TextLink is { } link && LinkedText.IsTimeVarying(link))) == true)
        // An alert counting a timer ("{Sermon} left") keeps counting on the audience too.
        || Show.LiveAlert is { ShowsOnAudience: true } alert && LinkedText.AlertMessageHasTimerToken(alert.Message, Timers.Timers);
}
