using System.Diagnostics;
using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Scene;

/// <summary>The live show's independent "functions", each clearable on its own (F1–F6).</summary>
public enum ShowFunction { Slides, Media, Overlays, Audio, Alerts, Inputs }

/// <summary>
/// What is live on the audience outputs: the slide, media on each media layer,
/// overlays and the alert, plus where the slide's animation builds are (clicks
/// used, exit pressed, a clear waiting for its Outs). Pure state; the app fires
/// cues into it and renders <see cref="AnimatedScene"/>. Port of
/// SlideScene/ShowState.swift without theme-override looks.
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
        if (Live is { } outgoing && outgoing.Slide.Id != slide.Id) Last = outgoing;
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

    // MARK: Animation builds (clicks)

    private static List<PlaceholderAnimations.Composition> Compositions(LiveSlide live) =>
        PlaceholderAnimations.Compositions(live.Slide, [live.Theme]);

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

    /// <summary>A slide or overlay is playing its Outs before being removed.</summary>
    public bool HasPendingExits => slideDismissAt is not null || overlayDismissAt.Count > 0;

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
        if (changed) Changed?.Invoke();
        return changed;
    }

    // MARK: Alerts

    public string? AlertMessage { get; private set; }
    public bool AlertOnAudience { get; private set; }
    public bool AlertOnStage { get; private set; }
    public bool HasAlert => AlertMessage is not null;

    public void FireAlert(string message, bool audience, bool stage)
    {
        AlertMessage = message;
        AlertOnAudience = audience;
        AlertOnStage = stage;
        Changed?.Invoke();
    }

    public void ClearAlert()
    {
        if (AlertMessage is null) return;
        AlertMessage = null;
        Changed?.Invoke();
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
                AlertMessage = null;
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
                AlertMessage = null;
                break;
            case LayerKind.Overlays:
                break;
            default:
                liveMedia.Remove(layer);
                directMediaFires.Remove(layer);
                break;
        }
        // Overlays live on whichever layer they name (Overlays unless set).
        foreach (var overlay in liveOverlays.Where(o => (LayerKinds.Parse(o.Layer) ?? LayerKind.Overlays) == layer).ToList())
            DismissOverlayQuiet(overlay.Id, atHostTime);
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
        slideAdvanceClicks.Clear();
        slideExitAt = null;
        slideDismissAt = null;
        slideArrivedSettled = false;
    }

    // MARK: Rendering

    /// <summary>The audience scene with everything at rest (no animation), for pictures, checks and text.</summary>
    public RenderScene Scene(SceneSize? canvasSize = null, Func<IReadOnlyList<SlideObject>, List<SlideObject>>? resolveLinkedText = null)
    {
        var animated = AnimatedScene(canvasSize, resolveLinkedText);
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
    public AnimatedScene AnimatedScene(SceneSize? canvasSize = null, Func<IReadOnlyList<SlideObject>, List<SlideObject>>? resolveLinkedText = null)
    {
        var canvas = canvasSize ?? SlideSceneBuilder.CanvasSizeFor(Live?.Presentation);
        var scene = new AnimatedScene(canvas);
        foreach (var (layer, media) in liveMedia)
            scene.AddItem(SlideSceneBuilder.MediaItem($"live-media-{layer.Raw()}", media, canvas), layer);

        if (Live is { } live)
        {
            if ((live.Slide.BackgroundFill ?? live.Presentation?.BackgroundFill) is { } fill
                && SlideSceneBuilder.BackdropItem(fill, canvas, $"{live.Slide.Id}-backdrop") is { } backdrop)
                scene.AddItem(backdrop, LayerKind.Slide);

            IReadOnlyList<SlideObject> objects = live.Slide.Objects;
            if (resolveLinkedText is not null) objects = resolveLinkedText(objects);
            var template = SlideSceneBuilder.ThemeSlide(live.Slide, live.Theme);
            var composition = AnimatedItems.Compose(objects, template, live.Theme, live.Slide.AnimationOrder, canvas);
            var wiring = SlideSceneBuilder.MaskWiring(composition.Stack.Select(e => e.Object).ToList());
            var context = SlideAnimationContext;
            foreach (var entry in composition.Stack)
            {
                var source = entry.FromTheme && resolveLinkedText is not null && entry.Object.TextLink is not null
                    ? resolveLinkedText([entry.Object])[0]
                    : entry.Object;
                var placeholder = entry.FromTheme ? null : composition.Placeholders.GetValueOrDefault(entry.Object.Id);
                var items = SlideSceneBuilder.RenderItems(source, live.Theme, placeholder, canvas, entry.FromTheme ? entry.Id : null,
                    wiring.MaskedBy.GetValueOrDefault(entry.Object.Id), wiring.Mattes.Contains(entry.Object.Id) ? entry.Object.Id : null);
                foreach (var item in AnimatedItems.For(source, items, composition.StepsFor(entry.Object.Id), live.Theme, placeholder, canvas,
                             context, live.FiredAtHostTime))
                    scene.AddItem(item, LayerKind.Slide);
            }
        }

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

        if (AlertMessage is { } message && AlertOnAudience)
            foreach (var item in AlertBanner.Items(message, canvas, "audience"))
                scene.AddItem(item, LayerKind.Alerts);
        return scene;
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
