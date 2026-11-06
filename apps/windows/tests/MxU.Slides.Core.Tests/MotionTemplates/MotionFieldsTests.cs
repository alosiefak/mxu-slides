using MxU.Slides.Core.MotionTemplates;

namespace MxU.Slides.Core.Tests.MotionTemplates;

// A template's fields (Phase 11): what can be edited is found in the animation itself (text
// layers, colours, pictures), and filling them in makes a changed copy that reads back with
// exactly the values put in, leaving everything else alone.
public sealed class MotionFieldsTests
{
    private static LottieDocument Strap() => LottieDocument.Parse(LottieSamples.Strap);

    private static Dictionary<string, MotionField> Fields(LottieDocument document) => MotionFields.Discover(document).ToDictionary(f => f.Id);

    [Fact]
    public void EveryTextLayerIsAFieldNamedAfterItsLayer()
    {
        var texts = MotionFields.Discover(Strap()).Where(f => f.Kind == MotionFieldKind.Text).ToList();
        Assert.Equal(["text:L0", "text:L1", "text:A:comp_0/L0"], texts.Select(t => t.Id));
        // Two layers with one name get told apart; the precomposition's text is found too.
        Assert.Equal(["Speaker Name", "Speaker Name 2", "Church"], texts.Select(t => t.Label));
        // After Effects' carriage return reads as an ordinary new line.
        Assert.Equal("Pastor Grace\nOkafor", texts[0].Default);
    }

    [Fact]
    public void EachColourIsOneFieldWhereverItIsUsedMostUsedFirst()
    {
        var colours = MotionFields.Discover(Strap()).Where(f => f.Kind == MotionFieldKind.Color).ToList();
        // White and navy are used three times each, gold and #336699 twice, the gradient's red and blue once.
        Assert.Equal(["#FFFFFF", "#0F1F3A", "#F5B700", "#336699", "#FF0000", "#0000FF"], colours.Select(c => c.Default));
        Assert.All(colours, c => Assert.Equal(MotionFields.ColorPrefix + c.Default, c.Id));
        var backing = colours.Single(c => c.Default == "#336699");
        Assert.Equal("Backing & Glow colour", backing.Label);
        Assert.Equal("Backing (solid) and 1 more", backing.Where);
        Assert.Equal("Plate colour", colours.Single(c => c.Default == "#FF0000").Label);
    }

    [Fact]
    public void PicturesAreFieldsNamedAfterTheLayerShowingThem()
    {
        var picture = Assert.Single(MotionFields.Discover(Strap()), f => f.Kind == MotionFieldKind.Image);
        Assert.Equal(("image:image_0", "Logo"), (picture.Id, picture.Label));
    }

    [Fact]
    public void FillingInFieldsReadsBackWithTheNewValues()
    {
        var original = Strap();
        var values = new Dictionary<string, string>
        {
            ["text:L0"] = "Rev. Ade Johnson",
            ["text:A:comp_0/L0"] = "Hope Church\nLagos",
            ["color:#0F1F3A"] = "#7a1f2b",
            ["color:#336699"] = "#112233",
        };
        var filled = MotionFields.Apply(original, values);
        var now = MotionFields.CurrentValues(filled);
        Assert.Equal("Rev. Ade Johnson", now["text:L0"]);
        Assert.Equal("Hope Church\nLagos", now["text:A:comp_0/L0"]);
        Assert.Equal("Lead Pastor", now["text:L1"]);
        // A colour field's id is the colour it was made with; the changed copy has the new colour
        // everywhere the old one was (its fill, the text's outline, the moving keyframe).
        Assert.DoesNotContain("color:#0F1F3A", now.Keys);
        Assert.Equal("#7A1F2B", now["color:#7A1F2B"]);
        Assert.Equal("#112233", now["color:#112233"]);
        Assert.Equal("Speaker Name (text outline) and 2 more", MotionFields.Discover(filled).Single(f => f.Default == "#7A1F2B").Where);
        // The original is never touched.
        Assert.Equal("Pastor Grace\nOkafor", MotionFields.CurrentValues(original)["text:L0"]);
        Assert.Equal("#0F1F3A", MotionFields.CurrentValues(original)["color:#0F1F3A"]);
    }

    [Fact]
    public void ANewColourReachesEveryPlaceTheOldOneWasAndKeepsEachPlacesOwnOpacity()
    {
        var filled = MotionFields.Apply(Strap(), new Dictionary<string, string> { ["color:#0F1F3A"] = "#FF8000" });
        var json = System.Text.Json.Nodes.JsonNode.Parse(filled.ToUtf8())!;
        var layers = json["layers"]!.AsArray();
        // The plate's fill keeps its alpha (the fourth number).
        var plateFill = layers[2]!["shapes"]![0]!["it"]![1]!["c"]!["k"]!.AsArray();
        Assert.Equal([1.0, 0.502, 0.0, 1.0], plateFill.Select(n => Math.Round(n!.GetValue<double>(), 3)));
        // The text's outline, and the first keyframe of the pulsing fill, changed too; the second keyframe (white) didn't.
        Assert.Equal(1.0, layers[0]!["t"]!["d"]!["k"]![0]!["s"]!["sc"]![0]!.GetValue<double>());
        var pulse = layers[3]!["shapes"]![0]!["c"]!["k"]!.AsArray();
        Assert.Equal(1.0, pulse[0]!["s"]![0]!.GetValue<double>());
        Assert.Equal(0.502, Math.Round(pulse[0]!["s"]![1]!.GetValue<double>(), 3));
        Assert.Equal([1.0, 1.0, 1.0], pulse[1]!["s"]!.AsArray().Take(3).Select(n => n!.GetValue<double>()));
    }

