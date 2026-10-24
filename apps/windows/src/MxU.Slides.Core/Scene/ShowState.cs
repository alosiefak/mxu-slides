using System.Diagnostics;
using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Stage;

namespace MxU.Slides.Core.Scene;

/// <summary>The live show's independent "functions", each clearable on its own (F1–F6).</summary>
public enum ShowFunction { Slides, Media, Overlays, Audio, Alerts, Inputs }

/// <summary>
/// What is live on the audience outputs: the slide, media on each media layer,
/// overlays and the alert, plus where the slide's animation builds are (clicks
/// used, exit pressed, a clear waiting for its Outs). Pure state; the app fires
/// cues into it and renders <see cref="AnimatedScene"/>. Port of
/// SlideScene/ShowState.swift.
/// <para>
/// Looks: an output can show the live slide through a different theme (a lower third
/// on the stream while the projector shows the full-screen lyric). The app lists the
/// looks some output shows in <see cref="SlideThemeOverrides"/> and builds one scene
/// per look with <c>AnimatedScene(slideThemeOverride: look)</c>. Every look shares the
/// slide's clock, so a click plays the builds of every look at once.
/// </para>
/// </summary>
public sealed class ShowState
{
    /// <param name="FiredAtHostTime">The <see cref="HostClock"/> time it went live: the anchor its animations play from.</param>
    public sealed record LiveSlide(Slide Slide, Presentation? Presentation, Theme? Theme, string? ArrangementId, DateTimeOffset FiredAt,
        double FiredAtHostTime = 0);

    /// <summary>Monotonic seconds shared by the show and the renderer; animations are timed against it. Tests replace it.</summary>
    public static Func<double> HostClock { get; set; } = () => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private readonly Dictionary<LayerKind, CueMedia> liveMedia = [];
    private readonly HashSet<LayerKind> directMediaFires = [];
    private readonly List<Overlay> liveOverlays = [];
    private readonly Dictionary<string, double> overlayFiredAt = [];
    private readonly Dictionary<string, double> overlayDismissAt = [];
    private readonly List<double> slideAdvanceClicks = [];
    private double? slideExitAt;
    private double? slideDismissAt;
    private bool slideArrivedSettled;

    // The slide that was live before this one, if it was still up (not cleared, not
    // exited): a look whose design didn't change "holds" across the two (see HoldsLook).
    private LiveSlide? heldFrom;
    private int heldFromClicks;
    private HashSet<string> heldFromDismissedLooks = [];

    // Looks taken down early (their Outs playing) while the slide stays live elsewhere, by LookKey.
    private readonly Dictionary<string, double> lookDismissAt = new(StringComparer.Ordinal);

    public LiveSlide? Live { get; private set; }
    public LiveSlide? Last { get; private set; }
    public IReadOnlyDictionary<LayerKind, CueMedia> LiveMedia => liveMedia;
    public IReadOnlyList<Overlay> LiveOverlays => liveOverlays;

    /// <summary>Raised after any change, so outputs can re-render.</summary>
    public event Action? Changed;

    public bool IsEmpty => Live is null && liveMedia.Count == 0 && liveOverlays.Count == 0 && !HasAlert;

    /// <param name="arrivesSettled">Show the slide with its automatic builds already played (a "cut" advance).</param>
    public void Fire(Slide slide, Presentation? presentation = null, Theme? theme = null, string? arrangementId = null,
        DateTimeOffset? at = null, double? atHostTime = null, bool arrivesSettled = false)
    {
        if (Live is { } outgoing && outgoing.Slide.Id != slide.Id)
        {
            Last = outgoing;
            // Remember the outgoing slide so a look that keeps its design can hold (no rebuild).
            heldFrom = slideDismissAt is null && slideExitAt is null ? outgoing : null;
            heldFromClicks = slideAdvanceClicks.Count;
            heldFromDismissedLooks = [.. lookDismissAt.Keys];
        }
        else
        {
            heldFrom = null;
            heldFromClicks = 0;
            heldFromDismissedLooks = [];
        }
        lookDismissAt.Clear();
        Live = new LiveSlide(slide, presentation, theme, arrangementId, at ?? DateTimeOffset.Now, atHostTime ?? HostClock());
        slideAdvanceClicks.Clear();
        slideExitAt = null;
        slideDismissAt = null;
        slideArrivedSettled = arrivesSettled;

        var background = SlideSceneBuilder.EffectiveBackground(slide, presentation, arrangementId);
        LayerKind? backgroundLayer = background is null ? null : SlideSceneBuilder.LayerKindOf(background.Layer);

        // A new slide sweeps foreground media off unless it brings its own.
        foreach (var layer in new[] { LayerKind.Videos, LayerKind.StillGraphics })
        {
            if (backgroundLayer == layer) continue;
            if (liveMedia.TryGetValue(layer, out var live) && SweepsOnSlideFire(live, layer)
                && !(layer == LayerKind.StillGraphics && (slide.Actions ?? []).Any(a => a.Kind == SlideActionKind.FireMedia && a.MediaId == live.MediaId)))
            {
                liveMedia.Remove(layer);
                directMediaFires.Remove(layer);
            }
        }
        if (background is not null && backgroundLayer is { } targetLayer
            && !(liveMedia.TryGetValue(targetLayer, out var current) && current.MediaId == background.MediaId))
        {
            liveMedia[targetLayer] = background;
            directMediaFires.Remove(targetLayer);
        }
        Changed?.Invoke();
    }

