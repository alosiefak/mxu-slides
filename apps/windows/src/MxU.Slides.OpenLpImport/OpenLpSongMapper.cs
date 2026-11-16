using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;

namespace MxU.Slides.OpenLpImport;

/// <summary>One row of OpenLP's <c>songs</c> table, with its authors' names looked up.</summary>
public sealed record OpenLpSong(
    long Id, string Title, string? AlternateTitle, string? Lyrics, string? VerseOrder,
    string? Copyright, string? CcliNumber, IReadOnlyList<string> Authors);

/// <summary>One row of OpenLP's <c>custom_slide</c> table (liturgy, announcements and the like).</summary>
public sealed record OpenLpCustomSlide(long Id, string Title, string? Text, string? Credits);

/// <summary>A mapped document (null when there was nothing to import) and what to tell the operator about it.</summary>
public sealed record OpenLpMapped(Presentation? Presentation, IReadOnlyList<string> Warnings);

/// <summary>
/// Turns OpenLP rows into the app's documents. A song is first written out as ChordPro text (a
/// plain-text song format the app already imports) and then handed to
/// <see cref="LyricTextImporter.MakePresentation"/>: the same door SongSelect and ChordPro files
/// come in by. So sections, slide phrasing, chords and the blank opening slide are made by the one
/// piece of code that already makes them, and an OpenLP song looks like any other song here.
/// </summary>
public static class OpenLpSongMapper
{
    /// <summary>The library folder custom slides land in.</summary>
    public const string CustomFolder = "OpenLP custom slides";

    /// <summary>The name of the arrangement made from OpenLP's verse order.</summary>
    public const string ArrangementName = "OpenLP order";

    /// <summary>What the song's "where it came from" note says.</summary>
    public const string OriginDetail = "OpenLP";

    private static string TypeName(char type) => type switch
    {
        'v' => "Verse",
        'c' => "Chorus",
        'b' => "Bridge",
        'p' => "Pre-Chorus",
        'i' => "Intro",
        'e' => "Ending",
        _ => "Other",
    };

    /// <summary>A verse that has words, with the section name it gets here.</summary>
    internal sealed record NamedVerse(OpenLpVerse Verse, string Name)
    {
        /// <summary>How OpenLP's verse order refers to it: the letter and the label, e.g. "v1".</summary>
        public string Tag => (Verse.Type + Verse.Label).ToLowerInvariant();
    }

