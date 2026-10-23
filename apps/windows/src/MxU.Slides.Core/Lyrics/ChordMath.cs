using System.Text;
using System.Text.RegularExpressions;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Lyrics;

public static class ChordMath
{
    public readonly record struct Symbol(string Root, string Suffix = "", string? Bass = null)
    {
        public string Formatted => Root + Suffix + (Bass is null ? "" : "/" + Bass);
    }

    public static Symbol? ParseSymbol(string text)
    {
        var trimmed = text.TrimmingWhitespaces();
        var match = SymbolRegex.Match(trimmed);
        if (!match.Success) return null;
        string? Group(int index) =>
            match.Groups[index].Success && match.Groups[index].Length > 0 ? match.Groups[index].Value : null;
        if (Group(1) is not { } root) return null;
        return new Symbol(root, Group(2) ?? "", Group(3));
    }

    public static bool IsChordToken(string text) => ParseSymbol(text) is not null;

    public static (int PitchClass, bool IsMinor)? ParseKey(string key)
    {
        var trimmed = key.TrimmingWhitespaces();
        var first = trimmed.FirstCharacter();
        if (first.Length == 0 || !NaturalPitchClasses.TryGetValue(first.ToUpperInvariant(), out var pitch)) return null;
        var rest = trimmed[first.Length..];
        if (rest.StartsWith('#')) { pitch += 1; rest = rest[1..]; }
        else if (rest.StartsWith('b')) { pitch -= 1; rest = rest[1..]; }
        var mode = rest.ToLowerInvariant();
        if (mode is not ("" or "m" or "min" or "minor")) return null;
        return ((pitch + 12) % 12, mode.Length > 0);
    }

