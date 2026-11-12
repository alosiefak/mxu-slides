using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Animation;

/// <summary>
/// How a layer changes. <see cref="Picture"/> is the Windows-only kinds (Phase 6): the layer's whole
/// old and new pictures, which the view mixes on the graphics card (<see cref="PictureTransitions"/>).
/// </summary>
public enum SceneTransitionKind { Cut, Dissolve, Fade, BlurDissolve, FilmBurn, Picture }

/// <summary>
/// How a layer changes between scenes. <see cref="Plate"/> is the colour a Fade passes through;
/// <see cref="Picture"/> is the Windows-only transition a <see cref="SceneTransitionKind.Picture"/> plays.
/// </summary>
public sealed record SceneTransition
{
    public SceneTransition(SceneTransitionKind kind, double duration = 0.5, SceneColor? plate = null)
    {
        Kind = kind;
        Duration = Math.Clamp(duration, 0.05, 30);
        Plate = plate ?? SceneColor.Black;
    }

    public SceneTransitionKind Kind { get; }
    public double Duration { get; }
    public SceneColor Plate { get; }
    public WindowsTransition? Picture { get; init; }

    public static readonly SceneTransition Cut = new(SceneTransitionKind.Cut, 0.05);

    public static SceneTransition Fade(SceneColor plate, double duration = 0.5) => new(SceneTransitionKind.Fade, duration, plate);

    /// <summary>A Windows-only transition, drawn by the view from the layer's two pictures.</summary>
    public static SceneTransition Of(WindowsTransition picture) =>
        new(SceneTransitionKind.Picture, picture.DurationSeconds) { Picture = picture };
}

/// <summary>
/// Cross-fades layers whose membership changes between pushed scenes (a content
/// edit with the same item ids never fades). Thread-safe: the app pushes, the
/// render loop pulls <see cref="Scene"/> each frame. Times are host-time seconds.
/// Port of RenderEngine/SceneTransitionEngine.swift.
/// </summary>
public sealed class SceneTransitionEngine
{
    private sealed record Active(AnimatedScene Previous, double StartedAt, IReadOnlyDictionary<string, SceneTransition> Transitions, double Duration);

    private readonly Lock gate = new();
    private AnimatedScene target;
    private Active? active;

    public SceneTransitionEngine(AnimatedScene? initial = null) =>
        target = initial ?? new AnimatedScene(SceneSize.Hd);

    /// <summary>Media the outgoing side still shows, so players aren't released mid-fade.</summary>
    public IReadOnlySet<string> HeldMediaIds(double now)
    {
        lock (gate)
        {
            var ids = new HashSet<string>();
            if (active is null || now - active.StartedAt >= active.Duration) return ids;
            foreach (var layer in active.Previous.Layers.Where(l => active.Transitions.ContainsKey(l.Id)))
                foreach (var item in layer.Items)
                    if (MediaId(item.Render.Content) is { } id) ids.Add(id);
            return ids;
        }
    }

    public void Push(AnimatedScene scene, SceneTransition transition, double now) => Push(scene, (string _) => transition, now);

    /// <summary>
    /// Pushes with a transition per built-in layer. A custom layer (Screen Map) holds fired media,
    /// so it changes the way Foreground Videos does.
    /// </summary>
    public void Push(AnimatedScene scene, Func<LayerKind, SceneTransition?> transitionFor, double now) =>
        Push(scene, (string id) => transitionFor(LayerKinds.Parse(id) ?? LayerKind.Videos), now);

