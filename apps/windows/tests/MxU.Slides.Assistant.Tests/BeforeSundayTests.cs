using System.Text.Json;
using MxU.Slides.Assistant.Testing;
using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Notes;
using MxU.Slides.Core.Scripture;

namespace MxU.Slides.Assistant.Tests;

/// <summary>
/// Suggest with AI, lyrics clean-up and the prayer-request filter. The model's replies are
/// scripted (some of them deliberately bad), so these test what the app does with a reply, not
/// how good the real model's replies are.
/// </summary>
public sealed class BeforeSundayTests
{
    private const string Sermon =
        "Unshakable\n"
        + "Intro: the earthquake in 1989\n"
        + "John 3:16\n"
        + "Psalm 46:1 – God is our refuge and strength, a very present help in trouble.\n"
        + "1. Everything that can be shaken will be shaken\n"
        + "2. What God builds can’t be shaken\n"
        + "Prayer requests:\n"
        + "Mary Okafor, surgery on Tuesday\n"
        + "The Bello family";

    private static readonly Bible Kjv = BibleCatalog.BuiltIn.Load("kjv");

    private static PassageText? Read(PassageReference reference) => Kjv.Read(reference);

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static int Block(NoteBody body, string starts) => body.Blocks.FindIndex(b => NoteReading.Text(b).StartsWith(starts, StringComparison.Ordinal));

    // MARK: Suggest with AI

    [Fact]
    public void TheSchemasAllowOnlyRealSlideTypesAndRealSectionLabels()
    {
        var kinds = Json(NoteSuggester.Schema).GetProperty("properties").GetProperty("passages").GetProperty("items").GetProperty("properties").GetProperty("kind").GetProperty("enum")
            .EnumerateArray().Select(k => k.GetString()!).ToList();
        Assert.NotEmpty(kinds);
        // Each is a type a note's mark can hold, and a kind the planner knows: nothing new enters a shared note.
        Assert.All(kinds, kind => Assert.NotNull(NoteSlideMarks.ParseType(kind)));
        Assert.All(kinds, kind => Assert.NotNull(SlideKinds.Parse(kind)));
        // No field for words: the model has nowhere to put verse text or rewritten notes.
        var fields = Json(NoteSuggester.Schema).GetProperty("properties").GetProperty("passages").GetProperty("items").GetProperty("properties").EnumerateObject().Select(p => p.Name);
        Assert.Equal(["first_block", "last_block", "kind", "reference"], fields);

        var labels = Json(LyricsCleanup.Schema).GetProperty("properties").GetProperty("sections").GetProperty("items").GetProperty("properties").GetProperty("label").GetProperty("enum")
            .EnumerateArray().Select(k => k.GetString()!).ToList();
        Assert.All(labels, label => Assert.NotNull(Reflow.LabelName(label)));
        Assert.All(labels, label => Assert.NotNull(Reflow.LabelName(label + " 2")));
    }