    public static List<string> KeyChoices(string musicKey)
    {
        var minor = ParseKey(musicKey)?.IsMinor ?? false;
        string[] names = ["C", "Db", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B"];
        return minor ? names.Select(name => name + "m").ToList() : names.ToList();
    }

    public static bool HasChords(Presentation presentation) =>
        presentation.Slides.Any(slide => slide.Objects.Any(obj => (obj.Chords ?? []).Count > 0));

    public static string? DisplayKey(string playedKey, string musicKey)
    {
        if (ParseKey(playedKey) is not { } played || ParseKey(musicKey) is not { } written) return null;
        var pitch = played.PitchClass;
        if (played.IsMinor != written.IsMinor)
        {
            pitch = (pitch + (played.IsMinor ? 3 : 9)) % 12;
        }
        return KeyChoices(musicKey).FirstOrDefault(choice => ParseKey(choice)?.PitchClass == pitch);
    }

    public static int? TransposeInterval(string? musicKey, string? displayKey)
    {
        if ((musicKey is null ? null : ParseKey(musicKey)) is not { } source
            || (displayKey is null ? null : ParseKey(displayKey)) is not { } target) return null;
        return (target.PitchClass - source.PitchClass + 12) % 12;
    }

    public static string Display(string symbol, string? musicKey, string? displayKey, ChordNotation notation)
    {
        if (ParseSymbol(symbol) is not { } parsed) return symbol;
        var interval = TransposeInterval(musicKey, displayKey) ?? 0;
        var keyName = displayKey ?? musicKey;
        switch (notation)
        {
            case ChordNotation.Numbers or ChordNotation.Numerals or ChordNotation.DoReMi:
                if (musicKey is null || ParseKey(musicKey) is not { } tonic)
                {
                    return Display(symbol, null, displayKey, ChordNotation.Chords);
                }
                return DegreeNotation(parsed, tonic.PitchClass, notation);
            default:
                if (interval == 0 || keyName is null) return parsed.Formatted;
                return Transpose(parsed, interval, keyName).Formatted;
        }
    }

    public static List<ChordPlacement> DisplayPlacements(
        IEnumerable<ChordPlacement> chords, string? musicKey, string? displayKey, ChordNotation notation) =>
        chords.Select(placement => new ChordPlacement
        {
            Line = placement.Line,
            Column = placement.Column,
            Symbol = Display(placement.Symbol, musicKey, displayKey, notation),
        }).ToList();

    internal static Symbol Transpose(Symbol symbol, int interval, string keyName)
    {
        var sharps = PrefersSharps(keyName);
        var result = symbol;
        if (ParseKey(symbol.Root) is { } root)
        {
            result = result with { Root = Spell((root.PitchClass + interval) % 12, sharps) };
        }
        if (symbol.Bass is { } bassText && ParseKey(bassText) is { } bass)
        {
            result = result with { Bass = Spell((bass.PitchClass + interval) % 12, sharps) };
        }
        return result;
    }

    internal static bool PrefersSharps(string keyName)
    {
        var afterFirst = keyName.DropFirst();
        if (afterFirst.StartsWith('#')) return true;
        if (afterFirst.StartsWith('b')) return false;
        if (ParseKey(keyName) is not { } key) return true;
        var majorTonic = key.IsMinor ? (key.PitchClass + 3) % 12 : key.PitchClass;
        return majorTonic is not (5 or 10 or 3 or 8 or 1 or 6);
    }

    internal static string Spell(int pitchClass, bool sharps)
    {
        string[] sharpNames = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
        string[] flatNames = ["C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B"];
        return (sharps ? sharpNames : flatNames)[(pitchClass + 12) % 12];
    }

    internal static string DegreeNotation(Symbol symbol, int tonic, ChordNotation notation)
    {
        if (ParseKey(symbol.Root) is not { } root) return symbol.Formatted;
        var rootDegree = Degree(root.PitchClass, tonic);
        (int Number, string Accidental)? bassDegree =
            symbol.Bass is not null && ParseKey(symbol.Bass) is { } bass ? Degree(bass.PitchClass, tonic) : null;
        switch (notation)
        {
            case ChordNotation.Numbers:
            {
                var result = rootDegree.Accidental + rootDegree.Number + symbol.Suffix;
                if (bassDegree is { } b) result += "/" + b.Accidental + b.Number;
                return result;
            }
            case ChordNotation.Numerals:
            {
                string[] numerals = ["I", "II", "III", "IV", "V", "VI", "VII"];
                var numeral = numerals[rootDegree.Number - 1];
                if (IsMinorQuality(symbol.Suffix)) numeral = numeral.ToLowerInvariant();
                var result = rootDegree.Accidental + numeral + StrippingMinorMark(symbol.Suffix);
                if (bassDegree is { } b) result += "/" + b.Accidental + numerals[b.Number - 1];
                return result;
            }
            case ChordNotation.DoReMi:
            {
                var result = Solfege(rootDegree) + symbol.Suffix;
                if (bassDegree is { } b) result += "/" + Solfege(b);
                return result;
            }
            default:
                return symbol.Formatted;
        }
    }

    internal static (int Number, string Accidental) Degree(int pitchClass, int tonic)
    {
        var interval = (pitchClass - tonic + 12) % 12;
        int[] majorScale = [0, 2, 4, 5, 7, 9, 11];
        var index = Array.IndexOf(majorScale, interval);
        if (index >= 0) return (index + 1, "");
        if (interval == 6) return (4, "#");
        var upper = Array.FindIndex(majorScale, step => step > interval) + 1;
        return (upper, "b");
    }

    internal static string Solfege((int Number, string Accidental) degree)
    {
        string[] natural = ["Do", "Re", "Mi", "Fa", "Sol", "La", "Ti"];
        string[] lowered = ["Do", "Ra", "Me", "Fa", "Se", "Le", "Te"];
        string[] raised = ["Di", "Ri", "Fi", "Fi", "Si", "Li", "Ti"];
        return degree.Accidental switch
        {
            "b" => lowered[degree.Number - 1],
            "#" => raised[degree.Number - 1],
            _ => natural[degree.Number - 1],
        };
    }

    internal static bool IsMinorQuality(string suffix)
    {
        if (suffix.StartsWith("maj", StringComparison.Ordinal)) return false;
        return suffix.StartsWith('m') || suffix.StartsWith("min", StringComparison.Ordinal)
            || suffix.StartsWith("dim", StringComparison.Ordinal) || suffix.StartsWith('°');
    }

    internal static string StrippingMinorMark(string suffix)
    {
        if (suffix.StartsWith("maj", StringComparison.Ordinal)) return suffix;
        if (suffix.StartsWith("min", StringComparison.Ordinal)) return suffix[3..];
        if (suffix.StartsWith('m')) return suffix[1..];
        return suffix;
    }

    public static (string Text, List<(int Column, string Symbol)> Chords) ExtractLine(string line)
    {
        var text = new StringBuilder();
        var chords = new List<(int Column, string Symbol)>();
        var characters = line.Characters();
        var rest = 0;
        while (characters.IndexOf("[", rest) is var open and >= 0)
        {
            text.AppendJoin("", characters[rest..open]);
            var close = characters.IndexOf("]", open + 1);
            if (close < 0)
            {
                text.AppendJoin("", characters[open..]);
                rest = characters.Count;
                break;
            }
            var token = string.Concat(characters[(open + 1)..close]);
            if (IsChordToken(token))
            {
                chords.Add((text.ToString().CharacterCount(), token.TrimmingWhitespaces()));
            }
            else
            {
                text.AppendJoin("", characters[open..(close + 1)]);
            }
            rest = close + 1;
        }
        text.AppendJoin("", characters[rest..]);
        return (text.ToString(), chords);
    }

    public static (string Text, List<ChordPlacement> Chords) Extract(string text)
    {
        var cleanLines = new List<string>();
        var placements = new List<ChordPlacement>();
        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var extracted = ExtractLine(lines[index]);
            cleanLines.Add(extracted.Text);
            foreach (var chord in extracted.Chords)
            {
                placements.Add(new ChordPlacement { Line = index, Column = chord.Column, Symbol = chord.Symbol });
            }
        }
        return (string.Join("\n", cleanLines), placements);
    }

    public static string Bracketed(string text, IEnumerable<ChordPlacement> chords)
    {
        List<string> lines = text.Length == 0 ? [""] : [.. text.Split('\n')];
        var named = chords.Where(chord => chord.Symbol.TrimmingWhitespaces().Length > 0);
        foreach (var placements in named.GroupBy(chord => chord.Line))
        {
            var lineIndex = placements.Key;
            while (lines.Count <= lineIndex) lines.Add("");
            var line = lines[lineIndex];
            var ordered = placements
                .Select((placement, offset) => (placement, offset))
                .OrderByDescending(entry => entry.placement.Column)
                .ThenByDescending(entry => entry.offset);
            foreach (var (placement, _) in ordered)
            {
                var column = Math.Min(Math.Max(0, placement.Column), line.CharacterCount());
                line = line.Insert(line.Utf16Offset(column), "[" + placement.Symbol + "]");
            }
            lines[lineIndex] = line;
        }
        return string.Join("\n", lines);
    }

    internal static readonly Dictionary<string, int> NaturalPitchClasses = new()
    {
        ["C"] = 0, ["D"] = 2, ["E"] = 4, ["F"] = 5, ["G"] = 7, ["A"] = 9, ["B"] = 11,
    };

    internal static readonly Regex SymbolRegex = new(
        "^([A-G][#b]?)((?:maj|min|dim|aug|sus|add|[mM0-9()#b+°ø*Δ-])*)(?:/([A-G][#b]?))?$",
        RegexOptions.CultureInvariant);
}
