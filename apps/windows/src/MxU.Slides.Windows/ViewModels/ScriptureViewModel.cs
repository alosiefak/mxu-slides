using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Core.Scripture;
using MxU.Slides.Core.Threading;
using MxU.Slides.Windows.Rendering;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>A verse in the Bible window's passage list, beside its twin in the second translation.</summary>
public sealed partial class VerseRow(VerseRef reference, string text, string? compare, int page) : ObservableObject
{
    public VerseRef Ref { get; } = reference;
    public string Number { get; } = reference.Verse.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string Text { get; } = text;

    /// <summary>The same verse in the compared translation (or a dash when it hasn't got it); null when not comparing.</summary>
    public string? CompareText { get; } = compare;

    /// <summary>The slide it's on.</summary>
    public int Page { get; } = page;

    /// <summary>"John 4": shown above the first verse of each chapter in a passage that crosses chapters.</summary>
    public string? Heading { get; init; }

    [ObservableProperty]
    public partial bool IsLive { get; set; }

    [ObservableProperty]
    public partial bool IsPreviewed { get; set; }
}

/// <summary>A slide of the passage, for the strip under the preview.</summary>
public sealed partial class PageTile(int index, RenderScene scene, string reference) : ObservableObject
{
    public int Index { get; } = index;
    public RenderScene Scene { get; } = scene;
    public string Reference { get; } = reference;

