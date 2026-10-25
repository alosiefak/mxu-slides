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
public sealed record ScripturePage(string Text, string Reference, VerseRef First, VerseRef Last)
{
    /// <summary>The same verses in a second translation, shown beside <see cref="Text"/>; null when there's one translation.</summary>
    public string? Beside { get; init; }

    /// <summary>A smaller font size for the reference line when it's too long for the design's box at its own; null when it fits.</summary>
    public double? ReferenceFontSize { get; init; }
}

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
/// The reference line is measured too, and made smaller when it won't sit on one line of its box
/// (<see cref="ReferenceSizer"/>). Without a measurer (unit tests, the Local API) an estimate
/// from the font size stands in.
/// </para>
/// <para>
/// <b>Two translations side by side.</b> Given a second translation, each slide gets two "Verse"
/// boxes, one per translation, in the two halves of the design's verse box (<see cref="Halves"/>).
/// Both boxes take the design's style; each is given only a position, a size and a slightly smaller
/// font size, fields any slide object has, so the Mac draws them the same. A slide holds as many
/// verses as fit on both sides.
/// </para>
/// </summary>
public static class ScriptureSlides
{
    public const string FullScreenDesign = "Verse + Reference";
    public const string LowerThirdDesign = "Verse + Reference (Lower Third)";
    public const string SideThirdDesign = "Verse + Reference (Side Third)";

    /// <summary>How far a design that shrinks text may shrink a slide's verses before they go onto another slide.</summary>
    public const double ReadableShrink = 0.85;

    /// <summary>
    /// Side by side, each translation's words at this share of the design's size: each has half the
    /// box, and at full size a verse would rarely fit in half. Three quarters still reads from the back.
    /// </summary>
    public const double SideBySideScale = 0.75;

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
    /// <param name="beside">The same passage in a second translation, to show side by side; null for one translation.</param>
    public static IReadOnlyList<ScripturePage> Pages(PassageText passage, Theme? theme, ScriptureSlideOptions options,
        Func<StyledText, SceneSize, bool>? overflows = null, PassageText? beside = null)
    {
        var (fits, guess) = Fitter(theme, options.Layout, overflows, sideBySide: beside is not null);
        var verses = passage.Verses;
        // A no-break space (U+00A0) keeps a verse number with its first word.
        string Words(VerseRef at, string text) => options.VerseNumbers ? $"{Superscript(at.Verse)} {text}" : text;
        var texts = verses.Select(v => Words(v.Ref, v.Text)).ToList();
        // Side by side, the first translation decides which verses there are; the second shows its
        // own words for them, and nothing for one it leaves out.
        var twins = beside?.Verses.ToDictionary(v => v.Ref, v => v.Text);
        var others = twins is null ? null : verses.Select(v => twins.TryGetValue(v.Ref, out var t) ? Words(v.Ref, t) : "").ToList();
        bool Fit(int at, int n, Func<string, bool> test) => test(Join(texts, at, n)) && (others is null || test(Join(others, at, n)));
        var pages = new List<ScripturePage>();
        var at = 0;
        while (at < verses.Count)
        {
            if (!Fit(at, 1, fits))
            {
                // One verse is more than a slide holds: it goes over several, each with its reference.
                // Side by side, each translation is cut to fit its half, and the parts are paired up.
                var parts = SplitLong(texts[at], fits, guess);
                var twinParts = others is null ? null : SplitLong(others[at], fits, guess);
                var reference = ReferenceLine(passage, [verses[at].Ref], beside);
                for (var i = 0; i < Math.Max(parts.Count, twinParts?.Count ?? 0); i++)
                    pages.Add(new ScripturePage(parts.ElementAtOrDefault(i) ?? "", reference, verses[at].Ref, verses[at].Ref)
                    {
                        Beside = twinParts is null ? null : twinParts.ElementAtOrDefault(i) ?? "",
                    });
                at++;
                continue;
            }
            // A new chapter starts a new slide (and a new section), as a printed Bible starts a new paragraph.
            var sameChapter = verses.Skip(at).TakeWhile(v => v.Ref.Book == verses[at].Ref.Book && v.Ref.Chapter == verses[at].Ref.Chapter).Count();
            var most = options.VersesPerSlide > 0 ? Math.Min(options.VersesPerSlide, sameChapter) : sameChapter;
            var count = Largest(most, n => Fit(at, n, fits), Largest(most, n => Fit(at, n, guess)));
            var covered = verses.Skip(at).Take(count).Select(v => v.Ref).ToList();
            pages.Add(new ScripturePage(Join(texts, at, count), ReferenceLine(passage, covered, beside), covered[0], covered[^1])
            {
                Beside = others is null ? null : Join(others, at, count),
            });
            at += count;
        }
        if (!options.ShowReference) return pages;
        var referenceSize = ReferenceSizer(theme, options.Layout, overflows);
        return [.. pages.Select(p => p with { ReferenceFontSize = referenceSize(p.Reference) })];
    }

