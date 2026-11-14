using System.Speech.Synthesis;
using System.Text.Json;
using MxU.Slides.Assistant.Spotting;
using MxU.Slides.Core.Scripture;

namespace MxU.Slides.Assistant.Tests;

/// <summary>
/// The scripture spotter. Most of these feed it written transcripts (some copied from what
/// Windows' recognizer really produced). <see cref="ASynthesizedSermonIsHeardByWindowsOwnRecognizer"/>
/// is the one that plays real sound, made by Windows' own text-to-speech voice, through Windows'
/// offline recognizer; it is skipped on a PC with no English speech pack.
/// </summary>
public sealed class SpottingTests
{
    private static readonly Bible Kjv = BibleCatalog.BuiltIn.Load("kjv");

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static string? First(string heard) => SpokenScripture.Find(heard, Kjv).FirstOrDefault()?.Reference.Display;

    [Theory]
    [InlineData("turn with me to Romans chapter eight verse twenty eight", "Romans 8:28")]
    [InlineData("Turn with me to John chapter 3.", "John 3")]
    [InlineData("john three sixteen", "John 3:16")]
    [InlineData("then first Corinthians thirteen verses four through seven", "1 Corinthians 13:4–7")]
    [InlineData("in 2nd Timothy 3 16 we read", "2 Timothy 3:16")]
    [InlineData("we are in one John four verse eight tonight", "1 John 4:8")]
    [InlineData("psalm twenty three", "Psalm 23")]
    [InlineData("Psalm one hundred and nineteen verse one hundred five", "Psalm 119:105")]
    [InlineData("psalm one nineteen verse eleven", "Psalm 119:11")]
    [InlineData("the song of solomon chapter two verse four", "Song of Solomon 2:4")]
    [InlineData("Romans 8:28 says it plainly", "Romans 8:28")]
    [InlineData("genesis chapter one verses one to three", "Genesis 1:1–3")]
    [InlineData("Revelations twenty one verse four", "Revelation 21:4")]
    // What Windows' dictation really wrote for "John three sixteen": chapter and verse run together.
    [InlineData("data we will read john 316", "John 3:16")]
    [InlineData("hebrews 1223", "Hebrews 12:23")]
    public void AReferenceSpokenOutrightIsFound(string heard, string expected)
    {
        Assert.Equal(expected, First(heard));
    }

    [Theory]
    [InlineData("John and Mark went to the shops")]
    [InlineData("there is one thing I ask and there are numbers of them")]
    [InlineData("Mark my words, it acts on all of us")]
    [InlineData("I am sure that he is 3 times as kind")]
    [InlineData("john chapter ninety nine")]
    [InlineData("and we know that all things work together for good")]
    public void OrdinaryWordsThatHappenToBeBookNamesAreNotReferences(string heard)
    {
        Assert.Empty(SpokenScripture.Find(heard, Kjv));
    }

    [Fact]
    public void TwoReferencesInOneSentenceAreBothFound()
    {
        var found = SpokenScripture.Find("compare Romans eight twenty eight with James one verse two", Kjv);

        Assert.Equal(["Romans 8:28", "James 1:2"], found.Select(f => f.Reference.Display));
        Assert.Equal("romans 8 28", found[0].Heard);
    }

    [Theory]
    [InlineData("remember the story of the prodigal son", true)]
    [InlineData("think of the feeding of the five thousand", true)]
    [InlineData("as Paul wrote to the church", true)]
    [InlineData("the Bible says we should love one another", true)]
    [InlineData("good morning church it is good to see you", false)]
    [InlineData("please stand for the next song", false)]
    public void OnlyAnAllusionIsWorthAskingAbout(string heard, bool allusion)
    {
        Assert.Equal(allusion, SpokenScripture.SoundsLikeAnAllusion(heard));
    }

    private sealed class FakeAssistant
    {
        public List<AssistantRequest> Asked { get; } = [];
        public string Reply { get; set; } = """{"found":true,"reference":"Luke 15:11-32"}""";
        public AssistantException? Fails { get; set; }

        public Task<AssistantReply> Ask(AssistantRequest request, CancellationToken cancel)
        {
            lock (Asked) Asked.Add(request);
            return Fails is { } failure
                ? Task.FromException<AssistantReply>(failure)
                : Task.FromResult(new AssistantReply(JsonDocument.Parse(Reply).RootElement.Clone(), new AssistantUsage(200, 20), AssistantModels.Find(null)));
        }
    }

    private static Utterance Said(string text, double at, bool grammar = false, double confidence = 0.6) => new(text, TimeSpan.FromSeconds(at), confidence, grammar);

