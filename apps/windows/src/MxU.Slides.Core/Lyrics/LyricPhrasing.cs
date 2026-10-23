using System.Text.RegularExpressions;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Lyrics;

public static class LyricPhrasing
{
    public const int MaxJoinedLength = 45;

    public sealed record Casing
    {
        internal HashSet<string> Capitalized { get; } = [];

        public Casing(string text)
        {
            foreach (var line in text.ComponentsSeparatedByNewlines())
            {
                foreach (var word in Words(line).Skip(1))
                {
                    if (SwiftString.IsUppercase(word.FirstCharacter())) Capitalized.Add(word.TrimmingPunctuation());
                }
            }
        }

        internal string MidLine(string word, string? next)
        {
            var keeps = word == "I" || word.StartsWith("I’", StringComparison.Ordinal) || word.StartsWith("I'", StringComparison.Ordinal)
                || Capitalized.Contains(word.TrimmingPunctuation())
                || AlwaysCapitalized.Contains(word.TrimmingPunctuation())
                || (next is not null && SwiftString.IsUppercase(next.FirstCharacter())
                    && !AlwaysCapitalized.Contains(next.TrimmingPunctuation()));
            return keeps ? word : word.FirstCharacter().ToLowerInvariant() + word.DropFirst();
        }

        internal static readonly HashSet<string> AlwaysCapitalized =
        [
            "God", "Jesus", "Lord", "Christ", "Spirit", "Father", "King", "Savior", "Saviour",
            "You", "Your", "Yours", "He", "Him", "His",
        ];

        public bool Equals(Casing? other) => other is not null && Capitalized.SetEquals(other.Capitalized);

        public override int GetHashCode() => Capitalized.Count;
    }

    public static bool Applies(PresentationOrigin? origin, string text) => origin?.Source switch
    {
        PresentationOriginSource.PlanningCenterChart or PresentationOriginSource.PlanningCenterLyrics
            or PresentationOriginSource.SongSelect or PresentationOriginSource.ChartFile => true,
        PresentationOriginSource.PastedLyrics or PresentationOriginSource.LyricsFile =>
            LyricTextImporter.DetectFormat(text) != LyricTextFormat.PlainText,
        _ => false,
    };

