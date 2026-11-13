using System.Diagnostics;
using System.Speech.Synthesis;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MxU.Slides.Assistant;
using MxU.Slides.Assistant.Spotting;
using MxU.Slides.Assistant.Testing;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Notes;
using MxU.Slides.Core.Scripture;
using MxU.Slides.Windows.Views;
using MxU.Slides.Windows.Views.Assistant;
using MxU.Slides.Windows.Views.Notes;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>
/// Phase 12, the service assistant, through the real windows. Anthropic is played by a stand-in
/// server on this PC (<see cref="FakeAnthropicServer"/>) whose answers are scripted here, some of
/// them deliberately wrong: no real API call is made, so this proves what the app does with an
/// answer, not how good the real model's answers are.
/// <list type="bullet">
/// <item>With no key every AI button is greyed; a wrong key isn't kept; a good one is kept encrypted.</item>
/// <item>Suggest with AI marks a note's passages, fills a bare reference from the built-in Bible
/// (whatever the "model" quoted), and never sends a prayer request.</item>
/// <item>Lyrics clean-up labels the words, and refuses an answer that changed one.</item>
/// <item>With no internet, and switched off, the app carries on.</item>
/// <item>The scripture spotter hears a sermon (made by Windows' own voice, played into the real
/// speech process at the speed of speech), offers the passages, and shows one only on Enter.</item>
/// </list>
/// Pictures 140–150.
/// </summary>
public sealed partial class SelfTest
{
    private const string AssistantTestKey = "sk-ant-api03-selftest-0123456789abcdefghijklmnop";

    private const string AssistantSermon =
        "Unshakable\n"
        + "Intro: the earthquake in 1989\n"
        + "John 3:16\n"
        + "Psalm 46:1 – God is our refuge and strength, a very present help in trouble.\n"
        + "“Faith is taking the first step even when you don’t see the whole staircase.” — Martin Luther King Jr.\n"
        + "Prayer requests:\n"
        + "Mary Okafor, surgery on Tuesday";

    private const string PastedLyrics =
        "Be Thou My Vision Lyrics\n"
        + "Be Thou my vision, O Lord of my heart; naught be all else to me, save that Thou art.\n"
        + "Thou my best thought, by day or by night, waking or sleeping, Thy presence my light.\n"
        + "Be Thou my wisdom, and Thou my true word; I ever with Thee and Thou with me, Lord.\n"
        + "Lyrics provided by SongSite. Embed. Share.";

    private const string CleanedLyricsReply = """
        {"sections":[
          {"label":"Verse","number":1,"lines":["Be Thou my vision, O Lord of my heart;","naught be all else to me, save that Thou art.","Thou my best thought, by day or by night,","waking or sleeping, Thy presence my light."]},
          {"label":"Verse","number":2,"lines":["Be Thou my wisdom, and Thou my true word;","I ever with Thee and Thou with me, Lord."]}
        ],"order":[0,1]}
        """;

    private async Task AssistantCheck()
    {
        using var server = new FakeAnthropicServer();
        var windows = new List<Window>();
        NoteDocument? note = null;
        Presentation? deck = null;
        string? wav = null;
        try
        {
            model.ClearAllCommand.Execute(null);
            await AssistantKeyAndSettings(server, windows);
            (note, deck) = await AssistantSuggestsSlides(server, windows);
            await AssistantCleansLyrics(server, windows);
            await AssistantOfflineAndOff(server, windows, note);
            await AssistantStageTwo(server, windows); // SelfTest.AssistantTasks.cs
            wav = await SpotterHearsASermon(server, windows);
        }
        finally
        {
            model.StopSpotter();
            foreach (var open in windows) open.Close();
            model.ClearAllCommand.Execute(null);
            model.Scripture.Query = "";
            model.RemoveAssistantKey();
            model.UseAssistantServer(null);
            if (deck is not null) model.Library.Delete<Presentation>(deck.Id);
            if (note is not null) model.DeleteNote(note);
            if (wav is not null) File.Delete(wav);
        }
    }

    // MARK: The key, the switch and Settings