    [Fact]
    public void ASpokenReferenceWaitsAndNothingIsAskedOfTheAssistant()
    {
        var assistant = new FakeAssistant();
        var spotter = new ScriptureSpotter(() => Kjv, assistant.Ask);
        var seen = new List<Spot>();
        spotter.Spotted += seen.Add;

        spotter.Hear(Said("turn with me to Romans chapter eight verse twenty eight", 12.5), Cancel);
        spotter.Hear(Said("and we know that all things work together for good", 16), Cancel);

        var spot = Assert.Single(seen);
        Assert.Equal("Romans 8:28", spot.Display);
        Assert.Equal(SpotSource.Spoken, spot.Source);
        Assert.Equal(TimeSpan.FromSeconds(12.5), spot.At);
        Assert.Empty(assistant.Asked);
    }

    [Fact]
    public void OnlyTheVagueSentenceGoesToTheAssistantAndNothingElseOfTheSermon()
    {
        var assistant = new FakeAssistant();
        var spotter = new ScriptureSpotter(() => Kjv, assistant.Ask);
        var seen = new List<Spot>();
        spotter.Spotted += seen.Add;

        spotter.Hear(Said("good morning church my wife and I had a hard week", 1), Cancel);
        spotter.Hear(Said("remember the story of the prodigal son", 20), Cancel);
        spotter.Hear(Said("he came home with nothing", 24), Cancel);

        var request = Assert.Single(assistant.Asked);
        Assert.Equal("remember the story of the prodigal son", request.Ask);
        Assert.DoesNotContain("hard week", request.Ask + request.Instructions, StringComparison.Ordinal);
        Assert.Equal(AssistantEffort.Low, request.Effort);
        var spot = Assert.Single(seen);
        Assert.Equal("Luke 15:11–32", spot.Display);
        Assert.Equal(SpotSource.Assistant, spot.Source);
        Assert.Equal(1, spotter.Asked);
        // The spotter's schema has a yes/no and a reference: nowhere for verse text.
        Assert.Equal(["found", "reference"], JsonDocument.Parse(ScriptureSpotter.Schema).RootElement.GetProperty("properties").EnumerateObject().Select(p => p.Name));
    }

    [Theory]
    [InlineData("""{"found":false,"reference":null}""")]
    [InlineData("""{"found":true,"reference":"somewhere in the Gospels"}""")]
    [InlineData("""{"found":true,"reference":"John 99:1"}""")]
    [InlineData("""{"found":true,"reference":null}""")]
    public void AnUnsureOrImpossibleAnswerOffersNothing(string reply)
    {
        var assistant = new FakeAssistant { Reply = reply };
        var spotter = new ScriptureSpotter(() => Kjv, assistant.Ask);

        spotter.Hear(Said("remember the story of the prodigal son", 20), Cancel);

        Assert.Single(assistant.Asked);
        Assert.Empty(spotter.Spots);
    }

    [Fact]
    public void WithNoInternetOrNoAssistantTheSpotterStillHearsReferences()
    {
        var offline = new FakeAssistant { Fails = new AssistantException(AssistantProblem.Offline, "offline") };
        foreach (var spotter in new[] { new ScriptureSpotter(() => Kjv, offline.Ask), new ScriptureSpotter(() => Kjv) })
        {
            spotter.Hear(Said("remember the story of the prodigal son", 20), Cancel);
            spotter.Hear(Said("john three sixteen", 30), Cancel);

            Assert.Equal("John 3:16", Assert.Single(spotter.Spots).Display);
        }
    }

    [Fact]
    public void TheAssistantIsAskedSparinglyAndNeverAboutAPrayerRequest()
    {
        var assistant = new FakeAssistant { Reply = """{"found":false,"reference":null}""" };
        var spotter = new ScriptureSpotter(() => Kjv, assistant.Ask);

        spotter.Hear(Said("remember the story of the prodigal son", 20), Cancel);
        spotter.Hear(Said("and the parable of the sower", 22), Cancel); // too soon after the last question
        spotter.Hear(Said("remember the story of the prodigal son", 60), Cancel); // already asked
        spotter.Hear(Said("as Paul said, pray for Mary Okafor this week", 90), Cancel); // names someone to pray for
        spotter.Hear(Said("think about the parable of the talents", 120), Cancel);

        Assert.Equal(["remember the story of the prodigal son", "think about the parable of the talents"], assistant.Asked.Select(a => a.Ask));
    }

