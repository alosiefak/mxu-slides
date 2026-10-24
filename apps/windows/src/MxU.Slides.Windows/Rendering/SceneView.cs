using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;
using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Infrastructure;
using LayerTransition = MxU.Slides.Core.Animation.SceneTransition;

namespace MxU.Slides.Windows.Rendering;

/// <summary>
/// How a view changes to a new scene. <see cref="Cut"/>, <see cref="Dissolve"/> and
/// <see cref="FadeThroughColor"/> change the whole picture at once. <see cref="Automatic"/>
/// changes each layer its own way, as the Mac does (a slide's own transition on the slide
/// layer, a video's on its layer, and so on): see <see cref="SceneTransition.Layers"/>.
/// </summary>
public enum TransitionStyle { Cut, Dissolve, FadeThroughColor, Automatic }

public readonly record struct SceneTransition(TransitionStyle Style, double Seconds = 0, Color? ThroughColor = null)
{
    public static readonly SceneTransition Cut = new(TransitionStyle.Cut);

    /// <summary>Let the show decide each layer's transition (the view model works it out; see <c>LiveTransitions</c>).</summary>
    public static readonly SceneTransition Automatic = new(TransitionStyle.Automatic);

    /// <summary>The transition for each layer whose items change; null (or a cut) for none. Set for <see cref="TransitionStyle.Automatic"/>.</summary>
    public Func<LayerKind, LayerTransition?>? Layers { get; init; }

    /// <summary>Each layer changes its own way: the slide dissolving while the background cuts, say.</summary>
    public static SceneTransition PerLayer(Func<LayerKind, LayerTransition?> layers) => new(TransitionStyle.Automatic) { Layers = layers };
}

/// <summary>
/// Draws a scene scaled to fit (letterboxed) with cut/dissolve/fade-through
/// transitions, or layer-by-layer transitions (dissolve, fade through a colour,
/// blur dissolve, film burn: <see cref="SceneTransitionEngine"/>), and plays its
/// animation builds: every frame while something
/// moves, the animation engine says where each item is, and the view moves,
/// fades, wipes and blurs that item's cached drawing (redrawing it only when its
/// look changes, e.g. a colour morph or letters typing on). Each item is its own
/// visual, so shadows, blur, rotation and opacity compose on the GPU; picture
/// effects are shader layers around it, and Echo and Ghost Trails blend in the
/// item's trail (<see cref="TrailStore"/>). A single bad item is skipped and
/// logged rather than taking the frame down.
/// </summary>
public sealed class SceneView : FrameworkElement
{
    private sealed class ItemVisual : DrawingVisual
    {
        public ItemVisual()
        {
            VisualTextRenderingMode = TextRenderingMode.Grayscale;
            VisualTextHintingMode = TextHintingMode.Animated;
            VisualBitmapScalingMode = BitmapScalingMode.HighQuality;
            VisualEdgeMode = EdgeMode.Unspecified;
        }
    }

    /// <summary>
    /// What an item's drawing depends on; when equal, the cached drawing is reused. A ticker's
    /// <paramref name="Path"/> pose changes every frame, so a moving ticker is redrawn each frame.
    /// </summary>
    private sealed record ContentKey(RenderItem Render, string Hidden, StrokeTrim? Trim, double FillOpacity, PathTextRenderer.Pose? Path = null);

    /// <summary>Where an item sits this frame; when equal, its transform, clip and mask stay.</summary>
    private sealed record PoseKey(SceneRect Frame, double Rotation, bool FlipH, bool FlipV, AnimationMotion Motion);

    /// <summary>
    /// One picture effect on an item. A shader pass is a layer with an <see cref="ItemEffect"/>.
    /// An Echo or Ghost Trails pass needs a picture of what is under it, so what is under it
    /// lives in <see cref="Source"/> (not in the visual tree) and is shown through
    /// <see cref="Face"/>, a rectangle painted with that picture over exactly the item's area,
    /// so the trail bitmap lines up with it.
    /// </summary>
    private sealed class EffectPass
    {
        public ContainerVisual Layer { get; } = new();
        public ItemEffect? Shader;
        public ContainerVisual? Source;
        public DrawingVisual? Face;
        public DrawingVisual? Under;
        public TrailEffect? Trail;
        public Rect Region;

        public bool IsTrail => Source is not null;

        /// <summary>Where what is under this pass goes.</summary>
        public ContainerVisual Host => Source ?? Layer;
    }

    /// <summary>
    /// A keystoned item: what it looks like untouched lives in <see cref="Source"/> (not in the
    /// visual tree) and <see cref="Face"/> paints that over <see cref="Region"/>, where the
    /// <see cref="KeystoneEffect"/> warps it into its trapezoid (the trail passes' trick).
    /// </summary>
    private sealed class KeystonePass
    {
        public KeystonePass()
        {
            Layer.Effect = Shader;
            Layer.Children.Add(Face);
        }

        public ContainerVisual Layer { get; } = new();
        public ContainerVisual Source { get; } = new();
        public DrawingVisual Face { get; } = new();
        public KeystoneEffect Shader { get; } = new();
        public Rect Region;
        public (SceneRect Frame, double Top, double Bottom, bool Stretch)? Warp;
    }

    /// <summary>
    /// One item: Outer (canvas space) carries the matte clip, Inner (moves with the item) the
    /// pose, opacity, wipe and blur, and between Inner and the drawing sits the keystone warp (if
    /// any), then one layer per picture effect (first effect innermost, as the Mac applies them in order).
    /// </summary>
    private sealed class Slot
    {
        public Slot() => Outer.Children.Add(Inner);
        public ContainerVisual Outer { get; } = new();
        public ContainerVisual Inner { get; } = new();
        public List<EffectPass> Passes { get; } = [];
        public KeystonePass? Keystone;
        public string PassShape = "";
        public ItemVisual? Drawing;
        public ContentKey? Content;
        public PoseKey? Pose;
        public double Blur;
        public Geometry? Matte;

        /// <summary>The video players its drawing uses (released when the item goes).</summary>
        public HashSet<string> Keys { get; } = new(StringComparer.Ordinal);

        /// <summary>Where the drawing goes: under the innermost effect pass, else under the keystone warp, else Inner.</summary>
        public ContainerVisual Host => Passes.Count > 0 ? Passes[0].Host : Keystone?.Source ?? Inner;
    }

    private sealed class Root
    {
        public Root(AnimatedScene scene)
        {
            Scene = scene;
            Visual.Children.Add(Background);
        }