    /// <summary>
    /// A presentation of the passage in <paramref name="theme"/>: one slide per page, each on the
    /// layout's design, with a section per chapter ("John 3"), named like "John 3:16–18 (KJV)", or
    /// "John 3:16–18 (KJV, WEB)" with a second translation <paramref name="beside"/> it.
    /// </summary>
    public static Presentation Build(PassageText passage, Theme theme, ScriptureSlideOptions options,
        Func<StyledText, SceneSize, bool>? overflows = null, PassageText? beside = null) =>
        Build(passage, theme, options, Pages(passage, theme, options, overflows, beside), beside);

    /// <summary>The same, from pages already worked out (the Bible window keeps them to know which slide each verse is on).</summary>
    public static Presentation Build(PassageText passage, Theme theme, ScriptureSlideOptions options, IReadOnlyList<ScripturePage> pages,
        PassageText? beside = null)
    {
        var design = Design(theme, options.Layout);
        (SceneRect First, SceneRect Second, double FontSize)? halves = beside is null ? null : Halves(theme, design);
        var sections = new List<PresentationSection>();
        var slides = new List<Slide>();
        foreach (var page in pages)
        {
            var chapter = $"{page.First.BookInfo.DisplayName(severalChapters: false)} {page.First.Chapter}";
            if (sections.Count == 0 || sections[^1].Name != chapter)
                sections.Add(new PresentationSection { Id = LibraryStore.NewId(), Name = chapter });
            var objects = halves is { } two
                ? [Placed(Text("Verse", page.Text), two.First, two.FontSize), Placed(Text("Verse", page.Beside ?? ""), two.Second, two.FontSize)]
                : new List<SlideObject> { Text("Verse", page.Text) };
            if (options.ShowReference)
            {
                var reference = Text("Reference", page.Reference);
                if (page.ReferenceFontSize is { } smaller) reference.TextStyle = new TextStyle { FontSize = smaller };
                objects.Add(reference);
            }
            slides.Add(new Slide
            {
                Id = LibraryStore.NewId(),
                Name = "",
                SectionId = sections[^1].Id,
                ThemeSlideName = design?.Name,
                Objects = objects,
            });
        }
        var translations = Translations(passage, beside, ", ");
        return new Presentation
        {
            Id = LibraryStore.NewId(),
            Name = $"{passage.Reference.Display} ({translations})",
            PresentationKind = PresentationKind.Deck,
            ThemeId = theme.Id,
            Slides = slides,
            Sections = sections,
            Origin = new PresentationOrigin
            {
                // The Mac shows this as "Made in MxU Slides · Bible (KJV)".
                Source = PresentationOriginSource.MadeHere,
                Detail = $"Bible ({translations})",
                CreatedAt = DateTimeOffset.Now.ToString("O"),
            },
        };
    }

    // "KJV", or "KJV, WEB" side by side.
    private static string Translations(PassageText passage, PassageText? beside, string between) =>
        beside is null ? passage.Translation.Abbreviation : $"{passage.Translation.Abbreviation}{between}{beside.Translation.Abbreviation}";

    // A text box with no position or style of its own: the design's box of the same name places
    // and styles it, so changing the theme restyles the whole passage.
    private static SlideObject Text(string name, string text) => new()
    {
        Id = LibraryStore.NewId(), ObjectKind = SlideObjectKind.Text, Name = name, Text = text,
    };

    // Side by side: the box keeps the design's style, but has its own half of the verse box and a
    // smaller font size. Those are ordinary fields of any slide object.
    private static SlideObject Placed(SlideObject box, SceneRect frame, double fontSize)
    {
        (box.X, box.Y, box.Width, box.Height) = (frame.X, frame.Y, frame.Width, frame.Height);
        box.TextStyle = new TextStyle { FontSize = fontSize };
        return box;
    }

    /// <summary>The design's verse box: the one named "Verse", else its first text box; null when it has none.</summary>
    private static SlideObject? VerseBox(Slide? design)
    {
        var boxes = design?.Objects.Where(o => o.ObjectKind == SlideObjectKind.Text).ToList() ?? [];
        return boxes.FirstOrDefault(o => string.Equals(o.Name, "Verse", StringComparison.OrdinalIgnoreCase)) ?? boxes.FirstOrDefault();
    }