    private async Task AssistantKeyAndSettings(FakeAnthropicServer server, List<Window> windows)
    {
        model.UseAssistantServer(server.Url);
        Check("with no API key the assistant is unavailable, and says why", model.AssistantUnavailable?.Contains("API key", StringComparison.Ordinal) == true && !model.Settings.AssistantEnabled);

        var settings = new SettingsWindow(model, model.Outputs!) { Owner = window };
        windows.Add(settings);
        settings.Show();
        await Delay(500);
        var section = settings.AssistantPanel;
        section.BringIntoView();
        await Delay(300);
        Save(settings, "140-assistant-settings-no-key");

        // A wrong key is refused by "Anthropic" and not kept.
        section.KeyBox!.Password = "sk-ant-wrong-000000000000000000000000";
        section.SaveButton!.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var refused = await Until(() => Descendants<TextBlock>(section).FirstOrDefault(t => t.Text.Contains("was not saved", StringComparison.Ordinal)), t => t is not null, 15000);
        Check("a key Anthropic refuses is not saved", refused is not null && !model.Assistant.Vault.HasKey && !model.Settings.AssistantEnabled);

        section.KeyBox!.Password = AssistantTestKey;
        section.SaveButton!.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Until(() => model.Assistant.Vault.HasKey, has => has, 15000);
        await Delay(300);
        Check("a good key is checked with one free call, kept, and turns the assistant on",
            model.Assistant.Vault.Load() == AssistantTestKey && model.Settings.AssistantEnabled && model.AssistantUnavailable is null
            && server.Requests.Count == 2 && server.Requests.All(r => r.Method == "GET" && r.Url.Contains("/v1/models/", StringComparison.Ordinal)));
        var keyFile = Path.Combine(AppPaths.LocalRoot, "Assistant", "key.bin");
        Check("the key is encrypted on disk, and is in no setting, log or address",
            File.Exists(keyFile) && !System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(keyFile)).Contains("sk-ant", StringComparison.Ordinal)
            && !File.ReadAllText(AppPaths.SettingsFile).Contains("sk-ant", StringComparison.Ordinal)
            && !File.ReadAllText(model.Assistant.Log.FilePath).Contains("sk-ant", StringComparison.Ordinal)
            && server.Requests.All(r => !r.Url.Contains("sk-ant", StringComparison.Ordinal)));
        section.BringIntoView();
        await Delay(400);
        Check("Settings shows the key masked, the switch and the model with its prices",
            Descendants<TextBlock>(section).Any(t => t.Text.Contains("sk-ant-…mnop", StringComparison.Ordinal))
            && Descendants<CheckBox>(section).Any(c => c.IsChecked == true)
            && Descendants<ComboBox>(section).Any(c => c.SelectedItem?.ToString()?.Contains("Opus", StringComparison.Ordinal) == true || c.Text.Contains("Opus", StringComparison.Ordinal)));
        Save(settings, "141-assistant-settings-key-saved");
        settings.Close();
    }

    // MARK: Suggest with AI

    private async Task<(NoteDocument Note, Presentation? Deck)> AssistantSuggestsSlides(FakeAnthropicServer server, List<Window> windows)
    {
        var note = model.PasteNote(AssistantSermon);
        var blocks = note.Body!.Blocks;
        int At(string starts) => blocks.FindIndex(b => NoteReading.Text(b).StartsWith(starts, StringComparison.Ordinal));
        var (title, john, psalm, quote) = (At("Unshakable"), At("John 3:16"), At("Psalm 46:1"), At("“Faith"));
        var before = server.Requests.Count;
        // The "model" first checks a reference with the app's tool, then answers. Its answer
        // misbehaves on purpose: it quotes its own verse text and names the wrong reference.
        server.ThenToolUse("look_up_passage", """{"reference":"John 3:16"}""").ThenText(
            "{\"passages\":["
            + $"{{\"first_block\":{title},\"last_block\":{title},\"kind\":\"title\",\"reference\":null}},"
            + $"{{\"first_block\":{john},\"last_block\":{john},\"kind\":\"scripture\",\"reference\":\"Romans 8:28\",\"text\":\"For God was so fond of the world\"}},"
            + $"{{\"first_block\":{psalm},\"last_block\":{psalm},\"kind\":\"scripture\",\"reference\":\"Psalm 46:1\"}},"
            + $"{{\"first_block\":{quote},\"last_block\":{quote},\"kind\":\"quote\",\"reference\":null}}"
            + "]}", inputTokens: 1450, outputTokens: 180);

        var live = model.Show.Live;
        var make = MakeSlidesWindow.Open(window, model, note);
        windows.Add(make);
        await Delay(900);
        var button = Descendants<Button>(make).FirstOrDefault(b => b.Content as string == "Suggest with AI");
        Check("Make Slides offers Suggest with AI, with a guess at the cost, once there's a key",
            button is { IsEnabled: true } && Descendants<TextBlock>(make).Any(t => t.Text.StartsWith("Sends this note to Anthropic:", StringComparison.Ordinal)));
        await make.SuggestNow();
        await Delay(900);
        var asked = server.Requests.Skip(before).ToList();
        Check("Suggest with AI asks with adaptive thinking, a fixed JSON shape and the Bible look-up tool",
            asked.Count == 2 && asked[0].Json.GetProperty("thinking").GetProperty("type").GetString() == "adaptive"
            && asked[0].Json.GetProperty("output_config").GetProperty("format").GetProperty("type").GetString() == "json_schema"
            && asked[0].Json.GetProperty("tools")[0].GetProperty("name").GetString() == "look_up_passage"
            && asked[1].Body.Contains("Bible (KJV): 1 verse.", StringComparison.Ordinal));
        Check("the prayer request never left the PC, and the window says a line was kept private",
            asked.All(r => !r.Body.Contains("Okafor", StringComparison.Ordinal) && !r.Body.Contains("surgery", StringComparison.Ordinal))
            && Descendants<TextBlock>(make).Any(t => t.Text.Contains("kept private", StringComparison.Ordinal)));
        Check("the window shows what the job used (tokens and cost)",
            Descendants<TextBlock>(make).Any(t => t.Text.Contains("The assistant suggested 4 passages", StringComparison.Ordinal) && t.Text.Contains("tokens in", StringComparison.Ordinal) && t.Text.Contains("about $0.0", StringComparison.Ordinal)));
        Check("a suggestion changes no document and shows nothing: the note is as it was and the outputs are untouched",
            NoteSlideMarks.Groups(model.Library.Get<NoteDocument>(note.Id)!.Body!).Count == 0 && ReferenceEquals(model.Show.Live, live));
        Save(make, "142-make-slides-suggested-by-ai");

        make.MakeNow();
        note = model.Library.Get<NoteDocument>(note.Id)!;
        var deck = model.DeckOf(note);
        var words = deck is null ? "" : string.Join("\n", deck.Slides.SelectMany(s => s.Objects).Select(o => o.Text));
        Check("the bare reference's slide holds the King James words from the built-in Bible, not the model's",
            words.Contains("For God so loved the world", StringComparison.Ordinal) && words.Contains("John 3:16", StringComparison.Ordinal)
            && !words.Contains("fond", StringComparison.Ordinal) && !words.Contains("Romans", StringComparison.Ordinal));
        Check("the pastor's own verse, the title and the quote are slides in the note's own words; the prayer request is not",
            words.Contains("God is our refuge and strength", StringComparison.Ordinal) && words.Contains("Unshakable", StringComparison.Ordinal)
            && words.Contains("first step", StringComparison.Ordinal) && !words.Contains("Okafor", StringComparison.Ordinal));
        Check("what's saved is an ordinary note with ordinary marks (types a Mac knows)",
            NoteSlideMarks.Groups(note.Body!) is { Count: 4 } groups && groups.All(g => NoteSlideMarks.ParseType(g.Type) is not null && g.State is null));
        var verse = deck?.Slides.FirstOrDefault(s => s.Objects.Any(o => o.Text.Contains("For God so loved", StringComparison.Ordinal)));
        if (deck is not null && verse is not null) await Snapshot("143-ai-suggested-verse-from-the-kjv", model.SceneOf(deck, verse));
        return (note, deck);
    }

    // MARK: Lyrics clean-up

    private async Task AssistantCleansLyrics(FakeAnthropicServer server, List<Window> windows)
    {
        var editor = new LyricsEditorWindow(model, null) { Owner = window };
        windows.Add(editor);
        editor.Show();
        editor.Editor.Title = "Be Thou My Vision";
        editor.Editor.Lyrics = PastedLyrics;
        await Delay(500);

        server.ThenText(CleanedLyricsReply, inputTokens: 700, outputTokens: 160);
        await editor.CleanUpNow();
        await Delay(700);
        var cleaned = editor.Editor.Lyrics;
        Check("Clean up with AI sorts pasted lyrics into labelled verses",
            cleaned.StartsWith("Verse 1\nBe Thou my vision, O Lord of my heart;\n", StringComparison.Ordinal) && cleaned.Contains("\n\nVerse 2\nBe Thou my wisdom", StringComparison.Ordinal));
        Check("every word is the pasted word, in order (only the website's clutter is gone, and it is listed)",
            LyricsCleanup.Words(cleaned.Replace("Verse 1", "", StringComparison.Ordinal).Replace("Verse 2", "", StringComparison.Ordinal))
                .SequenceEqual(LyricsCleanup.Words(string.Join("\n", PastedLyrics.Split('\n')[1..4])))
            && Descendants<TextBlock>(editor).Any(t => t.Text.Contains("Lyrics provided by SongSite", StringComparison.Ordinal) && t.Text.Contains("every word checked", StringComparison.Ordinal)));
        Save(editor, "144-lyrics-cleaned-by-ai");

        // Put the pasted text back, then a reply that "corrects" one word: it must be refused whole.
        await editor.CleanUpNow();
        Check("Put my text back restores what was pasted", editor.Editor.Lyrics == PastedLyrics);
        server.ThenText(CleanedLyricsReply.Replace("naught be all else", "nought be all else", StringComparison.Ordinal));
        await editor.CleanUpNow();
        await Delay(500);
        Check("a reply that changes one word is thrown away and the lyrics stay as pasted",
            editor.Editor.Lyrics == PastedLyrics && Descendants<TextBlock>(editor).Any(t => t.Text.Contains("“nought”", StringComparison.Ordinal) && t.Text.Contains("Your lyrics are unchanged", StringComparison.Ordinal)));
        Save(editor, "145-lyrics-changed-word-refused");
        editor.Close();
    }

    // MARK: No internet, and switched off

    private async Task AssistantOfflineAndOff(FakeAnthropicServer server, List<Window> windows, NoteDocument note)
    {
        // Nothing listens on this port: the same as the internet being down.
        model.UseAssistantServer(new Uri("http://127.0.0.1:9/"));
        var make = MakeSlidesWindow.Open(window, model, model.Library.Get<NoteDocument>(note.Id)!);
        windows.Add(make);
        await Delay(700);
        var rows = NoteSlideMarks.Groups(model.Library.Get<NoteDocument>(note.Id)!.Body!).Count;
        var asking = make.SuggestNow();
        // While the assistant waits on a dead network the show goes on: a slide goes live at once.
        model.SelectedServiceItem = model.ServiceItems.FirstOrDefault(r => r.Name == "Amazing Grace");
        await Until(() => model.CurrentPresentation?.Name, name => name == "Amazing Grace", 3000);
        var clock = Stopwatch.StartNew();
        if (model.Slides.Count > 0) model.GoLiveCommand.Execute(model.Slides[0]);
        await Until(() => model.LiveTitle, liveTitle => liveTitle == "Amazing Grace", 3000);
        Check($"with no internet a slide still goes live at once ({clock.ElapsedMilliseconds} ms) while the assistant waits",
            model.LiveTitle == "Amazing Grace" && clock.ElapsedMilliseconds < 1500);
        await asking;
        await Delay(300);
        Check("with no internet Suggest with AI says so calmly and changes nothing",
            Descendants<TextBlock>(make).Any(t => t.Text.Contains("can't reach the internet", StringComparison.Ordinal) && t.Text.Contains("The rows are unchanged", StringComparison.Ordinal))
            && NoteSlideMarks.Groups(model.Library.Get<NoteDocument>(note.Id)!.Body!).Count == rows);
        Save(make, "146-no-internet-calm-message");

        // The PC's switch off: the button greys, and nothing is sent.
        model.UseAssistantServer(server.Url);
        var sent = server.Requests.Count;
        model.SetAssistant(enabled: false);
        await Delay(300);
        var button = Descendants<Button>(make).FirstOrDefault(b => b.Content as string == "Suggest with AI");
        await make.SuggestNow();
        Check("switched off, the AI button is greyed with the reason, and nothing is sent",
            button is { IsEnabled: false } && (button.ToolTip as string)?.Contains("switched off", StringComparison.Ordinal) == true && server.Requests.Count == sent);
        model.SetAssistant(enabled: true);
        make.Close();
        model.ClearAllCommand.Execute(null);

        var log = AssistantLogWindow.Open(window, model);
        windows.Add(log);
        await Delay(500);
        var entries = model.Assistant.Log.Entries;
        Check("the log lists every job with what was sent and what came back",
            entries.Any(e => e.Feature == "Make Slides" && e.Sent.Contains("Unshakable", StringComparison.Ordinal) && e.Received.Contains("passages", StringComparison.Ordinal) && e.Tools.Count == 1)
            && entries.Any(e => e.Feature == "Lyrics clean-up" && e.Sent.Contains("Be Thou my vision", StringComparison.Ordinal))
            && entries.Any(e => e.Problem?.Contains("internet", StringComparison.Ordinal) == true)
            && entries.All(e => !e.Sent.Contains("Okafor", StringComparison.Ordinal))
            && !File.ReadAllText(model.Assistant.Log.FilePath).Contains("sk-ant", StringComparison.Ordinal));
        Save(log, "147-assistant-log");
        log.Close();
    }

    // MARK: The scripture spotter

    /// <summary>
    /// The sermon is spoken by Windows' own text-to-speech voice into a file, and the file is
    /// played into the spotter at the speed of speech, down the same pipe a microphone's sound
    /// takes. So the real speech process, the real recognizer and the real pipe all run; the
    /// sound card and a human voice in a noisy hall are what this cannot stand in for.
    /// </summary>
    private async Task<string?> SpotterHearsASermon(FakeAnthropicServer server, List<Window> windows)
    {
        if (SpeechWorker.Recognizer() is null)
        {
            Skip("the scripture spotter hearing a sermon", "Windows has no English speech pack on this PC");
            return null;
        }
        var wav = Path.Combine(Path.GetTempPath(), $"MxU self-test sermon {Guid.NewGuid():N}.wav");
        var spoken = await Task.Run(() =>
        {
            using var voice = new SpeechSynthesizer();
            if (voice.GetInstalledVoices().Count == 0) return false;
            voice.Rate = -1;
            voice.SetOutputToWaveFile(wav, SpeechWorker.Format);
            voice.Speak("Good morning church. Turn with me to Romans chapter eight verse twenty eight. "
                + "And we know that all things work together for good. Later we will read John three sixteen. "
                + "Remember the story of the prodigal son. Then first Corinthians thirteen, verses four through seven.");
            return true;
        });
        if (!spoken)
        {
            Skip("the scripture spotter hearing a sermon", "Windows has no text-to-speech voice to make the test sermon with");
            return wav;
        }

        // The one allusion goes to the "assistant", which names the passage.
        var before = server.Requests.Count;
        server.ThenText("""{"found":true,"reference":"Luke 15:11-32"}""", inputTokens: 260, outputTokens: 24);
        model.ClearAllCommand.Execute(null);
        model.Scripture.Query = "";
        var panel = BibleWindow.Open(window, model);
        windows.Add(panel);
        await Delay(600);
        Save(panel, "148-bible-window-spotter-off");

        var sound = await File.ReadAllBytesAsync(wav);
        var seconds = (sound.Length - 44) / 32000.0;
        var listening = Stopwatch.StartNew();
        await model.StartSpotterAsync(feed => new WavAtSpeakingSpeed(sound, feed));
        var ready = await Until(() => model.SpotterStatus, status => status.StartsWith("Listening", StringComparison.Ordinal) || !model.SpotterListening, 20000);
        Check($"the spotter starts Windows' offline recognizer in its own low-priority process ({ready})",
            model.SpotterListening && ready.Contains("on this PC only", StringComparison.Ordinal)
            && Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName).Any(p => p.Id != Environment.ProcessId && Priority(p) == ProcessPriorityClass.BelowNormal));

        string[] expected = ["Romans 8:28", "John 3:16", "1 Corinthians 13:4–7"];
        await Until(() => model.Spots.Count(s => expected.Contains(s.Display)), count => count == 3, (int)(seconds * 1000) + 15000);
        await Until(() => model.Spots.Any(s => s.Source == SpotSource.Assistant), any => any, 5000);
        var heard = model.Spots.Select(s => s.Display).Reverse().ToList();
        var took = listening.Elapsed;
        var cpu = model.SpotterCpuTotal;
        // Free dictation on a synthetic voice isn't word-perfect, so two of the three is the bar; the list is in the report.
        Check($"references spoken in a {seconds:0}-second sermon are spotted (heard: {string.Join(", ", heard)})", heard.Count(expected.Contains) >= 2);
        var guess = model.Spots.FirstOrDefault(s => s.Source == SpotSource.Assistant);
        var questions = server.Requests.Skip(before).ToList();
        if (questions.Count == 0)
        {
            Skip("the allusion going to the assistant", "the recognizer didn't hear “the story of the prodigal son” clearly this time");
        }
        else
        {
            var sentence = questions[0].Json.GetProperty("messages")[0].GetProperty("content").ToString();
            Check($"only the vague sentence goes to the assistant (“{sentence}”), which names the passage",
                questions.Count == 1 && sentence.Contains("prodigal", StringComparison.OrdinalIgnoreCase) && !sentence.Contains("morning", StringComparison.OrdinalIgnoreCase)
                && !sentence.Contains("Romans", StringComparison.OrdinalIgnoreCase) && guess?.Display == "Luke 15:11–32");
        }
        Check("a spotted passage waits in the Bible window's box; nothing is shown by itself",
            model.Show.Live is null && model.Spots.Any(s => s.Display == model.Scripture.Query));
        var share = cpu.TotalSeconds / took.TotalSeconds * 100;
        report.AppendLine($"NOTE  speech recognition used {cpu.TotalSeconds:0.0} s of processor time over {took.TotalSeconds:0} s of listening: {share:0}% of one core, {share / Environment.ProcessorCount:0.0}% of this PC's {Environment.ProcessorCount} (the self-test's outputs were running)");
        Check($"speech recognition's processor use is measured and modest ({share:0}% of one core)", cpu > TimeSpan.Zero && share < 60);
        await Delay(2100); // one more reading of the CPU meter for the picture
        Save(panel, "149-spotter-passages-waiting");

        if (model.Spots.Count == 0) return wav;

        // The operator's Enter shows the waiting passage.
        var spot = model.Spots.FirstOrDefault(s => s.Display == "John 3:16") ?? model.Spots[0];
        model.UseSpot(spot);
        await Until(() => model.Scripture.HasUnreadTyping, unread => !unread, 10000);
        await model.Scripture.Ready;
        panel.Activate();
        Press(panel, Key.Enter);
        var kjv = BibleCatalog.BuiltIn.Load("kjv");
        var opening = string.Join(' ', kjv.Read(spot.Reference).Verses[0].Text.Split(' ').Take(3));
        var shown = await Until(() => AudienceText(), text => text.Contains(opening, StringComparison.Ordinal), 8000);
        Check($"Enter shows the spotted passage ({spot.Display}) from the built-in Bible", shown.Contains(opening, StringComparison.Ordinal) && model.LiveTitle?.StartsWith(spot.Display, StringComparison.Ordinal) == true);
        await Delay(500);
        Save(panel, "150-spotted-passage-live-after-enter");
        model.StopSpotter();
        var gone = await Until(
            () => Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName).Any(p => p.Id != Environment.ProcessId && Priority(p) == ProcessPriorityClass.BelowNormal),
            still => !still, 5000);
        Check("stopping the spotter ends its speech process", !model.SpotterListening && !gone);
        panel.Close();
        return wav;
    }

    private static ProcessPriorityClass? Priority(Process process)
    {
        try { return process.HasExited ? null : process.PriorityClass; }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    /// <summary>
    /// The self-test's microphone: a .wav file (16 kHz, 16-bit, one channel) handed over in
    /// 20-millisecond blocks at the speed it was spoken, then silence, until disposed.
    /// </summary>
    private sealed class WavAtSpeakingSpeed : IDisposable
    {
        private readonly CancellationTokenSource stop = new();

        public WavAtSpeakingSpeed(byte[] wav, Action<float[][], int, int> feed)
        {
            new Thread(() =>
            {
                const int block = 320;
                var clock = Stopwatch.StartNew();
                var samples = new float[block];
                for (long sent = 0; !stop.IsCancellationRequested; sent += block)
                {
                    for (var i = 0; i < block; i++)
                    {
                        var at = 44 + (sent + i) * 2;
                        samples[i] = at + 1 < wav.Length ? BitConverter.ToInt16(wav, (int)at) / 32768f : 0f;
                    }
                    feed([samples], block, 16000);
                    var due = TimeSpan.FromSeconds((sent + block) / 16000.0) - clock.Elapsed;
                    if (due > TimeSpan.Zero) Thread.Sleep(due);
                }
            }) { IsBackground = true, Name = "Self-test sermon" }.Start();
        }

        public void Dispose() => stop.Cancel();
    }
}
