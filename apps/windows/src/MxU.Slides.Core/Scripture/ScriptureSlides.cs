using System.Globalization;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Scripture;

/// <summary>Which of a theme's scripture designs the slides use.</summary>
public enum ScriptureLayout { FullScreen, LowerThird, SideThird }

/// <summary>How a passage becomes slides.</summary>
public sealed record ScriptureSlideOptions
{
    public ScriptureLayout Layout { get; init; } = ScriptureLayout.FullScreen;

    /// <summary>At most this many verses on a slide; 0 = as many as fit (1 = one verse per slide).</summary>
    public int VersesPerSlide { get; init; }

    /// <summary>Small raised verse numbers before each verse ("¹⁶ For God so loved…").</summary>
    public bool VerseNumbers { get; init; } = true;

    /// <summary>The reference line under the verse ("John 3:16–17 KJV").</summary>
    public bool ShowReference { get; init; } = true;
}

/// <summary>One slide's worth of a passage: its words, its reference line, and the verses it covers.</summary>
public sealed record ScripturePage(string Text, string Reference, VerseRef First, VerseRef Last);

/// <summary>
/// Turns a passage into an ordinary presentation, the kind any song or deck is, so it can be
/// saved, put in a service, opened on a Mac and restyled by changing its theme. Each slide uses
/// one of the theme's existing scripture designs ("Verse + Reference", "… (Lower Third)",
/// "… (Side Third)": see Library/StarterPacks.cs) and fills its "Verse" and "Reference" boxes;
/// nothing new is written into the document. Windows only.
/// <para>
/// <b>Splitting.</b> A long passage is cut into slides by length, measured the way the outputs
/// will draw it: the app hands in its text measurer (TextLayout.OverflowsFrame), and verses are
/// added to a slide while they still fit the design's verse box at a readable size (the design's
/// own size, or down to <see cref="ReadableShrink"/> of it when the design shrinks text to fit).
/// A verse too long for one slide is cut between words, at a comma or full stop when one is near.
/// Without a measurer (unit tests, the Local API) an estimate from the font size stands in.
/// </para>
/// </summary>
public static class ScriptureSlides
{
    public const string FullScreenDesign = "Verse + Reference";
    public const string LowerThirdDesign = "Verse + Reference (Lower Third)";
    public const string SideThirdDesign = "Verse + Reference (Side Third)";

    /// <summary>How far a design that shrinks text may shrink a slide's verses before they go onto another slide.</summary>
    public const double ReadableShrink = 0.85;

    public static string DesignName(ScriptureLayout layout) => layout switch
    {
        ScriptureLayout.LowerThird => LowerThirdDesign,
        ScriptureLayout.SideThird => SideThirdDesign,
        _ => FullScreenDesign,
    };

    /// <summary>
    /// The theme's design for <paramref name="layout"/>, by name. A theme without one (made on a Mac,
    /// say) gets its first design with a "Verse" box, then its first design of all.
    /// </summary>
    public static Slide? Design(Theme? theme, ScriptureLayout layout)
    {
        if (theme?.Slides is not { Count: > 0 } designs) return null;
        var name = DesignName(layout);
        return designs.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? designs.FirstOrDefault(d => d.Objects.Any(o => o.ObjectKind == SlideObjectKind.Text && string.Equals(o.Name, "Verse", StringComparison.OrdinalIgnoreCase)))
            ?? designs[0];
    }

    /// <summary>"¹⁶": a verse number in Unicode's superscript digits, which every font can show and the Mac reads as plain text.</summary>
    public static string Superscript(int number)
    {
        const string digits = "⁰¹²³⁴⁵⁶⁷⁸⁹";
        return string.Concat(number.ToString(CultureInfo.InvariantCulture).Select(d => digits[d - '0']));
    }