    // With no design to follow: a 1920×1080 slide's middle at 64 pt.
    private static readonly SceneRect NoDesignFrame = new(230, 240, 1460, 600);
    private const double NoDesignFontSize = 64;

    /// <summary>
    /// Side by side: the design's verse box cut in two with a gutter between, left and right when
    /// it's wide (full screen, lower third), else one above the other (the side third's square box,
    /// whose halves would be too narrow for more than a word or two a line), and the font size each
    /// half's words get (<see cref="SideBySideScale"/> of the design's).
    /// </summary>
    public static (SceneRect First, SceneRect Second, double FontSize) Halves(Theme? theme, Slide? design)
    {
        var box = VerseBox(design);
        var frame = box is null ? NoDesignFrame : SlideSceneBuilder.Frame(box);
        var size = box is null ? NoDesignFontSize : SlideSceneBuilder.StyledTextFor(box, theme, box).FontSize;
        var fontSize = Math.Round(size * SideBySideScale);
        if (frame.Width >= frame.Height * 2)
        {
            var gutter = Math.Round(frame.Width * 0.05);
            var width = (frame.Width - gutter) / 2;
            return (frame with { Width = width }, frame with { X = frame.X + width + gutter, Width = width }, fontSize);
        }
        var gap = Math.Round(frame.Height * 0.06);
        var height = (frame.Height - gap) / 2;
        return (frame with { Height = height }, frame with { Y = frame.Y + height + gap, Height = height }, fontSize);
    }

    // The verses' words in a row; a verse the second translation leaves out adds nothing.
    private static string Join(List<string> texts, int at, int count) =>
        string.Join(" ", texts.Skip(at).Take(count).Where(t => t.Length > 0));

