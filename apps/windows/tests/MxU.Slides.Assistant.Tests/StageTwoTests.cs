using System.Text.Json;
using System.Text.Json.Nodes;
using MxU.Slides.Assistant.Testing;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Assistant.Tests;

/// <summary>
/// Stage 2 of the assistant: the order of service, typed requests, the service report, slide
/// checks, translation, captions and the sermon summary. As everywhere here, "Anthropic" is the
/// stand-in server or a scripted reply: these test what the app does with an answer.
/// </summary>
public sealed class StageTwoTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), $"MxU assistant tests {Guid.NewGuid():N}");
    private readonly FakeAnthropicServer server = new();
    private readonly AssistantLog log;
    private readonly AssistantClient client;

    public StageTwoTests()
    {
        log = new AssistantLog(Path.Combine(folder, "log.jsonl"));
        client = new AssistantClient(log, server.Url);
    }

    public void Dispose()
    {
        server.Dispose();
        try { Directory.Delete(folder, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private const string Key = "sk-ant-api03-test-0123456789abcdefghijklmnop";

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static Presentation Song(string id, string name, string? author = null) => new()
    {
        Id = id, Name = name, PresentationKind = PresentationKind.Song, ThemeId = "theme", Slides = [],
        Ccli = author is null ? null : new CCLIInfo { Author = author, SongTitle = name },
    };

    private static readonly Presentation[] Songs = [Song("s-grace", "Amazing Grace", "John Newton"), Song("s-way", "Way Maker", "Sinach"), Song("s-holy-a", "Holy", "A"), Song("s-holy-b", "Holy", "B")];

    private static readonly LibraryFacts Facts = new(
    [
        new("s-grace", "presentations", "Amazing Grace", "John Newton"),
        new("s-way", "presentations", "Way Maker", "Sinach"),
        new("ov-giving", "overlays", "Giving", "Lower thirds"),
        new("ov-welcome", "overlays", "Welcome"),
        new("th-1", "themes", "Sunday Blue"),
        new("row-2", "service-items", "Way Maker"),
    ], "1. Welcome | id row-1 | info\n2. Way Maker | id row-2 | presentation | LIVE");

    // MARK: Order of service

    private const string Bulletin =
        "Sunday Morning Worship, 8 November 2026\n"
        + "WORSHIP\n"
        + "10:00 Welcome\n"
        + "10:05 Amazing Grace - John Newton\n"
        + "10:10 Song: Oceans\n"
        + "Holy (4 min)\n"
        + "The Word:\n"
        + "Reading: John 3:16-18\n"
        + "Sermon - 30 min\n"
        + "Prayer requests:\n"
        + "Mary Okafor, surgery on Tuesday";

    [Fact]
    public void ABulletinIsReadWithoutAnyAi()
    {
        var plan = ServicePlanReader.ReadPlainly(Bulletin, Songs);
        Assert.Equal("Sunday Morning Worship, 8 November 2026", plan.Name);
        Assert.Equal("2026-11-08", plan.Date);
        Assert.Equal(
            ["header:Worship", "item:Welcome", "song:Amazing Grace", "song:Oceans", "song:Holy", "header:The Word", "scripture:John 3:16–18", "item:Sermon"],
            plan.Rows.Select(r => $"{r.Kind}:{r.Title}"));
        // Start times give lengths; so do "(4 min)" and "- 30 min".
        Assert.Equal([null, 5, 5, null, 4, null, null, 30], plan.Rows.Select(r => r.Minutes));
        Assert.Equal("s-grace", plan.Rows[2].Song?.Id);
        // A song the library doesn't have, and one it has two of, are listed, never made up.
        Assert.Equal(["Oceans", "Holy (more than one in your library)"], plan.MissingSongs);
        Assert.DoesNotContain(plan.Rows, r => r.Title.Contains("Okafor", StringComparison.Ordinal) || r.Title.Contains("Prayer", StringComparison.Ordinal));
    }

    [Fact]
    public void TheAssistantsPlanIsMatchedToTheLibraryAndNothingIsInvented()
    {
        var prepared = ServicePlanReader.Prepare(Bulletin, Facts);
        Assert.DoesNotContain("Okafor", prepared.Request.Ask, StringComparison.Ordinal);
        Assert.Contains("kept private", prepared.PrivacyWarning, StringComparison.Ordinal);
        Assert.Equal("search_library", Assert.Single(prepared.Request.Tools).Name);
        var kinds = Json(ServicePlanReader.Schema).GetProperty("properties").GetProperty("items").GetProperty("items").GetProperty("properties").GetProperty("kind").GetProperty("enum");
        Assert.Equal(["header", "song", "scripture", "item"], kinds.EnumerateArray().Select(k => k.GetString()));

        // The reply names a song the library lacks, an unknown kind, and tries to slip lyrics in.
        var plan = ServicePlanReader.Read(Json("""
            {"name":"Sunday Morning","date":"2026-11-08","items":[
              {"kind":"header","title":"Worship","author":null,"reference":null,"minutes":null},
              {"kind":"song","title":"amazing grace","author":"John Newton","reference":null,"minutes":5},
              {"kind":"song","title":"Oceans","author":"Hillsong United","reference":null,"minutes":6,"lyrics":"You call me out upon the waters"},
              {"kind":"video","title":"Bumper","author":null,"reference":null,"minutes":1},
              {"kind":"scripture","title":"Gospel reading","author":null,"reference":"Jn 3:16-18","minutes":null},
              {"kind":"scripture","title":"A reading from somewhere","author":null,"reference":"Hezekiah 4:1","minutes":null},
              {"kind":"item","title":"Sermon","author":null,"reference":null,"minutes":30}
            ]}
            """), Songs);
        Assert.Equal(["Worship", "Amazing Grace", "Oceans", "John 3:16–18", "A reading from somewhere", "Sermon"], plan.Rows.Select(r => r.Title));
        Assert.Equal(["Oceans"], plan.MissingSongs);

        var service = ServicePlanReader.Build(plan, new DateOnly(2026, 11, 1));
        Assert.Equal("2026-11-08", service.ServiceDate);
        // Only kinds a Mac already knows; the matched song plays the library's own deck.
        Assert.Equal(
            [ServiceItemKind.Header, ServiceItemKind.Presentation, ServiceItemKind.Info, ServiceItemKind.Info, ServiceItemKind.Info, ServiceItemKind.Info],
            service.Items.Select(i => i.ItemKind));
        Assert.Equal("s-grace", service.Items[1].RefId);
        Assert.Equal(300, service.Items[1].Duration);
        Assert.Equal(("", "Oceans", "Hillsong United"), (service.Items[2].RefId, service.Items[2].MxuSongTitle, service.Items[2].MxuSongAuthor));
        Assert.DoesNotContain("waters", JsonSerializer.Serialize(service), StringComparison.Ordinal);
    }

    // MARK: The library look-ups

    [Fact]
    public void TheLibraryLookUpsAnswerFromTheSnapshot()
    {
        var search = Facts.Tools[0];
        var found = search.Run(new Dictionary<string, JsonElement> { ["query"] = Json("\"giving\"") });
        Assert.Equal("overlays | Giving | id ov-giving | Lower thirds", found);
        // A wrong kind is tried again without it.
        Assert.Contains("ov-giving", search.Run(new Dictionary<string, JsonElement> { ["query"] = Json("\"giving\""), ["kind"] = Json("\"media\"") }), StringComparison.Ordinal);
        Assert.Contains("Nothing in the library", search.Run(new Dictionary<string, JsonElement> { ["query"] = Json("\"oceans\"") }), StringComparison.Ordinal);
        Assert.Contains("row-2", Facts.Tools[1].Run(new Dictionary<string, JsonElement>()), StringComparison.Ordinal);
        Assert.Equal("themes | Sunday Blue | id th-1", Facts.Tools[2].Run(new Dictionary<string, JsonElement>()));
    }

    // MARK: Typed requests

    private static List<ActionRoute> Routes(List<string> ran) =>
    [
        new("fireOverlay", "Go live with an overlay.", ["id"], null, (path, _) => { ran.Add($"fireOverlay {path["id"]}"); return Task.CompletedTask; }),
        new("advance", "Advance.", [], new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["steps"] = new JsonObject { ["type"] = "integer" } } },
            (_, body) => { ran.Add($"advance {System.Text.Encoding.UTF8.GetString(body)}"); return Task.CompletedTask; }),
        new("fireSlide", "Fire a slide.", [], new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["serviceItemId"] = new JsonObject { ["type"] = "string" } } },
            (_, body) => { ran.Add($"fireSlide {System.Text.Encoding.UTF8.GetString(body)}"); return Task.CompletedTask; }),
    ];

    [Fact]
    public async Task ATypedRequestBecomesAReadyActionAndRunsOnlyWhenTheOperatorSaysSo()
    {
        var ran = new List<string>();
        var (request, held) = TypedRequests.Prepare("put up the giving lower third", Routes(ran), Facts);
        server.ThenToolUse("search_library", """{"query":"giving","kind":"overlays"}""")
            .ThenToolUse("fireOverlay", """{"id":"ov-giving"}""")
            .ThenText("""{"summary":"Show the lower third “Giving”."}""");
        var reply = await client.Ask(Key, request, TestContext.Current.CancellationToken);
        var outcome = TypedRequests.Read(reply.Json, held);

        // The tools offered are the look-ups and one per route, named as the Local API names them.
        var sent = server.Requests[0].Json.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Equal(["search_library", "read_current_service", "fireOverlay", "advance", "fireSlide"], sent);
        // The model was told the action is held, and nothing has run.
        Assert.Contains("Held ready. Nothing has happened yet", server.Requests[2].Body, StringComparison.Ordinal);
        Assert.Empty(ran);
        Assert.NotNull(outcome.Action);
        Assert.Equal("Show the lower third “Giving”.", outcome.Action.Summary);
        Assert.Equal("fireOverlay id=ov-giving", outcome.Action.Detail);

        await outcome.Action.Run(); // the operator's Enter
        Assert.Equal(["fireOverlay ov-giving"], ran);
    }

    [Fact]
    public async Task AnInventedIdIsRefusedAndASecondActionIsNotHeld()
    {
        var ran = new List<string>();
        var (request, held) = TypedRequests.Prepare("show the offering slide then go next", Routes(ran), Facts);
        server.ThenToolUse("fireOverlay", """{"id":"ov-offering"}""")
            .ThenToolUse("fireSlide", """{"serviceItemId":"row-99"}""")
            .ThenToolUse("advance", """{"steps":1}""")
            .ThenToolUse("fireOverlay", """{"id":"ov-welcome"}""")
            .ThenText("""{"summary":"Go to the next slide."}""");
        var reply = await client.Ask(Key, request, TestContext.Current.CancellationToken);
        var outcome = TypedRequests.Read(reply.Json, held);

        Assert.Contains("an id in this library", server.Requests[1].Body, StringComparison.Ordinal);
        Assert.Contains("row-99", server.Requests[2].Body, StringComparison.Ordinal);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(server.Requests[2].Body, "an id in this library"));
        Assert.Contains("Another action is already held", server.Requests[4].Body, StringComparison.Ordinal);
        Assert.Equal("advance {\"steps\":1}", outcome.Action?.Detail);
        Assert.Empty(ran);
    }

    [Fact]
    public async Task ARequestTheAppCannotDoComesBackAsWordsWithNoAction()
    {
        var (request, held) = TypedRequests.Prepare("order pizza", Routes([]), Facts);
        server.ThenText("""{"summary":"I can't do that from here."}""");
        var reply = await client.Ask(Key, request, TestContext.Current.CancellationToken);
        var outcome = TypedRequests.Read(reply.Json, held);
        Assert.Null(outcome.Action);
        Assert.Equal("I can't do that from here.", outcome.Words);
    }

    // MARK: The service report

    private static readonly DateTimeOffset Ten = new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);

    private static ServiceRunEntry Fire(double minutes, string? id, string? name, int? planned, string? section = null) =>
        new("run-1", "svc", "Sunday Morning", Ten.AddMinutes(minutes), id, name, section, planned);

    [Fact]
    public void TheServiceReportSaysWhatRanWhenAndWhatWentOverWithNoAi()
    {
        List<ServiceRunEntry> run =
        [
            Fire(0, "a", "Welcome", 120, "Worship"),
            Fire(3, "b", "Amazing Grace", 300, "Worship"),
            Fire(8.25, "c", "Sermon", 1800, "The Word"),
            Fire(45.25, null, null, null),
        ];
        var report = ServiceReport.Build(run, ["Welcome", "Amazing Grace", "Announcements", "Sermon"])!;
        Assert.Equal([TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(5.25), TimeSpan.FromMinutes(37)], report.Lines.Select(l => l.Ran));
        Assert.Equal([TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(7)], report.Lines.Select(l => l.Over));
        // Fifteen seconds over isn't "over".
        Assert.Equal(["Sermon", "Welcome"], report.WentOver.Select(l => l.Name));
        Assert.Equal(["Announcements"], report.NeverFired);
        Assert.Equal(TimeSpan.FromMinutes(45.25), report.Ran);

        var text = ServiceReport.Text(report);
        Assert.Contains("Ran 45:15 against 37:00 planned", text, StringComparison.Ordinal);
        Assert.Contains("Sermon  ·  ran 37:00, planned 30:00  ▲ over by 7:00", text, StringComparison.Ordinal);
        Assert.Contains("Went over: Sermon by 7:00; Welcome by 1:00.", text, StringComparison.Ordinal);
        Assert.Contains("Never shown: Announcements.", text, StringComparison.Ordinal);
        Assert.Contains("THE WORD", text, StringComparison.Ordinal);

        // Still running, with no end recorded: the last row runs to "now"; with no "now" it has no length.
        var live = ServiceReport.Build(run[..3], [], Ten.AddMinutes(20))!;
        Assert.Equal(TimeSpan.FromMinutes(11.75), live.Lines[^1].Ran);
        Assert.Null(ServiceReport.Build(run[..3], [])!.Lines[^1].Ran);
        Assert.Null(ServiceReport.Build([], []));
    }

    [Fact]
    public void TheRunningRecordIsKeptOnDiskAndReadBackBySitting()
    {
        var record = new ServiceRunLog(Path.Combine(folder, "runs.jsonl"));
        record.Add(Fire(0, "a", "Welcome", 120));
        record.Add(Fire(3, "b", "Amazing Grace", null));
        record.Add(Fire(600, "a", "Welcome", 120) with { Run = "run-2" });
        File.AppendAllText(record.FilePath, "{\"run\":\"half a li"); // a power cut mid-line
        var runs = new ServiceRunLog(record.FilePath).Runs();
        Assert.Equal(["run-2", "run-1"], runs.Select(r => r[0].Run));
        Assert.Equal(["Welcome", "Amazing Grace"], runs[1].Select(e => e.Name));
    }

    [Fact]
    public void NotesForNextWeekAreOptionalAndOnlyTheReportIsSent()
    {
        var report = ServiceReport.Build([Fire(0, "a", "Welcome", 120), Fire(3, null, null, null)], [])!;
        var request = ServiceReport.NotesRequest(report);
        Assert.Equal(ServiceReport.Text(report), request.Ask);
        Assert.Equal(["Plan 3 minutes for the welcome."], ServiceReport.ReadNotes(Json("""{"notes":["Plan 3 minutes for the welcome.",""," "]}""")));
        Assert.Empty(ServiceReport.ReadNotes(Json("""{"notes":"none"}""")));
    }

    // MARK: Slide checks

    private static readonly SceneSize Hd = SceneSize.Hd;

    private static SlideChecks.TextBox Box(string text = "Welcome", SceneRect? frame = null, SceneColor? color = null, SceneColor? behind = null,
        double spread = 0, bool lifted = false, bool overflows = false, bool autoShrink = false, bool ownFrame = true, bool ownStyle = true, double font = 96) =>
        new("o1", text, frame ?? new SceneRect(160, 340, 1600, 400), color ?? SceneColor.White, font, behind ?? SceneColor.Black, spread, lifted, overflows, autoShrink, ownFrame, ownStyle);

    [Fact]
    public void ContrastIsTheWebStandardRatio()
    {
        Assert.Equal(21, SlideChecks.ContrastRatio(SceneColor.White, SceneColor.Black), 3);
        Assert.Equal(1, SlideChecks.ContrastRatio(SceneColor.White, SceneColor.White), 3);
        // Mid grey (#777) on white is the well-known 4.48.
        Assert.Equal(4.48, SlideChecks.ContrastRatio(new SceneColor(0x77 / 255.0, 0x77 / 255.0, 0x77 / 255.0), SceneColor.White), 2);
    }

    [Fact]
    public void AGoodSlidePassesAndEachProblemIsFoundWithAFixOnlyWhereOneIsSafe()
    {
        Assert.Empty(SlideChecks.Check("s", 1, Hd, [Box()]));

        var yellowOnWhite = SlideChecks.Check("s", 1, Hd, [Box(color: new SceneColor(1, 1, 0.4), behind: SceneColor.White)]);
        Assert.Equal((SlideChecks.Kind.LowContrast, null), (Assert.Single(yellowOnWhite).Kind, yellowOnWhite[0].Fix));
        // A shadow makes up for middling contrast, but not for none at all.
        var grey = new SceneColor(0.62, 0.62, 0.62);
        Assert.Single(SlideChecks.Check("s", 1, Hd, [Box(behind: grey)]));
        Assert.Empty(SlideChecks.Check("s", 1, Hd, [Box(behind: grey, lifted: true)]));
        Assert.Single(SlideChecks.Check("s", 1, Hd, [Box(behind: new SceneColor(0.97, 0.97, 0.97), lifted: true)]));

        Assert.Equal(SlideChecks.Kind.BusyBackground, Assert.Single(SlideChecks.Check("s", 1, Hd, [Box(spread: 0.3)])).Kind);
        Assert.Empty(SlideChecks.Check("s", 1, Hd, [Box(spread: 0.3, lifted: true)]));

        var wordy = string.Join(' ', Enumerable.Repeat("word", 60));
        Assert.Contains("60 words", Assert.Single(SlideChecks.Check("s", 1, Hd, [Box(wordy)])).Message, StringComparison.Ordinal);

        // Cut off: shrink-to-fit is offered only when the box has its own style and isn't shrinking already.
        Assert.Equal(SlideChecks.Fix.ShrinkToFit, Assert.Single(SlideChecks.Check("s", 1, Hd, [Box(overflows: true)])).Fix);
        Assert.Null(Assert.Single(SlideChecks.Check("s", 1, Hd, [Box(overflows: true, ownStyle: false)])).Fix);
        Assert.Null(Assert.Single(SlideChecks.Check("s", 1, Hd, [Box(overflows: true, autoShrink: true)])).Fix);

        // A lower third hanging off the bottom: moved back only when the place is the slide's own.
        var low = new SceneRect(100, 900, 1900, 240);
        var off = Assert.Single(SlideChecks.Check("s", 3, Hd, [Box(frame: low)]));
        Assert.Equal((SlideChecks.Kind.OffTheSlide, SlideChecks.Fix.MoveOntoSlide, 3), (off.Kind, off.Fix, off.SlideNumber));
        Assert.StartsWith("The lower-third text", off.Message, StringComparison.Ordinal);
        Assert.Null(Assert.Single(SlideChecks.Check("s", 3, Hd, [Box(frame: low, ownFrame: false)])).Fix);
        Assert.Equal(new SceneRect(20, 840, 1900, 240), SlideChecks.OntoSlide(low, Hd));
        Assert.Equal(new SceneRect(0, 0, 1920, 1080), SlideChecks.OntoSlide(new SceneRect(-50, -50, 2400, 1300), Hd));

        Assert.Equal(SlideChecks.Kind.SmallText, Assert.Single(SlideChecks.Check("s", 1, Hd, [Box(font: 24)])).Kind);
        Assert.Empty(SlideChecks.Check("s", 1, Hd, [Box("   ", overflows: true)]));
    }

    [Fact]
    public void ThePictureBehindTheWordsIsSampledForColourAndBusyness()
    {
        // BGRA: a flat dark blue, then black and white stripes.
        var flat = Enumerable.Repeat(new byte[] { 128, 0, 0, 255 }, 400).SelectMany(p => p).ToArray();
        var (average, spread) = SlideChecks.Sample(flat);
        Assert.Equal((0, 0), (average.Red, average.Green));
        Assert.Equal(128 / 255.0, average.Blue, 6);
        Assert.Equal(0, spread, 6);
        var stripes = Enumerable.Range(0, 400).SelectMany(i => i % 2 == 0 ? new byte[] { 0, 0, 0, 255 } : new byte[] { 255, 255, 255, 255 }).ToArray();
        Assert.True(SlideChecks.Sample(stripes, step: 1).Spread > SlideChecks.BusySpread);
    }

    [Fact]
    public async Task TheSecondLookSendsPicturesAndItsNotesAreKeptOnlyForSlidesThatWereSent()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];
        server.ThenText("""{"notes":[{"slide":2,"note":"White words on a pale sky: add a shadow."},{"slide":9,"note":"not a slide that was sent"}]}""");
        var reply = await client.Ask(Key, SlideChecks.VisionRequest([png, png], [2, 5]), TestContext.Current.CancellationToken);
        var content = server.Requests[0].Json.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(["image", "image", "text"], content.EnumerateArray().Select(b => b.GetProperty("type").GetString()));
        Assert.Equal(("base64", "image/png", Convert.ToBase64String(png)),
            (content[0].GetProperty("source").GetProperty("type").GetString(), content[0].GetProperty("source").GetProperty("media_type").GetString(), content[0].GetProperty("source").GetProperty("data").GetString()));
        Assert.Equal([new SlideChecks.VisionNote(2, "White words on a pale sky: add a shadow.")], SlideChecks.ReadVision(reply.Json, [2, 5]));
        // The log says pictures went, and doesn't hold them.
        var entry = Assert.Single(log.Entries);
        Assert.Contains("2 picture(s) of slides", entry.Sent, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(png), File.ReadAllText(log.FilePath), StringComparison.Ordinal);
    }

    // MARK: Translation

    private static SlideObject Words(string id, string text) => new() { Id = id, ObjectKind = SlideObjectKind.Text, Name = "Text", Text = text };

    private static Presentation Announcements() => new()
    {
        Id = "deck", Name = "Announcements", PresentationKind = PresentationKind.Deck, ThemeId = "theme",
        Slides =
        [
            new Slide { Id = "a", Name = "", Objects = [Words("a1", "Welcome!"), Words("a2", "Coffee after the service\nin the hall")] },
            new Slide { Id = "b", Name = "", Objects = [Words("b1", "For God so loved the world, that he gave his only begotten Son\nJohn 3:16 (KJV)")] },
            new Slide { Id = "c", Name = "", Notes = "Read slowly", Objects = [Words("c1", "Pray for Mary Okafor's surgery"), Words("c2", "Youth night: Friday 7pm")] },
        ],
    };

    [Fact]
    public void TranslationMakesAMarkedCopyAndNeverSendsOrChangesScripture()
    {
        var deck = Announcements();
        var prepared = SlideTranslator.Prepare(deck, "Spanish");
        Assert.NotNull(prepared.Request);
        Assert.Equal([2], prepared.ScriptureSlides);
        Assert.DoesNotContain("God so loved", prepared.Request.Ask, StringComparison.Ordinal);
        Assert.DoesNotContain("Okafor", prepared.Request.Ask, StringComparison.Ordinal);
        Assert.Equal(["a1", "a2", "c2"], prepared.Pieces.Select(p => p.ObjectId));
        Assert.Contains("[2]\nCoffee after the service\nin the hall", prepared.Request.Ask, StringComparison.Ordinal);

        // The reply answers numbers that were never sent (3 is the prayer request, which stayed
        // here; 77 is nothing): both are ignored.
        var reply = Json("""
            {"translations":[
              {"n":1,"text":"¡Bienvenidos!"},
              {"n":2,"text":"Café después del servicio\nen el salón"},
              {"n":3,"text":"Ore por la cirugía"},
              {"n":4,"text":"Noche de jóvenes: viernes 7pm"},
              {"n":77,"text":"nada"}
            ]}
            """);
        var made = SlideTranslator.Apply(deck, prepared, reply, "Spanish", DateTimeOffset.Now);
        var copy = made.Copy;
        Assert.NotEqual(deck.Id, copy.Id);
        Assert.Equal("Announcements (Spanish · AI translation: check before use)", copy.Name);
        Assert.Equal(SlideTranslator.Folder, copy.Folder);
        Assert.Equal(["¡Bienvenidos!", "Café después del servicio\nen el salón"], copy.Slides[0].Objects.Select(o => o.Text));
        // Scripture is word for word as it was; the prayer request stays here untranslated.
        Assert.Equal(deck.Slides[1].Objects[0].Text, copy.Slides[1].Objects[0].Text);
        Assert.Equal(["Pray for Mary Okafor's surgery", "Noche de jóvenes: viernes 7pm"], copy.Slides[2].Objects.Select(o => o.Text));
        Assert.Equal(3, made.Pieces);
        Assert.Contains("AI translation (Spanish): check before use. Original: Welcome!", copy.Slides[0].Notes, StringComparison.Ordinal);
        Assert.StartsWith("Read slowly\nAI translation (Spanish)", copy.Slides[2].Notes, StringComparison.Ordinal);
        Assert.Null(copy.Slides[1].Notes);
        Assert.Contains(made.Notes, n => n.StartsWith("Slide 2: scripture, left as it is.", StringComparison.Ordinal));
        Assert.Contains(made.Notes, n => n.Contains("kept private", StringComparison.Ordinal));
        // The original is untouched.
        Assert.Equal("Welcome!", deck.Slides[0].Objects[0].Text);
        Assert.Null(deck.Slides[0].Notes);

        // A piece that doesn't come back stays in the original and is listed.
        var partial = SlideTranslator.Apply(deck, prepared, Json("""{"translations":[{"n":1,"text":"¡Bienvenidos!"}]}"""), "Spanish", DateTimeOffset.Now);
        Assert.Equal("Coffee after the service\nin the hall", partial.Copy.Slides[0].Objects[1].Text);
        Assert.Contains(partial.Notes, n => n.StartsWith("Not translated (left in the original): slides 1, 3.", StringComparison.Ordinal));
    }

    [Fact]
    public void SongsAndScriptureOnlyDecksAreRefusedBeforeAnythingIsSent()
    {
        Assert.Null(SlideTranslator.Prepare(Songs[0], "French").Request);
        Assert.Contains("licensed", SlideTranslator.Prepare(Songs[0], "French").Refused, StringComparison.Ordinal);
        var verses = Announcements();
        verses.Slides.RemoveAt(2);
        verses.Slides.RemoveAt(0);
        var prepared = SlideTranslator.Prepare(verses, "French");
        Assert.Null(prepared.Request);
        Assert.Contains("never machine-translated", prepared.Refused, StringComparison.Ordinal);
    }

    // MARK: Captions

    [Fact]
    public void CaptionsRollTheNewestWordsAndAPrayerNameIsNeverSentForTranslation()
    {
        var roll = new CaptionRoll(mostLetters: 40);
        Assert.Equal("Good morning church", roll.Add("  good   morning church "));
        Assert.Equal("Good morning church Turn with me", roll.Add("turn with me"));
        var third = roll.Add("to the book of Romans");
        Assert.True(third.Length <= 40);
        Assert.Equal("Turn with me To the book of Romans", third);
        Assert.Equal(third, roll.Add("   "));

        Assert.True(Captions.MaySend("and that is why grace matters"));
        Assert.False(Captions.MaySend("please pray for Mary Okafor this week"));
        var request = Captions.TranslateRequest("grace is a gift", "French");
        Assert.Equal(("grace is a gift", AssistantEffort.Low), (request.Ask, request.Effort));
        Assert.Equal("La grâce est un don.", Captions.ReadTranslation(Json("""{"text":" La grâce est un don. "}"""), "grace is a gift"));
        Assert.Null(Captions.ReadTranslation(Json("""{"text":""}"""), "grace is a gift"));
    }

    // MARK: The sermon summary

    private static readonly List<SermonSummary.Heard> Transcript =
    [
        new(TimeSpan.FromSeconds(4), "good morning church turn with me to romans chapter eight"),
        new(TimeSpan.FromSeconds(65), "grace is not something you earn it is something you receive"),
        new(TimeSpan.FromSeconds(140), "please pray for Mary Okafor this week"),
        new(TimeSpan.FromSeconds(200), "so this week forgive someone"),
        new(TimeSpan.FromSeconds(215), "before they ask you to"),
    ];

    [Fact]
    public void TheSermonSummaryKeepsOnlyQuotesFoundWordForWordAndTakesTimesFromTheTranscript()
    {
        var prepared = SermonSummary.Prepare(Transcript);
        Assert.Contains("[2] (1:05) grace is not something you earn", prepared.Request.Ask, StringComparison.Ordinal);
        Assert.DoesNotContain("Okafor", prepared.Request.Ask, StringComparison.Ordinal);
        Assert.NotNull(prepared.PrivacyWarning);

        var summary = SermonSummary.Read(Json("""
            {"summary":"Grace is received, not earned.",
             "quotes":[
               {"line":2,"text":"Grace is not something you earn; it is something you receive."},
               {"line":4,"text":"So this week, forgive someone before they ask you to."},
               {"line":2,"text":"Grace is never something you earn."},
               {"line":1,"text":"Grace is not something you earn"},
               {"line":40,"text":"so this week forgive someone"}
             ],
             "posts":["Grace is a gift. (Romans 8)","","Forgive first."]}
            """), Transcript);
        Assert.Equal("Grace is received, not earned.", summary.Text);
        // Punctuation may be added; a changed word, or a line that doesn't hold the words, drops the quote.
        Assert.Equal(
            [(TimeSpan.FromSeconds(65), "Grace is not something you earn; it is something you receive."),
             (TimeSpan.FromSeconds(200), "So this week, forgive someone before they ask you to."),
             (TimeSpan.FromSeconds(65), "Grace is not something you earn")],
            summary.Quotes.Select(q => (q.At, q.Text)));
        Assert.Equal(2, summary.QuotesDropped);
        Assert.Equal(["Grace is a gift. (Romans 8)", "Forgive first."], summary.Posts);
        Assert.Contains("  1:05  “Grace is not something you earn; it is something you receive.”", SermonSummary.Text(summary), StringComparison.Ordinal);
        Assert.Contains("   3:20  so this week forgive someone", SermonSummary.TranscriptText(Transcript), StringComparison.Ordinal);
    }
}
