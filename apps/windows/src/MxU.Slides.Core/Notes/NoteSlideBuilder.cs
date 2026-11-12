using MxU.Slides.Core.Library;
using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Notes;

/// <summary>
/// Turns a plan into slides: text boxes named for the design's boxes, picture slides with their
/// photo spots, blank slides between passages, and each slide's switch. Also the design names a
/// kind of slide looks for in a theme, and how many lines a box holds. Port of NoteSlideBuilder.
/// </summary>
public static class NoteSlideBuilder
{
    /// <summary>The words a design's name may hold for each kind ("Scripture", "Bible verse", "Lower third: Verse").</summary>
    public static readonly IReadOnlyDictionary<SlideKind, string[]> DesignSynonyms = new Dictionary<SlideKind, string[]>
    {
        [SlideKind.Scripture] = ["scripture", "verse", "bible", "passage"],
        [SlideKind.Points] = ["points", "point", "bullets", "list"],
        [SlideKind.Quote] = ["quote", "quotation"],
        [SlideKind.Title] = ["title", "section", "heading"],
        [SlideKind.Lyrics] = ["lyrics", "song"],
        [SlideKind.Body] = ["body", "text", "default", "main", "content", "note", "notes", "statement"],
        [SlideKind.Media] = ["media"],
        [SlideKind.Picture] = ["picture"],
        [SlideKind.Video] = ["video"],
        [SlideKind.Blank] = ["blank", "empty", "logo"],
    };

    public static List<MarkedGroup> Groups(NoteBody body) => NoteSlideMarks.Groups(body).Select(Marked).ToList();

    public static MarkedGroup Marked(NoteSlideMarks.Group group) =>
        new(group.Id, group.Type, group.Shape, group.Text, group.Underlines.ToList(), group.Media.ToList(), group.Links.ToList());

    /// <summary>The theme's design for a kind of slide; for points, "Points" for several and "Point" for one.</summary>
    public static string? DesignName(SlideKind kind, Theme? theme, int pointCount = 0)
    {
        // Photo designs are only for pictures.
        var slides = theme?.Slides?.Where(s => kind == SlideKind.Picture || PhotoSpots.Of(s).Count == 0).ToList();
        if (slides is null || slides.Count == 0) return null;
        if (kind == SlideKind.Points)
        {
            string[] many = ["points", "list", "bullets"], one = ["point"];
            foreach (var names in pointCount >= 2 ? new[] { many, one } : [one, many])
            {
                if (slides.FirstOrDefault(s => DeterministicSlidePlanner.Matches(s.Name, names)) is { } match) return match.Name;
            }
        }
        if (slides.FirstOrDefault(s => DeterministicSlidePlanner.Matches(s.Name, DesignSynonyms.GetValueOrDefault(kind) ?? [])) is { } named)
            return named.Name;
        // Scripture and quotes want two boxes (words and reference), so any design with two will do.
        if (kind is SlideKind.Scripture or SlideKind.Quote
            && slides.FirstOrDefault(s => s.Objects.Count(o => o.ObjectKind == SlideObjectKind.Text) >= 2) is { } twoBox)
            return twoBox.Name;
        return null;
    }

    /// <summary>For each look (a screen's own theme), the design a kind of slide takes there.</summary>
    public static Dictionary<string, string> LookDesigns(SlideKind kind, IEnumerable<Theme> looks, IReadOnlyDictionary<string, string> chosen)
    {
        var result = new Dictionary<string, string>();
        foreach (var look in looks)
        {
            if ((chosen.GetValueOrDefault(look.Id) ?? DesignName(kind, look)) is { } design) result[look.Id] = design;
        }
        return result;
    }

    /// <summary>A box's room as an estimate: lines it holds, and characters a line.</summary>
    public sealed record FitInfo(int Lines, int CharsPerLine);

    public static FitInfo Fit(SlideObject? placeholder, Theme? theme)
    {
        var style = placeholder?.TextStyle;
        var fontSize = style?.FontSize ?? theme?.FontSize ?? DefaultFontSize;
        var lineHeight = fontSize * (style?.LineHeightMultiple ?? 1) * 1.2;
        var box = TextBox(placeholder);
        var lines = Math.Min(12, Math.Max(1, (int)(box.Height / Math.Max(1, lineHeight))));
        var chars = Math.Max(8, (int)(box.Width / Math.Max(1, fontSize * 0.5)));
        return new FitInfo(lines, chars);
    }