    private bool SweepsOnSlideFire(CueMedia cue, LayerKind layer) =>
        cue.Classification is null && layer == LayerKind.StillGraphics && directMediaFires.Contains(layer)
        || cue.ResolvedClassification() == MediaClassification.Foreground;

    /// <summary>Keeps the live slide's presentation current while it is edited, so the next fire uses the edit.</summary>
    public void PresentationEdited(Presentation presentation)
    {
        var changed = false;
        if (Live?.Presentation?.Id == presentation.Id)
        {
            var updated = presentation.Slides.FirstOrDefault(s => s.Id == Live.Slide.Id) ?? Live.Slide;
            Live = Live with { Presentation = presentation, Slide = updated };
            changed = true;
        }
        if (Last?.Presentation?.Id == presentation.Id) Last = Last with { Presentation = presentation };
        if (changed) Changed?.Invoke();
    }

    public void ThemeEdited(Theme theme)
    {
        if (Live?.Theme?.Id != theme.Id) return;
        Live = Live with { Theme = theme };
        Changed?.Invoke();
    }

    public void Fire(CueMedia media, LayerKind layer)
    {
        if (liveMedia.TryGetValue(layer, out var current) && current.MediaId == media.MediaId) return;
        liveMedia[layer] = media;
        directMediaFires.Add(layer);
        Changed?.Invoke();
    }

    public void Fire(Overlay overlay, double? atHostTime = null)
    {
        var index = liveOverlays.FindIndex(o => o.Id == overlay.Id);
        if (index >= 0) liveOverlays[index] = overlay;
        else liveOverlays.Add(overlay);
        overlayFiredAt[overlay.Id] = atHostTime ?? HostClock();
        overlayDismissAt.Remove(overlay.Id);
        Changed?.Invoke();
    }

    /// <summary>Swaps in an edited copy of a live overlay (typed fields) without replaying its entrance.</summary>
    public void OverlayEdited(Overlay overlay)
    {
        var index = liveOverlays.FindIndex(o => o.Id == overlay.Id);
        if (index < 0) return;
        liveOverlays[index] = overlay;
        Changed?.Invoke();
    }

    /// <summary>
    /// Takes an overlay down. Given a time, an overlay with Out animations plays them
    /// first and <see cref="SweepFinishedExits"/> removes it; dismissing it again while it leaves cuts it.
    /// </summary>
    public void DismissOverlay(string id, double? atHostTime = null)
    {
        if (DismissOverlayQuiet(id, atHostTime)) Changed?.Invoke();
    }

    private bool DismissOverlayQuiet(string id, double? atHostTime)
    {
        if (atHostTime is { } at && !overlayDismissAt.ContainsKey(id)
            && liveOverlays.FirstOrDefault(o => o.Id == id) is { } overlay
            && AnimationSequence.ExitDuration(overlay.Objects, overlay.AnimationOrder) > 0)
        {
            overlayDismissAt[id] = at;
            return true;
        }
        overlayFiredAt.Remove(id);
        overlayDismissAt.Remove(id);
        return liveOverlays.RemoveAll(o => o.Id == id) > 0;
    }

    /// <summary>True while an overlay plays its Outs on the way off.</summary>
    public bool IsLeaving(string overlayId) => overlayDismissAt.ContainsKey(overlayId);

    // MARK: Looks (theme overrides per output)

    /// <summary>
    /// The looks some output shows the live slide through, besides the slide's own theme
    /// (sorted by look key, as the Mac sorts them). Clicks, exits and clears count the
    /// builds of every look, so the slide only moves on when every output has played its.
    /// </summary>
    public IReadOnlyList<Theme> SlideThemeOverrides { get; set; } = [];

