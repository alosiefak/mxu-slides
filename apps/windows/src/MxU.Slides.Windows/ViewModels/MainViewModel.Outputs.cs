using System.Windows;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Outputs;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.Outputs;
using MxU.Slides.Windows.Rendering;
using MxU.Slides.Windows.Views;
using AnimatedScene = MxU.Slides.Core.Animation.AnimatedScene;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>One screen in the Screens panel: its name and job, what carries it, and whether it's on air.</summary>
public sealed record ScreenRow(Guid Id, string Name, ScreenRole Role, string Carrier, bool IsLive);

/// <summary>
/// The operator's view of the outputs: the output preset ("look") switcher, each screen's
/// Live/Off, and the previews: the live panel's (pointed at any screen or stage layout) and
/// any number of pop-out preview windows for a second monitor. Port of the Mac's
/// OutputPresetChip.swift, Modules/OutputsModule.swift and the preview half of LivePanel.swift.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Raised when the screens, their Live/Off, the active preset or the preview's target change: the panels redraw.</summary>
    public event Action? OutputsChanged;

    private OutputManager? operatorWiredTo;

    /// <summary>Once the outputs exist: keep the panels in step with the screen setup and the outputs' state.</summary>
    private void WireOperatorOutputs()
    {
        if (Outputs is not { } outputs || ReferenceEquals(operatorWiredTo, outputs)) return;
        operatorWiredTo = outputs;
        outputs.StatusChanged += () => OutputsChanged?.Invoke();
        outputs.Config.ConfigurationChanged += () => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            // A screen added, removed or given the stage job: the Stage panel and the stage outputs follow.
            OutputsChanged?.Invoke();
            StageLayoutsChanged?.Invoke();
            RenderStage(SceneTransition.Cut, refreshOnly: false);
        });
        // The screens exist now: read (or make) each stage screen's layout.
        StageAssignments();
        StageLayoutsChanged?.Invoke();
        OutputsChanged?.Invoke();
    }

    // MARK: Output presets (the chip and the Screens panel's menu)

    public List<OutputPresetMenu.Choice> OutputPresetChoices() => OutputPresetMenu.Choices(Library.All<OutputPreset>());

    public string? ActiveOutputPresetId => Settings.ActiveOutputPresetId;

    public string ActiveOutputPresetName => OutputPresetMenu.ActiveName(Library.All<OutputPreset>(), Settings.ActiveOutputPresetId);

    /// <summary>Switches the look ("" or null: no preset, every layer everywhere). Looks it drops play their Outs first.</summary>
    public void SwitchOutputPreset(string? presetId)
    {
        ActivateOutputPreset(string.IsNullOrEmpty(presetId) ? null : Library.Get<OutputPreset>(presetId));
        OutputsChanged?.Invoke();
    }

    // MARK: Screens (Live/Off, one by one)

    public List<ScreenRow> ScreenRows() => Outputs is not { } outputs
        ? []
        : [.. outputs.Config.Screens.Select(s => new ScreenRow(s.Id, s.Name, outputs.Config.Role(s.Id), outputs.ScreenCarrier(s.Id), outputs.IsScreenLive(s.Id)))];

    /// <summary>Takes one screen on or off air; the others stay as they are.</summary>
    public void SetScreenLive(Guid screenId, bool live)
    {
        if (Outputs is not { } outputs) return;
        outputs.SetScreenLive(screenId, live);
        RenderOutputs(SceneTransition.Cut);
        OutputsChanged?.Invoke();
    }

    // MARK: Previews

    // The pop-out preview windows open now, each showing its own target.
    private readonly List<PreviewPopOutWindow> popOuts = [];

    /// <summary>What the preview picker offers: every screen, then every stage layout.</summary>
    public (List<PreviewTargets.Choice> Screens, List<PreviewTargets.Choice> Layouts) PreviewChoices() =>
        (Outputs is { } outputs ? PreviewTargets.Screens(outputs.Config) : [],
         [.. StageLayouts.Select(l => new PreviewTargets.Choice(PreviewTargets.LayoutTarget(l.Id), l.Name))]);

    /// <summary>What the live panel's preview shows: the stored pick, else the first screen (null before the outputs exist).</summary>
    public PreviewTargets.Choice? PreviewTarget
    {
        get
        {
            var (screens, layouts) = PreviewChoices();
            return PreviewTargets.Resolve(Settings.PreviewTarget, screens, layouts);
        }
    }

    /// <summary>Points the live panel's preview at a screen or a stage layout (remembered for next time).</summary>
    public void SetPreviewTarget(string target)
    {
        if (Settings.PreviewTarget == target) return;
        Settings.PreviewTarget = target;
        Settings.Save();
        if (AudiencePreview is { } view) ShowTargetNow(view, PreviewTarget?.Id);
        OutputsChanged?.Invoke();
    }

    /// <summary>Opens a preview of a screen or layout in its own window (null: what the live panel shows).</summary>
    public void OpenPreviewWindow(string? target, Window? owner)
    {
        var (screens, layouts) = PreviewChoices();
        var choice = target is null ? PreviewTarget : screens.Concat(layouts).FirstOrDefault(c => c.Id == target);
        var window = new PreviewPopOutWindow(this, choice?.Id, choice?.Name ?? "Program", owner);
        popOuts.Add(window);
        window.Closed += (_, _) => popOuts.Remove(window);
        window.Show();
        ShowTargetNow(window.View, choice?.Id);
    }

    /// <summary>The shape a preview window opens at (a tall layout opens tall).</summary>
    public double PreviewAspect(string? target) => Outputs is { } outputs
        ? PreviewTargets.Aspect(target, outputs.Config, StageLayouts.ToDictionary(l => l.Id, l => PreviewTargets.Aspect(l.CanvasWidth, l.CanvasHeight)))
        : 16.0 / 9.0;

    private enum PreviewKind { Program, AudienceScreen, StageScreen, Layout }

    private (PreviewKind Kind, Guid? Screen, string? Layout) Classify(string? target)
    {
        if (target is null || Outputs is not { } outputs) return (PreviewKind.Program, null, null);
        if (PreviewTargets.LayoutId(target) is { } layout) return (PreviewKind.Layout, null, layout);
        if (SwiftUuid.Parse(target) is { } id && outputs.Config.Screen(id) is not null)
            return (outputs.Config.Feed(target).Kind == OutputFeedKind.Confidence ? PreviewKind.StageScreen : PreviewKind.AudienceScreen, id, null);
        return (PreviewKind.Program, null, null);
    }

    private IEnumerable<(SceneView View, string? Target)> PreviewViews()
    {
        if (AudiencePreview is { } main) yield return (main, PreviewTarget?.Id);
        foreach (var popOut in popOuts) yield return (popOut.View, popOut.Target);
    }

    /// <summary>Stage screens share the stage's video players, everything else the audience's, so a preview's video is in step with its screen.</summary>
    private static void UseScope(SceneView view, PreviewKind kind)
    {
        var scope = kind is PreviewKind.StageScreen or PreviewKind.Layout ? "stage" : "live";
        if (view.PlaybackScope == scope) return;
        view.PlaybackScope = scope;
        view.Present((AnimatedScene?)null, SceneTransition.Cut);
    }

    /// <summary>Called with every new audience scene (RenderOutputs): previews of the program or an audience screen follow it.</summary>
    private string? shownPresetId;

    private void ShowPreviews(OutputRole role, AnimatedScene audience, SceneTransition push)
    {
        WireOperatorOutputs();
        // A slide action, a combo, MIDI or the Local API can switch the look too: the chips follow.
        if (Settings.ActiveOutputPresetId != shownPresetId)
        {
            shownPresetId = Settings.ActiveOutputPresetId;
            OutputsChanged?.Invoke();
        }
        if (role != OutputRole.Audience) return;
        foreach (var (view, target) in PreviewViews())
        {
            var (kind, screen, _) = Classify(target);
            if (kind is PreviewKind.StageScreen or PreviewKind.Layout) continue;
            UseScope(view, kind);
            // A screen's preview shows exactly what that screen shows: its look and its layers.
            view.Present(screen is { } id ? Outputs?.Picture(id) ?? audience : audience, push);
        }
    }

    /// <summary>Some preview shows a stage screen or a layout (the stage must be drawn even with no stage output on).</summary>
    private bool PreviewsWantStage() => PreviewViews().Any(p => Classify(p.Target).Kind is PreviewKind.StageScreen or PreviewKind.Layout);

    /// <summary>Called with every new stage drawing (RenderStage): previews of a stage screen or a layout follow it.</summary>
    private void ShowStagePreviews(Func<string, RenderScene?> layoutScene, bool refreshOnly)
    {
        foreach (var (view, target) in PreviewViews())
        {
            var (kind, screen, layout) = Classify(target);
            AnimatedScene? scene = kind switch
            {
                PreviewKind.StageScreen => Outputs?.Picture(screen!.Value),
                PreviewKind.Layout => layoutScene(layout!) is { } drawn ? AnimatedScene.From(drawn) : null,
                _ => null,
            };
            if (kind is not (PreviewKind.StageScreen or PreviewKind.Layout)) continue;
            UseScope(view, kind);
            if (refreshOnly && scene is not null) view.Update(scene);
            else view.Present(scene, SceneTransition.Cut);
        }
    }

    /// <summary>A preview just pointed somewhere new: draw it there now, without waiting for the next cue.</summary>
    private void ShowTargetNow(SceneView view, string? target)
    {
        var (kind, screen, _) = Classify(target);
        UseScope(view, kind);
        if (kind is PreviewKind.StageScreen or PreviewKind.Layout)
        {
            // Drawing the stage draws every stage preview, this one included.
            RenderStage(SceneTransition.Cut, refreshOnly: false);
        }
        else
        {
            var program = Show.AnimatedScene(resolveLinkedText: ResolveLinkedText);
            view.Present(screen is { } id ? Outputs?.Picture(id) ?? program : program, SceneTransition.Cut);
        }
        EnsureTicking();
    }
}
