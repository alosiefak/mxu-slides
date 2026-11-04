using MxU.Slides.Core.Lyrics;
using System.Text.RegularExpressions;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scripture;

namespace MxU.Slides.Core.Notes;

/// <summary>
/// Finding a note's passages again after its marks were lost (a note pasted again, or edited in
/// an app that dropped them): each remembered passage is matched to the note's text, by its
/// words, its scripture, its first line, or enough words in common. Port of NoteSlideMatch.swift.
/// </summary>
public static class NoteSlideMatch
{
    /// <summary>How much of two passages' words must be shared (of all the words in either) to call them the same.</summary>
    public const double SimilarityThreshold = 0.6;

    /// <summary>How sure the match is, surest first.</summary>
    public enum Rung { Mark, Text, Scripture, Title, Similar }

    public sealed record Match(string GroupId, Rung Rung);

    public sealed record Result(NoteBody Body, List<Match> Matches, List<NoteMadePassage> Orphans);

    public static Result MatchPassages(NoteBody fresh, IReadOnlyList<NoteMadePassage> memory)
    {
        var body = fresh;
        var matches = new List<Match>();
        if (memory.Count == 0) return new Result(body, matches, []);
        var marked = NoteSlideMarks.Groups(fresh);
        var unmatched = memory.ToList();
        foreach (var group in marked)
        {
            var index = unmatched.FindIndex(p => p.GroupId == group.Id);
            if (index < 0) continue;
            unmatched.RemoveAt(index);
            matches.Add(new Match(group.Id, Rung.Mark));
        }
        var markedIds = marked.Select(g => g.Id).ToHashSet();
        var candidates = NoteSlideMarks.Groups(NoteSlideMarks.MarkingByStructure(fresh)).Where(g => !markedIds.Contains(g.Id)).ToList();
        var lastMemoryIndex = -1;
        foreach (var candidate in candidates)
        {
            if (unmatched.Count == 0) break;
            (int Index, Rung Rung, int Rank, int Distance)? best = null;
            for (var index = 0; index < unmatched.Count; index++)
            {
                if (RungOf(candidate, unmatched[index]) is not { } rung) continue;
                var rank = (int)rung;
                // Of equally sure matches, the one next in the note's old order.
                var distance = Math.Abs(index - (lastMemoryIndex + 1));
                if (best is null || rank < best.Value.Rank || (rank == best.Value.Rank && distance < best.Value.Distance))
                    best = (index, rung, rank, distance);
            }
            if (best is not { } found) continue;
            var passage = unmatched[found.Index];
            unmatched.RemoveAt(found.Index);
            lastMemoryIndex = found.Index;
            body = NoteSlideMarks.Marking(candidate.BlockIndexes, passage.GroupId, passage.Slides.FirstOrDefault()?.SlideId, body);
            matches.Add(new Match(passage.GroupId, found.Rung));
        }
        return new Result(body, matches, unmatched);
    }

    internal static Rung? RungOf(NoteSlideMarks.Group candidate, NoteMadePassage passage)
    {
        string a = Normalized(candidate.Text), b = Normalized(passage.Text);
        if (a.Length == 0 || b.Length == 0) return null;
        if (a == b) return Rung.Text;
        var refsA = References(candidate.Text);
        var refsB = References(passage.Text);
        if (refsA.Count > 0 && refsA.SetEquals(refsB)) return Rung.Scripture;
        if (candidate.Shape is NoteSlideMarks.Shape.Heading or NoteSlideMarks.Shape.List
            && FirstLine(candidate.Text) is { } titleA && FirstLine(passage.Text) is { } titleB && titleA == titleB)
            return Rung.Title;
        if (Similarity(a, b) >= SimilarityThreshold) return Rung.Similar;
        return null;
    }

