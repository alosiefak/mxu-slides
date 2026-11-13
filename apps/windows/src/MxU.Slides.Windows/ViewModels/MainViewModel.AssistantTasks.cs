using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MxU.Slides.Assistant;
using MxU.Slides.Assistant.Spotting;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.LocalApi;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.Rendering;

namespace MxU.Slides.Windows.ViewModels;

// The assistant's second half (Phase 12, stage 2): the order of service, typed requests, the
// service report, slide checks, translation, live captions and the sermon summary. The same three
// rules as MainViewModel.Assistant.cs hold: the AI suggests and a person decides; nothing here
// needs the internet to keep the show going; scripture and lyrics never come from the AI.
// Each feature's thinking is in MxU.Slides.Assistant (no WPF there); this file gives it the
// library's data, runs it, and saves what the operator accepts. The window is
// Views/Assistant/AssistantWindow.cs.
public sealed partial class MainViewModel
{
    // MARK: What the assistant may look up, and what it may ask the app to do

    // The kinds worth looking up by name. (Stream presets hold ingest keys; stage layouts and
    // triggers aren't things an operator asks for by name mid-service.)
    private static readonly ApiDocumentKind[] AssistantKinds =
    [
        ApiDocumentKind.Presentations, ApiDocumentKind.Services, ApiDocumentKind.Themes, ApiDocumentKind.Media, ApiDocumentKind.Audio,
        ApiDocumentKind.Playlists, ApiDocumentKind.Overlays, ApiDocumentKind.AlertPresets, ApiDocumentKind.OutputPresets, ApiDocumentKind.ActionCombos,
    ];

    /// <summary>
    /// A snapshot of the library's names and ids, the open service and the timers, for the
    /// assistant's look-ups. It reads through the Local API's own bridge, so the assistant sees
    /// exactly what a remote control sees. Call on the UI thread, just before a job.
    /// </summary>
    public LibraryFacts AssistantFacts()
    {
        IApiBridge bridge = this;
        var entries = new List<LibraryFacts.Entry>();
        foreach (var kind in AssistantKinds)
            entries.AddRange(bridge.LibraryEntries(kind).Select(e => new LibraryFacts.Entry(e.Id, kind.Raw(), e.Name, e.Folder)));
        entries.AddRange(bridge.Timers().Select(t => new LibraryFacts.Entry(t.Id, "timers", t.Name)));

        var service = new StringBuilder();
        if (SelectedService is { } open)
        {
            service.Append(CultureInfo.InvariantCulture, $"The service “{open.Name}” ({open.ServiceDate}). Rows in order:\n");
            var number = 0;
            foreach (var row in RunOfShow(open))
            {
                number++;
                if (row.ItemKind == ServiceItemKind.Header)
                {
                    service.Append(CultureInfo.InvariantCulture, $"{number}. header | {row.Name}\n");
                    continue;
                }
                entries.Add(new LibraryFacts.Entry(row.Id, "service-items", row.Name));
                service.Append(CultureInfo.InvariantCulture, $"{number}. {row.Name} | serviceItemId {row.Id} | {ServiceItemKindJsonConverter.ToRaw(row.ItemKind)}");
                if (row.Duration is > 0 and var seconds) service.Append(CultureInfo.InvariantCulture, $" | planned {ServiceReport.Clock(TimeSpan.FromSeconds(seconds))}");
                if (row.Id == ServiceTiming.CurrentServiceItemId) service.Append(" | CURRENT (on screen now)");
                service.Append('\n');
            }
        }
        return new LibraryFacts(entries, service.ToString().TrimEnd());
    }

    /// <summary>
    /// The actions a typed request may turn into: every route of the Local API's table that
    /// controls the show (a POST with the "control" scope: fire, clear, advance, timers…). Routes
    /// that edit or delete documents are left out. Running one calls the route's own handler, as
    /// a remote would, with the control scope and no more.
    /// </summary>
    public IReadOnlyList<ActionRoute> AssistantRoutes() =>
        [.. ApiRouteTable.Build(this)
            .Where(route => route is { Method: "POST", Scope: ApiScope.Control })
            .Select(route => new ActionRoute(
                route.OperationId, route.Summary, OpenApiDocument.PathParameters(route.Path), OpenApiDocument.RequestSchema(route),
                (path, body) => route.Handler(new ApiRequestContext { PathParameters = path, Body = body, Scope = ApiScope.Control })))];

    // MARK: Typed requests

