using System.Globalization;
using System.Text;

namespace MxU.Slides.Core.Scripture;

/// <summary>One verse's address: book (its place, 0–65), chapter and verse. They sort in Bible order.</summary>
public readonly record struct VerseRef(int Book, int Chapter, int Verse) : IComparable<VerseRef>
{
    public BibleBook BookInfo => BibleBooks.All[Book];

    public int CompareTo(VerseRef other) => (Book, Chapter, Verse).CompareTo((other.Book, other.Chapter, other.Verse));

    public static bool operator <(VerseRef a, VerseRef b) => a.CompareTo(b) < 0;
    public static bool operator >(VerseRef a, VerseRef b) => a.CompareTo(b) > 0;
    public static bool operator <=(VerseRef a, VerseRef b) => a.CompareTo(b) <= 0;
    public static bool operator >=(VerseRef a, VerseRef b) => a.CompareTo(b) >= 0;

    /// <summary>"Psalm 23:1", "John 3:16".</summary>
    public string Display => $"{BookInfo.DisplayName(severalChapters: false)} {Chapter}:{Verse}";

    /// <summary>The data files' form, "JOH 3:16".</summary>
    public override string ToString() => $"{BookInfo.Code} {Chapter}:{Verse}";
}

/// <summary>
/// One stretch of a reference, inside one book: a verse ("3:16"), verses ("3:16–18"), verses
/// across chapters ("3:16–4:2"), a chapter ("23") or chapters ("1–2"). A whole chapter has no
/// verse numbers (both null): how many verses it has depends on the translation.
/// </summary>
public sealed record PassageSpan(int Book, int FirstChapter, int? FirstVerse, int LastChapter, int? LastVerse)
{
    public bool IsWholeChapters => FirstVerse is null;

    public static PassageSpan Verse(VerseRef verse) => new(verse.Book, verse.Chapter, verse.Verse, verse.Chapter, verse.Verse);

    public static PassageSpan Verses(VerseRef first, VerseRef last) => new(first.Book, first.Chapter, first.Verse, last.Chapter, last.Verse);
}

/// <summary>
/// A reference read from what someone typed: "John 3:16", "Jn 3:16-18", "1 Cor 13:4–8",
/// "Psalm 23", "Ps 23:1-3, 5", "Rom 8:28,31-39", "Gen 1", "John 3:16; Rom 5:8". Book names can be
/// full, short ("Jn", "Ps", "1 Cor"), numbered in Roman or Arabic ("I John", "1st John"), in any
/// case, with or without a space before the chapter ("jn3:16"). Ranges take a hyphen or a dash;
/// "John 3 v 16" and "John 3.16" work too, and a translation at the end ("John 3:16 KJV") is kept.
/// Windows only: the Mac's <see cref="ScriptureReference"/> finds references in text but doesn't read them.
/// </summary>
public sealed record PassageReference(IReadOnlyList<PassageSpan> Spans, string? Translation = null)
{
    /// <summary>The reference read from <paramref name="text"/>, or null when it isn't one (then it's a word search).</summary>
    public static PassageReference? Parse(string text) => Parser.Parse(text);

    /// <summary>A single verse.</summary>
    public static PassageReference Of(VerseRef verse) => new([PassageSpan.Verse(verse)]);

    /// <summary>"John 3:16–18", "Psalm 23", "Romans 8:28, 31–39", "John 3:16; Romans 5:8".</summary>
    public string Display => Format(Spans);

    public override string ToString() => Display;

    /// <summary>Writes spans the way a reference is usually printed: en dashes, the book named once.</summary>
    public static string Format(IReadOnlyList<PassageSpan> spans)
    {
        var text = new StringBuilder();
        PassageSpan? previous = null;
        foreach (var group in Runs(spans))
        {
            var book = BibleBooks.All[group[0].Book];
            var severalChapters = group.Any(s => s.LastChapter != s.FirstChapter) || group.Select(s => s.FirstChapter).Distinct().Count() > 1;
            if (text.Length > 0) text.Append("; ");
            text.Append(book.DisplayName(severalChapters)).Append(' ');
            previous = null;
            foreach (var span in group)
            {
                if (previous is not null)
                {
                    // Verses of the chapter just named need only their numbers: "8:28, 31–39".
                    var sameChapter = !span.IsWholeChapters && !previous.IsWholeChapters
                        && span.FirstChapter == previous.LastChapter && span.LastChapter == span.FirstChapter;
                    text.Append(sameChapter || span.IsWholeChapters && previous.IsWholeChapters ? ", " : "; ");
                    text.Append(sameChapter ? VersesOnly(span) : Whole(span));
                }
                else
                {
                    text.Append(Whole(span));
                }
                previous = span;
            }
        }
        return text.ToString();
    }

    private static string Whole(PassageSpan span)
    {
        if (span.IsWholeChapters)
            return span.LastChapter == span.FirstChapter ? N(span.FirstChapter) : $"{N(span.FirstChapter)}–{N(span.LastChapter)}";
        var start = $"{N(span.FirstChapter)}:{N(span.FirstVerse ?? 1)}";
        if (span.LastChapter != span.FirstChapter) return $"{start}–{N(span.LastChapter)}:{N(span.LastVerse ?? 1)}";
        return span.LastVerse is { } last && last != span.FirstVerse ? $"{start}–{N(last)}" : start;
    }

