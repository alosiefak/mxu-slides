using System.Globalization;
using System.Text.Json.Serialization;

namespace MxU.Slides.Core.Scripture;

/// <summary>
/// One translation the app carries, as listed in <c>Scripture/Bibles/bibles.json</c>: its name,
/// abbreviation, licence notes (shown in the Bible window's About), where the text came from
/// (with the download's SHA-256, so it can be checked) and how many verses it has.
/// </summary>
public sealed record BibleTranslation(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("abbreviation")] string Abbreviation,
    [property: JsonPropertyName("notes")] string Notes,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("sourceSha256")] string SourceSha256,
    [property: JsonPropertyName("verses")] int Verses,
    [property: JsonPropertyName("file")] string File)
{
    public override string ToString() => Abbreviation;
}

/// <summary>A verse and its words.</summary>
public readonly record struct BibleVerse(VerseRef Ref, string Text);

/// <summary>
/// What a reference holds in one translation: the verses found, in the order asked, and the
/// verses it asked for that this translation doesn't have (<see cref="Absent"/>: Acts 8:37 in the
/// WEB and ASV, Matthew 17:21 in the ASV). <see cref="Reference"/> is the reference as it was
/// read, with any range that ran past a chapter's end cut back to it. <see cref="Problem"/> says,
/// calmly, why nothing (or less) was found: "John has 21 chapters."
/// </summary>
public sealed record PassageText(
    PassageReference Reference,
    BibleTranslation Translation,
    IReadOnlyList<BibleVerse> Verses,
    IReadOnlyList<VerseRef> Absent,
    string? Problem)
{
    public bool IsEmpty => Verses.Count == 0;

    /// <summary>"John 3:16–18 (KJV)": the name a presentation made from it gets.</summary>
    public string Title => $"{Reference.Display} ({Translation.Abbreviation})";
}

/// <summary>
/// One translation in memory, read from its data file: every verse by book, chapter and verse,
/// so a lookup is an array index. Verses a translation leaves out by design (later additions not
/// in the oldest manuscripts) are simply missing, and lookups report them as <see cref="PassageText.Absent"/>.
/// Load one through <see cref="BibleCatalog"/>, which does it once and off the UI thread.
/// Windows only (the Mac has no Bible yet).
/// </summary>
public sealed class Bible
{
    // [book][chapter - 1][verse - 1]: the words, or null for a verse this translation leaves out.
    private readonly string?[][][] text;

    private Bible(BibleTranslation translation, string?[][][] text, int count)
    {
        Translation = translation;
        this.text = text;
        VerseCount = count;
    }

    public BibleTranslation Translation { get; }

    /// <summary>How many verses it holds (31,102 in the KJV).</summary>
    public int VerseCount { get; }

