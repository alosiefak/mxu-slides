using System.Diagnostics;
using System.Speech.Synthesis;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MxU.Slides.Assistant;
using MxU.Slides.Assistant.Spotting;
using MxU.Slides.Assistant.Testing;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.Views.Assistant;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>
/// Phase 12, stage 2, through the real Assistant window (part of the Assistant section; it runs
/// with stage 1's key and stand-in server). As there, every "answer from Anthropic" is scripted
/// here, some of them wrong on purpose, and no real API call is made.
/// <list type="bullet">
/// <item>A typed request becomes a ready action; nothing happens until Enter; Esc forgets it; an invented id is refused.</item>
/// <item>A bulletin becomes a service plan with and without AI; a song the library lacks is listed, not made.</item>
/// <item>The service report is made from the fires just done, with no AI; notes are an extra.</item>
/// <item>Slide checks find weak contrast, a box off the slide and cut-off words with no AI, and fix the two that are safe to fix.</item>
/// <item>Translation saves a marked copy and never sends or changes the scripture slide.</item>
/// <item>Captions (Windows' own voice, heard by Windows' own recognizer) go up as a live alert, as heard and translated.</item>
/// <item>A recording is transcribed on this PC; a quote that isn't in the transcript is thrown away.</item>
/// </list>
/// Pictures 151–163.
/// </summary>
public sealed partial class SelfTest
{
    private const string AssistantBulletin =
        "Sunday Morning Worship, 8 November 2026\n"
        + "WORSHIP\n"
        + "10:00 Welcome\n"
        + "10:05 Amazing Grace\n"
        + "10:10 Song: Oceans\n"
        + "The Word:\n"
        + "Reading: John 3:16-18\n"
        + "Sermon - 30 min\n"
        + "Prayer requests:\n"
        + "Mary Okafor, surgery on Tuesday";

    private async Task AssistantStageTwo(FakeAnthropicServer server, List<Window> windows)
    {
        var serviceBefore = model.SelectedService?.Id;
        var made = new List<string>();
        Service? added = null;
        string? wav = null;
        var panel = AssistantWindow.Open(window, model);
        windows.Add(panel);
        try
        {
            await Delay(500);
            await AssistantTypedRequests(server, panel);
            added = await AssistantReadsABulletin(server, panel, serviceBefore);
            await AssistantServiceReport(server, panel);
            await AssistantChecksAndTranslates(server, panel, made);
            wav = await AssistantCaptionsAndSermon(server, panel);
        }
        finally
        {
            model.StopCaptions();
            server.ClearScript();
            model.Settings.CaptionsLanguage = null;
            model.Settings.CaptionsTarget = null;
            panel.Close();
            model.ClearServiceTrackingCommand.Execute(null);
            model.ClearAllCommand.Execute(null);
            foreach (var id in made) model.Library.Delete<Presentation>(id);
            if (added is not null) model.Library.Delete<Service>(added.Id);
            model.RefreshServices(keepSelection: true);
            model.SelectedService = model.Services.FirstOrDefault(s => s.Id == serviceBefore);
            model.RefreshLibrary();
            if (wav is not null) File.Delete(wav);
        }
    }

    // MARK: Typed requests

