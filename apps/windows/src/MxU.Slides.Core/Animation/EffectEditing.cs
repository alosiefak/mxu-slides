using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Animation;

/// <summary>Which number of an <see cref="Effect"/> a slider writes.</summary>
public enum EffectField { Radius, Amount, Scale, Speed, Fade, Smooth, Brightness, Contrast, Saturation, Hue, Opacity }

/// <summary>One slider in an effect's settings: its label, the field it writes, its range and step, and the value it starts at.</summary>
public sealed record EffectDial(string Label, EffectField Field, double Min, double Max, double Step, double Default);

/// <summary>
/// Editing a chain of picture effects (on a slide object, or a media item): which effects can be
/// added, what each is called, which sliders it has and where they start, and adding, removing,
/// reordering and bypassing effects. The effects are the shared <see cref="EffectKind"/> list
/// only, so whatever is made here plays on a Mac too.
/// <para>
/// Port of the Mac's <c>EffectKind</c> extension in Sources/MediaEditorView.swift (<c>addable</c>,
/// <c>displayName</c>, <c>amountControl</c>, <c>extraControls</c>, <c>freshEffect</c>) and the list
/// edits in SlideObjectInspector.swift's effects section (<c>addEffect</c>, <c>removeEffect</c>,
/// <c>moveEffect</c>, the bypass switch). The pixel-shader note is Windows-only.
/// </para>
/// </summary>
public static class EffectEditing
{
    /// <summary>The effects the Add Effect menu offers, in its order: everything but Hue, which Color Adjust covers.</summary>
    public static IReadOnlyList<EffectKind> Addable { get; } = [.. Enum.GetValues<EffectKind>().Where(k => k != EffectKind.HueRotate)];

    public static string DisplayName(this EffectKind kind) => kind switch
    {
        EffectKind.Blur => "Blur",
        EffectKind.ColorAdjust => "Color Adjust",
        EffectKind.HueRotate => "Hue",
        EffectKind.Invert => "Invert",
        EffectKind.Posterize => "Posterize",
        EffectKind.Pixelate => "Pixelate",
        EffectKind.Vignette => "Vignette",
        EffectKind.Warp => "Warp",
        EffectKind.Echo => "Echo",
        EffectKind.Scatter => "Scatter",
        EffectKind.StainedGlass => "Stained Glass",
        EffectKind.Grain => "Grain",
        _ => "Ghost Trails",
    };

    /// <summary>The effect's main slider (the Mac's <c>amountControl</c>), or null for those without one.</summary>
    public static EffectDial? AmountDial(EffectKind kind) => kind switch
    {
        EffectKind.HueRotate => new("Shift", EffectField.Amount, -180, 180, 1, 90),
        EffectKind.Posterize => new("Levels", EffectField.Amount, 2, 32, 1, 6),
        EffectKind.Pixelate => new("Size", EffectField.Amount, 1, 200, 1, 16),
        EffectKind.Vignette => new("Strength", EffectField.Amount, 0, 1.5, 0.02, 0.8),
        EffectKind.Warp => new("Amount", EffectField.Amount, 0, 300, 1, 40),
        EffectKind.Echo => new("Length", EffectField.Amount, 0, 20, 0.05, 1),
        EffectKind.Scatter => new("Amount", EffectField.Amount, 0, 200, 0.5, 8),
        EffectKind.StainedGlass => new("Jitter", EffectField.Amount, 0, 1, 0.02, 0.8),
        EffectKind.Grain => new("Amount", EffectField.Amount, 0, 1, 0.02, 0.3),
        EffectKind.GhostTrails => new("Drift", EffectField.Amount, 0, 60, 0.5, 6),
        _ => null,
    };

    /// <summary>The effect's other sliders (the Mac's <c>extraControls</c>).</summary>
    public static IReadOnlyList<EffectDial> ExtraDials(EffectKind kind) => kind switch
    {
        EffectKind.Warp => [new("Scale", EffectField.Scale, 20, 1000, 5, 240), new("Speed", EffectField.Speed, 0, 4, 0.05, 0.5)],
        EffectKind.Scatter =>
        [
            new("Size", EffectField.Scale, 1, 2.5, 0.05, 1), new("Smooth", EffectField.Smooth, 0, 1, 0.02, 0),
            new("Speed", EffectField.Speed, 0, 1, 0.02, 1),
        ],
        EffectKind.StainedGlass =>
        [
            new("Cell Size", EffectField.Scale, 8, 400, 1, 80), new("Leading", EffectField.Radius, 0, 20, 0.25, 3),
            new("Speed", EffectField.Speed, 0, 2, 0.05, 0),
        ],
        EffectKind.Grain => [new("Size", EffectField.Scale, 1, 8, 0.25, 1), new("Speed", EffectField.Speed, 0, 1, 0.02, 1)],
        EffectKind.GhostTrails =>
        [
            new("Length", EffectField.Fade, 0, 20, 0.05, 1.5), new("Scale", EffectField.Scale, 20, 600, 5, 160),
            new("Speed", EffectField.Speed, 0, 4, 0.05, 0.5),
        ],
        _ => [],
    };

