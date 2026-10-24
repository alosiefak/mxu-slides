using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Animation;

public enum SceneTransitionKind { Cut, Dissolve, Fade, BlurDissolve, FilmBurn }

/// <summary>How a layer changes between scenes. <see cref="Plate"/> is the colour a Fade passes through.</summary>
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

    public static readonly SceneTransition Cut = new(SceneTransitionKind.Cut, 0.05);

    public static SceneTransition Fade(SceneColor plate, double duration = 0.5) => new(SceneTransitionKind.Fade, duration, plate);
}

/// <summary>
/// Cross-fades layers whose membership changes between pushed scenes (a content
/// edit with the same item ids never fades). Thread-safe: the app pushes, the
/// render loop pulls <see cref="Scene"/> each frame. Times are host-time seconds.
/// Port of RenderEngine/SceneTransitionEngine.swift.
/// </summary>
public sealed class SceneTransitionEngine
{
    private sealed record Active(AnimatedScene Previous, double StartedAt, IReadOnlyDictionary<LayerKind, SceneTransition> Transitions, double Duration);

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
            foreach (var layer in active.Previous.Layers.Where(l => active.Transitions.ContainsKey(l.Kind)))
                foreach (var item in layer.Items)
                    if (MediaId(item.Render.Content) is { } id) ids.Add(id);
            return ids;
        }
    }

    public void Push(AnimatedScene scene, SceneTransition transition, double now) => Push(scene, _ => transition, now);

    public void Push(AnimatedScene scene, Func<LayerKind, SceneTransition?> transitionFor, double now)
    {
        lock (gate)
        {
            var changed = ChangedLayers(target, scene);
            if (changed.Count == 0)
            {
                target = scene;
                return;
            }
            var transitions = new Dictionary<LayerKind, SceneTransition>();
            foreach (var layer in changed)
                if (transitionFor(layer) is { Kind: not SceneTransitionKind.Cut } transition) transitions[layer] = transition;
            if (transitions.Count == 0)
            {
                target = scene;
                active = null;
                return;
            }
            var from = active is null ? target : Composite(active.Previous, target, active.Transitions, Progress(active, now));
            active = new Active(from, now, transitions, transitions.Values.Max(t => t.Duration));
            target = scene;
        }
    }

    /// <summary>The scene to draw at <paramref name="now"/>; the fade ends (and is dropped) once it lands.</summary>
    public AnimatedScene Scene(double now)
    {
        lock (gate)
        {
            if (active is null) return target;
            var progress = Progress(active, now);
            if (progress >= 1)
            {
                active = null;
                return target;
            }
            return Composite(active.Previous, target, active.Transitions, progress);
        }
    }

    /// <summary>True while a transition is still playing at <paramref name="now"/>, so the view keeps drawing frames.</summary>
    public bool IsTransitioning(double now)
    {
        lock (gate) return active is not null && Progress(active, now) < 1;
    }

    /// <summary>The newest scene pushed (what shows once any transition lands).</summary>
    public AnimatedScene Target
    {
        get { lock (gate) return target; }
    }

    private static double Progress(Active active, double now) => Math.Clamp((now - active.StartedAt) / active.Duration, 0, 1);

    internal static string? MediaId(ItemContent content) => content switch
    {
        ItemContent.Media media => media.Id,
        ItemContent.Shape { Style.Fill: SceneFill.Media fill } => fill.Id,
        _ => null,
    };

    /// <summary>An item's identity across scenes: its id, plus its media so a swap on a stable id still fades.</summary>
    internal static string MembershipKey(AnimatedItem item) =>
        MediaId(item.Render.Content) is { } id ? $"{item.Id}|{id}" : item.Id;

    internal static HashSet<LayerKind> ChangedLayers(AnimatedScene old, AnimatedScene @new)
    {
        var changed = new HashSet<LayerKind>();
        foreach (var kind in LayerKinds.All)
            if (!old[kind].Items.Select(MembershipKey).ToHashSet().SetEquals(@new[kind].Items.Select(MembershipKey)))
                changed.Add(kind);
        return changed;
    }

    internal static AnimatedScene Composite(AnimatedScene previous, AnimatedScene target, SceneTransition transition, double progress, IEnumerable<LayerKind> layers) =>
        Composite(previous, target, layers.ToDictionary(layer => layer, _ => transition), progress);

    internal static AnimatedScene Composite(AnimatedScene previous, AnimatedScene target, IReadOnlyDictionary<LayerKind, SceneTransition> transitions, double progress)
    {
        var scene = target.Copy();
        foreach (var layer in scene.Layers)
        {
            if (!transitions.TryGetValue(layer.Kind, out var transition)) continue;
            var oldItems = previous[layer.Kind].Items;
            var newItems = layer.Items.ToList();
            var oldKeys = oldItems.Select(MembershipKey).ToHashSet();
            var newKeys = newItems.Select(MembershipKey).ToHashSet();

            var leaving = oldItems
                .Where(item => !newKeys.Contains(MembershipKey(item)))
                .Select(item => item with { Render = item.Render with { Id = item.Id + "::leaving" } })
                .ToList();
            List<AnimatedItem> ArrivingFaded(IEnumerable<AnimatedItem> items, double opacity) => items
                .Select(item => oldKeys.Contains(MembershipKey(item)) ? item : Faded(item, opacity))
                .ToList();

            var hasArrivals = newItems.Any(item => !oldKeys.Contains(MembershipKey(item)));
            var holdsUnder = hasArrivals && layer.Kind is LayerKind.LoopingVideos or LayerKind.StillGraphics or LayerKind.Videos;
            List<AnimatedItem> items;
            switch (transition.Kind)
            {
                case SceneTransitionKind.Cut:
                    continue;
                case SceneTransitionKind.Dissolve:
                    items = [.. holdsUnder ? leaving : leaving.Select(i => Faded(i, 1 - progress)), .. ArrivingFaded(newItems, progress)];
                    break;
                case SceneTransitionKind.Fade:
                    var firstHalf = progress < 0.5;
                    var plateOpacity = firstHalf ? progress * 2 : (1 - progress) * 2;
                    items = [.. firstHalf ? oldItems : newItems, Plate(layer.Kind, scene.CanvasSize, transition.Plate.WithAlpha(plateOpacity))];
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
                        Plate(layer.Kind, scene.CanvasSize, new SceneColor(1, 0.72, 0.42, peak * 0.35)),
                    ];
                    break;
            }
            layer.Items.Clear();
            layer.Items.AddRange(items);
        }
        return scene;
    }

    private static AnimatedItem Plate(LayerKind layer, SceneSize canvas, SceneColor color) => new RenderItem
    {
        Id = $"transition-plate-{layer.Raw()}",
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
