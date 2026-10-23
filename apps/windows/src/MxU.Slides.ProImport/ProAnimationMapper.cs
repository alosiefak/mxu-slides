using MxU.Slides.Core.Model;
using RV = Pro.SerializationInterop.RVProtoData;

namespace MxU.Slides.ProImport;

/// <summary>ProPresenter element builds → MxU animation steps.</summary>
internal static class ProAnimationMapper
{
    public static List<AnimationStep> Steps(RV.Slide.Types.Element wrapped, string objectId, string text, List<string> warnings)
    {
        var steps = new List<AnimationStep>();
        if (wrapped.BuildIn is { } buildIn)
        {
            var (animation, configure) = Animation(buildIn.Transition, AnimationKind.In, warnings);
            if (wrapped.ChildBuilds.Count == 0)
            {
                steps.Add(Step(buildIn, AnimationKind.In, animation, configure, Id(buildIn.Uuid, $"{objectId}-in")));
            }
            else
            {
                var lines = text.Split('\n');
                for (var childIndex = 0; childIndex < wrapped.ChildBuilds.Count; childIndex++)
                {
                    var child = wrapped.ChildBuilds[childIndex];
                    var line = (int)child.Index;
                    if (line >= lines.Length || lines[line].Length == 0) continue;
                    var step = new AnimationStep
                    {
                        Id = Id(child.Uuid, $"{objectId}-in-{childIndex}"),
                        Kind = AnimationKind.In, Animation = animation, Trigger = Trigger(child.Start),
                        DelaySeconds = Delay(child.DelayTime), DurationSeconds = Duration(buildIn.Transition),
                        Ranges = [new AnimationRange { Line = line, Column = 0, Length = lines[line].CharacterCount() }],
                    };
                    configure(step);
                    if (child.Start == RV.Slide.Types.Element.Types.Build.Types.Start.WithSlide) step.Trigger = AnimationTrigger.WithPrevious;
                    if (wrapped.RevealType == RV.Slide.Types.Element.Types.TextRevealType.Underline) step.PlaceholderUnderline = true;
                    steps.Add(step);
                }
            }
        }
        if (wrapped.BuildOut is { } buildOut)
        {
            var (animation, configure) = Animation(buildOut.Transition, AnimationKind.Out, warnings);
            if ((buildOut.Transition?.Effect?.Name ?? "").ToLowerInvariant().Contains("reveal"))
            {
                var inEdge = steps.FirstOrDefault(s => s.Kind == AnimationKind.In)?.Edge;
                animation = StepAnimation.Move;
                configure = s =>
                {
                    s.Edge = inEdge ?? s.Edge ?? AnimationEdge.Left;
                    s.WithFade = null;
                };
            }
            steps.Add(Step(buildOut, AnimationKind.Out, animation, configure, Id(buildOut.Uuid, $"{objectId}-out")));
        }
        return steps;
    }

    public static List<string>? AnimationOrder(IList<RV.UUID> animationOrder, List<SlideObject> objects)
    {
        if (animationOrder.Count == 0) return null;
        var objectById = new Dictionary<string, SlideObject>();
        foreach (var obj in objects) objectById.TryAdd(obj.Id, obj);
        var stepIds = objects.SelectMany(o => (o.AnimationSteps ?? []).Select(s => s.Id)).ToHashSet();
        var order = new List<string>();
        foreach (var uuid in animationOrder)
        {
            var id = uuid.Lower();
            if (stepIds.Contains(id))
            {
                order.Add(id);
            }
            else if (objectById.TryGetValue(id, out var obj) && obj.AnimationSteps is { } steps)
            {
                order.AddRange(steps.Where(s => s.Kind != AnimationKind.Out).Select(s => s.Id));
                order.AddRange(steps.Where(s => s.Kind == AnimationKind.Out).Select(s => s.Id));
            }
        }
        var listed = order.ToHashSet();
        order.AddRange(objects.SelectMany(o => (o.AnimationSteps ?? []).Select(s => s.Id)).Where(id => !listed.Contains(id)));
        return order.Count == 0 ? null : order;
    }

    private static AnimationStep Step(
        RV.Slide.Types.Element.Types.Build build, AnimationKind kind, StepAnimation animation,
        Action<AnimationStep> configure, string id)
    {
        var step = new AnimationStep
        {
            Id = id, Kind = kind, Animation = animation, Trigger = Trigger(build.Start),
            DelaySeconds = Delay(build.DelayTime), DurationSeconds = Duration(build.Transition),
        };
        configure(step);
        return step;
    }

