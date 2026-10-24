using System.Globalization;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Animation;

/// <summary>
/// The rules for a slide's, presentation's or media item's transition: which one wins, the
/// durations to offer, and how the menu names them. Port of SlideScene/TransitionEdit.swift.
/// </summary>
public static class TransitionEdit
{
    /// <summary>The slider's range; longer can be typed (up to <see cref="TypedMaximum"/>).</summary>
    public const double SliderMaximum = 3;
    public const double TypedMaximum = 30;
    public const double DurationStep = 0.1;
    public const double DefaultDuration = 0.5;

    public static readonly IReadOnlyList<double> DurationPresets = [0.5, 1, 1.5, 2, 3];

    public static double Seconds(Transition? transition) => transition?.DurationSeconds ?? DefaultDuration;

    /// <summary>The menu's durations, with the current one added when it isn't a preset.</summary>
    public static List<double> DurationChoices(Transition? current)
    {
        var now = Math.Round(Seconds(current) * 100, MidpointRounding.AwayFromZero) / 100;
        return DurationPresets.Contains(now) ? [.. DurationPresets] : [.. DurationPresets.Append(now).Order()];
    }

    /// <summary>"1 s", "1.5 s", "0.41 s".</summary>
    public static string Label(double seconds)
    {
        var rounded = Math.Round(seconds * 100, MidpointRounding.AwayFromZero) / 100;
        return rounded == Math.Round(rounded, MidpointRounding.AwayFromZero)
            ? $"{(int)rounded} s"
            : $"{rounded.ToString("0.##", CultureInfo.InvariantCulture)} s";
    }

    /// <summary>How a slide comes in: its own transition (even a cut), else its presentation's; null = the room default.</summary>
    public static Transition? Entrance(Transition? own, Transition? presentation) => own ?? presentation;

    public static string Describe(Transition transition) =>
        transition.TransitionKind == TransitionKind.Cut
            ? MenuName(TransitionKind.Cut)
            : $"{MenuName(transition.TransitionKind)} {Label(Seconds(transition))}";

    /// <summary>What the transition menu shows: "Mixed", the transition, "Presentation Default (…)" or "Room Default".</summary>
    public static string MenuValue(Transition? current, bool mixed = false, Transition? inherited = null)
    {
        if (mixed) return "Mixed";
        if (current is not null) return Describe(current);
        if (inherited is not null) return $"Presentation Default ({Describe(inherited)})";
        return "Room Default";
    }

    /// <summary>A transition of another kind keeping the duration and colour; null kind = back to the default.</summary>
    public static Transition? WithKind(TransitionKind? kind, Transition? current) => kind is { } k
        ? new Transition { TransitionKind = k, DurationSeconds = current?.DurationSeconds, ColorHex = current?.ColorHex }
        : null;

    /// <summary>The same transition with a new length (0 to <see cref="TypedMaximum"/> seconds); none stays none.</summary>
    public static Transition? WithDuration(double seconds, Transition? current) => current is null
        ? null
        : new Transition { TransitionKind = current.TransitionKind, DurationSeconds = Math.Clamp(seconds, 0, TypedMaximum), ColorHex = current.ColorHex };

    public static string MenuName(TransitionKind kind) => kind switch
    {
        TransitionKind.Cut => "Cut",
        TransitionKind.Dissolve => "Dissolve",
        TransitionKind.FadeBlack => "Fade Black",
        TransitionKind.FadeWhite => "Fade White",
        TransitionKind.FadeColor => "Fade Color",
        TransitionKind.BlurDissolve => "Blur Dissolve",
        TransitionKind.FilmBurn => "Film Burn",
        _ => kind.ToString(),
    };
}