    /// <summary>
    /// Turns what the operator typed into a ready action (or words saying why not). Nothing is
    /// done: the caller shows the action and runs it on Enter.
    /// </summary>
    /// <exception cref="AssistantException">It couldn't (the message is for the operator).</exception>
    public async Task<TypedRequests.Outcome> AskTypedRequestAsync(string typed, CancellationToken cancel)
    {
        var (request, held) = TypedRequests.Prepare(typed, AssistantRoutes(), AssistantFacts());
        var reply = await Assistant.Ask(request, cancel);
        return TypedRequests.Read(reply.Json, held);
    }

    // MARK: Order of service

    public sealed record ServicePlanResult(ServicePlanReader.Plan Plan, string? CostLine, string? PrivacyWarning);

    /// <summary>The words of a bulletin file (.docx, .pdf, .txt… the files a note can be imported from); null when it can't be read.</summary>
    public static async Task<string?> ReadPlanFileAsync(string path) => (await Task.Run(() => ReadNoteFile(path, null)))?.PlainText;

    public string ServicePlanCostEstimate(string text) =>
        AssistantCost.Words(AssistantCost.Estimate(Assistant.Model, text.Length + 1800, 300 + text.Split('\n').Length * 30));

    /// <summary>Reads pasted text into a plan with no AI: one line, one row.</summary>
    public ServicePlanResult ReadServicePlan(string text) =>
        new(ServicePlanReader.ReadPlainly(text, Library.All<Presentation>()), null, null);

    /// <summary>Asks the assistant to read the text into a plan. Songs are then matched to the library here.</summary>
    /// <exception cref="AssistantException">It couldn't.</exception>
    public async Task<ServicePlanResult> ReadServicePlanWithAiAsync(string text, CancellationToken cancel)
    {
        var prepared = ServicePlanReader.Prepare(text, AssistantFacts());
        var reply = await Assistant.Ask(prepared.Request, cancel);
        return new ServicePlanResult(ServicePlanReader.Read(reply.Json, Library.All<Presentation>()), reply.CostLine, prepared.PrivacyWarning);
    }

    /// <summary>The operator's "Add as a service": saves the plan as an ordinary service and opens it.</summary>
    public Service AddServicePlan(ServicePlanReader.Plan plan)
    {
        var service = ServicePlanReader.Build(plan, DateOnly.FromDateTime(DateTime.Now));
        Library.Save(service);
        RefreshServices(keepSelection: true);
        if (Services.FirstOrDefault(s => s.Id == service.Id) is { } added) SelectedService = added;
        Notice = $"Added the service “{service.Name}”: {ServicePlanReader.Summary(plan)}.";
        return service;
    }

    // MARK: The service report

    private ServiceRunLog? serviceRuns;
    private string? serviceRunId;
    private string? serviceRunService;

    /// <summary>The running record of services on this PC (in the local data folder, not the library).</summary>
    public ServiceRunLog ServiceRuns => serviceRuns ??= new ServiceRunLog(Path.Combine(AppPaths.LocalRoot, "Assistant", "service-runs.jsonl"));

    // A row became the current one. Service tracking tells us; this only writes it down.
    private void RecordServiceFire(Service service, string itemId, DateTimeOffset now)
    {
        if (serviceRunId is null || serviceRunService != service.Id)
        {
            serviceRunId = Guid.NewGuid().ToString("N");
            serviceRunService = service.Id;
        }
        var rows = RunOfShow(service);
        var index = rows.FindIndex(r => r.Id == itemId);
        if (index < 0) return;
        var section = rows.Take(index).LastOrDefault(r => r.ItemKind == ServiceItemKind.Header)?.Name;
        ServiceRuns.Add(new ServiceRunEntry(serviceRunId, service.Id, service.Name, now, itemId, rows[index].Name, section, rows[index].Duration));
    }

    // The operator cleared the service's timers: that is the end of the service, as near as the app knows it.
    private void RecordServiceEnd(DateTimeOffset now)
    {
        if (serviceRunId is { } run && serviceRunService is { } id)
            ServiceRuns.Add(new ServiceRunEntry(run, id, Library.Get<Service>(id)?.Name ?? "", now, null, null, null, null));
        ForgetServiceRun();
    }

    private void ForgetServiceRun() => (serviceRunId, serviceRunService) = (null, null);