    internal const string DefaultFontName = "HelveticaNeue-Bold";
    internal const double DefaultFontSize = 96;

    /// <summary>The box's room for words, inside its insets. With no design, the renderer's default: the middle 80% of the canvas.</summary>
    internal static SceneSize TextBox(SlideObject? placeholder)
    {
        var style = placeholder?.TextStyle;
        return new SceneSize(
            Math.Max(1, (placeholder?.Width ?? 1920 * 0.8) - (style?.InsetLeft ?? 0) - (style?.InsetRight ?? 0)),
            Math.Max(1, (placeholder?.Height ?? 1080 * 0.8) - (style?.InsetTop ?? 0) - (style?.InsetBottom ?? 0)));
    }

    /// <summary>
    /// Whether the words fit the box in its own font. Text that shrinks to fit is measured at its
    /// smallest size when every line stays short (two lines or fewer there), as the Mac does.
    /// <paramref name="overflows"/> is the app's measurer; without one, an estimate in
    /// Helvetica's widths (the Mac's default font; Windows draws it in Arial, which has the same).
    /// </summary>
    public static bool Fits(string text, SlideObject? placeholder, Theme? theme, Func<StyledText, SceneSize, bool>? overflows = null)
    {
        if (text.Length == 0) return true;
        var style = placeholder?.TextStyle;
        var box = TextBox(placeholder);
        var fullSize = style?.FontSize ?? theme?.FontSize ?? DefaultFontSize;
        var floor = style?.AutoShrink == true ? Math.Min(fullSize, style.MinFontSize ?? fullSize) : fullSize;
        StyledText At(string words, double size) => new()
        {
            String = words,
            FontName = style?.FontName ?? (string.IsNullOrEmpty(theme?.FontFamily) ? DefaultFontName : theme!.FontFamily),
            FontSize = size,
            LineHeightMultiple = style?.LineHeightMultiple ?? 1,
            ParagraphSpacing = style?.ParagraphSpacing ?? 0,
            LeftIndent = style?.LeftIndent ?? 0,
            FirstLineIndent = style?.FirstLineIndent ?? 0,
            RightIndent = style?.RightIndent ?? 0,
            Tracking = (style?.Tracking ?? 0) * size / Math.Max(1, fullSize),
            Transform = style?.TextTransform == TextTransform.Uppercase ? SceneTextTransform.Uppercase : SceneTextTransform.None,
        };
        var measure = overflows ?? EstimatedOverflow;
        // "Two lines or fewer" is a frame two and a half lines tall, the width of the box.
        bool Short(string line) => !measure(At(line, floor), new SceneSize(box.Width, floor * LineHeight * (style?.LineHeightMultiple ?? 1) * 2.5));
        var shortLines = floor < fullSize && text.Split('\n').All(Short);
        return !measure(At(text, shortLines ? floor : fullSize), box);
    }

    // Helvetica Neue's line height (its ascender and descender), as Core Text lays a line out.
    private const double LineHeight = 1.165;

    /// <summary>Wraps the words at the box's width in Helvetica Bold's letter widths and adds up the lines.</summary>
    internal static bool EstimatedOverflow(StyledText text, SceneSize frame)
    {
        var words = text.Transform == SceneTextTransform.Uppercase ? text.String.ToUpperInvariant() : text.String;
        var size = text.FontSize;
        var width = frame.Width - Math.Max(0, text.RightIndent);
        double Advance(string piece) => piece.Sum(c => HelveticaBold.Width(c) * size / 1000 + text.Tracking);
        var space = Advance(" ");
        var lines = 0;
        var paragraphs = words.Split('\n');
        for (var index = 0; index < paragraphs.Length; index++)
        {
            var indent = Math.Max(0, text.LeftIndent + (index == 0 ? text.FirstLineIndent : 0));
            var used = indent;
            var count = 1;
            foreach (var word in paragraphs[index].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var w = Advance(word);
                if (used > indent && used + space + w > width)
                {
                    count++;
                    used = Math.Max(0, text.LeftIndent) + w;
                }
                else
                {
                    used += (used > indent ? space : 0) + w;
                }
                // A word wider than the box breaks inside itself, as Core Text does.
                while (used > width && width > 0)
                {
                    count++;
                    used -= width;
                }
            }
            lines += count;
        }
        var height = lines * size * LineHeight * text.LineHeightMultiple + Math.Max(0, paragraphs.Length - 1) * text.ParagraphSpacing;
        return height > frame.Height + 0.5;
    }