    private static string VersesOnly(PassageSpan span) =>
        span.LastVerse is { } last && last != span.FirstVerse ? $"{N(span.FirstVerse ?? 1)}–{N(last)}" : N(span.FirstVerse ?? 1);

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

    // Consecutive spans in the same book.
    private static List<List<PassageSpan>> Runs(IReadOnlyList<PassageSpan> spans)
    {
        var runs = new List<List<PassageSpan>>();
        foreach (var span in spans)
        {
            if (runs.Count > 0 && runs[^1][0].Book == span.Book) runs[^1].Add(span);
            else runs.Add([span]);
        }
        return runs;
    }

    /// <summary>
    /// Reads a reference. Pieces between semicolons each start with a book or carry on in the
    /// last one ("John 3:16; 4:1"). Inside a piece, commas separate items; a bare number after a
    /// verse is another verse of that chapter ("8:28, 31"), otherwise it's a chapter ("Psalm 23, 24").
    /// </summary>
    private static class Parser
    {
        private enum Kind { Number, Separator, Dash, Comma }

        private readonly record struct Token(Kind Kind, int Value = 0);

        public static PassageReference? Parse(string text)
        {
            var (body, translation) = TrailingTranslation(text.Trim());
            var spans = new List<PassageSpan>();
            BibleBook? book = null;
            foreach (var raw in body.Split(';'))
            {
                var piece = raw.Trim();
                if (piece.Length == 0) continue;
                var (named, rest, exact) = ReadBook(piece);
                if (named is null)
                {
                    // No book: carry on in the last one, or it isn't a reference at all.
                    if (book is null || !char.IsDigit(piece[0])) return null;
                    rest = piece;
                }
                else
                {
                    book = named;
                }
                if (rest.Trim().Length == 0)
                {
                    // Just a book ("John"): its first chapter, as ProPresenter does. A name cut short
                    // ("Judge") needs its chapter first: on its own it could be a word to search for.
                    if (!exact) return null;
                    spans.Add(new PassageSpan(book.Index, 1, null, 1, null));
                    continue;
                }
                if (Tokens(rest) is not { } tokens || !Items(book, tokens, spans)) return null;
            }
            return spans.Count == 0 ? null : new PassageReference(spans, translation);
        }

        /// <summary>
        /// The book a piece starts with and what follows it; null when it starts with no book.
        /// <c>Exact</c> is false for a name cut short ("Philipp").
        /// </summary>
        private static (BibleBook? Book, string After, bool Exact) ReadBook(string piece)
        {
            var i = 0;
            var ordinal = "";
            // A leading 1, 2 or 3 is the book's number when a name follows it ("1 Cor", "2jn").
            if (piece[0] is '1' or '2' or '3')
            {
                var j = 1;
                while (j < piece.Length && piece[j] == ' ') j++;
                if (j < piece.Length && char.IsLetter(piece[j]))
                {
                    ordinal = piece[..1];
                    i = j;
                }
            }
            var start = i;
            while (i < piece.Length && (char.IsLetter(piece[i]) || piece[i] is ' ' or '.' or '\'' or '’')) i++;
            var name = piece[start..i];
            if (name.Trim().Length == 0) return (null, piece, false);
            // "Matthew 3 v 28": the "v" comes after the chapter, so the name never takes it in.
            var book = BibleBooks.Find(ordinal + name);
            return book is null ? (null, piece, false) : (book, piece[i..], BibleBooks.FindExact(ordinal + name) is not null);
        }