    /// <summary>
    /// The report for one sitting from <see cref="ServiceRunLog.Runs"/>: no AI. A sitting still
    /// going is measured up to now; one that ended without the timers being cleared has no length
    /// for its last row.
    /// </summary>
    public ServiceReport.Report? ServiceReportFor(IReadOnlyList<ServiceRunEntry> run)
    {
        if (run.Count == 0) return null;
        var planned = Library.Get<Service>(run[0].ServiceId) is { } service
            ? RunOfShow(service).Where(r => r.ItemKind != ServiceItemKind.Header).Select(r => r.Name).ToList()
            : [];
        return ServiceReport.Build(run, planned, run[0].Run == serviceRunId ? DateTimeOffset.Now : null);
    }

    public sealed record ReportNotes(IReadOnlyList<string> Notes, string CostLine);

    /// <summary>The optional AI part of the report: up to five notes for next week. Only the report's text is sent.</summary>
    /// <exception cref="AssistantException">It couldn't.</exception>
    public async Task<ReportNotes> ServiceReportNotesAsync(ServiceReport.Report report, CancellationToken cancel)
    {
        var reply = await Assistant.Ask(ServiceReport.NotesRequest(report), cancel);
        return new ReportNotes(ServiceReport.ReadNotes(reply.Json), reply.CostLine);
    }

    // MARK: Slide checks

    /// <summary>
    /// Checks every slide of a deck for readability, with no AI. For each slide the app builds
    /// its picture twice over: once as data (which text boxes, where, what colour) and once drawn
    /// small without its words, to measure the colour and busyness behind each box.
    /// </summary>
    public async Task<List<SlideChecks.Finding>> CheckSlidesAsync(Presentation deck)
    {
        var findings = new List<SlideChecks.Finding>();
        for (var index = 0; index < deck.Slides.Count; index++)
        {
            var slide = deck.Slides[index];
            var scene = SceneOf(deck, slide);
            var canvas = scene.CanvasSize;
            var words = scene[LayerKind.Slide].Items
                .Where(item => item.Content is ItemContent.Text { Styled: var styled } && styled.PathData is null && styled.TickerSpeed == 0 && SlideSceneBuilder.HasWords(styled.String))
                .ToList();
            if (words.Count == 0) continue;
            // The slide without its words: what is behind them.
            foreach (var layer in scene.Layers) layer.Items.RemoveAll(item => item.Content is ItemContent.Text);
            var behind = await RenderSceneAsync(scene, 480);
            var scale = behind.PixelWidth / canvas.Width;
            var boxes = new List<SlideChecks.TextBox>();
            foreach (var item in words)
            {
                var styled = ((ItemContent.Text)item.Content).Styled;
                var id = item.Id.EndsWith("::text", StringComparison.Ordinal) ? item.Id[..^"::text".Length] : item.Id;
                var source = slide.Objects.FirstOrDefault(o => o.Id == id);
                var (average, spread) = SampleBehind(behind, item.Frame, scale);
                boxes.Add(new SlideChecks.TextBox(
                    id, styled.String, item.Frame, styled.Color, styled.FontSize, average, spread,
                    Lifted: styled.Shadow is not null || styled.Outline is not null || styled.LineFill is not null,
                    Overflows: styled.Scroll is null && TextLayout.OverflowsFrame(styled, new SceneSize(item.Frame.Width, item.Frame.Height)),
                    AutoShrink: styled.AutoShrink,
                    OwnFrame: source is { X: not null, Y: not null, Width: not null, Height: not null },
                    OwnStyle: source?.TextStyle is not null));
            }
            findings.AddRange(SlideChecks.Check(slide.Id, index + 1, canvas, boxes));
        }
        return findings;
    }

    // Draws a scene offscreen at a small size, as an output would draw it (videos as their still).
    private async Task<RenderTargetBitmap> RenderSceneAsync(RenderScene scene, int width)
    {
        var height = Math.Max(1, (int)Math.Round(width * scene.CanvasSize.Height / scene.CanvasSize.Width));
        var view = new SceneView { Media = Media, PlaysVideo = false, Width = width, Height = height };
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.Present(scene, SceneTransition.Cut);
        // Let the pictures load and the layout settle before taking the picture.
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        return bitmap;
    }

    private static (SceneColor? Average, double Spread) SampleBehind(BitmapSource picture, SceneRect frame, double scale)
    {
        var x = Math.Clamp((int)(frame.X * scale), 0, picture.PixelWidth - 1);
        var y = Math.Clamp((int)(frame.Y * scale), 0, picture.PixelHeight - 1);
        var width = Math.Clamp((int)(frame.Width * scale), 0, picture.PixelWidth - x);
        var height = Math.Clamp((int)(frame.Height * scale), 0, picture.PixelHeight - y);
        if (width < 2 || height < 2) return (null, 0);
        var pixels = new byte[width * height * 4];
        picture.CopyPixels(new Int32Rect(x, y, width, height), pixels, width * 4, 0);
        var (average, spread) = SlideChecks.Sample(pixels);
        return (average, spread);
    }