    /// <summary>The made slides, their sections and arrangement (for lyrics), and which passage each slide came from.</summary>
    public sealed class Built
    {
        public List<Slide> Slides { get; set; } = [];
        public List<PresentationSection> Sections { get; set; } = [];
        public Arrangement? Arrangement { get; set; }
        public Dictionary<string, string> GroupBySlideId { get; set; } = [];

        /// <summary>Blank slides, by the passage they follow.</summary>
        public Dictionary<string, string> BlankFollows { get; set; } = [];

        public Dictionary<string, PictureShape> PictureShapes { get; set; } = [];

        public string? Group(string slideId) => GroupBySlideId.GetValueOrDefault(slideId) ?? BlankFollows.GetValueOrDefault(slideId);
    }

    /// <summary>A row's switch: the type's own (the default), none, or one chosen for this passage.</summary>
    public abstract record SwitchOverride
    {
        public sealed record TypeDefault : SwitchOverride;

        public sealed record None : SwitchOverride;

        public sealed record Chosen(SlidePresetChoice Choice) : SwitchOverride;
    }

    public static SlidePresetChoice? PresetChoice(SlideKind kind, SwitchOverride @override, SlideDesignMap settings) =>
        PresetChoice(kind.Raw(), @override, settings);

    public static SlidePresetChoice? PresetChoice(string type, SwitchOverride @override, SlideDesignMap settings) => @override switch
    {
        SwitchOverride.None => null,
        SwitchOverride.Chosen chosen => chosen.Choice,
        _ => settings.PresetForType(type),
    };

    /// <summary>How the blank slides between passages look: their design and switch.</summary>
    public sealed record BlankStyle(string? ThemeId = null, string? ThemeSlideName = null, IReadOnlyList<SlideAction>? Actions = null);

    public static Slide BlankSlide(Slide? after, BlankStyle style)
    {
        var blank = new Slide { Id = SwiftString.Uuid(), Name = "", Objects = [] };
        blank.SectionId = after?.SectionId;
        blank.ThemeSlideName = style.ThemeSlideName;
        blank.ThemeId = style.ThemeId;
        var actions = style.Actions ?? [];
        blank.Actions = actions.Count == 0 ? null : actions.Select(a =>
        {
            var copy = Json.MxuJson.Clone(a);
            copy.Id = SwiftString.Uuid();
            return copy;
        }).ToList();
        return blank;
    }

    public static Built Build(SlidePlan plan) => Build(plan, new Dictionary<string, BlankPlacement>(), new BlankStyle());