    /// <summary>
    /// Section names for the verses that have words. Verses always keep their number ("Verse 1").
    /// A chorus, bridge… is numbered only when the song has more than one of them, so the usual
    /// song gets plain "Chorus" and "Bridge". Two verses stored under the same name (OpenLP allows
    /// it) are told apart with "(2)".
    /// </summary>
    internal static List<NamedVerse> Named(IReadOnlyList<OpenLpVerse> verses)
    {
        var withWords = verses.Where(verse => verse.Slides.Count > 0).ToList();
        var named = new List<NamedVerse>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var verse in withWords)
        {
            var alone = withWords.Count(other => other.Type == verse.Type) == 1;
            var name = TypeName(verse.Type) + (verse.Type != 'v' && alone && verse.Label == "1" ? "" : " " + verse.Label);
            var unique = name;
            for (var copy = 2; !used.Add(unique); copy++) unique = $"{name} ({copy})";
            named.Add(new NamedVerse(verse, unique));
        }
        return named;
    }

    public static OpenLpMapped MapSong(OpenLpSong song, string themeId = "", int linesPerSlide = 2)
    {
        var parsed = OpenLpLyrics.Parse(song.Lyrics);
        var warnings = parsed.Warnings.ToList();
        if (parsed.Unreadable) return new OpenLpMapped(null, warnings);
        var verses = Named(parsed.Verses);
        if (verses.Count == 0)
        {
            warnings.Add("it has no words, so it was not imported");
            return new OpenLpMapped(null, warnings);
        }

        var title = Title(song.Title, song.Id, "song");
        var ccli = Ccli(song, title);
        var text = ChordProText(title, ccli, verses, warnings);
        var presentation = LyricTextImporter.MakePresentation(text, title, themeId: themeId, linesPerSlide: linesPerSlide);
        // The title and credits are set from the database itself, not read back out of the text:
        // a copyright line can run over several lines, which a ChordPro directive can't hold.
        presentation.Name = title;
        presentation.Ccli = ccli;
        presentation.PresentationKind = PresentationKind.Song;
        presentation.Origin = new PresentationOrigin
        {
            // "LyricsFile" is the closest of the kinds the shared format allows (the Mac rejects a
            // document with a kind it doesn't know, so there can be no "OpenLP" kind).
            Source = PresentationOriginSource.LyricsFile,
            Detail = OriginDetail,
            CreatedAt = DateTimeOffset.Now.ToString("O"),
        };
        ApplyVerseOrder(presentation, verses, song.VerseOrder, warnings);
        return new OpenLpMapped(presentation, warnings);
    }

    /// <summary>
    /// A custom slide becomes an ordinary presentation in the "OpenLP custom slides" folder: one
    /// slide per OpenLP slide (and one more wherever OpenLP had a split mark). The words are not
    /// re-phrased the way lyrics are: liturgy and notices stay exactly as they were typed.
    /// </summary>
    public static OpenLpMapped MapCustom(OpenLpCustomSlide custom, string themeId = "")
    {
        var parsed = OpenLpLyrics.Parse(custom.Text);
        var warnings = parsed.Warnings.ToList();
        if (parsed.Unreadable) return new OpenLpMapped(null, warnings);
        var slides = new List<Slide>();
        foreach (var content in parsed.Verses.SelectMany(verse => verse.Slides))
        {
            // Chords make no sense on a notice, but a chord in brackets must still never be shown as words.
            var extracted = ChordMath.Extract(content);
            if (extracted.Text.Trim().Length == 0) continue;
            slides.Add(new Slide
            {
                Id = LibraryStore.NewId(), Name = "", ThemeSlideName = "Lyrics",
                Objects = [new SlideObject { Id = LibraryStore.NewId(), ObjectKind = SlideObjectKind.Text, Name = "Lyrics", Text = extracted.Text }],
            });
        }
        if (slides.Count == 0)
        {
            warnings.Add("it has no words, so it was not imported");
            return new OpenLpMapped(null, warnings);
        }
        var presentation = new Presentation
        {
            Id = LibraryStore.NewId(),
            Name = Title(custom.Title, custom.Id, "custom slide"),
            PresentationKind = PresentationKind.Deck,
            ThemeId = themeId,
            Folder = CustomFolder,
            Slides = slides,
            Origin = new PresentationOrigin
            {
                Source = PresentationOriginSource.LyricsFile, Detail = OriginDetail, CreatedAt = DateTimeOffset.Now.ToString("O"),
            },
        };
        return new OpenLpMapped(presentation, warnings);
    }

    private static string Title(string? raw, long id, string what)
    {
        var title = OneLine(raw);
        return title.Length > 0 ? title : $"Untitled {what} {id.ToString(CultureInfo.InvariantCulture)} (OpenLP)";
    }

    private static string OneLine(string? text) => Regex.Replace(text ?? "", @"\s+", " ").Trim();

    /// <summary>The song's credits in the app's own shape, or null when OpenLP had none.</summary>
    private static CCLIInfo? Ccli(OpenLpSong song, string title)
    {
        var digits = new string((song.CcliNumber ?? "").Where(char.IsAsciiDigit).ToArray());
        int? number = int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : null;
        var authors = string.Join(", ", song.Authors.Select(OneLine).Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));
        var copyright = (song.Copyright ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (number is null && authors.Length == 0 && copyright.Length == 0) return null;
        var year = Regex.Match(copyright, @"\b(?:19|20)\d{2}\b");
        return new CCLIInfo
        {
            SongNumber = number,
            SongTitle = title,
            Author = authors.Length == 0 ? null : authors,
            Copyright = copyright.Length == 0 ? null : copyright,
            CopyrightYear = year.Success ? int.Parse(year.Value, CultureInfo.InvariantCulture) : null,
        };
    }

    /// <summary>
    /// The song as ChordPro text: a few <c>{name: value}</c> lines for the title and credits, then
    /// each section between <c>{start_of_part: Verse 1}</c> and <c>{end_of_part}</c>, with a blank
    /// line wherever a new slide must start.
    /// </summary>
    internal static string ChordProText(string title, CCLIInfo? ccli, IReadOnlyList<NamedVerse> verses, List<string> warnings)
    {
        var text = new StringBuilder();
        text.Append("{title: ").Append(title).Append("}\n");
        if (ccli?.Author is { } author) text.Append("{artist: ").Append(author).Append("}\n");
        if (ccli?.Copyright is { } copyright) text.Append("{copyright: ").Append(OneLine(copyright)).Append("}\n");
        if (ccli?.SongNumber is { } number) text.Append("{ccli: ").Append(number.ToString(CultureInfo.InvariantCulture)).Append("}\n");
        var names = verses.Select(verse => verse.Name).ToList();
        foreach (var verse in verses)
        {
            text.Append("\n{start_of_part: ").Append(verse.Name).Append("}\n");
            text.Append(string.Join("\n\n", verse.Verse.Slides.Select(slide => string.Join("\n", slide.Split('\n').Select(line => SafeLine(line, names, warnings))))));
            text.Append("\n{end_of_part}\n");
        }
        return text.ToString();
    }

    /// <summary>
    /// Two kinds of lyric line would be misread on the way in. A line wholly inside braces looks
    /// like a ChordPro instruction and would vanish, so its braces become round brackets. A line
    /// that reads like a section name ("Chorus") starts a new section; it can't be disguised
    /// without changing the words, so the operator is told.
    /// </summary>
    private static string SafeLine(string line, IReadOnlyList<string> names, List<string> warnings)
    {
        if (line.StartsWith('{') && line.EndsWith('}'))
        {
            warnings.Add($"the line \"{line}\" was in braces; they were changed to round brackets so the line is not lost");
            return "(" + line[1..^1] + ")";
        }
        if (Reflow.LabelName(line, names) is not null)
            warnings.Add($"a line that reads \"{line}\" was taken as a section name, not as words to sing");
        return line;
    }

    /// <summary>
    /// OpenLP's verse order ("v1 c1 v2 c1") becomes the song's arrangement: the list of sections
    /// in the order they are sung, which is what lets one chorus be stored once and shown three
    /// times. With no verse order, or one that just lists the sections as stored, the song needs
    /// no arrangement and gets none.
    /// </summary>
    internal static void ApplyVerseOrder(Presentation presentation, IReadOnlyList<NamedVerse> verses, string? verseOrder, List<string> warnings)
    {
        var tokens = (verseOrder ?? "").Split([' ', ',', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0 || presentation.Sections is not { Count: > 0 } sections) return;

        // Each verse's section, found by name. The lyric importer may have put a blank opening
        // section first ("Intro" or "Blank"); ours are looked for from the end so that one is never hit.
        var sectionOf = new Dictionary<NamedVerse, string>();
        foreach (var verse in verses)
        {
            if (sections.LastOrDefault(section => section.Name.Equals(verse.Name, StringComparison.OrdinalIgnoreCase)) is { } section)
                sectionOf[verse] = section.Id;
        }
        var ours = sectionOf.Values.ToHashSet();
        var opening = sections.TakeWhile(section => !ours.Contains(section.Id)).Select(section => section.Id).ToList();

        var order = new List<string>(opening);
        foreach (var token in tokens)
        {
            // "c" means "c1", as in OpenLP. A tag two verses share plays both, as in OpenLP.
            var tag = token.ToLowerInvariant();
            if (tag.Length == 1) tag += "1";
            var matches = verses.Where(verse => verse.Tag == tag && sectionOf.ContainsKey(verse)).ToList();
            if (matches.Count == 0)
            {
                warnings.Add($"its verse order names \"{token}\", which the song does not have; that step was left out");
                continue;
            }
            order.AddRange(matches.Select(verse => sectionOf[verse]));
        }
        if (order.Count == opening.Count) return;
        if (order.SequenceEqual(sections.Select(section => section.Id))) return;

        var arrangement = new Arrangement { Id = LibraryStore.NewId(), Name = ArrangementName, SectionIds = order };
        presentation.Arrangements = [arrangement];
        presentation.DefaultArrangementId = arrangement.Id;
    }
}
