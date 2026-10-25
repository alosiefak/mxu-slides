using System.Numerics;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Motion;

namespace MxU.Slides.Core.Tests.Motion;

/// <summary>Recipes (kept in Windows extras, so "Edit motion background…" can open one again) and palettes.</summary>
public sealed class MotionRecipeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "MotionRecipe-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void ThereAreElevenStylesEachWithItsOwnPreset()
    {
        Assert.Equal(11, MotionStyles.All.Count);
        Assert.Equal(MotionStyles.All.Count, MotionStyles.All.Select(s => s.Id).Distinct().Count());
        Assert.All(MotionStyles.All, s => Assert.NotNull(MotionPalette.Find(s.DefaultPalette)));
        Assert.All(MotionPalette.Presets, p => Assert.Equal(4, p.Colors.Count));
    }

    [Fact]
    public void NormalizingPutsEverythingInRange()
    {
        var wild = new MotionRecipe
        {
            Style = "lasers", Palette = "Nope", Colors = ["#zz", "blue"], Speed = 9, Intensity = -1, Seconds = 5, Height = 720, FrameRate = 24,
        }.Normalized();
        Assert.Equal((MotionStyles.All[0].Id, 2.0, 0.0, 10, 1080, 30), (wild.Style, wild.Speed, wild.Intensity, wild.Seconds, wild.Height, wild.FrameRate));
        Assert.Equal(MotionStyles.All[0].DefaultPalette, wild.Palette);
        Assert.Equal(MotionPalette.Find(wild.Palette)!.Colors, wild.Colors);

        var big = new MotionRecipe { Height = 4000, FrameRate = 120, Seconds = 300, Speed = 0.01, Intensity = 3 }.Normalized();
        Assert.Equal((2160, 3840, 60, 30, 0.25, 1.0), (big.Height, big.Width, big.FrameRate, big.Seconds, big.Speed, big.Intensity));
        Assert.Equal(1800, big.FrameCount);
    }

    [Fact]
    public void ARecipeRoundTripsThroughTheWindowsExtras()
    {
        var extras = new WindowsExtras(root);
        var recipe = MotionRecipe.For(MotionStyles.Find("aurora")!) with { Speed = 0.75, Variation = 12, Name = "Easter morning", Seconds = 25 };
        extras.Save(MotionRecipe.ExtraKind, "item-1", recipe, basis: "hash-1");
        var back = extras.Get<MotionRecipe>(MotionRecipe.ExtraKind, "item-1", basis: "hash-1");
        Assert.True(MxuJson.StructurallyEqual(recipe, back));
        // A different file under the item (replaced on a Mac) means the recipe no longer describes it.
        Assert.Null(extras.Get<MotionRecipe>(MotionRecipe.ExtraKind, "item-1", basis: "hash-2"));

        var json = File.ReadAllText(Path.Combine(root, WindowsExtras.FolderName, MotionRecipe.ExtraKind, "item-1.json"));
        foreach (var key in new[] { "\"style\"", "\"palette\"", "\"colors\"", "\"speed\"", "\"intensity\"", "\"seconds\"", "\"height\"", "\"frameRate\"", "\"variation\"" })
            Assert.Contains(key, json);
        Assert.DoesNotContain("\"width\"", json);
    }

    [Fact]
    public void TheSuggestedNameIsStyleAndColours()
    {
        Assert.Equal("Aurora · Northern Lights", MotionRecipe.For(MotionStyles.Find("aurora")!).SuggestedName());
        Assert.Equal("Bokeh · Custom colours", (MotionRecipe.For(MotionStyles.Find("bokeh")!) with { Palette = null }).SuggestedName());
    }

    [Fact]
    public void HandPickedColoursAreSortedDarkToLightAndSpreadToFour()
    {
        var stops = MotionPalette.Stops(["#F0E0FF", "#081020"]);
        Assert.Equal(4, stops.Length);
        var lightness = stops.Select(c => MotionColor.ToOklab(c).X).ToArray();
        Assert.Equal(lightness.OrderBy(l => l), lightness);
        Assert.Equal("#081020", MotionColor.ToHex(stops[0]));
        Assert.Equal("#F0E0FF", MotionColor.ToHex(stops[3]));
        Assert.Equal(4, MotionPalette.Stops(["#102040", "#405080", "#F0E0FF"]).Length);
    }

    [Fact]
    public void APresetKeepsItsDesignedOrder()
    {
        // In Northern Lights the green is designed as the third colour (the aurora's foot), though
        // it looks lighter than the violet that tops it.
        var colours = MotionPalette.Find("Northern Lights")!.Colors;
        var stops = MotionPalette.Stops(colours, inDesignedOrder: true);
        Assert.Equal(["#02040A", "#0B2A3A", "#2BD98A", "#B07CFF"], stops.Select(MotionColor.ToHex));
    }

    [Fact]
    public void APaleBackgroundColourIsDeepenedSoTextReads()
    {
        var stops = MotionPalette.Stops(["#FFD1DC", "#FFF5BA"]);
        Assert.InRange(MotionColor.ToOklab(stops[0]).X, 0, 0.2001);
        // Same hue: still pink, just deep.
        Assert.True(stops[0].X > stops[0].Z);
    }

    [Fact]
    public void BlendingTwoHuesKeepsTheColourInsteadOfGoingGrey()
    {
        static Vector3 Lab(string hex) => MotionColor.ToOklab(MotionColor.FromHex(hex)!.Value);
        // Royal's purple and gold: far apart in hue.
        Vector3 purple = Lab("#8E44C9"), gold = Lab("#F3C96B");
        var straight = Vector3.Lerp(purple, gold, 0.5f);
        var kept = MotionColor.LerpLab(purple, gold, 0.5f);
        // A straight line through OKLab loses most of the colour half-way: that's the mud...
        Assert.True(MotionColor.Chroma(straight) < 0.75f * Math.Min(MotionColor.Chroma(purple), MotionColor.Chroma(gold)),
            $"straight {MotionColor.Chroma(straight):0.000}, purple {MotionColor.Chroma(purple):0.000}, gold {MotionColor.Chroma(gold):0.000}");
        // ...the blend keeps the two colours' average colourfulness, with the straight line's lightness and hue.
        Assert.Equal((MotionColor.Chroma(purple) + MotionColor.Chroma(gold)) / 2, MotionColor.Chroma(kept), 3);
        Assert.Equal(straight.X, kept.X, 5);
        Assert.Equal(MathF.Atan2(straight.Z, straight.Y), MathF.Atan2(kept.Z, kept.Y), 4);
        // The ends are the colours themselves.
        Assert.Equal(purple, MotionColor.LerpLab(purple, gold, 0));
        Assert.Equal(gold, MotionColor.LerpLab(purple, gold, 1));

        // Two shades of one hue (Midnight's blues) blend almost exactly as a straight line does.
        Vector3 navy = Lab("#0B1E4B"), blue = Lab("#2160C4");
        Assert.InRange(MotionColor.Chroma(MotionColor.LerpLab(navy, blue, 0.5f)) / MotionColor.Chroma(Vector3.Lerp(navy, blue, 0.5f)), 1, 1.05);
        // And two opposite colours aren't given a strong colour that neither has.
        var grey = MotionColor.LerpLab(new Vector3(0.6f, 0.1f, 0), new Vector3(0.6f, -0.1f, 0.001f), 0.5f);
        Assert.True(MotionColor.Chroma(grey) < 0.01f);
    }

    [Fact]
    public void ColoursRoundTripThroughHex()
    {
        foreach (var hex in new[] { "#000000", "#FFFFFF", "#2BD98A", "#B07CFF", "#0B1E4B" })
            Assert.Equal(hex, MotionColor.ToHex(MotionColor.FromHex(hex)!.Value));
        Assert.Null(MotionColor.FromHex("not a colour"));
        Assert.Equal(MotionColor.FromHex("#2BD98A"), MotionColor.FromHex("#2BD98AFF"));
    }
}
