using System.Text;
using System.Text.RegularExpressions;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Lyrics;

public static class Reflow
{
    public sealed record ParsedSection(string Name, IReadOnlyList<string> Slides)
    {
        public bool Equals(ParsedSection? other) =>
            other is not null && Name == other.Name && Slides.SequenceEqual(other.Slides);

        public override int GetHashCode() => HashCode.Combine(Name, Slides.Count);
    }

    public sealed record ParseResult(IReadOnlyList<ParsedSection> Sections, IReadOnlyList<int> Order)
    {
        public ParseResult() : this([], []) { }

        public ParseResult OpeningOnBlank()
        {
            if (Sections.Count == 0) return this;
            if (IsIntro(Sections[0].Name))
            {
                if (Sections[0].Slides.Count == 0)
                {
                    return this with { Sections = [Sections[0] with { Slides = [""] }, .. Sections.Skip(1)] };
                }
                if (Sections[0].Slides.All(IsWordless)) return this;
            }
            var name = Sections.Any(section => IsIntro(section.Name)) ? "Blank" : "Intro";
            return new ParseResult([new ParsedSection(name, [""]), .. Sections], [0, .. Order.Select(index => index + 1)]);
        }

        public bool Equals(ParseResult? other) =>
            other is not null && Sections.SequenceEqual(other.Sections) && Order.SequenceEqual(other.Order);

        public override int GetHashCode() => HashCode.Combine(Sections.Count, Order.Count);
    }

    internal static bool IsWordless(string slide) =>
        ChordMath.Extract(slide).Text.TrimmingWhitespacesAndNewlines().Length == 0;

    internal static bool IsIntro(string name)
    {
        var lowered = name.TrimmingWhitespaces().ToLowerInvariant();
        return lowered == "intro" || lowered.StartsWith("intro ", StringComparison.Ordinal);
    }

    private static readonly string[] LabelNames =
    [
        "verse", "chorus", "pre[- ]?chorus", "bridge", "tag", "intro",
        "outro", "ending", "refrain", "interlude", "vamp", "breakdown",
        "point", "reading",
        "misc", "instrumental", "rap", "descant",
        "half[- ]?chorus", "post[- ]?chorus", "turnaround", "hook", "channel", "coda",
    ];

