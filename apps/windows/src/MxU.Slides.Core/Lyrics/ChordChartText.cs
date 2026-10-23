using System.Text;

namespace MxU.Slides.Core.Lyrics;

internal static class ChordChartText
{
    /// <summary>Swift <c>ChordChartText.Inlined</c> (renamed: C# can't share it with the method name).</summary>
    internal readonly record struct InlinedChart(string Text, int ChordCount, string? MusicKey);

    internal static InlinedChart Inlined(string text, IEnumerable<string>? lyricLines = null)
    {
        var lyricSet = new HashSet<string>(lyricLines ?? []);
        var lines = text.Split('\n').Select(ExpandingTabs).ToList();
        List<(int Column, string Symbol)>? Chords(string line) =>
            lyricSet.Contains(line.TrimmingWhitespaces()) ? null : ChordTokens(line);
        bool IsLyric(string line)
        {
            var trimmed = line.TrimmingWhitespaces();
            return trimmed.Length > 0
                && Chords(line) is null
                && LabeledChordLine(line) is null
                && KeyLine(line) is null
                && Reflow.LabelName(trimmed) is null
                && !(trimmed.StartsWith('{') && trimmed.EndsWith('}'));
        }
        var result = new List<string>();
        var chordCount = 0;
        string? musicKey = null;
        var index = 0;
        while (index < lines.Count)
        {
            var line = lines[index];
            if (KeyLine(line) is { } key)
            {
                musicKey ??= key;
            }
            else if (!lyricSet.Contains(line.TrimmingWhitespaces()) && LabeledChordLine(line) is { } labeled)
            {
                chordCount += labeled.Chords.Count;
                result.Add(labeled.Label);
                result.Add(ChordOnly(labeled.Chords));
            }
            else if (Chords(line) is { } chords)
            {
                chordCount += chords.Count;
                if (index + 1 < lines.Count && IsLyric(lines[index + 1]))
                {
                    result.Add(Merging(chords, lines[index + 1]));
                    index += 1;
                }
                else
                {
                    result.Add(ChordOnly(chords));
                }
            }
            else
            {
                result.Add(line);
            }
            index += 1;
        }
        return chordCount > 0
            ? new InlinedChart(string.Join("\n", result), chordCount, musicKey)
            : new InlinedChart(text, 0, null);
    }

    internal static List<string> ChordLines(string text)
    {
        var seen = new HashSet<string>();
        var result = new List<string>();
        foreach (var line in text.Split('\n').Select(ExpandingTabs))
        {
            var trimmed = line.TrimmingWhitespaces();
            if ((ChordTokens(line) is not null || LabeledChordLine(line) is not null) && seen.Add(trimmed))
            {
                result.Add(trimmed);
            }
        }
        return result;
    }

    internal static string Titled(string text)
    {
        var lines = text.Split('\n');
        var first = Array.FindIndex(lines, line => line.TrimmingWhitespaces().Length > 0);
        if (first >= 0
            && (first + 1 == lines.Length || lines[first + 1].TrimmingWhitespaces().Length == 0)
            && !lines[first].Contains('[') && !lines[first].Contains('{')
            && Reflow.LabelName(lines[first]) is null)
        {
            lines[first] = "{title: " + lines[first].TrimmingWhitespaces() + "}";
            return string.Join("\n", lines);
        }
        return text;
    }

    internal static List<(int Column, string Symbol)>? ChordTokens(string line)
    {
        var chords = new List<(int Column, string Symbol)>();
        var allChart = true;
        var column = 0;
        var token = new List<string>();
        var tokenStart = 0;
        void Close()
        {
            if (token.Count == 0) return;
            var leading = token.TakeWhile(character => character is "|" or "(" or "[").Count();
            var core = string.Concat(token.Skip(leading)).Trimming(rune => rune.Value is '|' or ')' or ']');
            if (ChordMath.IsChordToken(core))
            {
                chords.Add((tokenStart + leading, core));
            }
            else if (!ChartPunctuation.WholeMatch(string.Concat(token)))
            {
                allChart = false;
            }
            token.Clear();
        }
        foreach (var character in line.Characters())
        {
            if (character == " ")
            {
                Close();
            }
            else
            {
                if (token.Count == 0) tokenStart = column;
                token.Add(character);
            }
            column += 1;
        }
        Close();
        return allChart && chords.Count > 0 ? chords : null;
    }

    private static (string Label, List<(int Column, string Symbol)> Chords)? LabeledChordLine(string line)
    {
        var parts = line.SplitOmittingEmpty(':', maxSplits: 1);
        if (parts.Count == 2 && Reflow.LabelName(parts[0]) is { } label && ChordTokens(parts[1]) is { } chords)
        {
            return (label, chords);
        }
        return null;
    }

    private static string? KeyLine(string line)
    {
        if (KeyLineRegex.FirstMatch(line.TrimmingWhitespaces()) is not { } match) return null;
        var key = match.FirstCharacter().ToUpperInvariant() + match.DropFirst();
        return ChordMath.ParseKey(key) is not null ? key : null;
    }

    internal static string Merging(IReadOnlyList<(int Column, string Symbol)> chords, string lyric)
    {
        var characters = lyric.Characters();
        var end = characters.Count;
        foreach (var chord in chords.OrderByDescending(chord => chord.Column))
        {
            characters.InsertRange(Math.Min(chord.Column, end), ("[" + chord.Symbol + "]").Characters());
        }
        return string.Concat(characters);
    }

    private static string ChordOnly(IEnumerable<(int Column, string Symbol)> chords) =>
        string.Concat(chords.Select(chord => "[" + chord.Symbol + "]"));

    private static string ExpandingTabs(string line)
    {
        if (!line.Contains('\t')) return line;
        var expanded = new StringBuilder();
        foreach (var character in line.Characters())
        {
            if (character == "\t")
            {
                expanded.Append(' ', 4 - expanded.ToString().CharacterCount() % 4);
            }
            else
            {
                expanded.Append(character);
            }
        }
        return expanded.ToString();
    }

    private static readonly SimpleRegex ChartPunctuation = new(
        @"[|/\\.%:\-–~*]+|\(?[xX]\d+\)?|\(?\d+[xX]\)?|N\.?C\.?|\(|\)", caseInsensitive: true);

    private static readonly SimpleRegex KeyLineRegex = new(
        @"^key\s*(?::|-|–|of|=)?\s*([A-G][#b♯♭]?(?:m|min|minor)?)$", caseInsensitive: true);
}