    private async Task AssistantTypedRequests(FakeAnthropicServer server, AssistantWindow panel)
    {
        model.ClearAllCommand.Execute(null);
        var row = model.ServiceItems.FirstOrDefault(r => r.Name == "Amazing Grace");
        if (row is null)
        {
            Skip("typed requests", "the self-test service has no “Amazing Grace” row");
            return;
        }
        panel.Go(AssistantWindow.Ask);
        await Delay(300);
        var before = server.Requests.Count;
        // The "model" reads the service with the app's tool, then calls the Local API's fireSlide.
        server.ThenToolUse("read_current_service", "{}")
            .ThenToolUse("fireSlide", JsonSerializer.Serialize(new { serviceItemId = row.Item.Id }))
            .ThenText("""{"summary":"Go to the song “Amazing Grace”."}""", inputTokens: 2100, outputTokens: 40);
        panel.AskBox.Text = "next song";
        Press(panel, Key.Enter);
        await Until(() => panel.Ready, ready => ready is not null, 15000);
        var asked = server.Requests.Skip(before).ToList();
        var tools = asked.Count == 0 ? [] : asked[0].Json.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToList();
        Check($"a typed request offers the model the Local API's control routes as tools ({tools.Count} tools)",
            tools.Take(2).SequenceEqual(["search_library", "read_current_service"])
            && new[] { "fireSlide", "advance", "clearAll", "fireOverlay", "fireMedia", "fireAlert" }.All(tools.Contains)
            && !tools.Any(t => t is "createDocument" or "deleteDocument" or "updateDocument"));
        Check("the model reads the open service through the app's look-up (rows with their ids)",
            asked.Count == 3 && asked[1].Body.Contains(row.Item.Id, StringComparison.Ordinal) && asked[1].Body.Contains("Amazing Grace", StringComparison.Ordinal));
        Check("the request comes back as a READY action: its sentence, the route it will call, and nothing on screen yet",
            panel.Ready is { Route.Name: "fireSlide" } && model.Show.Live is null
            && Descendants<TextBlock>(panel).Any(t => t.Text == "Go to the song “Amazing Grace”.") && Descendants<TextBlock>(panel).Any(t => t.Text.Contains("fireSlide", StringComparison.Ordinal)));
        Save(panel, "151-typed-request-ready-not-done");

        await Delay(700);
        Press(panel, Key.Enter); // the operator's Enter
        var live = await Until(() => model.LiveTitle, title => title == "Amazing Grace", 5000);
        Check("Enter does it: the song is live, through the Local API's own handler", live == "Amazing Grace" && panel.Ready is null);
        await Delay(300);
        Save(panel, "152-typed-request-done-after-enter");

        // Esc forgets a ready action.
        model.ClearAllCommand.Execute(null);
        server.ThenToolUse("clearAll", "{}").ThenText("""{"summary":"Clear everything from the screens."}""");
        panel.AskBox.Text = "clear the screen";
        await panel.AskNow();
        var ready = panel.Ready is { Route.Name: "clearAll" };
        Press(panel, Key.Escape);
        Check("Esc forgets a ready action and nothing is done", ready && panel.Ready is null && Descendants<TextBlock>(panel).Any(t => t.Text.StartsWith("Cancelled", StringComparison.Ordinal)));

        // An id the model made up never reaches the app.
        var sent = server.Requests.Count;
        server.ThenToolUse("fireOverlay", """{"id":"overlay-that-does-not-exist"}""").ThenText("""{"summary":"There is no lower third called Giving in the library."}""");
        panel.AskBox.Text = "put up the giving lower third";
        await panel.AskNow();
        var refusal = server.Requests.Skip(sent).ElementAtOrDefault(1)?.Body ?? "";
        Check("an id the model invents is refused by the app, and no action is made ready",
            panel.Ready is null && refusal.Contains("an id in this library", StringComparison.Ordinal)
            && Descendants<TextBlock>(panel).Any(t => t.Text.StartsWith("There is no lower third", StringComparison.Ordinal)));
    }

    // MARK: Order of service

    private async Task<Service?> AssistantReadsABulletin(FakeAnthropicServer server, AssistantWindow panel, string? serviceBefore)
    {
        panel.Go(AssistantWindow.Plan);
        await Delay(300);
        var grace = model.Library.All<Presentation>().FirstOrDefault(p => p.Name == "Amazing Grace");
        var sent = server.Requests.Count;
        panel.PlanBox.Text = AssistantBulletin;
        panel.ReadPlanNow();
        await Delay(300);
        string[] expected = ["♪  Amazing Grace", "⚠  Oceans", "✝  John 3:16–18", "•  Sermon", "WORSHIP", "THE WORD"];
        var rows = Descendants<TextBlock>(panel).Select(t => string.Concat(t.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text))).ToList();
        Check("a pasted bulletin is read into rows with no AI: headers, a matched song, a missing song, a reading",
            server.Requests.Count == sent && expected.All(e => rows.Any(r => r.StartsWith(e, StringComparison.Ordinal)))
            && rows.Any(r => r.Contains("Sermon", StringComparison.Ordinal) && r.Contains("30 min", StringComparison.Ordinal))
            && Descendants<TextBlock>(panel).Any(t => t.Text.StartsWith("Songs to add or link: Oceans", StringComparison.Ordinal))
            && !rows.Any(r => r.Contains("Okafor", StringComparison.Ordinal)));
        Save(panel, "153-bulletin-read-without-ai");