    public void Push(AnimatedScene scene, Func<string, SceneTransition?> transitionFor, double now)
    {
        lock (gate)
        {
            var changed = ChangedLayers(target, scene);
            if (changed.Count == 0)
            {
                target = scene;
                return;
            }
            var transitions = new Dictionary<string, SceneTransition>(StringComparer.Ordinal);
            foreach (var layer in changed)
                if (transitionFor(layer) is { Kind: not SceneTransitionKind.Cut } transition) transitions[layer] = transition;
            if (transitions.Count == 0)
            {
                target = scene;
                active = null;
                return;
            }
            // Interrupted: start from where every layer had got to, its whole-stack mixes flattened.
            var from = active is null ? target : Flattened(Composite(active.Previous, target, active.Transitions, now - active.StartedAt));
            active = new Active(from, now, transitions, transitions.Values.Max(t => t.Duration));
            target = scene;
        }
    }

    /// <summary>The scene to draw at <paramref name="now"/>; the fade ends (and is dropped) once every layer lands.</summary>
    public AnimatedScene Scene(double now)
    {
        lock (gate)
        {
            if (active is null) return target;
            if (now - active.StartedAt >= active.Duration)
            {
                active = null;
                return target;
            }
            return Composite(active.Previous, target, active.Transitions, now - active.StartedAt);
        }
    }

    /// <summary>True while a transition is still playing at <paramref name="now"/>, so the view keeps drawing frames.</summary>
    public bool IsTransitioning(double now)
    {
        lock (gate) return active is not null && now - active.StartedAt < active.Duration;
    }

    /// <summary>The newest scene pushed (what shows once any transition lands).</summary>
    public AnimatedScene Target
    {
        get { lock (gate) return target; }
    }

    internal static string? MediaId(ItemContent content) => content switch
    {
        ItemContent.Media media => media.Id,
        ItemContent.Shape { Style.Fill: SceneFill.Media fill } => fill.Id,
        _ => null,
    };

    /// <summary>An item's identity across scenes: its id, plus its media so a swap on a stable id still fades.</summary>
    internal static string MembershipKey(AnimatedItem item) =>
        MediaId(item.Render.Content) is { } id ? $"{item.Id}|{id}" : item.Id;