    [Fact]
    public void TwoColoursCanSwap()
    {
        var swapped = MotionFields.Apply(Strap(), new Dictionary<string, string> { ["color:#F5B700"] = "#0F1F3A", ["color:#0F1F3A"] = "#F5B700" });
        var json = System.Text.Json.Nodes.JsonNode.Parse(swapped.ToUtf8())!;
        var items = json["layers"]![2]!["shapes"]![0]!["it"]!.AsArray();
        Assert.Equal(0.9608, items[1]!["c"]!["k"]![0]!.GetValue<double>(), 3); // the fill was navy, is gold
        Assert.Equal(0.0588, items[2]!["c"]!["k"]![0]!.GetValue<double>(), 3); // the stroke was gold, is navy
    }

    [Fact]
    public void ASolidLayersColourIsWrittenAsLottieWritesIt()
    {
        var filled = MotionFields.Apply(Strap(), new Dictionary<string, string> { ["color:#336699"] = "#ABCDEF" });
        var json = System.Text.Json.Nodes.JsonNode.Parse(filled.ToUtf8())!;
        Assert.Equal("#abcdef", json["layers"]![4]!["sc"]!.GetValue<string>());
    }

    [Fact]
    public void NewLinesAreStoredAsAfterEffectsWritesThem()
    {
        var filled = MotionFields.Apply(Strap(), new Dictionary<string, string> { ["text:L1"] = "Lead\r\nPastor" });
        var json = System.Text.Json.Nodes.JsonNode.Parse(filled.ToUtf8())!;
        Assert.Equal("Lead\rPastor", json["layers"]![1]!["t"]!["d"]!["k"]![0]!["s"]!["t"]!.GetValue<string>());
    }

    [Fact]
    public void APictureIsSwappedThroughTheLookup()
    {
        var filled = MotionFields.Apply(Strap(), new Dictionary<string, string> { ["image:image_0"] = "media-42" },
            value => value == "media-42" ? "data:image/png;base64,QkJCQg==" : null);
        var asset = System.Text.Json.Nodes.JsonNode.Parse(filled.ToUtf8())!["assets"]![0]!;
        Assert.Equal(("data:image/png;base64,QkJCQg==", 1), (asset["p"]!.GetValue<string>(), asset["e"]!.GetValue<int>()));
        // A value the lookup doesn't know leaves the picture as it was.
        var unchanged = MotionFields.Apply(Strap(), new Dictionary<string, string> { ["image:image_0"] = "gone" }, _ => null);
        Assert.Equal("data:image/png;base64,AAAA", System.Text.Json.Nodes.JsonNode.Parse(unchanged.ToUtf8())!["assets"]![0]!["p"]!.GetValue<string>());
    }

    [Fact]
    public void ValuesThatChangeNothingGiveBackTheSameAnimation()
    {
        var document = Strap();
        Assert.Same(document, MotionFields.Apply(document, null));
        Assert.Same(document, MotionFields.Apply(document, new Dictionary<string, string>
        {
            ["text:L0"] = "Pastor Grace\nOkafor", // the default
            ["color:#FFFFFF"] = "#ffffff",         // the same colour, written differently
            ["text:L99"] = "a layer that isn't there",
            ["color:#123456"] = "#000000",         // a colour the animation doesn't use
            ["color:#F5B700"] = "not a colour",
        }));
    }

    [Fact]
    public void ColoursFromOldFilesWrittenFrom0To255StayInThatScale()
    {
        var json = LottieSamples.Strap.Replace("\"fc\": [0.9608, 0.7176, 0]", "\"fc\": [245, 183, 0]");
        var filled = MotionFields.Apply(LottieDocument.Parse(json), new Dictionary<string, string> { ["color:#F5B700"] = "#102030" });
        var fc = System.Text.Json.Nodes.JsonNode.Parse(filled.ToUtf8())!["layers"]![1]!["t"]!["d"]!["k"]![0]!["s"]!["fc"]!.AsArray();
        Assert.Equal([16.0, 32.0, 48.0], fc.Select(n => n!.GetValue<double>()));
    }

    [Theory]
    [InlineData("#abc", "#AABBCC")]
    [InlineData("#11223344", "#112233")]
    [InlineData("  #a1b2c3 ", "#A1B2C3")]
    [InlineData("A1B2C3", "#A1B2C3")]
    [InlineData("#12345", null)]
    [InlineData("#GGGGGG", null)]
    [InlineData("", null)]
    public void ColoursAreReadInTheUsualWays(string typed, string? expected) => Assert.Equal(expected, MotionFields.NormalHex(typed));

    [Fact]
    public void TheStarterTemplatesHaveTheFieldsTheyPromise()
    {
        var strap = Fields(StarterMotionTemplates.NameStrap());
        Assert.Equal("Pastor Grace Okafor", strap["text:L0"].Default);
        Assert.Equal(("Name", "Title"), (strap["text:L0"].Label, strap["text:L1"].Label));
        Assert.Equal("Title & Accent colour", strap["color:#F5B700"].Label);
        Assert.Equal("Plate colour", strap["color:#0F1F3A"].Label);
        var scripture = Fields(StarterMotionTemplates.ScriptureBar());
        Assert.Equal(["Verse", "Reference"], scripture.Values.Where(f => f.Kind == MotionFieldKind.Text).Select(f => f.Label));
        var card = Fields(StarterMotionTemplates.TitleCard());
        // The title card's glows are gradients: each one's colour is a field.
        Assert.Equal(3, card.Values.Count(f => f.Label.StartsWith("Glow", StringComparison.Ordinal)));
        Assert.Equal("Background colour", card["color:#0B1024"].Label);
    }
}
