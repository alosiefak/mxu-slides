using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Scene;

/// <summary>The live show's independent "functions", each clearable on its own (F1–F6).</summary>
public enum ShowFunction { Slides, Media, Overlays, Audio, Alerts, Inputs }

/// <summary>
/// What is live on the audience outputs: the slide, media on each media layer,
/// overlays and the alert. Pure state; the app fires cues into it and renders
/// <see cref="Scene"/>. Port of SlideScene/ShowState.swift without animations.
/// </summary>
public sealed class ShowState
{
    public sealed record LiveSlide(Slide Slide, Presentation? Presentation, Theme? Theme, string? ArrangementId, DateTimeOffset FiredAt);

    private readonly Dictionary<LayerKind, CueMedia> liveMedia = [];
    private readonly HashSet<LayerKind> directMediaFires = [];
    private readonly List<Overlay> liveOverlays = [];

    public LiveSlide? Live { get; private set; }
    public LiveSlide? Last { get; private set; }
    public IReadOnlyDictionary<LayerKind, CueMedia> LiveMedia => liveMedia;
    public IReadOnlyList<Overlay> LiveOverlays => liveOverlays;

    /// <summary>Raised after any change, so outputs can re-render.</summary>
    public event Action? Changed;

    public bool IsEmpty => Live is null && liveMedia.Count == 0 && liveOverlays.Count == 0 && !HasAlert;

    public void Fire(Slide slide, Presentation? presentation = null, Theme? theme = null, string? arrangementId = null, DateTimeOffset? at = null)
    {
        if (Live is { } outgoing && outgoing.Slide.Id != slide.Id) Last = outgoing;
        Live = new LiveSlide(slide, presentation, theme, arrangementId, at ?? DateTimeOffset.Now);

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

    public void Fire(Overlay overlay)
    {
        var index = liveOverlays.FindIndex(o => o.Id == overlay.Id);
        if (index >= 0) liveOverlays[index] = overlay;
        else liveOverlays.Add(overlay);
        Changed?.Invoke();
    }

    public void DismissOverlay(string id)
    {
        if (liveOverlays.RemoveAll(o => o.Id == id) > 0) Changed?.Invoke();
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

    public void Clear(ShowFunction function)
    {
        switch (function)
        {
            case ShowFunction.Slides:
                ClearSlide();
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
                liveOverlays.Clear();
                break;
            case ShowFunction.Alerts:
                AlertMessage = null;
                break;
            case ShowFunction.Audio:
                break;
        }
        Changed?.Invoke();
    }

    public void Clear(LayerKind layer)
    {
        switch (layer)
        {
            case LayerKind.Slide:
                ClearSlide();
                break;
            case LayerKind.Overlays:
                liveOverlays.Clear();
                break;
            case LayerKind.Alerts:
                AlertMessage = null;
                break;
            default:
                liveMedia.Remove(layer);
                directMediaFires.Remove(layer);
                break;
        }
        Changed?.Invoke();
    }

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

    private void ClearSlide()
    {
        if (Live is not null) Last = Live;
        Live = null;
    }

    // MARK: Rendering

    /// <summary>
    /// The audience scene: media layers, the live slide composed with its theme,
    /// overlays and the alert band. <paramref name="resolveLinkedText"/> fills in
    /// clock/timer/slide-linked text objects (stage-style text on audience slides).
    /// </summary>
    public RenderScene Scene(SceneSize? canvasSize = null, Func<IReadOnlyList<SlideObject>, List<SlideObject>>? resolveLinkedText = null)
    {
        var canvas = canvasSize ?? SlideSceneBuilder.CanvasSizeFor(Live?.Presentation);
        var scene = new RenderScene(canvas);
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
            SlideSceneBuilder.AddSlideObjects(scene, objects, live.Theme, template, canvas,
                resolveTemplateObject: resolveLinkedText is null ? null : o => o.TextLink is null ? o : resolveLinkedText([o])[0]);
        }

        foreach (var overlay in liveOverlays)
        {
            var layer = LayerKinds.Parse(overlay.Layer) ?? LayerKind.Overlays;
            IReadOnlyList<SlideObject> objects = overlay.Objects;
            if (resolveLinkedText is not null) objects = resolveLinkedText(objects);
            foreach (var o in objects)
                foreach (var item in SlideSceneBuilder.RenderItems(o, null, null, canvas, $"overlay-{overlay.Id}-{o.Id}"))
                    scene.AddItem(item, layer);
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
