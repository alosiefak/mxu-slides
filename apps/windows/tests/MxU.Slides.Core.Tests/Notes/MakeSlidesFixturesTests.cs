using System.Text.Json;
using System.Text.Json.Nodes;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Notes;

namespace MxU.Slides.Core.Tests.Notes;

/// <summary>
/// The shared Make Slides cases in packages/make-slides-fixtures, read exactly as the Mac's
/// MakeSlidesFixturesTests reads them: the suggester's passages, and the deck the planner makes
/// from them with no theme. Both apps must give the same answers from the same note.
/// </summary>
public class MakeSlidesFixturesTests
{
    internal static string Folder { get; } = FindFolder();

    private static string FindFolder()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            var path = Path.Combine(folder.FullName, "packages", "make-slides-fixtures");
            if (Directory.Exists(path)) return path;
        }
        throw new DirectoryNotFoundException("packages/make-slides-fixtures");
    }

    public static TheoryData<string> Files() => new(Directory.GetFiles(Folder, "*.json").Select(Path.GetFileName).Order(StringComparer.Ordinal).OfType<string>());

    public static TheoryData<string> LiveFiles() =>
        new(Directory.GetFiles(Folder, "*.json").Where(f => File.ReadAllText(f).Contains("\"liveAdds\"")).Select(Path.GetFileName).Order(StringComparer.Ordinal).OfType<string>());

    internal static NoteBody Body(JsonObject json) => MxuJson.Deserialize<NoteBody>(json["body"]!.ToJsonString());

    private static JsonObject Read(string file) => JsonNode.Parse(File.ReadAllText(Path.Combine(Folder, file)))!.AsObject();

    /// <summary>A suggestion as the fixture writes it: missing keys are left out.</summary>
    private static string Suggestion(string? type, string text, IReadOnlyList<string>? media)
    {
        var o = new JsonObject();
        if (type is not null) o["type"] = type;
        o["text"] = text;
        if (media is { Count: > 0 }) o["media"] = new JsonArray(media.Select(m => (JsonNode)m).ToArray());
        return o.ToJsonString();
    }

    private static string Normalized(JsonNode? node) => node is JsonArray a
        ? "[" + string.Join(",", a.Select(item => item is JsonObject o ? Sorted(o) : item?.ToJsonString())) + "]"
        : "[]";

    private static string Sorted(JsonObject o) => "{" + string.Join(",", o.OrderBy(p => p.Key, StringComparer.Ordinal)
        .Select(p => $"\"{p.Key}\":{(p.Value is JsonObject inner ? Sorted(inner) : p.Value is JsonArray arr ? Normalized(arr) : p.Value?.ToJsonString() ?? "null")}")) + "}";

    [Fact]
    public void TheFixturesFolderHoldsTheUnshakableNote() =>
        Assert.Contains("unshakable.json", Directory.GetFiles(Folder, "*.json").Select(Path.GetFileName));

    [Theory]
    [MemberData(nameof(Files))]
    public void FixtureGivesItsSuggestionsAndSlides(string file)
    {
        var json = Read(file);
        var name = (string)json["name"]!;
        var body = Body(json);
        var options = SlideOptions.FromJson(json["options"]);
        var mode = NoteSlideMarks.ParseMode((string?)json["mode"]) ?? NoteSlideMarks.SuggestMode.PickForMe;
        var blanksRaw = (string?)json["blanks"];
        var blanks = blanksRaw switch { "afterPassage" => BlankPlacement.AfterPassage, "afterSlide" => BlankPlacement.AfterSlide, _ => BlankPlacement.None };

        var suggested = NoteSlideMarks.Suggesting(body, mode, options.OwnTypes.Select(t => t.Name).ToList());
        var groups = NoteSlideMarks.Groups(suggested);
        Assert.True(groups.All(g => g.State == NoteSlideMarks.Suggested), name);
        var suggestions = "[" + string.Join(",", groups.Select(g => Suggestion(g.Type, g.Text, g.Media))) + "]";
        Assert.Equal(json["suggestions"]!.AsArray().Count, groups.Count);
        Assert.Equal(Normalized(json["suggestions"]), Normalized(JsonNode.Parse(suggestions)));

        var marked = NoteSlideBuilder.Groups(NoteSlideMarks.AcceptingSuggestions(suggested));
        var rows = new DeterministicSlidePlanner(options).Plan(marked, null, new Dictionary<string, SlideRowOverride>()).Rows;
        var blankPlacements = marked.ToDictionary(g => g.Id, _ => blanks);
        var built = NoteSlideBuilder.Build(new SlidePlan(rows), blankPlacements, new NoteSlideBuilder.BlankStyle(), options);
        var kinds = rows.ToDictionary(r => r.GroupId, r => r.Kind.Raw());
        var made = new JsonArray(built.Slides.Select(slide =>
        {
            var main = slide.Objects.FirstOrDefault();
            var o = new JsonObject
            {
                ["kind"] = built.GroupBySlideId.TryGetValue(slide.Id, out var g) && kinds.TryGetValue(g, out var k) ? k : SlideKind.Blank.Raw(),
                ["text"] = string.Join("\n", slide.Objects.Select(x => x.Text)),
            };
            if (slide.Notes is not null) o["notes"] = slide.Notes;
            if (main?.AnimationSteps is { } steps) o["reveal"] = new JsonArray(steps.Select(s => s.Ranges?.FirstOrDefault()?.Line).OfType<int>().Select(l => (JsonNode)l).ToArray());
            if (main?.StyleRuns is { } runs)
                o["underlines"] = new JsonArray(runs.Select(r => (JsonNode)new JsonObject { ["column"] = r.Column, ["length"] = r.Length, ["line"] = r.Line }).ToArray());
            if (slide.Background is { } background) o["media"] = NoteMedia.KeyOfPendingId(background.MediaId) ?? background.MediaId;
            if (slide.Background?.ScaleMode is { } scale) o["scale"] = MediaScaleModeJsonConverter.ToRaw(scale);
            return (JsonNode)o;
        }).ToArray());
        Assert.NotEmpty(made);
        Assert.Equal(json["slides"]!.AsArray().Count, made.Count);
        Assert.Equal(Normalized(json["slides"]), Normalized(made));
    }

    [Theory]
    [MemberData(nameof(LiveFiles))]
    public void ALiveNoteOffersItsAdds(string file)
    {
        var json = Read(file);
        var body = Body(json);
        var made = NoteSlides.Make(body, "", new SlideDesignMap(), _ => null);
        var adds = NoteLiveUpdates.Candidates(made.Body, NoteLiveUpdates.BuildMode(body))
            .Select(c => Suggestion(c.Type, string.Join("\n", c.Blocks.Select(b => NoteReading.Text(made.Body.Blocks[b]))), null));
        Assert.Equal(Normalized(json["liveAdds"]), Normalized(JsonNode.Parse("[" + string.Join(",", adds) + "]")));
    }

    [Fact]
    public void MakingFromTheNoteMakesTheDeckTheSheetMakes()
    {
        var body = Body(Read("slide-markers.json"));
        static List<string> Look(IEnumerable<Slide> slides) => slides.Select(slide => string.Join(" | ",
            string.Join("\n", slide.Objects.Select(o => o.Text)), slide.Notes ?? "",
            slide.Background is { } b ? NoteMedia.KeyOfPendingId(b.MediaId) ?? b.MediaId : "")).ToList();

        var made = NoteSlides.Make(body, "", new SlideDesignMap(), _ => null);
        var marked = NoteSlideBuilder.Groups(NoteSlideMarks.AcceptingSuggestions(NoteSlideMarks.AutoMark.PickForMe.Marking(body)));
        var sheet = NoteSlideBuilder.Build(new DeterministicSlidePlanner().Plan(marked, null, new Dictionary<string, SlideRowOverride>()));
        Assert.Equal(Look(sheet.Slides), Look(made.NewSlides));
        Assert.Equal(marked.Select(g => g.Type), NoteSlideMarks.Groups(made.Body).Select(g => g.Type));
        Assert.Equal(["points", "video", null, "points", "picture", null], marked.Select(g => g.Type));
    }

    // The Mac's four packs (StarterPack.allCases there). Windows' own worship looks (Phase 6) put
    // a number box in their Points design, so the planner rightly gives them a point a slide.
    public static TheoryData<StarterPack> Packs() =>
        new(StarterPack.CleanGeometric, StarterPack.EditorialSerif, StarterPack.BoldBlocks, StarterPack.Digital);

    [Theory]
    [MemberData(nameof(Packs))]
    public void ThreeWarningsAreOnePointsSlide(StarterPack pack)
    {
        var body = Body(Read("slide-markers.json"));
        static NoteBlock P(string text) => new() { Type = NoteBlockType.Paragraph, Runs = [new NoteRun { Text = text }] };
        body.Blocks.AddRange([
            P("Slide 8:"), P("Three Warnings:"),
            new NoteBlock
            {
                Type = NoteBlockType.List, Ordered = false, Items =
                [
                    new NoteListItem { Runs = [new NoteRun { Text = "\"Don't forget Me when you experience prosperity\" (vs. 12)" }] },
                    new NoteListItem { Runs = [new NoteRun { Text = "Don't fall for the surrounding culture's idols (vs. 14)" }] },
                    new NoteListItem { Runs = [new NoteRun { Text = "Don't test God when hardship comes (vs. 16)" }] },
                ],
            },
        ]);
        var marked = NoteSlideBuilder.Groups(NoteSlideMarks.AcceptingSuggestions(NoteSlideMarks.AutoMark.PickForMe.Marking(body)));
        var warnings = marked[^1];
        Assert.Equal("points", warnings.Type);
        var theme = pack.MakeTheme();
        var built = NoteSlideBuilder.Build(new DeterministicSlidePlanner().Plan(marked, theme, new Dictionary<string, SlideRowOverride>()));
        var slides = built.Slides.Where(s => built.GroupBySlideId.GetValueOrDefault(s.Id) == warnings.Id).ToList();
        Assert.True(slides.Count == 1, $"{pack}: {string.Join(" / ", slides.Select(s => string.Join("|", s.Objects.Select(o => o.Text))))}");
        Assert.Equal("Points", slides[0].ThemeSlideName);
    }

    [Fact]
    public void TheRepairGivesThePointsBoxItsFloor()
    {
        var pack = StarterPack.BoldBlocks;
        var fresh = pack.MakeTheme().Slides!;
        List<Slide> Changing(IReadOnlyList<Slide> slides, Action<TextStyle> change)
        {
            var copy = MxuJson.Clone(slides.ToList());
            foreach (var box in copy.SelectMany(s => s.Objects).Where(o => o.Id == $"{pack.ThemeId()}.points.text" && o.TextStyle is not null))
                change(box.TextStyle!);
            return copy;
        }
        var stale = Changing(fresh, style => (style.AutoShrink, style.MinFontSize) = (null, null));
        Assert.False(MxuJson.StructurallyEqual(stale, fresh));
        Assert.True(MxuJson.StructurallyEqual(fresh, pack.ShrinkingPoints(stale)));
        // A box whose owner chose not to shrink is left alone.
        var chosen = Changing(stale, style => style.AutoShrink = false);
        Assert.True(MxuJson.StructurallyEqual(chosen, pack.ShrinkingPoints(chosen)));
    }

    [Theory]
    [MemberData(nameof(Packs))]
    public void LongPointsKeepTheirSizeAndSplit(StarterPack pack)
    {
        const string rules = "Backyard Ultra - Rules:\nComplete 4.167 miles, every hour, on the hour.\nWarnings given 3, 2, and 1 minute prior to each loop. All competitors must be in the starting corral before the starting whistle.\nYou win by being the last person standing and must complete an additional loop in order to win outright.";
        var theme = pack.MakeTheme();
        var built = NoteSlideBuilder.Build(new DeterministicSlidePlanner().Plan([new MarkedGroup("r", rules, "points")], theme, new Dictionary<string, SlideRowOverride>()));
        Assert.True(built.Slides.Count >= 2, $"{pack}: {string.Join(" / ", built.Slides.Select(s => string.Join("|", s.Objects.Select(o => o.Text))))}");
        var box = Json.MxuJson.Clone(theme.Slides!.First(s => s.Name == "Points").Objects.First(o => o.Id == $"{pack.ThemeId()}.points.text"));
        box.TextStyle!.AutoShrink = null;
        foreach (var slide in built.Slides)
        {
            var words = slide.Objects.First(o => o.Text.Contains('\n') || o.Text.Length > 30).Text;
            Assert.True(NoteSlideBuilder.Fits(words, box, theme), $"{pack}: fits at full size: {words}");
        }
    }
}