        // The same text by the assistant, whose reply names a song the library doesn't have.
        server.ThenToolUse("search_library", """{"query":"Amazing Grace","kind":"presentations"}""").ThenText("""
            {"name":"Sunday Morning Worship","date":"2026-11-08","items":[
              {"kind":"header","title":"Worship","author":null,"reference":null,"minutes":null},
              {"kind":"item","title":"Welcome","author":null,"reference":null,"minutes":5},
              {"kind":"song","title":"Amazing Grace","author":null,"reference":null,"minutes":5},
              {"kind":"song","title":"Oceans","author":"Hillsong United","reference":null,"minutes":6},
              {"kind":"header","title":"The Word","author":null,"reference":null,"minutes":null},
              {"kind":"scripture","title":"Reading","author":null,"reference":"John 3:16-18","minutes":null},
              {"kind":"item","title":"Sermon","author":null,"reference":null,"minutes":30}
            ]}
            """, inputTokens: 1300, outputTokens: 240);
        await panel.ReadPlanWithAiNow();
        var asked = server.Requests.Skip(sent).ToList();
        Check("Read with AI sends the text without the prayer request, and the model can only search the library",
            asked.Count == 2 && asked.All(r => !r.Body.Contains("Okafor", StringComparison.Ordinal))
            && asked[0].Json.GetProperty("tools").GetArrayLength() == 1 && asked[1].Body.Contains(grace?.Id ?? "?", StringComparison.Ordinal)
            && Descendants<TextBlock>(panel).Any(t => t.Text.StartsWith("Read by the assistant", StringComparison.Ordinal) && t.Text.Contains("kept private", StringComparison.Ordinal) && t.Text.Contains("Nothing is saved until", StringComparison.Ordinal)));
        var services = model.Library.All<Service>().Count();
        Save(panel, "154-bulletin-read-by-ai");