    /// <summary>How text pages turn: the way the slide transition looks (<see cref="Paging.Turn.Following"/>).</summary>
    public Paging.Turn PageTurn { get; set; } = Paging.Turn.Cut;

    /// <summary>
    /// Measures text, so a long lyric on a "page on click" placeholder becomes click pages.
    /// The app supplies its text layout here; without one nothing pages (the text shrinks to fit instead).
    /// </summary>
    public TextPager? Pager { get; set; }

    /// <summary>Looks taken down early with <see cref="DismissLooks"/>, by <see cref="LookKey"/> (their Outs are playing).</summary>
    public IReadOnlyDictionary<string, double> LookDismissAt => lookDismissAt;

    /// <summary>When the live slide's clear was pressed (its Outs are playing), or null.</summary>
    public double? SlideDismissAt => slideDismissAt;

    /// <summary>When the live slide's exit group was pressed, or null.</summary>
    public double? SlideExitAt => slideExitAt;

    /// <summary>How long a held look's words fade in over the design that stays up.</summary>
    public const double HoldFadeSeconds = 0.3;

    /// <summary>The design a slide uses through a look (its override design for that theme, else by name).</summary>
    private static string? TemplateId(Slide slide, Theme theme) =>
        SlideSceneBuilder.ThemeSlide(PlaceholderAnimations.RenderedThroughOverride(slide, theme), theme)?.Id;

    /// <summary>Two slides use the same design through a look (an empty slide never matches: it takes the look down).</summary>
    private static bool SameTemplate(Slide a, Slide b, Theme theme) =>
        a.Objects.Count > 0 && b.Objects.Count > 0 && TemplateId(a, theme) == TemplateId(b, theme);

    /// <summary>The same look: the same theme scoped to the same designs.</summary>
    public static bool SameLook(Theme a, Theme b) =>
        a.Id == b.Id && (a.Slides is null) == (b.Slides is null)
        && (a.Slides ?? []).Select(s => s.Id).SequenceEqual((b.Slides ?? []).Select(s => s.Id));

    /// <summary>A look's identity: its theme id and the designs it is scoped to.</summary>
    public static string LookKey(Theme theme) =>
        theme.Id + "|" + (theme.Slides is { } slides ? string.Join(",", slides.Select(s => s.Id)) : "");

    /// <summary>
    /// True when the live slide arrived on a look that was already up with the same design
    /// (verse 2 after verse 1 on the stream's lower third): that output keeps its plate and
    /// bands where they are and only fades the new words in, instead of building in again.
    /// A look that was taken down, cleared or exited, or whose Outs were all clicked off, doesn't hold.
    /// </summary>
    public bool HoldsLook(Theme theme) =>
        Live is { } live && heldFrom is { } previous
        && !heldFromDismissedLooks.Contains(LookKey(theme))
        && LookStands(previous, theme, heldFromClicks)
        && SameTemplate(previous.Slide, live.Slide, theme);

    // A look is still standing unless its Outs have all been clicked off already.
    private bool LookStands(LiveSlide live, Theme theme, int consumedClicks)
    {
        var composition = PlaceholderAnimations.StackComposition(PlaceholderAnimations.RenderedThroughOverride(live.Slide, theme), theme, Pager);
        var hasOuts = AnimationSequence.SceneSteps(composition.Objects, composition.Order).Values
            .SelectMany(steps => steps).Any(step => step.Kind == SceneAnimationKind.Exit);
        return !hasOuts || LookExitDuration(live, theme, consumedClicks) > 0;
    }

    private double LookExitDuration(LiveSlide live, Theme theme, int consumedClicks)
    {
        var composition = PlaceholderAnimations.StackComposition(PlaceholderAnimations.RenderedThroughOverride(live.Slide, theme), theme, Pager);
        return AnimationSequence.ExitDuration(composition.Objects, composition.Order, consumedClicks);
    }

    /// <summary>
    /// Before <paramref name="slide"/> fires with <paramref name="incomingOverrides"/> as the looks then
    /// shown: the looks that must play their Outs first (the look goes away, changes design, or the new
    /// slide is blank) and how long to wait for them. Nothing waits when nothing is live, the slide is
    /// already leaving, or it is a re-fire of the live slide.
    /// </summary>
    public (double Wait, IReadOnlyList<Theme> Leaving) Handoff(Slide slide, IReadOnlyList<Theme> incomingOverrides)
    {
        if (Live is not { } live || slideDismissAt is not null || slideExitAt is not null || live.Slide.Id == slide.Id) return (0, []);
        var leaving = new List<Theme>();
        var wait = 0.0;
        foreach (var theme in SlideThemeOverrides.Where(t => !lookDismissAt.ContainsKey(LookKey(t))))
        {
            var stays = incomingOverrides.Any(t => SameLook(t, theme));
            if (stays && SameTemplate(live.Slide, slide, theme)) continue;
            var outDuration = LookExitDuration(live, theme, slideAdvanceClicks.Count);
            if (outDuration <= 0) continue;
            leaving.Add(theme);
            wait = Math.Max(wait, outDuration);
        }
        return (wait, leaving);
    }

