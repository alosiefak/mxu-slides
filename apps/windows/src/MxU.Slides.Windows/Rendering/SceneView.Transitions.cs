using System.Windows;
using System.Windows.Media;
using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Scene;
using LayerTransition = MxU.Slides.Core.Animation.SceneTransition;

namespace MxU.Slides.Windows.Rendering;

// The Windows-only transitions (Phase 6). While one plays on a layer, the engine hands the view both
// of the layer's pictures as items (the old picture's ids end "::leaving") and a LayerMix saying what
// is playing. Instead of drawing both pictures, the view puts each in a container of its own, off the
// screen, and draws one rectangle the size of the canvas in their place, with a TransitionEffect: the
// shader reads the two pictures through brushes and works out every pixel from them.
public sealed partial class SceneView
{
    /// <summary>
    /// A layer being mixed. <see cref="Layer"/> sits in the view where the layer's items would;
    /// <see cref="Face"/> paints <see cref="To"/> (the new picture) over exactly the canvas, and is
    /// what the effect is applied to; <see cref="From"/> (the old picture) reaches the effect through
    /// a brush. Neither container is in the visual tree: they are only seen through brushes, which
    /// is how the Echo and keystone passes work too. If the old picture was itself caught mid-way
    /// through a transition, <see cref="Inner"/> draws it, inside <see cref="From"/>.
    /// </summary>
    private sealed class MixPass
    {
        public MixPass(PictureShader shader, MixPass? inner)
        {
            Shader = shader;
            Effect = new TransitionEffect(shader);
            Layer.Effect = Effect;
            Layer.Children.Add(Face);
            Inner = inner;
        }

        public PictureShader Shader { get; }
        public TransitionEffect Effect { get; }
        public ContainerVisual Layer { get; } = new();
        public DrawingVisual Face { get; } = new();
        public ContainerVisual From { get; } = new();
        public ContainerVisual To { get; } = new();
        public MixPass? Inner { get; }
        public Rect Canvas = Rect.Empty;

        /// <summary>
        /// A clear rectangle the size of the canvas at the bottom of each picture. A picture can be
        /// empty (a slide pushed off to nothing, or onto a clear screen), and WPF draws nothing for a
        /// brush of nothing, and skips an effect on nothing: the slide would just vanish. With these,
        /// each picture always covers the canvas, even when it's all clear.
        /// </summary>
        private readonly DrawingVisual fromFrame = new(), toFrame = new();

        /// <summary>What each picture holds this frame (applied in <see cref="Reparent"/>).</summary>
        public List<Visual> FromItems { get; } = [];
        public List<Visual> ToItems { get; } = [];

        /// <summary>The families down the chain ("Planes>Wipe"): when it changes, the passes are made afresh.</summary>
        public string Shape => Inner is null ? Shader.ToString() : $"{Shader}>{Inner.Shape}";

        public IEnumerable<MixPass> Chain()
        {
            for (var pass = this; pass is not null; pass = pass.Inner) yield return pass;
        }

        /// <summary>Starts a frame: each picture just its clear frame, with the inner pass standing in for the whole old picture.</summary>
        public void Begin()
        {
            foreach (var pass in Chain())
            {
                pass.FromItems.Clear();
                pass.ToItems.Clear();
                pass.FromItems.Add(pass.fromFrame);
                pass.ToItems.Add(pass.toFrame);
                if (pass.Inner is { } inner) pass.FromItems.Add(inner.Layer);
            }
        }

        /// <summary>Puts an item in its picture: no ::leaving is the new picture, one or more the old one (or deeper, an older one).</summary>
        public void Place(string itemId, Visual visual)
        {
            var depth = LeavingDepth(itemId);
            var pass = this;
            while (depth > 0 && pass.Inner is not null)
            {
                pass = pass.Inner;
                depth--;
            }
            (depth == 0 ? pass.ToItems : pass.FromItems).Add(visual);
        }