        var added = panel.AddPlanNow();
        await Delay(400);
        var saved = added is null ? null : model.Library.Get<Service>(added.Id);
        Check("Add as a service saves an ordinary service: the library's own song, and the missing song as a plain row to link",
            saved is { Name: "Sunday Morning Worship", ServiceDate: "2026-11-08", Items.Count: 7 } && model.Library.All<Service>().Count() == services + 1
            && saved.Items[2] is { ItemKind: ServiceItemKind.Presentation, Duration: 300 } song && song.RefId == grace?.Id
            && saved.Items[3] is { ItemKind: ServiceItemKind.Info, RefId: "", MxuSongTitle: "Oceans" }
            && saved.Items[5] is { ItemKind: ServiceItemKind.Info, Name: "John 3:16–18" }
            && model.Library.All<Presentation>().All(p => p.Name != "Oceans") && model.SelectedService?.Id == saved.Id);
        // Back to the service the rest of the test runs on.
        model.SelectedService = model.Services.FirstOrDefault(s => s.Id == serviceBefore);
        await Delay(300);
        return added;
    }

    // MARK: The service report

    private async Task AssistantServiceReport(FakeAnthropicServer server, AssistantWindow panel)
    {
        // The typed request fired “Amazing Grace”. Fire one more row, then clear the timers: the end.
        var second = model.ServiceItems.FirstOrDefault(r => r.Item.ItemKind == ServiceItemKind.Presentation && r.Name != "Amazing Grace");
        var first = model.ServiceItems.FirstOrDefault(r => r.Name == "Amazing Grace");
        if (first is not null && second is not null)
        {
            model.SelectedServiceItem = first;
            await Until(() => model.CurrentPresentation?.Name, name => name == first.Name, 3000);
            if (model.Slides.Count > 0) model.GoLiveCommand.Execute(model.Slides[0]);
            await Delay(1200);
            model.SelectedServiceItem = second;
            await Until(() => model.CurrentPresentation?.Name, name => name == second.Name, 3000);
            if (model.Slides.Count > 0) model.GoLiveCommand.Execute(model.Slides[0]);
            await Delay(1200);
        }
        model.ClearServiceTrackingCommand.Execute(null);
        model.ClearAllCommand.Execute(null);

        var sent = server.Requests.Count;
        panel.Go(AssistantWindow.Report);
        await Delay(400);
        var text = panel.ReportText;
        Check("the service report lists what ran and for how long, from this PC's own fires, with no AI",
            server.Requests.Count == sent && text.Contains("service report", StringComparison.Ordinal) && text.Contains("Amazing Grace  ·  ran 0:0", StringComparison.Ordinal)
            && (second is null || text.Contains($"{second.Name}  ·  ran 0:0", StringComparison.Ordinal)) && text.Contains("ended", StringComparison.Ordinal)
            && File.Exists(model.ServiceRuns.FilePath) && !model.ServiceRuns.FilePath.StartsWith(model.Library.Root, StringComparison.OrdinalIgnoreCase));
        server.ThenText("""{"notes":["Nothing had a planned length: add lengths in the service so next week's report can show what ran over."]}""", inputTokens: 420, outputTokens: 40);
        await panel.ReportNotesNow();
        var asked = server.Requests.Skip(sent).ToList();
        Check("notes for next week are an extra: only the report's text is sent, and the notes are shown as the assistant's",
            asked.Count == 1 && asked[0].Json.GetProperty("messages")[0].GetProperty("content").ToString().StartsWith(text.Split('\n')[0], StringComparison.Ordinal)
            && Descendants<TextBlock>(panel).Any(t => t.Text.StartsWith("• Nothing had a planned length", StringComparison.Ordinal))
            && Descendants<TextBlock>(panel).Any(t => t.Text.StartsWith("Notes for next week, the assistant's suggestions", StringComparison.Ordinal)));
        Save(panel, "155-service-report-and-notes");
    }

    // MARK: Slide checks and translation

    private static Slide CheckSlide(string id, string backgroundHex, SlideObject box) => new()
    {
        Id = id, Name = "", Unthemed = true, BackgroundFill = new ObjectFill { FillKind = FillKind.Solid, ColorHex = backgroundHex }, Objects = [box],
    };

    private static SlideObject CheckBox(string id, string text, double x, double y, double width, double height, string colorHex, double size = 80) => new()
    {
        Id = id, ObjectKind = SlideObjectKind.Text, Name = "Text", Text = text, X = x, Y = y, Width = width, Height = height,
        TextStyle = new TextStyle { FontSize = size, ColorHex = colorHex, HorizontalAlignment = TextHorizontalAlignment.Center, VerticalAlignment = TextVerticalAlignment.Middle },
    };

    private async Task AssistantChecksAndTranslates(FakeAnthropicServer server, AssistantWindow panel, List<string> made)
    {
        const string verse = "For God so loved the world, that he gave his only begotten Son\nJohn 3:16 (KJV)";
        var deck = new Presentation
        {
            Id = "selftest-assistant-deck", Name = "Self-test announcements", PresentationKind = PresentationKind.Deck,
            ThemeId = model.Library.All<Presentation>().FirstOrDefault()?.ThemeId ?? "",
            Slides =
            [
                // Pale yellow on white, in a lower-third box that hangs off the right and the bottom.
                CheckSlide("st-low", "#FFFFFFFF", CheckBox("st-low-text", "Join us for coffee after the service", 160, 860, 1900, 300, "#FFF3B0FF")),
                // Too many big words for a small box, with shrink-to-fit off.
                CheckSlide("st-cut", "#102040FF", CheckBox("st-cut-text", "Youth night is this Friday at seven in the main hall, bring a friend", 660, 400, 600, 240, "#FFFFFFFF", size: 110)),
                CheckSlide("st-good", "#102040FF", CheckBox("st-good-text", "Welcome!", 160, 340, 1600, 400, "#FFFFFFFF", size: 160)),
                CheckSlide("st-verse", "#102040FF", CheckBox("st-verse-text", verse, 160, 240, 1600, 600, "#FFFFFFFF", size: 64)),
            ],
        };
        model.Library.Save(deck);
        made.Add(deck.Id);
        model.Open(deck, null, null);
        await Until(() => model.CurrentPresentation?.Id, id => id == deck.Id, 3000);

        panel.Go(AssistantWindow.Checks);
        await Delay(300);
        var sent = server.Requests.Count;
        await panel.CheckNow();
        bool Found(SlideChecks.Kind kind, int slide) => panel.Findings.Any(f => f.Kind == kind && f.SlideNumber == slide);
        Check($"slide checks find weak contrast, a box off the slide and cut-off words, with no AI ({string.Join(", ", panel.Findings.Select(f => $"{f.SlideNumber}:{f.Kind}"))})",
            server.Requests.Count == sent && Found(SlideChecks.Kind.LowContrast, 1) && Found(SlideChecks.Kind.OffTheSlide, 1) && Found(SlideChecks.Kind.CutOff, 2)
            && !panel.Findings.Any(f => f.SlideNumber is 3 or 4));
        Check("a Fix is offered only where one is safe: moving the box and shrink-to-fit, never the colour",
            panel.Findings.All(f => f.Kind switch
            {
                SlideChecks.Kind.OffTheSlide => f.Fix == SlideChecks.Fix.MoveOntoSlide,
                SlideChecks.Kind.CutOff => f.Fix == SlideChecks.Fix.ShrinkToFit,
                _ => f.Fix is null,
            }) && Descendants<Button>(panel).Count(b => b.Content as string == "Fix") == 2);
        Save(panel, "156-slide-checks-found");
        await Snapshot("157-the-slide-the-checks-flagged", model.SceneOf(deck, deck.Slides[0]));

        panel.FixNow(panel.Findings.ToList().FindIndex(f => f.Kind == SlideChecks.Kind.OffTheSlide));
        await Until(() => panel.Findings.Any(f => f.Kind == SlideChecks.Kind.OffTheSlide), still => !still, 5000);
        panel.FixNow(panel.Findings.ToList().FindIndex(f => f.Kind == SlideChecks.Kind.CutOff));
        await Until(() => panel.Findings.Any(f => f.Kind == SlideChecks.Kind.CutOff), still => !still, 5000);
        var now = model.Library.Get<Presentation>(deck.Id)!;
        var moved = now.Slides[0].Objects[0];
        Check("Fix moves the box back onto the slide and turns on shrink-to-fit; the colour finding stays for a person to judge",
            moved is { X: >= 0, Y: >= 0 } && moved.X + moved.Width <= 1920 && moved.Y + moved.Height <= 1080 && moved.Width == 1900
            && now.Slides[1].Objects[0].TextStyle?.AutoShrink == true
            && panel.Findings.Count == 1 && panel.Findings[0].Kind == SlideChecks.Kind.LowContrast);

        // The second look: pictures go to the "assistant", its notes come back as an opinion.
        server.ThenText("""{"notes":[{"slide":1,"note":"Pale yellow on white washes out on a projector: use a dark colour for the words."},{"slide":9,"note":"no such slide"}]}""", inputTokens: 2300, outputTokens: 60);
        await panel.LookNow();
        var look = server.Requests.Skip(sent).LastOrDefault();
        var blocks = look?.Json.GetProperty("messages")[0].GetProperty("content");
        Check("the second look sends pictures of the slides, and shows the assistant's notes as an opinion that changes nothing",
            blocks is { ValueKind: JsonValueKind.Array } content && content.EnumerateArray().Count(b => b.GetProperty("type").GetString() == "image") == 4
            && Descendants<TextBlock>(panel).Any(t => t.Text.StartsWith("Pale yellow on white", StringComparison.Ordinal))
            && !Descendants<TextBlock>(panel).Any(t => t.Text == "no such slide")
            && Descendants<TextBlock>(panel).Any(t => t.Text.Contains("nothing was changed", StringComparison.Ordinal))
            && !File.ReadAllText(model.Assistant.Log.FilePath).Contains("iVBOR", StringComparison.Ordinal));
        Save(panel, "158-slide-checks-fixed-and-second-look");

        // Translation of the same deck.
        model.Settings.TranslateLanguage = "Spanish";
        panel.Go(AssistantWindow.Translate);
        await Delay(300);
        sent = server.Requests.Count;
        server.ThenText("""
            {"translations":[
              {"n":1,"text":"Acompáñenos a tomar café después del servicio"},
              {"n":2,"text":"La noche de jóvenes es este viernes a las siete en el salón principal, trae a un amigo"},
              {"n":3,"text":"¡Bienvenidos!"},
              {"n":4,"text":"Porque de tal manera amó Dios al mundo"}
            ]}
            """, inputTokens: 900, outputTokens: 120);
        await panel.TranslateNow();
        var copy = model.Library.All<Presentation>().FirstOrDefault(p => p.Name.StartsWith("Self-test announcements (Spanish", StringComparison.Ordinal));
        if (copy is not null) made.Add(copy.Id);
        var request = server.Requests.Skip(sent).FirstOrDefault()?.Body ?? "";
        Check("translation never sends the scripture slide, and says so before the button is pressed",
            request.Contains("coffee after the service", StringComparison.Ordinal) && !request.Contains("God so loved", StringComparison.Ordinal) && !request.Contains("John 3:16", StringComparison.Ordinal)
            && Descendants<TextBlock>(panel).Any(t => t.Text.Contains("Scripture (slide 4) is not sent", StringComparison.Ordinal)));
        Check("the copy is saved, marked as an AI translation, with the scripture word for word and the original deck untouched",
            copy is { Folder: SlideTranslator.Folder } && copy.Id != deck.Id && copy.Name.Contains("AI translation: check before use", StringComparison.Ordinal)
            && copy.Slides[2].Objects[0].Text == "¡Bienvenidos!" && copy.Slides[3].Objects[0].Text == verse
            && copy.Slides[2].Notes?.Contains("Original: Welcome!", StringComparison.Ordinal) == true
            && model.Library.Get<Presentation>(deck.Id)!.Slides[2].Objects[0].Text == "Welcome!"
            && Descendants<TextBlock>(panel).Any(t => t.Text.Contains("Slide 4: scripture, left as it is", StringComparison.Ordinal)));
        Save(panel, "159-translated-copy-saved");
        if (copy is not null) await Snapshot("160-translated-slide-spanish", model.SceneOf(copy, copy.Slides[0]));

        // A song is refused before anything is sent.
        var song = model.Library.All<Presentation>().FirstOrDefault(p => p.Name == "Amazing Grace");
        if (song is not null)
        {
            sent = server.Requests.Count;
            model.Open(song, null, null);
            await Until(() => model.CurrentPresentation?.Id, id => id == song.Id, 3000);
            panel.Go(AssistantWindow.Checks);
            panel.Go(AssistantWindow.Translate);
            await panel.TranslateNow();
            await Delay(300); // the page was only just shown: let it be laid out
            var said = Descendants<TextBlock>(panel).FirstOrDefault(t => t.Text.Contains("lyrics are licensed", StringComparison.Ordinal))?.Text;
            Check($"a song is not translated: nothing is sent and the window says why (open: {model.CurrentPresentation?.Name}; {server.Requests.Count - sent} sent; “{said}”)",
                server.Requests.Count == sent && said is not null);
        }
    }

    // MARK: Captions and the sermon summary

    private async Task<string?> AssistantCaptionsAndSermon(FakeAnthropicServer server, AssistantWindow panel)
    {
        if (SpeechWorker.Recognizer() is null)
        {
            Skip("live captions and the sermon transcript", "Windows has no English speech pack on this PC");
            return null;
        }
        var wav = Path.Combine(Path.GetTempPath(), $"MxU self-test captions {Guid.NewGuid():N}.wav");
        var spoken = await Task.Run(() =>
        {
            using var voice = new SpeechSynthesizer();
            if (voice.GetInstalledVoices().Count == 0) return false;
            voice.Rate = -1;
            voice.SetOutputToWaveFile(wav, SpeechWorker.Format);
            voice.Speak("Good morning everyone and welcome to church. Grace is not something you earn. It is something you receive. So this week forgive someone before they ask you to.");
            return true;
        });
        if (!spoken)
        {
            Skip("live captions and the sermon transcript", "Windows has no text-to-speech voice to make the test recording with");
            return wav;
        }
        var sound = await File.ReadAllBytesAsync(wav);

        // Captions as heard, on both kinds of screen. Nothing is sent anywhere.
        model.ClearAllCommand.Execute(null);
        model.Settings.CaptionsTarget = "both";
        model.Settings.CaptionsLanguage = null;
        panel.Go(AssistantWindow.CaptionsPage);
        var sent = server.Requests.Count;
        model.StartCaptions(feed => new WavAtSpeakingSpeed(sound, feed));
        var alert = await Until(() => model.Show.LiveAlert, live => live?.Id == Captions.AlertId, 30000);
        await Delay(2500);
        var caption = model.Show.LiveAlert?.Message ?? "";
        Check($"captions go up as a live alert on the stage and audience screens, heard on this PC with nothing sent (“{caption}”)",
            alert is { Id: Captions.AlertId, Target: AlertTarget.Both } && caption.Length > 3 && model.CaptionsLine == caption && server.Requests.Count == sent
            && AudienceText().Contains(caption.Split(' ')[0], StringComparison.OrdinalIgnoreCase));
        Save(panel, "161-live-captions-on");
        await Snapshot("162-caption-on-the-audience-screen", model.Show.Scene());
        model.StopCaptions();
        Check("turning captions off takes the caption down and writes nothing into the library",
            model.Show.LiveAlert is null && !model.CaptionsOn && model.Library.All<AlertPreset>().All(a => a.Id != Captions.AlertId));

        // Translated: each sentence goes alone to the "assistant".
        model.Settings.CaptionsLanguage = "Spanish";
        for (var i = 0; i < 8; i++) server.ThenText("""{"text":"La gracia es un regalo."}""", inputTokens: 180, outputTokens: 12);
        model.StartCaptions(feed => new WavAtSpeakingSpeed(sound, feed));
        var translated = await Until(() => model.Show.LiveAlert?.Message ?? "", message => message.Contains("gracia", StringComparison.Ordinal), 40000);
        var sentences = server.Requests.Skip(sent).Select(r => r.Json.GetProperty("messages")[0].GetProperty("content").ToString()).ToList();
        Check($"translated captions send one sentence at a time, as text, and show the translation ({sentences.Count} sent, first “{sentences.FirstOrDefault()}”)",
            translated.Contains("La gracia es un regalo.", StringComparison.Ordinal) && sentences.Count >= 1 && sentences.All(s => s.Length < 200)
            && server.Requests.Skip(sent).All(r => r.Json.GetProperty("system")[0].GetProperty("text").GetString()!.Contains("Spanish", StringComparison.Ordinal)));
        model.StopCaptions();
        server.ClearScript();
        model.Settings.CaptionsLanguage = null;
        await Delay(500);

        // The sermon: the recording is turned into text here, then only the text is sent.
        panel.Go(AssistantWindow.Sermon);
        sent = server.Requests.Count;
        var transcribing = panel.TranscribeNow(wav);
        await Task.WhenAny(transcribing, Task.Delay(120000));
        var transcript = panel.Transcript;
        var words = string.Join(' ', transcript.Select(h => h.Text));
        Check($"a recording is transcribed on this PC through Windows' decoder and recognizer, with nothing sent (“{words}”)",
            transcribing.IsCompletedSuccessfully && transcript.Count >= 1 && words.Split(' ').Length >= 8 && server.Requests.Count == sent
            && transcript.Zip(transcript.Skip(1)).All(pair => pair.First.At <= pair.Second.At));
        var line = transcript.ToList().FindIndex(h => h.Text.Split(' ').Length >= 3);
        if (line < 0)
        {
            Skip("the sermon summary", "the recognizer heard too little of the test recording");
            return wav;
        }
        server.ThenText(JsonSerializer.Serialize(new
        {
            summary = "Grace is received, not earned, so forgive first.",
            quotes = new object[]
            {
                new { line = line + 1, text = transcript[line].Text },
                new { line = 1, text = "words that were never said in this sermon at all" },
            },
            posts = new[] { "Grace is a gift you receive.", "Forgive someone this week, before they ask." },
        }), inputTokens: 700, outputTokens: 210);
        await panel.SummariseNow();
        var asked = server.Requests.Skip(sent).FirstOrDefault()?.Body ?? "";
        var result = panel.SermonResultText;
        Check("the summary is written from the transcript's text; a quote that isn't in it word for word is thrown away, and times are the transcript's",
            asked.Contains(transcript[line].Text, StringComparison.Ordinal) && !asked.Contains("RIFF", StringComparison.Ordinal)
            && result.Contains("Grace is received, not earned", StringComparison.Ordinal)
            && result.Contains($"{ServiceReport.Clock(transcript[line].At)}  “{transcript[line].Text}”", StringComparison.Ordinal)
            && !result.Contains("never said", StringComparison.Ordinal) && result.Contains("SOCIAL POST DRAFTS", StringComparison.Ordinal)
            && Descendants<TextBlock>(panel).Any(t => t.Text.Contains("1 quote(s) that didn't match", StringComparison.Ordinal)));
        Save(panel, "163-sermon-transcript-and-summary");
        return wav;
    }
}
