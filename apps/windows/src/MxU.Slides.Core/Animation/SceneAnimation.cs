using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Animation;

// The scene-level animation vocabulary: the scheduled steps a render item
// carries, the live clock they run against, and the phase/visibility the
// timeline derives from them. Mirrors the animation types in
// RenderEngine/SceneModel.swift and RenderEngine/AnimationTimeline.swift.
// Times are host-time seconds; nothing here reads a wall clock.

public enum SceneAnimationKind { Enter, Exit, Emphasis, Morph }

public enum SceneStepAnimation { Fade, Move, Scale, Wipe, Blur, Burn, Glitch, Draw, Type, Pulse, Color }

public enum SceneAnimationRamp { None, EaseIn, EaseOut, EaseInOut }

public static class SceneAnimationRamps
{
    public static double Apply(this SceneAnimationRamp ramp, double t)
    {
        var x = Math.Clamp(t, 0, 1);
        return ramp switch
        {
            SceneAnimationRamp.EaseIn => x * x,
            SceneAnimationRamp.EaseOut => 1 - (1 - x) * (1 - x),
            SceneAnimationRamp.EaseInOut => x * x * (3 - 2 * x),
            _ => x,
        };
    }
}

public enum SceneAnimationEdge { Left, Right, Top, Bottom }

public readonly record struct SceneVector(double Dx, double Dy)
{
    public static readonly SceneVector Zero = new(0, 0);

    public static SceneVector operator +(SceneVector a, SceneVector b) => new(a.Dx + b.Dx, a.Dy + b.Dy);
    public static SceneVector operator *(SceneVector a, double s) => new(a.Dx * s, a.Dy * s);
}

/// <summary>A run of characters (Swift Characters, i.e. grapheme clusters) on one line of a text item.</summary>
public readonly record struct SceneAnimationRange(int Line, int Column, int Length);

public enum SceneAnimationGroupKind { Auto, Click, Exit }

/// <summary>Which instant a step keys off: the fire (auto), the nth click, or the exit/dismiss.</summary>
public readonly record struct SceneAnimationGroup(SceneAnimationGroupKind Kind, int Index = 0)
{
    public static readonly SceneAnimationGroup Auto = new(SceneAnimationGroupKind.Auto);
    public static readonly SceneAnimationGroup Exit = new(SceneAnimationGroupKind.Exit);
    public static SceneAnimationGroup Click(int index) => new(SceneAnimationGroupKind.Click, index);

    public bool IsClick => Kind == SceneAnimationGroupKind.Click;

    public override string ToString() => Kind == SceneAnimationGroupKind.Click ? $"click({Index})" : Kind.ToString().ToLowerInvariant();
}

public enum SceneVideoPushAlignment { Center, Left, Right, Top, Bottom }

public enum SceneVideoPushMode { Fill, Fit, BlurBackground }

/// <summary>A step that squeezes the video layers into the space its object leaves free.</summary>
public sealed record SceneVideoPush(
    SceneVideoPushAlignment Alignment = SceneVideoPushAlignment.Center,
    SceneVideoPushMode Mode = SceneVideoPushMode.Fill,
    double Zoom = 1,
    double Margin = 0,
    bool Backdrop = false,
    double BlurRadius = 36);

public enum SceneScrollAxis { Up, Down, Left, Right }

/// <summary>A text block that rolls through its frame like credits (StyledText.scroll on the Mac).</summary>
public sealed record SceneBlockScroll(
    double Speed,
    SceneScrollAxis Axis = SceneScrollAxis.Up,
    int Passes = 0,
    SceneAnimationRamp Ramp = SceneAnimationRamp.None,
    bool RestAtEnd = false,
    double FadeTowardTop = 0);

/// <summary>One scheduled animation step on a render item. Port of SceneAnimationStep.</summary>
public sealed record SceneAnimationStep
{
    private readonly double duration;

    public required string Id { get; init; }
    public required SceneAnimationKind Kind { get; init; }
    public required SceneStepAnimation Animation { get; init; }
    public required SceneAnimationGroup Group { get; init; }

    /// <summary>Seconds after the group's instant.</summary>
    public double StartOffset { get; init; }
    public required double Duration { get => duration; init => duration = Math.Max(value, 0); }
    public SceneAnimationRamp Ramp { get; init; } = SceneAnimationRamp.EaseOut;
    public bool WithFade { get; init; }

    /// <summary>Text ranges the step animates; null animates the whole object.</summary>
    public IReadOnlyList<SceneAnimationRange>? Ranges { get; init; }
    public SceneAnimationEdge? Edge { get; init; }
    public SceneVector Offset { get; init; }
    public double FromScale { get; init; } = 0.95;
    public double Amount { get; init; } = 0.6;
    public double SoftEdge { get; init; }
    public bool Cursor { get; init; } = true;
    public SceneColor? Color { get; init; }
    public bool PlaceholderUnderline { get; init; }
    public double DrawStart { get; init; }
    public bool Reverse { get; init; }