    public static AnimationTrigger Trigger(RV.Slide.Types.Element.Types.Build.Types.Start start) => start switch
    {
        RV.Slide.Types.Element.Types.Build.Types.Start.OnClick => AnimationTrigger.OnClick,
        RV.Slide.Types.Element.Types.Build.Types.Start.WithPrevious => AnimationTrigger.WithPrevious,
        RV.Slide.Types.Element.Types.Build.Types.Start.AfterPrevious => AnimationTrigger.AfterPrevious,
        RV.Slide.Types.Element.Types.Build.Types.Start.WithSlide => AnimationTrigger.WithPrevious,
        _ => AnimationTrigger.OnClick,
    };

    public static double Duration(RV.Transition? transition) =>
        transition is { Duration: > 0 } ? SwiftCompat.Rounded(transition.Duration * 1000) / 1000 : 0.5;

    public static double? Delay(double raw) => Math.Abs(raw) < 0.001 ? null : SwiftCompat.Rounded(raw * 1000) / 1000;

    private static string Id(RV.UUID? uuid, string fallback)
    {
        var id = uuid.Lower();
        return id.Length == 0 ? fallback : id;
    }

    public static AnimationEdge? Edge(RV.Transition? transition)
    {
        foreach (var variable in transition?.Effect?.Variables ?? [])
        {
            if (variable.TypeCase != RV.Effect.Types.EffectVariable.TypeOneofCase.Direction) continue;
            return variable.Direction.Direction switch
            {
                RV.Effect.Types.EffectVariable.Types.EffectDirection.Types.EffectDirection.Left
                    or RV.Effect.Types.EffectVariable.Types.EffectDirection.Types.EffectDirection.TopLeft
                    or RV.Effect.Types.EffectVariable.Types.EffectDirection.Types.EffectDirection.BottomLeft => AnimationEdge.Left,
                RV.Effect.Types.EffectVariable.Types.EffectDirection.Types.EffectDirection.Right
                    or RV.Effect.Types.EffectVariable.Types.EffectDirection.Types.EffectDirection.TopRight
                    or RV.Effect.Types.EffectVariable.Types.EffectDirection.Types.EffectDirection.BottomRight => AnimationEdge.Right,
                RV.Effect.Types.EffectVariable.Types.EffectDirection.Types.EffectDirection.Top => AnimationEdge.Top,
                RV.Effect.Types.EffectVariable.Types.EffectDirection.Types.EffectDirection.Bottom => AnimationEdge.Bottom,
                _ => null,
            };
        }
        return null;
    }

    public static (StepAnimation Animation, Action<AnimationStep> Configure) Animation(
        RV.Transition? transition, AnimationKind kind, List<string> warnings)
    {
        var name = (transition?.Effect?.Name ?? "").TrimmingWhitespaces();
        var lowered = name.ToLowerInvariant();
        bool Has(string word) => lowered.Contains(word, StringComparison.Ordinal);

        var edge = Edge(transition)
            ?? (Has("left") ? AnimationEdge.Left : Has("right") ? AnimationEdge.Right
                : Has("top") || Has("up") ? AnimationEdge.Top
                : Has("bottom") || Has("down") ? AnimationEdge.Bottom : null);
        if (lowered.Length == 0 || lowered == "none" || lowered == "cut") return (StepAnimation.Fade, s => s.DurationSeconds = 0);
        if (Has("blur")) return (StepAnimation.Blur, s => s.WithFade = true);
        if (Has("dissolve") || Has("fade") || Has("cross")) return (StepAnimation.Fade, _ => { });
        if (Has("wipe") || Has("reveal") || Has("iris")) return (StepAnimation.Wipe, s => s.Edge = edge ?? AnimationEdge.Left);
        if (Has("move") || Has("fly") || Has("slide") || Has("push") || Has("swipe"))
            return (StepAnimation.Move, s =>
            {
                s.Edge = edge ?? AnimationEdge.Left;
                s.WithFade = true;
            });
        if (Has("zoom") || Has("scale") || Has("grow") || Has("pop") || Has("bounce"))
            return (StepAnimation.Scale, s =>
            {
                s.FromScale = Has("out") && kind == AnimationKind.In ? 1.3 : 0.7;
                s.WithFade = true;
            });
        if (Has("burn") || Has("flash") || Has("light")) return (StepAnimation.Burn, s => s.Amount = 0.6);
        if (Has("glitch") || Has("static") || Has("noise")) return (StepAnimation.Glitch, s => s.Amount = 0.6);
        if (Has("type")) return (StepAnimation.Type, s => s.Cursor = true);
        if (Has("draw") || Has("stroke")) return (StepAnimation.Draw, _ => { });
        var warning = $"animation \"{name}\" has no MxU equivalent; imported as a fade";
        if (!warnings.Contains(warning)) warnings.Add(warning);
        return (StepAnimation.Fade, _ => { });
    }
}