    /// <summary>
    /// A finding's Fix, where it has one: the box moved back onto the slide, or shrink-to-fit
    /// turned on for a box with its own style. Saves the deck. False when the slide has changed
    /// since the check and the fix no longer applies.
    /// </summary>
    public bool FixSlideFinding(Presentation deck, SlideChecks.Finding finding)
    {
        var target = CurrentPresentation?.Id == deck.Id ? CurrentPresentation! : Library.Get<Presentation>(deck.Id);
        if (target?.Slides.FirstOrDefault(s => s.Id == finding.SlideId) is not { } slide
            || slide.Objects.FirstOrDefault(o => o.Id == finding.ObjectId) is not { } box) return false;
        switch (finding.Fix)
        {
            case SlideChecks.Fix.MoveOntoSlide when box is { X: not null, Y: not null, Width: not null, Height: not null }:
                var moved = SlideChecks.OntoSlide(new SceneRect(box.X.Value, box.Y.Value, box.Width.Value, box.Height.Value), SlideSceneBuilder.CanvasSizeFor(target));
                (box.X, box.Y, box.Width, box.Height) = (moved.X, moved.Y, moved.Width, moved.Height);
                break;
            case SlideChecks.Fix.ShrinkToFit when box.TextStyle is { } style:
                style.AutoShrink = true;
                break;
            default:
                return false;
        }
        SavePresentation(target);
        if (CurrentPresentation?.Id == target.Id) RebuildSlides();
        return true;
    }

    public sealed record SlideLook(IReadOnlyList<SlideChecks.VisionNote> Notes, string CostLine);

    /// <summary>The slides the assistant would be shown: those with findings first, then the rest in order, up to eight.</summary>
    public static List<int> SlidesToLookAt(Presentation deck, IReadOnlyList<SlideChecks.Finding> findings) =>
        [.. findings.Select(f => f.SlideNumber).Concat(Enumerable.Range(1, deck.Slides.Count)).Distinct().Take(SlideChecks.MostPictures).Order()];

    /// <summary>A picture is about 500 tokens at this size; the guess is shown before anything is sent.</summary>
    public string SlideLookCostEstimate(int pictures) =>
        AssistantCost.Words(new AssistantUsage(pictures * 500 + 300, 60 * pictures).Cost(Assistant.Model));

    /// <summary>The optional second look: pictures of up to eight slides go to the assistant, which answers with notes.</summary>
    /// <exception cref="AssistantException">It couldn't.</exception>
    public async Task<SlideLook> LookAtSlidesAsync(Presentation deck, IReadOnlyList<int> numbers, CancellationToken cancel)
    {
        var pictures = new List<byte[]>();
        foreach (var number in numbers)
        {
            var bitmap = await RenderSceneAsync(SceneOf(deck, deck.Slides[number - 1]), 640);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var png = new MemoryStream();
            encoder.Save(png);
            pictures.Add(png.ToArray());
        }
        var reply = await Assistant.Ask(SlideChecks.VisionRequest(pictures, numbers), cancel);
        return new SlideLook(SlideChecks.ReadVision(reply.Json, numbers), reply.CostLine);
    }

    // MARK: Translation

    public sealed record DeckTranslation(SlideTranslator.Translated Made, string CostLine);

    /// <summary>A guess at the cost of translating a deck, or why it can't be; shown before the button is pressed.</summary>
    public string TranslateEstimate(Presentation deck, string language)
    {
        var prepared = SlideTranslator.Prepare(deck, language);
        if (prepared.Request is not { } request) return prepared.Refused ?? "";
        var letters = request.Ask.Length;
        return $"Sends {prepared.Pieces.Count} piece{(prepared.Pieces.Count == 1 ? "" : "s")} of text to Anthropic: {AssistantCost.Words(AssistantCost.Estimate(Assistant.Model, letters + 1200, letters / 2 + 100))}."
            + (prepared.ScriptureSlides.Count > 0 ? $" Scripture (slide{(prepared.ScriptureSlides.Count == 1 ? "" : "s")} {string.Join(", ", prepared.ScriptureSlides)}) is not sent." : "");
    }

