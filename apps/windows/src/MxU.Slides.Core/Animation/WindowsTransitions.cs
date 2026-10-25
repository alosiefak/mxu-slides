using System.Text.Json;
using System.Text.Json.Serialization;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Animation;

/// <summary>
/// Every transition the Windows app can play. The first seven are the Mac's, which a shared
/// document can hold (<see cref="TransitionKind"/>); the rest exist only on Windows (Phase 6) and
/// are drawn by the graphics card from two pictures, the layer before and after
/// (<see cref="PictureTransitions"/>). The Mac refuses a whole document that names a kind it
/// doesn't know, so these never go into one: see <see cref="WindowsTransitions.ToShared"/>.
/// </summary>
[JsonConverter(typeof(CamelCaseEnum<WindowsTransitionKind>))]
public enum WindowsTransitionKind
{
    // The Mac's seven.
    Cut, Dissolve, FadeBlack, FadeWhite, FadeColor, BlurDissolve, FilmBurn,

    // Moves: the pictures travel.
    Push, SlideOver, SlideReveal, WhipPan,

    // Zooms.
    ZoomIn, ZoomOut, ZoomThrough, CrossZoom,

    // Wipes: the new picture shows through a growing shape.
    Iris, Diamond, Clock, SoftWipe, LumaWipe,

    // 3-D.
    Cube, Flip,

    // Effects.
    LightLeak, Glitch, Ripple,
}

/// <summary>Which way a transition moves: the way the pictures travel (Push Left moves everything left, so the new one comes in from the right).</summary>
[JsonConverter(typeof(CamelCaseEnum<TransitionDirection>))]
public enum TransitionDirection { Left, Right, Up, Down }

/// <summary>Enums saved by name ("slideOver", not 8), so adding a kind later never changes what an old file means.</summary>
public sealed class CamelCaseEnum<T>() : JsonStringEnumConverter<T>(JsonNamingPolicy.CamelCase) where T : struct, Enum;

/// <summary>
/// A transition as the Windows app holds it: any kind it can play, with its length and, where the
/// kind uses them, a direction, a softness (0 hard edge … 1 very soft) and a colour.
/// It is kept in two places, never in a shared document: beside the library for a slide,
/// presentation or media item (<see cref="Library.TransitionExtras"/>), and in this PC's
/// settings for the room defaults.
/// </summary>
public sealed record WindowsTransition
{
    public required WindowsTransitionKind Kind { get; init; }
    public double DurationSeconds { get; init; } = WindowsTransitions.DefaultSeconds;
    public TransitionDirection Direction { get; init; } = TransitionDirection.Left;
    public double Softness { get; init; } = WindowsTransitions.DefaultSoftness;

    /// <summary>"#RRGGBB" for a Fade Color or a Light Leak; null = the kind's own colour.</summary>
    public string? ColorHex { get; init; }

    /// <summary>One the Mac can play as it is (the shared document can hold it).</summary>
    [JsonIgnore]
    public bool IsShared => WindowsTransitions.IsShared(Kind);
}

/// <summary>
/// The catalogue of transitions: names, groups, which settings each kind has, and the rule that
/// keeps documents readable on the Mac (<see cref="ToShared"/>). Windows only: the Mac has no twin.
/// </summary>
public static class WindowsTransitions
{
    public const double DefaultSeconds = 0.8;
    public const double DefaultSoftness = 0.35;

    /// <summary>A Light Leak's warm amber, when it isn't given a colour.</summary>
    public const string LightLeakColor = "#FFB45A";

    /// <summary>The kinds in the order the picker shows them, Mac kinds first.</summary>
    public static readonly IReadOnlyList<WindowsTransitionKind> All = Enum.GetValues<WindowsTransitionKind>();

    /// <summary>The kinds only Windows has.</summary>
    public static readonly IReadOnlyList<WindowsTransitionKind> WindowsOnly = [.. All.Where(k => !IsShared(k))];

    public static bool IsShared(WindowsTransitionKind kind) => kind <= WindowsTransitionKind.FilmBurn;

    public static string Name(WindowsTransitionKind kind) => kind switch
    {
        WindowsTransitionKind.FadeBlack => "Fade Black",
        WindowsTransitionKind.FadeWhite => "Fade White",
        WindowsTransitionKind.FadeColor => "Fade Color",
        WindowsTransitionKind.BlurDissolve => "Blur Dissolve",
        WindowsTransitionKind.FilmBurn => "Film Burn",
        WindowsTransitionKind.SlideOver => "Slide Over",
        WindowsTransitionKind.SlideReveal => "Slide Reveal",
        WindowsTransitionKind.WhipPan => "Whip Pan",
        WindowsTransitionKind.ZoomIn => "Zoom In",
        WindowsTransitionKind.ZoomOut => "Zoom Out",
        WindowsTransitionKind.ZoomThrough => "Zoom Through",
        WindowsTransitionKind.CrossZoom => "Cross Zoom",
        WindowsTransitionKind.SoftWipe => "Soft Wipe",
        WindowsTransitionKind.LumaWipe => "Luma Wipe",
        WindowsTransitionKind.LightLeak => "Light Leak",
        _ => kind.ToString(),
    };