    /// <summary>The ids of the layers (built-in and custom) whose items differ between the scenes.</summary>
    internal static HashSet<string> ChangedLayers(AnimatedScene old, AnimatedScene @new)
    {
        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in old.Layers.Select(l => l.Id).Concat(@new.Layers.Select(l => l.Id)).Distinct())
        {
            var before = old.Layer(id)?.Items.Select(MembershipKey).ToHashSet() ?? [];
            var after = @new.Layer(id)?.Items.Select(MembershipKey) ?? [];
            if (!before.SetEquals(after)) changed.Add(id);
        }
        return changed;
    }

    /// <summary>One transition on some layers, at a share of its length (tests and the Phase 6 previews).</summary>
    internal static AnimatedScene Composite(AnimatedScene previous, AnimatedScene target, SceneTransition transition, double progress, IEnumerable<LayerKind> layers) =>
        Composite(previous, target, layers.ToDictionary(layer => layer.Raw(), _ => transition, StringComparer.Ordinal), progress * transition.Duration);

    /// <summary>
    /// The scene <paramref name="elapsed"/> seconds into its transitions. Each layer runs on its own
    /// length (a quick background fade lands while the words still dissolve), and a layer whose
    /// transition is over shows the target as it is.
    /// </summary>
    internal static AnimatedScene Composite(AnimatedScene previous, AnimatedScene target, IReadOnlyDictionary<string, SceneTransition> transitions, double elapsed)
    {
        var scene = target.Copy();
        var order = scene.Layers.Select(l => l.Id).ToList();
        foreach (var layer in scene.Layers)
        {
            if (!transitions.TryGetValue(layer.Id, out var transition)) continue;
            var progress = Math.Clamp(elapsed / transition.Duration, 0, 1);
            if (progress >= 1) continue;
            var oldLayer = previous.Layer(layer.Id);
            IReadOnlyList<AnimatedItem> oldLayerItems = oldLayer?.Items ?? [];
            if (transition.Kind == SceneTransitionKind.Picture && transition.Picture is { } picture)
            {
                // The whole old picture and the whole new one, for the view's shader to mix: every item
                // the layer showed comes along as a ::leaving copy (staying ones too, so each picture is
                // complete). If the old picture was itself mid-transition, it is drawn as it was then.
                layer.Items.Clear();
                layer.Items.AddRange([.. oldLayerItems.Select(Leaving), .. target.Layer(layer.Id)?.Items ?? []]);
                layer.CrossfadeProgress = null;
                // A light leak on a layer lower down already lights the screen: this one lights only its own pixels.
                var index = order.IndexOf(layer.Id);
                var litBelow = picture.Kind == WindowsTransitionKind.LightLeak
                    && transitions.Any(other => order.IndexOf(other.Key) is var at && at >= 0 && at < index
                        && other.Value.Picture?.Kind == WindowsTransitionKind.LightLeak);
                layer.Mix = new LayerMix(picture, progress, progress * transition.Duration, oldLayer?.Mix, litBelow);
                continue;
            }
            // A picture transition that was interrupted by one of the Mac's kinds: its two pictures
            // can't be mixed by an item fade, so they're cross-faded where they had got to.
            var oldItems = oldLayer?.Mix is { } interrupted ? Flattened(oldLayerItems, interrupted) : oldLayerItems;
            var newItems = layer.Items.ToList();
            var oldKeys = oldItems.Select(MembershipKey).ToHashSet();
            var newKeys = newItems.Select(MembershipKey).ToHashSet();

            var leaving = oldItems
                .Where(item => !newKeys.Contains(MembershipKey(item)))
                .Select(Leaving)
                .ToList();
            List<AnimatedItem> ArrivingFaded(IEnumerable<AnimatedItem> items, double opacity) => items
                .Select(item => oldKeys.Contains(MembershipKey(item)) ? item : Faded(item, opacity))
                .ToList();
            // The whole old stack (staying items too), for a dissolve that mixes two pictures.
            List<AnimatedItem> Outgoing() => [.. oldItems.Select(item => Leaving(item) with { CrossfadeLeaving = true })];

            var isMedia = layer.IsMediaLayer;
            var hasArrivals = newItems.Any(item => !oldKeys.Contains(MembershipKey(item)));
            var holdsUnder = hasArrivals && isMedia;
            List<AnimatedItem> items;
            switch (transition.Kind)
            {
                case SceneTransitionKind.Cut:
                    continue;
                case SceneTransitionKind.Dissolve when !isMedia:
                    // Words, overlays, alerts: each stack fades as one picture, so an opaque backdrop
                    // leaving never lets what it covered show through its fading words.
                    items = [.. Outgoing(), .. newItems];
                    layer.CrossfadeProgress = progress;
                    break;
                case SceneTransitionKind.BlurDissolve when !isMedia:
                    var swellMix = (1 - Math.Abs(progress * 2 - 1)) * 60;
                    items = [.. Blurred(Outgoing(), swellMix), .. Blurred(newItems, swellMix)];
                    layer.CrossfadeProgress = progress;
                    break;
                case SceneTransitionKind.Dissolve:
                    items = [.. holdsUnder ? leaving : leaving.Select(i => Faded(i, 1 - progress)), .. ArrivingFaded(newItems, progress)];
                    break;
                case SceneTransitionKind.Fade:
                    var firstHalf = progress < 0.5;
                    var plateOpacity = firstHalf ? progress * 2 : (1 - progress) * 2;
                    items = [.. firstHalf ? oldItems : newItems, Plate(layer.Id, scene.CanvasSize, transition.Plate.WithAlpha(plateOpacity))];
                    break;
                case SceneTransitionKind.BlurDissolve:
                    var swell = (1 - Math.Abs(progress * 2 - 1)) * 60;
                    items =
                    [
                        .. Blurred(holdsUnder ? leaving : leaving.Select(i => Faded(i, 1 - progress)), swell),
                        .. Blurred(ArrivingFaded(newItems, progress), swell),
                    ];
                    break;
                default:
                    var peak = 1 - Math.Abs(progress * 2 - 1);
                    items =
                    [
                        .. Burned(leaving.Select(i => Faded(i, 1 - progress)), peak, progress),
                        .. Burned(ArrivingFaded(newItems, progress), peak, progress),
                        Plate(layer.Id, scene.CanvasSize, new SceneColor(1, 0.72, 0.42, peak * 0.35)),
                    ];
                    break;
            }
            layer.Items.Clear();
            layer.Items.AddRange(items);
        }
        return scene;
    }

    /// <summary>
    /// A scene with its whole-stack mixes turned into item fades where they had got to (an item in
    /// both stacks shows once, at full): the starting point when a new push interrupts a dissolve.
    /// Port of SceneTransitionEngine.flattened.
    /// </summary>
    internal static AnimatedScene Flattened(AnimatedScene scene)
    {
        if (scene.Layers.All(l => l.CrossfadeProgress is null)) return scene;
        var flat = scene.Copy();
        foreach (var layer in flat.Layers)
        {
            if (layer.CrossfadeProgress is not { } progress) continue;
            string Key(AnimatedItem item) => MembershipKey(item.CrossfadeLeaving
                ? item with { Render = item.Render with { Id = item.Id[..^LeavingSuffix.Length] } }
                : item);
            var before = layer.Items.Where(i => i.CrossfadeLeaving).Select(Key).ToHashSet();
            var after = layer.Items.Where(i => !i.CrossfadeLeaving).Select(Key).ToHashSet();
            var items = new List<AnimatedItem>();
            foreach (var item in layer.Items)
            {
                var key = Key(item);
                if (before.Contains(key) && after.Contains(key))
                {
                    if (!item.CrossfadeLeaving) items.Add(item);
                    continue;
                }
                items.Add(Faded(item, item.CrossfadeLeaving ? 1 - progress : progress) with { CrossfadeLeaving = false });
            }
            layer.CrossfadeProgress = null;
            layer.Items.Clear();
            layer.Items.AddRange(items);
        }
        return flat;
    }

    internal const string LeavingSuffix = "::leaving";

    private static AnimatedItem Leaving(AnimatedItem item) => item with { Render = item.Render with { Id = item.Id + LeavingSuffix } };

    /// <summary>
    /// A mixed layer's items as a plain cross-fade at the point the mix had reached: the new
    /// picture's items (no ::leaving) faded in by how far it had got, the old picture's faded out.
    /// </summary>
    internal static List<AnimatedItem> Flattened(IReadOnlyList<AnimatedItem> items, LayerMix mix)
    {
        var shown = TransitionCurves.Eased(mix.Transition.Kind, mix.Progress);
        return [.. items.Select(item => Faded(item, item.Id.EndsWith(LeavingSuffix, StringComparison.Ordinal) ? 1 - shown : shown))];
    }

    private static AnimatedItem Plate(string layerId, SceneSize canvas, SceneColor color) => new RenderItem
    {
        Id = $"transition-plate-{layerId}",
        Frame = new SceneRect(canvas),
        Content = new ItemContent.Shape(new ShapeStyle(SceneShapeKind.Rect, new SceneFill.Solid(color))),
    };

    private static AnimatedItem Faded(AnimatedItem item, double opacity) =>
        item with { Render = item.Render with { Opacity = item.Render.Opacity * opacity } };

    private static IEnumerable<AnimatedItem> Burned(IEnumerable<AnimatedItem> items, double amount, double phase) =>
        amount <= 0.01 ? items : items.Select(item => item with { Effects = [new AnimationEffect.Burn(amount, phase), .. item.Effects] });

    private static IEnumerable<AnimatedItem> Blurred(IEnumerable<AnimatedItem> items, double radius) =>
        radius <= 0.5 ? items : items.Select(item => item with { Effects = [new AnimationEffect.Blur(radius), .. item.Effects] });
}
