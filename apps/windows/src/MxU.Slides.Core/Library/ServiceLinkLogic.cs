using System.Globalization;
using System.Text;
using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Library;

/// <summary>
/// How a row that came from a plan (Planning Center, say) finds its song in the library: a
/// "sticky" rule someone made earlier, then the song's CCLI number, then its title. A link is made
/// only when exactly one song fits, so a plan never lands on the wrong lyrics. Also the song-key
/// rule (the plan's key is applied to a linked song once) and the rules list's upkeep.
/// Port of the Mac's ServiceLinkLogic.swift, line for line.
/// </summary>
public static class ServiceLinkLogic
{
    /// <summary>The well-known ids of the rules documents (ServiceLinkRules.wellKnownID / teamID on the Mac).</summary>
    public const string RulesId = "service-link-rules";

    public const string TeamRulesId = "service-link-rules-team";

    /// <summary>What applying a plan's key to a song changes: the key to show (null: as written) and the key it was for.</summary>
    public sealed record SongKeyUpdate(string? DisplayKey, string AppliedKey);

    /// <summary>The plan's key for a linked row, when it hasn't been applied yet (after that, the operator's own pick stays).</summary>
    public static string? PendingSongKey(ServiceItem item)
    {
        if (item.ItemKind != ServiceItemKind.Presentation || item.RefId.Length == 0 || item.MxuSongKeyApplied is not null) return null;
        var key = item.MxuSongKey?.Trim();
        return string.IsNullOrEmpty(key) ? null : key;
    }

    /// <summary>The display key that plays <paramref name="key"/> on a song with chords and a written key; null when it can't.</summary>
    public static SongKeyUpdate? SongKeyUpdateFor(string key, Presentation presentation)
    {
        if (!ChordMath.HasChords(presentation) || presentation.MusicKey is not { } musicKey
            || ChordMath.DisplayKey(key, musicKey) is not { } spelled)
            return null;
        var original = string.Equals(spelled, musicKey, StringComparison.OrdinalIgnoreCase);
        return new SongKeyUpdate(original ? null : spelled, key);
    }

    /// <summary>A library song a row may link to.</summary>
    public sealed record Candidate(string Id, string Name, int? CcliNumber = null, string? CcliSongTitle = null);

    /// <summary>The answer: link the row to this item, or leave it unlinked.</summary>
    public abstract record Resolution
    {
        public sealed record Link(ServiceItemKind ItemKind, string RefId) : Resolution;

        public sealed record None : Resolution;

        public static Resolution NoLink { get; } = new None();
    }

