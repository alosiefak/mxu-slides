using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using MxU.Slides.Assistant;
using MxU.Slides.Assistant.Spotting;
using MxU.Slides.Audio;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scripture;
using MxU.Slides.Windows.Infrastructure;

namespace MxU.Slides.Windows.ViewModels;

// The service assistant's half of the main view model (Phase 12): the one AssistantService the
// app has, the two "before Sunday" jobs (Suggest with AI in Make Slides, lyrics clean-up), and
// the scripture spotter. The rules these keep:
//   1. The AI suggests, the operator decides: nothing here fires a slide or saves a document.
//      A suggestion lands in a window, and a person presses the button or the Enter key.
//   2. The show never depends on the internet: every job runs on the assistant's own thread,
//      and with no key, no network or the switch off, the buttons are simply greyed.
//   3. Scripture comes from the built-in Bible and lyrics from what the operator pasted.
public sealed partial class MainViewModel
{
    private AssistantService? assistant;
    private Uri? assistantServer;

    /// <summary>Raised (on the UI thread) when the key, the switch or the model changes, so windows redraw their AI buttons.</summary>
    public event Action? AssistantChanged;

    /// <summary>The assistant (made the first time it's needed).</summary>
    public AssistantService Assistant => assistant ??= new AssistantService(
        new AssistantKeyVault(Path.Combine(AppPaths.LocalRoot, "Assistant", "key.bin")),
        new AssistantLog(Path.Combine(AppPaths.LocalRoot, "Assistant", "log.jsonl")),
        () => Settings.AssistantEnabled, () => Settings.AssistantModel, assistantServer);

    /// <summary>
    /// Points the assistant at a stand-in server on this PC instead of Anthropic. Only the
    /// self-test calls this; there is no setting or environment variable that does, so a real key
    /// can't be sent anywhere but Anthropic by a changed file.
    /// </summary>
    internal void UseAssistantServer(Uri? server)
    {
        assistant?.Dispose();
        assistant = null;
        assistantServer = server;
        AssistantChanged?.Invoke();
    }

    /// <summary>The tooltip for a greyed AI button: why it can't be used; null when it can.</summary>
    public string? AssistantUnavailable => Assistant.Unavailable is { } why ? AssistantException.Words(why) : null;

    /// <summary>Checks a pasted key with Anthropic (one free call), keeps it encrypted, and turns the assistant on.</summary>
    public async Task SaveAssistantKeyAsync(string key, CancellationToken cancel)
    {
        await Assistant.TestKey(key.Trim(), cancel);
        Assistant.Vault.Save(key);
        Settings.AssistantEnabled = true;
        Settings.Save();
        AssistantChanged?.Invoke();
    }

    public void RemoveAssistantKey()
    {
        Assistant.Vault.Clear();
        Settings.AssistantEnabled = false;
        Settings.Save();
        AssistantChanged?.Invoke();
    }

    public void SetAssistant(bool? enabled = null, string? model = null)
    {
        if (enabled is { } on) Settings.AssistantEnabled = on;
        if (model is not null) Settings.AssistantModel = model;
        Settings.Save();
        AssistantChanged?.Invoke();
    }

    // The translation the Bible window uses, read off the UI thread the first time.
    private Task<Bible> AssistantBible() =>
        BibleCatalog.BuiltIn.LoadAsync((BibleCatalog.BuiltIn.Find(Settings.ScriptureTranslation) ?? BibleCatalog.BuiltIn.Default).Id);

    // MARK: Suggest with AI (Make Slides)

    /// <summary>What Suggest with AI hands Make Slides: the note marked, and what to tell the operator.</summary>
    public sealed record NoteSuggestion(NoteSuggester.Applied Applied, string CostLine, string? PrivacyWarning, string Translation);

    /// <summary>A guess at what Suggest with AI will cost for this note, before it's sent ("about $0.02").</summary>
    public string SuggestCostEstimate(NoteBody note) =>
        AssistantCost.Words(AssistantCost.Estimate(Assistant.Model, note.PlainText.Length + 2500, 600 + note.Blocks.Count * 25));

