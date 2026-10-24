using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Animation;

/// <summary>
/// The live header's two transition chips: the slide layer's and the media layer's room default
/// (what a cue that doesn't name its own transition does). Port of TransitionChip in the Mac's
/// Sources/PerformanceHUD.swift and its two uses in AppShell.swift.
/// </summary>
public static class TransitionChips
{
    /// <summary>What a chip offers, in the Mac's order.</summary>
    public static readonly IReadOnlyList<TransitionKind> Choices =
        [TransitionKind.Cut, TransitionKind.Dissolve, TransitionKind.FadeBlack, TransitionKind.FadeWhite, TransitionKind.BlurDissolve];

    /// <summary>The Mac's defaults: media dissolves in 0.7 s, slides cut (0.5 s when given a kind).</summary>
    public const double MediaFallbackSeconds = 0.7;
    public const double SlideFallbackSeconds = 0.5;

    /// <summary>The chip's short name (it has to fit beside the slider).</summary>
    public static string ShortName(TransitionKind kind) => kind switch
    {
        TransitionKind.BlurDissolve => "Blur Dslv",
        _ => TransitionEdit.MenuName(kind),
    };

    /// <summary>A cut has no length to show.</summary>
    public static bool ShowsDuration(TransitionKind kind) => kind != TransitionKind.Cut;

    /// <summary>"Dissolve 0.7 s", or "Cut".</summary>
    public static string Caption(TransitionKind kind, double seconds) =>
        ShowsDuration(kind) ? $"{ShortName(kind)} {TransitionEdit.Label(seconds)}" : ShortName(kind);

    public static string Help(TransitionKind kind, double seconds) =>
        $"{Caption(kind, seconds)}: this layer's default; items with their own always win";

    /// <summary>A slider's value, snapped to tenths of a second as the Mac stores it when the drag ends.</summary>
    public static double Snapped(double seconds) =>
        Math.Round(seconds / TransitionEdit.DurationStep, MidpointRounding.AwayFromZero) * TransitionEdit.DurationStep;
}
