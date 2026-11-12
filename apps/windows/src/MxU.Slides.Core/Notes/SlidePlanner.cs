using System.Text.RegularExpressions;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Core.Scripture;

namespace MxU.Slides.Core.Notes;

/// <summary>What a made slide is for. The raw values ("points") are what notes and settings store.</summary>
public enum SlideKind { Scripture, Points, Quote, Title, Lyrics, Body, Media, Blank, Picture, Video }

public static class SlideKinds
{
    /// <summary>Every kind, in the Mac's order (the order design names are matched in).</summary>
    public static readonly IReadOnlyList<SlideKind> All = Enum.GetValues<SlideKind>();

    public static string Raw(this SlideKind kind) => kind.ToString().ToLowerInvariant();

    public static SlideKind? Parse(string? raw) => All.Where(k => k.Raw() == raw).Select(k => (SlideKind?)k).FirstOrDefault();
}

/// <summary>A passage marked in the note: what the planner reads. Kept small so a second planner (Phase 12's AI) can take the same input.</summary>
public sealed record MarkedGroup(
    string Id, string? Type, NoteSlideMarks.Shape Shape, string Text,
    IReadOnlyList<string> Underlines, IReadOnlyList<string> Media, IReadOnlyList<string> Links)
{
    public MarkedGroup(string id, string text, string? type = null, NoteSlideMarks.Shape shape = NoteSlideMarks.Shape.Paragraph)
        : this(id, type, shape, text, [], [], []) { }

    public bool Equals(MarkedGroup? other) =>
        other is not null && Id == other.Id && Type == other.Type && Shape == other.Shape && Text == other.Text
        && Underlines.SequenceEqual(other.Underlines) && Media.SequenceEqual(other.Media) && Links.SequenceEqual(other.Links);

    public override int GetHashCode() => HashCode.Combine(Id, Type, Shape, Text);
}

/// <summary>One text box's words on a made slide, by the design's box name.</summary>
public sealed record SlideBox(string Name, string Text, List<TextStyleRun>? StyleRuns = null, List<int>? RevealLines = null);

/// <summary>The plan for one passage: its kind, design, and the words of each slide it becomes.</summary>
public sealed class SlideRow
{
    public required string GroupId { get; init; }
    public required SlideKind Kind { get; init; }
    public string? ThemeSlideName { get; set; }
    public int LinesPerSlide { get; init; }

    /// <summary>Each slide's boxes, in the design's box order.</summary>
    public List<List<SlideBox>> Parts { get; set; } = [];

    /// <summary>A song's sections, when the passage is lyrics (made by the lyric pipeline).</summary>
    public Reflow.ParseResult? Lyrics { get; init; }

    /// <summary>What goes in the slide's notes: a link, an instruction, asides in brackets.</summary>
    public string? Notes { get; set; }

    /// <summary>A warning Make Slides shows beside the row ("Needs media").</summary>
    public string? Warning { get; init; }

    /// <summary>The church's own slide type the passage is, by name.</summary>
    public string? OwnType { get; init; }

    public List<string> Media { get; set; } = [];
    public MediaScaleMode? Scale { get; set; }
    public SlideOptions.VideoEnds? VideoEnd { get; set; }
    public int PhotosPerSlide { get; set; } = 1;

    /// <summary>Pictures too tall or wide for their spot, which Scale and Blur shows whole.</summary>
    public Dictionary<string, PictureShape> PictureShapes { get; set; } = [];

    /// <summary>Someone chose the Scale by hand, so it wins even inside a photo spot.</summary>
    public bool ScaleChosen { get; set; }

    public MediaScaleMode? SpotScale(string key)
    {
        if (PictureShapes.ContainsKey(key)) return MediaScaleMode.ScaleAndBlur;
        return ScaleChosen ? Scale : Scale?.InPhotoSpot();
    }

    public MediaScaleMode ShownScale =>
        ThemeSlideName is null ? Scale ?? MediaScaleMode.Fill : Media.Select(SpotScale).FirstOrDefault() ?? MediaScaleMode.Fill;

    /// <summary>The type settings are kept under: an own type's name, else the kind.</summary>
    public string TypeKey => OwnType ?? Kind.Raw();

    public bool HoldsMedia => Kind is SlideKind.Picture or SlideKind.Video or SlideKind.Media;
}

public sealed record SlidePlan(IReadOnlyList<SlideRow> Rows);

/// <summary>What someone changed for a row in Make Slides: its kind, design, or lines per slide.</summary>
public sealed record SlideRowOverride(SlideKind? Kind = null, string? ThemeSlideName = null, int? LinesPerSlide = null);

/// <summary>Turns marked passages into a plan. The deterministic planner below is the one Make Slides uses today.</summary>
public interface ISlidePlanner
{
    SlidePlan Plan(IReadOnlyList<MarkedGroup> groups, Theme? theme, IReadOnlyDictionary<string, SlideRowOverride> overrides);
}

