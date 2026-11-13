using System.Diagnostics;
using System.Globalization;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using System.Text.Json;
using MxU.Slides.Core.Scripture;

namespace MxU.Slides.Assistant.Spotting;

/// <summary>
/// The speech recognizer, run as a separate small process (<c>MxUSlides.exe --speech-worker</c>)
/// so that it can be given a low priority, measured, and stopped without touching the show.
/// It turns sound into text with <b>Windows' desktop speech recognizer</b> (SAPI, through
/// <c>System.Speech</c>), which works entirely on this PC from the speech pack Windows already
/// has: nothing is downloaded and no sound leaves the machine.
/// <para>
/// <b>Why not Windows.Media.SpeechRecognition</b> (the newer API the plan named)? Its free
/// dictation is done by Microsoft's online service ("Online speech recognition" in Windows'
/// privacy settings), so the sermon's sound would leave the PC. It also can't listen to a chosen
/// input or to a file. The desktop recognizer does all three.
/// </para>
/// <para>
/// <b>Two listeners on one sound.</b> Free dictation is weak at chapter and verse ("john 316"),
/// so a second recognizer listens to the same sound with a small grammar that knows only
/// "book, [chapter] number, [verse] number, [to number]". Dictation gives the sentences (for
/// the parser and for allusions); the grammar gives clean references.
/// </para>
/// <para>
/// <b>The pipe.</b> Sound arrives on standard input as 16 kHz, 16-bit, mono samples (or from a
/// .wav file, for tests). Each thing heard leaves on standard output as one line of JSON:
/// <c>{"t":"heard","src":"dictation","text":"…","at":12.3,"conf":0.62}</c>.
/// </para>
/// </summary>
public static class SpeechWorker
{
    public const string Switch = "--speech-worker";

    /// <summary>After <see cref="Switch"/>: transcribe this .wav file (16 kHz, 16-bit, one channel) instead of listening to the pipe.</summary>
    public const string WavSwitch = "--wav";

    public static readonly SpeechAudioFormatInfo Format = new(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono);

    /// <summary>
    /// Runs the worker when the process was started as one, at below-normal priority, and returns
    /// true (the caller then exits without opening any window).
    /// </summary>
    public static bool RunIfAsked(string[] args)
    {
        if (!args.Contains(Switch)) return false;
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        // On a thread of its own, not the app's UI thread. The recognizer hands its results to
        // the thread that made it through that thread's message queue when it has one, and the
        // UI thread's would never be read here (it is waiting for this to finish): nothing
        // would ever be heard. A plain thread has no queue, so results arrive directly.
        // A recording to transcribe (the sermon summary): dictation only, since nobody is waiting
        // for references, and it halves the work.
        var at = Array.IndexOf(args, WavSwitch);
        var wav = at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
        var worker = new Thread(() => Run(wav, wav is null ? Console.OpenStandardInput() : null, output, CancellationToken.None, referencesToo: wav is null)) { Name = "Speech recognizer" };
        worker.Start();
        worker.Join();
        return true;
    }

    /// <summary>The installed recognizer to use: the PC's own English if it has one, else US English; null when none is installed.</summary>
    public static RecognizerInfo? Recognizer()
    {
        var installed = SpeechRecognitionEngine.InstalledRecognizers().Where(r => r.Culture.TwoLetterISOLanguageName == "en").ToList();
        return installed.FirstOrDefault(r => r.Culture.Name == CultureInfo.CurrentUICulture.Name)
            ?? installed.FirstOrDefault(r => r.Culture.Name == "en-US")
            ?? installed.FirstOrDefault();
    }

