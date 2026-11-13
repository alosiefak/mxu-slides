using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using MxU.Slides.Assistant;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;
using MxU.Slides.LocalApi;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views.Assistant;

/// <summary>
/// The Assistant window (the toolbar's Assistant button): seven jobs, picked from the list on the
/// left. Built in code, like the other tool windows. It only draws; the work is in
/// <c>MainViewModel.AssistantTasks.cs</c> and the <c>MxU.Slides.Assistant</c> project.
/// <para>
/// Two things are true of every page. A button that needs the assistant is greyed, with the
/// reason as its tooltip, when there is no key or the switch is off; the buttons that need no AI
/// (Read it, the service report, Check the slides, Transcribe) always work. And nothing here puts
/// anything on a screen or saves a document without a press: the typed request waits for Enter,
/// the plan for "Add as a service", a fix for its Fix button, captions for their switch.
/// </para>
/// The <c>…Now</c> methods are what the buttons call; the self-test calls them too.
/// </summary>
public sealed class AssistantWindow : Window
{
    public const string Ask = "Ask", Plan = "Order of service", Checks = "Slide checks", Translate = "Translate",
        CaptionsPage = "Live captions", Report = "Service report", Sermon = "Sermon summary";

    private static AssistantWindow? open;

    private readonly MainViewModel model;
    private readonly ListBox pages = new() { Width = 170, Margin = new Thickness(0, 0, 14, 0), BorderThickness = new Thickness(0) };
    private readonly ContentControl host = new();
    private readonly Dictionary<string, FrameworkElement> built = [];
    private readonly List<Button> aiButtons = [];
    private CancellationTokenSource? job;

