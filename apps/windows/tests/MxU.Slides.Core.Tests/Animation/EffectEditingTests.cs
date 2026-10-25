using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Animation;

// The Mac has no tests for its effects inspector (it's app code); these pin down what was ported
// from it: the menu, the sliders and their defaults, and the list edits.
public class EffectEditingTests
{
    private static List<Effect> Chain(params EffectKind[] kinds) => [.. kinds.Select(EffectEditing.Fresh)];

    [Fact]
    public void TheAddMenuOffersEveryEffectButHue()
    {
        Assert.DoesNotContain(EffectKind.HueRotate, EffectEditing.Addable);
        Assert.Equal(Enum.GetValues<EffectKind>().Length - 1, EffectEditing.Addable.Count);
        Assert.Equal(EffectKind.Blur, EffectEditing.Addable[0]);
        Assert.Equal("Stained Glass", EffectKind.StainedGlass.DisplayName());
        Assert.Equal("Color Adjust", EffectKind.ColorAdjust.DisplayName());
    }

    [Fact]
    public void FreshEffectsStartAtTheMacsValues()
    {
        Assert.Equal(12, EffectEditing.Fresh(EffectKind.Blur).Radius);
        var adjust = EffectEditing.Fresh(EffectKind.ColorAdjust);
        Assert.Equal((0.0, 0.0, 1.0), (adjust.Brightness!.Value, adjust.Contrast!.Value, adjust.Saturation!.Value));
        var warp = EffectEditing.Fresh(EffectKind.Warp);
        Assert.Equal((40.0, 240.0, 0.5), (warp.Amount!.Value, warp.Scale!.Value, warp.Speed!.Value));
        var trails = EffectEditing.Fresh(EffectKind.GhostTrails);
        Assert.Equal((6.0, 1.5, 160.0, 0.5), (trails.Amount!.Value, trails.Fade!.Value, trails.Scale!.Value, trails.Speed!.Value));
        Assert.Null(EffectEditing.Fresh(EffectKind.Invert).Amount);
        Assert.Null(EffectEditing.Fresh(EffectKind.Invert).Enabled); // absent means on
    }

    [Fact]
    public void EachEffectShowsItsSlidersThenMix()
    {
        Assert.Equal(["Radius", "Mix"], EffectEditing.Dials(EffectKind.Blur).Select(d => d.Label));
        Assert.Equal(["Hue", "Brightness", "Contrast", "Saturation", "Mix"], EffectEditing.Dials(EffectKind.ColorAdjust).Select(d => d.Label));
        Assert.Equal(["Jitter", "Cell Size", "Leading", "Speed", "Mix"], EffectEditing.Dials(EffectKind.StainedGlass).Select(d => d.Label));
        Assert.Equal(["Mix"], EffectEditing.Dials(EffectKind.Invert).Select(d => d.Label));
        var leading = EffectEditing.Dials(EffectKind.StainedGlass).Single(d => d.Label == "Leading");
        Assert.Equal(EffectField.Radius, leading.Field); // stained glass keeps its leading in "radius", as on the Mac
    }

    [Fact]
    public void SlidersReadTheirDefaultUntilSetAndStayInRange()
    {
        var effect = new Effect { EffectKind = EffectKind.Pixelate };
        var size = EffectEditing.AmountDial(EffectKind.Pixelate)!;
        Assert.Equal(16, EffectEditing.Read(effect, size.Field, size.Default));
        EffectEditing.Write(effect, size.Field, EffectEditing.Clamp(size, 512.4));
        Assert.Equal(200, effect.Amount);
        Assert.Equal(2.5, EffectEditing.Clamp(EffectEditing.Dials(EffectKind.Scatter).Single(d => d.Label == "Size"), 2.56));
        Assert.Equal(0.42, EffectEditing.Clamp(EffectEditing.Mix, 0.4199));
        Assert.Equal(1, EffectEditing.Clamp(EffectEditing.Mix, double.NaN)); // a nonsense number goes back to the default
    }

