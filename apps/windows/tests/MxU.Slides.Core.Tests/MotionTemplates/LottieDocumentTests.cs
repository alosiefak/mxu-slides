using MxU.Slides.Core.MotionTemplates;

namespace MxU.Slides.Core.Tests.MotionTemplates;

// Reading Lottie files (Phase 11, Windows only): size, timing, markers, layers and fonts, and
// refusing what isn't an animation, with a reason an operator can read.
public sealed class LottieDocumentTests
{
    private static LottieDocument Strap() => LottieDocument.Parse(LottieSamples.Strap);

    [Fact]
    public void ItReadsTheSizeTimingAndName()
    {
        var document = Strap();
        Assert.Equal((1920.0, 1080.0, 30.0), (document.Width, document.Height, document.FrameRate));
        Assert.Equal((0.0, 150.0, 150.0, 5.0), (document.InPoint, document.OutPoint, document.Frames, document.Seconds));
        Assert.Equal(("Speaker Strap", "5.7.4"), (document.Name, document.Version));
    }

    [Fact]
    public void MarkersComeInTimeOrderAndJsonCommentsAreReadForTheirName()
    {
        var markers = Strap().Markers;
        Assert.Equal(["In", "Hold", "Out"], markers.Select(m => m.Name));
        Assert.Equal([0.0, 20.0, 120.0], markers.Select(m => m.Frame));
        Assert.Equal([20.0, 100.0, 30.0], markers.Select(m => m.DurationFrames));
        Assert.Equal("intro", LottieDocument.MarkerName("""{"name":"intro"}"""));
        Assert.Equal("Out", LottieDocument.MarkerName("  Out "));
    }

    [Fact]
    public void EveryLayerIsListedIncludingThoseInsidePrecompositions()
    {
        var layers = Strap().Layers;
        Assert.Equal(9, layers.Count);
        Assert.Equal(("L0", "Speaker Name", LottieLayer.Text), (layers[0].Path, layers[0].Name, layers[0].Type));
        Assert.Equal(("L6", "Logo", LottieLayer.Image, "image_0"), (layers[6].Path, layers[6].Name, layers[6].Type, layers[6].RefId));
        Assert.Equal(("A:comp_0/L0", "Church", LottieLayer.Text), (layers[8].Path, layers[8].Name, layers[8].Type));
    }

    [Fact]
    public void ItKnowsTheFontsTheTextAsksFor()
    {
        var font = Assert.Single(Strap().Fonts);
        Assert.Equal(new LottieFont("Montserrat-Bold", "Montserrat", "Bold", null), font);
    }

    [Fact]
    public void ARoundTripKeepsEverything()
    {
        var document = Strap();
        var again = LottieDocument.Parse(document.ToUtf8());
        Assert.Equal(document.Layers, again.Layers);
        Assert.Equal(document.Markers, again.Markers);
        // Words stay readable in the stored file rather than \u-escaped.
        Assert.Contains("Pastor Grace", System.Text.Encoding.UTF8.GetString(again.ToUtf8()));
    }

    [Fact]
    public void AByteOrderMarkIsAllowed()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(System.Text.Encoding.UTF8.GetBytes(LottieSamples.Strap)).ToArray();
        Assert.Equal("Speaker Strap", LottieDocument.Parse(bytes).Name);
    }

    [Theory]
    [InlineData("not json at all", "readable JSON")]
    [InlineData("[1, 2, 3]", "isn't a JSON object")]
    [InlineData("""{ "w": 100, "h": 100, "fr": 30, "ip": 0, "op": 10 }""", "no layers")]
    [InlineData("""{ "w": 0, "h": 100, "fr": 30, "ip": 0, "op": 10, "layers": [] }""", "picture size")]
    [InlineData("""{ "w": 100, "h": 100, "fr": 0, "ip": 0, "op": 10, "layers": [] }""", "frame rate")]
    [InlineData("""{ "w": 100, "h": 100, "fr": 30, "ip": 10, "op": 10, "layers": [] }""", "no frames")]
    public void WhatIsntAnAnimationIsRefusedSayingWhy(string json, string reason)
    {
        var error = Assert.Throws<LottieFormatException>(() => LottieDocument.Parse(json));
        Assert.Contains(reason, error.Message);
    }

    [Fact]
    public void AStarterTemplateIsAnOrdinaryLottieFile()
    {
        foreach (var starter in StarterMotionTemplates.All)
        {
            var document = starter.Make();
            Assert.Equal((1920.0, 1080.0), (document.Width, document.Height));
            // What any Lottie player needs: a version, layers and assets.
            Assert.NotNull(document.Root["v"]);
            Assert.NotNull(document.Root["assets"]);
            Assert.NotEmpty(document.Layers);
        }
    }
}
