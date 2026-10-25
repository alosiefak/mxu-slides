using MxU.Slides.Core.Animation;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>
/// A room default's direction, softness and colour, for the kinds that use them (Phase 6). The
/// kind and length stay in <see cref="AppSettings.Transition"/> and <see cref="AppSettings.TransitionSeconds"/>
/// (and the media pair), where they always were.
/// </summary>
public sealed record TransitionOptions(
    TransitionDirection Direction = TransitionDirection.Left,
    double Softness = WindowsTransitions.DefaultSoftness,
    string? ColorHex = null);

public sealed partial class AppSettings
{
    /// <summary>The slide room default's direction, softness and colour. A room default is this PC's own, so it can be any kind.</summary>
    public TransitionOptions SlideTransitionOptions { get; set; } = new();

    /// <summary>The media room default's.</summary>
    public TransitionOptions MediaTransitionOptions { get; set; } = new();
}

/// <summary>
/// The room-default setting (<see cref="OutputTransition"/>) and the Windows app's transitions
/// (<see cref="WindowsTransitionKind"/>), each way. The setting's first five names are older than
/// Phase 6 and differ ("FadeThroughBlack"); every later one has the same name as its kind.
/// </summary>
public static class OutputTransitions
{
    public static WindowsTransitionKind Kind(OutputTransition setting) => setting switch
    {
        OutputTransition.Cut => WindowsTransitionKind.Cut,
        OutputTransition.Dissolve => WindowsTransitionKind.Dissolve,
        OutputTransition.FadeThroughBlack => WindowsTransitionKind.FadeBlack,
        OutputTransition.FadeThroughWhite => WindowsTransitionKind.FadeWhite,
        OutputTransition.FadeThroughColor => WindowsTransitionKind.FadeColor,
        OutputTransition.BlurDissolve => WindowsTransitionKind.BlurDissolve,
        _ => Enum.TryParse<WindowsTransitionKind>(setting.ToString(), out var kind) ? kind : WindowsTransitionKind.Dissolve,
    };

    public static OutputTransition Setting(WindowsTransitionKind kind) => kind switch
    {
        WindowsTransitionKind.Cut => OutputTransition.Cut,
        WindowsTransitionKind.Dissolve => OutputTransition.Dissolve,
        WindowsTransitionKind.FadeBlack => OutputTransition.FadeThroughBlack,
        WindowsTransitionKind.FadeWhite => OutputTransition.FadeThroughWhite,
        WindowsTransitionKind.FadeColor => OutputTransition.FadeThroughColor,
        WindowsTransitionKind.BlurDissolve => OutputTransition.BlurDissolve,
        _ => Enum.TryParse<OutputTransition>(kind.ToString(), out var setting) ? setting : OutputTransition.Dissolve,
    };

    /// <summary>
    /// A fade through a colour plays each half for the setting's length (as the room default always
    /// has: "Fade Black 0.4 s" is 0.4 s down and 0.4 s up); every other kind plays for the length itself.
    /// </summary>
    public static double PlayedSeconds(WindowsTransitionKind kind, double seconds) =>
        kind is WindowsTransitionKind.FadeBlack or WindowsTransitionKind.FadeWhite or WindowsTransitionKind.FadeColor ? seconds * 2 : seconds;
}