    private AssistantWindow(Window owner, MainViewModel model)
    {
        this.model = model;
        EditorKit.Dress(this, owner, "Assistant", 980, 680);
        foreach (var name in new[] { Ask, Plan, Checks, Translate, CaptionsPage, Report, Sermon }) pages.Items.Add(name);
        pages.SelectionChanged += (_, _) => Draw();
        var grid = new DockPanel();
        DockPanel.SetDock(pages, Dock.Left);
        grid.Children.Add(pages);
        grid.Children.Add(new ScrollViewer { Content = host, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 8, 0) });
        Content = new Border { Padding = new Thickness(16), Child = grid };
        model.AssistantChanged += DrawAi;
        model.CaptionsChanged += DrawCaptions;
        Closed += (_, _) =>
        {
            model.AssistantChanged -= DrawAi;
            model.CaptionsChanged -= DrawCaptions;
            job?.Cancel();
            if (ReferenceEquals(open, this)) open = null;
        };
        pages.SelectedIndex = 0;
    }

    /// <summary>Opens the window (one at a time), on a page if named.</summary>
    public static AssistantWindow Open(Window owner, MainViewModel model, string? page = null)
    {
        if (open is null)
        {
            open = new AssistantWindow(owner, model);
            open.Show();
        }
        open.Activate();
        if (page is not null) open.Go(page);
        return open;
    }

    public void Go(string page) => pages.SelectedItem = page;

    private void Draw()
    {
        if (pages.SelectedItem is not string page) return;
        if (!built.TryGetValue(page, out var content))
        {
            built[page] = content = page switch
            {
                Ask => AskPage(), Plan => PlanPage(), Checks => ChecksPage(), Translate => TranslatePage(),
                CaptionsPage => CaptionPage(), Report => ReportPage(), _ => SermonPage(),
            };
            DrawAi();
        }
        host.Content = content;
        if (page == Ask) askBox.Focus();
        if (page == Report) FillReports();
        if (page is Checks or Translate) DrawDeck();
    }

    // MARK: Shared pieces

    private static StackPanel Page(string title, string about)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(EditorKit.Muted(about, 12, 12));
        return panel;
    }

    private static TextBlock Line(double top = 8) => new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, top, 0, 0) };

    private static TextBox Paper(double height, bool readOnly = false) => new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = height,
        IsReadOnly = readOnly, Padding = new Thickness(8), FontSize = 13, VerticalContentAlignment = VerticalAlignment.Top,
        FontFamily = readOnly ? new FontFamily("Cascadia Mono, Consolas") : SystemFonts.MessageFontFamily,
    };

    private static StackPanel Buttons(params UIElement[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var button in buttons)
        {
            if (button is FrameworkElement element) element.Margin = new Thickness(0, 0, 8, 0);
            row.Children.Add(button);
        }
        return row;
    }

    // A button that sends something to Anthropic: greyed, with the reason, when the assistant can't be used.
    private Button AiButton(string text, Func<Task> click, string tooltip, bool accent = false)
    {
        var button = EditorKit.Button(text, async () => await click(), tooltip, accent);
        button.Tag = tooltip;
        aiButtons.Add(button);
        return button;
    }

    private void DrawAi()
    {
        var why = model.AssistantUnavailable;
        foreach (var button in aiButtons)
        {
            button.IsEnabled = why is null && !model.Assistant.IsBusy;
            button.ToolTip = why ?? button.Tag;
            ToolTipService.SetShowOnDisabled(button, true);
        }
        if (askHint is not null) askHint.Text = why ?? "Type what you want and press Enter. The assistant makes it ready; Enter again does it, Esc cancels.";
    }

    // Runs a job for a page: its words go to the page's line; a failure is said calmly; buttons grey while it runs.
    private async Task Run(TextBlock line, string busy, Func<CancellationToken, Task<string>> work)
    {
        job?.Cancel();
        job = new CancellationTokenSource();
        var mine = job;
        line.Foreground = EditorKit.Res<Brush>("MutedText");
        line.Text = busy;
        foreach (var button in aiButtons) button.IsEnabled = false;
        try
        {
            line.Text = await work(mine.Token);
            line.Foreground = EditorKit.Res<Brush>("Text");
        }
        catch (AssistantException e)
        {
            line.Foreground = EditorKit.Res<Brush>("Warn");
            line.Text = e.IsQuiet && e.Problem != AssistantProblem.Cancelled ? e.Message : $"{e.Message} Nothing was changed.";
        }
        finally
        {
            if (ReferenceEquals(job, mine)) job = null;
            DrawAi();
        }
    }

    private static void Copy(string text)
    {
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.COMException) { } // another program holds the clipboard
    }

    private void SaveText(string name, string text)
    {
        var dialog = new SaveFileDialog { FileName = name, Filter = "Text|*.txt", DefaultExt = ".txt" };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, text); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { model.Notice = $"Couldn't save: {e.Message}"; }
    }

    // MARK: Ask (typed requests)

    private readonly TextBox askBox = new() { FontSize = 18, Padding = new Thickness(10, 7, 10, 7), Tag = "put up the giving lower third · next song · clear the screen" };
    private TextBlock? askHint;
    private readonly TextBlock askLine = Line();
    private readonly Border readyCard = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(2) };
    private readonly TextBlock readySummary = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock readyDetail = new() { FontSize = 11, Margin = new Thickness(0, 4, 0, 8), TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
    private DateTime readySince;

    /// <summary>The action waiting for Enter; null when there is none.</summary>
    public ReadyAction? Ready { get; private set; }

    public TextBox AskBox => askBox;

    private FrameworkElement AskPage()
    {
        var page = Page("Ask", "Type a request in your own words. The assistant finds the matching action in this app (the same ones a remote control has) and holds it ready. It never does anything by itself.");
        page.Children.Add(askBox);
        askHint = EditorKit.Muted("", 12, 0);
        askHint.Margin = new Thickness(2, 6, 0, 0);
        page.Children.Add(askHint);
        readyCard.BorderBrush = EditorKit.Res<Brush>("Accent");
        readyCard.Background = EditorKit.Res<Brush>("AccentWash");
        var card = new StackPanel();
        card.Children.Add(EditorKit.Faint("READY: NOT DONE YET"));
        card.Children.Add(readySummary);
        card.Children.Add(readyDetail);
        card.Children.Add(Buttons(
            EditorKit.Button("Do it  ⏎", async () => await DoReadyNow(), "Carry out the action (Enter)", accent: true),
            EditorKit.Button("Cancel  Esc", CancelReady, "Forget it (Esc)")));
        readyCard.Child = card;
        page.Children.Add(readyCard);
        page.Children.Add(askLine);
        // Enter asks; with an action ready, Enter does it. Esc cancels either.
        PreviewKeyDown += async (_, e) =>
        {
            if (!ReferenceEquals(host.Content, page)) return;
            if (e.Key == Key.Escape && (Ready is not null || job is not null))
            {
                e.Handled = true;
                job?.Cancel();
                CancelReady();
            }
            else if (e.Key == Key.Enter)
            {
                e.Handled = true;
                // A second Enter straight after the first must not run an action nobody has read yet.
                if (Ready is not null && DateTime.UtcNow - readySince > TimeSpan.FromMilliseconds(600)) await DoReadyNow();
                else if (Ready is null && job is null) await AskNow();
            }
        };
        return page;
    }

    /// <summary>Sends what's typed; the answer becomes the ready card (or a line saying why not).</summary>
    public async Task AskNow()
    {
        var typed = askBox.Text.Trim();
        if (typed.Length == 0 || model.AssistantUnavailable is not null) return;
        CancelReady();
        await Run(askLine, "Working out what to do…", async cancel =>
        {
            var outcome = await model.AskTypedRequestAsync(typed, cancel);
            if (outcome.Action is not { } action) return outcome.Words;
            Ready = action;
            readySince = DateTime.UtcNow;
            readySummary.Text = action.Summary;
            readyDetail.Text = $"{action.Route.Summary}\n{action.Detail}";
            readyCard.Visibility = Visibility.Visible;
            return "Enter does it · Esc cancels";
        });
    }

    /// <summary>The operator's Enter: carries out the ready action through the Local API's own handler.</summary>
    public async Task DoReadyNow()
    {
        if (Ready is not { } action) return;
        Ready = null;
        readyCard.Visibility = Visibility.Collapsed;
        try
        {
            await action.Run();
            askLine.Foreground = EditorKit.Res<Brush>("Good");
            askLine.Text = $"Done: {action.Summary}";
            askBox.Clear();
        }
        catch (ApiError e)
        {
            askLine.Foreground = EditorKit.Res<Brush>("Warn");
            askLine.Text = $"The app couldn't do that: {e.Message}";
        }
    }

    public void CancelReady()
    {
        if (Ready is null) return;
        Ready = null;
        readyCard.Visibility = Visibility.Collapsed;
        askLine.Foreground = EditorKit.Res<Brush>("MutedText");
        askLine.Text = "Cancelled. Nothing was done.";
    }

    // MARK: Order of service

    private readonly TextBox planBox = Paper(170);
    private readonly TextBlock planLine = Line();
    private readonly TextBlock planCost = Line(0);
    private readonly StackPanel planRows = new() { Margin = new Thickness(0, 10, 0, 0) };
    private Button? planAdd;
    private ServicePlanReader.Plan? plan;

    public TextBox PlanBox => planBox;

    private FrameworkElement PlanPage()
    {
        var page = Page("Order of service", "Paste a bulletin, an email or your notes (or open a Word or PDF file). You get the rows of a service to review; songs are matched to your library, and a song you don't have is listed, never made up.");
        planBox.Tag = "10:00 Welcome\n10:05 Amazing Grace\nReading: John 3:16-18\nSermon (30 min)";
        planBox.TextChanged += (_, _) => planCost.Text = planBox.Text.Trim().Length == 0 ? "" : $"Read with AI sends this text to Anthropic: {model.ServicePlanCostEstimate(planBox.Text)}.";
        page.Children.Add(planBox);
        planCost.Style = EditorKit.Res<Style>("Muted");
        planAdd = EditorKit.Button("Add as a service", () => AddPlanNow(), "Saves these rows as a new service and opens it", accent: true);
        planAdd.IsEnabled = false;
        page.Children.Add(Buttons(
            EditorKit.Button("Open a file…", async () => await OpenPlanFile(), "A .docx, .pdf, .rtf, .odt or text file"),
            EditorKit.Button("Read it", ReadPlanNow, "No AI: each line becomes a row"),
            AiButton("Read with AI", ReadPlanWithAiNow, "The assistant reads prose and untidy layouts into rows"),
            planAdd));
        page.Children.Add(planCost);
        page.Children.Add(planLine);
        page.Children.Add(planRows);
        return page;
    }

    private async Task OpenPlanFile()
    {
        var dialog = new OpenFileDialog { Filter = "Documents|*.docx;*.pdf;*.rtf;*.odt;*.txt;*.md|All files|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        if (await MainViewModel.ReadPlanFileAsync(dialog.FileName) is { Length: > 0 } text) planBox.Text = text;
        else planLine.Text = "That file couldn't be read as text.";
    }

    /// <summary>The reader with no AI.</summary>
    public void ReadPlanNow()
    {
        if (planBox.Text.Trim().Length == 0) return;
        ShowPlan(model.ReadServicePlan(planBox.Text), "Read on this computer (no AI)");
    }

    public Task ReadPlanWithAiNow() => planBox.Text.Trim().Length == 0 ? Task.CompletedTask : Run(planLine, "The assistant is reading it…", async cancel =>
    {
        var result = await model.ReadServicePlanWithAiAsync(planBox.Text, cancel);
        return ShowPlan(result, $"Read by the assistant ({result.CostLine})");
    });

    private string ShowPlan(MainViewModel.ServicePlanResult result, string how)
    {
        plan = result.Plan;
        planRows.Children.Clear();
        planRows.Children.Add(new TextBlock { Text = plan.Name + (plan.Date is null ? "" : $"  ·  {plan.Date}"), FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 0, 0, 6) });
        foreach (var row in plan.Rows)
        {
            var header = row.Kind == ServicePlanReader.Header;
            var mark = row switch
            {
                { Kind: ServicePlanReader.Header } => "",
                { Song: not null } => "♪  ",
                { Kind: ServicePlanReader.Song } => "⚠  ",
                { Kind: ServicePlanReader.Scripture } => "✝  ",
                _ => "•  ",
            };
            var tail = row switch
            {
                { Song: not null } => "   in your library",
                { Kind: ServicePlanReader.Song, Ambiguous: true } => "   more than one in your library: link it yourself",
                { Kind: ServicePlanReader.Song } => "   not in your library",
                { Kind: ServicePlanReader.Scripture, Reference: null } => "   (reference not recognised)",
                _ => "",
            };
            var line = new TextBlock { Margin = new Thickness(header ? 0 : 12, header ? 8 : 2, 0, 2), FontSize = header ? 11 : 13 };
            line.Inlines.Add(new System.Windows.Documents.Run(mark + (header ? row.Title.ToUpperInvariant() : row.Title)) { FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal });
            if (row.Minutes is { } minutes) line.Inlines.Add(new System.Windows.Documents.Run($"   {minutes} min") { Foreground = EditorKit.Res<Brush>("MutedText") });
            if (tail.Length > 0) line.Inlines.Add(new System.Windows.Documents.Run(tail) { Foreground = EditorKit.Res<Brush>(row.Song is not null ? "Good" : "Warn"), FontSize = 11 });
            planRows.Children.Add(line);
        }
        if (plan.MissingSongs.Count > 0)
            planRows.Children.Add(new TextBlock
            {
                Text = $"Songs to add or link: {string.Join("; ", plan.MissingSongs)}. They become plain rows; no lyrics were made.",
                TextWrapping = TextWrapping.Wrap, Foreground = EditorKit.Res<Brush>("Warn"), Margin = new Thickness(0, 10, 0, 0), FontSize = 12,
            });
        planAdd!.IsEnabled = plan.Rows.Count > 0;
        var words = $"{how}: {ServicePlanReader.Summary(plan)}." + (result.PrivacyWarning is { } warning ? $" {warning}" : "") + (plan.Rows.Count > 0 ? " Nothing is saved until you press Add as a service." : "");
        planLine.Foreground = EditorKit.Res<Brush>("Text");
        return planLine.Text = words;
    }

    /// <summary>The operator's "Add as a service".</summary>
    public Service? AddPlanNow()
    {
        if (plan is not { Rows.Count: > 0 } ready) return null;
        var service = model.AddServicePlan(ready);
        planLine.Text = $"Added “{service.Name}” to your services and opened it.";
        planAdd!.IsEnabled = false;
        plan = null;
        return service;
    }

    // MARK: Slide checks

    private readonly TextBlock deckLine = Line(0);
    private readonly TextBlock translateDeckLine = Line(0);
    private readonly TextBlock checksLine = Line();
    private readonly StackPanel checkRows = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly StackPanel lookRows = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock lookLine = Line();
    private Presentation? checkedDeck;

    /// <summary>What the last check found.</summary>
    public IReadOnlyList<SlideChecks.Finding> Findings { get; private set; } = [];

    private FrameworkElement ChecksPage()
    {
        var page = Page("Slide checks", "Checks the open presentation for things that make a slide hard to read from the back: weak contrast, a busy picture behind words, a lot of text, words cut off or off the edge, small text. This is worked out on this computer; no AI is needed.");
        page.Children.Add(deckLine);
        page.Children.Add(Buttons(
            EditorKit.Button("Check the slides", async () => await CheckNow(), "No AI: measured on this computer", accent: true),
            AiButton("Ask the assistant to look too", LookNow, "Sends pictures of up to eight slides to Anthropic for a second opinion")));
        page.Children.Add(checksLine);
        page.Children.Add(checkRows);
        page.Children.Add(lookLine);
        page.Children.Add(lookRows);
        return page;
    }

    private void DrawDeck()
    {
        var deck = model.CurrentPresentation;
        deckLine.Text = translateDeckLine.Text = deck is null ? "Open a presentation in the main window first." : $"The open presentation: “{deck.Name}” ({deck.Slides.Count} slide{(deck.Slides.Count == 1 ? "" : "s")})";
        DrawTranslateEstimate();
    }

    public async Task CheckNow()
    {
        DrawDeck();
        if (model.CurrentPresentation is not { } deck) return;
        checksLine.Text = "Checking…";
        checkedDeck = deck;
        Findings = await model.CheckSlidesAsync(deck);
        lookRows.Children.Clear();
        lookLine.Text = model.AssistantUnavailable is null
            ? $"A second opinion would send {MainViewModel.SlidesToLookAt(deck, Findings).Count} picture(s) to Anthropic: {model.SlideLookCostEstimate(MainViewModel.SlidesToLookAt(deck, Findings).Count)}."
            : "";
        DrawFindings();
    }

    private void DrawFindings()
    {
        checkRows.Children.Clear();
        var slides = Findings.Select(f => f.SlideNumber).Distinct().Count();
        checksLine.Foreground = EditorKit.Res<Brush>(Findings.Count == 0 ? "Good" : "Text");
        checksLine.Text = checkedDeck is null ? ""
            : Findings.Count == 0 ? $"All {checkedDeck.Slides.Count} slides read well. (Checked on this computer, no AI.)"
            : $"{Findings.Count} thing{(Findings.Count == 1 ? "" : "s")} to look at on {slides} of {checkedDeck.Slides.Count} slides. (Checked on this computer, no AI.)";
        for (var i = 0; i < Findings.Count; i++)
        {
            var finding = Findings[i];
            var index = i;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
            if (finding.Fix is { } fix)
            {
                var button = EditorKit.Button("Fix", () => FixNow(index), fix == SlideChecks.Fix.MoveOntoSlide ? "Moves the box back onto the slide" : "Turns on shrink-to-fit for this box");
                DockPanel.SetDock(button, Dock.Right);
                row.Children.Add(button);
            }
            var label = new TextBlock { Text = $"Slide {finding.SlideNumber}", Width = 62, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(label, Dock.Left);
            row.Children.Add(label);
            row.Children.Add(new TextBlock { Text = finding.Message, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            checkRows.Children.Add(EditorKit.Card(row, 4));
        }
    }

    /// <summary>A finding's Fix button. The slides are checked again afterwards.</summary>
    public bool FixNow(int index)
    {
        if (checkedDeck is not { } deck || index < 0 || index >= Findings.Count || !model.FixSlideFinding(deck, Findings[index])) return false;
        _ = CheckNow();
        return true;
    }

    public Task LookNow() => checkedDeck is not { } deck ? CheckNow() : Run(lookLine, "The assistant is looking at the slides…", async cancel =>
    {
        var numbers = MainViewModel.SlidesToLookAt(deck, Findings);
        var look = await model.LookAtSlidesAsync(deck, numbers, cancel);
        lookRows.Children.Clear();
        foreach (var note in look.Notes)
        {
            var row = new DockPanel();
            var label = new TextBlock { Text = $"Slide {note.SlideNumber}", Width = 62, FontWeight = FontWeights.SemiBold };
            DockPanel.SetDock(label, Dock.Left);
            row.Children.Add(label);
            row.Children.Add(new TextBlock { Text = note.Note, TextWrapping = TextWrapping.Wrap });
            lookRows.Children.Add(EditorKit.Card(row, 4));
        }
        return look.Notes.Count == 0
            ? $"The assistant looked at {numbers.Count} slide(s) and had nothing to add ({look.CostLine})."
            : $"The assistant's opinion on {numbers.Count} slide(s); nothing was changed ({look.CostLine}):";
    });

    // MARK: Translate

    private readonly TextBlock translateLine = Line();
    private readonly TextBlock translateCost = Line();
    private readonly StackPanel translateNotes = new() { Margin = new Thickness(0, 8, 0, 0) };
    private string ChosenLanguage => model.Settings.TranslateLanguage is { } chosen && SlideTranslator.Languages.Contains(chosen) ? chosen : SlideTranslator.Languages[0];

    private FrameworkElement TranslatePage()
    {
        var page = Page("Translate", "Makes a copy of the open presentation in another language, for announcements and sermon points. The copy is marked as an AI translation to check; your original isn't touched. Scripture is never machine-translated, and songs aren't translated here.");
        page.Children.Add(translateDeckLine);
        var picker = EditorKit.Picker([.. SlideTranslator.Languages.Select(l => new EditorChoice(l, l))], ChosenLanguage, language =>
        {
            model.Settings.TranslateLanguage = language;
            model.Settings.Save();
            DrawTranslateEstimate();
        }, minWidth: 180);
        page.Children.Add(Buttons(picker, AiButton("Make a translated copy", TranslateNow, "Sends the slides' words (not scripture) to Anthropic and saves a marked copy", accent: true)));
        translateCost.Style = EditorKit.Res<Style>("Muted");
        page.Children.Add(translateCost);
        page.Children.Add(translateLine);
        page.Children.Add(translateNotes);
        return page;
    }

    private void DrawTranslateEstimate() =>
        translateCost.Text = model.CurrentPresentation is { } deck ? model.TranslateEstimate(deck, ChosenLanguage) : "";

    public Task TranslateNow() => model.CurrentPresentation is not { } deck ? Task.CompletedTask : Run(translateLine, $"Translating into {ChosenLanguage}…", async cancel =>
    {
        translateNotes.Children.Clear();
        var done = await model.TranslateDeckAsync(deck, ChosenLanguage, cancel);
        foreach (var note in done.Made.Notes)
            translateNotes.Children.Add(new TextBlock { Text = "• " + note, TextWrapping = TextWrapping.Wrap, Foreground = EditorKit.Res<Brush>("Warn"), FontSize = 12, Margin = new Thickness(0, 0, 0, 4) });
        return $"Saved “{done.Made.Copy.Name}” in the library folder {SlideTranslator.Folder}: {done.Made.Pieces} piece(s) of text translated ({done.CostLine}). Have someone who reads {ChosenLanguage} check it before it is shown.";
    });

    // MARK: Live captions

    private readonly ToggleButton captionSwitch = new() { Content = "Captions on", Padding = new Thickness(12, 4, 12, 4) };
    private readonly TextBlock captionStatus = Line();
    private readonly TextBlock captionNow = new() { FontSize = 20, TextWrapping = TextWrapping.Wrap, MinHeight = 60 };

    private FrameworkElement CaptionPage()
    {
        var page = Page("Live captions", "Shows what is being said as a line of text, from Windows' own speech recognizer on this computer (the sound is never sent anywhere). Captions are a live message, like a stage message: they are never saved into a presentation. They listen on the input chosen for the scripture spotter in the Bible window.");
        captionSwitch.Click += (_, _) =>
        {
            if (captionSwitch.IsChecked == true) model.StartCaptions();
            else model.StopCaptions();
        };
        var where = EditorKit.Picker(
            [new EditorChoice("stage", "On the stage screens"), new EditorChoice("audience", "On the audience screens"), new EditorChoice("both", "On both")],
            model.Settings.CaptionsTarget ?? "stage", target =>
            {
                model.Settings.CaptionsTarget = target;
                model.Settings.Save();
            }, minWidth: 190);
        List<EditorChoice> languages = [new EditorChoice("", "As heard (no translation)"), .. SlideTranslator.Languages.Select(l => new EditorChoice(l, $"Translated into {l} (uses the assistant)"))];
        var into = EditorKit.Picker(languages, model.Settings.CaptionsLanguage ?? "", language =>
        {
            model.Settings.CaptionsLanguage = language.Length == 0 ? null : language;
            model.Settings.Save();
        }, minWidth: 260);
        page.Children.Add(Buttons(captionSwitch, where, into));
        page.Children.Add(captionStatus);
        page.Children.Add(EditorKit.Section("On screen now"));
        page.Children.Add(EditorKit.Card(captionNow));
        page.Children.Add(EditorKit.Muted("Translation sends each finished sentence, alone, to Anthropic, and costs a little for each one. Its words then go straight to the screens you chose, without anyone reading them first: that is what switching translation on means, so try it on the stage screens before the audience's. With no internet the caption simply appears as heard. Windows' recognizer makes mistakes, more so with music or a noisy room.", 11, 0));
        DrawCaptions();
        return page;
    }

    private void DrawCaptions()
    {
        captionSwitch.IsChecked = model.CaptionsOn;
        captionStatus.Text = model.CaptionsStatus;
        captionNow.Text = model.CaptionsLine;
    }

    // MARK: Service report

    private readonly ComboBox reportPicker = new() { MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox reportText = Paper(330, readOnly: true);
    private readonly TextBlock reportLine = Line();
    private readonly StackPanel reportNotes = new() { Margin = new Thickness(0, 6, 0, 0) };
    private List<IReadOnlyList<ServiceRunEntry>> runs = [];
    private ServiceReport.Report? report;

    public string ReportText => reportText.Text;

    private FrameworkElement ReportPage()
    {
        var page = Page("Service report", "What ran when, and what went over its planned length, from the service's own timers on this computer. No AI is used for the report. The assistant can add a few notes for next week if you ask.");
        reportPicker.SelectionChanged += (_, _) => ShowReport();
        page.Children.Add(reportPicker);
        reportText.Margin = new Thickness(0, 8, 0, 0);
        page.Children.Add(reportText);
        page.Children.Add(Buttons(
            EditorKit.Button("Copy", () => Copy(FullReport())),
            EditorKit.Button("Save…", () => SaveText($"Service report {report?.Started:yyyy-MM-dd}.txt", FullReport())),
            AiButton("Notes for next week", ReportNotesNow, "Sends this report's text (row names and times) to Anthropic for up to five planning notes")));
        page.Children.Add(reportLine);
        page.Children.Add(reportNotes);
        return page;
    }

    private void FillReports()
    {
        runs = model.ServiceRuns.Runs();
        reportPicker.Items.Clear();
        foreach (var run in runs) reportPicker.Items.Add($"{run[0].ServiceName}  ·  {run[0].At.LocalDateTime:ddd d MMM, HH:mm}");
        if (runs.Count > 0) reportPicker.SelectedIndex = 0;
        else
        {
            report = null;
            reportText.Text = "No service has been run on this computer yet.\n\nOpen a service and fire its rows as you go: the report is made from when each row was first shown. Clearing the service's timers (Service Tracking › Clear) marks the end.";
        }
    }

    private void ShowReport()
    {
        reportNotes.Children.Clear();
        reportLine.Text = "";
        report = reportPicker.SelectedIndex >= 0 && reportPicker.SelectedIndex < runs.Count ? model.ServiceReportFor(runs[reportPicker.SelectedIndex]) : null;
        if (report is not null) reportText.Text = ServiceReport.Text(report);
    }

    private string FullReport()
    {
        var notes = reportNotes.Children.OfType<TextBlock>().Select(t => t.Text).ToList();
        return reportText.Text + (notes.Count == 0 ? "" : "\nNotes for next week (the assistant's suggestions)\n" + string.Join("\n", notes) + "\n");
    }

    public Task ReportNotesNow() => report is not { } current ? Task.CompletedTask : Run(reportLine, "The assistant is reading the report…", async cancel =>
    {
        var notes = await model.ServiceReportNotesAsync(current, cancel);
        reportNotes.Children.Clear();
        foreach (var note in notes.Notes) reportNotes.Children.Add(new TextBlock { Text = "• " + note, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
        return notes.Notes.Count == 0 ? $"The assistant had no notes ({notes.CostLine})." : $"Notes for next week, the assistant's suggestions ({notes.CostLine}):";
    });

    // MARK: Sermon summary

    private readonly TextBlock sermonLine = Line();
    private readonly ProgressBar sermonProgress = new() { Height = 6, Maximum = 1, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    private readonly TextBox sermonText = Paper(210, readOnly: true);
    private readonly TextBox sermonResult = Paper(230, readOnly: true);
    private readonly TextBlock summaryLine = Line();

    /// <summary>The transcript of the recording last transcribed.</summary>
    public IReadOnlyList<SermonSummary.Heard> Transcript { get; private set; } = [];

    public string SermonResultText => sermonResult.Text;

    private FrameworkElement SermonPage()
    {
        var page = Page("Sermon summary", "Choose a recording of the sermon. It is turned into text on this computer by Windows' speech recognizer: the sound is never sent anywhere. The assistant can then write a summary, key quotes with their times, and drafts for social posts from the text.");
        page.Children.Add(Buttons(
            EditorKit.Button("Choose a recording…", async () => await ChooseRecording(), "A video or sound file (.mp4, .mov, .m4a, .mp3, .wav)", accent: true),
            EditorKit.Button("Copy the transcript", () => Copy(sermonText.Text)),
            AiButton("Summarise with AI", SummariseNow, "Sends the transcript's text (never the sound) to Anthropic")));
        page.Children.Add(sermonProgress);
        page.Children.Add(sermonLine);
        sermonText.Margin = new Thickness(0, 8, 0, 0);
        page.Children.Add(sermonText);
        page.Children.Add(summaryLine);
        sermonResult.Margin = new Thickness(0, 8, 0, 0);
        sermonResult.Visibility = Visibility.Collapsed;
        page.Children.Add(sermonResult);
        page.Children.Add(Buttons(
            EditorKit.Button("Copy the summary", () => Copy(sermonResult.Text)),
            EditorKit.Button("Save…", () => SaveText("Sermon summary.txt", sermonResult.Text + "\n\nTRANSCRIPT\n" + sermonText.Text))));
        return page;
    }

    private async Task ChooseRecording()
    {
        var dialog = new OpenFileDialog { Filter = "Recordings|*.mp4;*.mov;*.m4v;*.m4a;*.mp3;*.wav;*.wma;*.aac;*.mkv|All files|*.*" };
        if (dialog.ShowDialog(this) == true) await TranscribeNow(dialog.FileName);
    }

    /// <summary>Transcribes a recording on this PC (no AI, no internet).</summary>
    public async Task TranscribeNow(string path)
    {
        job?.Cancel();
        job = new CancellationTokenSource();
        var mine = job;
        sermonProgress.Value = 0;
        sermonProgress.Visibility = Visibility.Visible;
        sermonLine.Foreground = EditorKit.Res<Brush>("MutedText");
        sermonLine.Text = $"Listening to “{Path.GetFileName(path)}” on this computer… (a long sermon takes several minutes; you can carry on working)";
        sermonResult.Visibility = Visibility.Collapsed;
        summaryLine.Text = "";
        try
        {
            Transcript = await model.TranscribeRecordingAsync(path, done => sermonProgress.Value = done, mine.Token);
            sermonText.Text = SermonSummary.TranscriptText(Transcript);
            sermonLine.Foreground = EditorKit.Res<Brush>("Text");
            sermonLine.Text = Transcript.Count == 0
                ? "Nothing was heard in that recording."
                : $"Transcribed on this computer: {Transcript.Count} lines, {Transcript.Sum(h => h.Text.Split(' ').Length):N0} words. It will have mistakes; read it before you rely on it."
                    + (model.AssistantUnavailable is null ? $" Summarising would send this text to Anthropic: {model.SermonCostEstimate(Transcript)}." : "");
        }
        catch (OperationCanceledException)
        {
            sermonLine.Text = "Stopped.";
        }
        catch (InvalidOperationException e)
        {
            sermonLine.Foreground = EditorKit.Res<Brush>("Warn");
            sermonLine.Text = e.Message;
        }
        finally
        {
            sermonProgress.Visibility = Visibility.Collapsed;
            if (ReferenceEquals(job, mine)) job = null;
        }
    }

    public Task SummariseNow() => Transcript.Count == 0 ? Task.CompletedTask : Run(summaryLine, "The assistant is reading the transcript…", async cancel =>
    {
        var result = await model.SummariseSermonAsync(Transcript, cancel);
        sermonResult.Text = SermonSummary.Text(result.Summary);
        sermonResult.Visibility = Visibility.Visible;
        return $"The assistant's draft ({result.CostLine}). Quote times come from the transcript"
            + (result.Summary.QuotesDropped > 0 ? $"; {result.Summary.QuotesDropped} quote(s) that didn't match it word for word were thrown away" : "")
            + "." + (result.PrivacyWarning is { } warning ? $" {warning}" : "") + " Check it against the recording before posting.";
    });
}
