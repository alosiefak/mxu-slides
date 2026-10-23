using System.Globalization;
using System.Text.RegularExpressions;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Lyrics;

public enum LyricTextFormat
{
    SongSelect,
    ChordPro,
    ChordChart,
    PlainText,
}

public static class LyricTextImporter
{
    public sealed record Normalized
    {
        public required LyricTextFormat Format { get; init; }
        public string? Title { get; init; }
        public CCLIInfo? Ccli { get; init; }
        public string? ChordProSource { get; init; }
        public string? MusicKey { get; init; }
        public required string Body { get; init; }
        public IReadOnlyList<string> ExtraLabels { get; init; } = [];

        public bool Equals(Normalized? other) =>
            other is not null && Format == other.Format && Title == other.Title
            && MxuJson.StructurallyEqual(Ccli, other.Ccli) && ChordProSource == other.ChordProSource
            && MusicKey == other.MusicKey && Body == other.Body && ExtraLabels.SequenceEqual(other.ExtraLabels);

        public override int GetHashCode() => HashCode.Combine(Format, Title, Body);
    }

    public static List<string> ChordChartLines(string text) => ChordChartText.ChordLines(Unified(text));

    internal static string Unified(string text) => text.Replace("\r\n", "\n").Replace("\r", "\n");

    public static LyricTextFormat DetectFormat(string text, IEnumerable<string>? lyricLines = null)
    {
        if (CcliSongNumberRegex.FirstMatch(text) is not null) return LyricTextFormat.SongSelect;
        var directiveHits = 0;
        var chordLineHits = 0;
        foreach (var line in text.ComponentsSeparatedByNewlines())
        {
            var trimmed = line.TrimmingWhitespaces();
            if (ChordProDirectiveRegex.WholeMatch(trimmed)) directiveHits += 1;
            if (InlineChordRegex.FirstMatch(trimmed) is not null) chordLineHits += 1;
        }
        if (directiveHits >= 1) return LyricTextFormat.ChordPro;
        if (ChordChartText.Inlined(Unified(text), lyricLines).ChordCount >= 2) return LyricTextFormat.ChordChart;
        if (chordLineHits >= 2) return LyricTextFormat.ChordPro;
        return LyricTextFormat.PlainText;
    }

    public static Presentation MakePresentation(
        string text,
        string? fallbackTitle = null,
        string? id = null,
        string? folder = null,
        string themeId = "",
        string themeSlideName = "Lyrics",
        int linesPerSlide = 2,
        IEnumerable<string>? lyricLines = null)
    {
        var normalized = Normalize(text, lyricLines);

        var phrased = normalized.Format != LyricTextFormat.PlainText;
        var built = Reflow.Build(
            Reflow.Parse(normalized.Body, linesPerSlide, normalized.ExtraLabels, phrased).OpeningOnBlank(),
            themeSlideName);
        var slides = built.Slides;
        if (slides.Count == 0)
        {
            slides = [new Slide { Id = SwiftString.Uuid(), Name = "", Objects = [] }];
        }
        var presentation = new Presentation
        {
            Id = id ?? SwiftString.Uuid(),
            Name = normalized.Title ?? fallbackTitle ?? "Imported Lyrics",
            PresentationKind = PresentationKind.Deck,
            ThemeId = themeId,
            Folder = folder,
            Slides = slides,
            Sections = built.Sections.Count == 0 ? null : built.Sections,
            Arrangements = built.Arrangement is { } arrangement ? [arrangement] : null,
            DefaultArrangementId = built.Arrangement?.Id,
            ReflowSource = normalized.Body.Length == 0 ? null : normalized.Body,
            Ccli = normalized.Ccli,
            ChordProSource = normalized.ChordProSource,
            MusicKey = normalized.MusicKey,
        };
        if (phrased)
        {
            presentation.ReflowSource = ChordProExport.ReflowSeed(presentation);
        }
        return presentation;
    }

    public static Normalized Normalize(string text, IEnumerable<string>? lyricLines = null)
    {
        var lyricSet = lyricLines as IReadOnlyCollection<string> ?? lyricLines?.ToList();
        var unified = Unified(text);

        var chart = ChordChartText.Inlined(unified, lyricSet);
        switch (DetectFormat(unified, lyricSet))
        {
            case LyricTextFormat.SongSelect:
                return NormalizeSongSelect(chart.Text) with { MusicKey = chart.MusicKey };
            case LyricTextFormat.ChordPro:
                return NormalizeChordPro(unified);
            case LyricTextFormat.ChordChart:
                var normalized = NormalizeChordPro(ChordChartText.Titled(chart.Text));
                return normalized with { Format = LyricTextFormat.ChordChart, MusicKey = normalized.MusicKey ?? chart.MusicKey };
            default:
                return new Normalized { Format = LyricTextFormat.PlainText, Body = unified.TrimmingWhitespacesAndNewlines() };
        }
    }

    private static Normalized NormalizeSongSelect(string text)
    {
        var lines = text.Split('\n').ToList();

        var ccli = new CCLIInfo();
        var footerStart = lines.FindIndex(line => CcliSongNumberRegex.FirstMatch(line) is not null);
        if (footerStart >= 0)
        {
            var footer = lines.Skip(footerStart).Select(line => line.TrimmingWhitespaces()).ToList();
            lines = lines.Take(footerStart).ToList();
            for (var offset = 0; offset < footer.Count; offset++)
            {
                var line = footer[offset];
                if (CcliSongNumberRegex.FirstMatch(line) is { } number)
                {
                    ccli.SongNumber = ParseInt(number);

                    var next = footer.Skip(offset + 1).FirstOrDefault(candidate => candidate.Length > 0);
                    if (next is { } authors && !authors.StartsWith('©') && !IsCCLIBoilerplate(authors))
                    {
                        ccli.Author = authors;
                    }
                }
                else if (line.StartsWith('©'))
                {
                    ccli.Copyright = (ccli.Copyright is null ? "" : ccli.Copyright + "\n") + line;
                    ccli.CopyrightYear ??= ParseInt(CopyrightYearRegex.FirstMatch(line));
                }
            }
        }

        string? title = null;
        var body = new List<string>();
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimmingWhitespaces();
            if (title is null)
            {
                if (line.Length > 0) title = line;
                continue;
            }
            body.Add(line);
        }
        ccli.SongTitle = title;