    /// <summary>Lower case, anything that isn't a letter, mark or digit becomes a space, runs of spaces become one: "Way-Maker (Live)" → "way maker live".</summary>
    public static string Normalize(string name)
    {
        var lowered = name.ToLowerInvariant();
        var stripped = new StringBuilder(lowered.Length);
        foreach (var rune in lowered.EnumerateRunes())
        {
            // Swift's CharacterSet.alphanumerics: Unicode letters (L*), marks (M*) and numbers (N*).
            if (IsAlphanumeric(rune)) stripped.Append(rune.ToString());
            else stripped.Append(' ');
        }
        return string.Join(' ', stripped.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool IsAlphanumeric(Rune rune) => Rune.GetUnicodeCategory(rune) switch
    {
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
            or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
            or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber => true,
        _ => false,
    };

    /// <summary>
    /// A rule for this name (in this service type) wins. Otherwise only a row that is a song (it
    /// has a song title or CCLI number) is matched: by CCLI number when exactly one song has it,
    /// else by title (the song's name or its CCLI title) when exactly one fits.
    /// </summary>
    public static Resolution Resolve(ServiceItem item, string? serviceTypeName, ServiceLinkRules rules, IReadOnlyList<Candidate> candidates)
    {
        if (MatchingRule(item.Name, serviceTypeName, rules) is { } rule) return new Resolution.Link(rule.ItemKind, rule.RefId);

        if (item.MxuSongTitle is null && item.MxuCcliNumber is null) return Resolution.NoLink;

        if (item.MxuCcliNumber is { } ccli)
        {
            var hits = candidates.Where(c => c.CcliNumber == ccli).ToList();
            if (hits.Count == 1) return new Resolution.Link(ServiceItemKind.Presentation, hits[0].Id);
            if (hits.Count > 1) return Resolution.NoLink;
        }

        var wanted = Normalize(item.MxuSongTitle ?? item.Name);
        if (wanted.Length == 0) return Resolution.NoLink;
        var titled = candidates.Where(c => Normalize(c.Name) == wanted || (c.CcliSongTitle is { } title && Normalize(title) == wanted)).ToList();
        return titled.Count == 1 ? new Resolution.Link(ServiceItemKind.Presentation, titled[0].Id) : Resolution.NoLink;
    }

    public static ServiceLinkRule? MatchingRule(string name, string? serviceTypeName, ServiceLinkRules rules)
    {
        var normalized = Normalize(name);
        if (normalized.Length == 0) return null;
        return rules.Rules.FirstOrDefault(r => r.NormalizedName == normalized && r.ServiceTypeName == serviceTypeName);
    }

    /// <summary>The team's rules, plus this computer's own for names the team hasn't covered.</summary>
    public static ServiceLinkRules Merged(IReadOnlyList<ServiceLinkRule> team, IReadOnlyList<ServiceLinkRule> own)
    {
        var covered = team.Select(r => (r.NormalizedName, r.ServiceTypeName ?? "")).ToHashSet();
        var kept = own.Where(r => !covered.Contains((r.NormalizedName, r.ServiceTypeName ?? "")));
        return new ServiceLinkRules { Id = TeamRulesId, Rules = [.. team, .. kept] };
    }

    /// <summary>Remembers "this name links to that item" (in this service type), replacing an earlier rule for the same name.</summary>
    public static void StampRule(
        ServiceLinkRules rules, string itemName, string? serviceTypeName, ServiceItemKind itemKind, string refId, Func<string>? newId = null)
    {
        var normalized = Normalize(itemName);
        if (normalized.Length == 0) return;
        var index = rules.Rules.FindIndex(r => r.NormalizedName == normalized && r.ServiceTypeName == serviceTypeName);
        if (index >= 0)
        {
            rules.Rules[index].ItemKind = itemKind;
            rules.Rules[index].RefId = refId;
        }
        else
        {
            rules.Rules.Add(new ServiceLinkRule
            {
                Id = (newId ?? (() => Guid.NewGuid().ToString().ToUpperInvariant()))(),
                NormalizedName = normalized,
                ServiceTypeName = serviceTypeName,
                ItemKind = itemKind,
                RefId = refId,
            });
        }
    }

    public static void RemoveRule(ServiceLinkRules rules, string itemName, string? serviceTypeName)
    {
        var normalized = Normalize(itemName);
        rules.Rules.RemoveAll(r => r.NormalizedName == normalized && r.ServiceTypeName == serviceTypeName);
    }

    /// <summary>The song's arrangement whose name matches (ignoring case and punctuation), or null.</summary>
    public static string? MatchArrangement(string? name, IReadOnlyList<Arrangement>? arrangements)
    {
        if (name is null || arrangements is null) return null;
        var wanted = Normalize(name);
        if (wanted.Length == 0) return null;
        return arrangements.FirstOrDefault(a => Normalize(a.Name) == wanted)?.Id;
    }

    /// <summary>The lowest score a suggestion needs (a prefix match), so shared words alone never suggest a song.</summary>
    public const int MinimumScore = 15;

    /// <summary>Songs to offer when a row didn't link by itself, best first: its rule, its CCLI number, then how well the title fits.</summary>
    public static List<Candidate> Suggestions(
        ServiceItem item, string? serviceTypeName, ServiceLinkRules rules, IReadOnlyList<Candidate> candidates, int limit = 5)
    {
        var ruleRefId = MatchingRule(item.Name, serviceTypeName, rules)?.RefId;
        var wanted = Normalize(item.MxuSongTitle ?? item.Name);
        var wantedWords = wanted.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

        var scored = new List<(Candidate Candidate, int Score)>();
        foreach (var candidate in candidates)
        {
            var score = 0;
            if (candidate.Id == ruleRefId) score += 100;
            if (item.MxuCcliNumber is { } ccli && candidate.CcliNumber == ccli) score += 50;

            var name = Normalize(candidate.Name);
            var ccliTitle = candidate.CcliSongTitle is { } t ? Normalize(t) : null;
            if (wanted.Length > 0)
            {
                if (name == wanted || ccliTitle == wanted)
                {
                    score += 30;
                }
                else if (name.StartsWith(wanted, StringComparison.Ordinal) || wanted.StartsWith(name, StringComparison.Ordinal))
                {
                    score += 15;
                }
                else
                {
                    var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
                    var shared = wantedWords.Intersect(words).Count();
                    if (shared > 0 && wantedWords.Count > 0) score += Math.Min(12, shared * 12 / Math.Max(wantedWords.Count, 1));
                }
            }
            if (score >= MinimumScore) scored.Add((candidate, score));
        }

        // Highest score first; equal scores by name, A to Z (Swift sorts the pair (score, other's name) descending).
        return [.. scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Candidate.Name, StringComparer.Ordinal)
            .Take(limit)
            .Select(s => s.Candidate)];
    }
}