    public double HandoffWait(Slide slide, IReadOnlyList<Theme> incomingOverrides) => Handoff(slide, incomingOverrides).Wait;

    /// <summary>Switching to <paramref name="incomingOverrides"/> (an output preset change) while the slide stays live: the looks that go, and how long their Outs take.</summary>
    public (double Wait, IReadOnlyList<Theme> Leaving) LookSwitch(IReadOnlyList<Theme> incomingOverrides)
    {
        if (Live is not { } live || slideDismissAt is not null || slideExitAt is not null) return (0, []);
        var leaving = new List<Theme>();
        var wait = 0.0;
        foreach (var theme in SlideThemeOverrides.Where(t => !lookDismissAt.ContainsKey(LookKey(t))))
        {
            if (incomingOverrides.Any(t => SameLook(t, theme))) continue;
            var outDuration = LookExitDuration(live, theme, slideAdvanceClicks.Count);
            if (outDuration <= 0) continue;
            leaving.Add(theme);
            wait = Math.Max(wait, outDuration);
        }
        return (wait, leaving);
    }

    /// <summary>Plays these looks' Outs now, while the slide stays live on the other outputs (a hand-off or a look switch).</summary>
    public void DismissLooks(IEnumerable<Theme> themes, double atHostTime)
    {
        if (Live is null) return;
        foreach (var theme in themes) lookDismissAt[LookKey(theme)] = atHostTime;
        Changed?.Invoke();
    }

    /// <summary>A held look's entrances become a quick fade in place (its Outs stay the design's).</summary>
    private static AnimationStep HoldFade(AnimationStep step)
    {
        if (step.Kind != AnimationKind.In || step.Trigger is not (AnimationTrigger.WithPrevious or AnimationTrigger.AfterPrevious)) return step;
        var fade = MxuJson.Clone(step);
        fade.Animation = StepAnimation.Fade;
        fade.Trigger = AnimationTrigger.WithPrevious;
        fade.DelaySeconds = null;
        fade.DurationSeconds = HoldFadeSeconds;
        fade.Edge = null;
        fade.OffsetX = null;
        fade.OffsetY = null;
        fade.FromScale = null;
        fade.WithFade = null;
        return fade;
    }

    /// <summary>The object's steps all came from its placeholder (every id is <c>objectId::stepId</c>).</summary>
    private static bool IsDonated(SlideObject o) =>
        o.AnimationSteps is { Count: > 0 } steps && steps.All(s => s.Id.Contains("::", StringComparison.Ordinal));

    // MARK: Animation builds (clicks)

    /// <summary>One composition per look: the slide's own theme first, then each override.</summary>
    private List<PlaceholderAnimations.Composition> Compositions(LiveSlide live) =>
        PlaceholderAnimations.Compositions(live.Slide, [live.Theme, .. SlideThemeOverrides], Pager);

    /// <summary>Click groups on the live slide.</summary>
    public int SlideClickCount => Live is { } live
        ? Compositions(live).Select(c => AnimationSequence.ClickCount(c.Objects, c.Order)).DefaultIfEmpty(0).Max()
        : 0;

    public bool SlideHasExitGroup => Live is { } live && Compositions(live).Any(c => AnimationSequence.HasExitGroup(c.Objects, c.Order));

    /// <summary>Presses the live slide takes before Next moves on: its click groups, plus one for an exit group.</summary>
    public int SlideAdvanceCount => SlideClickCount + (SlideHasExitGroup ? 1 : 0);

    /// <summary>Presses used and available on the live slide; null when it has no builds.</summary>
    public (int Consumed, int Total)? SlideAnimationStep
    {
        get
        {
            var total = SlideAdvanceCount;
            if (total == 0) return null;
            var consumed = Math.Min(slideAdvanceClicks.Count, SlideClickCount) + (slideExitAt is not null ? 1 : 0);
            return (Math.Min(consumed, total), total);
        }
    }