    private static readonly Regex LabelPattern = new(
        @"^\[?\s*(?:repeat\s+)?(" + string.Join("|", LabelNames) + @")\s*(\d+)?\s*\]?\s*:?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public const int StanzaOnly = 10_000;

    public static string Materialize(
        string text, int linesPerSlide, bool mergeStanzas = false, IReadOnlyList<string>? extraLabels = null, bool phrased = false)
    {
        var perSlide = Math.Max(1, linesPerSlide);
        var casing = new LyricPhrasing.Casing(text);
        var blocks = new List<string>();
        var stanzas = new List<List<string>>();
        var pending = new List<string>();

        void FlushStanza()
        {
            if (pending.Count > 0) stanzas.Add(pending);
            pending = [];
        }
        void FlushSection()
        {
            FlushStanza();
            var groups = mergeStanzas
                ? new List<List<string>> { stanzas.SelectMany(stanza => stanza).ToList() }.Where(group => group.Count > 0).ToList()
                : stanzas;
            blocks.AddRange(Chunk(groups, perSlide, phrased, casing));
            stanzas = [];
        }

        foreach (var rawLine in text.ComponentsSeparatedByNewlines())
        {
            var line = rawLine.TrimmingWhitespaces();
            if (LabelName(line, extraLabels) is not null)
            {
                FlushSection();
                blocks.Add(line);
            }
            else if (line.Length == 0)
            {
                FlushStanza();
            }
            else
            {
                pending.Add(line);
            }
        }
        FlushSection();

        var output = new StringBuilder();
        var previousWasLabel = false;
        foreach (var block in blocks)
        {
            var isLabel = LabelName(block, extraLabels) is not null && !block.Contains('\n');
            if (output.Length > 0) output.Append(previousWasLabel && !isLabel ? "\n" : "\n\n");
            output.Append(block);
            previousWasLabel = isLabel;
        }
        return output.ToString();
    }

    public static string? LabelName(string line, IReadOnlyList<string>? extraLabels = null)
    {
        var trimmed = line.TrimmingWhitespaces();
        var match = LabelPattern.Match(trimmed);
        if (!match.Success)
        {
            if (extraLabels is null || extraLabels.Count == 0) return null;
            var bare = trimmed.Trimming(rune => rune.Value is '[' or ']' or ':').TrimmingWhitespaces();
            if (bare.Length == 0) return null;
            return extraLabels.FirstOrDefault(label => SwiftString.CaseInsensitiveEquals(label, bare));
        }
        var name = match.Groups[1].Value.ToLowerInvariant()
            .Replace("pre chorus", "pre-chorus")
            .Replace("half chorus", "half-chorus")
            .Replace("post chorus", "post-chorus")
            .Capitalized();
        return match.Groups[2].Success ? name + " " + match.Groups[2].Value : name;
    }

    public static ParseResult Parse(string text, int linesPerSlide, IReadOnlyList<string>? extraLabels = null, bool phrased = false)
    {
        var perSlide = Math.Max(1, linesPerSlide);
        var casing = new LyricPhrasing.Casing(text);

        var sections = new List<ParsedSection>();
        var order = new List<int>();
        var lines = text.ComponentsSeparatedByNewlines();
        var hasAnyLabel = lines.Any(line => LabelName(line, extraLabels) is not null);
        var verseCounter = 0;

        string? currentName = null;
        var currentStanzas = new List<List<string>>();
        var pendingStanza = new List<string>();

        void FlushStanza()
        {
            if (pendingStanza.Count > 0) currentStanzas.Add(pendingStanza);
            pendingStanza = [];
        }

        void CloseSection()
        {
            FlushStanza();
            if (currentName is not { } name)
            {
                currentStanzas = [];
                return;
            }
            var slides = Chunk(currentStanzas, perSlide, phrased, casing);
            currentName = null;
            currentStanzas = [];

            var existing = sections.FindIndex(section =>
                SwiftString.CaseInsensitiveEquals(section.Name, name)
                && (slides.Count == 0 || section.Slides.SequenceEqual(slides)));
            if (existing >= 0)
            {
                order.Add(existing);
                return;
            }
            sections.Add(new ParsedSection(name, slides));
            order.Add(sections.Count - 1);
        }

        string NextVerseName()
        {
            verseCounter += 1;
            return $"Verse {verseCounter}";
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimmingWhitespaces();
            if (LabelName(line, extraLabels) is { } label)
            {
                CloseSection();
                currentName = label;
            }
            else if (line.Length == 0)
            {
                if (hasAnyLabel)
                {
                    FlushStanza();
                }
                else
                {
                    CloseSection();
                }
            }
            else
            {
                currentName ??= NextVerseName();
                pendingStanza.Add(line);
            }
        }
        CloseSection();
        return new ParseResult(sections, order);
    }

    private static List<string> Chunk(List<List<string>> stanzas, int perSlide, bool phrased, LyricPhrasing.Casing casing) =>
        stanzas.SelectMany(stanza => phrased
            ? LyricPhrasing.Slides(LyricPhrasing.Rejoined(stanza, casing), perSlide).Select(slide => string.Join("\n", slide))
            : stanza.Chunk(perSlide).Select(slide => string.Join("\n", slide))).ToList();

    public sealed record Built(List<PresentationSection> Sections, List<Slide> Slides, Arrangement? Arrangement);

    public static Built Build(
        string text, int linesPerSlide, string themeSlideName = "Lyrics", IReadOnlyList<string>? extraLabels = null, bool phrased = false) =>
        Build(Parse(text, linesPerSlide, extraLabels, phrased), themeSlideName);

    public static string LyricDesign(IEnumerable<Slide> slides) =>
        slides.Select(slide => slide.ThemeSlideName).FirstOrDefault(name => !string.IsNullOrEmpty(name)) ?? "Lyrics";

    public static Built Build(ParseResult parsed, string themeSlideName = "Lyrics")
    {
        var sections = new List<PresentationSection>();
        var slides = new List<Slide>();
        foreach (var section in parsed.Sections)
        {
            var sectionId = SwiftString.Uuid();
            sections.Add(new PresentationSection { Id = sectionId, Name = section.Name });
            foreach (var content in section.Slides)
            {
                var slide = new Slide { Id = SwiftString.Uuid(), Name = "", Objects = [] };
                if (content.Length > 0)
                {
                    var extracted = ChordMath.Extract(content);
                    var lyrics = new SlideObject
                    {
                        Id = SwiftString.Uuid(), ObjectKind = SlideObjectKind.Text,
                        Name = "Lyrics", Text = extracted.Text,
                    };
                    if (extracted.Chords.Count > 0) lyrics.Chords = extracted.Chords;
                    slide.Objects = [lyrics];
                }
                slide.SectionId = sectionId;
                slide.ThemeSlideName = themeSlideName;
                slides.Add(slide);
            }
        }
        var hasRepeats = parsed.Order.Count != parsed.Order.Distinct().Count();
        var arrangement = hasRepeats
            ? new Arrangement
            {
                Id = SwiftString.Uuid(), Name = "As Pasted",
                SectionIds = parsed.Order.Select(index => sections[index].Id).ToList(),
            }
            : null;
        return new Built(sections, slides, arrangement);
    }
}