    /// <summary>
    /// Makes a second-language copy of a deck and saves it, marked, under "Translations". The
    /// original isn't touched, and the copy isn't shown or added to a service: the operator does that.
    /// </summary>
    /// <exception cref="AssistantException">It couldn't, or the deck is one that isn't translated (a song).</exception>
    public async Task<DeckTranslation> TranslateDeckAsync(Presentation deck, string language, CancellationToken cancel)
    {
        var prepared = SlideTranslator.Prepare(deck, language);
        if (prepared.Request is not { } request) throw new AssistantException(AssistantProblem.Other, prepared.Refused ?? AssistantException.Words(AssistantProblem.Other));
        var reply = await Assistant.Ask(request, cancel);
        var made = SlideTranslator.Apply(deck, prepared, reply.Json, language, DateTimeOffset.Now);
        if (made.Pieces == 0) throw new AssistantException(AssistantProblem.BadReply, AssistantException.Words(AssistantProblem.BadReply));
        Library.Save(made.Copy);
        RefreshLibrary();
        Notice = $"Saved “{made.Copy.Name}” in {SlideTranslator.Folder}.";
        return new DeckTranslation(made, reply.CostLine);
    }

    // MARK: Live captions

    private SpeechListener? captionEars;
    private IDisposable? captionSound;
    private readonly CaptionRoll captionRoll = new();
    private CancellationTokenSource? captionCancel;
    private int captionsWaiting;
    private decimal captionCost;

    /// <summary>Raised when captions start, stop or show a new line.</summary>
    public event Action? CaptionsChanged;

    public bool CaptionsOn => captionEars is not null;

    public string CaptionsStatus { get; private set; } = "";

    /// <summary>The caption on screen now.</summary>
    public string CaptionsLine => captionRoll.Current;

