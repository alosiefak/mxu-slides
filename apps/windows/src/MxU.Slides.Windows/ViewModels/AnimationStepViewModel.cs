using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>A choice in one of the step editor's lists: a value and the words shown for it.</summary>
public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One step in the overlay editor's list of animations, under its group's heading.</summary>
public sealed partial class StepRowViewModel(string stepId, string group) : ObservableObject
{
    public string StepId { get; } = stepId;

    /// <summary>"When it goes up", "On click 1" or "When it's taken down" (the list is grouped by this).</summary>
    public string Group { get; } = group;

    [ObservableProperty]
    public partial string Title { get; set; } = "";

    [ObservableProperty]
    public partial string Timing { get; set; } = "";

    [ObservableProperty]
    public partial string? Warning { get; set; }

    /// <summary>The colour of the step's kind (In green, Out orange, emphasis purple, morph blue), as the Mac shows it.</summary>
    [ObservableProperty]
    public partial string KindColor { get; set; } = "#28A05C";

    public static string ColorOf(AnimationKind kind) => kind switch
    {
        AnimationKind.In => "#299E5C",
        AnimationKind.Out => "#D97829",
        AnimationKind.Emphasis => "#8C61D9",
        _ => "#338CD9",
    };
}

/// <summary>
/// The settings of the animation step picked in the overlay editor: when it plays, how
/// fast, its delay and easing, and the settings of its effect (where a slide comes
/// from, how soft a wipe's edge is…). Every change is written straight onto the step.
/// Port of the Mac's AnimationStepStrip and AnimationStepEditor (AnimationInspector.swift).
/// </summary>
public sealed partial class AnimationStepViewModel : ObservableObject
{
    private readonly IAnimatedComposition composition;
    private readonly Action changed;

    public AnimationStepViewModel(IAnimatedComposition composition, string stepId, Action changed)
    {
        this.composition = composition;
        StepId = stepId;
        this.changed = changed;
    }

    public string StepId { get; }

    private (string ObjectId, AnimationStep Step)? Found => AnimationEditing.Find(composition, StepId);

    private AnimationStep Step => Found?.Step ?? new AnimationStep { Id = StepId, Kind = AnimationKind.In, Animation = StepAnimation.Fade, DurationSeconds = 0.5 };

    private SlideObject? Owner => Found is { } f ? composition.Objects.FirstOrDefault(o => o.Id == f.ObjectId) : null;

    public bool Exists => Found is not null;

    /// <summary>Re-reads everything after the step changed elsewhere (re-timed, moved, its object renamed).</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);

    private void Write(Action<AnimationStep> change)
    {
        if (!AnimationEditing.Update(composition, StepId, change)) return;
        OnPropertyChanged(string.Empty);
        changed();
    }

    private static string Num(double value) => Math.Round(value, 2).ToString(CultureInfo.CurrentCulture);

    private static double? Parse(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v)
            ? double.IsFinite(v) ? v : null
            : null;

    // MARK: What it is

    public string Title => $"{AnimationEditing.TargetLabel(Owner, Step)} · {AnimationEditing.Sentence(Step, Owner)}";

    public string? Warning => AnimationEditing.OverlayWarnings(composition).GetValueOrDefault(StepId);

    public bool IsMorph => Step.Kind == AnimationKind.Morph;

    public IReadOnlyList<Choice<AnimationKind>> Kinds { get; } =
    [
        new(AnimationKind.In, "Bring in"),
        new(AnimationKind.Out, "Take out"),
        new(AnimationKind.Emphasis, "Emphasize"),
    ];

    /// <summary>The kind of step; changing it keeps the effect when the new kind has it, else uses the first one.</summary>
    public Choice<AnimationKind>? Kind
    {
        get => Kinds.FirstOrDefault(k => k.Value == Step.Kind);
        set
        {
            if (value is null || value.Value == Step.Kind || IsMorph) return;
            var animation = AnimationEditing.Choices(value.Value).Contains(Step.Animation) ? Step.Animation : AnimationEditing.Choices(value.Value)[0];
            if (!AnimationEditing.ReplaceEffect(composition, StepId, value.Value, animation)) return;
            OnPropertyChanged(string.Empty);
            changed();
        }
    }

    public IReadOnlyList<Choice<StepAnimation>> Effects =>
        AnimationEditing.Choices(Step.Kind).Select(a => new Choice<StepAnimation>(a, AnimationEditing.TileTitle(a, Step.Kind))).ToList();