    /// <summary>
    /// Asks the assistant which lines of a note are worth a slide. Nothing is saved: the marked
    /// note goes back to the Make Slides window, where each passage is a row to untick or change.
    /// </summary>
    /// <exception cref="AssistantException">It couldn't (the message is for the operator).</exception>
    public async Task<NoteSuggestion> SuggestNoteSlidesAsync(NoteBody basis, CancellationToken cancel)
    {
        var bible = await AssistantBible();
        var prepared = NoteSuggester.Prepare(basis, bible.Read);
        var reply = await Assistant.Ask(prepared.Request, cancel);
        var applied = NoteSuggester.Apply(basis, NoteSuggester.Read(reply.Json, basis.Blocks.Count), bible.Read);
        return new NoteSuggestion(applied, reply.CostLine, prepared.PrivacyWarning, bible.Translation.Abbreviation);
    }

    // MARK: Lyrics clean-up

    public sealed record LyricsSuggestion(LyricsCleanup.Cleaned Cleaned, string CostLine);

    /// <summary>Asks the assistant to label and re-break pasted lyrics. The words are checked; a reply that changed one is refused.</summary>
    /// <exception cref="AssistantException">It couldn't, or the reply changed the words.</exception>
    public async Task<LyricsSuggestion> CleanUpLyricsAsync(string pasted, CancellationToken cancel)
    {
        var reply = await Assistant.Ask(LyricsCleanup.Prepare(pasted), cancel);
        return new LyricsSuggestion(LyricsCleanup.Read(reply.Json, pasted), reply.CostLine);
    }

    // MARK: The scripture spotter

    private SpeechListener? spotterEars;
    private ScriptureSpotter? spotter;
    private IDisposable? spotterSound;
    private DispatcherTimer? spotterClock;
    private CancellationTokenSource? spotterCancel;
    private string? lastSpotShown;

    /// <summary>The passages heard, newest first. The newest waits in the Bible window's box; Enter shows it.</summary>
    public ObservableCollection<Spot> Spots { get; } = [];

    /// <summary>Raised when the spotter starts, stops, hears something or has a new CPU figure.</summary>
    public event Action? SpotterChanged;

    public bool SpotterListening => spotterEars is not null;

    /// <summary>What the spotter is doing, in a line: listening and to what, or why it stopped.</summary>
    public string SpotterStatus { get; private set; } = "";

    /// <summary>The last sentence the recognizer heard (so the operator can see it is listening, and how well).</summary>
    public string SpotterHeard { get; private set; } = "";

    /// <summary>The share of ONE processor core speech recognition used over the last two seconds (0–100).</summary>
    public double SpotterCpu { get; private set; }

    /// <summary>The processor time recognition has used since it started.</summary>
    public TimeSpan SpotterCpuTotal => spotterEars?.CpuTotal ?? TimeSpan.Zero;

    /// <summary>How many sentences this session were sent to the assistant (allusions only).</summary>
    public int SpotterAsked => spotter?.Asked ?? 0;