    /// <summary>
    /// Starts live captions on the spotter's sound input: the operator's switch. Each sentence
    /// Windows' recognizer hears (on this PC) goes up as a live alert on the chosen screens, in
    /// place of the last. While another alert is up (a nursery call), captions wait for it.
    /// </summary>
    /// <param name="testSound">The self-test's stand-in for the sound card, as for the spotter.</param>
    public void StartCaptions(Func<Action<float[][], int, int>, IDisposable>? testSound = null)
    {
        StopCaptions();
        var dispatcher = Application.Current.Dispatcher;
        var ears = new SpeechListener(Environment.ProcessPath!);
        captionCancel = new CancellationTokenSource();
        var cancel = captionCancel.Token;
        ears.Heard += heard =>
        {
            // Free dictation only: the references-only listener hears the same words again.
            if (!heard.FromGrammar) dispatcher.BeginInvoke(() => OnCaptionHeard(ears, heard.Text, cancel));
        };
        ears.Ready += language => dispatcher.BeginInvoke(() => CaptionsSay($"Captions on ({language}, heard on this PC only)"));
        ears.Failed += problem => dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(captionEars, ears)) StopCaptions(problem);
        });
        ears.Ended += () => dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(captionEars, ears)) StopCaptions("The speech recognizer stopped.");
        });
        try
        {
            ears.Start();
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ears.Dispose();
            CaptionsSay("The speech recognizer couldn't start.");
            return;
        }
        captionEars = ears;
        string? problem = null;
        captionSound = testSound is not null
            ? testSound(ears.Feed)
            : OpenSpotterInput((buffer, _) => ears.Feed(buffer.Data, buffer.FrameLength, buffer.SampleRate), out problem);
        if (captionSound is null)
        {
            StopCaptions($"Can't listen: {problem ?? "no sound input was found"}.");
            return;
        }
        captionCost = 0;
        CaptionsSay("Starting Windows' speech recognizer…");
    }

    public void StopCaptions(string? why = null)
    {
        captionCancel?.Cancel();
        captionCancel = null;
        captionSound?.Dispose();
        captionSound = null;
        captionEars?.Dispose();
        captionEars = null;
        captionsWaiting = 0;
        captionRoll.Clear();
        if (Show.LiveAlert?.Id == Captions.AlertId) DismissAlert();
        CaptionsSay(why ?? "");
    }

    private void CaptionsSay(string status)
    {
        CaptionsStatus = status;
        CaptionsChanged?.Invoke();
    }

    private async void OnCaptionHeard(SpeechListener ears, string heard, CancellationToken cancel)
    {
        if (!ReferenceEquals(captionEars, ears) || Captions.Tidy(heard) is not { Length: > 0 } sentence) return;
        var shown = sentence;
        // Translation, when chosen and possible. A sentence naming someone to pray for is never
        // sent, and when the assistant is behind (three waiting) the sentence goes up as heard.
        if (Settings.CaptionsLanguage is { Length: > 0 } language && Assistant.IsReady && Captions.MaySend(sentence) && captionsWaiting < 3)
        {
            captionsWaiting++;
            try
            {
                var reply = await Assistant.Ask(Captions.TranslateRequest(sentence, language), cancel);
                captionCost += reply.Usage.Cost(reply.Model);
                shown = Captions.ReadTranslation(reply.Json, sentence) ?? sentence;
            }
            catch (AssistantException)
            {
                // No internet, busy, cancelled: the caption is shown as it was heard.
            }
            finally
            {
                captionsWaiting = Math.Max(0, captionsWaiting - 1);
            }
            if (!ReferenceEquals(captionEars, ears)) return; // stopped while it was away
        }
        var line = captionRoll.Add(shown);
        // Another alert (not ours) is on screen: leave it there. The roll still moves on.
        if (Show.LiveAlert is null || Show.LiveAlert.Id == Captions.AlertId)
        {
            var target = Settings.CaptionsTarget switch { "audience" => AlertTarget.Audience, "both" => AlertTarget.Both, _ => AlertTarget.Confidence };
            FireAlert(Captions.AlertId, line, AlertBehavior.Persist, target);
        }
        CaptionsStatus = captionCost > 0 ? $"Captions on · translation so far {AssistantCost.Words(captionCost)}" : CaptionsStatus;
        CaptionsChanged?.Invoke();
    }

    // MARK: The sermon summary

    /// <summary>
    /// Transcribes a recording on this PC. The file's sound is decoded by Windows (Media
    /// Foundation, so most audio and video files work) into a temporary 16 kHz file, which the
    /// speech worker reads through. Nothing leaves the computer.
    /// </summary>
    /// <param name="progress">Called on the UI thread with how far through it is, 0 to 1.</param>
    /// <exception cref="InvalidOperationException">The file couldn't be read, or Windows has no speech pack (the message says which).</exception>
    public async Task<IReadOnlyList<SermonSummary.Heard>> TranscribeRecordingAsync(string path, Action<double> progress, CancellationToken cancel)
    {
        var wav = Path.Combine(Path.GetTempPath(), $"MxU sermon {Guid.NewGuid():N}.wav");
        SpeechListener? ears = null;
        try
        {
            var length = await Task.Run(() => SermonSound.ToSpeechWav(path, wav, cancel), cancel);
            var heard = new List<SermonSummary.Heard>();
            var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var dispatcher = Application.Current.Dispatcher;
            ears = new SpeechListener(Environment.ProcessPath!);
            ears.Heard += said =>
            {
                lock (heard) heard.Add(new SermonSummary.Heard(said.At, said.Text));
                dispatcher.BeginInvoke(() => progress(length > TimeSpan.Zero ? Math.Clamp(said.At / length, 0, 1) : 0));
            };
            ears.Failed += problem => done.TrySetResult(problem);
            ears.Ended += () => done.TrySetResult(null);
            ears.Start(wav);
            await using var stop = cancel.Register(() => done.TrySetCanceled(cancel));
            if (await done.Task is { } failed) throw new InvalidOperationException(failed);
            lock (heard) return [.. heard.OrderBy(h => h.At)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException($"That file's sound couldn't be read: {e.Message}", e);
        }
        finally
        {
            ears?.Dispose();
            try { File.Delete(wav); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    public sealed record SermonResult(SermonSummary.Summary Summary, string CostLine, string? PrivacyWarning);

    public string SermonCostEstimate(IReadOnlyList<SermonSummary.Heard> transcript) =>
        AssistantCost.Words(AssistantCost.Estimate(Assistant.Model, transcript.Sum(h => h.Text.Length + 14) + 2000, 900));

    /// <summary>Sends the transcript's text (never the sound) for a summary, quotes and post drafts. Quotes are checked against the transcript.</summary>
    /// <exception cref="AssistantException">It couldn't.</exception>
    public async Task<SermonResult> SummariseSermonAsync(IReadOnlyList<SermonSummary.Heard> transcript, CancellationToken cancel)
    {
        var prepared = SermonSummary.Prepare(transcript);
        var reply = await Assistant.Ask(prepared.Request, cancel);
        return new SermonResult(SermonSummary.Read(reply.Json, transcript), reply.CostLine, prepared.PrivacyWarning);
    }
}