    /// <summary>
    /// Next on a slide with builds: plays its next click group, then its exit group.
    /// False when nothing is left, so the caller moves on to the next slide.
    /// </summary>
    public bool AdvanceStep(double? atHostTime = null)
    {
        if (Live is null || slideDismissAt is not null) return false;
        var at = atHostTime ?? HostClock();
        if (slideAdvanceClicks.Count < SlideClickCount)
        {
            slideAdvanceClicks.Add(at);
            Changed?.Invoke();
            return true;
        }
        if (SlideHasExitGroup && slideExitAt is null)
        {
            slideExitAt = at;
            Changed?.Invoke();
            return true;
        }
        return false;
    }

    /// <summary>Previous on a slide with builds: takes back the exit press, then the last click. False when there's nothing to take back.</summary>
    public bool UnadvanceStep()
    {
        if (slideExitAt is not null)
        {
            slideExitAt = null;
            Changed?.Invoke();
            return true;
        }
        if (slideAdvanceClicks.Count == 0) return false;
        slideAdvanceClicks.RemoveAt(slideAdvanceClicks.Count - 1);
        Changed?.Invoke();
        return true;
    }

    /// <summary>The live slide's animation clock; null when nothing on it animates.</summary>
    public AnimationContext? SlideAnimationContext
    {
        get
        {
            if (Live is not { } live || !Compositions(live).Any(c => AnimationSequence.HasAnimationSteps(c.Objects))) return null;
            return new AnimationContext(live.FiredAtHostTime, [.. slideAdvanceClicks],
                dismissHostTime: slideDismissAt, exitHostTime: slideExitAt, autoSettled: slideArrivedSettled);
        }
    }

    /// <summary>The text the next click reveals, for the stage display.</summary>
    public string? UpcomingRevealText() => Live is { } live
        ? Compositions(live).Select(c => AnimationSequence.UpcomingRevealText(c.Objects, c.Order, slideAdvanceClicks.Count)).FirstOrDefault(t => t is not null)
        : null;

    private double SlideExitDuration(LiveSlide live) =>
        Compositions(live).Select(c => AnimationSequence.ExitDuration(c.Objects, c.Order, slideAdvanceClicks.Count)).DefaultIfEmpty(0).Max();

    /// <summary>A slide, overlay or alert is playing its Outs before being removed.</summary>
    public bool HasPendingExits => slideDismissAt is not null || overlayDismissAt.Count > 0 || alertDismissAt is not null;

    /// <summary>Removes whatever has finished leaving. Call on a timer while <see cref="HasPendingExits"/>; true when something went.</summary>
    public bool SweepFinishedExits(double? now = null)
    {
        var t = now ?? HostClock();
        var changed = false;
        if (slideDismissAt is { } dismissAt && Live is { } live && t >= dismissAt + SlideExitDuration(live))
        {
            RemoveSlide();
            changed = true;
        }
        foreach (var (id, at) in overlayDismissAt.ToList())
        {
            if (liveOverlays.FirstOrDefault(o => o.Id == id) is not { } overlay)
            {
                overlayDismissAt.Remove(id);
                continue;
            }
            if (t < at + AnimationSequence.ExitDuration(overlay.Objects, overlay.AnimationOrder)) continue;
            liveOverlays.Remove(overlay);
            overlayFiredAt.Remove(id);
            overlayDismissAt.Remove(id);
            changed = true;
        }
        if (alertDismissAt is { } alertAt && (LiveAlert is null || t >= alertAt + AlertSceneBuilder.ExitDuration(LiveAlert)))
        {
            RemoveAlert();
            changed = true;
        }
        if (changed) Changed?.Invoke();
        return changed;
    }

    // MARK: Alerts

    private double alertFiredAt;
    private double? alertDismissAt;

    /// <summary>The alert up now (one at a time: firing another replaces it), with its target, behaviour, layer and theme.</summary>
    public CueAlert? LiveAlert { get; private set; }

    /// <summary>
    /// False on a Flash alert's off beat. The app blinks it twice a second (the Mac keeps this
    /// as ServiceControls.alertVisible); a new alert always starts visible.
    /// </summary>
    public bool AlertVisible { get; set; } = true;

    /// <summary>Fills "{Timer Name}" slots in an audience alert with live digits. Set by the app, which knows the timers.</summary>
    public Func<string, string>? AlertMessageResolver { get; set; }

    public string? AlertMessage => LiveAlert?.Message;
    public bool AlertOnAudience => LiveAlert?.ShowsOnAudience == true;
    public bool AlertOnStage => LiveAlert?.ShowsOnConfidence == true;
    public bool HasAlert => LiveAlert is not null;