    [Fact]
    public void ARepeatedReferenceIsOfferedOnceAMinute()
    {
        var spotter = new ScriptureSpotter(() => Kjv);

        spotter.Hear(Said("john three sixteen", 10), Cancel);
        spotter.Hear(Said("yes john three sixteen", 25), Cancel);
        spotter.Hear(Said("back to john three sixteen", 200), Cancel);

        Assert.Equal([10, 200], spotter.Spots.Select(s => s.At.TotalSeconds));
    }

    [Fact]
    public void TheReferenceGrammarIsBelievedOverDictationForTheSameWords()
    {
        // What the two recognizers really produced for "Romans chapter eight verse twenty eight".
        var spotter = new ScriptureSpotter(() => Kjv);
        spotter.Hear(Said("turn with me to romans chapter eight 1st 28", 2.57), Cancel);
        Assert.Equal("Romans 8", Assert.Single(spotter.Spots).Display);

        spotter.Hear(Said("Romans 8:28", 2.6, grammar: true, confidence: 0.90), Cancel);
        Assert.Equal("Romans 8:28", Assert.Single(spotter.Spots).Display);

        // And when the grammar comes first, dictation's different reading of that moment is dropped.
        spotter.Hear(Said("1 Corinthians 13:4-7", 18.8, grammar: true, confidence: 0.84), Cancel);
        spotter.Hear(Said("then first Corinthians 13", 18.79), Cancel);
        Assert.Equal(["Romans 8:28", "1 Corinthians 13:4–7"], spotter.Spots.Select(s => s.Display));
    }

    [Fact]
    public void WhenTheTwoRecognizersNameDifferentBooksDictationsBookStands()
    {
        // The grammar sometimes hears a neighbouring book with a high score; dictation hears names well.
        foreach (var grammarFirst in new[] { true, false })
        {
            var spotter = new ScriptureSpotter(() => Kjv);
            var heard = new[] { Said("1 John 3:16", 11.39, grammar: true, confidence: 0.95), Said("later we will read John 316", 11.36) };
            foreach (var one in grammarFirst ? heard : heard.Reverse()) spotter.Hear(one, Cancel);

            Assert.Equal("John 3:16", Assert.Single(spotter.Spots).Display);
        }
    }

    [Fact]
    public void AnUnsureGrammarResultIsIgnored()
    {
        // Really heard for "John three sixteen": the grammar guessed the wrong book, with a low score.
        var spotter = new ScriptureSpotter(() => Kjv);

        spotter.Hear(Said("Hebrews 3:16", 11.39, grammar: true, confidence: 0.65), Cancel);
        spotter.Hear(Said("data we will read john 316", 11.34), Cancel);

        Assert.Equal("John 3:16", Assert.Single(spotter.Spots).Display);
    }

    [Fact]
    public void SoundFromTheSoundCardIsThinnedTo16KilohertzMono()
    {
        // Half a second of 48 kHz stereo is 8,000 samples at 16 kHz, whatever the block sizes.
        using var listener = new SpeechListener("unused.exe");
        var total = 0;
        var sink = typeof(SpeechListener).GetField("sound", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        foreach (var frames in new[] { 480, 1000, 4000, 7, 18513 })
        {
            var block = new float[2][];
            block[0] = Enumerable.Repeat(0.5f, frames).ToArray();
            block[1] = Enumerable.Repeat(-0.25f, frames).ToArray();
            listener.Feed(block, frames, 48000);
        }
        var queue = (System.Collections.Concurrent.BlockingCollection<byte[]>)sink.GetValue(listener)!;
        var first = queue.First();
        while (queue.TryTake(out var bytes)) total += bytes.Length / 2;

        Assert.Equal(8000, total);
        // (0.5 + -0.25) / 2 = 0.125 of full scale.
        Assert.Equal((short)(0.125f * 32767f), BitConverter.ToInt16(first, 0));
    }

    // What Windows' recognizer hears depends on the PC: its speech pack, its voice and its Windows
    // version. GitHub's test machine (Windows Server, another voice) hears the same sermon differently,
    // so these two tests would fail there for reasons that say nothing about our code. They run on a
    // real PC; GitHub sets GITHUB_ACTIONS=true on its machines.
    private static void SkipOnGitHub()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
            Assert.Skip("What the recognizer hears depends on the PC's voice and speech pack; this runs on a real PC, not GitHub's machine.");
    }