    /// <summary>
    /// Makes the slides. <paramref name="media"/> gives a picture's library id by its note key;
    /// a picture not in the library yet gets a stand-in id that <see cref="NoteMedia.Filling"/> swaps later.
    /// </summary>
    public static Built Build(
        SlidePlan plan, IReadOnlyDictionary<string, BlankPlacement> blanks, BlankStyle style, SlideOptions? options = null,
        Func<string, string?>? media = null)
    {
        options ??= new SlideOptions();
        media ??= _ => null;
        var built = new Built();
        foreach (var row in plan.Rows)
        {
            var start = built.Slides.Count;
            if (row.Lyrics is { } lyrics)
            {
                var reflowed = Reflow.Build(lyrics, row.ThemeSlideName ?? "Lyrics");
                built.Sections.AddRange(reflowed.Sections);
                built.Slides.AddRange(reflowed.Slides);
                built.Arrangement ??= reflowed.Arrangement;
                foreach (var slide in reflowed.Slides) built.GroupBySlideId[slide.Id] = row.GroupId;
            }
            else if (row.Kind == SlideKind.Picture && row.ThemeSlideName is not null && row.Media.Count > 0)
            {
                // Pictures in a photo design: as many a slide as it has spots, in its text boxes the caption.
                var per = Math.Max(1, row.PhotosPerSlide);
                for (var at = 0; at < row.Media.Count; at += per)
                {
                    var keys = row.Media.Skip(at).Take(per).ToList();
                    var photos = keys.Select(key => PhotoSpots.Photo(media(key) ?? NoteMedia.PendingId(key), row.SpotScale(key)));
                    var slide = new Slide
                    {
                        Id = SwiftString.Uuid(), Name = "",
                        Objects = [.. photos, .. (row.Parts.FirstOrDefault() ?? []).Select(TextObject)],
                        ThemeSlideName = row.ThemeSlideName, Notes = row.Notes,
                    };
                    if (keys.Select(k => row.PictureShapes.TryGetValue(k, out var s) ? s : (PictureShape?)null).FirstOrDefault(s => s is not null) is { } shape)
                        built.PictureShapes[slide.Id] = shape;
                    built.Slides.Add(slide);
                    built.GroupBySlideId[slide.Id] = row.GroupId;
                }
            }
            else if (row.Kind is SlideKind.Picture or SlideKind.Video && row.Media.Count > 0)
            {
                foreach (var key in row.Media)
                {
                    var slide = MediaSlide(row, media(key) ?? NoteMedia.PendingId(key));
                    built.Slides.Add(slide);
                    built.GroupBySlideId[slide.Id] = row.GroupId;
                }
            }
            else if (row.Kind is SlideKind.Media or SlideKind.Picture or SlideKind.Video)
            {
                // Media to add by hand: an empty slide with the link or passage in its notes.
                var slide = new Slide { Id = SwiftString.Uuid(), Name = "", Objects = [], Notes = row.Notes };
                built.Slides.Add(slide);
                built.GroupBySlideId[slide.Id] = row.GroupId;
            }
            else
            {
                foreach (var part in row.Parts)
                {
                    var slide = new Slide
                    {
                        Id = SwiftString.Uuid(), Name = "", Objects = part.Select(TextObject).ToList(),
                        ThemeSlideName = row.ThemeSlideName, Notes = row.Notes,
                    };
                    built.Slides.Add(slide);
                    built.GroupBySlideId[slide.Id] = row.GroupId;
                }
            }
            var made = built.Slides.Count - start;
            if (made <= 0 || !blanks.TryGetValue(row.GroupId, out var placement) || placement == BlankPlacement.None) continue;
            // Blanks between a passage's verses only when asked for: otherwise after the whole passage.
            if (placement == BlankPlacement.AfterSlide && row.Kind == SlideKind.Scripture && !options.BlanksBetweenVerses)
                placement = BlankPlacement.AfterPassage;
            Slide Blank(int index)
            {
                var slide = BlankSlide(built.Slides[index], style);
                built.BlankFollows[slide.Id] = row.GroupId;
                return slide;
            }
            if (placement == BlankPlacement.AfterPassage)
            {
                built.Slides.Add(Blank(built.Slides.Count - 1));
            }
            else
            {
                var withBlanks = new List<Slide>();
                for (var index = start; index < built.Slides.Count; index++)
                {
                    withBlanks.Add(built.Slides[index]);
                    withBlanks.Add(Blank(index));
                }
                built.Slides.RemoveRange(start, built.Slides.Count - start);
                built.Slides.AddRange(withBlanks);
            }
        }
        return built;
    }

    internal static Slide MediaSlide(SlideRow row, string mediaId)
    {
        var slide = new Slide { Id = SwiftString.Uuid(), Name = "", Objects = [] };
        slide.ShowMedia(mediaId, row.Kind == SlideKind.Video ? MediaKind.Video : MediaKind.Image, row.Scale, row.VideoEnd ?? SlideOptions.VideoEnds.Next);
        return slide;
    }

    /// <summary>A text box for the slide, with a fade in on a click for each line that reveals.</summary>
    public static SlideObject TextObject(SlideBox box)
    {
        var o = new SlideObject
        {
            Id = SwiftString.Uuid(), ObjectKind = SlideObjectKind.Text, Name = box.Name, Text = box.Text,
            StyleRuns = box.StyleRuns?.Select(Json.MxuJson.Clone).ToList(),
        };
        if (box.RevealLines is { } reveal)
        {
            var lines = box.Text.Split('\n');
            o.AnimationSteps = reveal.Where(l => l >= 0 && l < lines.Length).Select(line => new AnimationStep
            {
                Id = SwiftString.Uuid(), Kind = AnimationKind.In, Animation = StepAnimation.Fade, Trigger = AnimationTrigger.OnClick,
                DurationSeconds = 0.5, Ranges = [new AnimationRange { Line = line, Column = 0, Length = lines[line].Length }],
            }).ToList();
        }
        return o;
    }