    /// <summary>True while the alert's design plays its Outs on the way down.</summary>
    public bool AlertIsLeaving => alertDismissAt is not null;

    /// <param name="atHostTime">When it went up: the clock its design's animations play from.</param>
    public void FireAlert(CueAlert alert, double? atHostTime = null)
    {
        LiveAlert = alert;
        alertFiredAt = atHostTime ?? HostClock();
        alertDismissAt = null;
        AlertVisible = true;
        Changed?.Invoke();
    }

    /// <summary>A plain message that holds steady (no saved alert): on the stage, the audience or both.</summary>
    public void FireAlert(string message, bool audience, bool stage) => FireAlert(new CueAlert(message, AlertBehavior.Persist,
        audience && stage ? AlertTarget.Both : audience ? AlertTarget.Audience : AlertTarget.Confidence));

    public void ClearAlert()
    {
        if (LiveAlert is null) return;
        RemoveAlert();
        Changed?.Invoke();
    }

    /// <summary>Given a time, a themed alert with Outs plays them first and <see cref="SweepFinishedExits"/> removes it; asked again, it cuts.</summary>
    private void ClearAlert(double? atHostTime)
    {
        if (atHostTime is { } at && alertDismissAt is null && LiveAlert is { } alert && AlertSceneBuilder.ExitDuration(alert) > 0)
        {
            alertDismissAt = at;
            return;
        }
        RemoveAlert();
    }

    private void RemoveAlert()
    {
        LiveAlert = null;
        alertDismissAt = null;
    }

    // MARK: Clearing

    /// <param name="atHostTime">Given, a slide or overlays with Out animations play them before going (F2, F4); otherwise they cut.</param>
    public void Clear(ShowFunction function, double? atHostTime = null)
    {
        switch (function)
        {
            case ShowFunction.Slides:
                ClearSlide(atHostTime);
                break;
            case ShowFunction.Media:
            case ShowFunction.Inputs:
                foreach (var (layer, cue) in liveMedia.ToList())
                {
                    if ((cue.IsLiveInput() ? ShowFunction.Inputs : ShowFunction.Media) != function) continue;
                    liveMedia.Remove(layer);
                    directMediaFires.Remove(layer);
                }
                break;
            case ShowFunction.Overlays:
                foreach (var overlay in liveOverlays.ToList()) DismissOverlayQuiet(overlay.Id, atHostTime);
                break;
            case ShowFunction.Alerts:
                ClearAlert(atHostTime);
                break;
            case ShowFunction.Audio:
                break;
        }
        Changed?.Invoke();
    }

    public void Clear(LayerKind layer, double? atHostTime = null)
    {
        switch (layer)
        {
            case LayerKind.Slide:
                ClearSlide(atHostTime);
                break;
            case LayerKind.Alerts:
            case LayerKind.Overlays:
                break;
            default:
                liveMedia.Remove(layer);
                directMediaFires.Remove(layer);
                break;
        }
        // Overlays live on whichever layer they name (Overlays unless set), and so does the alert (Alerts unless set).
        foreach (var overlay in liveOverlays.Where(o => (LayerKinds.Parse(o.Layer) ?? LayerKind.Overlays) == layer).ToList())
            DismissOverlayQuiet(overlay.Id, atHostTime);
        if (LiveAlert is { } alert && (LayerKinds.Parse(alert.Layer) ?? LayerKind.Alerts) == layer) ClearAlert(atHostTime);
        Changed?.Invoke();
    }

    /// <summary>Clears every function (or the ones given) at once, cutting rather than playing Outs.</summary>
    public void ClearAll(ISet<ShowFunction>? sweeping = null)
    {
        foreach (var function in Enum.GetValues<ShowFunction>())
            if (sweeping is null || sweeping.Contains(function)) ClearQuiet(function);
        Changed?.Invoke();
    }

    private void ClearQuiet(ShowFunction function)
    {
        var handler = Changed;
        Changed = null;
        try { Clear(function); }
        finally { Changed = handler; }
    }

    private void ClearSlide(double? atHostTime)
    {
        if (slideExitAt is null && atHostTime is { } at && slideDismissAt is null && Live is { } live && SlideExitDuration(live) > 0)
        {
            slideDismissAt = at;
            return;
        }
        RemoveSlide();
    }

    private void RemoveSlide()
    {
        if (Live is not null) Last = Live;
        Live = null;
        heldFrom = null;
        heldFromClicks = 0;
        heldFromDismissedLooks = [];
        lookDismissAt.Clear();
        slideAdvanceClicks.Clear();
        slideExitAt = null;
        slideDismissAt = null;
        slideArrivedSettled = false;
    }