    /// <summary>Reads the data file's lines: <c>BOOK&lt;TAB&gt;chapter&lt;TAB&gt;verse&lt;TAB&gt;text</c> (already unzipped).</summary>
    public static Bible Read(BibleTranslation translation, TextReader lines)
    {
        var books = BibleBooks.All.Select(_ => new List<List<string?>>()).ToArray();
        var count = 0;
        while (lines.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            var parts = line.Split('\t', 4);
            if (parts.Length < 4 || BibleBooks.WithCode(parts[0]) is not { } book
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var chapter) || chapter <= 0
                || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var verse) || verse <= 0)
                continue;
            var chapters = books[book.Index];
            while (chapters.Count < chapter) chapters.Add([]);
            var verses = chapters[chapter - 1];
            while (verses.Count < verse) verses.Add(null);
            if (verses[verse - 1] is null) count++;
            verses[verse - 1] = parts[3];
        }
        return new Bible(translation, books.Select(b => b.Select(c => c.ToArray()).ToArray()).ToArray(), count);
    }

    /// <summary>Chapters in a book, as the data has them.</summary>
    public int ChapterCount(int book) => book >= 0 && book < text.Length ? text[book].Length : 0;

    /// <summary>The chapter's last verse number, as the data has it (verses left out inside it still count).</summary>
    public int VersesIn(int book, int chapter) =>
        chapter >= 1 && chapter <= ChapterCount(book) ? text[book][chapter - 1].Length : 0;

    /// <summary>The verse's words, or null when this translation hasn't got it.</summary>
    public string? this[VerseRef verse] =>
        verse.Verse >= 1 && verse.Verse <= VersesIn(verse.Book, verse.Chapter) ? text[verse.Book][verse.Chapter - 1][verse.Verse - 1] : null;

    public bool Contains(VerseRef verse) => this[verse] is not null;

    /// <summary>The next verse this translation has, across chapters and books; null after Revelation 22:21.</summary>
    public VerseRef? After(VerseRef verse)
    {
        var (b, c, v) = (verse.Book, Math.Max(verse.Chapter, 1), Math.Max(verse.Verse + 1, 1));
        for (; b < text.Length; b++, c = 1, v = 1)
            for (; c <= ChapterCount(b); c++, v = 1)
                for (; v <= VersesIn(b, c); v++)
                    if (text[b][c - 1][v - 1] is not null) return new VerseRef(b, c, v);
        return null;
    }

    /// <summary>The verse before, across chapters and books; null before Genesis 1:1.</summary>
    public VerseRef? Before(VerseRef verse)
    {
        var (b, c, v) = (verse.Book, Math.Min(verse.Chapter, ChapterCount(verse.Book)), verse.Verse - 1);
        if (c < verse.Chapter) v = int.MaxValue; // past the book's last chapter: start from its end
        for (; b >= 0; b--, c = b >= 0 ? ChapterCount(b) : 0, v = int.MaxValue)
            for (; c >= 1; c--, v = int.MaxValue)
                for (v = Math.Min(v, VersesIn(b, c)); v >= 1; v--)
                    if (text[b][c - 1][v - 1] is not null) return new VerseRef(b, c, v);
        return null;
    }

    /// <summary>Every verse it holds, in order (the search index reads these).</summary>
    public IEnumerable<BibleVerse> AllVerses()
    {
        for (var b = 0; b < text.Length; b++)
            for (var c = 0; c < text[b].Length; c++)
                for (var v = 0; v < text[b][c].Length; v++)
                    if (text[b][c][v] is { } words) yield return new BibleVerse(new VerseRef(b, c + 1, v + 1), words);
    }

    /// <summary>
    /// Looks a reference up. Verses come back in the order asked (each once). A range running past
    /// a chapter's end stops there; a chapter or verse that isn't in the book is a <see cref="PassageText.Problem"/>.
    /// </summary>
    public PassageText Read(PassageReference reference)
    {
        var verses = new List<BibleVerse>();
        var absent = new List<VerseRef>();
        var spans = new List<PassageSpan>();
        var seen = new HashSet<VerseRef>();
        string? problem = null;
        foreach (var span in reference.Spans)
        {
            var book = BibleBooks.All[span.Book];
            var chapters = ChapterCount(span.Book);
            if (span.FirstChapter > chapters)
            {
                problem ??= chapters == 1 ? $"{book.Name} has only one chapter." : $"{book.Name} has {chapters} chapters.";
                continue;
            }
            var lastChapter = Math.Min(span.LastChapter, chapters);
            VerseRef? first = null, last = null;
            void Take(VerseRef at, bool exists)
            {
                if (!seen.Add(at)) return;
                if (exists && this[at] is { } words) verses.Add(new BibleVerse(at, words));
                else absent.Add(at);
                first ??= at;
                last = at;
            }
            for (var c = span.FirstChapter; c <= lastChapter; c++)
            {
                var count = VersesIn(span.Book, c);
                var from = c == span.FirstChapter ? span.FirstVerse ?? 1 : 1;
                var asked = c == lastChapter ? span.LastVerse ?? count : count;
                for (var v = from; v <= Math.Min(asked, count); v++) Take(new VerseRef(span.Book, c, v), exists: true);
                // Past the chapter's end. Verses this translation numbers elsewhere (Romans 16:25–27
                // in the WEB) are absent, not a mistake; anything else is.
                var renumbered = 0;
                for (var v = Math.Max(from, count + 1); v <= asked; v++)
                {
                    var at = new VerseRef(span.Book, c, v);
                    if (AbsentVerses.MovedTo(at) is not { } there || !Contains(there)) continue;
                    Take(at, exists: false);
                    renumbered++;
                }
                if (from > count && renumbered == 0) problem ??= $"{book.DisplayName(false)} {c} has {count} verses.";
            }
            if (first is not { } a || last is not { } z) continue;
            // Whole chapters stay whole ("Psalm 23", not "Psalm 23:1–6"); verse ranges are cut back to what exists.
            spans.Add(span.IsWholeChapters
                ? span with { LastChapter = lastChapter }
                : PassageSpan.Verses(a, z));
        }
        if (verses.Count == 0 && absent.Count > 0) problem ??= AbsentVerses.Explain(absent, this, null);
        return new PassageText(spans.Count > 0 ? new PassageReference(spans, reference.Translation) : reference,
            Translation, verses, absent, problem);
    }
}