    /// <summary>The effect; picking another swaps it, keeping the timing (the Mac's replaceStepEffect).</summary>
    public Choice<StepAnimation>? Effect
    {
        get => Effects.FirstOrDefault(e => e.Value == Step.Animation);
        set
        {
            if (value is null || value.Value == Step.Animation || IsMorph) return;
            if (!AnimationEditing.ReplaceEffect(composition, StepId, Step.Kind, value.Value)) return;
            OnPropertyChanged(string.Empty);
            changed();
        }
    }

    // MARK: When and how fast

    /// <summary>Plays: 0 Click, 1 With (the previous), 2 After (the previous), 3 Final (on exit).</summary>
    public int TriggerIndex
    {
        get => Step.Trigger switch
        {
            AnimationTrigger.OnClick => 0,
            AnimationTrigger.WithPrevious => 1,
            AnimationTrigger.AfterPrevious => 2,
            _ => 3,
        };
        set
        {
            var trigger = value switch
            {
                0 => AnimationTrigger.OnClick,
                1 => AnimationTrigger.WithPrevious,
                2 => AnimationTrigger.AfterPrevious,
                _ => AnimationTrigger.OnDismiss,
            };
            if (trigger != Step.Trigger) Write(s => s.Trigger = trigger);
        }
    }

    /// <summary>Speed: 0 Quick, 1 Normal, 2 Slow, 3 Custom (type the seconds).</summary>
    public int SpeedIndex
    {
        get => (int)AnimationNames.SpeedOf(Step.DurationSeconds);
        set
        {
            if (((AnimationSpeed)value).Seconds() is { } seconds) Write(s => s.DurationSeconds = seconds);
            else OnPropertyChanged(nameof(IsCustomSpeed));
        }
    }

    public bool IsCustomSpeed => AnimationNames.SpeedOf(Step.DurationSeconds) == AnimationSpeed.Custom;

    public string Duration
    {
        get => Num(Step.DurationSeconds);
        set { if (Parse(value) is { } v) Write(s => s.DurationSeconds = Math.Clamp(v, 0, 60)); }
    }

    public string Delay
    {
        get => Num(Step.DelaySeconds ?? 0);
        set { if (Parse(value) is { } v) Write(s => s.DelaySeconds = Math.Abs(v) < 0.0005 ? null : Math.Clamp(v, -60, 600)); }
    }

    public string DelayHint
    {
        get
        {
            var auto = AnimationSequence.Groups(composition.Objects, composition.AnimationOrder).Auto.Any(e => e.Step.Id == StepId);
            return AnimationEditing.DelayHint(Step, auto);
        }
    }

    /// <summary>Easing: 0 None, 1 In, 2 Out, 3 Both.</summary>
    public int RampIndex
    {
        get => (int)AnimationEditing.EffectiveRamp(Step);
        set => Write(s => s.Ramp = (AnimationRamp)Math.Clamp(value, 0, 3));
    }

    public bool ShowsWithFade => Step.Animation != StepAnimation.Fade && Step.Kind is AnimationKind.In or AnimationKind.Out;

    public bool WithFade
    {
        get => Step.WithFade ?? false;
        set => Write(s => s.WithFade = value);
    }

    // MARK: The effect's own settings

    private StepAnimation Shown => IsMorph ? StepAnimation.Fade : Step.Animation;

    public bool IsMove => Shown == StepAnimation.Move;
    public bool IsWipe => Shown == StepAnimation.Wipe;
    public bool IsScale => Shown == StepAnimation.Scale;
    public bool IsBlur => Shown == StepAnimation.Blur;
    public bool IsBurnOrGlitch => Shown is StepAnimation.Burn or StepAnimation.Glitch;
    public bool IsPulse => Shown == StepAnimation.Pulse;
    public bool IsType => Shown == StepAnimation.Type;
    public bool IsDraw => Shown == StepAnimation.Draw;
    public bool IsColor => Shown == StepAnimation.Color;

    /// <summary>Where a slide comes from (or goes to): 0–3 an edge (left, right, top, bottom), 4 an offset you type.</summary>
    public int MoveFromIndex
    {
        get => Step.Edge is { } edge ? (int)edge : 4;
        set => Write(s => s.Edge = value is >= 0 and <= 3 ? (AnimationEdge)value : null);
    }

    public bool UsesOffset => IsMove && Step.Edge is null;

    public string OffsetX
    {
        get => Num(Step.OffsetX ?? 0);
        set { if (Parse(value) is { } v) Write(s => s.OffsetX = Math.Clamp(v, -4000, 4000)); }
    }