    /// <summary>The Mix slider every effect ends with: how much of the effect shows over the untouched picture.</summary>
    public static readonly EffectDial Mix = new("Mix", EffectField.Opacity, 0, 1, 0.02, 1);

    /// <summary>
    /// Every slider an effect shows, in the inspector's order: Blur its radius; Color Adjust hue,
    /// brightness, contrast and saturation; the rest their main slider and extras; then Mix.
    /// </summary>
    public static IReadOnlyList<EffectDial> Dials(EffectKind kind)
    {
        var dials = new List<EffectDial>();
        switch (kind)
        {
            case EffectKind.Blur:
                dials.Add(new("Radius", EffectField.Radius, 0, 200, 1, 12));
                break;
            case EffectKind.ColorAdjust:
                dials.Add(new("Hue", EffectField.Hue, -180, 180, 1, 0));
                dials.Add(new("Brightness", EffectField.Brightness, -1, 1, 0.02, 0));
                dials.Add(new("Contrast", EffectField.Contrast, -1, 1, 0.02, 0));
                dials.Add(new("Saturation", EffectField.Saturation, 0, 2, 0.02, 1));
                break;
            default:
                if (AmountDial(kind) is { } amount) dials.Add(amount);
                dials.AddRange(ExtraDials(kind));
                break;
        }
        dials.Add(Mix);
        return dials;
    }

    /// <summary>A new effect with its sliders at their starting values (the Mac's <c>freshEffect</c>).</summary>
    public static Effect Fresh(EffectKind kind)
    {
        switch (kind)
        {
            case EffectKind.Blur: return new Effect { EffectKind = kind, Radius = 12 };
            case EffectKind.ColorAdjust: return new Effect { EffectKind = kind, Brightness = 0, Contrast = 0, Saturation = 1 };
            default:
                var effect = new Effect { EffectKind = kind, Amount = AmountDial(kind)?.Default };
                foreach (var dial in ExtraDials(kind)) Write(effect, dial.Field, dial.Default);
                return effect;
        }
    }

    /// <summary>What a slider shows: the stored value, else <paramref name="fallback"/> (the dial's default).</summary>
    public static double Read(Effect effect, EffectField field, double fallback) => field switch
    {
        EffectField.Radius => effect.Radius,
        EffectField.Amount => effect.Amount,
        EffectField.Scale => effect.Scale,
        EffectField.Speed => effect.Speed,
        EffectField.Fade => effect.Fade,
        EffectField.Smooth => effect.Smooth,
        EffectField.Brightness => effect.Brightness,
        EffectField.Contrast => effect.Contrast,
        EffectField.Saturation => effect.Saturation,
        EffectField.Hue => effect.Hue,
        _ => effect.Opacity,
    } ?? fallback;

    /// <summary>Stores a slider's value on the effect.</summary>
    public static void Write(Effect effect, EffectField field, double value)
    {
        switch (field)
        {
            case EffectField.Radius: effect.Radius = value; break;
            case EffectField.Amount: effect.Amount = value; break;
            case EffectField.Scale: effect.Scale = value; break;
            case EffectField.Speed: effect.Speed = value; break;
            case EffectField.Fade: effect.Fade = value; break;
            case EffectField.Smooth: effect.Smooth = value; break;
            case EffectField.Brightness: effect.Brightness = value; break;
            case EffectField.Contrast: effect.Contrast = value; break;
            case EffectField.Saturation: effect.Saturation = value; break;
            case EffectField.Hue: effect.Hue = value; break;
            default: effect.Opacity = value; break;
        }
    }

    /// <summary>A typed or dragged value, kept inside the dial's range and on its step.</summary>
    public static double Clamp(EffectDial dial, double value)
    {
        if (!double.IsFinite(value)) return dial.Default;
        var stepped = dial.Step > 0 ? Math.Round(value / dial.Step) * dial.Step : value;
        return Math.Round(Math.Clamp(stepped, dial.Min, dial.Max), 4);
    }

    // MARK: Changing a chain. Each returns the new chain, or null when it's empty (stored as absent, as on the Mac).