        public AnimatedScene Scene { get; set; }
        public ContainerVisual Visual { get; } = new();
        public DrawingVisual Background { get; } = new ItemVisual();
        public (SceneColor Color, SceneSize Size)? DrawnBackground;
        public Dictionary<string, Slot> Slots { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Keys { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Failed { get; } = new(StringComparer.Ordinal);
        public bool Animating;
    }

    private readonly VisualCollection children;
    private readonly DrawingVisual throughColor = new();
    private Root? current;
    private Root? outgoing;
    private SceneTransition activeTransition;
    private double transitionStart;
    private bool transitioning;
    private bool hooked;
    private MediaHub? media;

    // Layer-by-layer transitions (TransitionStyle.Automatic): the engine composites the old and
    // new scenes per layer, and the one root draws its result every frame until it lands.
    private SceneTransitionEngine? layered;
    private bool layerTransitioning;

    public SceneView()
    {
        children = new VisualCollection(this);
        SnapsToDevicePixels = false;
        ClipToBounds = true;
        if (DefaultMedia is not null) Media = DefaultMedia;
        Unloaded += (_, _) => ReleaseAll();
    }

    /// <summary>The hub views created in XAML (thumbnails, previews) use unless given their own.</summary>
    public static MediaHub? DefaultMedia { get; set; }

    /// <summary>Shared images and video players. Required before presenting media.</summary>
    public MediaHub? Media
    {
        get => media;
        set
        {
            if (media is not null) media.MediaReady -= OnMediaReady;
            media = value;
            if (media is not null) media.MediaReady += OnMediaReady;
        }
    }

    /// <summary>When false (thumbnails, editor), videos draw as a still placeholder and no players start.</summary>
    public bool PlaysVideo { get; set; }

    /// <summary>Videos with the same scope and item id share one player (audience output and its preview).</summary>
    public string PlaybackScope { get; set; } = "live";

    /// <summary>Which media layers may play sound. Background loops stay silent.</summary>
    public Func<string, bool> IsAudible { get; set; } = itemId => itemId == "live-media-videos";

    /// <summary>
    /// A view that is never on screen (the program feed): it still plays transitions and
    /// animations, which an ordinary hidden view skips.
    /// </summary>
    public bool Offscreen { get; set; }

    /// <summary>Colour outside the scene when the view's shape differs from the canvas.</summary>
    public Brush Letterbox { get; set; } = Brushes.Black;

    public AnimatedScene? Scene => current?.Scene;

    public static readonly DependencyProperty StaticSceneProperty = DependencyProperty.Register(
        nameof(StaticScene), typeof(RenderScene), typeof(SceneView),
        new FrameworkPropertyMetadata(null, (d, e) => ((SceneView)d).Present((RenderScene?)e.NewValue, SceneTransition.Cut)));

    /// <summary>Bindable scene for thumbnails and previews (always a cut).</summary>
    public RenderScene? StaticScene
    {
        get => (RenderScene?)GetValue(StaticSceneProperty);
        set => SetValue(StaticSceneProperty, value);
    }

    private static double Now => ShowState.HostClock();

    public void Present(RenderScene? scene, SceneTransition transition) =>
        Present(scene is null ? null : AnimatedScene.From(scene), transition);

    public void Present(AnimatedScene? scene, SceneTransition transition)
    {
        if (transition.Style == TransitionStyle.Automatic && scene is not null)
        {
            PresentLayered(scene, transition.Layers ?? (_ => null));
            return;
        }
        layered = null;
        layerTransitioning = false;
        var next = scene is null ? null : new Root(scene);
        if (next is not null) Render(next, Now);

        if (transition.Style == TransitionStyle.Cut || transition.Seconds <= 0.01 || !IsVisible && !Offscreen)
        {
            FinishTransition();
            Detach(current);
            current = next;
            Attach(current, opacity: 1);
            EnsureFrames();
            return;
        }

        FinishTransition();
        outgoing = current;
        current = next;
        activeTransition = transition;
        if (transition.Style == TransitionStyle.FadeThroughColor)
        {
            using (var dc = throughColor.RenderOpen())
                dc.DrawRectangle(new SolidColorBrush(transition.ThroughColor ?? Colors.Black), null, new Rect(RenderSize));
            if (!children.Contains(throughColor)) children.Insert(0, throughColor);
        }
        Attach(current, opacity: 0);
        transitionStart = Now;
        transitioning = true;
        EnsureFrames();
    }

    public void Update(RenderScene scene) => Update(AnimatedScene.From(scene));

    /// <summary>Re-renders in place (clock ticks, edits, a click) without a transition; unchanged items keep their drawings.</summary>
    public void Update(AnimatedScene scene)
    {
        if (current is null)
        {
            Present(scene, SceneTransition.Cut);
            return;
        }
        if (layered is not null)
        {
            // A layer transition carries on; the new scene becomes what it lands on.
            PresentLayered(scene, _ => null);
            return;
        }
        Show(current, scene);
        EnsureFrames();
    }

    /// <summary>Draws a new scene on a root that's already showing, keeping each unchanged item's drawing.</summary>
    private void Show(Root root, AnimatedScene scene)
    {
        var resized = root.Scene.CanvasSize != scene.CanvasSize;
        root.Scene = scene;
        Render(root, Now);
        if (resized) root.Visual.Transform = FitTransform(scene.CanvasSize);
    }

    /// <summary>
    /// The Mac's way: each layer whose items change transitions on its own (the slide layer
    /// dissolving, a background fading through black, the alert cutting), composited by the
    /// <see cref="SceneTransitionEngine"/> into one scene this view draws every frame until it lands.
    /// Items that stay (same id, same media) are never faded, so a clock tick or a build never flickers.
    /// </summary>
    private void PresentLayered(AnimatedScene scene, Func<LayerKind, LayerTransition?> transitionFor)
    {
        FinishTransition();
        var now = Now;
        // A view no one can see cuts, as with whole-view transitions.
        var cut = !IsVisible && !Offscreen;
        layered ??= new SceneTransitionEngine(current?.Scene ?? new AnimatedScene(scene.CanvasSize, scene.Background));
        layered.Push(scene, cut ? LiveTransitions.AllCut : transitionFor, now);
        var shown = layered.Scene(now);
        if (current is null)
        {
            current = new Root(shown);
            Render(current, now);
            Attach(current, opacity: 1);
        }
        else
        {
            Show(current, shown);
        }
        layerTransitioning = layered.IsTransitioning(now);
        EnsureFrames();
    }

    // MARK: Frames

    private void EnsureFrames()
    {
        var wanted = transitioning || layerTransitioning || current?.Animating == true || outgoing?.Animating == true;
        if (wanted == hooked) return;
        if (wanted) CompositionTarget.Rendering += OnFrame;
        else CompositionTarget.Rendering -= OnFrame;
        hooked = wanted;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = Now;
        if (layerTransitioning && layered is not null && current is not null)
        {
            // The engine's composite for this instant; once it lands it hands back the new scene.
            current.Scene = layered.Scene(now);
            layerTransitioning = layered.IsTransitioning(now);
            Render(current, now);
        }
        else if (current?.Animating == true)
        {
            Render(current, now);
        }
        if (outgoing?.Animating == true) Render(outgoing, now);
        if (transitioning)
        {
            var t = Math.Clamp((now - transitionStart) / Math.Max(activeTransition.Seconds, 0.01), 0, 1);
            var eased = t * t * (3 - 2 * t);
            if (activeTransition.Style == TransitionStyle.FadeThroughColor)
            {
                if (outgoing is not null) outgoing.Visual.Opacity = Math.Clamp(1 - eased * 2, 0, 1);
                if (current is not null) current.Visual.Opacity = Math.Clamp(eased * 2 - 1, 0, 1);
            }
            else if (current is not null)
            {
                current.Visual.Opacity = eased;
            }
            if (t >= 1) FinishTransition();
        }
        EnsureFrames();
    }

    private void FinishTransition()
    {
        transitioning = false;
        Detach(outgoing);
        outgoing = null;
        if (children.Contains(throughColor)) children.Remove(throughColor);
        if (current is not null) current.Visual.Opacity = 1;
    }

    private void Attach(Root? root, double opacity)
    {
        if (root is null) return;
        root.Visual.Opacity = opacity;
        root.Visual.Transform = FitTransform(root.Scene.CanvasSize);
        children.Add(root.Visual);
    }

    private void Detach(Root? root)
    {
        if (root is null) return;
        children.Remove(root.Visual);
        foreach (var key in root.Keys) ReleaseKey(key);
        root.Keys.Clear();
    }

    private void ReleaseAll()
    {
        FinishTransition();
        Detach(current);
        current = null;
        layered = null;
        layerTransitioning = false;
        EnsureFrames();
    }

    private void OnMediaReady(string mediaId)
    {
        if (current is null || !UsesMedia(current.Scene, mediaId)) return;
        foreach (var slot in current.Slots.Values) slot.Content = null;
        Render(current, Now);
    }

    private static bool UsesMedia(AnimatedScene scene, string mediaId) => scene.AllItems.Any(item => item.Render.Content switch
    {
        ItemContent.Media m => m.Id == mediaId,
        ItemContent.Shape { Style.Fill: SceneFill.Media fill } => fill.Id == mediaId,
        _ => false,
    });

    protected override int VisualChildrenCount => children.Count;

    protected override Visual GetVisualChild(int index) => children[index];

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
        double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);

