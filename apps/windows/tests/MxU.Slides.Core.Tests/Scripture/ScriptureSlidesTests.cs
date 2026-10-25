using System.Text.Json;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Core.Scripture;

namespace MxU.Slides.Core.Tests.Scripture;

public class ScriptureSlidesTests
{
    private static readonly Theme Theme = StarterPack.CleanGeometric.MakeTheme();

    private static PassageText Look(string translation, string reference) =>
        BibleCatalog.BuiltIn.Load(translation).Read(PassageReference.Parse(reference)!);

    private static string VerseText(Slide slide) => slide.Objects.Single(o => o.Name == "Verse").Text;

    private static string? ReferenceText(Slide slide) => slide.Objects.SingleOrDefault(o => o.Name == "Reference")?.Text;

    // A stand-in measurer: anything over this many letters doesn't fit.
    private static Func<StyledText, SceneSize, bool> LongerThan(int letters) => (text, _) => text.String.Length > letters;

    [Fact]
    public void APassageBecomesAnOrdinaryPresentationInTheThemesDesign()
    {
        var passage = Look("kjv", "John 3:16-18");
        var deck = ScriptureSlides.Build(passage, Theme, new ScriptureSlideOptions());
        Assert.Equal("John 3:16–18 (KJV)", deck.Name);
        Assert.Equal(PresentationKind.Deck, deck.PresentationKind);
        Assert.Equal(Theme.Id, deck.ThemeId);
        Assert.Equal("Bible (KJV)", deck.Origin?.Detail);
        Assert.All(deck.Slides, s => Assert.Equal("Verse + Reference", s.ThemeSlideName));
        Assert.All(deck.Slides, s => Assert.All(s.Objects, o => Assert.True(o.X is null && o.TextStyle is null, "the design places and styles the text")));
        Assert.StartsWith("¹⁶ For God so loved the world", VerseText(deck.Slides[0]));
        Assert.StartsWith("John 3:16", ReferenceText(deck.Slides[0]));
        Assert.EndsWith(" KJV", ReferenceText(deck.Slides[0]));
        // Every verse is there, once, in order.
        Assert.Equal(string.Join(" ", passage.Verses.Select(v => $"{ScriptureSlides.Superscript(v.Ref.Verse)} {v.Text}")),
            string.Join(" ", deck.Slides.Select(VerseText)));
        Assert.Equal("John 3", Assert.Single(deck.Sections!).Name);
    }

    [Fact]
    public void EachLayoutUsesItsDesign()
    {
        var passage = Look("kjv", "John 3:16");
        Assert.Equal("Verse + Reference (Lower Third)",
            ScriptureSlides.Build(passage, Theme, new() { Layout = ScriptureLayout.LowerThird }).Slides[0].ThemeSlideName);
        Assert.Equal("Verse + Reference (Side Third)",
            ScriptureSlides.Build(passage, Theme, new() { Layout = ScriptureLayout.SideThird }).Slides[0].ThemeSlideName);
        foreach (var pack in StarterPacks.All)
            foreach (var layout in Enum.GetValues<ScriptureLayout>())
                Assert.Equal(ScriptureSlides.DesignName(layout), ScriptureSlides.Design(pack.MakeTheme(), layout)?.Name);
        // A theme without the scripture designs still gets slides, on its first design.
        var plain = new Theme { Id = "plain", Name = "Plain", Slides = [new Slide { Id = "d", Name = "Lyrics", Objects = [] }] };
        Assert.Equal("Lyrics", ScriptureSlides.Design(plain, ScriptureLayout.LowerThird)?.Name);
    }

    [Fact]
    public void LongPassagesAreSplitByLengthAndNothingOverflows()
    {
        var passage = Look("kjv", "Psalm 119:1-24");
        var pages = ScriptureSlides.Pages(passage, Theme, new ScriptureSlideOptions(), LongerThan(300));
        Assert.True(pages.Count > 3);
        Assert.All(pages, p => Assert.True(p.Text.Length <= 300, p.Text));
        // As full as they can be: no slide could also have taken the first verse of the next.
        for (var i = 0; i + 1 < pages.Count; i++)
        {
            var following = passage.Verses.Single(v => v.Ref == pages[i + 1].First);
            var numbered = $"{ScriptureSlides.Superscript(following.Ref.Verse)} {following.Text}";
            Assert.True(pages[i].Text.Length + 1 + numbered.Length > 300, $"slide {i + 1} had room for {following.Ref}");
        }
        Assert.Equal(passage.Verses.Count, pages.Sum(p => p.Last.Verse - p.First.Verse + 1));
        Assert.Equal("Psalm 119:1–", pages[0].Reference[..12]);
    }