    /// <summary>The picker's headings.</summary>
    public static string Group(WindowsTransitionKind kind) => kind switch
    {
        <= WindowsTransitionKind.FilmBurn => "Classic",
        <= WindowsTransitionKind.WhipPan => "Move",
        <= WindowsTransitionKind.CrossZoom => "Zoom",
        <= WindowsTransitionKind.LumaWipe => "Wipe",
        <= WindowsTransitionKind.Flip => "3-D",
        _ => "Effects",
    };

    /// <summary>A sentence for the picker's tooltip.</summary>
    public static string Describe(WindowsTransitionKind kind) => kind switch
    {
        WindowsTransitionKind.Cut => "Straight to the new picture.",
        WindowsTransitionKind.Dissolve => "The new picture fades in over the old.",
        WindowsTransitionKind.FadeBlack => "Down to black, then up into the new picture.",
        WindowsTransitionKind.FadeWhite => "Up to white, then down into the new picture.",
        WindowsTransitionKind.FadeColor => "Through a colour of your choice.",
        WindowsTransitionKind.BlurDissolve => "Both pictures soften as they cross.",
        WindowsTransitionKind.FilmBurn => "A warm flare burns from one to the other.",
        WindowsTransitionKind.Push => "The new picture pushes the old one off the screen.",
        WindowsTransitionKind.SlideOver => "The new picture slides in over the old one.",
        WindowsTransitionKind.SlideReveal => "The old picture slides away to show the new one beneath.",
        WindowsTransitionKind.WhipPan => "A fast camera pan, blurred by its speed.",
        WindowsTransitionKind.ZoomIn => "Flies into the old picture as the new one arrives.",
        WindowsTransitionKind.ZoomOut => "The old picture falls away; the new one settles in.",
        WindowsTransitionKind.ZoomThrough => "Rushes through the old picture into the new, blurred by the speed.",
        WindowsTransitionKind.CrossZoom => "Both pictures streak towards a moving point and cross.",
        WindowsTransitionKind.Iris => "The new picture opens from the middle in a circle.",
        WindowsTransitionKind.Diamond => "The new picture opens from the middle in a diamond.",
        WindowsTransitionKind.Clock => "A clock hand sweeps the new picture in.",
        WindowsTransitionKind.SoftWipe => "A soft edge wipes across.",
        WindowsTransitionKind.LumaWipe => "The brightest parts of the old picture give way first.",
        WindowsTransitionKind.Cube => "The pictures are two sides of a turning cube.",
        WindowsTransitionKind.Flip => "The picture flips over like a card.",
        WindowsTransitionKind.LightLeak => "A warm light sweeps across as the pictures cross.",
        WindowsTransitionKind.Glitch => "A burst of digital interference.",
        WindowsTransitionKind.Ripple => "Ripples spread out and carry the new picture with them.",
        _ => "",
    };

    public static bool HasDirection(WindowsTransitionKind kind) => kind is WindowsTransitionKind.Push or WindowsTransitionKind.SlideOver
        or WindowsTransitionKind.SlideReveal or WindowsTransitionKind.WhipPan or WindowsTransitionKind.SoftWipe
        or WindowsTransitionKind.Cube or WindowsTransitionKind.Flip;

    public static bool HasSoftness(WindowsTransitionKind kind) => kind is WindowsTransitionKind.Iris or WindowsTransitionKind.Diamond
        or WindowsTransitionKind.Clock or WindowsTransitionKind.SoftWipe or WindowsTransitionKind.LumaWipe;

    public static bool HasColor(WindowsTransitionKind kind) => kind is WindowsTransitionKind.FadeColor or WindowsTransitionKind.LightLeak;

    /// <summary>A cut has no length.</summary>
    public static bool HasDuration(WindowsTransitionKind kind) => kind != WindowsTransitionKind.Cut;

    /// <summary>The colour a kind uses: its own, else the kind's default (black for a fade, amber for a light leak).</summary>
    public static string Color(WindowsTransition transition) => transition.ColorHex
        ?? (transition.Kind == WindowsTransitionKind.LightLeak ? LightLeakColor : "#000000");