        private static List<Token>? Tokens(string rest)
        {
            var tokens = new List<Token>();
            var i = 0;
            while (i < rest.Length)
            {
                var c = rest[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (char.IsDigit(c))
                {
                    var start = i;
                    while (i < rest.Length && char.IsDigit(rest[i])) i++;
                    if (!int.TryParse(rest.AsSpan(start, i - start), NumberStyles.None, CultureInfo.InvariantCulture, out var value)) return null;
                    tokens.Add(new Token(Kind.Number, value));
                    // A part of a verse ("16a", "16b"): the whole verse is shown.
                    if (i < rest.Length && rest[i] is 'a' or 'b' or 'c' && (i + 1 == rest.Length || !char.IsLetter(rest[i + 1]))) i++;
                    continue;
                }
                if (c is ':' or '.') { tokens.Add(new Token(Kind.Separator)); i++; continue; }
                if (c is '-' or '–' or '—' or '‐' or '‑' or '−') { tokens.Add(new Token(Kind.Dash)); i++; continue; }
                if (c is ',' or '&') { tokens.Add(new Token(Kind.Comma)); i++; continue; }
                if (char.IsLetter(c))
                {
                    var start = i;
                    while (i < rest.Length && char.IsLetter(rest[i])) i++;
                    var word = rest[start..i].ToLowerInvariant();
                    if (i < rest.Length && rest[i] == '.') i++;
                    switch (word)
                    {
                        case "v" or "vs" or "vv" or "ver" or "verse" or "verses":
                            tokens.Add(new Token(Kind.Separator));
                            break;
                        case "to" or "through" or "thru":
                            tokens.Add(new Token(Kind.Dash));
                            break;
                        case "and":
                            tokens.Add(new Token(Kind.Comma));
                            break;
                        default:
                            return null; // other words: it's a sentence, not a reference
                    }
                    continue;
                }
                return null;
            }
            // A separator or dash at the end is someone still typing ("John 3:", "Ps 23:1-"): read what's there.
            while (tokens.Count > 0 && tokens[^1].Kind != Kind.Number) tokens.RemoveAt(tokens.Count - 1);
            return tokens.Count == 0 ? null : tokens;
        }

        private static bool Items(BibleBook book, List<Token> tokens, List<PassageSpan> spans)
        {
            var at = 0;
            int? chapter = null; // the chapter a bare number's verse belongs to, once one is named
            var first = true;
            while (at < tokens.Count)
            {
                if (!first)
                {
                    if (tokens[at].Kind != Kind.Comma) return false;
                    at++;
                }
                // A trailing comma ("Ps 23:1,") is someone still typing: ignore it.
                if (at >= tokens.Count) break;
                if (!Number(tokens, ref at, out var a)) return false;
                var onlyItem = first && at == tokens.Count;
                first = false;
                if (At(tokens, at, Kind.Separator))
                {
                    at++;
                    if (!Number(tokens, ref at, out var v)) return false;
                    chapter = a;
                    var (endChapter, endVerse) = (a, v);
                    if (At(tokens, at, Kind.Dash))
                    {
                        at++;
                        if (!Number(tokens, ref at, out var x)) return false;
                        if (At(tokens, at, Kind.Separator))
                        {
                            at++;
                            if (!Number(tokens, ref at, out var y)) return false;
                            (endChapter, endVerse) = (x, y);
                            chapter = x;
                        }
                        else
                        {
                            endVerse = x;
                        }
                    }
                    spans.Add(Ordered(book, a, v, endChapter, endVerse));
                    continue;
                }
                // A bare number: a verse once a chapter is named, or in a one-chapter book ("Jude 3",
                // though "Jude 1" on its own is the whole book); otherwise a chapter.
                var verseOfChapter = chapter ?? (book.HasOneChapter && !(onlyItem && a == 1) ? 1 : null);
                if (At(tokens, at, Kind.Dash))
                {
                    at++;
                    if (!Number(tokens, ref at, out var x)) return false;
                    if (At(tokens, at, Kind.Separator))
                    {
                        // "John 3-4:5" or "Ps 23:6-24:2" style: from a chapter's start, or a verse, to a chapter and verse.
                        at++;
                        if (!Number(tokens, ref at, out var y)) return false;
                        spans.Add(verseOfChapter is { } c0 ? Ordered(book, c0, a, x, y) : Ordered(book, a, 1, x, y));
                        chapter = x;
                    }
                    else if (verseOfChapter is { } c)
                    {
                        spans.Add(Ordered(book, c, a, c, x));
                    }
                    else
                    {
                        spans.Add(new PassageSpan(book.Index, Math.Min(a, x), null, Math.Max(a, x), null));
                    }
                    continue;
                }
                spans.Add(verseOfChapter is { } vc
                    ? new PassageSpan(book.Index, vc, a, vc, a)
                    : new PassageSpan(book.Index, a, null, a, null));
            }
            return true;
        }

        // A range typed backwards ("3:18-16") is read forwards.
        private static PassageSpan Ordered(BibleBook book, int c1, int v1, int c2, int v2) =>
            (c2, v2).CompareTo((c1, v1)) < 0
                ? new PassageSpan(book.Index, c2, v2, c1, v1)
                : new PassageSpan(book.Index, c1, v1, c2, v2);

        private static bool At(List<Token> tokens, int at, Kind kind) => at < tokens.Count && tokens[at].Kind == kind;

        private static bool Number(List<Token> tokens, ref int at, out int value)
        {
            value = 0;
            if (!At(tokens, at, Kind.Number) || tokens[at].Value <= 0) return false;
            value = tokens[at].Value;
            at++;
            return true;
        }

        // "John 3:16 KJV", "John 3:16 (WEB)": the translation is taken off the end.
        private static (string Body, string? Translation) TrailingTranslation(string text)
        {
            var end = text.Length;
            while (end > 0 && text[end - 1] is ')' or ' ') end--;
            var start = end;
            while (start > 0 && char.IsLetter(text[start - 1])) start--;
            if (start == end || start == 0) return (text, null);
            var word = text[start..end].ToUpperInvariant();
            var known = ScriptureReference.Translations.Split('|').Concat(["WEB", "ASV"]).Contains(word);
            if (!known) return (text, null);
            var body = text[..start].TrimEnd(' ', '(');
            // Only when what's left still has a chapter: "Job" is a book, not a translation's leftovers.
            return body.Any(char.IsDigit) ? (body, word) : (text, null);
        }
    }
}