    // MARK: Rendering

    /// <summary>The audience scene with everything at rest (no animation), for pictures, checks and text.</summary>
    public RenderScene Scene(SceneSize? canvasSize = null, Func<IReadOnlyList<SlideObject>, List<SlideObject>>? resolveLinkedText = null,
        Theme? slideThemeOverride = null)
    {
        var animated = AnimatedScene(canvasSize, resolveLinkedText, slideThemeOverride);
        var scene = new RenderScene(animated.CanvasSize) { Background = animated.Background };
        foreach (var layer in animated.Layers)
            foreach (var item in layer.Items)
                scene.AddItem(item.Render, layer.Kind);
        return scene;
    }

    /// <summary>
    /// The audience scene with every item's animation steps and clock: media layers,
    /// the live slide composed with its theme (placeholder animations donated), overlays
    /// and the alert band. <paramref name="resolveLinkedText"/> fills in clock, timer and
    /// slide-linked text objects. Port of ShowState.scene.
    /// </summary>
    /// <param name="slideThemeOverride">
    /// A look: the live slide drawn through this theme instead of its own (its text takes the look's
    /// placeholders, keeping only the emphasis the look allows), with the look's builds. Everything
    /// else (media, overlays, the alert) is the same as the plain scene.
    /// </param>
    public AnimatedScene AnimatedScene(SceneSize? canvasSize = null, Func<IReadOnlyList<SlideObject>, List<SlideObject>>? resolveLinkedText = null,
        Theme? slideThemeOverride = null)
    {
        var canvas = canvasSize ?? SlideSceneBuilder.CanvasSizeFor(Live?.Presentation);
        var scene = new AnimatedScene(canvas);
        foreach (var (layer, media) in liveMedia)
            scene.AddItem(SlideSceneBuilder.MediaItem($"live-media-{layer.Raw()}", media, canvas), layer);

        if (Live is { } live) AddSlide(scene, live, canvas, resolveLinkedText, slideThemeOverride);

        foreach (var overlay in liveOverlays)
        {
            var layer = LayerKinds.Parse(overlay.Layer) ?? LayerKind.Overlays;
            IReadOnlyList<SlideObject> objects = overlay.Objects;
            if (resolveLinkedText is not null) objects = resolveLinkedText(objects);
            var wiring = SlideSceneBuilder.MaskWiring(objects);
            var steps = AnimationSequence.SceneSteps(objects, overlay.AnimationOrder);
            var firedAt = overlayFiredAt.GetValueOrDefault(overlay.Id);
            var context = steps.Count == 0 ? null
                : new AnimationContext(firedAt, dismissHostTime: overlayDismissAt.TryGetValue(overlay.Id, out var dismiss) ? dismiss : null);
            foreach (var o in objects)
            {
                var items = SlideSceneBuilder.RenderItems(o, null, null, canvas, $"overlay-{overlay.Id}-{o.Id}",
                    wiring.MaskedBy.GetValueOrDefault(o.Id), wiring.Mattes.Contains(o.Id) ? o.Id : null);
                foreach (var item in AnimatedItems.For(o, items, steps.GetValueOrDefault(o.Id) ?? [], null, null, canvas, context, firedAt))
                    scene.AddItem(item, layer);
            }
        }

        // The alert, on its own layer, through its theme's Alerts design (or the amber band); hidden on a flash's off beat.
        if (LiveAlert is { ShowsOnAudience: true } alert && AlertVisible)
        {
            var context = AlertSceneBuilder.Animates(alert) ? new AnimationContext(alertFiredAt, dismissHostTime: alertDismissAt) : null;
            foreach (var item in AlertSceneBuilder.AudienceItems(alert, canvas, AlertMessageResolver, resolveLinkedText, context, alertFiredAt))
                scene.AddItem(item, LayerKinds.Parse(alert.Layer) ?? LayerKind.Alerts);
        }
        return scene;
    }