    /// <summary>A morph's end state.</summary>
    public AnimatedItem? ToItem { get; init; }

    /// <summary>An In's custom start pose.</summary>
    public AnimatedItem? FromItem { get; init; }
    public SceneVideoPush? VideoPush { get; init; }

    public bool TargetsWholeObject => Ranges is null || Ranges.Count == 0;
}

/// <summary>
/// The live clock an item's steps run against: when it fired, when each click
/// was consumed, and when it was dismissed or its exit was pressed.
/// </summary>
public sealed record AnimationContext
{
    public AnimationContext(
        double anchorHostTime, IReadOnlyList<double>? clickHostTimes = null, double? dismissHostTime = null,
        double? exitHostTime = null, double? frozenHostTime = null, bool autoSettled = false)
    {
        AnchorHostTime = anchorHostTime;
        ClickHostTimes = clickHostTimes ?? [];
        DismissHostTime = dismissHostTime;
        ExitHostTime = exitHostTime;
        FrozenHostTime = frozenHostTime;
        AutoSettled = autoSettled;
    }

    /// <summary>The fire instant: the auto group's start.</summary>
    public double AnchorHostTime { get; init; }

    /// <summary>The instant each consumed click was pressed, in order.</summary>
    public IReadOnlyList<double> ClickHostTimes { get; init; }

    /// <summary>The clear/dismiss instant (the exit group plays, then the cue leaves).</summary>
    public double? DismissHostTime { get; init; }

    /// <summary>The exit press (the final advance) when the slide has an exit group.</summary>
    public double? ExitHostTime { get; init; }

    /// <summary>A fixed clock (editor scrubbing, thumbnails) that overrides the render time.</summary>
    public double? FrozenHostTime { get; init; }

    /// <summary>The auto group counts as already played (a slide that arrives settled).</summary>
    public bool AutoSettled { get; init; }

    /// <summary>Every click consumed, frozen far in the future: the end of the sequence (thumbnails).</summary>
    public static readonly AnimationContext Settled = new(0, Enumerable.Repeat(0.0, 256).ToArray(), frozenHostTime: 1e9);

    public double? Start(SceneAnimationGroup group) => group.Kind switch
    {
        SceneAnimationGroupKind.Auto => AnchorHostTime,
        SceneAnimationGroupKind.Click => group.Index < ClickHostTimes.Count ? ClickHostTimes[group.Index] : null,
        _ => ExitHostTime ?? DismissHostTime,
    };

    public bool Equals(AnimationContext? other) =>
        other is not null && AnchorHostTime == other.AnchorHostTime && ClickHostTimes.SequenceEqual(other.ClickHostTimes)
        && DismissHostTime == other.DismissHostTime && ExitHostTime == other.ExitHostTime
        && FrozenHostTime == other.FrozenHostTime && AutoSettled == other.AutoSettled;

    public override int GetHashCode() =>
        HashCode.Combine(AnchorHostTime, ClickHostTimes.Count, DismissHostTime, ExitHostTime, FrozenHostTime, AutoSettled);
}

public enum AnimationPhaseKind { Pending, Running, Done }

/// <summary>Where a step is at a given instant; <see cref="Value"/> is the eased progress while running.</summary>
public readonly record struct AnimationStepPhase(AnimationPhaseKind Kind, double Value = 0)
{
    public static readonly AnimationStepPhase Pending = new(AnimationPhaseKind.Pending);
    public static readonly AnimationStepPhase Done = new(AnimationPhaseKind.Done);
    public static AnimationStepPhase Running(double progress) => new(AnimationPhaseKind.Running, progress);

    public double Progress => Kind switch
    {
        AnimationPhaseKind.Pending => 0,
        AnimationPhaseKind.Running => Value,
        _ => 1,
    };

    public bool HasStarted => Kind != AnimationPhaseKind.Pending;
    public bool IsDone => Kind == AnimationPhaseKind.Done;

    public bool IsRunning(out double progress)
    {
        progress = Value;
        return Kind == AnimationPhaseKind.Running;
    }
}

/// <summary>What the steps hide right now: the whole object, or ranges of its text.</summary>
public sealed record AnimationVisibility(
    bool HidesWholeObject,
    IReadOnlyList<SceneAnimationRange> HiddenRanges,
    IReadOnlyList<SceneAnimationRange> PlaceholderRanges)
{
    public static readonly AnimationVisibility Shown = new(false, [], []);
}