    /// <summary>
    /// Starts listening for Bible references on the chosen sound input. The sound goes to a
    /// speech process on this PC and nowhere else.
    /// </summary>
    /// <param name="testSound">
    /// The self-test's stand-in for the sound card: it is given the place to send sound blocks
    /// (channels, frames, sample rate) and plays a recording into it at the speed of speech.
    /// </param>
    public async Task StartSpotterAsync(Func<Action<float[][], int, int>, IDisposable>? testSound = null)
    {
        StopSpotter();
        var bible = await AssistantBible();
        if (SpotterListening) return; // started again while the Bible loaded
        var dispatcher = Application.Current.Dispatcher;
        spotterCancel = new CancellationTokenSource();
        // Allusions go to the assistant only while it's usable; the spotter asks afresh each time,
        // so turning the switch off mid-service stops the questions at once.
        spotter = new ScriptureSpotter(() => bible, (request, cancel) => Assistant.Ask(request, cancel));
        spotter.Spotted += spot => dispatcher.BeginInvoke(() => OnSpotted(spot));
        var ears = new SpeechListener(Environment.ProcessPath!);
        var mine = spotter;
        var cancel = spotterCancel.Token;
        ears.Heard += heard =>
        {
            mine.Hear(heard, cancel);
            if (!heard.FromGrammar) dispatcher.BeginInvoke(() => SpotterSays(heard: heard.Text));
        };
        ears.Ready += language => dispatcher.BeginInvoke(() => SpotterSays(status: $"Listening ({language}, on this PC only)"));
        ears.Failed += problem => dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(spotterEars, ears)) StopSpotter(problem);
        });
        ears.Ended += () => dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(spotterEars, ears)) StopSpotter("The speech recognizer stopped.");
        });
        try
        {
            ears.Start();
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ears.Dispose();
            SpotterSays(status: "The speech recognizer couldn't start.");
            return;
        }
        spotterEars = ears;
        string? problem = null;
        spotterSound = testSound is not null
            ? testSound(ears.Feed)
            : OpenSpotterInput((buffer, _) => ears.Feed(buffer.Data, buffer.FrameLength, buffer.SampleRate), out problem);
        if (spotterSound is null)
        {
            StopSpotter($"Can't listen: {problem ?? "no sound input was found"}.");
            return;
        }
        spotterClock = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) =>
        {
            SpotterCpu = ears.SampleCpu();
            SpotterChanged?.Invoke();
        }, dispatcher);
        SpotterSays(status: "Starting Windows' speech recognizer…");
    }

    // The chosen input (an input already set up for streaming is shared, not opened twice).
    private IDisposable? OpenSpotterInput(Action<PcmBuffer, double> consumer, out string? problem)
    {
        problem = null;
        var uid = Settings.SpotterInputUid;
        if (uid is null || AudioDeviceList.InputDevice(uid) is null) uid = AudioDeviceList.InputDevices().FirstOrDefault()?.Uid;
        if (uid is null) return null;
        if (AudioInputs.ForUid(uid) is { } entry && AudioInputs.Source(entry.Id) is { } shared) return shared.Open(consumer, out problem);
        return new AudioInputSource(uid, InputChannelSelection.Default).Open(consumer, out problem);
    }

    public void StopSpotter(string? why = null)
    {
        spotterClock?.Stop();
        spotterClock = null;
        spotterCancel?.Cancel();
        spotterCancel = null;
        spotterSound?.Dispose();
        spotterSound = null;
        spotterEars?.Dispose();
        spotterEars = null;
        SpotterCpu = 0;
        SpotterSays(status: why ?? "");
    }

    private void SpotterSays(string? status = null, string? heard = null)
    {
        if (status is not null) SpotterStatus = status;
        if (heard is not null) SpotterHeard = heard;
        SpotterChanged?.Invoke();
    }

    // A passage was heard. It is put in the Bible window's box, ready, unless the operator is in
    // the middle of something of their own there; it is never shown. Enter (the operator's) shows it.
    private void OnSpotted(Spot spot)
    {
        foreach (var gone in Spots.Where(s => spotter?.Spots.Contains(s) == false).ToList()) Spots.Remove(gone);
        Spots.Insert(0, spot);
        while (Spots.Count > 8) Spots.RemoveAt(Spots.Count - 1);
        if (Scripture.Query.Trim().Length == 0 || Scripture.Query == lastSpotShown) UseSpot(spot);
        SpotterChanged?.Invoke();
    }

    /// <summary>Puts a heard passage in the Bible window's box (a click on it in the list). Showing it is still Enter.</summary>
    public void UseSpot(Spot spot)
    {
        lastSpotShown = spot.Display;
        Scripture.Query = spot.Display;
    }
}