    [Fact]
    public void PrayerRequestNamesAreNeverInWhatIsSent()
    {
        var body = NoteFileReader.Body(Sermon);

        var prepared = NoteSuggester.Prepare(body, Read);

        Assert.DoesNotContain("Okafor", prepared.Request.Ask, StringComparison.Ordinal);
        Assert.DoesNotContain("Bello", prepared.Request.Ask, StringComparison.Ordinal);
        Assert.DoesNotContain("surgery", prepared.Request.Ask, StringComparison.Ordinal);
        Assert.Contains(PrayerPrivacy.Placeholder, prepared.Request.Ask, StringComparison.Ordinal);
        Assert.Contains("kept private", prepared.PrivacyWarning, StringComparison.Ordinal);
        // The rest of the note is sent, numbered by block.
        Assert.Contains($"[{Block(body, "Unshakable")}] ", prepared.Request.Ask, StringComparison.Ordinal);
        Assert.Contains("John 3:16", prepared.Request.Ask, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Pray for Mary's surgery on Tuesday", true)]
    [InlineData("Please keep Tunde in your prayers", true)]
    [InlineData("Prayer request: the Adeyemi family", true)]
    [InlineData("3. Pray for your enemies", false)]
    [InlineData("The Lord's Prayer teaches us to forgive", false)]
    public void ASingleLineNamingSomeoneToPrayForIsHeldBack(string line, bool held)
    {
        Assert.Equal(held, PrayerPrivacy.Strip([line]).Withheld.Count == 1);
    }

    [Fact]
    public void APrayerListEndsAtTheNextHeading()
    {
        var result = PrayerPrivacy.Strip(["Praise reports", "Ade got the job", "", "Announcements:", "Picnic on Saturday"]);

        Assert.Equal([0, 1], result.Withheld);
        Assert.Equal("Picnic on Saturday", result.Lines[4]);
        Assert.Equal(5, result.Lines.Count);
    }

    [Fact]
    public void VerseTextComesFromOurBibleWhateverTheReplyQuotes()
    {
        var body = NoteFileReader.Body(Sermon);
        var (title, john, psalm) = (Block(body, "Unshakable"), Block(body, "John 3:16"), Block(body, "Psalm 46:1"));
        // A misbehaving reply: it carries its own (wrong) verse text and a wrong reference for the bare line.
        var reply = Json($$"""
            {"passages":[
              {"first_block":{{title}},"last_block":{{title}},"kind":"title","reference":null},
              {"first_block":{{john}},"last_block":{{john}},"kind":"scripture","reference":"Romans 8:28","text":"For God was so fond of the world"},
              {"first_block":{{psalm}},"last_block":{{psalm}},"kind":"scripture","reference":"Psalm 46:1","text":"God is our shelter"}
            ]}
            """);

        var applied = NoteSuggester.Apply(body, NoteSuggester.Read(reply, body.Blocks.Count), Read);

        Assert.Equal(3, applied.Passages);
        Assert.Equal(1, applied.VersesFilled);
        var groups = NoteSlideMarks.Groups(applied.Body);
        Assert.All(groups, g => Assert.Equal(NoteSlideMarks.Suggested, g.State));
        var scripture = groups.Where(g => g.Type == "scripture").ToList();
        // The bare reference got the King James words for the reference in the NOTE (John 3:16), not the reply's.
        Assert.Equal(Kjv[new VerseRef(BibleBooks.Find("John")!.Index, 3, 16)] + "\nJohn 3:16 (KJV)", scripture[0].Text);
        Assert.DoesNotContain("fond", applied.Body.Blocks.Select(NoteReading.Text).Aggregate(string.Concat), StringComparison.Ordinal);
        // The pastor's own verse stays exactly as written.
        Assert.StartsWith("Psalm 46:1 – God is our refuge", scripture[1].Text, StringComparison.Ordinal);

        // And it goes through the same planner Pick for me uses: a scripture slide with the verse and its reference.
        var plan = new DeterministicSlidePlanner().Plan(NoteSlideBuilder.Groups(NoteSlideMarks.AcceptingSuggestions(applied.Body)), null, new Dictionary<string, SlideRowOverride>());
        var row = plan.Rows.First(r => r.GroupId == scripture[0].Id);
        Assert.Equal(SlideKind.Scripture, row.Kind);
        var words = string.Join("\n", row.Parts.SelectMany(part => part).Select(box => box.Text));
        Assert.Contains("For God so loved the world", words, StringComparison.Ordinal);
        Assert.Contains("John 3:16", words, StringComparison.Ordinal);
    }

    [Fact]
    public void SeveralVersesAreNumberedSoTheyCanBeSplit()
    {
        var body = NoteFileReader.Body("Romans 8:28-29");
        var reply = Json("""{"passages":[{"first_block":0,"last_block":0,"kind":"scripture","reference":"Romans 8:28-29"}]}""");

        var applied = NoteSuggester.Apply(body, NoteSuggester.Read(reply, body.Blocks.Count), Read);

        var text = Assert.Single(NoteSlideMarks.Groups(applied.Body)).Text;
        Assert.StartsWith("28 And we know that all things work together for good", text, StringComparison.Ordinal);
        Assert.Contains(" 29 For whom he did foreknow", text, StringComparison.Ordinal);
        Assert.EndsWith("\nRomans 8:28–29 (KJV)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AReplyCannotIntroduceAKindOrABlockThatIsNotThere()
    {
        var body = NoteFileReader.Body(Sermon);
        var reply = Json("""
            {"passages":[
              {"first_block":0,"last_block":0,"kind":"sparkle","reference":null},
              {"first_block":0,"last_block":0,"kind":"lyrics","reference":null},
              {"first_block":2,"last_block":99,"kind":"body","reference":null},
              {"first_block":-1,"last_block":0,"kind":"body","reference":null},
              {"first_block":1,"last_block":1,"kind":"body","reference":null},
              {"first_block":1,"last_block":2,"kind":"points","reference":null},
              {"kind":"body"},
              "nonsense"
            ]}
            """);

        var read = NoteSuggester.Read(reply, body.Blocks.Count);

        // Only the one well-formed passage survives; the one overlapping it is dropped.
        var only = Assert.Single(read);
        Assert.Equal([1], only.Blocks);
        Assert.Equal("body", only.Kind);
        Assert.Empty(NoteSuggester.Read(Json("""{"something":"else"}"""), 3));
    }

    [Fact]
    public void LinesMarkedByHandAreNotSentAndNotChanged()
    {
        var body = NoteFileReader.Body(Sermon);
        var title = Block(body, "Unshakable");
        body.Blocks[title] = NoteSlideMarks.Marking(body.Blocks[title], "byhand", "quote");

        var prepared = NoteSuggester.Prepare(body, Read);
        var applied = NoteSuggester.Apply(body, NoteSuggester.Read(Json($$"""{"passages":[{"first_block":{{title}},"last_block":{{title}},"kind":"title","reference":null}]}"""), body.Blocks.Count), Read);

        Assert.Contains($"[{title}] (paragraph) [marked by hand]", prepared.Request.Ask, StringComparison.Ordinal);
        Assert.Equal(0, applied.Passages);
        Assert.Equal("quote", Assert.Single(NoteSlideMarks.Groups(applied.Body)).Type);
    }

    [Fact]
    public void TheLookUpToolAnswersFromOurBibleWithoutTheVerseText()
    {
        Assert.Equal("John 3:16 is in the church's Bible (KJV): 1 verse.", NoteSuggester.LookUp("John 3:16", Read));
        Assert.StartsWith("John 40:1 isn't in the church's Bible.", NoteSuggester.LookUp("John 40:1", Read), StringComparison.Ordinal);
        Assert.Contains("doesn't read as a Bible reference", NoteSuggester.LookUp("the prodigal son", Read), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuggestWithAiEndToEndAgainstTheStandInServer()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"MxU assistant tests {Guid.NewGuid():N}");
        using var server = new FakeAnthropicServer();
        var vault = new AssistantKeyVault(Path.Combine(folder, "key.bin"));
        vault.Save(AssistantClientTests.Key);
        using var service = new AssistantService(vault, new AssistantLog(Path.Combine(folder, "log.jsonl")), () => true, () => null, server.Url);
        var body = NoteFileReader.Body(Sermon);
        var john = Block(body, "John 3:16");
        server.ThenToolUse("look_up_passage", """{"reference":"John 3:16"}""")
            .ThenText($$"""{"passages":[{"first_block":{{john}},"last_block":{{john}},"kind":"scripture","reference":"John 3:16"}]}""");

        var prepared = NoteSuggester.Prepare(body, Read);
        var reply = await service.Ask(prepared.Request, TestContext.Current.CancellationToken);
        var applied = NoteSuggester.Apply(body, NoteSuggester.Read(reply.Json, body.Blocks.Count), Read);

        Assert.Equal(1, applied.VersesFilled);
        // The tool's answer went back to the model, and no request carried a prayer name.
        Assert.Contains("Bible (KJV): 1 verse.", server.Requests[1].Body, StringComparison.Ordinal);
        Assert.All(server.Requests, r => Assert.DoesNotContain("Okafor", r.Body, StringComparison.Ordinal));
        Directory.Delete(folder, recursive: true);
    }

    // MARK: Lyrics clean-up

    private const string Pasted =
        "Amazing Grace Lyrics\n"
        + "Amazing grace! How sweet the sound, that saved a wretch like me!\n"
        + "I once was lost, but now am found; was blind, but now I see.\n"
        + "Praise God, praise God\n"
        + "’Twas grace that taught my heart to fear, and grace my fears relieved;\n"
        + "Praise God, praise God\n"
        + "Lyrics provided by SongSite. Embed. Share.";

    private const string GoodReply = """
        {"sections":[
          {"label":"Verse","number":1,"lines":["Amazing grace! How sweet the sound,","that saved a wretch like me!","I once was lost, but now am found;","was blind, but now I see."]},
          {"label":"Chorus","number":null,"lines":["Praise God, praise God"]},
          {"label":"Verse","number":2,"lines":["’Twas grace that taught my heart to fear,","and grace my fears relieved;"]}
        ],"order":[0,1,2,1]}
        """;

    [Fact]
    public void CleanedLyricsAreLabelledReBrokenAndInSungOrder()
    {
        var cleaned = LyricsCleanup.Read(Json(GoodReply), Pasted);

        Assert.Equal(
            "Verse 1\nAmazing grace! How sweet the sound,\nthat saved a wretch like me!\nI once was lost, but now am found;\nwas blind, but now I see.\n\n"
            + "Chorus\nPraise God, praise God\n\n"
            + "Verse 2\n’Twas grace that taught my heart to fear,\nand grace my fears relieved;\n\n"
            + "Chorus", cleaned.Text);
        Assert.Equal(3, cleaned.Sections);
        // The site's clutter is listed for the operator, not silently dropped.
        Assert.Equal(["Amazing Grace Lyrics", "Lyrics provided by SongSite. Embed. Share."], cleaned.LeftOut);
        // The existing lyric pipeline reads it: three sections, the chorus sung twice.
        var parsed = Reflow.Parse(cleaned.Text, 2);
        Assert.Equal(["Verse 1", "Chorus", "Verse 2"], parsed.Sections.Select(s => s.Name));
        Assert.Equal([0, 1, 2, 1], parsed.Order);
        // Word for word, the result is the pasted lyrics (less the clutter).
        var sung = LyricsCleanup.Words(string.Join("\n", parsed.Sections.SelectMany(s => s.Slides)));
        var original = LyricsCleanup.Words(string.Join("\n", Pasted.Split('\n')[1..5]));
        Assert.Equal(original, sung);
    }

    [Theory]
    [InlineData("that saved a wretch like me!", "that saved a soul like me!")] // a changed word
    [InlineData("that saved a wretch like me!", "that saved a wretch like me! Hallelujah!")] // an added word
    [InlineData("that saved a wretch like me!", "that saved a wretch like me")] // punctuation is part of the word
    [InlineData("Amazing grace! How sweet the sound,", "amazing grace! How sweet the sound,")] // so are capitals
    [InlineData("\"I once was lost, but now am found;\",\"was blind, but now I see.\"", "\"was blind, but now I see.\",\"I once was lost, but now am found;\"")] // moved
    public void AReplyThatChangesAddsOrMovesAWordIsRejected(string from, string to)
    {
        var tampered = GoodReply.Replace(from, to, StringComparison.Ordinal);
        Assert.NotEqual(GoodReply, tampered);

        var failure = Assert.Throws<AssistantException>(() => LyricsCleanup.Read(Json(tampered), Pasted));

        Assert.Equal(AssistantProblem.BadReply, failure.Problem);
        Assert.Contains("Your lyrics are unchanged", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOrderThatWouldLoseASectionIsIgnored()
    {
        var cleaned = LyricsCleanup.Read(Json(GoodReply.Replace("[0,1,2,1]", "[0,7,1]", StringComparison.Ordinal)), Pasted);

        Assert.Equal([0, 1, 2], Reflow.Parse(cleaned.Text, 2).Order);
        Assert.Throws<AssistantException>(() => LyricsCleanup.Read(Json("""{"sections":[],"order":[]}"""), Pasted));
        Assert.Throws<AssistantException>(() => LyricsCleanup.Read(Json("""{"sections":[{"label":"Rap","number":null,"lines":["Amazing"]}],"order":[0]}"""), Pasted));
    }
}
