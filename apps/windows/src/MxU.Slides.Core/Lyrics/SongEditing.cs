using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Lyrics;

/// <summary>
/// Editing a song as text: labelled lyrics in, slides/sections/arrangements
/// out. Re-editing keeps the song's id, theme and background, and carries
/// each slide's background, design and notes over to the slide in the same
/// place (same section, same position) in the new version.
/// </summary>
public static class SongEditing
{
    public sealed record Details(string Title, string? Author = null, int? CcliNumber = null, string? Copyright = null, string? MusicKey = null);

    /// <summary>The lyrics text a song is edited as (section labels, stanzas, [chords] inline).</summary>
    public static string LyricsText(Presentation presentation) =>
        presentation.Slides.Count == 0 ? presentation.ReflowSource ?? "" : ChordProExport.ReflowSeed(presentation);

    /// <summary>The most lines any slide shows, so re-editing keeps the existing slide breaks.</summary>
    public static int LinesPerSlide(Presentation presentation, int fallback = 2)
    {
        var most = presentation.Slides
            .Select(s => s.Objects.Where(o => o.ObjectKind == SlideObjectKind.Text).Sum(o => o.Text.Length == 0 ? 0 : o.Text.Split('\n').Length))
            .DefaultIfEmpty(0)
            .Max();
        return most > 0 ? Math.Clamp(most, 1, 12) : fallback;
    }

    public static Presentation Build(
        Presentation? existing, string lyrics, int linesPerSlide, Details details, string themeId, string? arrangementOrder = null)
    {
        var text = lyrics.ReplaceLineEndings("\n").Trim();
        var normalized = LyricTextImporter.Normalize(text);
        var phrased = normalized.Format != LyricTextFormat.PlainText;
        var themeSlideName = existing is null ? "Lyrics" : Reflow.LyricDesign(existing.Slides);
        var built = Reflow.Build(
            Reflow.Parse(normalized.Body, Math.Max(1, linesPerSlide), normalized.ExtraLabels, phrased).OpeningOnBlank(),
            themeSlideName);

        var song = existing is null
            ? new Presentation
            {
                Id = LibraryStore.NewId(),
                PresentationKind = PresentationKind.Song,
                Origin = new PresentationOrigin { Source = PresentationOriginSource.MadeHere, CreatedAt = DateTimeOffset.Now.ToString("O") },
            }
            : Json.MxuJson.Clone(existing);
        song.Name = string.IsNullOrWhiteSpace(details.Title) ? normalized.Title ?? "Untitled Song" : details.Title.Trim();
        song.PresentationKind = PresentationKind.Song;
        song.ThemeId = themeId;
        song.Slides = built.Slides.Count > 0 ? built.Slides : [new Slide { Id = LibraryStore.NewId(), Name = "", ThemeSlideName = themeSlideName }];
        song.Sections = built.Sections.Count > 0 ? built.Sections : null;
        song.ReflowSource = text.Length == 0 ? null : text;
        song.ChordProSource = normalized.ChordProSource;
        song.MusicKey = string.IsNullOrWhiteSpace(details.MusicKey) ? normalized.MusicKey ?? existing?.MusicKey : details.MusicKey.Trim();

        var ccli = song.Ccli ?? normalized.Ccli ?? new CCLIInfo();
        ccli.SongTitle = song.Name;
        if (details.Author is not null) ccli.Author = Blank(details.Author);
        if (details.Copyright is not null) ccli.Copyright = Blank(details.Copyright);
        if (details.CcliNumber is not null) ccli.SongNumber = details.CcliNumber;
        song.Ccli = ccli.Author is null && ccli.Copyright is null && ccli.SongNumber is null ? null : ccli;

        if (existing is not null) CarryOver(existing, song);
        ApplyArrangements(song, built.Arrangement, existing, arrangementOrder);
        return song;
    }

    private static string? Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Copies per-slide background, design, notes and transition by (section name, position in section).</summary>
    private static void CarryOver(Presentation from, Presentation to)
    {
        static IEnumerable<(string Key, Slide Slide)> Keyed(Presentation p)
        {
            var counts = new Dictionary<string, int>();
            foreach (var slide in p.Slides)
            {
                var name = p.Sections?.FirstOrDefault(s => s.Id == slide.SectionId)?.Name ?? "";
                var n = counts.GetValueOrDefault(name);
                counts[name] = n + 1;
                yield return ($"{name.ToLowerInvariant()}#{n}", slide);
            }
        }
        var old = Keyed(from).GroupBy(k => k.Key).ToDictionary(g => g.Key, g => g.First().Slide);
        foreach (var (key, slide) in Keyed(to))
        {
            if (!old.TryGetValue(key, out var previous)) continue;
            slide.Background = previous.Background;
            slide.Notes = previous.Notes;
            slide.Transition = previous.Transition;
            if (!string.IsNullOrEmpty(previous.ThemeSlideName)) slide.ThemeSlideName = previous.ThemeSlideName;
        }
        // Section backgrounds and colours follow their section by name.
        foreach (var section in to.Sections ?? [])
        {
            var match = from.Sections?.FirstOrDefault(s => string.Equals(s.Name, section.Name, StringComparison.OrdinalIgnoreCase));
            if (match is null) continue;
            section.Background = match.Background;
            section.ColorHex = match.ColorHex;
        }
    }