    /// <summary>Each made slide fires its type's switch (or the one chosen for its passage).</summary>
    public static void AttachingSwitches(Built built, IReadOnlyList<SlideRow> rows, SlideDesignMap settings, IReadOnlyDictionary<string, SwitchOverride> overrides)
    {
        var rowsByGroup = new Dictionary<string, SlideRow>();
        foreach (var row in rows) rowsByGroup.TryAdd(row.GroupId, row);
        foreach (var slide in built.Slides)
        {
            if (!built.GroupBySlideId.TryGetValue(slide.Id, out var group) || !rowsByGroup.TryGetValue(group, out var row)) continue;
            if (PresetChoice(row.TypeKey, overrides.GetValueOrDefault(group) ?? new SwitchOverride.TypeDefault(), settings) is not { } choice) continue;
            slide.Actions = [.. slide.Actions ?? [], choice.Action()];
        }
    }

    /// <summary>Each slide takes on its design's own actions.</summary>
    public static void AttachingDesignActions(Built built, string deckThemeId, Func<string, Theme?> theme)
    {
        foreach (var slide in built.Slides)
        {
            if (slide.ThemeSlideName is not { Length: > 0 } name) continue;
            var own = slide.ThemeId ?? "";
            var themeId = own.Length == 0 ? deckThemeId : own;
            slide.AdoptActions(theme(themeId)?.Design(name));
        }
    }
}

/// <summary>
/// Helvetica Bold's letter widths, in thousandths of the type size (Adobe's published metrics,
/// which Arial Bold shares), for estimating how words wrap when no real measurer is at hand.
/// </summary>
internal static class HelveticaBold
{
    private static readonly Dictionary<char, int> Widths = Build();

    public static int Width(char c) => Widths.TryGetValue(c, out var w) ? w : c > 0x2000 && c < 0x2070 ? 500 : 600;

    private static Dictionary<char, int> Build()
    {
        var w = new Dictionary<char, int>();
        void Set(string chars, int width)
        {
            foreach (var c in chars) w[c] = width;
        }
        Set(" ", 278); Set("!", 333); Set("\"", 474); Set("#$", 556); Set("%", 889); Set("&", 722); Set("'", 238);
        Set("()", 333); Set("*", 389); Set("+", 584); Set(",", 278); Set("-", 333); Set(".", 278); Set("/", 278);
        Set("0123456789", 556); Set(":;", 333); Set("<=>", 584); Set("?", 611); Set("@", 975);
        Set("A", 722); Set("B", 722); Set("C", 722); Set("D", 722); Set("E", 667); Set("F", 611); Set("G", 778); Set("H", 722);
        Set("I", 278); Set("J", 556); Set("K", 722); Set("L", 611); Set("M", 833); Set("N", 722); Set("O", 778); Set("P", 667);
        Set("Q", 778); Set("R", 722); Set("S", 667); Set("T", 611); Set("U", 722); Set("V", 667); Set("W", 944); Set("X", 667);
        Set("Y", 667); Set("Z", 611); Set("[]", 333); Set("\\", 278); Set("^", 584); Set("_", 556); Set("`", 333);
        Set("a", 556); Set("b", 611); Set("c", 556); Set("d", 611); Set("e", 556); Set("f", 333); Set("g", 611); Set("h", 611);
        Set("i", 278); Set("j", 278); Set("k", 556); Set("l", 278); Set("m", 889); Set("n", 611); Set("o", 611); Set("p", 611);
        Set("q", 611); Set("r", 389); Set("s", 556); Set("t", 333); Set("u", 611); Set("v", 556); Set("w", 778); Set("x", 556);
        Set("y", 556); Set("z", 500); Set("{}", 389); Set("|", 280); Set("~", 584);
        Set("‘’", 278); Set("“”", 500); Set("–", 556); Set("—", 1000); Set("…", 1000); Set("•", 350);
        Set("é", 556); Set("á", 556); Set("ó", 611); Set("í", 278); Set("ú", 611); Set("ñ", 611);
        return w;
    }
}