    private static readonly Regex ListMarker = new(@"^\s*(\d+[.)]|[-*•])\s+", RegexOptions.CultureInvariant);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.CultureInvariant);

    /// <summary>Lower case, no list markers or punctuation, single spaces, one line per line with words.</summary>
    internal static string Normalized(string text) => string.Join('\n', text.ComponentsSeparatedByNewlines().Where(l => l.Length > 0).Select(raw =>
    {
        var line = ListMarker.Replace(raw.ToLowerInvariant(), "", 1);
        line = new string(line.Select(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) ? c : ' ').ToArray());
        return Spaces.Replace(line, " ").TrimmingWhitespaces();
    }).Where(l => l.Length > 0));

    internal static HashSet<string> References(string text) =>
        text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries).SelectMany(line => ScriptureReference.Matches(line).Select(m => Normalized(m.Text))).ToHashSet();

    internal static string? FirstLine(string text) => Normalized(text).Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

    /// <summary>Words of two letters or more in common, over all the words in either.</summary>
    internal static double Similarity(string a, string b)
    {
        static HashSet<string> Words(string s) =>
            Regex.Split(s, @"[^\p{L}\p{N}]+").Where(w => w.Length >= 2).ToHashSet();
        var x = Words(a);
        var y = Words(b);
        var union = x.Union(y).Count();
        return union == 0 ? 0 : x.Intersect(y).Count() / (double)union;
    }

    /// <summary>The note's memory of the slides each passage made, read from its cards (or its stamps).</summary>
    public static List<NoteMadePassage> Memory(NoteBody body, Presentation deck, IReadOnlyDictionary<string, SlideKind>? kinds = null) =>
        NoteSlideMarks.Groups(body).Select(group =>
        {
            if (group.SlideId is null) return null;
            var slides = NoteSlideUpdate.CardsAfter(group, body)
                .Select(card => deck.Slides.FirstOrDefault(s => s.Id == card.SlideId)).OfType<Slide>().ToList();
            if (slides.Count == 0 && deck.Slides.FirstOrDefault(s => s.Id == group.SlideId) is { } own) slides = [own];
            if (slides.Count == 0) return null;
            var first = slides[0];
            return new NoteMadePassage
            {
                GroupId = group.Id, Text = group.Text, Kind = kinds?.GetValueOrDefault(group.Id) is { } k ? k.Raw() : null,
                ThemeSlideName = first.ThemeSlideName, ThemeId = first.ThemeId,
                Slides = slides.Select(s => new NoteMadeSlide { SlideId = s.Id, Boxes = Boxes(s) }).ToList(),
            };
        }).OfType<NoteMadePassage>().ToList();

    public static List<NoteMadeBox> Boxes(Slide slide) =>
        slide.Objects.Where(o => o.ObjectKind == SlideObjectKind.Text).Select(o => new NoteMadeBox { Name = o.Name, Text = o.Text }).ToList();

    public static bool SameBoxes(IReadOnlyList<NoteMadeBox>? a, IReadOnlyList<NoteMadeBox>? b) =>
        a is null || b is null ? a is null && b is null : a.Count == b.Count && a.Zip(b).All(p => p.First.Name == p.Second.Name && p.First.Text == p.Second.Text);

    /// <summary>A made slide nobody has touched since: the same words, design and theme, and nothing added but actions and notes.</summary>
    public static bool IsUntouched(Slide slide, NoteMadeSlide memory, NoteMadePassage passage)
    {
        if (!SameBoxes(Boxes(slide), memory.Boxes) || slide.ThemeSlideName != passage.ThemeSlideName || slide.ThemeId != passage.ThemeId) return false;
        var bare = new Slide
        {
            Id = slide.Id, Name = slide.Name,
            Objects = slide.Objects.Select(o => new SlideObject { Id = o.Id, ObjectKind = SlideObjectKind.Text, Name = o.Name, Text = o.Text }).ToList(),
            ThemeSlideName = slide.ThemeSlideName, ThemeId = slide.ThemeId,
        };
        var stripped = MxuJson.Clone(slide);
        stripped.Actions = null;
        stripped.Notes = null;
        stripped.History = null;
        stripped.KeepWords = null;
        return MxuJson.StructurallyEqual(stripped, bare);
    }
}