    /// <summary>
    /// "John 3:16–17 KJV" for the verses on one slide (a gap that's only absent verses doesn't break
    /// the range); "John 3:16–17 KJV · WEB" side by side.
    /// </summary>
    private static string ReferenceLine(PassageText passage, IReadOnlyList<VerseRef> verses, PassageText? beside)
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
        return $"{PassageReference.Format(spans)} {Translations(passage, beside, " · ")}";
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
        for (var n = 2; ; n = Math.Min(most, n * 2))
        {
            if (!fits(n)) return Between(good, n, fits);
            if (n == most) return most;
            good = n;
        }
    }

    /// <summary>
    /// The same, starting from a <paramref name="guess"/> (the estimate's answer): the real
    /// measurer then usually needs only two measurements, "the guess fits" and "one more doesn't",
    /// stepping further, in growing strides, only when the guess was off.
    /// </summary>
    private static int Largest(int most, Func<int, bool> fits, int guess)
    {
        if (most <= 1) return 1;
        guess = Math.Clamp(guess, 1, most);
        var stride = 1;
        if (guess == 1 || fits(guess))
        {
            var good = guess;
            while (good < most)
            {
                var next = Math.Min(most, good + stride);
                if (!fits(next)) return Between(good, next, fits);
                good = next;
                stride *= 2;
            }
            return good;
        }
        var bad = guess;
        while (true)
        {
            var next = Math.Max(1, bad - stride);
            if (next == 1 || fits(next)) return Between(next, bad, fits);
            bad = next;
            stride *= 2;
        }
    }

    // The largest n that fits, knowing good fits and bad doesn't.
    private static int Between(int good, int bad, Func<int, bool> fits)
    {
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
    private static List<string> SplitLong(string text, Func<string, bool> fits, Func<string, bool> guess)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var parts = new List<string>();
        var at = 0;
        while (at < words.Length)
        {
            var most = words.Length - at;
            var count = Largest(most, n => fits(string.Join(' ', words, at, n)), Largest(most, n => guess(string.Join(' ', words, at, n))));
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
    /// "Does this text fit the layout's verse box?" (and a quick guess at it), from the theme's design: its box's size and
    /// text style, at a readable size. Side by side, the question is about one half, at its smaller size
    /// (<see cref="Halves"/>). With no theme or design, a 1920×1080 slide's middle at 64 pt.
    /// </summary>
    private static (Func<string, bool> Fits, Func<string, bool> Guess) Fitter(Theme? theme, ScriptureLayout layout,
        Func<StyledText, SceneSize, bool>? overflows, bool sideBySide)
    {
        var design = Design(theme, layout);
        var box = VerseBox(design);
        var probe = new SlideObject { Id = "scripture-probe", ObjectKind = SlideObjectKind.Text, Name = "Verse", Text = "" };
        var frame = new SceneSize(box?.Width ?? NoDesignFrame.Width, box?.Height ?? NoDesignFrame.Height);
        if (sideBySide)
        {
            // Measured exactly as Build places it: the first half's box, with its font size.
            var (half, _, fontSize) = Halves(theme, design);
            Placed(probe, half, fontSize);
            frame = new SceneSize(half.Width, half.Height);
        }
        var style = box is null
            ? new StyledText { FontName = theme?.FontFamily ?? "HelveticaNeue", FontSize = probe.TextStyle?.FontSize ?? NoDesignFontSize, LineHeightMultiple = 1.15 }
            : SlideSceneBuilder.StyledTextFor(probe, theme, box);
        // Text that shrinks to fit is measured at the smallest size we call readable, so the slide never
        // relies on tiny text or on turning pages. Measuring once at that size is the same question as
        // "does it fit, shrinking no further than that?", and a tenth of the work: the shrinking itself
        // is a search over sizes. Side by side, the words are already smaller, so they're measured
        // at that size and shrink no further.
        if (sideBySide) style = style with { AutoShrink = false };
        if (style.AutoShrink)
        {
            var scale = Math.Max(ReadableShrink, Math.Min(1, style.MinFontSize / Math.Max(style.FontSize, 1)));
            style = style with
            {
                AutoShrink = false, FontSize = style.FontSize * scale, Tracking = style.Tracking * scale,
                ParagraphSpacing = style.ParagraphSpacing * scale,
            };
        }
        // The estimate is cheap arithmetic; the real measurer lays the text out. The estimate's answer
        // is where the real one starts, so it confirms more than it searches.
        var measure = overflows ?? EstimatedOverflow;
        return (text => !measure(style with { String = text }, frame), text => !EstimatedOverflow(style with { String = text }, frame));
    }

    /// <summary>
    /// The font size a reference line needs to sit on one line of the design's "Reference" box:
    /// null when the design's own size does ("John 3:16 KJV"), else the largest of 90, 80 and 70 %
    /// of it that does, or 60 % ("1 Thessalonians 4:13–18 KJV", or two translations, on a lower
    /// third's small plate). One line, because a reference wrapped onto a second line overflows a
    /// plate made for one. Each different line is measured once.
    /// </summary>
    private static Func<string, double?> ReferenceSizer(Theme? theme, ScriptureLayout layout, Func<StyledText, SceneSize, bool>? overflows)
    {
        var box = Design(theme, layout)?.Objects.FirstOrDefault(o =>
            o.ObjectKind == SlideObjectKind.Text && string.Equals(o.Name, "Reference", StringComparison.OrdinalIgnoreCase));
        if (box is null) return _ => null;
        var probe = new SlideObject { Id = "reference-probe", ObjectKind = SlideObjectKind.Text, Name = "Reference", Text = "" };
        var style = SlideSceneBuilder.StyledTextFor(probe, theme, box) with { AutoShrink = false };
        var frame = SlideSceneBuilder.Frame(box);
        var measure = overflows ?? EstimatedOverflow;
        bool OneLine(string text, double size)
        {
            // Room for a line and a half: one fits, two don't.
            var height = Math.Min(frame.Height, size * Math.Max(style.LineHeightMultiple, 1) * 1.6 + style.InsetTop + style.InsetBottom);
            var sized = style with { String = text, FontSize = size, Tracking = style.Tracking * size / Math.Max(style.FontSize, 1) };
            return !measure(sized, new SceneSize(frame.Width, height));
        }
        var known = new Dictionary<string, double?>();
        return text =>
        {
            if (known.TryGetValue(text, out var size)) return size;
            if (!OneLine(text, style.FontSize))
            {
                size = Math.Round(style.FontSize * 0.6);
                foreach (var scale in new[] { 0.9, 0.8, 0.7 })
                {
                    if (!OneLine(text, Math.Round(style.FontSize * scale))) continue;
                    size = Math.Round(style.FontSize * scale);
                    break;
                }
            }
            return known[text] = size;
        };
    }

    /// <summary>
    /// A rough stand-in for the real measurer: an average letter is half the font size wide (a
    /// capital a little more, plus any tracking) and a line 1.2 × the line height; words wrap
    /// whole. Errs on the side of fewer words per slide.
    /// </summary>
    public static bool EstimatedOverflow(StyledText text, SceneSize frame)
    {
        var size = text.AutoShrink ? Math.Min(text.FontSize, Math.Max(text.MinFontSize, 1)) : text.FontSize;
        var width = Math.Max(frame.Width - text.InsetLeft - text.InsetRight, 1);
        var height = Math.Max(frame.Height - text.InsetTop - text.InsetBottom, 1);
        var letter = size * (text.Transform == SceneTextTransform.Uppercase ? 0.65 : 0.5) + Math.Max(text.Tracking, 0);
        var perLine = Math.Max(1, (int)(width / letter));
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
