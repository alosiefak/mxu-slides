using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Scene;

/// <summary>
/// Group hot keys (C = Chorus, A = Verse 1 …): jump the live song to the next
/// block of that section, cycling through repeats. Port of
/// SlideScene/HotKeyJump.swift and the GroupPalette hot-key defaults.
/// </summary>
public static class HotKeyJump
{
    public static readonly IReadOnlyDictionary<string, string> DefaultHotKeys = new Dictionary<string, string>
    {
        ["verse"] = "a", ["verse1"] = "a", ["verse2"] = "s", ["verse3"] = "d",
        ["verse4"] = "f", ["verse5"] = "g", ["verse6"] = "h",
        ["chorus"] = "c", ["chorus1"] = "c", ["chorus2"] = "x", ["chorus3"] = "z",
        ["bridge"] = "b", ["bridge1"] = "b", ["bridge2"] = "n", ["bridge3"] = "m",
        ["prechorus"] = "p", ["tag"] = "t", ["intro"] = "i", ["ending"] = "e", ["outro"] = "o",
    };

    /// <summary>Letters and digits only, lower-cased ("Pre-Chorus 2" → "prechorus2").</summary>
    public static string NormalizedName(string name) =>
        new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    public static string? EffectiveHotKey(GroupDefinition group) =>
        group.HotKey is { } stored
            ? stored.Length == 0 ? null : stored.ToLowerInvariant()
            : DefaultHotKeys.GetValueOrDefault(NormalizedName(group.Name));

    /// <summary>Letter → the normalized section names it jumps to.</summary>
    public static Dictionary<string, List<string>> HotKeyTargets(IEnumerable<GroupDefinition> groups)
    {
        var table = new Dictionary<string, List<string>>();
        var seen = new HashSet<string>();
        foreach (var group in groups)
        {
            var key = NormalizedName(group.Name);
            if (!seen.Add(key) || EffectiveHotKey(group) is not { } hotKey) continue;
            if (!table.TryGetValue(hotKey, out var names)) table[hotKey] = names = [];
            names.Add(key);
        }
        return table;
    }

    public static List<(int Start, string NormalizedName)> BlockSectionNames(Presentation p, string? arrangementId = null, bool? emptyRepeatsOnce = null)
    {
        var sections = p.Sections ?? [];
        string Normalized(string? id) => id is not null && sections.FirstOrDefault(s => s.Id == id) is { } section ? NormalizedName(section.Name) : "";
        var result = new List<(int, string)>();
        if (!string.IsNullOrEmpty(arrangementId) && p.Arrangements?.FirstOrDefault(a => a.Id == arrangementId) is { SectionIds.Count: > 0 } arrangement)
        {
            var index = 0;
            var blocks = SlideSceneBuilder.PlayedBlocks(arrangement.SectionIds, p, emptyRepeatsOnce);
            for (var i = 0; i < arrangement.SectionIds.Count; i++)
            {
                result.Add((index, Normalized(arrangement.SectionIds[i])));
                index += blocks[i].Count;
            }
            return result;
        }
        string? lastId = null;
        for (var i = 0; i < p.Slides.Count; i++)
        {
            var slide = p.Slides[i];
            if (slide.SectionId != lastId && slide.SectionId is { } id && sections.Any(s => s.Id == id)) result.Add((i, Normalized(id)));
            lastId = slide.SectionId;
        }
        return result;
    }

    /// <summary>The slide index to jump to, or null when the song has no such section.</summary>
    public static int? JumpStart(Presentation p, string? arrangementId, IReadOnlyCollection<string> normalizedNames, int? liveIndex, bool? emptyRepeatsOnce = null)
    {
        var blocks = BlockSectionNames(p, arrangementId, emptyRepeatsOnce);
        var slideCount = SlideSceneBuilder.ArrangedSlides(p, arrangementId, emptyRepeatsOnce).Count;
        int Width(int i) => (i + 1 < blocks.Count ? blocks[i + 1].Start : slideCount) - blocks[i].Start;
        var names = normalizedNames.ToHashSet();
        var matched = Enumerable.Range(0, blocks.Count).Where(i => names.Contains(blocks[i].NormalizedName) && Width(i) > 0).ToList();
        if (matched.Count == 0) return null;
        if (liveIndex is { } live)
        {
            var livePill = Enumerable.Range(0, blocks.Count)
                .LastOrDefault(i => Width(i) > 0 && blocks[i].Start <= live && live < blocks[i].Start + Width(i), -1);
            var position = matched.IndexOf(livePill);
            if (livePill >= 0 && position >= 0) return blocks[matched[(position + 1) % matched.Count]].Start;
        }
        return blocks[matched[0]].Start;
    }
}