/// <summary>
/// Plans each passage by reading it: a "Scripture:" label, a reference, numbered lines, a quote
/// with its attribution, lyrics with section labels, a picture. Then it fits the words into the
/// design's main box, cutting where the box is full. Port of DeterministicSlidePlanner.
/// </summary>
/// <param name="Overflows">The app's text measurer (true when the text doesn't fit a frame of that size); null estimates.</param>
public sealed record DeterministicSlidePlanner(
    SlideOptions? Options = null,
    IReadOnlyDictionary<string, double>? PictureAspects = null,
    bool PictureScaleChosen = false,
    Func<StyledText, SceneSize, bool>? Overflows = null) : ISlidePlanner
{
    private SlideOptions Opts => Options ?? new SlideOptions();

    public SlidePlan Plan(IReadOnlyList<MarkedGroup> groups, Theme? theme, IReadOnlyDictionary<string, SlideRowOverride> overrides) =>
        new(groups.Select(g => Row(g, theme, overrides.GetValueOrDefault(g.Id) ?? new SlideRowOverride())).ToList());

    private SlideRow Row(MarkedGroup group, Theme? theme, SlideRowOverride @override)
    {
        var options = Opts;
        var text = group.Text;
        var kind = @override.Kind ?? SlideKinds.Parse(group.Type);
        var design = @override.ThemeSlideName;
        string? notes = null;

        // The church's own slide types ("Announcement:") come first, as a plain statement.
        SlideOptions.OwnType? own = null;
        if (@override.Kind is null)
        {
            own = options.OwnTypeNamed(group.Type);
            if (own is null && group.Type is null && LeadingLabel(text) is { } label0) own = options.OwnTypeNamed(label0.Name);
            if (own is not null && LeadingLabel(text) is { } label && string.Equals(label.Name, own.Name, StringComparison.OrdinalIgnoreCase))
            {
                var lines = text.ComponentsSeparatedByNewlines().ToList();
                var colon = lines[0].IndexOf(':');
                lines[0] = (colon < 0 ? "" : lines[0][(colon + 1)..]).TrimmingWhitespaces();
                if (lines[0].Length == 0) lines.RemoveAt(0);
                text = string.Join("\n", lines);
            }
            if (own is not null) kind = SlideKind.Body;
        }
        // "Quote: …" or "Scripture: …", or a label naming one of the theme's designs.
        if (kind is null && LeadingLabel(text) is { } named)
        {
            if ((SlideKinds.Parse(named.Name.ToLowerInvariant()) ?? KindOfDesignNamed(named.Name)) is { } namedKind)
            {
                kind = namedKind;
                text = named.Words;
                if (design is null && DesignNamed(named.Name, theme) is { } match) design = match;
            }
            else if (DesignNamed(named.Name, theme) is { } match)
            {
                kind = KindOfDesignNamed(match) ?? SlideKind.Body;
                design ??= match;
                text = named.Words;
            }
        }

        if (kind is null && group.Media.Count > 0) kind = group.Type == SlideKind.Video.Raw() ? SlideKind.Video : SlideKind.Picture;
        // A link to a video file ("sermon-bumper.mp4") is a video slide that waits for its video.
        if (kind is null && group.Media.Count == 0 && group.Links.Count > 0 && IsVideoFileName(text)) kind = SlideKind.Video;
        if (kind is SlideKind.Picture or SlideKind.Video) return MediaRow(group, kind.Value, text, design, theme);

        if (kind is null && LoneLink(text) is { } link)
            return new SlideRow
            {
                GroupId = group.Id, Kind = SlideKind.Media, LinesPerSlide = 1, Notes = link,
                Warning = "Needs media: the link is in the slide's notes",
            };
        if (kind is null && LooksLikeLyrics(text, theme)) kind = SlideKind.Lyrics;
        if (kind is null && ScriptureReference.SplitText(StrippingInstruction(text).Text) is not null) kind = SlideKind.Scripture;
        // Two numbered lines, or a heading line over one: points.
        if (kind is null && ReadsAsPoints(text)) kind = SlideKind.Points;
        // "Big idea: What can't be shaken lasts." is one point with its heading.
        if (kind is null && LabelledPoint(text) is not null) kind = SlideKind.Points;
        var resolvedKind = kind ?? KindOfShape(group.Shape);

        var asides = new List<string>();
        if (options.ParenthesesToNotes && resolvedKind != SlideKind.Lyrics && resolvedKind != SlideKind.Media)
            (text, asides) = StrippingParentheticals(text);

        var textLines = text.Split('\n').Select(l => l.TrimmingWhitespaces()).Where(l => l.Length > 0).ToList();
        var listItems = group.Shape == NoteSlideMarks.Shape.List ? textLines.Count : 0;
        // A heading line over its points counts its points, not itself.
        var underHeading = PointsHeading(textLines) is not null ? textLines.Count - 1 : 0;
        var pointCount = resolvedKind == SlideKind.Points ? Math.Max(Math.Max(1, PointLines(text).Count), Math.Max(listItems, underHeading)) : 0;
        var designName = design ?? (own is null ? null : DesignNamed(own.Name, theme))
            ?? NoteSlideBuilder.DesignName(resolvedKind, theme, pointCount);
        var template = Template(designName, theme);
        // The design's text boxes that take words (a decoration such as a big quote mark doesn't).
        var placeholders = template?.Objects.Where(o => o.ObjectKind == SlideObjectKind.Text && !IsDecoration(o)).ToList() ?? [];

        if (resolvedKind == SlideKind.Lyrics)
        {
            var lyricBox = MainPlaceholder(placeholders, shortToo: false);
            var lyricFit = NoteSlideBuilder.Fit(lyricBox, theme);
            var perSlide = @override.LinesPerSlide ?? lyricFit.Lines;
            return new SlideRow
            {
                GroupId = group.Id, Kind = SlideKind.Lyrics, ThemeSlideName = designName, LinesPerSlide = perSlide,
                Lyrics = Reflow.Parse(text, perSlide),
            };
        }
        if (resolvedKind == SlideKind.Media)
            return new SlideRow
            {
                GroupId = group.Id, Kind = SlideKind.Media, LinesPerSlide = 1, Notes = text,
                Warning = "Needs media: the passage is in the slide's notes",
            };

        // Lines that name a box ("Reference: John 3:16") go to that box; the rest are the main words.
        var namedBoxes = new List<SlideBox>();
        var mainLines = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimmingWhitespaces();
            if (trimmed.Length == 0)
            {
                mainLines.Add("");
                continue;
            }
            if (LeadingLabel(trimmed) is { } label
                && placeholders.FirstOrDefault(p => string.Equals(p.Name, label.Name, StringComparison.OrdinalIgnoreCase)) is { } placeholder)
            {
                var at = namedBoxes.FindIndex(b => b.Name == placeholder.Name);
                if (at >= 0) namedBoxes[at] = namedBoxes[at] with { Text = namedBoxes[at].Text + "\n" + label.Words };
                else namedBoxes.Add(new SlideBox(placeholder.Name, label.Words));
            }
            else
            {
                mainLines.Add(trimmed);
            }
        }
        (string Text, string[] Synonyms)? shortText = null;
        string? title = null;
        // Scripture split one verse a slide: each verse with the reference it shows.
        List<(string Text, string Reference)>? verseParts = null;
        switch (resolvedKind)
        {
            case SlideKind.Scripture:
            {
                var stripped = StrippingInstruction(string.Join("\n", mainLines.Where(l => l.Length > 0)));
                notes = stripped.Instruction;
                if (ScriptureReference.SplitText(stripped.Text) is { } split)
                {
                    mainLines = split.Verse.Split('\n').ToList();
                    shortText = (split.Reference, ReferenceNames);
                    if (ScriptureReference.VersesOf(split.Reference) is { } numbered)
                    {
                        var verses = ScriptureReference.VersesIn(split.Verse, numbered.First, numbered.Last);
                        if (!options.VerseNumbers)
                            mainLines = ScriptureReference.RemovingVerseNumbers(split.Verse, numbered.First, numbered.Last).Split('\n').ToList();
                        if (options.ScriptureSplit == SlideOptions.ScriptureSplits.OneVerse && verses.Count >= 2)
                        {
                            verseParts = verses.Select((verse, index) =>
                            {
                                var number = verse.Number ?? numbered.First;
                                return (options.VerseNumbers ? verse.Numbered : verse.Text,
                                    index == verses.Count - 1 ? split.Reference : $"{numbered.Chapter}:{number}");
                            }).ToList();
                        }
                    }
                }
                break;
            }
            case SlideKind.Quote:
            {
                var content = mainLines.Where(l => l.Length > 0).ToList();
                if (Attribution(content) is { } attribution)
                {
                    mainLines = content.SkipLast(1).ToList();
                    shortText = (attribution, AttributionNames);
                }
                else if (content.Count > 0 && InlineAttribution(content[^1]) is { } inline)
                {
                    mainLines = [.. content.SkipLast(1), inline.Quote];
                    shortText = (inline.Name, AttributionNames);
                }
                // A design that draws its own quote marks gets the words without theirs.
                if (template?.Objects.Any(o => o.ObjectKind == SlideObjectKind.Text && IsDecoration(o) && Unquoted(o.Text) != o.Text) == true)
                {
                    var words = Unquoted(string.Join("\n", mainLines.Where(l => l.Length > 0)));
                    mainLines = words.Split('\n').ToList();
                }
                break;
            }
            case SlideKind.Title:
            {
                // A title's second line is its subtitle.
                var content = mainLines.Where(l => l.Length > 0).ToList();
                if (content.Count >= 2)
                {
                    mainLines = [content[0]];
                    shortText = (string.Join("\n", content.Skip(1)), SubtitleNames);
                }
                break;
            }
            case SlideKind.Points:
            {
                // A heading over the points goes to a title box when the design has one.
                var content = mainLines.Where(l => l.Length > 0).ToList();
                if (content.Count == 1 && LabelledPoint(content[0]) is { } label)
                {
                    title = label.Name;
                    mainLines = [label.Words];
                }
                else if (content.Count > 0 && PointsHeading(content) is { } heading)
                {
                    title = heading;
                    var first = content[0];
                    mainLines = mainLines.SkipWhile(l => l.Length == 0 || l == first).ToList();
                }
                break;
            }
        }

        // Stanzas: blank lines and "Heading:" lines start a new one, and a slide is cut between them first.
        var stanzas = new List<List<string>> { new() };
        foreach (var line in mainLines)
        {
            if (line.Length == 0)
            {
                if (stanzas[^1].Count > 0) stanzas.Add([]);
                continue;
            }
            if (line.EndsWith(':') && stanzas[^1].Count > 0) stanzas.Add([]);
            stanzas[^1].Add(line);
        }
        stanzas.RemoveAll(s => s.Count == 0);
        var main = string.Join("\n", stanzas.Select(s => string.Join("\n", s)));
        var points = main.Split('\n').Where(l => l.Length > 0).ToList();

        var claimed = namedBoxes.Select(b => b.Name).ToHashSet();
        var free = placeholders.Where(p => !claimed.Contains(p.Name)).ToList();
        SlideBox? titleBox = null;
        if (title is not null)
        {
            if ((free.FirstOrDefault(p => Matches(p.Name, TitleNames)) ?? free.FirstOrDefault(p => Matches(p.Name, SubtitleNames))) is { } byName)
            {
                titleBox = new SlideBox(byName.Name, title);
                free.RemoveAll(p => p.Name == byName.Name);
            }
            else
            {
                main = main.Length == 0 ? title : title + "\n" + main;
                stanzas.Insert(0, [title]);
            }
        }
        var mainPlaceholder = MainPlaceholder(free, shortToo: shortText is not null);
        var mainName = mainPlaceholder?.Name ?? placeholders.FirstOrDefault()?.Name ?? "Text";
        SlideBox? shortBox = null;
        // A reference, attribution or subtitle with no box of its own goes at the end of the words, as their tail.
        string? tail = null;
        if (shortText is { } shortValue)
        {
            var candidates = free.Where(p => p.Name != mainName).ToList();
            if (candidates.FirstOrDefault(p => Matches(p.Name, shortValue.Synonyms)) is { } byName)
            {
                shortBox = new SlideBox(byName.Name, shortValue.Text);
            }
            else if (candidates.Count > 0)
            {
                var smallest = candidates.Aggregate((a, b) => Area(b) < Area(a) ? b : a);
                shortBox = new SlideBox(smallest.Name, shortValue.Text);
            }
            else if (main.Length == 0)
            {
                main = shortValue.Text;
                stanzas = [[shortValue.Text]];
            }
            else
            {
                main += "\n" + shortValue.Text;
                tail = shortValue.Text;
            }
        }

        var fit = NoteSlideBuilder.Fit(mainPlaceholder, theme);
        var linesPerSlide = @override.LinesPerSlide ?? fit.Lines;
        bool Fits(string words) => NoteSlideBuilder.Fits(words, mainPlaceholder, theme, Overflows);

        List<string> Parts(List<List<string>> stanzaList, string? tailText)
        {
            List<string> Ending(List<string> parts)
            {
                if (tailText is null || parts.Count == 0) return tailText is null ? parts : [.. parts, tailText];
                return [.. parts.SkipLast(1), parts[^1] + "\n" + tailText];
            }
            var parts = Chunk(stanzaList, linesPerSlide, fit.CharsPerLine);
            if (tailText is not null && parts.Count > 0
                && Cost([.. parts[^1].Split('\n'), tailText], fit.CharsPerLine) > linesPerSlide)
            {
                var room = Math.Max(1, linesPerSlide - Cost([tailText], fit.CharsPerLine));
                parts = Chunk(stanzaList, room, fit.CharsPerLine);
            }
            if (@override.LinesPerSlide is not null) return Ending(parts);
            // The estimate cut the words; the box's real font has the last word. A part it can't
            // hold is cut once more, and neighbours it can hold together are joined again.
            var split = parts.SelectMany((part, index) =>
            {
                var partLines = part.Split('\n').ToList();
                var end = index == parts.Count - 1 ? tailText : null;
                List<string> With(List<string> pieces) => end is null ? pieces : Ending(pieces);
                var perSlide = Cost(partLines, fit.CharsPerLine);
                var pieces = With([part]);
                while (perSlide > 1 && pieces.Any(p => !Fits(p)))
                {
                    perSlide--;
                    pieces = With(Chunk(partLines, perSlide, fit.CharsPerLine));
                }
                return pieces;
            }).ToList();
            var joined = new List<string>();
            foreach (var piece in split)
            {
                if (joined.Count > 0 && Fits(joined[^1] + "\n" + piece)) joined[^1] = joined[^1] + "\n" + piece;
                else joined.Add(piece);
            }
            return joined;
        }

        List<(string Text, string? Short)> chunks;
        if (verseParts is not null)
        {
            chunks = verseParts.SelectMany(verse =>
                Parts([[verse.Text]], shortBox is null ? verse.Reference : null).Select(p => (p, (string?)verse.Reference))).ToList();
        }
        else if (own is not null && own.Split != SlideOptions.OwnType.Splits.FitToBox)
        {
            var all = tail is null ? stanzas : [.. stanzas, [tail]];
            chunks = (own.Split == SlideOptions.OwnType.Splits.Paragraph ? all.Select(s => string.Join("\n", s)) : all.SelectMany(s => s))
                .Select(t => (t, (string?)null)).ToList();
        }
        else
        {
            var texts = resolvedKind == SlideKind.Title || main.Length == 0
                ? (main.Length == 0 ? [] : [main])
                : Parts(stanzas, tail);
            chunks = texts.Select(t => (t, (string?)null)).ToList();
        }

        var textBoxes = template?.Objects.Where(o => o.ObjectKind == SlideObjectKind.Text).ToList() ?? [];
        List<SlideBox> Ordered(List<SlideBox> boxes)
        {
            int Place(SlideBox b) => textBoxes.FindIndex(o => o.Name == b.Name) is var i and >= 0 ? i : textBoxes.Count;
            // Stable, as Swift's sorted(by:) is for boxes with the same place.
            return boxes.Select((b, i) => (b, i)).OrderBy(p => Place(p.b)).ThenBy(p => p.i).Select(p => p.b).ToList();
        }

        // Decorations (a big quote mark) are kept as the design has them.
        var decorations = (template?.Objects ?? []).Where(o => o.ObjectKind == SlideObjectKind.Text && IsDecoration(o))
            .Select(o => new SlideBox(o.Name, o.Text)).ToList();
        List<SlideBox> Extras(string? shortWords = null)
        {
            var boxes = new List<SlideBox>(namedBoxes);
            if (titleBox is not null) boxes.Add(titleBox);
            if (shortBox is not null) boxes.Add(new SlideBox(shortBox.Name, shortWords ?? shortBox.Text));
            boxes.AddRange(decorations);
            return boxes;
        }
        List<List<SlideBox>> slideParts;
        if (resolvedKind == SlideKind.Points && pointCount >= 1
            && free.FirstOrDefault(p => Matches(p.Name, NumberNames) && p.Name != mainName) is { } numberBox)
        {
            // A design with a number box: one point a slide, its number in the box.
            var lead = titleBox is null ? title : null;
            var numbered = points.Where(IsPointLine).ToList();
            var source = numbered.Count == 0 ? points : numbered;
            slideParts = source.Select((line, index) =>
            {
                var split = SplitPointNumber(line);
                var words = lead is null ? split.Text : $"{lead}\n{split.Text}";
                var boxes = new List<SlideBox> { new(mainName, words) };
                boxes.AddRange(Extras());
                if ((split.Number ?? (numbered.Count == 0 && points.Count >= 2 ? $"{index + 1}." : null)) is { } number)
                    boxes.Add(new SlideBox(numberBox.Name, number));
                return Ordered(boxes);
            }).ToList();
        }
        else
        {
            var leadingTitle = titleBox is null ? title : null;
            slideParts = chunks.Select((chunk, index) =>
            {
                var mainBox = new SlideBox(mainName, chunk.Text);
                if (own?.Reveal ?? (options.RevealPoints && resolvedKind == SlideKind.Points))
                {
                    // Each line comes in on a click, except a heading at the top of the first slide.
                    var reveal = Enumerable.Range(0, chunk.Text.Split('\n').Length)
                        .Where(line => !(index == 0 && line == 0 && leadingTitle is not null)).ToList();
                    if (reveal.Count >= 2) mainBox = mainBox with { RevealLines = reveal };
                }
                return Ordered([mainBox, .. Extras(chunk.Short)]);
            }).ToList();
        }
        if (slideParts.Count == 0)
        {
            var extras = Ordered(Extras());
            if (extras.Count > 0) slideParts = [extras];
        }
        if (options.KeepUnderlines && group.Underlines.Count > 0) Underlining(group.Underlines, slideParts, mainName);
        var allNotes = string.Join("\n", (notes is null ? asides : [notes, .. asides]));
        return new SlideRow
        {
            GroupId = group.Id, Kind = resolvedKind, ThemeSlideName = designName, LinesPerSlide = linesPerSlide,
            Parts = slideParts, Notes = allNotes.Length == 0 ? null : allNotes, OwnType = own?.Name,
        };
    }

    private SlideRow MediaRow(MarkedGroup group, SlideKind kind, string text, string? design, Theme? theme)
    {
        var options = Opts;
        var picture = kind == SlideKind.Picture;
        var words = text.TrimmingWhitespacesAndNewlines();
        var row = new SlideRow
        {
            GroupId = group.Id, Kind = kind, LinesPerSlide = 1,
            // A picture's words become its caption or notes; a link with no media is kept in the notes.
            Notes = group.Media.Count == 0 ? (FirstLink(text) ?? group.Links.FirstOrDefault() ?? text) : (words.Length == 0 ? null : words),
            Warning = group.Media.Count == 0 ? "Needs media: the link is in the slide's notes" : null,
            Media = group.Media.ToList(),
        };
        if (picture) row.Scale = options.PictureScale;
        else row.VideoEnd = options.VideoEnd;

        var together = options.PicturesTogether && group.Media.Count > 1;
        if (picture && group.Media.Count > 0 && PhotoDesign(design, together, group.Media.Count, theme) is { } pick)
        {
            row.ThemeSlideName = pick.Name;
            var spots = pick.PhotoSpotsOf();
            row.PhotosPerSlide = together ? spots.Count : 1;
            row.ScaleChosen = PictureScaleChosen;
            if (!PictureScaleChosen && row.Scale?.InPhotoSpot() == MediaScaleMode.Fill)
            {
                for (var index = 0; index < group.Media.Count; index++)
                {
                    var key = group.Media[index];
                    var spot = spots[index % Math.Max(1, row.PhotosPerSlide)];
                    var aspect = PictureAspects?.TryGetValue(key, out var a) == true ? a : (double?)null;
                    if (PictureShapes.Of(aspect, (spot.Width ?? 1920) / Math.Max(1, spot.Height ?? 1080)) is { } shape)
                        row.PictureShapes[key] = shape;
                }
            }
            var boxes = pick.Objects.Where(o => o.ObjectKind == SlideObjectKind.Text && !IsDecoration(o)).ToList();
            if (words.Length > 0 && MainPlaceholder(boxes, shortToo: false) is { } mainBox)
            {
                row.Parts = [[new SlideBox(mainBox.Name, words)]];
                row.Notes = null;
            }
        }
        return row;
    }

    // Box names that hold each part of a slide, as designers name them.
    internal static readonly string[] ReferenceNames = ["reference", "ref", "citation", "address", "verse reference", "scripture reference"];
    internal static readonly string[] AttributionNames = ["attribution", "author", "source", "credit", "speaker"];
    internal static readonly string[] TitleNames = ["title", "heading", "header", "headline"];
    internal static readonly string[] SubtitleNames = ["subtitle", "kicker", "subheading", "tagline", "series", "caption"];
    internal static readonly string[] MainNames = ["verse", "scripture", "text", "body", "quote", "lyrics", "points", "point", "main", "content", "passage", "note", "default"];
    internal static readonly string[] NumberNames = ["number", "no", "index", "numeral", "digit"];

    /// <summary>"Name: rest" at the start of the text: a short label of letters and spaces, then a colon.</summary>
    public static (string Name, string Words)? LeadingLabel(string text)
    {
        var all = text.ComponentsSeparatedByNewlines();
        var first = all.Length > 0 ? all[0] : text;
        var colon = first.IndexOf(':');
        if (colon < 0) return null;
        var name = first[..colon].TrimmingWhitespaces();
        if (name.Length == 0 || name.Length > 24 || !name.All(c => char.IsLetter(c) || c == ' ')) return null;
        var restOfFirst = first[(colon + 1)..].TrimmingWhitespaces();
        var rest = string.Join("\n", new[] { restOfFirst }.Concat(all.Skip(1)).Where(l => l.Length > 0));
        return (name, rest);
    }

    internal static SlideKind KindOfShape(NoteSlideMarks.Shape shape) => shape switch
    {
        NoteSlideMarks.Shape.Heading => SlideKind.Title,
        NoteSlideMarks.Shape.List => SlideKind.Points,
        NoteSlideMarks.Shape.Quote => SlideKind.Quote,
        _ => SlideKind.Body,
    };

    /// <summary>The design a picture slide takes: the chosen one with photo spots, or, for pictures together, the one with the most spots it can fill.</summary>
    internal static Slide? PhotoDesign(string? chosen, bool together, int count, Theme? theme)
    {
        var picked = Template(chosen, theme) is { } t && t.PhotoSpotsOf().Count > 0 ? t : null;
        if (!together) return picked;
        if (picked is not null && picked.PhotoSpotsOf().Count >= 2) return picked;
        var designs = (theme?.Slides ?? []).Where(s => s.PhotoSpotsOf().Count >= 2).ToList();
        if (designs.Count == 0) return picked;
        var most = designs.Max(s => s.PhotoSpotsOf().Count);
        var want = Math.Min(count, most);
        var fits = designs.Where(s => s.PhotoSpotsOf().Count == want).ToList();
        return fits.FirstOrDefault(s => s.Folder == picked?.Folder) ?? fits.FirstOrDefault() ?? picked;
    }

    internal static string? FirstLink(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(word =>
            word.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || word.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    internal static string? LoneLink(string text)
    {
        var trimmed = text.TrimmingWhitespacesAndNewlines();
        if (trimmed.Length == 0 || trimmed.Any(char.IsWhiteSpace)) return null;
        return trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? trimmed : null;
    }

    /// <summary>"John 3:16 (read slowly)": the reference, with the bracketed instruction taken off for the notes.</summary>
    internal static (string Text, string? Instruction) StrippingInstruction(string text)
    {
        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimmingWhitespaces();
            if (!line.EndsWith(')')) continue;
            var open = line.LastIndexOf('(');
            if (open < 0) continue;
            var before = line[..open].TrimmingWhitespaces();
            var inside = line[(open + 1)..^1].TrimmingWhitespaces();
            if (before.Length == 0 || inside.Length == 0 || IsTranslation(inside) || !ScriptureReference.IsReferenceLine(before)
                || ScriptureReference.Matches(inside).Count > 0) continue;
            lines[index] = before;
            return (string.Join("\n", lines), inside);
        }
        return (text, null);
    }

    private static readonly Regex DoubleSpaces = new(@"\s{2,}", RegexOptions.CultureInvariant);
    private static readonly Regex SpaceBeforePunctuation = new(@"\s+([.,;:!?])", RegexOptions.CultureInvariant);

    /// <summary>Asides in brackets ("(hold up the envelope)") come out of the words, for the notes. A reference or translation in brackets stays.</summary>
    internal static (string Text, List<string> Asides) StrippingParentheticals(string text)
    {
        var asides = new List<string>();
        var lines = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            var kept = new System.Text.StringBuilder();
            var rest = line;
            var found = asides.Count;
            while (rest.IndexOf('(') is var open and >= 0 && rest.IndexOf(')', open) is var close and >= 0)
            {
                var inside = rest[(open + 1)..close].TrimmingWhitespaces();
                if (!MovesToNotes(inside))
                {
                    kept.Append(rest[..(close + 1)]);
                }
                else
                {
                    kept.Append(rest[..open]);
                    asides.Add(inside);
                }
                rest = rest[(close + 1)..];
            }
            kept.Append(rest);
            if (asides.Count <= found)
            {
                lines.Add(line);
                continue;
            }
            var cleaned = SpaceBeforePunctuation.Replace(DoubleSpaces.Replace(kept.ToString(), " "), "$1").TrimmingWhitespaces();
            if (cleaned.Length == 0 && line.TrimmingWhitespaces().Length > 0) continue;
            lines.Add(cleaned);
        }
        return (string.Join("\n", lines), asides);
    }

    internal static bool MovesToNotes(string inside) =>
        inside.Length > 0 && !IsTranslation(inside) && ScriptureReference.Matches(inside).Count == 0;

    /// <summary>Where the asides are in a passage, so the note can show them in grey (UTF-16 ranges).</summary>
    public static List<(int Start, int Length)> AsideRanges(string text)
    {
        var found = new List<(int, int)>();
        var from = 0;
        while (text.IndexOf('(', from) is var open and >= 0)
        {
            var close = text.IndexOfAny([')', '\n'], open);
            if (close < 0) break;
            if (text[close] == ')' && MovesToNotes(text[(open + 1)..close].TrimmingWhitespaces())) found.Add((open, close + 1 - open));
            from = close + 1;
        }
        return found;
    }

    /// <summary>"NIV", "WEB": two to six capitals.</summary>
    internal static bool IsTranslation(string text) => text.Length is >= 2 and <= 6 && text.All(char.IsUpper);

    /// <summary>"Big idea: What can't be shaken lasts.": a short label that is no lyric section or slide kind, and its point.</summary>
    internal static (string Name, string Words)? LabelledPoint(string text)
    {
        var trimmed = text.TrimmingWhitespacesAndNewlines();
        if (trimmed.Contains('\n') || LeadingLabel(trimmed) is not { } label || label.Words.Length == 0) return null;
        if (Reflow.LabelName(label.Name) is not null || label.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 3
            || SlideKinds.Parse(label.Name.ToLowerInvariant()) is not null || KindOfDesignNamed(label.Name) is not null)
            return null;
        return label;
    }

    private static readonly Regex InlineAttributionPattern = new(@"^(.*[""”’])\s*[-–—~]\s*(\S.*)$", RegexOptions.CultureInvariant);

    /// <summary>"“Faith is…” — Martin Luther King Jr.": the quote and who said it.</summary>
    internal static (string Quote, string Name)? InlineAttribution(string line)
    {
        var match = InlineAttributionPattern.Match(line);
        if (!match.Success) return null;
        return (match.Groups[1].Value.TrimmingWhitespaces(), match.Groups[2].Value.TrimmingWhitespaces());
    }

    /// <summary>Words that open with a quote mark and close with one, or end with who said them.</summary>
    internal static bool ReadsAsQuote(string text)
    {
        var trimmed = text.TrimmingWhitespacesAndNewlines();
        if (trimmed.Length == 0 || !"“\"".Contains(trimmed[0])) return false;
        var lines = trimmed.Split('\n').ToList();
        if (Attribution(lines) is not null || InlineAttribution(lines[^1]) is not null) return true;
        return trimmed.Length > 1 && "”\"".Contains(trimmed[^1]);
    }

    /// <summary>Underlines words the note underlined, each phrase where it next appears in the main box, in order.</summary>
    internal static void Underlining(IReadOnlyList<string> phrases, List<List<SlideBox>> parts, string mainName)
    {
        var part = 0;
        var from = 0;
        foreach (var phrase in phrases.Select(p => p.TrimmingWhitespaces()).Where(p => p.Length > 0))
        {
            for (var candidate = part; candidate < parts.Count; candidate++)
            {
                var box = parts[candidate].FindIndex(b => b.Name == mainName);
                if (box < 0) continue;
                var text = parts[candidate][box].Text;
                var start = candidate == part ? Math.Min(from, text.Length) : 0;
                var found = text.IndexOf(phrase, start, StringComparison.Ordinal);
                if (found < 0) continue;
                var before = text[..found];
                var line = before.Count(c => c == '\n');
                var column = before.Length - (before.LastIndexOf('\n') + 1);
                var runs = parts[candidate][box].StyleRuns ?? [];
                parts[candidate][box] = parts[candidate][box] with
                {
                    StyleRuns = [.. runs, new TextStyleRun { Line = line, Column = column, Length = phrase.Length, Underline = true }],
                };
                part = candidate;
                from = found + phrase.Length;
                break;
            }
        }
    }

    /// <summary>"1. Grace", "2) Faith", "- Hope", "• Love": a line that is one point.</summary>
    internal static bool IsPointLine(string line)
    {
        var trimmed = line.TrimmingWhitespaces();
        if (trimmed.Length >= 2 && "-•*–".Contains(trimmed[0]) && trimmed[1] == ' ') return true;
        var digits = trimmed.TakeWhile(char.IsDigit).Count();
        if (digits == 0 || digits > 2) return false;
        var rest = trimmed[digits..];
        return (rest.StartsWith(". ", StringComparison.Ordinal) || rest.StartsWith(") ", StringComparison.Ordinal)) && rest.Length > 2;
    }

    internal static bool ReadsAsPoints(string text)
    {
        var lines = text.Split('\n').Select(l => l.TrimmingWhitespaces()).Where(l => l.Length > 0).ToList();
        var points = lines.Count(IsPointLine);
        if (points >= 2) return true;
        if (points != 1 || lines.Count != 2) return false;
        return !IsPointLine(lines[0]) && lines[0].Length <= 80;
    }

    /// <summary>A heading line over points: "Three warnings:" or a short line above point lines.</summary>
    internal static string? PointsHeading(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0 || IsPointLine(lines[0]) || lines[0].Length > 80) return null;
        var first = lines[0];
        if (first.EndsWith(':') && lines.Count >= 2) return first[..^1].TrimmingWhitespaces();
        return lines.Skip(1).All(IsPointLine) ? first : null;
    }

    private static readonly Regex VideoFile = new(NoteSlideMarks.VideoFilePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static bool IsVideoFileName(string text)
    {
        var trimmed = text.TrimmingWhitespacesAndNewlines();
        return !trimmed.Contains('\n') && VideoFile.IsMatch(trimmed);
    }

    private static readonly HashSet<char> QuoteMarks = ['"', '“', '”', '„', '«', '»', '\'', '‘', '’'];

    /// <summary>The words without the quote marks around them (for a design that draws its own).</summary>
    internal static string Unquoted(string text)
    {
        var trimmed = text.TrimmingWhitespacesAndNewlines();
        if (trimmed.Length == 1 && QuoteMarks.Contains(trimmed[0])) return "";
        if (trimmed.Length < 2 || !QuoteMarks.Contains(trimmed[0]) || !QuoteMarks.Contains(trimmed[^1])) return text;
        var inner = trimmed[1..^1];
        // A quote within the quote: its marks are the reader's, not the design's.
        if (inner.Any(c => "“”\"".Contains(c))) return text;
        return inner;
    }

    internal static List<string> PointLines(string text) => text.Split('\n').Where(IsPointLine).ToList();

    /// <summary>"2. Faith" as its number "2." and its words "Faith"; a bullet goes, a plain line stays as it is.</summary>
    internal static (string? Number, string Text) SplitPointNumber(string line)
    {
        var trimmed = line.TrimmingWhitespaces();
        if (!IsPointLine(trimmed)) return (null, trimmed);
        if ("-•*–".Contains(trimmed[0])) return (null, trimmed[1..].TrimmingWhitespaces());
        var digits = trimmed.TakeWhile(char.IsDigit).Count();
        var marker = trimmed[..(digits + 1)];
        return (marker, trimmed[marker.Length..].TrimmingWhitespaces());
    }

    /// <summary>Two song section labels ("Verse 1", "Chorus"), or a theme whose only design is for lyrics.</summary>
    internal static bool LooksLikeLyrics(string text, Theme? theme)
    {
        var labels = text.ComponentsSeparatedByNewlines().Count(l => Reflow.LabelName(l) is not null);
        if (labels >= 2) return true;
        return theme?.Slides is { Count: 1 } slides && KindOfDesignNamed(slides[0].Name) == SlideKind.Lyrics;
    }

    /// <summary>Who said it: a last line after a dash ("— C.S. Lewis"), or a short one under a line that closes a quote.</summary>
    internal static string? Attribution(IReadOnlyList<string> lines)
    {
        if (lines.Count < 2) return null;
        var last = lines[^1];
        var previous = lines[^2];
        if (last.Length > 0 && "-–—~".Contains(last[0])) return last[1..].TrimmingWhitespaces();
        if (last.Length <= 60 && previous.Length > 0 && "\"”’'".Contains(previous[^1])) return last;
        return null;
    }

    /// <summary>A box name that is, or holds the word, one of the synonyms ("Verse Reference", "main text").</summary>
    internal static bool Matches(string name, IReadOnlyList<string> synonyms)
    {
        var lowered = name.ToLowerInvariant();
        var words = Regex.Split(lowered, @"[^\p{L}]+").Where(w => w.Length > 0).ToHashSet();
        return synonyms.Any(s => lowered == s || words.Contains(s));
    }

    internal static string? DesignNamed(string name, Theme? theme) =>
        theme?.Slides?.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))?.Name;

    /// <summary>The design by name, else the theme's first (as Theme.design(named:) does on the Mac).</summary>
    internal static Slide? Template(string? name, Theme? theme) => theme?.Design(name);

    internal static SlideKind? KindOfDesignNamed(string name) =>
        SlideKinds.All.Where(k => Matches(name, NoteSlideBuilder.DesignSynonyms.GetValueOrDefault(k) ?? [])).Select(k => (SlideKind?)k).FirstOrDefault();

    internal static double Area(SlideObject o) => (o.Width ?? 1920) * (o.Height ?? 1080);

    /// <summary>A box whose sample text has no letters or digits: a big quote mark, a line, an ornament.</summary>
    internal static bool IsDecoration(SlideObject box)
    {
        var sample = box.Text.TrimmingWhitespacesAndNewlines();
        return sample.Length > 0 && !sample.Any(char.IsLetterOrDigit);
    }

    /// <summary>The box the words go in: one named for them, else the biggest.</summary>
    internal static SlideObject? MainPlaceholder(IReadOnlyList<SlideObject> placeholders, bool shortToo)
    {
        if (placeholders.FirstOrDefault(p => Matches(p.Name, MainNames)) is { } named) return named;
        if (placeholders.Count >= 2) return placeholders.Aggregate((a, b) => Area(b) > Area(a) ? b : a);
        return placeholders.FirstOrDefault();
    }

    /// <summary>
    /// Cuts stanzas into slides of at most <paramref name="linesPerSlide"/> lines (a long line
    /// counts as the lines it wraps to), keeping a stanza whole when it fits. A stanza that's too
    /// long goes line by line, and a line too long for a slide is cut at its sentences.
    /// </summary>
    internal static List<string> Chunk(List<List<string>> stanzas, int linesPerSlide, int charsPerLine)
    {
        var perSlide = Math.Max(1, linesPerSlide);
        int LineCost(string line) => Cost([line], charsPerLine);
        var parts = new List<string>();
        var current = new List<string>();
        var used = 0;
        void Flush()
        {
            if (current.Count > 0) parts.Add(string.Join("\n", current));
            current = [];
            used = 0;
        }
        foreach (var stanza in stanzas)
        {
            var stanzaCost = stanza.Sum(LineCost);
            if (stanzaCost <= perSlide)
            {
                if (used > 0 && used + stanzaCost > perSlide) Flush();
                current.AddRange(stanza);
                used += stanzaCost;
                continue;
            }
            Flush();
            foreach (var line in stanza)
            {
                var units = LineCost(line) > perSlide ? Pieces(line) : [line];
                foreach (var unit in units)
                {
                    var unitCost = LineCost(unit);
                    if (used > 0 && used + unitCost > perSlide) Flush();
                    current.Add(unit);
                    used += unitCost;
                }
            }
            Flush();
        }
        Flush();
        return parts;
    }

    internal static int Cost(IEnumerable<string> lines, int charsPerLine) =>
        lines.Sum(l => Math.Max(1, (int)Math.Ceiling(l.Length / (double)Math.Max(1, charsPerLine))));

    private static readonly Regex PiecePattern = new(@"(?<=[.!?;:])\s+|\s+(?=\d{1,3}\s+[A-Za-z“""‘'])", RegexOptions.CultureInvariant);

    /// <summary>A long line cut after its sentences and before verse numbers.</summary>
    internal static List<string> Pieces(string line)
    {
        var pieces = new List<string>();
        var start = 0;
        foreach (Match match in PiecePattern.Matches(line))
        {
            var piece = line[start..match.Index].TrimmingWhitespaces();
            if (piece.Length > 0) pieces.Add(piece);
            start = match.Index + match.Length;
        }
        var tail = line[start..].TrimmingWhitespaces();
        if (tail.Length > 0) pieces.Add(tail);
        return pieces.Count == 0 ? [line] : pieces;
    }

    internal static List<string> Chunk(List<string> lines, int linesPerSlide, int charsPerLine) =>
        Chunk([lines.Where(l => l.Length > 0).ToList()], linesPerSlide, charsPerLine);
}