        /// <summary>Points the brushes at the canvas (once, and again if the canvas changes size).</summary>
        public void Fit(Rect canvas)
        {
            if (Canvas == canvas) return;
            foreach (var frame in new[] { fromFrame, toFrame })
                using (var dc = frame.RenderOpen()) dc.DrawRectangle(Brushes.Transparent, null, canvas);
            using (var dc = Face.RenderOpen()) dc.DrawRectangle(Picture(To, canvas), null, canvas);
            Effect.Previous = Picture(From, canvas);
            Canvas = canvas;
        }
    }

    /// <summary>
    /// A container seen as a picture of the canvas. The brush keeps what it drew (CachingHint) and
    /// only draws it again when something in it changes (a video frame, a build), so a transition
    /// between still slides costs the graphics card little more than the shader itself.
    /// </summary>
    private static VisualBrush Picture(Visual source, Rect canvas)
    {
        var brush = new VisualBrush(source) { Viewbox = canvas, ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
        RenderOptions.SetCachingHint(brush, CachingHint.Cache);
        return brush;
    }

    private static int LeavingDepth(string id)
    {
        var depth = 0;
        while (id.EndsWith(LeavingSuffix, StringComparison.Ordinal))
        {
            id = id[..^LeavingSuffix.Length];
            depth++;
        }
        return depth;
    }

    /// <summary>The mix pass for a layer this frame (made, or kept from the last frame); null when the layer isn't mixed.</summary>
    private static MixPass? BeginMix(Root root, LayerKind kind)
    {
        if (root.Scene[kind].Mix is not { } mix)
        {
            root.Mixes.Remove(kind);
            return null;
        }
        var shape = Shape(mix);
        if (!root.Mixes.TryGetValue(kind, out var pass) || pass.Shape != shape)
            root.Mixes[kind] = pass = Build(mix);
        pass.Begin();
        return pass;

        static string Shape(LayerMix mix) => mix.Inner is null ? Family(mix) : $"{Family(mix)}>{Shape(mix.Inner)}";
        static string Family(LayerMix mix) => PictureTransitions.ShaderFor(mix.Transition.Kind).ToString();
        static MixPass Build(LayerMix mix) => new(PictureTransitions.ShaderFor(mix.Transition.Kind), mix.Inner is null ? null : Build(mix.Inner));
    }

    /// <summary>This frame's numbers for a mixed layer (and for the frozen picture inside it, if any).</summary>
    private void FinishMix(MixPass pass, LayerMix mix, Rect canvas)
    {
        var scale = RenderScale();
        var pixels = (canvas.Width * scale, canvas.Height * scale);
        MixPass? p = pass;
        LayerMix? m = mix;
        while (p is not null && m is not null)
        {
            p.Fit(canvas);
            p.Effect.Set(PictureTransitions.Mix(m.Transition, m.Progress, canvas.Width / Math.Max(canvas.Height, 1), m.Elapsed, pixels, m.LitBelow));
            p = p.Inner;
            m = m.Inner;
        }
    }

    /// <summary>
    /// Gives each container its children for this frame: the root its items and mix layers in order,
    /// each mix pass its two pictures. A container whose list changed is emptied first, all of them
    /// before any is filled, because an item moving between a picture and the root (a transition
    /// starting or landing) can only have one parent at a time.
    /// </summary>
    private static void Reparent(Root root, List<Visual> order, IEnumerable<MixPass> dropped)
    {
        var wanted = new List<(ContainerVisual Container, List<Visual> Children)> { (root.Visual, order) };
        foreach (var pass in dropped.SelectMany(p => p.Chain()))
        {
            wanted.Add((pass.From, []));
            wanted.Add((pass.To, []));
        }
        foreach (var pass in root.Mixes.Values.SelectMany(p => p.Chain()))
        {
            wanted.Add((pass.From, pass.FromItems));
            wanted.Add((pass.To, pass.ToItems));
        }
        var changed = wanted.Where(w => !Same(w.Container.Children, w.Children)).ToList();
        foreach (var (container, _) in changed) container.Children.Clear();
        foreach (var (container, children) in changed)
            foreach (var visual in children)
                container.Children.Add(visual);

        static bool Same(VisualCollection kids, List<Visual> wanted) =>
            kids.Count == wanted.Count && !wanted.Where((visual, i) => !ReferenceEquals(kids[i], visual)).Any();
    }

    /// <summary>
    /// Set by a capture that draws this view (the program feed, a screen's or a device's feed): true
    /// while it copies the picture with WPF's software drawing instead of the graphics card (the
    /// software copy, HOW-IT-WORKS section 17). The shaders do draw in software (they're ps_2_0), but
    /// slowly: a 1080p frame of a push takes about half a second, a zoom through nearly two, and the
    /// copy runs on the UI thread, so the operator's screen and the outputs would stall with it.
    /// While this is true the view plays a plain Dissolve instead (<see cref="LiveTransitions.InSoftware"/>).
    /// The screens themselves, drawn by the graphics card, still play the real transition.
    /// </summary>
    public Func<bool>? CopiedInSoftware { get; set; }

    /// <summary>
    /// The transitions this view can draw. A picture transition plays instead as:
    /// <list type="bullet">
    /// <item>a plain Dissolve at the same length (<see cref="LiveTransitions.InSoftware"/>) when the view
    /// is copied in software (<see cref="CopiedInSoftware"/>), or when it's a live view (one that plays
    /// video) and WPF draws everything in software on this PC (no graphics card, or some remote desktop
    /// sessions: "rendering tier 0"), where each frame of one would cost too much to keep the screens
    /// moving. The Mac's Blur Dissolve does the same there: its blur is dearer still. Thumbnails and
    /// the picker's small previews still play them there: they're small enough to draw quickly.</item>
    /// <item>the nearest of the Mac's kinds, at the same length (<see cref="LiveTransitions.Fallback"/>),
    /// when this PC can't run its shader at all.</item>
    /// </list>
    /// </summary>
    private Func<LayerKind, LayerTransition?> Drawable(Func<LayerKind, LayerTransition?> transitionFor)
    {
        var software = CopiedInSoftware?.Invoke() == true || PlaysVideo && RenderCapability.Tier >> 16 == 0;
        return layer =>
        {
            var transition = transitionFor(layer);
            if (software) return LiveTransitions.InSoftware(transition);
            if (transition is not { Kind: SceneTransitionKind.Picture, Picture: { } picture }) return transition;
            return TransitionShaders.CanDraw(picture.Kind) ? transition : LiveTransitions.Fallback(transition);
        };
    }

    private bool warmedGraphicsCard;

    private static readonly Brush Speck = Frozen(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)));

    /// <summary>
    /// The first time a live view changes layer by layer, it draws every transition shader once,
    /// out of sight under its own picture and on nothing, so the graphics card has them ready
    /// before the first real transition (each graphics card makes its own, and an output may be on
    /// a different one from the operator's screen). The shaders themselves were compiled when the
    /// app started (<see cref="TransitionShaders.WarmUp"/>).
    /// </summary>
    private void WarmGraphicsCard()
    {
        if (warmedGraphicsCard || !PlaysVideo && !Offscreen) return;
        warmedGraphicsCard = true;
        var holder = new ContainerVisual();
        var tiny = new Rect(0, 0, 8, 8);
        foreach (var family in Enum.GetValues<PictureShader>())
        {
            // An all-but-transparent speck to work on: WPF skips an effect with nothing under it.
            var speck = new DrawingVisual();
            using (var dc = speck.RenderOpen()) dc.DrawRectangle(Speck, null, tiny);
            var pass = new MixPass(family, null);
            pass.To.Children.Add(speck);
            pass.Fit(tiny);
            holder.Children.Add(pass.Layer);
        }
        children.Insert(0, holder);
        var frames = 0;
        void Tick(object? sender, EventArgs e)
        {
            if (++frames < 3) return;
            CompositionTarget.Rendering -= Tick;
            children.Remove(holder);
        }
        CompositionTarget.Rendering += Tick;
    }
}