    protected override void OnRender(DrawingContext dc) => dc.DrawRectangle(Letterbox, null, new Rect(RenderSize));

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (current is not null) current.Visual.Transform = FitTransform(current.Scene.CanvasSize);
        if (outgoing is not null) outgoing.Visual.Transform = FitTransform(outgoing.Scene.CanvasSize);
        if (children.Contains(throughColor))
            using (var dc = throughColor.RenderOpen())
                dc.DrawRectangle(new SolidColorBrush(activeTransition.ThroughColor ?? Colors.Black), null, new Rect(RenderSize));
    }

    private Transform FitTransform(SceneSize canvas)
    {
        var size = RenderSize;
        if (canvas.Width <= 0 || canvas.Height <= 0 || size.Width <= 0 || size.Height <= 0) return Transform.Identity;
        var scale = Math.Min(size.Width / canvas.Width, size.Height / canvas.Height);
        var transform = new MatrixTransform(scale, 0, 0, scale,
            (size.Width - canvas.Width * scale) / 2, (size.Height - canvas.Height * scale) / 2);
        transform.Freeze();
        return transform;
    }

    // MARK: Rendering a frame

    private void Render(Root root, double now)
    {
        var scene = root.Scene;
        var canvasRect = new Rect(0, 0, scene.CanvasSize.Width, scene.CanvasSize.Height);
        if (root.DrawnBackground != (scene.Background, scene.CanvasSize))
        {
            using (var dc = root.Background.RenderOpen())
                dc.DrawRectangle(Painting.Brush(scene.Background), null, canvasRect);
            var clip = new RectangleGeometry(canvasRect);
            clip.Freeze();
            root.Visual.Clip = clip;
            root.DrawnBackground = (scene.Background, scene.CanvasSize);
        }

        var ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        ResolvedScene resolved;
        try
        {
            resolved = AnimationEvaluator.Resolve(scene, now, (text, width) => TextLayout.Build(text, new SceneRect(0, 0, width, 1e6), ppd).Bounds.Height);
        }
        catch (Exception ex)
        {
            // Never blank the screen over an animation fault: show everything at rest.
            if (root.Failed.Add("#evaluate")) Log.Error("Animation failed; showing the scene without it", ex);
            resolved = AnimationEvaluator.Resolve(AnimatedScene.From(Still(scene)), now);
        }

        var order = new List<Visual> { root.Background };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var layer in resolved.Layers)
        {
            if (layer.IsHidden) continue;
            // Mattes are never drawn; they only clip the items masked by them.
            var mattes = new Dictionary<string, Geometry>(StringComparer.Ordinal);
            foreach (var matte in layer.Items.Where(r => r.Item.Render.MatteGroup is not null && !r.Item.Id.EndsWith("::text", StringComparison.Ordinal)))
                mattes[matte.Item.Render.MatteGroup!] = MatteGeometry(matte.Item.Render, matte.Motion);

            foreach (var resolvedItem in layer.Items.Where(r => r.Item.Render.MatteGroup is null))
            {
                var id = resolvedItem.Item.Id;
                for (var n = 1; !seen.Add(id); n++) id = $"{resolvedItem.Item.Id}@{n}";
                try
                {
                    if (!root.Slots.TryGetValue(id, out var slot)) root.Slots[id] = slot = new Slot();
                    Apply(slot, resolvedItem, root, ppd, mattes, canvasRect, now);
                    order.Add(slot.Outer);
                }
                catch (Exception ex)
                {
                    root.Slots.Remove(id);
                    if (root.Failed.Add(id)) Log.Error($"Skipped an item that failed to draw ({id})", ex);
                }
            }
        }
        foreach (var gone in root.Slots.Keys.Where(k => !seen.Contains(k)).ToList()) root.Slots.Remove(gone);
        ReleaseUnusedPlayers(root);