    private static List<Effect> Copy(IReadOnlyList<Effect>? chain) => [.. (chain ?? []).Select(MxuJson.Clone)];

    private static List<Effect>? Stored(List<Effect> chain) => chain.Count == 0 ? null : chain;

    /// <summary>The chain with a fresh effect of <paramref name="kind"/> at its end.</summary>
    public static List<Effect>? Added(IReadOnlyList<Effect>? chain, EffectKind kind) => [.. Copy(chain), Fresh(kind)];

    /// <summary>The chain without the effect at <paramref name="index"/>.</summary>
    public static List<Effect>? Removed(IReadOnlyList<Effect>? chain, int index)
    {
        var copy = Copy(chain);
        if (index >= 0 && index < copy.Count) copy.RemoveAt(index);
        return Stored(copy);
    }

    /// <summary>
    /// The chain with the effect at <paramref name="from"/> dropped on the row at <paramref name="onto"/>
    /// (it lands before that row's effect, as the Mac's drag and drop does), or at the end for an
    /// index past the last row. Effects run first to last, so the order changes the picture.
    /// </summary>
    public static List<Effect>? Moved(IReadOnlyList<Effect>? chain, int from, int onto)
    {
        var copy = Copy(chain);
        if (from < 0 || from >= copy.Count || from == onto) return Stored(copy);
        var moved = copy[from];
        copy.RemoveAt(from);
        var target = onto > from ? onto - 1 : onto;
        copy.Insert(Math.Clamp(target, 0, copy.Count), moved);
        return Stored(copy);
    }

    /// <summary>One place earlier (−1) or later (+1): Windows' Move up / Move down, beside dragging.</summary>
    public static List<Effect>? Stepped(IReadOnlyList<Effect>? chain, int index, int by)
    {
        var count = chain?.Count ?? 0;
        var target = index + by;
        if (index < 0 || index >= count || target < 0 || target >= count) return Stored(Copy(chain));
        return Moved(chain, index, by > 0 ? target + 1 : target);
    }

    /// <summary>
    /// The chain with one effect switched on or bypassed. A bypassed effect keeps its settings; an
    /// effect that is on is stored without the flag (absent means on, as on the Mac).
    /// </summary>
    public static List<Effect>? Enabled(IReadOnlyList<Effect>? chain, int index, bool on)
    {
        var copy = Copy(chain);
        if (index >= 0 && index < copy.Count) copy[index].Enabled = on ? null : false;
        return Stored(copy);
    }

    /// <summary>
    /// Puts a chain on an object. With no effects left, "apply to content below" goes too: it has
    /// nothing to apply (the Mac's removeEffect).
    /// </summary>
    public static void SetChain(SlideObject o, List<Effect>? chain)
    {
        o.Effects = chain is { Count: > 0 } ? chain : null;
        if (o.Effects is null) o.EffectsApplyBelow = null;
    }

    // MARK: Drawing without the graphics card

    /// <summary>
    /// True for effects WPF can only draw with a pixel shader 3.0 graphics card: everything but Blur,
    /// which WPF draws itself. Without one (a virtual machine, a PC with no graphics driver, WPF's
    /// software drawing) they aren't drawn at all, so the editor says so instead of showing a picture
    /// that looks right only by luck. Windows-only.
    /// </summary>
    public static bool NeedsPixelShader(EffectKind kind) => kind != EffectKind.Blur;

    /// <summary>The line the editor shows under a chain when this PC can't draw some of it, or null when it can.</summary>
    public static string? SoftwareNote(IReadOnlyList<Effect>? chain, bool canDrawShaders)
    {
        if (canDrawShaders) return null;
        var missing = (chain ?? []).Where(e => e.Enabled != false && NeedsPixelShader(e.EffectKind))
            .Select(e => e.EffectKind.DisplayName()).Distinct().ToList();
        if (missing.Count == 0) return null;
        var names = missing.Count == 1 ? missing[0] : string.Join(", ", missing[..^1]) + " and " + missing[^1];
        return $"This PC's graphics can't draw {names} (it needs pixel shader 3.0), so the preview here leaves "
            + (missing.Count == 1 ? "it" : "them") + " out. A projector PC with a graphics card shows "
            + (missing.Count == 1 ? "it" : "them") + ", and so does a Mac.";
    }

    /// <summary>The effects in a chain as a short line for a list ("Blur, Vignette"), or "None".</summary>
    public static string Summary(IReadOnlyList<Effect>? chain) =>
        chain is { Count: > 0 } ? string.Join(", ", chain.Select(e => e.EffectKind.DisplayName() + (e.Enabled == false ? " (off)" : ""))) : "None";
}