    [Fact]
    public void WithoutAMeasurerAnEstimateKeepsEachSlideInItsBox()
    {
        var passage = Look("kjv", "Romans 8:28-39");
        var lower = ScriptureSlides.Pages(passage, Theme, new ScriptureSlideOptions { Layout = ScriptureLayout.LowerThird });
        Assert.True(lower.Count > 3);
        var design = ScriptureSlides.Design(Theme, ScriptureLayout.LowerThird)!;
        var box = design.Objects.First(o => o.Name == "Verse");
        var style = SlideSceneBuilder.StyledTextFor(new SlideObject { Id = "x", ObjectKind = SlideObjectKind.Text, Name = "Verse", Text = "" }, Theme, box);
        style = style with { MinFontSize = style.FontSize * ScriptureSlides.ReadableShrink };
        Assert.All(lower, p => Assert.False(ScriptureSlides.EstimatedOverflow(style with { String = p.Text }, new SceneSize(box.Width!.Value, box.Height!.Value)), p.Text));
    }

    [Fact]
    public void AVerseTooLongForOneSlideGoesOverSeveralBetweenWords()
    {
        var passage = Look("kjv", "Esther 8:9"); // the Bible's longest verse
        var pages = ScriptureSlides.Pages(passage, Theme, new ScriptureSlideOptions { VerseNumbers = false }, LongerThan(200));
        Assert.True(pages.Count >= 3);
        Assert.All(pages, p => Assert.True(p.Text.Length <= 200));
        Assert.All(pages, p => Assert.Equal("Esther 8:9 KJV", p.Reference));
        Assert.Equal(passage.Verses[0].Text, string.Join(" ", pages.Select(p => p.Text)));
        // Cuts land after punctuation when there's some near the end of a part.
        Assert.Contains(pages.SkipLast(1), p => p.Text[^1] is ',' or ';' or ':' or '.');
    }

    [Fact]
    public void OptionsChangeTheSlides()
    {
        var passage = Look("kjv", "John 3:16-18");
        var single = ScriptureSlides.Build(passage, Theme, new() { VersesPerSlide = 1 });
        Assert.Equal(["John 3:16 KJV", "John 3:17 KJV", "John 3:18 KJV"], single.Slides.Select(ReferenceText));

        var two = ScriptureSlides.Pages(passage, Theme, new() { VersesPerSlide = 2 }, LongerThan(10_000));
        Assert.Equal(["John 3:16–17 KJV", "John 3:18 KJV"], two.Select(p => p.Reference));

        var plain = ScriptureSlides.Build(passage, Theme, new() { VerseNumbers = false, ShowReference = false, VersesPerSlide = 1 });
        Assert.All(plain.Slides, s => Assert.Equal("Verse", Assert.Single(s.Objects).Name));
        Assert.StartsWith("For God so loved", VerseText(plain.Slides[0]));
        Assert.Equal("¹²³⁴⁵⁶⁷⁸⁹⁰", string.Concat(Enumerable.Range(1, 9).Select(ScriptureSlides.Superscript)) + ScriptureSlides.Superscript(0));
        Assert.Equal("¹⁷⁶", ScriptureSlides.Superscript(176));
    }

    [Fact]
    public void ReferenceLinesFollowTheVersesOnEachSlide()
    {
        // A gap that's only a verse the translation leaves out doesn't break the range.
        var acts = ScriptureSlides.Pages(Look("web", "Acts 8:36-38"), Theme, new(), LongerThan(10_000));
        Assert.Equal("Acts 8:36–38 WEB", Assert.Single(acts).Reference);
        // A gap that was asked for does.
        var romans = ScriptureSlides.Pages(Look("kjv", "Rom 8:28, 31-32"), Theme, new(), LongerThan(10_000));
        Assert.Equal("Romans 8:28, 31–32 KJV", Assert.Single(romans).Reference);
        // A new chapter starts a new slide and a new section.
        var across = ScriptureSlides.Build(Look("kjv", "John 3:35-4:2"), Theme, new(), LongerThan(10_000));
        Assert.Equal(["John 3:35–36 KJV", "John 4:1–2 KJV"], across.Slides.Select(ReferenceText));
        Assert.Equal(["John 3", "John 4"], across.Sections!.Select(s => s.Name));
        Assert.Equal(across.Sections!.Select(s => s.Id), across.Slides.Select(s => s.SectionId));
    }