    public string OffsetY
    {
        get => Num(Step.OffsetY ?? 0);
        set { if (Parse(value) is { } v) Write(s => s.OffsetY = Math.Clamp(v, -4000, 4000)); }
    }

    /// <summary>Where a wipe starts: left, right, top or bottom.</summary>
    public int WipeFromIndex
    {
        get => (int)(Step.Edge ?? AnimationEdge.Left);
        set => Write(s => s.Edge = (AnimationEdge)Math.Clamp(value, 0, 3));
    }

    public string SoftEdge
    {
        get => Num(Step.SoftEdge ?? 0);
        set { if (Parse(value) is { } v) Write(s => s.SoftEdge = Math.Clamp(v, 0, 500)); }
    }

    public string FromScale
    {
        get => Num(Step.FromScale ?? 0.95);
        set { if (Parse(value) is { } v) Write(s => s.FromScale = Math.Clamp(v, 0, 4)); }
    }

    /// <summary>The blur's radius, a burn or glitch's strength, or a pulse's peak size, whichever the effect uses.</summary>
    public string Amount
    {
        get => Num(Step.Amount ?? DefaultAmount);
        set
        {
            if (Parse(value) is not { } v) return;
            var (min, max) = Shown switch
            {
                StepAnimation.Blur => (0.0, 200.0),
                StepAnimation.Pulse => (0.5, 3.0),
                _ => (0.0, 1.0),
            };
            Write(s => s.Amount = Math.Clamp(v, min, max));
        }
    }

    private double DefaultAmount => Shown switch
    {
        StepAnimation.Blur => 24,
        StepAnimation.Pulse => 1.1,
        _ => 0.6,
    };

    public string AmountLabel => Shown switch
    {
        StepAnimation.Blur => "Radius",
        StepAnimation.Pulse => "Peak scale",
        _ => "Amount",
    };

    public bool UsesAmount => IsBlur || IsBurnOrGlitch || IsPulse;

    public bool Cursor
    {
        get => Step.Cursor ?? true;
        set => Write(s => s.Cursor = value);
    }

    /// <summary>"Type from the end" for typing, "Counter-clockwise" for drawing.</summary>
    public bool Reverse
    {
        get => Step.Reverse ?? false;
        set => Write(s => s.Reverse = value);
    }

    private static readonly double[] DrawCorners = [0, 0.25, 0.5, 0.75];

    /// <summary>Where a drawn outline starts: 0 top left, 1 top right, 2 bottom right, 3 bottom left, 4 somewhere else.</summary>
    public int DrawStartIndex
    {
        get
        {
            var v = Step.DrawStart ?? 0;
            var index = Array.FindIndex(DrawCorners, c => Math.Abs(c - v) < 0.001);
            return index < 0 ? 4 : index;
        }
        set
        {
            if (value is < 0 or > 3) return;
            var start = DrawCorners[value];
            Write(s => s.DrawStart = start == 0 ? null : start);
        }
    }

    public string DrawStart
    {
        get => Num(Step.DrawStart ?? 0);
        set { if (Parse(value) is { } v) Write(s => s.DrawStart = Math.Clamp(v, 0, 1) is var c && c == 0 ? null : c); }
    }

    public string ColorHex
    {
        get => Step.ColorHex ?? "#FFD54FFF";
        set => Write(s => s.ColorHex = string.IsNullOrEmpty(value) ? null : value);
    }

    public bool HasRanges => Step.Ranges is { Count: > 0 };

    public bool PlaceholderUnderline
    {
        get => Step.PlaceholderUnderline ?? false;
        set => Write(s => s.PlaceholderUnderline = value);
    }

    // MARK: Camera push

    public bool ShowsCameraPush => !HasRanges && Step.Kind != AnimationKind.Emphasis && !IsMorph;

    /// <summary>The program video moves into the space this object leaves free, in step with this animation.</summary>
    public bool CameraPush
    {
        get => Step.VideoPush is not null;
        set => Write(s => s.VideoPush = value ? s.VideoPush ?? new VideoPush() : null);
    }

    /// <summary>0 push (crop in), 1 push (letterbox), 2 blur background.</summary>
    public int PushModeIndex
    {
        get => Step.VideoPush?.Mode switch { VideoPushMode.Fit => 1, VideoPushMode.BlurBackground => 2, _ => 0 };
        set => Write(s =>
        {
            if (s.VideoPush is { } push) push.Mode = value switch { 1 => VideoPushMode.Fit, 2 => VideoPushMode.BlurBackground, _ => VideoPushMode.Fill };
        });
    }
}