    [ObservableProperty]
    public partial bool IsLive { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>What the Bible window shows for what's typed.</summary>
public enum ScriptureMode { Empty, Reference, Search }

/// <summary>
/// The Bible tool: type a reference (or words), see the passage and its slides, press Enter to
/// put it on screen, then Next for the following slide, and past the end the following verse.
/// The window (<c>Views/BibleWindow.cs</c>) only draws this and passes keys on.
/// <para>
/// <b>Live without saving.</b> Going live makes a presentation for the passage and fires it like
/// any other (<see cref="MainViewModel.FireScripture"/>), but doesn't save it: looking up twenty
/// verses during a sermon shouldn't leave twenty presentations in the library. Because it isn't
/// in the library, the main window's Next asks this tool what comes next (<see cref="Step"/>).
/// <b>Add to Service</b> and <b>Save</b> make it a real library presentation.
/// </para>
/// <para>
/// <b>Never waiting.</b> Translations load, and searches run, on the Bible thread
/// (<see cref="BibleCatalog"/>). Making the slides measures every slide's words with the outputs'
/// own text layout, which for a long passage takes a while, so it runs on a thread of its own
/// too (<see cref="LayoutThread"/>). An older search still running when a newer one starts is
/// cancelled, and anything that arrives late for text that has since changed is dropped
/// (<see cref="generation"/>). Enter waits for the slides of what's typed (<see cref="Ready"/>).
/// </para>
/// </summary>
public sealed partial class ScriptureViewModel : ObservableObject
{
    private readonly MainViewModel main;
    private readonly BibleCatalog catalog = BibleCatalog.BuiltIn;
    private readonly DispatcherTimer typingPause;
    private CancellationTokenSource? searching;
    private int generation;
    private bool settingQuery;
    private string? readQuery; // the text RefreshNow last read

    public ScriptureViewModel(MainViewModel main)
    {
        this.main = main;
        var settings = main.Settings;
        translation = catalog.Find(settings.ScriptureTranslation) ?? catalog.Default;
        compare = catalog.Find(settings.ScriptureCompare);
        bothOnSlides = settings.ScriptureCompareOnSlides;
        layout = Enum.TryParse<ScriptureLayout>(settings.ScriptureLayout, out var saved) ? saved : ScriptureLayout.FullScreen;
        versesPerSlide = Math.Clamp(settings.ScriptureVersesPerSlide, 0, 9);
        verseNumbers = settings.ScriptureVerseNumbers;
        referenceLine = settings.ScriptureReferenceLine;
        themeId = settings.ScriptureThemeId;
        typingPause = new DispatcherTimer(TimeSpan.FromMilliseconds(70), DispatcherPriority.Background, (_, _) => RefreshNow(), Dispatcher.CurrentDispatcher);
        typingPause.Stop();
        // Read the usual translation (and build its search index) now, in the background,
        // so the first lookup during the service is instant.
        catalog.Warm(translation.Id);
        if (compare is not null) _ = catalog.LoadAsync(compare.Id);
    }

    /// <summary>Raised when the passage, results, slides, live marks or status change (the window redraws that part).</summary>
    public event Action? Changed;

    public IReadOnlyList<BibleTranslation> Translations => catalog.Translations;

    // MARK: Choices

    private BibleTranslation translation;
    private BibleTranslation? compare;
    private bool bothOnSlides;
    private ScriptureLayout layout;
    private int versesPerSlide;
    private bool verseNumbers;
    private bool referenceLine;
    private string? themeId;

    public BibleTranslation Translation
    {
        get => translation;
        set
        {
            if (value == translation) return;
            translation = value;
            main.Settings.ScriptureTranslation = value.Id;
            Choose();
        }
    }

    /// <summary>The second translation beside the first, or null.</summary>
    public BibleTranslation? Compare
    {
        get => compare;
        set
        {
            if (value == compare) return;
            compare = value;
            main.Settings.ScriptureCompare = value?.Id;
            Choose();
        }
    }

    /// <summary>
    /// True to put the second translation on the slides as well, side by side with the first
    /// (<see cref="ScriptureSlides"/> halves the design's verse box). Without a second translation
    /// it does nothing.
    /// </summary>
    public bool BothOnSlides
    {
        get => bothOnSlides;
        set
        {
            if (value == bothOnSlides) return;
            bothOnSlides = value;
            main.Settings.ScriptureCompareOnSlides = value;
            Choose();
        }
    }

    // The translation to put beside a passage in another on its slides, or null.
    private BibleTranslation? SideBySide(BibleTranslation shown) =>
        bothOnSlides && compare is { } other && other.Id != shown.Id ? other : null;

    // The same passage in that translation, when it's wanted and in memory.
    private PassageText? Beside(PassageText passage) =>
        SideBySide(passage.Translation) is { } other && catalog.Loaded(other.Id) is { } bible ? bible.Read(passage.Reference) : null;

    public ScriptureLayout Layout
    {
        get => layout;
        set
        {
            if (value == layout) return;
            layout = value;
            main.Settings.ScriptureLayout = value.ToString();
            Choose();
        }
    }

    /// <summary>0 = as many as fit.</summary>
    public int VersesPerSlide
    {
        get => versesPerSlide;
        set
        {
            if (value == versesPerSlide) return;
            versesPerSlide = Math.Clamp(value, 0, 9);
            main.Settings.ScriptureVersesPerSlide = versesPerSlide;
            Choose();
        }
    }

    public bool VerseNumbers
    {
        get => verseNumbers;
        set
        {
            if (value == verseNumbers) return;
            verseNumbers = value;
            main.Settings.ScriptureVerseNumbers = value;
            Choose();
        }
    }

    public bool ReferenceLine
    {
        get => referenceLine;
        set
        {
            if (value == referenceLine) return;
            referenceLine = value;
            main.Settings.ScriptureReferenceLine = value;
            Choose();
        }
    }

    /// <summary>The themes in the library, for the picker.</summary>
    public IReadOnlyList<Theme> Themes => [.. main.Library.All<Theme>().OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>The theme the slides are made in: the one picked here, else the app's default, else any.</summary>
    public Theme? Theme =>
        main.Library.Get<Theme>(themeId ?? main.Settings.DefaultThemeId) ?? main.Library.Get<Theme>(main.Settings.DefaultThemeId)
        ?? main.Library.All<Theme>().FirstOrDefault();

    public void ChooseTheme(string id)
    {
        if (id == Theme?.Id) return;
        themeId = id;
        main.Settings.ScriptureThemeId = id;
        Choose();
    }

    private ScriptureSlideOptions Options => new()
    {
        Layout = layout, VersesPerSlide = versesPerSlide, VerseNumbers = verseNumbers, ShowReference = referenceLine,
    };

    // A choice changed: save it, and show the passage with it (what's live stays until Enter).
    private void Choose()
    {
        main.Settings.Save();
        if (compare is not null) _ = catalog.LoadAsync(compare.Id);
        RefreshNow();
    }

    // MARK: What's typed

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    partial void OnQueryChanged(string value)
    {
        if (settingQuery) return;
        typingPause.Stop();
        typingPause.Start();
    }

    public ScriptureMode Mode { get; private set; }

    /// <summary>Calm words under the box: what was found, why nothing was, or a verse left out.</summary>
    public string Status { get; private set; } = "";

    /// <summary>True while the status is a mistake or a missing verse (shown amber, never red).</summary>
    public bool StatusIsNote { get; private set; }

    /// <summary>Books to offer while a name is being typed.</summary>
    public IReadOnlyList<BibleBook> Suggestions { get; private set; } = [];

    /// <summary>Search results, best first.</summary>
    public IReadOnlyList<SearchHit> Results { get; private set; } = [];

    /// <summary>The result Enter would take, or -1.</summary>
    public int SelectedResult { get; private set; } = -1;

    /// <summary>The typed passage in the chosen translation.</summary>
    public PassageText? Passage { get; private set; }

    /// <summary>The presentation the typed passage becomes (not saved), with a scene per slide.</summary>
    public Presentation? Preview { get; private set; }

    public IReadOnlyList<VerseRow> Verses { get; private set; } = [];

    public IReadOnlyList<PageTile> Pages { get; private set; } = [];

    /// <summary>The slide shown large in the preview, and that Enter puts up.</summary>
    public int SelectedPage { get; private set; }

    /// <summary>What the preview shows: the selected slide, or while searching the chosen result's verse.</summary>
    public RenderScene? PreviewScene => Mode == ScriptureMode.Search
        ? resultPreview?.Scenes.FirstOrDefault()
        : SelectedPage >= 0 && SelectedPage < Pages.Count ? Pages[SelectedPage].Scene : null;

    /// <summary>"1 Corinthians 13:4" while a search result is previewed.</summary>
    public string? PreviewedResult => Mode == ScriptureMode.Search ? resultPreview?.Deck.Slides.FirstOrDefault()?.Objects.FirstOrDefault(o => o.Name == "Reference")?.Text : null;

    // The chosen search result's verse as a slide, for the preview.
    private Made? resultPreview;

    /// <summary>
    /// A translation that has the verses this one leaves out (the KJV for Acts 8:37 in the WEB),
    /// offered as "Show it in the KJV"; null otherwise.
    /// </summary>
    public BibleTranslation? Alternative { get; private set; }

    /// <summary>Looks what's typed up in the <see cref="Alternative"/> ("Acts 8:37 KJV").</summary>
    public void UseAlternative()
    {
        if (Alternative is not { } other || Passage is not { } passage) return;
        Query = $"{passage.Reference.Display} {other.Abbreviation}";
        RefreshNow();
    }

    /// <summary>The slides for what's typed, while they're being made (Enter waits for them); finished otherwise.</summary>
    public Task Ready { get; private set; } = Task.CompletedTask;

    /// <summary>Reads the box now (Enter does this, so a fast typist never waits for the pause).</summary>
    public void RefreshNow()
    {
        typingPause.Stop();
        readQuery = Query;
        var text = Query.Trim();
        var mine = ++generation;
        searching?.Cancel();
        searching = null;
        Ready = Task.CompletedTask;
        Results = [];
        SelectedResult = -1;
        resultPreview = null;
        Alternative = null;
        StatusIsNote = false;
        // A book name on its own, whole or half-typed ("phil", "1 co", "John"), offers the books it could be.
        Suggestions = text.TrimStart('1', '2', '3', ' ').Any(char.IsDigit) ? [] : BibleBooks.Complete(text, 6);
        if (text.Length == 0)
        {
            Mode = ScriptureMode.Empty;
            ShowPassage(null, null);
            Status = "Type a reference (John 3:16, Ps 23, 1 Cor 13:4-8) or words to search for.";
            Changed?.Invoke();
            return;
        }

        var reference = PassageReference.Parse(text);
        if (reference is null)
        {
            Mode = ScriptureMode.Search;
            ShowPassage(null, null);
            Status = $"Searching the {translation.Abbreviation}…";
            Changed?.Invoke();
            _ = Search(text, mine);
            return;
        }

        Mode = ScriptureMode.Reference;
        // "John 3:16 WEB" looks it up in the WEB without changing the usual translation.
        var named = catalog.Find(reference.Translation) ?? translation;
        // Both translations must be in memory when the slides show them side by side.
        foreach (var needed in new[] { named, SideBySide(named) }.OfType<BibleTranslation>())
        {
            if (catalog.Loaded(needed.Id) is not null) continue;
            Status = $"Opening the {needed.Abbreviation}…";
            Changed?.Invoke();
            Ready = LoadThenRefresh(needed.Id, mine);
            return;
        }
        var bible = catalog.Loaded(named.Id)!;
        var passage = bible.Read(reference);
        if (passage.IsEmpty || Theme is not { } theme)
        {
            ShowPassage(passage, null);
            Describe(passage, bible);
            Changed?.Invoke();
            return;
        }
        // The slides follow from the layout thread a moment later; the list and the preview change
        // together then, so they never disagree. Meanwhile the status says what's coming.
        Status = $"{passage.Reference.Display} · {Count(passage.Verses.Count, "verse")} · making the slides…";
        Changed?.Invoke();
        Ready = ShowWhenMade(passage, Beside(passage), bible, MxuJson.Clone(theme), Options, mine);
    }

    private async Task LoadThenRefresh(string id, int mine)
    {
        try
        {
            await catalog.LoadAsync(id);
        }
        catch (Exception error)
        {
            Status = $"Couldn't open the Bible text: {error.Message}";
            StatusIsNote = true;
            Changed?.Invoke();
            return;
        }
        if (mine != generation) return;
        RefreshNow();
        await Ready;
    }

    private async Task ShowWhenMade(PassageText passage, PassageText? beside, Bible bible, Theme theme, ScriptureSlideOptions options, int mine)
    {
        Made made;
        try
        {
            made = await OnLayoutThread(() => Make(passage, beside, theme, options));
        }
        catch (Exception error)
        {
            if (mine != generation) return;
            Status = $"Couldn't make the slides: {error.Message}";
            StatusIsNote = true;
            Changed?.Invoke();
            return;
        }
        if (mine != generation) return; // the box has changed since: newer slides are on their way
        ShowPassage(passage, made);
        Describe(passage, bible);
        Changed?.Invoke();
    }

    private async Task Search(string text, int mine)
    {
        var cancel = new CancellationTokenSource();
        searching = cancel;
        try
        {
            var hits = await catalog.SearchAsync(translation.Id, text, 60, cancel.Token);
            if (mine != generation) return; // the box has changed since: a newer search is on its way
            Results = hits;
            SelectedResult = hits.Count > 0 ? 0 : -1;
            _ = PreviewResult(mine);
            Status = hits.Count == 0
                ? Suggestions.Count > 0 ? "Pick a book, or keep typing." : $"Nothing in the {translation.Abbreviation} matches “{text}”."
                : $"{hits.Count}{(hits.Count == 60 ? "+" : "")} verses in the {translation.Abbreviation} · ↑↓ to choose, Enter to show";
            Changed?.Invoke();
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(searching, cancel)) searching = null;
            cancel.Dispose();
        }
    }

    private static string Count(int count, string what) => $"{count} {what}{(count == 1 ? "" : "s")}";

    // "John 3:16–18 · 3 verses · 2 slides", or why not.
    private void Describe(PassageText passage, Bible bible)
    {
        var others = catalog.AllLoaded();
        if (passage.IsEmpty)
        {
            Status = passage.Absent.Count > 0 ? AbsentVerses.Explain(passage.Absent, bible, others) : passage.Problem ?? "Nothing found.";
            StatusIsNote = true;
            Alternative = passage.Absent.Count == 0 ? null
                : catalog.Translations.Where(t => t.Id != passage.Translation.Id)
                    .FirstOrDefault(t => catalog.Loaded(t.Id) is { } other && passage.Absent.All(other.Contains));
            return;
        }
        Status = $"{passage.Reference.Display} · {Count(passage.Verses.Count, "verse")} · {Count(Preview?.Slides.Count ?? 0, "slide")}";
        if (passage.Absent.Count > 0)
        {
            Status += " · " + AbsentVerses.Explain(passage.Absent, bible, others);
            StatusIsNote = true;
        }
        else if (passage.Problem is { } problem)
        {
            Status += " · " + problem;
            StatusIsNote = true;
        }
    }

    // MARK: Making the slides

    /// <summary>A passage's presentation, its pages (which verses each slide holds) and a picture of each slide.</summary>
    private sealed record Made(Presentation Deck, IReadOnlyList<ScripturePage> Pages, IReadOnlyList<RenderScene> Scenes);

    /// <summary>
    /// The thread slides are made on. WPF's text layout measures the words; like anything in WPF
    /// it wants a single-threaded apartment (STA) thread, and the app's text measurer is safe to
    /// share between threads (its caches are locked, its brushes frozen). Normal priority: the
    /// show's own threads (clock, sound, capture) run above normal and still come first, but the
    /// operator is waiting for this, so it mustn't queue behind every other busy thread on the PC.
    /// (At below normal, a busy machine starved it for seconds.)
    /// </summary>
    private static readonly Lazy<WorkQueue> LayoutThread = new(() => new WorkQueue("Bible slides", ThreadPriority.Normal, ApartmentState.STA));

    private static Task<T> OnLayoutThread<T>(Func<T> work)
    {
        // Continuations run asynchronously: the awaiting UI method resumes on the UI thread.
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        LayoutThread.Value.Async(() =>
        {
            try { done.SetResult(work()); }
            catch (Exception error) { done.SetException(error); }
        });
        return done.Task;
    }

    /// <summary>The passage as slides (side by side with <paramref name="beside"/>, if given), measured the way the outputs draw text (on any thread).</summary>
    private static Made Make(PassageText passage, PassageText? beside, Theme theme, ScriptureSlideOptions options)
    {
        var pages = ScriptureSlides.Pages(passage, theme, options, TextLayout.OverflowsFrame, beside);
        var deck = ScriptureSlides.Build(passage, theme, options, pages, beside);
        return new Made(deck, pages, [.. deck.Slides.Select(slide => SlideSceneBuilder.Scene(slide, theme, deck, pager: TextLayout.Pager))]);
    }

    // Shows a passage and its slides (or nothing) in the list, the strip and the preview.
    private void ShowPassage(PassageText? passage, Made? made)
    {
        Passage = passage;
        Preview = made?.Deck;
        SelectedPage = 0;
        if (passage is null || made is null)
        {
            Verses = passage is null ? [] : Rows(passage, []);
            Pages = [];
            return;
        }
        Pages = [.. made.Deck.Slides.Select((slide, i) => new PageTile(i, made.Scenes[i],
            slide.Objects.FirstOrDefault(o => o.Name == "Reference")?.Text ?? ""))];
        Verses = Rows(passage, made.Pages);
        MarkLive();
    }

    // The verse rows, each knowing the slide it's on.
    private List<VerseRow> Rows(PassageText passage, IReadOnlyList<ScripturePage> pages)
    {
        var compared = compare is not null && compare.Id != passage.Translation.Id ? catalog.Loaded(compare.Id) : null;
        var rows = new List<VerseRow>();
        var chapters = passage.Verses.Select(v => (v.Ref.Book, v.Ref.Chapter)).Distinct().Count();
        (int, int)? previous = null;
        foreach (var verse in passage.Verses)
        {
            var page = Math.Max(0, pages.ToList().FindLastIndex(p => p.First <= verse.Ref));
            var twin = compared is null ? null : compared[verse.Ref] ?? "—";
            var chapter = (verse.Ref.Book, verse.Ref.Chapter);
            rows.Add(new VerseRow(verse.Ref, verse.Text, twin, page)
            {
                Heading = chapters > 1 && chapter != previous ? $"{verse.Ref.BookInfo.DisplayName(false)} {verse.Ref.Chapter}" : null,
            });
            previous = chapter;
        }
        return rows;
    }

    /// <summary>Shows a slide of the passage large in the preview (a click on a tile or a verse).</summary>
    public void SelectPage(int page)
    {
        if (page < 0 || page >= Pages.Count) return;
        SelectedPage = page;
        MarkLive();
        Changed?.Invoke();
    }

    /// <summary>
    /// ↓ / ↑ in the box. Searching: the highlighted result. While the passage is live: its next or
    /// previous slide, then verse (<see cref="Step"/>). Otherwise only the preview moves, so an
    /// arrow key in a text box never changes what the audience sees.
    /// </summary>
    public void Arrow(int by)
    {
        if (Mode == ScriptureMode.Search) MoveResult(by);
        else if (IsLive) Step(by);
        else SelectPage(SelectedPage + by);
    }

    /// <summary>Moves the highlighted search result (↑ ↓ in the box).</summary>
    public void MoveResult(int by)
    {
        if (Results.Count == 0) return;
        SelectResult(Math.Clamp(SelectedResult + by, 0, Results.Count - 1));
    }

    public void SelectResult(int index)
    {
        if (index < 0 || index >= Results.Count || index == SelectedResult) return;
        SelectedResult = index;
        Changed?.Invoke();
        _ = PreviewResult(generation);
    }

    // The chosen result's verse, made into a slide on the layout thread for the preview.
    private async Task PreviewResult(int mine)
    {
        if (SelectedResult < 0 || catalog.Loaded(translation.Id) is not { } bible || Theme is not { } theme) return;
        var chosen = SelectedResult;
        var verse = bible.Read(PassageReference.Of(Results[chosen].Verse.Ref));
        var beside = Beside(verse);
        var options = Options;
        var copy = MxuJson.Clone(theme);
        try
        {
            var made = await OnLayoutThread(() => Make(verse, beside, copy, options));
            if (mine != generation || chosen != SelectedResult) return;
            resultPreview = made;
            Changed?.Invoke();
        }
        catch (Exception error) { Infrastructure.Log.Warn($"Couldn't preview the search result: {error.Message}"); }
    }

    /// <summary>Tab, or a click on a book: the box becomes "John " ready for the chapter.</summary>
    public void AcceptSuggestion(BibleBook book)
    {
        Query = book.Name + " ";
        RefreshNow();
    }

    // MARK: Live

    /// <summary>The presentation this tool last put on screen (not in the library unless saved).</summary>
    public Presentation? LivePresentation { get; private set; }

    public PassageText? LivePassage { get; private set; }

    /// <summary>True while this tool's passage is on the audience screen.</summary>
    public bool IsLive => LivePresentation is { } live && main.Live?.PresentationId == live.Id && main.Show.Live is not null;

    /// <summary>The live slide of the passage, or -1.</summary>
    public int LivePage => IsLive ? main.Live!.Occurrence : -1;

    /// <summary>"John 3:16–18 (KJV) · 1 of 2" while live.</summary>
    public string LiveLine => IsLive ? $"{LivePresentation!.Name} · slide {LivePage + 1} of {LivePresentation.Slides.Count}" : "";

    /// <summary>
    /// Enter: puts the selected slide of what's typed on screen (or the search result chosen,
    /// which becomes what's typed), once its slides are made. False when there's nothing to show.
    /// </summary>
    public async Task<bool> GoLive(int? page = null)
    {
        await ReadyToKeep();
        if (Preview is not { } deck || Passage is not { } passage || deck.Slides.Count == 0) return false;
        Fire(deck, passage, Math.Clamp(page ?? SelectedPage, 0, deck.Slides.Count - 1));
        return true;
    }

    /// <summary>
    /// Next or Previous (the window's ◀ ▶, PageDown and PageUp, and the main window's Next
    /// while the passage is live): the next slide of the passage, and past its end the next verse
    /// (across chapters and books), which becomes what's typed. While something else is on screen
    /// (a song), they move that on instead, as the main window's would: a clicker pressed while
    /// this window has the focus must never put scripture over the lyrics. With nothing on screen,
    /// Next puts the typed passage up. False when there's nowhere to go.
    /// </summary>
    public bool Step(int direction)
    {
        if (!IsLive || LivePresentation is not { } deck || LivePassage is not { } passage)
        {
            if (main.Show.Live is not null)
            {
                (direction > 0 ? main.NextCommand : main.PreviousCommand).Execute(null);
                return true;
            }
            if (direction <= 0) return false;
            _ = GoLive();
            return true;
        }
        var page = LivePage + direction;
        if (page >= 0 && page < deck.Slides.Count)
        {
            Fire(deck, passage, page);
            return true;
        }
        if (catalog.Loaded(passage.Translation.Id) is not { } bible || passage.Verses.Count == 0 || Theme is not { } theme) return false;
        var edge = direction > 0 ? passage.Verses[^1].Ref : passage.Verses[0].Ref;
        if ((direction > 0 ? bible.After(edge) : bible.Before(edge)) is not { } verse)
        {
            main.Notice = direction > 0 ? "That's the end of the Bible." : "That's the start of the Bible.";
            return false;
        }
        // One verse is made here and now (a few milliseconds), so Next answers at once.
        var next = bible.Read(PassageReference.Of(verse));
        var made = Make(next, Beside(next), theme, Options);
        if (made.Deck.Slides.Count == 0) return false;
        // The box, the list and the preview follow, so the operator sees where they are.
        ++generation;
        searching?.Cancel();
        Ready = Task.CompletedTask;
        SetQuery(next.Reference.Display);
        Mode = ScriptureMode.Reference;
        Suggestions = [];
        Results = [];
        StatusIsNote = false;
        ShowPassage(next, made);
        Describe(next, bible);
        Fire(made.Deck, next, direction > 0 ? 0 : made.Deck.Slides.Count - 1);
        return true;
    }

    private void Fire(Presentation deck, PassageText passage, int page)
    {
        LivePresentation = deck;
        LivePassage = passage;
        main.FireScripture(deck, page);
        if (ReferenceEquals(deck, Preview)) SelectedPage = page;
        MarkLive();
        Changed?.Invoke();
    }

    /// <summary>Marks the live slide and verses, and the previewed ones (after anything goes live or clears).</summary>
    public void MarkLive()
    {
        var livePage = ReferenceEquals(LivePresentation, Preview) ? LivePage : -1;
        foreach (var tile in Pages)
        {
            tile.IsLive = tile.Index == livePage;
            tile.IsSelected = tile.Index == SelectedPage;
        }
        foreach (var row in Verses)
        {
            row.IsLive = row.Page == livePage;
            row.IsPreviewed = row.Page == SelectedPage && Pages.Count > 0;
        }
    }

    /// <summary>The main window's show changed (a slide went live, or was cleared): redraw the live marks.</summary>
    public void LiveChanged()
    {
        MarkLive();
        Changed?.Invoke();
    }

    /// <summary>True from a key typed in the box until the box is read (the short pause after typing).</summary>
    public bool HasUnreadTyping => typingPause.IsEnabled || readQuery != Query;

    // Typing not yet read (Enter pressed inside the pause after a key) is read now; text already
    // read isn't read again, which would throw away the search results Enter is choosing from.
    private void ReadTyping()
    {
        if (HasUnreadTyping) RefreshNow();
    }

    // Shows text in the box without reading it again (what it says is already shown).
    private void SetQuery(string text)
    {
        settingQuery = true;
        try { Query = text; }
        finally { settingQuery = false; }
        typingPause.Stop();
        readQuery = text;
    }

    // MARK: Keeping it

    /// <summary>Saves the typed passage as a presentation in the library and adds it to the service. Its name, or null.</summary>
    public async Task<string?> AddToService() =>
        await Keepable() is { } deck ? main.AddScriptureToService(deck) : null;

    /// <summary>Saves the typed passage as a presentation in the library. Its name, or null.</summary>
    public async Task<string?> SaveToLibrary() =>
        await Keepable() is { } deck ? main.SaveScripture(deck) : null;

    private async Task<Presentation?> Keepable()
    {
        await ReadyToKeep();
        return Preview is { Slides.Count: > 0 } deck ? deck : null;
    }

    // What's typed, read and made into slides; a chosen search result first becomes what's typed.
    private async Task ReadyToKeep()
    {
        ReadTyping();
        if (Mode == ScriptureMode.Search && SelectedResult >= 0 && SelectedResult < Results.Count)
        {
            SetQuery(PassageReference.Of(Results[SelectedResult].Verse.Ref).Display);
            RefreshNow();
        }
        await Ready;
    }
}