    /// <summary>
    /// The nearest kind the Mac knows. It is what a shared document keeps for a Windows-only
    /// transition, so a Mac opening the document plays something close, and it is what the
    /// outputs play on a PC that can't draw the graphics-card transitions.
    /// </summary>
    public static TransitionKind Nearest(WindowsTransitionKind kind) => kind switch
    {
        WindowsTransitionKind.Cut => TransitionKind.Cut,
        WindowsTransitionKind.Dissolve => TransitionKind.Dissolve,
        WindowsTransitionKind.FadeBlack => TransitionKind.FadeBlack,
        WindowsTransitionKind.FadeWhite => TransitionKind.FadeWhite,
        WindowsTransitionKind.FadeColor => TransitionKind.FadeColor,
        WindowsTransitionKind.BlurDissolve => TransitionKind.BlurDissolve,
        WindowsTransitionKind.FilmBurn => TransitionKind.FilmBurn,
        // A light sweeping across is the Mac's film burn, more or less; the blurred zooms are its blur dissolve.
        WindowsTransitionKind.LightLeak => TransitionKind.FilmBurn,
        WindowsTransitionKind.ZoomThrough or WindowsTransitionKind.CrossZoom or WindowsTransitionKind.WhipPan => TransitionKind.BlurDissolve,
        _ => TransitionKind.Dissolve,
    };

    /// <summary>
    /// What a shared document stores for <paramref name="transition"/>: the transition itself for a
    /// Mac kind; for a Windows-only kind the nearest Mac kind at the same length (the Windows kind
    /// itself goes beside the library). The colour is kept only where the Mac uses it (Fade Color).
    /// </summary>
    public static Transition ToShared(this WindowsTransition transition)
    {
        var kind = Nearest(transition.Kind);
        return new Transition
        {
            TransitionKind = kind,
            DurationSeconds = Math.Round(Math.Clamp(transition.DurationSeconds, 0, TransitionEdit.TypedMaximum), 3),
            ColorHex = kind == TransitionKind.FadeColor ? transition.ColorHex : null,
        };
    }

    /// <summary>A shared transition as the Windows app holds it (its length defaults as the Mac's does).</summary>
    public static WindowsTransition FromShared(Transition transition) => new()
    {
        Kind = transition.TransitionKind switch
        {
            TransitionKind.Cut => WindowsTransitionKind.Cut,
            TransitionKind.FadeBlack => WindowsTransitionKind.FadeBlack,
            TransitionKind.FadeWhite => WindowsTransitionKind.FadeWhite,
            TransitionKind.FadeColor => WindowsTransitionKind.FadeColor,
            TransitionKind.BlurDissolve => WindowsTransitionKind.BlurDissolve,
            TransitionKind.FilmBurn => WindowsTransitionKind.FilmBurn,
            _ => WindowsTransitionKind.Dissolve,
        },
        DurationSeconds = TransitionEdit.Seconds(transition),
        ColorHex = transition.ColorHex,
    };

    /// <summary>The same transition as another kind, keeping its length, direction, softness and colour (a picker's tile click).</summary>
    public static WindowsTransition WithKind(this WindowsTransition? current, WindowsTransitionKind kind) =>
        current is null ? new WindowsTransition { Kind = kind } : current with { Kind = kind };

    /// <summary>"Push Left 0.8 s", "Iris 1 s", "Cut".</summary>
    public static string Describe(WindowsTransition transition)
    {
        if (!HasDuration(transition.Kind)) return Name(transition.Kind);
        var direction = HasDirection(transition.Kind) ? $" {transition.Direction}" : "";
        return $"{Name(transition.Kind)}{direction} {TransitionEdit.Label(transition.DurationSeconds)}";
    }

    /// <summary>The live header's short form, which has to fit on a chip ("Blur Dslv", "Push ← 0.8 s").</summary>
    public static string ChipCaption(WindowsTransition transition)
    {
        var name = transition.Kind switch
        {
            WindowsTransitionKind.BlurDissolve => "Blur Dslv",
            _ => Name(transition.Kind),
        };
        if (!HasDuration(transition.Kind)) return name;
        var arrow = HasDirection(transition.Kind) ? " " + Arrow(transition.Direction) : "";
        return $"{name}{arrow} {TransitionEdit.Label(transition.DurationSeconds)}";
    }

    public static string Arrow(TransitionDirection direction) => direction switch
    {
        TransitionDirection.Right => "→",
        TransitionDirection.Up => "↑",
        TransitionDirection.Down => "↓",
        _ => "←",
    };
}