    /// <summary>The passage cut into slides.</summary>
    /// <param name="overflows">The app's text measurer: true when text doesn't fit a frame of that size. Null estimates.</param>
    public static IReadOnlyList<ScripturePage> Pages(PassageText passage, Theme? theme, ScriptureSlideOptions options,
        Func<StyledText, SceneSize, bool>? overflows = null)
    {
        var fits = Fitter(theme, options.Layout, overflows);
        var verses = passage.Verses;
        // A no-break space keeps a verse number with its first word.
        var texts = verses.Select(v => options.VerseNumbers ? $"{Superscript(v.Ref.Verse)} {v.Text}" : v.Text).ToList();
        var pages = new List<ScripturePage>();
        var at = 0;
        while (at < verses.Count)
        {
            if (!fits(texts[at]))
            {
                // One verse is more than a slide holds: it goes over several, each with its reference.
                foreach (var part in SplitLong(texts[at], fits))
                    pages.Add(new ScripturePage(part, ReferenceLine(passage, [verses[at].Ref], options), verses[at].Ref, verses[at].Ref));
                at++;
                continue;
            }
            // A new chapter starts a new slide (and a new section), as a printed Bible starts a new paragraph.
            var sameChapter = verses.Skip(at).TakeWhile(v => v.Ref.Book == verses[at].Ref.Book && v.Ref.Chapter == verses[at].Ref.Chapter).Count();
            var most = options.VersesPerSlide > 0 ? Math.Min(options.VersesPerSlide, sameChapter) : sameChapter;
            var count = Largest(most, n => fits(Join(texts, at, n)));
            var covered = verses.Skip(at).Take(count).Select(v => v.Ref).ToList();
            pages.Add(new ScripturePage(Join(texts, at, count), ReferenceLine(passage, covered, options), covered[0], covered[^1]));
            at += count;
        }
        return pages;
    }

    /// <summary>
    /// A presentation of the passage in <paramref name="theme"/>: one slide per page, each on the
    /// layout's design, with a section per chapter ("John 3"), named like "John 3:16–18 (KJV)".
    /// </summary>
    public static Presentation Build(PassageText passage, Theme theme, ScriptureSlideOptions options,
        Func<StyledText, SceneSize, bool>? overflows = null) =>
        Build(passage, theme, options, Pages(passage, theme, options, overflows));

    /// <summary>The same, from pages already worked out (the Bible window keeps them to know which slide each verse is on).</summary>
    public static Presentation Build(PassageText passage, Theme theme, ScriptureSlideOptions options, IReadOnlyList<ScripturePage> pages)
    {
        var design = Design(theme, options.Layout);
        var sections = new List<PresentationSection>();
        var slides = new List<Slide>();
        foreach (var page in pages)
        {
            var chapter = $"{page.First.BookInfo.DisplayName(severalChapters: false)} {page.First.Chapter}";
            if (sections.Count == 0 || sections[^1].Name != chapter)
                sections.Add(new PresentationSection { Id = LibraryStore.NewId(), Name = chapter });
            var objects = new List<SlideObject> { Text("Verse", page.Text) };
            if (options.ShowReference) objects.Add(Text("Reference", page.Reference));
            slides.Add(new Slide
            {
                Id = LibraryStore.NewId(),
                Name = "",
                SectionId = sections[^1].Id,
                ThemeSlideName = design?.Name,
                Objects = objects,
            });
        }
        return new Presentation
        {
            Id = LibraryStore.NewId(),
            Name = passage.Title,
            PresentationKind = PresentationKind.Deck,
            ThemeId = theme.Id,
            Slides = slides,
            Sections = sections,
            Origin = new PresentationOrigin
            {
                // The Mac shows this as "Made in MxU Slides · Bible (KJV)".
                Source = PresentationOriginSource.MadeHere,
                Detail = $"Bible ({passage.Translation.Abbreviation})",
                CreatedAt = DateTimeOffset.Now.ToString("O"),
            },
        };
    }

    // A text box with no position or style of its own: the design's box of the same name places
    // and styles it, so changing the theme restyles the whole passage.
    private static SlideObject Text(string name, string text) => new()
    {
        Id = LibraryStore.NewId(), ObjectKind = SlideObjectKind.Text, Name = name, Text = text,
    };

    private static string Join(List<string> texts, int at, int count) => string.Join(" ", texts.Skip(at).Take(count));

    /// <summary>"John 3:16–17 KJV" for the verses on one slide (a gap that's only absent verses doesn't break the range).</summary>
    private static string ReferenceLine(PassageText passage, IReadOnlyList<VerseRef> verses, ScriptureSlideOptions options)
    {
        var spans = new List<PassageSpan>();
        var (first, last) = (verses[0], verses[0]);
        foreach (var verse in verses.Skip(1))
        {
            if (passage.Reference.Spans.Any(s => Covers(s, last) && Covers(s, verse)))
            {
                last = verse;
                continue;
            }
            spans.Add(PassageSpan.Verses(first, last));
            (first, last) = (verse, verse);
        }
        spans.Add(PassageSpan.Verses(first, last));
        return $"{PassageReference.Format(spans)} {passage.Translation.Abbreviation}";
    }

    private static bool Covers(PassageSpan span, VerseRef verse) =>
        span.Book == verse.Book
        && (verse.Chapter, verse.Verse).CompareTo((span.FirstChapter, span.FirstVerse ?? 1)) >= 0
        && (verse.Chapter, verse.Verse).CompareTo((span.LastChapter, span.LastVerse ?? int.MaxValue)) <= 0;