    [Fact]
    public void TwoTranslationsGoSideBySideInTheDesignsVerseBox()
    {
        var kjv = Look("kjv", "John 3:16-18");
        var deck = ScriptureSlides.Build(kjv, Theme, new(), LongerThan(10_000), beside: Look("web", "John 3:16-18"));
        Assert.Equal("John 3:16–18 (KJV, WEB)", deck.Name);
        Assert.Equal("Bible (KJV, WEB)", deck.Origin?.Detail);
        var slide = Assert.Single(deck.Slides);
        Assert.Equal("Verse + Reference", slide.ThemeSlideName);
        Assert.Equal("John 3:16–18 KJV · WEB", ReferenceText(slide));
        var (left, right) = (slide.Objects[0], slide.Objects[1]);
        Assert.Equal(["Verse", "Verse", "Reference"], slide.Objects.Select(o => o.Name));
        Assert.StartsWith("¹⁶ For God so loved the world, that he gave his only begotten Son", left.Text);
        Assert.StartsWith("¹⁶ For God so loved the world, that he gave his only born Son", right.Text);

        // The two halves of the design's verse box, a gutter between, the words a quarter smaller.
        var box = ScriptureSlides.Design(Theme, ScriptureLayout.FullScreen)!.Objects.First(o => o.Name == "Verse");
        var whole = SlideSceneBuilder.Frame(box);
        Assert.Equal((whole.X, whole.Y, whole.Height), (left.X!.Value, left.Y!.Value, left.Height!.Value));
        Assert.Equal(whole.X + whole.Width, right.X!.Value + right.Width!.Value, 3);
        Assert.True(left.X + left.Width < right.X && right.Y == left.Y && right.Width == left.Width);
        Assert.Equal(Math.Round(box.TextStyle!.FontSize!.Value * ScriptureSlides.SideBySideScale), left.TextStyle!.FontSize);
        // Only the size is its own: the rest of the style is the design's, as the Mac reads it too.
        Assert.Null(left.TextStyle.FontName);
        var scene = SlideSceneBuilder.Scene(slide, Theme, deck);
        var texts = scene[LayerKind.Slide].Items.Select(i => i.Content).OfType<ItemContent.Text>().Select(t => t.Styled).ToList();
        var designStyle = SlideSceneBuilder.StyledTextFor(box, Theme, box);
        Assert.Equal(2, texts.Count(t => t.FontName == designStyle.FontName && t.Color == designStyle.Color && t.FontSize == left.TextStyle.FontSize));

        // The side third's box is square, so its halves are one above the other.
        var side = ScriptureSlides.Halves(Theme, ScriptureSlides.Design(Theme, ScriptureLayout.SideThird));
        Assert.True(side.First.X == side.Second.X && side.First.Width == side.Second.Width && side.First.Y + side.First.Height < side.Second.Y);
    }

    [Fact]
    public void SideBySideEachTranslationFitsItsHalfAndAGapStaysEmpty()
    {
        var kjv = Look("kjv", "Acts 8:26-40");
        var web = Look("web", "Acts 8:26-40");
        var pages = ScriptureSlides.Pages(kjv, Theme, new(), LongerThan(220), beside: web);
        Assert.True(pages.Count > 3);
        Assert.All(pages, p => Assert.True(p.Text.Length <= 220 && p.Beside!.Length <= 220, p.Reference));
        // Every verse is on a slide, once, on both sides; the KJV decides which verses there are.
        Assert.Equal(string.Join(" ", kjv.Verses.Select(v => $"{ScriptureSlides.Superscript(v.Ref.Verse)} {v.Text}")), string.Join(" ", pages.Select(p => p.Text)));
        Assert.Equal(string.Join(" ", web.Verses.Select(v => $"{ScriptureSlides.Superscript(v.Ref.Verse)} {v.Text}")), string.Join(" ", pages.Select(p => p.Beside).Where(t => t!.Length > 0)));
        // Acts 8:37 is in the KJV; the WEB's side of its slide just hasn't got it.
        var gap = pages.First(p => p.First.Verse <= 37 && p.Last.Verse >= 37);
        Assert.Contains("³⁷", gap.Text);
        Assert.DoesNotContain("³⁷", gap.Beside);
        Assert.All(pages, p => Assert.EndsWith(" KJV · WEB", p.Reference));
    }

    // A stand-in measurer that wraps: each letter 0.6 of the font size wide, each line 1.2 tall.
    private static bool Wraps(StyledText text, SceneSize frame)
    {
        var lines = Math.Ceiling(text.String.Length * text.FontSize * 0.6 / (frame.Width - text.InsetLeft - text.InsetRight));
        return lines * text.FontSize * 1.2 > frame.Height;
    }