    /// <summary>
    /// The live slide on the slide layer, through its own theme or a look. In order, as on the Mac:
    /// linked text filled in, the slide's text restyled for the look, placeholder steps donated (a held
    /// look's entrances turned into a quick fade), long text paged, the design's stack composed, and
    /// each piece given its steps and the slide's clock (a look being taken down plays its Outs from then).
    /// </summary>
    private void AddSlide(AnimatedScene scene, LiveSlide live, SceneSize canvas,
        Func<IReadOnlyList<SlideObject>, List<SlideObject>>? resolveLinkedText, Theme? look)
    {
        if ((live.Slide.BackgroundFill ?? live.Presentation?.BackgroundFill) is { } fill
            && SlideSceneBuilder.BackdropItem(fill, canvas, $"{live.Slide.Id}-backdrop") is { } backdrop)
            scene.AddItem(backdrop, LayerKind.Slide);

        var theme = look ?? live.Theme;
        // Through a look, the slide uses the design it chose for that theme (if it chose one).
        var renderedSlide = look is null ? live.Slide : PlaceholderAnimations.RenderedThroughOverride(live.Slide, look);
        IReadOnlyList<SlideObject> objects = live.Slide.Objects;
        if (resolveLinkedText is not null) objects = resolveLinkedText(objects);
        if (look is not null) objects = PlaceholderAnimations.OverrideObjects(renderedSlide, objects, live.Theme, look);
        var template = SlideSceneBuilder.ThemeSlide(renderedSlide, theme);

        var donated = PlaceholderAnimations.Compose(objects, template, live.Slide.AnimationOrder);
        var slideObjects = donated.Objects;
        var held = look is not null && HoldsLook(look);
        if (held)
        {
            slideObjects = slideObjects.Select(o =>
            {
                if (!IsDonated(o)) return o;
                var copy = MxuJson.Clone(o);
                copy.AnimationSteps = [.. o.AnimationSteps!.Select(HoldFade)];
                return copy;
            }).ToList();
        }

        var paged = Paging.Paged(slideObjects, template, theme, donated.Order, canvas, Pager, PageTurn);
        var placeholders = SlideSceneBuilder.PlaceholderAssignments(paged.Objects, template);
        var stack = SlideSceneBuilder.ComposedStack(paged.Objects, template);
        var wiring = SlideSceneBuilder.MaskWiring(stack.Select(e => e.Object).ToList());
        var steps = AnimationSequence.SceneSteps(stack.Select(e => e.Object).ToList(), paged.Order);

        var lookContext = SlideAnimationContext;
        if (look is not null && lookContext is not null && lookDismissAt.TryGetValue(LookKey(look), out var dismissedAt))
            lookContext = lookContext with { DismissHostTime = dismissedAt };
        // A held look's design (plate, bands) counts as already built in: it stays where it is.
        var heldContext = lookContext is null ? null : lookContext with { AutoSettled = true };

        foreach (var entry in stack)
        {
            var source = entry.FromTheme && resolveLinkedText is not null && entry.Object.TextLink is not null
                ? resolveLinkedText([entry.Object])[0]
                : entry.Object;
            var placeholder = entry.FromTheme ? null : placeholders.GetValueOrDefault(entry.Object.Id);
            var items = SlideSceneBuilder.RenderItems(source, theme, placeholder, canvas, entry.FromTheme ? entry.Id : null,
                wiring.MaskedBy.GetValueOrDefault(entry.Object.Id), wiring.Mattes.Contains(entry.Object.Id) ? entry.Object.Id : null);
            var context = held && entry.FromTheme ? heldContext : lookContext;
            foreach (var item in AnimatedItems.For(source, items, steps.GetValueOrDefault(entry.Object.Id) ?? [], theme, placeholder, canvas,
                         context, live.FiredAtHostTime))
                scene.AddItem(item, LayerKind.Slide);
        }
    }
}

/// <summary>The amber message band used for alerts. Port of AlertSceneBuilder.bannerItems.</summary>
public static class AlertBanner
{
    public static readonly SceneColor Amber = new(1.0, 0.72, 0.15);

    public static IEnumerable<RenderItem> Items(string message, SceneSize canvas, string id)
    {
        var height = canvas.Height * (150.0 / 1080.0);
        var band = new SceneRect(0, canvas.Height - height, canvas.Width, height);
        var inset = canvas.Width * (40.0 / 1920.0);
        yield return new RenderItem
        {
            Id = $"alert-band-{id}",
            Frame = band,
            Content = new ItemContent.Shape(new ShapeStyle(SceneShapeKind.Rect, new SceneFill.Solid(Amber))),
        };
        yield return new RenderItem
        {
            Id = $"alert-text-{id}",
            Frame = band.Inset(inset, 0),
            Content = new ItemContent.Text(new StyledText
            {
                String = message,
                FontSize = canvas.Height * (72.0 / 1080.0),
                Color = SceneColor.Black,
                Alignment = SceneTextAlignment.Center,
                AutoShrink = true,
                MinFontSize = canvas.Height * (24.0 / 1080.0),
            }),
        };
    }
}