        var kids = root.Visual.Children;
        if (kids.Count != order.Count || order.Where((visual, i) => !ReferenceEquals(kids[i], visual)).Any())
        {
            kids.Clear();
            foreach (var visual in order) kids.Add(visual);
        }
        root.Animating = AnimationTimeline.IsAnimating(scene, now, tickers: PlaysVideo);
    }

    /// <summary>
    /// Lets go of video players no item on this root draws any more (a video that left in a layer
    /// transition), so its sound and decoding stop. A root used to live for one scene; with layer
    /// transitions one root shows scene after scene, so this is checked on every frame.
    /// </summary>
    private void ReleaseUnusedPlayers(Root root)
    {
        if (root.Keys.Count == 0) return;
        var inUse = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slot in root.Slots.Values) inUse.UnionWith(slot.Keys);
        foreach (var key in root.Keys.Where(k => !inUse.Contains(k)).ToList())
        {
            root.Keys.Remove(key);
            ReleaseKey(key);
        }
    }

    private static RenderScene Still(AnimatedScene scene)
    {
        var still = new RenderScene(scene.CanvasSize, scene.Background);
        foreach (var layer in scene.Layers)
            foreach (var item in layer.Items)
                still.AddItem(item.Render, layer.Kind);
        return still;
    }

    private void Apply(Slot slot, ResolvedItem resolved, Root root, double ppd, Dictionary<string, Geometry> mattes, Rect canvasRect, double now)
    {
        var item = resolved.Item;
        var render = item.Render;
        var motion = resolved.Motion;

        var pathPose = PathTextPose(item, ppd, now);
        var content = new ContentKey(
            render with { Opacity = 1, BlurRadius = 0, RotationDegrees = 0, FlipHorizontal = false, FlipVertical = false, SkewX = 0, SkewY = 0, MaskedBy = null, MaskOut = false },
            HiddenKey(item.HiddenRuns), item.StrokeTrim, item.FillOpacity, pathPose);
        if (slot.Content != content)
        {
            slot.Keys.Clear();
            var drawing = BuildContent(item, ppd, pathPose, key =>
            {
                slot.Keys.Add(key);
                if (root.Keys.Add(key)) RetainKey(key);
            });
            slot.Host.Children.Clear();
            slot.Host.Children.Add(drawing);
            slot.Drawing = drawing;
            slot.Content = content;
        }
        ApplyEffects(slot, item, motion, now);

        var opacity = Math.Clamp(render.Opacity, 0, 1);
        if (slot.Inner.Opacity != opacity) slot.Inner.Opacity = opacity;

        var pose = new PoseKey(render.Frame, render.RotationDegrees, render.FlipHorizontal, render.FlipVertical, motion);
        if (slot.Pose != pose)
        {
            slot.Inner.Transform = ItemTransform(render, motion);
            slot.Inner.Clip = ClipGeometry(render.Frame, motion.Clip);
            slot.Inner.OpacityMask = WipeMask(render.Frame, motion.Wipe);
            slot.Pose = pose;
        }

        var blur = render.BlurRadius + item.Effects.OfType<AnimationEffect.Blur>().Sum(b => b.Radius * b.Opacity) * 2.5;
        if (Math.Abs(slot.Blur - blur) > 0.05)
        {
            slot.Inner.Effect = blur > 0.5 ? new BlurEffect { Radius = blur, KernelType = KernelType.Gaussian } : null;
            slot.Blur = blur;
        }

        Geometry? matte = null;
        if (render.MaskedBy is { } matteId)
        {
            if (mattes.TryGetValue(matteId, out var clip))
                matte = render.MaskOut ? new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(canvasRect), clip) : clip;
            else if (!render.MaskOut)
                // "Keep inside" a matte that isn't drawn (hidden): nothing shows, as on the Mac. A knockout of nothing cuts nothing.
                matte = Geometry.Empty;
        }
        if (!ReferenceEquals(slot.Matte, matte))
        {
            slot.Outer.Clip = matte;
            slot.Matte = matte;
        }
    }

    /// <summary>One pass to draw this frame: a shader pass (mode and parameters) or a trail pass (the effect and its index in the list).</summary>
    private readonly record struct PassPlan(AnimationEffect Effect, int Index, int Mode, Point4D Parameters, bool Trail);

    /// <summary>Keeps one effect layer per drawable pass (and the keystone warp) and sets each pass's parameters for this frame.</summary>
    private void ApplyEffects(Slot slot, AnimatedItem item, AnimationMotion motion, double now)
    {
        var effects = item.Effects;
        var passes = new List<PassPlan>();
        if (effects.Count > 0)
        {
            var pixelsPerUnit = RenderScale();
            for (var index = 0; index < effects.Count; index++)
            {
                var effect = effects[index];
                if (effect is AnimationEffect.Echo or AnimationEffect.GhostTrails)
                {
                    // Trails need frames over time; thumbnails and the editor show the item as it is.
                    if (PlaysVideo) passes.Add(new PassPlan(effect, index, 0, default, Trail: true));
                }
                else if (ItemEffect.IsSupported && ItemEffect.Pass(effect, now, pixelsPerUnit) is { } pass)
                {
                    passes.Add(new PassPlan(effect, index, pass.Mode, pass.Parameters, Trail: false));
                }
            }
        }
        var keystone = BuildProjector.HasKeystone(motion) && KeystoneEffect.IsSupported;
        var shape = string.Concat(passes.Select(p => p.Trail ? 'T' : 'S')) + (keystone ? "K" : "");
        if (shape != slot.PassShape) RebuildPasses(slot, passes, keystone);
        if (slot.Keystone is { } warp) ApplyKeystone(warp, item.Render.Frame, motion);
        for (var i = 0; i < passes.Count; i++)
        {
            var plan = passes[i];
            var pass = slot.Passes[i];
            if (pass.Shader is { } shader)
            {
                var modeOpacity = new Point4D(plan.Mode, plan.Effect.Opacity, 0, 0);
                if (shader.ModeOpacity != modeOpacity) shader.ModeOpacity = modeOpacity;
                if (shader.Parameters != plan.Parameters) shader.Parameters = plan.Parameters;
            }
            else
            {
                ApplyTrail(pass, plan, item, now);
            }
        }
    }

    /// <summary>
    /// Rebuilds the chain: Inner → keystone → last effect → … → first effect → drawing (a trail pass and
    /// the keystone hold what's under them in their Source). The keystone goes outermost because the Mac
    /// warps the item after its effects.
    /// </summary>
    private static void RebuildPasses(Slot slot, List<PassPlan> passes, bool keystone = false)
    {
        foreach (var pass in slot.Passes)
        {
            pass.Layer.Children.Clear();
            pass.Source?.Children.Clear();
        }
        slot.Keystone?.Source.Children.Clear();
        slot.Inner.Children.Clear();
        slot.Passes.Clear();
        ContainerVisual outer = slot.Inner;
        slot.Keystone = null;
        if (keystone)
        {
            slot.Keystone = new KeystonePass();
            outer.Children.Add(slot.Keystone.Layer);
            outer = slot.Keystone.Source;
        }
        var chain = new List<EffectPass>();
        for (var i = passes.Count - 1; i >= 0; i--)
        {
            var pass = new EffectPass();
            outer.Children.Add(pass.Layer);
            if (passes[i].Trail)
            {
                pass.Source = new ContainerVisual();
                pass.Face = new DrawingVisual();
                if (TrailEffect.IsSupported)
                {
                    pass.Trail = new TrailEffect();
                    pass.Layer.Effect = pass.Trail;
                }
                else
                {
                    // No pixel shader 3.0: the trail is drawn under the live picture instead of blended with it.
                    pass.Under = new DrawingVisual();
                    pass.Layer.Children.Add(pass.Under);
                }
                pass.Layer.Children.Add(pass.Face);
            }
            else
            {
                pass.Shader = new ItemEffect();
                pass.Layer.Effect = pass.Shader;
            }
            chain.Insert(0, pass);
            outer = pass.Host;
        }
        slot.Passes.AddRange(chain);
        slot.PassShape = string.Concat(passes.Select(p => p.Trail ? 'T' : 'S')) + (keystone ? "K" : "");
        if (slot.Drawing is { } drawing) outer.Children.Add(drawing);
    }

    /// <summary>
    /// The keystone warp this frame: the face shows the untouched item over the warp's region, and the
    /// shader squeezes it into its trapezoid. Only redrawn when the frame or the keystone changes.
    /// </summary>
    private static void ApplyKeystone(KeystonePass pass, SceneRect frame, AnimationMotion motion)
    {
        var warp = (frame, motion.KeystoneTop, motion.KeystoneBottom, motion.KeystoneStretch);
        if (pass.Warp == warp) return;
        var region = KeystoneEffect.Region(frame, motion.KeystoneTop, motion.KeystoneBottom);
        if (pass.Region != region)
        {
            var brush = new VisualBrush(pass.Source) { Viewbox = region, ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
            using (var dc = pass.Face.RenderOpen()) dc.DrawRectangle(brush, null, region);
            pass.Region = region;
        }
        // A trapezoid folded flat (an edge at 0%) has nothing to show.
        pass.Layer.Opacity = pass.Shader.Set(frame, region, motion.KeystoneTop, motion.KeystoneBottom, motion.KeystoneStretch) ? 1 : 0;
        pass.Warp = warp;
    }

    /// <summary>
    /// An Echo or Ghost Trails pass this frame: the area it covers (the item's frame, plus room for a
    /// ghost's drift, as the Mac pads it), the picture of what's under it, and the trail from
    /// <see cref="TrailStore"/> combined with it on the graphics card (or drawn under it without shaders).
    /// </summary>
    private void ApplyTrail(EffectPass pass, PassPlan plan, AnimatedItem item, double now)
    {
        var frame = item.Render.Frame;
        var pad = plan.Effect is AnimationEffect.GhostTrails ghost ? ghost.Drift * 12 : 0;
        var region = new Rect(frame.X - pad, frame.Y - pad, Math.Max(1, frame.Width + 2 * pad), Math.Max(1, frame.Height + 2 * pad));
        if (pass.Region != region)
        {
            // The face shows what's under the pass over exactly the region, so the trail lines up with it.
            var brush = new VisualBrush(pass.Source) { Viewbox = region, ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
            using (var dc = pass.Face!.RenderOpen()) dc.DrawRectangle(brush, null, region);
            pass.Region = region;
        }
        // Views drawing the same item at the same moment share one trail; looks with the same item id differ by frame.
        var key = $"{PlaybackScope}:{item.Id}#{plan.Index}@{frame.X:0},{frame.Y:0},{frame.Width:0},{frame.Height:0}";
        var trail = TrailStore.Update(key, pass.Source!, region, plan.Effect, now);
        var ghostly = plan.Effect is AnimationEffect.GhostTrails;
        if (pass.Trail is { } effect)
        {
            if (!ReferenceEquals(effect.Trail, trail.Brush)) effect.Trail = trail.Brush;
            var modeKeep = new Point4D(ghostly ? 1 : 0, trail.Keep, trail.Has ? 1 : 0, plan.Effect.Opacity);
            if (effect.ModeKeep != modeKeep) effect.ModeKeep = modeKeep;
        }
        else if (pass.Under is { } under)
        {
            using (var dc = under.RenderOpen())
                if (trail.Has && trail.Brush is { } brush) dc.DrawRectangle(brush, null, region);
            under.Opacity = plan.Effect.Opacity;
            // A ghost is mostly its trail: the live picture shows through by what isn't kept.
            pass.Face!.Opacity = ghostly && trail.Has ? Math.Max(0.05, 1 - trail.Keep * plan.Effect.Opacity) : 1;
        }
    }

    private static string HiddenKey(IReadOnlyList<HiddenTextRun> runs) => runs.Count == 0
        ? ""
        : string.Join(";", runs.Select(r => $"{r.Line}:{r.Column}:{r.Length}:{(r.PlaceholderUnderline ? 1 : 0)}{(r.Caret ? 1 : 0)}"));

    /// <summary>
    /// Skew, flip, scale (with the 3-D swing and tilt flattened to a squash), rotation
    /// about the frame centre, then the animated move. Keystone is drawn inside this, by
    /// the keystone pass (<see cref="ApplyKeystone"/>), as the Mac warps before it skews.
    /// </summary>
    private static Transform? ItemTransform(RenderItem render, AnimationMotion motion)
    {
        var f = render.Frame;
        var cx = f.X + f.Width / 2;
        var cy = f.Y + f.Height / 2;
        var sx = motion.Scale * Math.Cos(Math.Clamp(motion.Swing, -89.5, 89.5) * Math.PI / 180) * (render.FlipHorizontal ? -1 : 1);
        var sy = (motion.ScaleY ?? motion.Scale) * Math.Cos(Math.Clamp(motion.Tilt, -89.5, 89.5) * Math.PI / 180) * (render.FlipVertical ? -1 : 1);
        var m = Matrix.Identity;
        m.Translate(-cx, -cy);
        if (motion.SkewX != 0 || motion.SkewY != 0) m.Skew(-Math.Clamp(motion.SkewX, -85, 85), Math.Clamp(motion.SkewY, -85, 85));
        if (sx != 1 || sy != 1) m.Scale(sx, sy);
        if (render.RotationDegrees != 0) m.Rotate(render.RotationDegrees);
        m.Translate(cx + motion.Translate.Dx, cy + motion.Translate.Dy);
        if (m.IsIdentity) return null;
        var transform = new MatrixTransform(m);
        transform.Freeze();
        return transform;
    }

    /// <summary>The visible window of an item (block scroll, video push), in its own unit square.</summary>
    private static Geometry? ClipGeometry(SceneRect frame, MotionClip? clip)
    {
        if (clip is null) return null;
        var geometry = new RectangleGeometry(new Rect(
            frame.X + clip.MinU * frame.Width, frame.Y + clip.MinV * frame.Height,
            Math.Max(0, (clip.MaxU - clip.MinU) * frame.Width), Math.Max(0, (clip.MaxV - clip.MinV) * frame.Height)));
        geometry.Freeze();
        return geometry;
    }

    /// <summary>A wipe reveals the item from one edge (as the Mac's build shader does), with a soft edge of <c>Feather</c> canvas points.</summary>
    private static Brush? WipeMask(SceneRect frame, MotionWipe? wipe)
    {
        if (wipe is not { } w) return null;
        var p = Math.Clamp(w.Progress, 0, 1);
        if (p >= 1) return null;
        var feather = Math.Max(w.Feather, 0.5);
        var midX = frame.X + frame.Width / 2;
        var midY = frame.Y + frame.Height / 2;
        var (shown, hidden) = w.Edge switch
        {
            SceneAnimationEdge.Right => (new Point(frame.Right - p * frame.Width + feather, midY), new Point(frame.Right - p * frame.Width, midY)),
            SceneAnimationEdge.Top => (new Point(midX, frame.Y + p * frame.Height - feather), new Point(midX, frame.Y + p * frame.Height)),
            SceneAnimationEdge.Bottom => (new Point(midX, frame.Bottom - p * frame.Height + feather), new Point(midX, frame.Bottom - p * frame.Height)),
            _ => (new Point(frame.X + p * frame.Width - feather, midY), new Point(frame.X + p * frame.Width, midY)),
        };
        var brush = new LinearGradientBrush(new GradientStopCollection { new(Colors.Black, 0), new(Colors.Transparent, 1) }, shown, hidden)
        {
            MappingMode = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        return brush;
    }

    private static Geometry MatteGeometry(RenderItem matte, AnimationMotion motion)
    {
        var frame = new Rect(matte.Frame.X, matte.Frame.Y, Math.Max(0, matte.Frame.Width), Math.Max(0, matte.Frame.Height));
        var geometry = matte.Content is ItemContent.Shape shape ? ShapeGeometry(shape.Style.Kind, frame) : new RectangleGeometry(frame);
        if (ItemTransform(matte, motion) is { } transform)
            geometry = new GeometryGroup { Children = { geometry }, Transform = transform };
        if (ClipGeometry(matte.Frame, motion.Clip) is { } window)
            geometry = new CombinedGeometry(GeometryCombineMode.Intersect, geometry, window);
        geometry.Freeze();
        return geometry;
    }

    private static Geometry ShapeGeometry(SceneShapeKind kind, Rect frame) => kind switch
    {
        SceneShapeKind.RoundedRectangle rounded => new RectangleGeometry(frame,
            Math.Min(rounded.CornerRadius, Math.Min(frame.Width, frame.Height) / 2),
            Math.Min(rounded.CornerRadius, Math.Min(frame.Width, frame.Height) / 2)),
        SceneShapeKind.Ellipse => new EllipseGeometry(frame),
        SceneShapeKind.Path path => PathGeometry(path.Data, frame),
        _ => new RectangleGeometry(frame),
    };

    // MARK: Drawing an item

    /// <summary>
    /// Where text on a path sits this frame (null for other items, and for a ticker with nothing to show
    /// now). Live views move tickers; thumbnails and the editor show them at rest, unless the scene is
    /// frozen at an instant (scrubbing, the self-test's pictures).
    /// </summary>
    private PathTextRenderer.Pose? PathTextPose(AnimatedItem item, double ppd, double now)
    {
        if (item.Render.Content is not ItemContent.Text { Styled: var text } || !PathTextLayout.IsPathText(text)) return null;
        var frozen = item.AnimationContext?.FrozenHostTime;
        var moving = (PlaysVideo || frozen is not null) && frozen != AnimationContext.Settled.FrozenHostTime;
        var anchor = item.TickerAnchorHostTime ?? item.AnimationContext?.AnchorHostTime;
        return PathTextRenderer.PoseAt(text, item.Render.Frame, ppd, frozen ?? now, anchor, moving);
    }

    private ItemVisual BuildContent(AnimatedItem animated, double ppd, PathTextRenderer.Pose? pathPose, Action<string> usesKey)
    {
        var item = animated.Render;
        var visual = new ItemVisual();
        var frame = new Rect(item.Frame.X, item.Frame.Y, Math.Max(0, item.Frame.Width), Math.Max(0, item.Frame.Height));
        using (var dc = visual.RenderOpen())
        {
            switch (item.Content)
            {
                case ItemContent.Text text when PathTextLayout.IsPathText(text.Styled):
                    // Along its path (a ticker, or text round a shape's edge) instead of as a block.
                    if (pathPose is { } pose) PathTextRenderer.Draw(dc, text.Styled, item.Frame, ppd, pose);
                    visual.Effect = ShadowEffect(text.Styled.Shadow);
                    break;
                case ItemContent.Text text:
                    DrawText(dc, text.Styled, item.Frame, ppd, animated.HiddenRuns);
                    visual.Effect = ShadowEffect(text.Styled.Shadow);
                    break;
                case ItemContent.Shape shape:
                    DrawShape(dc, shape.Style, frame, item, usesKey, animated.StrokeTrim, animated.FillOpacity);
                    visual.Effect = ShadowEffect(shape.Style.Shadow);
                    break;
                case ItemContent.Media mediaContent:
                    DrawMedia(dc, mediaContent.Id, mediaContent.ScaleMode, frame, item, usesKey, new RectangleGeometry(frame));
                    break;
            }
        }
        return visual;
    }

    private static Effect? ShadowEffect(TextShadow? shadow)
    {
        if (shadow is null || shadow.Color.Alpha <= 0.001) return null;
        var depth = Math.Sqrt(shadow.OffsetX * shadow.OffsetX + shadow.OffsetY * shadow.OffsetY);
        var effect = new DropShadowEffect
        {
            Color = Painting.ToColor(shadow.Color with { Alpha = 1 }),
            Opacity = shadow.Color.Alpha,
            BlurRadius = shadow.BlurRadius * 2,
            ShadowDepth = depth,
            Direction = depth > 0 ? Math.Atan2(-shadow.OffsetY, shadow.OffsetX) * 180 / Math.PI : 315,
            RenderingBias = RenderingBias.Quality,
        };
        effect.Freeze();
        return effect;
    }

    /// <param name="hidden">Characters a build hasn't revealed yet (line = source line, column/length in characters).</param>
    private static void DrawText(DrawingContext dc, StyledText text, SceneRect frame, double ppd, IReadOnlyList<HiddenTextRun> hidden)
    {
        if (string.IsNullOrEmpty(text.String)) return;
        var layout = TextLayout.Build(text, frame, ppd);
        if (layout.Lines.Count == 0) return;
        // Words that don't fit are cut off at the box's edge, as the Mac draws them (and as the
        // slide grid's warning says), rather than spilling over what's around the box.
        var clipped = layout.Overflows;
        if (clipped) dc.PushClip(new RectangleGeometry(new Rect(frame.X, frame.Y, Math.Max(0, frame.Width), Math.Max(0, frame.Height))));
        DrawLaidOutText(dc, text, frame, layout, hidden);
        if (clipped) dc.Pop();
    }

    private static void DrawLaidOutText(DrawingContext dc, StyledText text, SceneRect frame, TextLayout layout, IReadOnlyList<HiddenTextRun> hidden)
    {

        if (text.LineFill is { } lineFill && Painting.FillBrush(lineFill.Fill, Bounds(layout)) is { } fillBrush)
        {
            var maxWidth = layout.Lines.Max(l => l.Width);
            foreach (var line in layout.Lines.Where(l => l.Text.Text.Trim().Length > 0))
            {
                var (x, width) = lineFill.WidthMode switch
                {
                    LineFillWidthMode.LineWidth => (line.X - lineFill.HorizontalPadding, line.Width + 2 * lineFill.HorizontalPadding),
                    LineFillWidthMode.MaxLineWidth => (layout.Bounds.X - lineFill.HorizontalPadding, maxWidth + 2 * lineFill.HorizontalPadding),
                    _ => (frame.X, frame.Width),
                };
                var rect = new Rect(x + lineFill.HorizontalOffset, line.Y - lineFill.VerticalPadding + lineFill.VerticalOffset,
                    Math.Max(0, width), line.Height + 2 * lineFill.VerticalPadding);
                dc.DrawRoundedRectangle(fillBrush, null, rect, lineFill.CornerRadius, lineFill.CornerRadius);
            }
        }

        var textBrush = text.Fill is { } fill and not SceneFill.NoFill ? Painting.FillBrush(fill, Bounds(layout)) : null;
        Pen? outlinePen = null;
        if (text.Outline is { Width: > 0 } outline)
        {
            outlinePen = new Pen(Painting.Brush(outline.Color), outline.Width * 2) { LineJoin = PenLineJoin.Round };
            outlinePen.Freeze();
        }
        foreach (var line in layout.Lines)
        {
            var ranges = hidden.Count == 0 ? [] : HiddenRanges(line, hidden);
            var pieces = line.Glyphs ?? [(line.Text, 0.0)];
            Geometry? hiddenArea = null;
            if (ranges.Count > 0 && line.Glyphs is null)
            {
                var group = new GeometryGroup();
                foreach (var r in ranges)
                    if (line.Text.BuildHighlightGeometry(new Point(line.X, line.Y), r.Start, r.Length) is { } box) group.Children.Add(box);
                hiddenArea = group;
                dc.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(-1e5, -1e5, 2e5, 2e5)), group));
            }
            for (var i = 0; i < pieces.Count; i++)
            {
                var (piece, offset) = pieces[i];
                if (line.Glyphs is not null && line.GlyphStarts is { } starts && ranges.Any(r => starts[i] >= r.Start && starts[i] < r.Start + r.Length))
                    continue;
                if (textBrush is not null) piece.SetForegroundBrush(textBrush);
                var origin = new Point(line.X + offset, line.Y);
                if (outlinePen is not null) dc.DrawGeometry(null, outlinePen, piece.BuildGeometry(origin));
                if (text.Fill is SceneFill.NoFill) continue;
                dc.DrawText(piece, origin);
            }
            if (hiddenArea is not null) dc.Pop();
            if (ranges.Count > 0) DrawBuildMarks(dc, line, ranges, text);
        }
        if (hidden.Count == 0)
            foreach (var chord in layout.Chords) dc.DrawText(chord.Text, new Point(chord.X, chord.Y));
    }

    private readonly record struct HiddenRange(int Start, int Length, bool Underline, bool Caret);

    /// <summary>The hidden runs on one laid-out line, as UTF-16 ranges of the line's own text.</summary>
    private static List<HiddenRange> HiddenRanges(TextLayout.Line line, IReadOnlyList<HiddenTextRun> hidden)
    {
        var result = new List<HiddenRange>();
        var lineLength = line.Text.Text.Length;
        int[]? clusters = null;
        foreach (var run in hidden.Where(r => r.Line == line.Paragraph && r.Length > 0))
        {
            clusters ??= StringInfo.ParseCombiningCharacters(line.Source);
            int Offset(int column) => column < clusters.Length ? clusters[Math.Max(0, column)] : line.Source.Length;
            var start = Math.Max(Offset(run.Column), line.Start) - line.Start;
            var end = Math.Min(Offset(run.Column + run.Length), line.Start + lineLength) - line.Start;
            if (end > start) result.Add(new HiddenRange(start, end - start, run.PlaceholderUnderline, run.Caret));
            else if (run.Caret && Offset(run.Column) - line.Start is var at && at >= 0 && at <= lineLength) result.Add(new HiddenRange(at, 0, false, true));
        }
        return result;
    }

    /// <summary>Placeholder underlines where hidden words will appear, and the typewriter caret.</summary>
    private static void DrawBuildMarks(DrawingContext dc, TextLayout.Line line, List<HiddenRange> ranges, StyledText text)
    {
        var brush = Painting.Brush(text.Color);
        foreach (var r in ranges.Where(r => r.Underline || r.Caret))
        {
            var x0 = line.X + XAt(line, r.Start);
            if (r.Underline && r.Length > 0)
            {
                var x1 = line.X + XAt(line, r.Start + r.Length);
                dc.DrawRectangle(brush, null, new Rect(x0, line.Y + line.Height * 0.92, Math.Max(0, x1 - x0), Math.Max(1, line.Height * 0.05)));
            }
            if (r.Caret) dc.DrawRectangle(brush, null, new Rect(x0, line.Y + line.Height * 0.12, Math.Max(1.5, line.Height * 0.06), line.Height * 0.8));
        }
    }

    private static double XAt(TextLayout.Line line, int index)
    {
        if (index <= 0) return 0;
        if (line.Glyphs is { } glyphs && line.GlyphStarts is { } starts)
        {
            for (var i = 0; i < starts.Count; i++)
                if (starts[i] >= index) return glyphs[i].X;
            return line.Width;
        }
        if (index >= line.Text.Text.Length) return line.Width;
        return line.Text.BuildHighlightGeometry(new Point(0, 0), index, 1)?.Bounds.X ?? line.Width;
    }

    private static Rect Bounds(TextLayout layout) =>
        new(layout.Bounds.X, layout.Bounds.Y, Math.Max(1, layout.Bounds.Width), Math.Max(1, layout.Bounds.Height));

    private void DrawShape(DrawingContext dc, ShapeStyle style, Rect frame, RenderItem item, Action<string> usesKey, StrokeTrim? trim, double fillOpacity)
    {
        var geometry = ShapeGeometry(style.Kind, frame);
        geometry.Freeze();

        if (fillOpacity > 0.001)
        {
            if (fillOpacity < 1) dc.PushOpacity(fillOpacity);
            if (style.Fill is SceneFill.Media mediaFill)
                DrawMedia(dc, mediaFill.Id, mediaFill.ScaleMode, frame, item, usesKey, geometry, isFill: true);
            else if (Painting.FillBrush(style.Fill, frame) is { } brush)
                dc.DrawGeometry(brush, null, geometry);
            if (fillOpacity < 1) dc.Pop();
        }

        if (style.Stroke is { Width: > 0 } stroke && (trim is not { } t || t.Length > 0.0005))
        {
            var pen = new Pen(Painting.Brush(stroke.Color), stroke.Width)
            {
                DashStyle = trim is { } part ? TrimDashes(geometry, part, stroke.Width) : stroke.Dash switch
                {
                    StrokeDash.Dashed => new DashStyle([3, 2], 0),
                    StrokeDash.Dotted => new DashStyle([0, 2], 0),
                    _ => DashStyles.Solid,
                },
                DashCap = stroke.Dash == StrokeDash.Dotted && trim is null ? PenLineCap.Round : PenLineCap.Flat,
                LineJoin = PenLineJoin.Miter,
            };
            pen.Freeze();
            dc.DrawGeometry(null, pen, geometry);
        }
    }

    /// <summary>
    /// A "draw" build: one dash covering <c>Length</c> of the outline from <c>Start</c>
    /// (clockwise from the path's start; Reversed runs the other way), as a fraction of the perimeter.
    /// </summary>
    private static DashStyle TrimDashes(Geometry geometry, StrokeTrim trim, double thickness)
    {
        var perimeter = Perimeter(geometry) / Math.Max(thickness, 0.01);
        var length = Math.Clamp(trim.Length, 0, 1);
        if (length >= 0.9995 || perimeter <= 0) return DashStyles.Solid;
        var start = trim.Reversed ? trim.Start - length : trim.Start;
        start -= Math.Floor(start);
        var dash = length * perimeter;
        var style = new DashStyle([dash, perimeter - dash], perimeter - start * perimeter);
        style.Freeze();
        return style;
    }

    private static double Perimeter(Geometry geometry)
    {
        var total = 0.0;
        foreach (var figure in geometry.GetFlattenedPathGeometry(0.25, ToleranceType.Absolute).Figures)
        {
            var from = figure.StartPoint;
            foreach (var segment in figure.Segments)
            {
                var points = segment switch
                {
                    PolyLineSegment poly => poly.Points,
                    LineSegment line => [line.Point],
                    _ => [],
                };
                foreach (var point in points)
                {
                    total += (point - from).Length;
                    from = point;
                }
            }
            if (figure.IsClosed) total += (figure.StartPoint - from).Length;
        }
        return total;
    }

    /// <summary>
    /// Path data is in unit coordinates (0–1 across the frame), as in the Mac's
    /// SVGPathParser, filled with the non-zero rule like Core Graphics.
    /// </summary>
    private static Geometry PathGeometry(string data, Rect frame)
    {
        if (string.IsNullOrWhiteSpace(data)) return new RectangleGeometry(frame);
        try
        {
            var parsed = Geometry.Parse("F1 " + data);
            return new GeometryGroup
            {
                FillRule = FillRule.Nonzero,
                Children = { parsed },
                Transform = new MatrixTransform(frame.Width, 0, 0, frame.Height, frame.X, frame.Y),
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"Unreadable path data, drawing a rectangle instead: {ex.Message}");
            return new RectangleGeometry(frame);
        }
    }

    /// <summary>
    /// A camera, NDI source or mirrored screen: its picture updates in place as frames arrive, and
    /// the view keeps it coming for as long as it shows it (<see cref="MediaHub.LiveKey"/>). On the
    /// Video Input layer it says "Waiting for…" until the first frame. As a shape's fill (a
    /// picture-in-picture camera on a slide), live views draw nothing until then, as the Mac does,
    /// and thumbnails and the editor always draw the Mac's placeholder instead of the live picture.
    /// </summary>
    private void DrawLivePicture(DrawingContext dc, string mediaId, SceneMediaScaleMode mode, Rect frame, Action<string> usesKey, bool isFill)
    {
        if (isFill && !PlaysVideo)
        {
            if (LiveInputPlaceholder.For(mediaId) is { } glyph)
                LiveInputPlaceholder.Draw(dc, glyph, Place(LiveInputPlaceholder.NaturalSize, frame, mode));
            return;
        }
        usesKey(MediaHub.LiveKey(mediaId));
        if (media!.LiveInputPicture(mediaId) is { } picture)
            dc.DrawImage(picture, Place(new Size(picture.PixelWidth, picture.PixelHeight), frame, mode));
        else if (!isFill)
            DrawPlaceholder(dc, frame, $"Waiting for {media.LiveInputName?.Invoke(mediaId) ?? "the input"}…", failed: false);
    }

    /// <summary>Holds a video player or a live picture while an item draws it.</summary>
    private void RetainKey(string key)
    {
        if (MediaHub.IsLiveKey(key)) media?.RetainLive(key);
        else media?.Retain(key);
    }

    private void ReleaseKey(string key)
    {
        if (MediaHub.IsLiveKey(key)) media?.ReleaseLive(key);
        else media?.Release(key);
    }

    private void DrawMedia(DrawingContext dc, string mediaId, SceneMediaScaleMode mode, Rect frame, RenderItem item, Action<string> usesKey, Geometry clip,
        bool isFill = false)
    {
        if (media is null || frame.Width <= 0 || frame.Height <= 0) return;
        dc.PushClip(clip);
        try
        {
            if (MediaHub.IsLivePicture(mediaId))
            {
                DrawLivePicture(dc, mediaId, mode, frame, usesKey, isFill);
                return;
            }
            switch (media.KindOf(mediaId))
            {
                case Core.Model.MediaKind.Image:
                    var pixelWidth = (int)Math.Ceiling(frame.Width * Math.Max(1, RenderScale()));
                    if (media.Image(mediaId, pixelWidth) is { } image)
                        dc.DrawImage(image, Place(new Size(image.Width, image.Height), frame, mode));
                    break;
                case Core.Model.MediaKind.Video when PlaysVideo:
                    // A video fading out in a layer transition keeps playing on the same player.
                    var playbackId = PlaybackId(item.Id);
                    var key = $"{PlaybackScope}:{playbackId}:{mediaId}";
                    var player = media.Video(key, mediaId, item.Loops, IsAudible(playbackId));
                    usesKey(key);
                    if (player is null || media.IsVideoFailed(key))
                    {
                        DrawPlaceholder(dc, frame, media.NameOf(mediaId), failed: true);
                        break;
                    }
                    var natural = media.NaturalSize(mediaId) ?? frame.Size;
                    dc.DrawVideo(player, Place(natural, frame, mode));
                    break;
                case Core.Model.MediaKind.Video when media.Poster(mediaId) is { } poster:
                    dc.DrawImage(poster, Place(new Size(poster.Width, poster.Height), frame, mode));
                    break;
                case Core.Model.MediaKind.Video:
                    DrawPlaceholder(dc, frame, media.NameOf(mediaId), failed: false);
                    break;
                default:
                    DrawPlaceholder(dc, frame, "Missing media", failed: true);
                    break;
            }
        }
        finally
        {
            dc.Pop();
        }
    }

    /// <summary>The item id a video plays under: a leaving copy (<c>id::leaving</c>, from a layer transition) shares its player.</summary>
    private static string PlaybackId(string itemId) =>
        itemId.EndsWith(LeavingSuffix, StringComparison.Ordinal) ? itemId[..^LeavingSuffix.Length] : itemId;

    private const string LeavingSuffix = "::leaving";

    private double RenderScale()
    {
        var canvas = current?.Scene.CanvasSize ?? SceneSize.Hd;
        var size = RenderSize;
        if (size.Width <= 0) return 1;
        return Math.Min(size.Width / canvas.Width, size.Height / canvas.Height) * VisualTreeHelper.GetDpi(this).DpiScaleX;
    }

    private static readonly Brush PlaceholderFill = Frozen(new LinearGradientBrush(Color.FromRgb(0x1E, 0x29, 0x3B), Color.FromRgb(0x0B, 0x12, 0x20), 90));
    private static readonly Brush FailedFill = Frozen(new SolidColorBrush(Color.FromRgb(0x3B, 0x1E, 0x1E)));

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private static void DrawPlaceholder(DrawingContext dc, Rect frame, string? name, bool failed)
    {
        dc.DrawRectangle(failed ? FailedFill : PlaceholderFill, null, frame);
        var size = Math.Max(12, Math.Min(frame.Width, frame.Height) * 0.08);
        var label = new FormattedText((failed ? "⚠ " : "▶ ") + (name ?? "Video"), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, Brushes.White, 1.0)
        { MaxTextWidth = Math.Max(1, frame.Width * 0.9), MaxLineCount = 2, Trimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center };
        dc.PushOpacity(0.7);
        dc.DrawText(label, new Point(frame.X + frame.Width * 0.05, frame.Y + (frame.Height - label.Height) / 2));
        dc.Pop();
    }

    /// <summary>Where content of <paramref name="natural"/> size lands in <paramref name="frame"/> for a scale mode.</summary>
    public static Rect Place(Size natural, Rect frame, SceneMediaScaleMode mode)
    {
        if (mode == SceneMediaScaleMode.Stretch || natural.Width <= 0 || natural.Height <= 0) return frame;
        var scale = mode == SceneMediaScaleMode.Fit
            ? Math.Min(frame.Width / natural.Width, frame.Height / natural.Height)
            : Math.Max(frame.Width / natural.Width, frame.Height / natural.Height);
        var w = natural.Width * scale;
        var h = natural.Height * scale;
        return new Rect(frame.X + (frame.Width - w) / 2, frame.Y + (frame.Height - h) / 2, w, h);
    }
}