    [Fact]
    public void ASynthesizedSermonIsHeardByWindowsOwnRecognizer()
    {
        if (SpeechWorker.Recognizer() is null) Assert.Skip("This PC has no English speech pack, so Windows' offline recognizer can't run.");
        SkipOnGitHub();
        var wav = Path.Combine(Path.GetTempPath(), $"MxU spotter test {Guid.NewGuid():N}.wav");
        try
        {
            using (var voice = new SpeechSynthesizer())
            {
                if (voice.GetInstalledVoices().Count == 0) Assert.Skip("This PC has no text-to-speech voice to make the test sermon with.");
                voice.Rate = -1;
                voice.SetOutputToWaveFile(wav, SpeechWorker.Format);
                voice.Speak(Sermon);
            }
            var lines = new StringWriter();

            SpeechWorker.Run(wav, null, lines, TestContext.Current.CancellationToken);

            var spotter = new ScriptureSpotter(() => Kjv);
            var allusions = new List<string>();
            foreach (var line in lines.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var message = JsonDocument.Parse(line).RootElement;
                if (message.GetProperty("t").GetString() != "heard") continue;
                var heard = new Utterance(message.GetProperty("text").GetString()!, TimeSpan.FromSeconds(message.GetProperty("at").GetDouble()),
                    message.GetProperty("conf").GetDouble(), message.GetProperty("src").GetString() == "refs");
                spotter.Hear(heard, Cancel);
                if (!heard.FromGrammar && SpokenScripture.SoundsLikeAnAllusion(heard.Text)) allusions.Add(heard.Text);
            }
            var spotted = spotter.Spots.Select(s => s.Display).ToList();
            TestContext.Current.TestOutputHelper?.WriteLine(lines.ToString());
            // The three references spoken outright are spotted, in order, each at a later moment than the last.
            Assert.Contains("Romans 8:28", spotted);
            Assert.Contains("John 3:16", spotted);
            Assert.Contains("1 Corinthians 13:4–7", spotted);
            Assert.True(spotter.Spots.Select(s => s.At).SequenceEqual(spotter.Spots.Select(s => s.At).Order()), "the spots are in the order they were said");
            Assert.True(spotter.Spots[^1].At > TimeSpan.FromSeconds(10), "each spot carries its time in the recording");
            // The allusion is heard well enough to be the one sentence worth asking the assistant about.
            Assert.Contains(allusions, a => a.Contains("prodigal", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(wav);
        }
    }

    [Fact]
    public void SoundArrivingDownThePipeIsHeardTheSameAsAFile()
    {
        // A live microphone has no file: its sound arrives in small blocks down a pipe, and both
        // recognizers read the same blocks. Here the pipe is fed a spoken sentence, then closed.
        if (SpeechWorker.Recognizer() is null) Assert.Skip("This PC has no English speech pack, so Windows' offline recognizer can't run.");
        SkipOnGitHub();
        using var sound = new MemoryStream();
        using (var voice = new SpeechSynthesizer())
        {
            if (voice.GetInstalledVoices().Count == 0) Assert.Skip("This PC has no text-to-speech voice to make the test sermon with.");
            voice.Rate = -1;
            voice.SetOutputToAudioStream(sound, SpeechWorker.Format);
            voice.Speak("Then first Corinthians thirteen, verses four through seven. Remember the story of the prodigal son.");
            voice.SetOutputToNull();
        }
        // Two seconds of quiet at the end, as after a real sentence.
        sound.Write(new byte[64000]);
        sound.Position = 0;
        var lines = new StringWriter();

        SpeechWorker.Run(null, new SmallReads(sound), lines, TestContext.Current.CancellationToken);

        var heard = lines.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        TestContext.Current.TestOutputHelper?.WriteLine(lines.ToString());
        Assert.Equal("ready", heard[0].GetProperty("t").GetString());
        Assert.Equal("end", heard[^1].GetProperty("t").GetString());
        Assert.Contains(heard, h => h.GetProperty("t").GetString() == "heard" && h.GetProperty("src").GetString() == "refs" && h.GetProperty("text").GetString() == "1 Corinthians 13:4-7");
        Assert.Contains(heard, h => h.GetProperty("t").GetString() == "heard" && h.GetProperty("src").GetString() == "dictation" && h.GetProperty("text").GetString()!.Contains("prodigal", StringComparison.OrdinalIgnoreCase));
    }

    // A stream that hands over at most 700 bytes a read, as a pipe from another process does.
    private sealed class SmallReads(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, 700));
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    internal const string Sermon =
        "Good morning church. Turn with me to Romans chapter eight verse twenty eight. "
        + "And we know that all things work together for good. Later we will read John three sixteen. "
        + "Remember the story of the prodigal son. Then first Corinthians thirteen, verses four through seven.";
}