    [Fact]
    public void AReferenceTooLongForItsBoxIsMadeSmallerToStayOnOneLine()
    {
        var lower = new ScriptureSlideOptions { Layout = ScriptureLayout.LowerThird };
        var plate = ScriptureSlides.Design(Theme, ScriptureLayout.LowerThird)!.Objects.First(o => o.Name == "Reference");
        var plateStyle = SlideSceneBuilder.StyledTextFor(plate, Theme, plate);
        // A short one fits the lower third's small plate as the design has it.
        Assert.Null(Assert.Single(ScriptureSlides.Pages(Look("kjv", "John 3:16"), Theme, lower, Wraps)).ReferenceFontSize);

        var deck = ScriptureSlides.Build(Look("kjv", "1 Thess 4:13-18"), Theme, lower, Wraps);
        Assert.All(deck.Slides, slide =>
        {
            var reference = slide.Objects.Single(o => o.Name == "Reference");
            var size = reference.TextStyle?.FontSize;
            Assert.True(size < plateStyle.FontSize && size >= Math.Round(plateStyle.FontSize * 0.6), $"{reference.Text}: {size}");
            // At that size it sits on one line of the plate; only the size is its own.
            Assert.False(Wraps(plateStyle with { String = reference.Text, FontSize = size!.Value }, new SceneSize(plate.Width!.Value, size.Value * 1.5)));
            Assert.Null(reference.TextStyle!.FontName);
        });
        // Full screen has room for it at the design's size.
        Assert.All(ScriptureSlides.Pages(Look("kjv", "1 Thess 4:13-18"), Theme, new(), Wraps), p => Assert.Null(p.ReferenceFontSize));
    }

    [Fact]
    public void GeneratedPresentationsAreValidSharedDocuments()
    {
        foreach (var layout in Enum.GetValues<ScriptureLayout>())
        {
            foreach (var beside in new[] { null, Look("kjv", "Psalm 23") })
            {
                var deck = ScriptureSlides.Build(Look("web", "Psalm 23"), Theme, new() { Layout = layout }, beside: beside);
                var json = MxuJson.Serialize(deck);
                var back = MxuJson.Deserialize<Presentation>(json);
                Assert.Equal(json, MxuJson.Serialize(back));
                SharedSchema.Validate("Presentation", JsonDocument.Parse(json).RootElement);
            }
        }
    }
}

/// <summary>
/// Checks a document's JSON against packages/schema/documents.json, the schema the Mac app's
/// models come from: every field must be one it knows, every required one present, every enum
/// value one of its cases. The Mac refuses a whole document that breaks any of these.
/// </summary>
internal static class SharedSchema
{
    private static readonly Lazy<JsonElement> Types = new(() =>
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            var path = Path.Combine(folder.FullName, "packages", "schema", "documents.json");
            if (File.Exists(path)) return JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("types");
        }
        throw new FileNotFoundException("packages/schema/documents.json");
    });

    public static void Validate(string type, JsonElement value, string path = "$")
    {
        switch (type)
        {
            case "string":
                Assert.True(value.ValueKind == JsonValueKind.String, $"{path} should be a string");
                return;
            case "bool":
                Assert.True(value.ValueKind is JsonValueKind.True or JsonValueKind.False, $"{path} should be true or false");
                return;
            case "int":
                Assert.True(value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _), $"{path} should be a whole number");
                return;
            case "double":
                Assert.True(value.ValueKind == JsonValueKind.Number, $"{path} should be a number");
                return;
        }
        if (type.StartsWith("list:", StringComparison.Ordinal))
        {
            Assert.True(value.ValueKind == JsonValueKind.Array, $"{path} should be a list");
            var i = 0;
            foreach (var item in value.EnumerateArray()) Validate(type[5..], item, $"{path}[{i++}]");
            return;
        }
        var definition = Types.Value.GetProperty(type);
        if (definition.GetProperty("kind").GetString() == "enum")
        {
            var cases = definition.GetProperty("cases").EnumerateArray().Select(c => c.GetString()).ToList();
            Assert.True(value.ValueKind == JsonValueKind.String && cases.Contains(value.GetString()), $"{path}: “{value}” isn't a {type} the Mac knows");
            return;
        }
        Assert.True(value.ValueKind == JsonValueKind.Object, $"{path} should be a {type}");
        var fields = definition.GetProperty("fields").EnumerateArray().ToDictionary(f => f.GetProperty("name").GetString()!);
        foreach (var property in value.EnumerateObject())
        {
            Assert.True(fields.TryGetValue(property.Name, out var field), $"{path}.{property.Name} isn't a field of {type} on the Mac");
            if (property.Value.ValueKind == JsonValueKind.Null) continue;
            Validate(field.GetProperty("type").GetString()!, property.Value, $"{path}.{property.Name}");
        }
        foreach (var (name, field) in fields)
        {
            var optional = field.TryGetProperty("optional", out var o) && o.GetBoolean();
            if (!optional) Assert.True(value.TryGetProperty(name, out _), $"{path} is missing {type}.{name}");
        }
    }
}