    /// <summary>
    /// Listens until the sound ends (a file's end, the pipe closing) or <paramref name="cancel"/>.
    /// </summary>
    /// <param name="wav">A .wav file to listen to; null to listen to <paramref name="input"/>.</param>
    /// <param name="referencesToo">False to run free dictation alone (a transcript), without the second, references-only listener.</param>
    public static void Run(string? wav, Stream? input, TextWriter output, CancellationToken cancel, bool referencesToo = true)
    {
        var gate = new Lock();
        // A file is read by each recognizer as fast as it can, so one finishes the whole file
        // before the other starts speaking up. What they heard is held and written in the order
        // it was said, as it would have arrived live.
        var held = wav is null || !referencesToo ? null : new List<(double At, string Line)>();
        void Say(object line, double? at = null)
        {
            var json = JsonSerializer.Serialize(line);
            lock (gate)
            {
                if (held is not null && at is { } time) held.Add((time, json));
                else output.WriteLine(json);
            }
        }

        if (Recognizer() is not { } recognizer)
        {
            Say(new { t = "error", message = "Windows has no English speech pack installed (Settings › Time & language › Speech)." });
            return;
        }
        try
        {
            using var dictation = new SpeechRecognitionEngine(recognizer);
            using var references = new SpeechRecognitionEngine(recognizer);
            dictation.LoadGrammar(new DictationGrammar { Name = "dictation" });
            references.LoadGrammar(ReferenceGrammar(recognizer.Culture));
            // A preacher pauses mid-sentence: wait a little longer than the default before closing one.
            dictation.EndSilenceTimeoutAmbiguous = TimeSpan.FromMilliseconds(700);

            using var ended = new CountdownEvent(referencesToo ? 2 : 1);
            void Wire(SpeechRecognitionEngine engine, string source)
            {
                engine.SpeechRecognized += (_, e) =>
                {
                    var text = source == "refs" ? ReferenceText(e.Result) : e.Result.Text;
                    var at = Math.Round((e.Result.Audio?.AudioPosition ?? TimeSpan.Zero).TotalSeconds, 2);
                    if (text is { Length: > 0 }) Say(new { t = "heard", src = source, text, at, conf = Math.Round(e.Result.Confidence, 2) }, at);
                };
                engine.RecognizeCompleted += (_, _) =>
                {
                    if (!ended.IsSet) ended.Signal();
                };
            }
            Wire(dictation, "dictation");
            if (referencesToo) Wire(references, "refs");

            Thread? pump = null;
            if (wav is not null)
            {
                dictation.SetInputToWaveFile(wav);
                if (referencesToo) references.SetInputToWaveFile(wav);
            }
            else
            {
                // One sound, two listeners: copy each block from the pipe to both.
                var (first, second) = (new SoundPipe(), new SoundPipe());
                dictation.SetInputToAudioStream(first, Format);
                references.SetInputToAudioStream(second, Format);
                pump = new Thread(() =>
                {
                    var buffer = new byte[3200]; // a tenth of a second
                    try
                    {
                        int read;
                        while ((read = input!.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            first.Write(buffer, 0, read);
                            second.Write(buffer, 0, read);
                        }
                    }
                    catch (Exception e) when (e is IOException or ObjectDisposedException) { }
                    first.Complete();
                    second.Complete();
                }) { IsBackground = true, Name = "Speech sound in" };
            }
            Say(new { t = "ready", recognizer = recognizer.Culture.Name });
            dictation.RecognizeAsync(RecognizeMode.Multiple);
            if (referencesToo) references.RecognizeAsync(RecognizeMode.Multiple);
            pump?.Start();
            try { ended.Wait(cancel); }
            catch (OperationCanceledException)
            {
                dictation.RecognizeAsyncCancel();
                if (referencesToo) references.RecognizeAsyncCancel();
            }
            if (held is not null)
            {
                foreach (var (_, line) in held.OrderBy(h => h.At)) output.WriteLine(line);
            }
            Say(new { t = "end" });
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or FormatException or System.Runtime.InteropServices.COMException or PlatformNotSupportedException)
        {
            Say(new { t = "error", message = $"Windows' speech recognizer couldn't start: {e.Message}" });
        }
    }

    /// <summary>
    /// The grammar that hears references: any words, then a book, an optional "chapter", a number,
    /// and optionally [verse(s)] number [to/through number]. Each part is tagged, so the result
    /// says which book and numbers were heard, whatever the surrounding words were.
    /// </summary>
    private static Grammar ReferenceGrammar(CultureInfo culture)
    {
        var numbers = new Choices();
        for (var n = 1; n <= 176; n++)
        {
            numbers.Add(new SemanticResultValue(SpokenScripture.SpokenNumber(n), n));
            // "Psalm one nineteen", "a hundred and three".
            if (n > 100)
            {
                numbers.Add(new SemanticResultValue($"one {(n % 100 < 10 ? "oh " : "")}{SpokenScripture.SpokenNumber(n % 100)}", n));
                numbers.Add(new SemanticResultValue($"one hundred and {SpokenScripture.SpokenNumber(n % 100)}", n));
            }
        }
        var books = new Choices();
        foreach (var book in BibleBooks.All)
        {
            if (char.IsAsciiDigit(book.Name[0]))
            {
                var rest = book.Name[2..];
                var (ordinal, cardinal) = book.Name[0] switch { '1' => ("first", "one"), '2' => ("second", "two"), _ => ("third", "three") };
                books.Add(new SemanticResultValue($"{ordinal} {rest}", book.Name));
                books.Add(new SemanticResultValue($"{cardinal} {rest}", book.Name));
            }
            else
            {
                books.Add(new SemanticResultValue(book.Name, book.Name));
            }
        }
        books.Add(new SemanticResultValue("Psalm", "Psalms"));
        books.Add(new SemanticResultValue("Revelations", "Revelation"));

        var range = new GrammarBuilder();
        range.Append(new Choices("to", "through"));
        range.Append(new SemanticResultKey("last", numbers));
        var verse = new GrammarBuilder();
        verse.Append(new Choices("verse", "verses"), 0, 1);
        verse.Append(new SemanticResultKey("verse", numbers));
        verse.Append(range, 0, 1);

        var reference = new GrammarBuilder { Culture = culture };
        reference.AppendWildcard();
        reference.Append(new SemanticResultKey("book", books));
        reference.Append("chapter", 0, 1);
        reference.Append(new SemanticResultKey("chapter", numbers));
        reference.Append(verse, 0, 1);
        return new Grammar(reference) { Name = "refs" };
    }

    // "Romans 8:28", "1 Corinthians 13:4-7", "Psalms 23" from the grammar's tags.
    private static string? ReferenceText(RecognitionResult result)
    {
        var tags = result.Semantics;
        if (!tags.ContainsKey("book") || !tags.ContainsKey("chapter")) return null;
        var text = string.Create(CultureInfo.InvariantCulture, $"{tags["book"].Value} {tags["chapter"].Value}");
        if (tags.ContainsKey("verse")) text += string.Create(CultureInfo.InvariantCulture, $":{tags["verse"].Value}");
        if (tags.ContainsKey("verse") && tags.ContainsKey("last")) text += string.Create(CultureInfo.InvariantCulture, $"-{tags["last"].Value}");
        return text;
    }

    /// <summary>
    /// A stream one thread writes sound into and the recognizer reads from, waiting when there's
    /// none yet. (The recognizer needs a Stream; a live microphone has no end and no length.)
    /// </summary>
    private sealed class SoundPipe : Stream
    {
        private readonly Lock gate = new();
        private readonly Queue<byte[]> blocks = new();
        private readonly SemaphoreSlim ready = new(0);
        private byte[]? current;
        private int offset;
        private long position;
        private bool complete;
        private long waiting;

        private const long MostWaiting = 60 * 32000; // a minute of 16 kHz, 16-bit sound

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => -1;
        public override long Position { get => position; set { } }

        public override void Write(byte[] buffer, int from, int count)
        {
            lock (gate)
            {
                blocks.Enqueue(buffer.AsSpan(from, count).ToArray());
                waiting += count;
                // A recognizer starved of processor time must not pile sound up for ever: past a
                // minute behind, the oldest goes.
                while (waiting > MostWaiting && blocks.TryDequeue(out var dropped)) waiting -= dropped.Length;
            }
            ready.Release();
        }

        public void Complete()
        {
            lock (gate) complete = true;
            ready.Release();
        }

        // The recognizer takes a short read to mean the sound has ended, so this waits until it
        // can hand over everything asked for (or the sound really has ended).
        public override int Read(byte[] buffer, int at, int count)
        {
            var filled = 0;
            while (filled < count)
            {
                if (current is not null && offset < current.Length)
                {
                    var take = Math.Min(count - filled, current.Length - offset);
                    Array.Copy(current, offset, buffer, at + filled, take);
                    offset += take;
                    filled += take;
                    continue;
                }
                lock (gate)
                {
                    if (blocks.TryDequeue(out current))
                    {
                        offset = 0;
                        waiting -= current.Length;
                        continue;
                    }
                    if (complete) break;
                }
                ready.Wait();
            }
            position += filled;
            return filled;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => position;
        public override void SetLength(long value) { }
    }
}