        return new Normalized
        {
            Format = LyricTextFormat.SongSelect,
            Title = title,
            Ccli = MxuJson.StructurallyEqual(ccli, new CCLIInfo { SongTitle = title }) && title is null ? null : ccli,
            Body = string.Join("\n", body).TrimmingWhitespacesAndNewlines(),
        };
    }

    private static Normalized NormalizeChordPro(string text)
    {
        string? title = null;
        var ccli = new CCLIInfo();
        var hasCCLI = false;
        string? musicKey = null;
        var body = new List<string>();
        var extraLabels = new List<string>();

        void AppendLabel(string label)
        {
            if (body.Count > 0 && body[^1].Length > 0) body.Add("");
            body.Add(label);
            if (Reflow.LabelName(label) is null && !extraLabels.Contains(label)) extraLabels.Add(label);
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimmingWhitespaces();
            if (ChordProDirective(line) is { } directive)
            {
                var value = directive.Value;
                switch (directive.Key)
                {
                    case "title" or "t":
                        title = value;
                        break;
                    case "subtitle" or "st" or "artist" or "author":
                        if (ccli.Author is null && value is not null)
                        {
                            ccli.Author = value;
                            hasCCLI = true;
                        }
                        break;
                    case "copyright":
                        if (value is not null)
                        {
                            ccli.Copyright = value;
                            ccli.CopyrightYear = ParseInt(CopyrightYearRegex.FirstMatch(value));
                            hasCCLI = true;
                        }
                        break;
                    case "ccli":
                        if (value is not null && ParseInt(new string(value.Where(char.IsNumber).ToArray())) is int number and > 0)
                        {
                            ccli.SongNumber = number;
                            hasCCLI = true;
                        }
                        break;
                    case "comment" or "c":
                        if (value is not null && Reflow.LabelName(value) is { } label) AppendLabel(label);
                        break;
                    case "start_of_chorus" or "soc":
                        AppendLabel(value ?? "Chorus");
                        break;
                    case "start_of_verse" or "sov":
                        AppendLabel(value ?? "Verse");
                        break;
                    case "start_of_bridge" or "sob":
                        AppendLabel(value ?? "Bridge");
                        break;
                    case "start_of_part":
                        if (value is not null) AppendLabel(value);
                        break;
                    case "key":
                        if (value is not null && ChordMath.ParseKey(value) is not null) musicKey = value;
                        break;
                }
                continue;
            }
            if (line.Length == 0)
            {
                if (body.Count > 0 && body[^1].Length > 0) body.Add("");
                continue;
            }
            body.Add(line);
        }

        return new Normalized
        {
            Format = LyricTextFormat.ChordPro,
            Title = title,
            Ccli = hasCCLI ? ccli : null,
            ChordProSource = text,
            MusicKey = musicKey,
            Body = string.Join("\n", body).TrimmingWhitespacesAndNewlines(),
            ExtraLabels = extraLabels,
        };
    }

    private static (string Key, string? Value)? ChordProDirective(string line)
    {
        if (!line.StartsWith('{') || !line.EndsWith('}')) return null;
        var parts = line[1..^1].SplitOmittingEmpty(':', maxSplits: 1);
        if (parts.Count == 0) return null;
        var value = parts.Count > 1 ? parts[1].TrimmingWhitespaces() : null;
        return (parts[0].TrimmingWhitespaces().ToLowerInvariant(), string.IsNullOrEmpty(value) ? null : value);
    }

    private static bool IsCCLIBoilerplate(string line) =>
        line.LocalizedCaseInsensitiveContains("songselect")
        || line.LocalizedCaseInsensitiveContains("ccli licen")
        || line.LocalizedCaseInsensitiveContains("all rights reserved")
        || line.LocalizedCaseInsensitiveContains("www.ccli.com");

    /// <summary>Swift <c>Int(String)</c> for the digit runs the regexes capture.</summary>
    private static int? ParseInt(string? text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static readonly SimpleRegex CcliSongNumberRegex = new(@"CCLI Song #\s*(\d+)", caseInsensitive: true);
    private static readonly SimpleRegex CopyrightYearRegex = new(@"(\b(?:19|20)\d{2}\b)");
    private static readonly SimpleRegex ChordProDirectiveRegex = new(@"^\{[a-zA-Z_]+(:.*)?\}$");
    private static readonly SimpleRegex InlineChordRegex = new(@"\[[A-G][#b]?(?:m|maj|min|dim|aug|sus|add)?\d*(?:/[A-G][#b]?)?\]");
}

internal sealed class SimpleRegex(string pattern, bool caseInsensitive = false)
{
    private readonly Regex regex = new(
        pattern, RegexOptions.CultureInvariant | (caseInsensitive ? RegexOptions.IgnoreCase : RegexOptions.None));

    public string? FirstMatch(string text)
    {
        var match = regex.Match(text);
        if (!match.Success) return null;
        return match.Groups.Count > 1 && match.Groups[1].Success ? match.Groups[1].Value : match.Value;
    }

    public bool WholeMatch(string text)
    {
        var match = regex.Match(text);
        return match.Success && match.Index == 0 && match.Length == text.Length;
    }
}