    [Fact]
    public void AddingRemovingAndBypassingReturnFreshChains()
    {
        var original = Chain(EffectKind.Blur);
        var added = EffectEditing.Added(original, EffectKind.Vignette)!;
        Assert.Equal([EffectKind.Blur, EffectKind.Vignette], added.Select(e => e.EffectKind));
        Assert.Single(original); // the old chain is left alone

        var bypassed = EffectEditing.Enabled(added, 1, false)!;
        Assert.False(bypassed[1].Enabled);
        Assert.Equal(0.8, bypassed[1].Amount); // a bypassed effect keeps its settings
        Assert.Null(EffectEditing.Enabled(bypassed, 1, true)![1].Enabled);

        Assert.Null(EffectEditing.Removed(Chain(EffectKind.Blur), 0)); // an empty chain is stored as absent
        Assert.Equal(2, EffectEditing.Removed(added, 5)!.Count); // no such row: nothing goes
    }

    [Fact]
    public void DroppingAnEffectOnARowPutsItBeforeThatRow()
    {
        var chain = Chain(EffectKind.Blur, EffectKind.Invert, EffectKind.Grain, EffectKind.Warp);
        static List<EffectKind> Kinds(List<Effect>? c) => [.. c!.Select(e => e.EffectKind)];
        Assert.Equal([EffectKind.Invert, EffectKind.Grain, EffectKind.Blur, EffectKind.Warp], Kinds(EffectEditing.Moved(chain, 0, 3)));
        Assert.Equal([EffectKind.Warp, EffectKind.Blur, EffectKind.Invert, EffectKind.Grain], Kinds(EffectEditing.Moved(chain, 3, 0)));
        Assert.Equal([EffectKind.Invert, EffectKind.Grain, EffectKind.Warp, EffectKind.Blur], Kinds(EffectEditing.Moved(chain, 0, 4))); // past the end
        Assert.Equal([EffectKind.Blur, EffectKind.Grain, EffectKind.Invert, EffectKind.Warp], Kinds(EffectEditing.Stepped(chain, 1, +1)));
        Assert.Equal([EffectKind.Invert, EffectKind.Blur, EffectKind.Grain, EffectKind.Warp], Kinds(EffectEditing.Stepped(chain, 1, -1)));
        Assert.Equal(Kinds(chain), Kinds(EffectEditing.Stepped(chain, 0, -1))); // the first can't go higher
    }

    [Fact]
    public void AnObjectWithNoEffectsLeftLosesApplyBelow()
    {
        var plate = new SlideObject { Id = "p", ObjectKind = SlideObjectKind.Shape, Name = "Plate", Text = "", EffectsApplyBelow = true, Effects = Chain(EffectKind.Blur) };
        EffectEditing.SetChain(plate, EffectEditing.Removed(plate.Effects, 0));
        Assert.Null(plate.Effects);
        Assert.Null(plate.EffectsApplyBelow);
    }

    [Fact]
    public void WithoutPixelShadersTheEditorSaysWhatItCantDraw()
    {
        Assert.False(EffectEditing.NeedsPixelShader(EffectKind.Blur));
        Assert.True(EffectEditing.NeedsPixelShader(EffectKind.Warp));
        Assert.Null(EffectEditing.SoftwareNote(Chain(EffectKind.Warp), canDrawShaders: true));
        Assert.Null(EffectEditing.SoftwareNote(Chain(EffectKind.Blur), canDrawShaders: false));
        var note = EffectEditing.SoftwareNote(Chain(EffectKind.Blur, EffectKind.Warp, EffectKind.Grain, EffectKind.Warp), canDrawShaders: false)!;
        Assert.Contains("Warp and Grain", note, StringComparison.Ordinal);
        Assert.Contains("pixel shader 3.0", note, StringComparison.Ordinal);
        var bypassed = EffectEditing.Enabled(Chain(EffectKind.Warp), 0, false);
        Assert.Null(EffectEditing.SoftwareNote(bypassed, canDrawShaders: false)); // a bypassed effect draws nothing anyway
        Assert.Equal("Blur, Invert (off)", EffectEditing.Summary(EffectEditing.Enabled(Chain(EffectKind.Blur, EffectKind.Invert), 1, false)));
        Assert.Equal("None", EffectEditing.Summary(null));
    }
}