    public static List<string> Rejoined(IReadOnlyList<string> lines, Casing casing)
    {
        var result = new List<string>();
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var next = index + 1 < lines.Count ? lines[index + 1] : null;
            if (result.Count > 0 && Continues(line, result[^1], next))
            {
                result[^1] = Joining(result[^1], line, casing);
            }
            else
            {
                result.Add(line);
            }
        }
        return result;
    }

    internal static bool Continues(string line, string previous, string? next)
    {
        var words = Words(line);
        var previousWords = Words(previous);
        var bare = string.Join(" ", words);
        var previousBare = string.Join(" ", previousWords);
        var fits = words.Length > 0 && previousWords.Length > 0
            && previousBare.CharacterCount() + 1 + bare.CharacterCount() <= MaxJoinedLength
            && !previousBare.EndsWith('.') && !previousBare.EndsWith('!')
            && !previousBare.EndsWith('?') && !previousBare.EndsWith(';')
            && bare.ToLowerInvariant() != previousBare.ToLowerInvariant()
            && (next is null || string.Join(" ", Words(next)).ToLowerInvariant() != bare.ToLowerInvariant());
        var first = Key(words.FirstOrDefault());
        var cue = RunsOn.Contains(Key(previousWords.LastOrDefault()))
            || Continuations.Contains(first)
            || (words.Length <= 2 && !Interjections.Contains(first)
                && (next is null || Words(next).Length > 3));
        return fits && cue;
    }

    internal static string Joining(string previous, string line, Casing casing)
    {
        var characters = line.Characters();
        var index = 0;
        var inChord = false;
        while (index < characters.Count
               && (inChord || characters[index] == "[" || !SwiftString.IsLetter(characters[index])))
        {
            if (characters[index] == "[") inChord = true;
            else if (characters[index] == "]") inChord = false;
            index += 1;
        }
        var words = Words(line);
        if (words.Length > 0 && index < characters.Count)
        {
            var first = words[0];
            var cased = casing.MidLine(first, words.Length > 1 ? words[1] : null);
            if (cased != first && cased.Characters().FirstOrDefault(SwiftString.IsLetter) is { } letter)
            {
                characters[index] = letter;
            }
        }
        return previous + " " + string.Concat(characters);
    }

    public static List<List<string>> Slides(IReadOnlyList<string> lines, int perSlide)
    {
        var limit = Math.Max(1, perSlide);
        var count = lines.Count;
        if (count <= limit)
        {
            return count == 0 ? [] : [[.. lines]];
        }
        var bare = lines.Select(line => string.Join(" ", Words(line)).ToLowerInvariant()).ToList();
        var ends = Enumerable.Range(0, count).Select(index => EndsPhrase(index, bare)).ToList();

        var best = Enumerable.Repeat(double.PositiveInfinity, count + 1).ToArray();
        var from = new int[count + 1];
        best[0] = 0;
        for (var start = 0; start < count; start++)
        {
            for (var size = 1; size <= Math.Min(limit, count - start); size++)
            {
                var end = start + size;
                var cost = best[start] + 1;
                if (size < limit) cost += end == count || ends[end - 1] ? 0.7 : 0.9;
                if (SlideEndsBadly.Contains(Key(Words(lines[end - 1]).LastOrDefault()))) cost += 5;
                cost += Enumerable.Range(start, end - 1 - start).Count(index => ends[index]) * 3;
                if (cost < best[end])
                {
                    best[end] = cost;
                    from[end] = start;
                }
            }
        }
        var slides = new List<List<string>>();
        var cursor = count;
        while (cursor > 0)
        {
            slides.Insert(0, lines.Skip(from[cursor]).Take(cursor - from[cursor]).ToList());
            cursor = from[cursor];
        }
        return slides;
    }

    internal static bool EndsPhrase(int index, IReadOnlyList<string> bare)
    {
        var line = bare[index];
        var after = index + 1 < bare.Count ? bare[index + 1] : null;
        var besideItself = (index > 0 && bare[index - 1] == line) || after == line;
        return line.Length > 0 && !besideItself && Enumerable.Range(0, bare.Count).Any(other =>
            Math.Abs(other - index) > 1 && bare[other] == line
            && (other + 1 < bare.Count ? bare[other + 1] : null) != after);
    }

    public static (int Location, int Length, string Replacement)? JoinEdit(
        int caret, string text, IReadOnlyList<string>? extraLabels = null)
    {
        var lines = text.Split('\n');
        var starts = new List<int>();
        var offset = 0;
        foreach (var line in lines)
        {
            starts.Add(offset);
            offset += line.Length + 1;
        }
        var here = Math.Max(0, starts.FindLastIndex(start => start <= Math.Max(0, caret)));
        var next = Enumerable.Range(0, lines.Length)
            .Where(index => index > here && lines[index].TrimmingWhitespaces().Length > 0)
            .Select(index => (int?)index)
            .FirstOrDefault();
        bool IsWords(string line) => Words(line).Length > 0 && Reflow.LabelName(line, extraLabels) is null;
        if (next is { } nextIndex && IsWords(lines[here]) && IsWords(lines[nextIndex]))
        {
            var left = Regex.Replace(lines[here], @"\s+$", "");
            var joined = Joining(left, lines[nextIndex].TrimmingWhitespaces(), new Casing(text));
            return (starts[here], starts[nextIndex] + lines[nextIndex].Length - starts[here], joined);
        }
        return null;
    }

    internal static string[] Words(string line) =>
        ChordMath.ExtractLine(line).Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    internal static string Key(string? word) => (word ?? "").ToLowerInvariant().Trimming(rune => !SwiftString.IsLetterOrMark(rune));

    internal static readonly HashSet<string> RunsOn =
    [
        "the", "a", "an", "of", "to", "for", "and", "or", "but", "with", "in", "on", "at", "by",
        "from", "into", "about", "than", "that", "my", "your", "our", "his", "her", "their", "its",
        "be", "been", "is", "when", "who", "as", "if", "like",
    ];

    internal static readonly HashSet<string> Continuations = ["of", "than", "about"];

    internal static readonly HashSet<string> SlideEndsBadly =
    [
        "the", "a", "an", "of", "to", "for", "and", "or", "but", "with", "in", "on", "at", "by",
        "from", "into", "about", "than", "that", "my", "your", "our", "his", "her", "their", "its",
    ];

    internal static readonly HashSet<string> Interjections = ["oh", "o", "hallelujah", "alleluia", "amen", "hosanna", "whoa", "yeah"];
}
