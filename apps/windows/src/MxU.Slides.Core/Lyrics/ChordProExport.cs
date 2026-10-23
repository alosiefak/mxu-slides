using System.Globalization;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Lyrics;

public static class ChordProExport
{
    public static string Text(Presentation presentation)
    {
        var lines = new List<string> { "{title: " + presentation.Name + "}" };
        if (presentation.Ccli?.Author is { Length: > 0 } author)
        {
            lines.Add("{artist: " + author + "}");
        }
        if (presentation.MusicKey is { Length: > 0 } key)
        {
            lines.Add("{key: " + key + "}");
        }
        foreach (var copyright in (presentation.Ccli?.Copyright ?? "").Split('\n').Where(line => line.Length > 0))
        {
            lines.Add("{copyright: " + copyright + "}");
        }
        if (presentation.Ccli?.SongNumber is { } number)
        {
            lines.Add("{ccli: " + number.ToString(CultureInfo.InvariantCulture) + "}");
        }

        foreach (var block in Blocks(presentation))
        {
            lines.Add("");
            var environment = block.Name is null ? null : Environment(block.Name);
            if (block.Name is { } name && environment is not null)
            {
                lines.Add("{start_of_" + environment + ": " + name + "}");
            }
            else if (block.Name is { } comment)
            {
                lines.Add("{comment: " + comment + "}");
            }
            lines.Add(string.Join("\n\n", block.Stanzas));
            if (environment is not null)
            {
                lines.Add("{end_of_" + environment + "}");
            }
        }
        return string.Join("\n", lines) + "\n";
    }

    public static bool IsSong(Presentation presentation) =>
        ChordMath.HasChords(presentation)
        || presentation.MusicKey is { Length: > 0 }
        || presentation.Ccli?.SongNumber is not null
        || presentation.ChordProSource is { Length: > 0 }
        || (presentation.Sections ?? []).Any(section => SongSectionWords.Contains(
            section.Name.ToLowerInvariant().Replace("-", " ").SplitOmittingEmpty(' ').FirstOrDefault() ?? ""));

    private static readonly HashSet<string> SongSectionWords =
    [
        "verse", "chorus", "pre", "prechorus", "bridge", "tag", "intro", "outro", "ending",
        "refrain", "interlude", "vamp", "breakdown", "turnaround", "instrumental",
    ];

    public enum FileFormat
    {
        PlainText,
        ChordPro,
    }

    public static string RawValue(this FileFormat format) => format switch
    {
        FileFormat.ChordPro => "cho",
        _ => "txt",
    };

    public static string Label(this FileFormat format) => format switch
    {
        FileFormat.ChordPro => "ChordPro (.cho)",
        _ => "Plain Text (.txt)",
    };

    public static string FileName(Presentation presentation, FileFormat format = FileFormat.PlainText)
    {
        var name = string.Join("-", presentation.Name.Split(['/', ':', '\\'])).TrimmingWhitespaces();
        return (name.Length == 0 ? "Song" : name) + "." + format.RawValue();
    }

    internal sealed record Block(string? Name, List<string> Stanzas);

    internal static List<Block> Blocks(Presentation presentation, bool sungOrder = true)
    {
        var sections = presentation.Sections ?? [];
        var arrangement = sungOrder
            ? presentation.Arrangements?.FirstOrDefault(candidate => candidate.Id == presentation.DefaultArrangementId)
            : null;
        var order = arrangement?.SectionIds ?? sections.Select(section => section.Id).ToList();
        var names = new Dictionary<string, string>();
        foreach (var section in sections) names.TryAdd(section.Id, section.Name);
        var known = sections.Select(section => section.Id).ToHashSet();
        var unsectioned = presentation.Slides.Where(slide => slide.SectionId is null || !known.Contains(slide.SectionId)).ToList();

        var sectioned = order
            .Where(names.ContainsKey)
            .Select(sectionId => new Block(
                names[sectionId],
                presentation.Slides.Where(slide => slide.SectionId == sectionId).Select(Stanza).OfType<string>().ToList()));
        List<Block> loose = unsectioned.Count == 0 ? [] : [new Block(null, unsectioned.Select(Stanza).OfType<string>().ToList())];
        return loose.Concat(sectioned).Where(block => block.Stanzas.Count > 0).ToList();
    }

    internal static string? Stanza(Slide slide)
    {
        var obj = slide.Objects.FirstOrDefault(candidate => candidate.ObjectKind == SlideObjectKind.Text && candidate.Text.Length > 0)
            ?? slide.Objects.FirstOrDefault(candidate => candidate.ObjectKind == SlideObjectKind.Text);
        if (obj is null) return null;
        var text = ChordMath.Bracketed(obj.Text, obj.Chords ?? []);
        return text.TrimmingWhitespacesAndNewlines().Length == 0 ? null : text;
    }

    private static string? Environment(string name)
    {
        var word = name.ToLowerInvariant().SplitOmittingEmpty(' ').FirstOrDefault() ?? "";
        if (word is "verse" or "chorus" or "bridge") return word;
        return Reflow.LabelName(name) is null ? "part" : null;
    }

    public static string ReflowSeed(Presentation presentation)
    {
        var fromSlides = string.Join("\n\n", Blocks(presentation, sungOrder: false).Select(block =>
            string.Join("\n", (block.Name is null ? [] : new[] { block.Name }).Append(string.Join("\n\n", block.Stanzas)))));
        if (presentation.ReflowSource is { Length: > 0 } source && LyricLines(source).SequenceEqual(LyricLines(fromSlides)))
        {
            return source;
        }
        return fromSlides;
    }

    private static List<string> LyricLines(string text) =>
        Reflow.Parse(text, linesPerSlide: 1).Sections
            .SelectMany(section => section.Slides.Prepend(section.Name))
            .Select(Canonical)
            .ToList();

    private static string Canonical(string line)
    {
        var extracted = ChordMath.Extract(line);
        return ChordMath.Bracketed(extracted.Text, extracted.Chords);
    }
}