    private static void ApplyArrangements(Presentation song, Arrangement? asPasted, Presentation? existing, string? order)
    {
        var sections = song.Sections ?? [];
        var arrangements = new List<Arrangement>();
        if (asPasted is not null) arrangements.Add(asPasted);
        string? defaultId = asPasted?.Id;

        // Keep the existing named arrangements, re-pointed at the new sections by name.
        foreach (var previous in existing?.Arrangements ?? [])
        {
            if (previous.Name == "As Pasted") continue;
            var names = previous.SectionIds
                .Select(id => existing!.Sections?.FirstOrDefault(s => s.Id == id)?.Name)
                .OfType<string>();
            var ids = names.Select(n => sections.FirstOrDefault(s => string.Equals(s.Name, n, StringComparison.OrdinalIgnoreCase))?.Id)
                .OfType<string>()
                .ToList();
            if (ids.Count == 0) continue;
            arrangements.Add(new Arrangement { Id = previous.Id, Name = previous.Name, SectionIds = ids });
            if (existing!.DefaultArrangementId == previous.Id) defaultId = previous.Id;
        }

        if (!string.IsNullOrWhiteSpace(order))
        {
            var ids = ParseOrder(order, sections);
            if (ids.Count > 0)
            {
                var custom = arrangements.FirstOrDefault(a => a.Name == "Custom");
                if (custom is null)
                {
                    custom = new Arrangement { Id = LibraryStore.NewId(), Name = "Custom" };
                    arrangements.Add(custom);
                }
                custom.SectionIds = ids;
                defaultId = custom.Id;
            }
        }
        song.Arrangements = arrangements.Count > 0 ? arrangements : null;
        song.DefaultArrangementId = defaultId;
    }

    private static readonly Dictionary<string, string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["v"] = "Verse", ["c"] = "Chorus", ["ch"] = "Chorus", ["b"] = "Bridge", ["br"] = "Bridge",
        ["pc"] = "Pre-Chorus", ["pre"] = "Pre-Chorus", ["t"] = "Tag", ["i"] = "Intro", ["in"] = "Intro",
        ["o"] = "Outro", ["e"] = "Ending", ["end"] = "Ending", ["int"] = "Interlude", ["vamp"] = "Vamp",
    };

    /// <summary>
    /// Section ids for an order like "Intro, V1, C, V2, C, B, C x2". Names match
    /// case-insensitively; V1/C/B/PC/T/I/O/E abbreviate the usual sections and
    /// "x2" repeats. Unknown names are skipped.
    /// </summary>
    public static List<string> ParseOrder(string order, IReadOnlyList<PresentationSection> sections)
    {
        var ids = new List<string>();
        foreach (var raw in order.Split([',', '\n', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = raw;
            var repeat = 1;
            var times = System.Text.RegularExpressions.Regex.Match(token, @"\s*[x×]\s*(\d+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (times.Success)
            {
                repeat = Math.Clamp(int.Parse(times.Groups[1].Value), 1, 20);
                token = token[..times.Index].Trim();
            }
            if (Resolve(token, sections) is not { } id) continue;
            for (var i = 0; i < repeat; i++) ids.Add(id);
        }
        return ids;
    }

    private static string? Resolve(string token, IReadOnlyList<PresentationSection> sections)
    {
        var exact = sections.FirstOrDefault(s => string.Equals(s.Name, token, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact.Id;
        var match = System.Text.RegularExpressions.Regex.Match(token, @"^([A-Za-z-]+)\s*(\d+)?$");
        if (!match.Success) return null;
        var word = Abbreviations.TryGetValue(match.Groups[1].Value, out var full) ? full : match.Groups[1].Value;
        var number = match.Groups[2].Success ? match.Groups[2].Value : null;
        var name = number is null ? word : $"{word} {number}";
        var found = sections.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        // "C" means the first Chorus (often named "Chorus 1"); "V" alone means Verse 1.
        found ??= number is null
            ? sections.FirstOrDefault(s => s.Name.StartsWith(word + " ", StringComparison.OrdinalIgnoreCase))
            : null;
        return found?.Id;
    }

    /// <summary>The arrangement written back as an editable order ("Verse 1, Chorus, Verse 2").</summary>
    public static string OrderText(Presentation presentation, string? arrangementId)
    {
        var arrangement = presentation.Arrangements?.FirstOrDefault(a => a.Id == arrangementId);
        if (arrangement is null) return "";
        return string.Join(", ", arrangement.SectionIds
            .Select(id => presentation.Sections?.FirstOrDefault(s => s.Id == id)?.Name)
            .OfType<string>());
    }
}