    /// <summary>The largest n in 1…<paramref name="most"/> that <paramref name="fits"/> (at least 1): doubling, then halving, so a long chapter needs only a few measurements.</summary>
    private static int Largest(int most, Func<int, bool> fits)
    {
        if (most <= 1) return 1;
        var good = 1;
        var bad = most + 1;
        for (var n = 2; n <= most; n *= 2)
        {
            if (!fits(n))
            {
                bad = n;
                break;
            }
            good = n;
        }
        if (bad == most + 1 && good < most && fits(most)) return most;
        if (bad == most + 1) bad = most;
        while (bad - good > 1)
        {
            var mid = (good + bad) / 2;
            if (fits(mid)) good = mid;
            else bad = mid;
        }
        return good;
    }

    // A verse too long for one slide, cut between words: each part as long as fits, ending at
    // punctuation when there's some in its last two fifths.
    private static List<string> SplitLong(string text, Func<string, bool> fits)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var parts = new List<string>();
        var at = 0;
        while (at < words.Length)
        {
            var count = Largest(words.Length - at, n => fits(string.Join(' ', words, at, n)));
            if (at + count < words.Length)
            {
                for (var end = at + count - 1; end >= at + (int)Math.Ceiling(count * 0.6); end--)
                {
                    if (words[end][^1] is ',' or ';' or ':' or '.' or '?' or '!')
                    {
                        count = end - at + 1;
                        break;
                    }
                }
            }
            parts.Add(string.Join(' ', words, at, count));
            at += count;
        }
        return parts;
    }

    /// <summary>
    /// "Does this text fit the layout's verse box?", from the theme's design: its box's size and
    /// text style, at a readable size. With no theme or design, a 1920×1080 slide's middle at 64 pt.
    /// </summary>
    private static Func<string, bool> Fitter(Theme? theme, ScriptureLayout layout, Func<StyledText, SceneSize, bool>? overflows)
    {
        var design = Design(theme, layout);
        var boxes = design?.Objects.Where(o => o.ObjectKind == SlideObjectKind.Text).ToList() ?? [];
        var box = boxes.FirstOrDefault(o => string.Equals(o.Name, "Verse", StringComparison.OrdinalIgnoreCase)) ?? boxes.FirstOrDefault();
        var probe = new SlideObject { Id = "scripture-probe", ObjectKind = SlideObjectKind.Text, Name = "Verse", Text = "" };
        var style = box is null
            ? new StyledText { FontName = theme?.FontFamily ?? "HelveticaNeue", FontSize = 64, LineHeightMultiple = 1.15 }
            : SlideSceneBuilder.StyledTextFor(probe, theme, box);
        var frame = new SceneSize(box?.Width ?? 1460, box?.Height ?? 600);
        // Text that pages or shrinks to fit is measured at the smallest size we call readable, so
        // the slide never relies on pages or tiny text.
        style = style with { MinFontSize = Math.Max(style.MinFontSize, style.FontSize * ReadableShrink) };
        var measure = overflows ?? EstimatedOverflow;
        return text => !measure(style with { String = text }, frame);
    }

    /// <summary>
    /// A rough stand-in for the real measurer: an average letter is half the font size wide and a
    /// line 1.2 × the line height; words wrap whole. Errs on the side of fewer words per slide.
    /// </summary>
    public static bool EstimatedOverflow(StyledText text, SceneSize frame)
    {
        var size = text.AutoShrink ? Math.Min(text.FontSize, Math.Max(text.MinFontSize, 1)) : text.FontSize;
        var width = Math.Max(frame.Width - text.InsetLeft - text.InsetRight, 1);
        var height = Math.Max(frame.Height - text.InsetTop - text.InsetBottom, 1);
        var perLine = Math.Max(1, (int)(width / (size * 0.5)));
        var lineHeight = size * Math.Max(text.LineHeightMultiple, 0.5) * 1.2;
        var lines = 0;
        foreach (var paragraph in text.String.Split('\n'))
        {
            lines++;
            var used = 0;
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var length = new StringInfo(word).LengthInTextElements; // a superscript digit is one letter
                if (used > 0 && used + 1 + length > perLine)
                {
                    lines++;
                    used = 0;
                }
                used += (used > 0 ? 1 : 0) + length;
                while (used > perLine)
                {
                    lines++;
                    used -= perLine;
                }
            }
        }
        return lines * lineHeight > height + 0.5;
    }
}
