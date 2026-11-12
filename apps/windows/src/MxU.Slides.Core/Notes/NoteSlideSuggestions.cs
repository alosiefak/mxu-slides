using System.Text.RegularExpressions;
using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scripture;

namespace MxU.Slides.Core.Notes;

/// <summary>
/// The suggester behind Make Slides' Pick for Me, Bold lines only and By structure: it reads the
/// note's layout and words and marks the passages that look like slides, as suggestions someone
/// accepts or turns down. Port of NoteSlideSuggestions.swift.
/// </summary>
public static partial class NoteSlideMarks
{
    public enum SuggestMode { PickForMe, BoldLines, Structure }

    public static string Raw(this SuggestMode mode) => mode switch
    {
        SuggestMode.BoldLines => "boldLines",
        SuggestMode.Structure => "structure",
        _ => "pickForMe",
    };

    public static SuggestMode? ParseMode(string? raw) => raw switch
    {
        "pickForMe" => SuggestMode.PickForMe,
        "boldLines" => SuggestMode.BoldLines,
        "structure" => SuggestMode.Structure,
        _ => null,
    };

    /// <summary>What Make Slides marks on its own when it opens on a note with no marks.</summary>
    public enum AutoMark { PickForMe, BoldLines, Structure, Nothing }

    public static string Title(this AutoMark mark) => mark switch
    {
        AutoMark.PickForMe => "Pick for me",
        AutoMark.BoldLines => "Bold lines only",
        AutoMark.Structure => "By structure",
        _ => "Nothing",
    };

    public static NoteBody Marking(this AutoMark mark, NoteBody body, IReadOnlyList<string>? ownTypes = null) => mark switch
    {
        AutoMark.PickForMe => Suggesting(body, SuggestMode.PickForMe, ownTypes),
        AutoMark.BoldLines => Suggesting(body, SuggestMode.BoldLines, ownTypes),
        AutoMark.Structure => Suggesting(body, SuggestMode.Structure, ownTypes),
        _ => body,
    };

    /// <summary>The note with its suggested passages marked "suggested", each typed as the suggester reads it.</summary>
    public static NoteBody Suggesting(NoteBody body, SuggestMode mode = SuggestMode.PickForMe, IReadOnlyList<string>? ownTypes = null)
    {
        var marked = body.Copy();
        foreach (var passage in Suggestions(body, mode, ownTypes ?? []))
        {
            var group = MintGroupId();
            foreach (var index in passage.Blocks)
                marked.Blocks[index] = Marking(marked.Blocks[index], group, passage.Type, Suggested);
        }
        return marked;
    }

    /// <summary>One suggested passage: its blocks, and its type (null leaves the kind to the planner).</summary>
    public sealed record Suggestion(IReadOnlyList<int> Blocks, string? Type)
    {
        public bool Equals(Suggestion? other) => other is not null && Type == other.Type && Blocks.SequenceEqual(other.Blocks);

        public override int GetHashCode() => HashCode.Combine(Type, Blocks.Count);
    }

    internal static List<Suggestion> Suggestions(NoteBody body, SuggestMode mode, IReadOnlyList<string>? ownTypes = null)
    {
        ownTypes ??= [];
        // A note with markers says where its slides are: the passages under them.
        if (body.Blocks.Any(NoteSlides.StartsSlide))
        {
            var before = Groups(body).Select(g => g.Id).ToHashSet();
            return Groups(MarkingByMarkers(body)).Where(g => !before.Contains(g.Id)).Select(group =>
            {
                var first = body.Blocks[group.BlockIndexes[0]];
                if (group.Media.Count > 0)
                    return new Suggestion(group.BlockIndexes, (NoteMedia.IsVideo(first) ? SlideType.Video : SlideType.Picture).Raw());
                if (IsVideoFileLink(first)) return new Suggestion(group.BlockIndexes, SlideType.Video.Raw());
                if (ReadsAsListUnderHeading(group.BlockIndexes, body) && OwnTypeOf(group.Text, ownTypes) is null)
                    return new Suggestion(group.BlockIndexes, SlideType.Points.Raw());
                return SuggestionFor(group.BlockIndexes, group.Text, group.Shape, ownTypes);
            }).OfType<Suggestion>().ToList();
        }
        var texts = body.Blocks.Select(LineText).ToList();

        bool IsOwn(string text) => OwnTypeOf(text, ownTypes) is not null;
        bool IsSubtitleLine(string text) => IsSubtitle(text) && !IsOwn(text);

        var mediaType = new Dictionary<int, string>();
        for (var index = 0; index < body.Blocks.Count; index++)
        {
            var block = body.Blocks[index];
            if (block.SlideGroup is not null || NoteMedia.Blocks(new NoteBody { Blocks = [block], PlainText = "" }).Count == 0) continue;
            mediaType[index] = (NoteMedia.IsVideo(block) ? SlideType.Video : SlideType.Picture).Raw();
        }
        var candidates = Enumerable.Range(0, body.Blocks.Count).Where(index =>
        {
            if (mediaType.ContainsKey(index)) return true;
            var block = body.Blocks[index];
            var text = texts[index];
            // A run sheet's own lines ("Offering", "Prayer") are no slides, unless they're the church's own type or a video.
            return IsPassageType(block) && text.Length > 0 && !NoteSlides.IsMarker(block, NoteSlides.DefaultMarker)
                && Lines(block).SelectMany(l => l).All(r => r.SlideGroup is null)
                && (!IsRunSheet(text) || IsOwn(text) || IsVideoLink(block)) && !IsAside(text);
        }).ToList();
        var open = candidates.ToHashSet();
        bool IsParagraph(int index) => open.Contains(index) && body.Blocks[index].Type == NoteBlockType.Paragraph;
        bool IsReference(int index) => ScriptureReference.IsReferenceLine(texts[index]);

        var firstContent = Enumerable.Range(0, body.Blocks.Count).Where(i => texts[i].Length > 0).Select(i => (int?)i).FirstOrDefault();
        // A heading, or a short first line that reads as a title.
        bool IsTitle(int index) =>
            body.Blocks[index].Type == NoteBlockType.Heading
            || (index == firstContent && body.Blocks[index].Type == NoteBlockType.Paragraph && IsSubtitleLine(texts[index])
                && !IsReference(index) && !DeterministicSlidePlanner.ReadsAsQuote(texts[index]));
        var passages = new List<List<int>>();
        var titles = new HashSet<int>();
        var used = new HashSet<int>();
        foreach (var index in candidates)
        {
            if (used.Contains(index)) continue;
            var passage = new List<int> { index };
            var next = index + 1;
            if (IsTitle(index)) titles.Add(index);
            if (mediaType.GetValueOrDefault(index) == SlideType.Picture.Raw())
            {
                // Pictures in a row, blank lines between them or not, are one passage.
                for (var after = next; after < body.Blocks.Count; after++)
                {
                    if (mediaType.GetValueOrDefault(after) == SlideType.Picture.Raw()) passage.Add(after);
                    else if (!(body.Blocks[after].Type == NoteBlockType.Paragraph && texts[after].Length == 0)) break;
                }
            }
            else if (IsOwn(texts[index]) || mediaType.ContainsKey(index) || IsVideoLink(body.Blocks[index]))
            {
                // On its own.
            }
            else if (DeterministicSlidePlanner.IsPointLine(texts[index]) && IsParagraph(index))
            {
                // Numbered paragraphs in a row are one list of points.
                for (var after = next; IsParagraph(after) && DeterministicSlidePlanner.IsPointLine(texts[after]); after++) passage.Add(after);
            }
            else if (IsParagraph(index) && IsListHeading(texts[index]) && !IsOwn(texts[index])
                     && open.Contains(next) && body.Blocks[next].Type is NoteBlockType.List or NoteBlockType.TaskList)
            {
                // "Three warnings:" and the list under it.
                for (var after = next; open.Contains(after) && body.Blocks[after].Type is NoteBlockType.List or NoteBlockType.TaskList; after++)
                    passage.Add(after);
            }
            else if (IsTitle(index) && IsParagraph(next) && IsSubtitleLine(texts[next]))
            {
                // A title and its subtitle.
                passage.Add(next);
            }
            else if (IsReference(index))
            {
                // A reference line joins its verse: the one after it (that starts with its first
                // verse's number, best of all), or the one before it.
                var first = ScriptureReference.VersesOf(texts[index]) is { } verses ? $"{verses.First} " : null;
                var previous = index - 1;
                var forward = IsParagraph(next) && !IsReference(next) && !IsOwn(texts[next]);
                if (forward && first is not null && texts[next].StartsWith(first, StringComparison.Ordinal))
                {
                    passage.Add(next);
                }
                else if (passages.Count > 0 && passages[^1] is [var only] && only == previous && IsParagraph(previous)
                         && ReadsAsVerse(texts[previous]) && !IsOwn(texts[previous]))
                {
                    passages[^1].Add(index);
                    used.Add(index);
                    continue;
                }
                else if (forward)
                {
                    passage.Add(next);
                }
            }
            used.UnionWith(passage);
            passages.Add(passage);
        }

        var found = passages.Select(blocks =>
        {
            if (mediaType.TryGetValue(blocks[0], out var type)) return new Suggestion(blocks, type);
            if (IsVideoFileLink(body.Blocks[blocks[0]])) return new Suggestion(blocks, SlideType.Video.Raw());
            if (ReadsAsListUnderHeading(blocks, body) && !IsOwn(texts[blocks[0]])) return new Suggestion(blocks, SlideType.Points.Raw());
            return SuggestionFor(blocks, string.Join("\n", blocks.Select(b => texts[b])),
                titles.Contains(blocks[0]) ? Shape.Heading : ShapeOf(body.Blocks[blocks[0]]), ownTypes);
        }).OfType<Suggestion>().ToList();
        // A note that sets its slide lines in bold: only the bold ones (and the scripture, quotes, titles and media).
        if (mode == SuggestMode.BoldLines || (mode == SuggestMode.PickForMe && MixesBold(body, candidates, texts)))
        {
            found = found.Where(passage =>
                passage.Type == SlideType.Scripture.Raw() || passage.Type == SlideType.Quote.Raw()
                || passage.Type == SlideType.Title.Raw() || passage.Type == SlideType.Picture.Raw()
                || passage.Type == SlideType.Video.Raw()
                || passage.Blocks.All(b => IsBold(body.Blocks[b]))).ToList();
        }
        return found;
    }

    /// <summary>
    /// In a note with markers, the lines outside them that are strongly slide-like anyway
    /// (scripture, a quote, a picture, a labelled line, the church's own type, bold in a note that
    /// bolds its slide lines), for the live note to offer as Adds.
    /// </summary>
    internal static List<Suggestion> StrongSuggestionsOutsideMarkers(NoteBody body, IReadOnlyList<string>? ownTypes = null)
    {
        ownTypes ??= [];
        if (!body.Blocks.Any(NoteSlides.StartsSlide)) return [];
        var held = MarkerPassages(body).SelectMany(p => p).ToHashSet();
        var rest = body.Copy();
        for (var index = 0; index < rest.Blocks.Count; index++)
        {
            if (held.Contains(index) || NoteSlides.StartsSlide(rest.Blocks[index]))
                rest.Blocks[index] = new NoteBlock { Type = NoteBlockType.Paragraph, Runs = [] };
        }
        var texts = body.Blocks.Select(LineText).ToList();
        var lines = Enumerable.Range(0, body.Blocks.Count).Where(i => texts[i].Length > 0 && !NoteSlides.StartsSlide(body.Blocks[i])).ToList();
        var usesBold = MixesBold(body, lines, texts);
        var strong = new[] { SlideType.Scripture, SlideType.Quote, SlideType.Picture, SlideType.Video }.Select(t => t.Raw()).Concat(ownTypes).ToHashSet();
        return Suggestions(rest, SuggestMode.Structure, ownTypes).Where(passage =>
        {
            if (passage.Type is { } type && strong.Contains(type)) return true;
            if (DeterministicSlidePlanner.LeadingLabel(texts[passage.Blocks[0]]) is not null) return true;
            return usesBold && passage.Blocks.All(b => IsBold(body.Blocks[b]));
        }).ToList();
    }

    private static Suggestion? SuggestionFor(IReadOnlyList<int> blocks, string text, Shape shape, IReadOnlyList<string> ownTypes)
    {
        if (OwnTypeOf(text, ownTypes) is { } own) return new Suggestion(blocks, own);
        // A bare link or loose words with no slide shape aren't suggested.
        if (KindOf(text, shape) is not { } kind || kind is SlideKind.Media or SlideKind.Picture) return null;
        return new Suggestion(blocks, kind == SlideKind.Body ? null : ParseType(kind.Raw())?.Raw());
    }

    /// <summary>The church's own type a passage's label names ("Announcement: …").</summary>
    internal static string? OwnTypeOf(string text, IReadOnlyList<string> ownTypes)
    {
        if (ownTypes.Count == 0 || DeterministicSlidePlanner.LeadingLabel(text) is not { } label) return null;
        return ownTypes.FirstOrDefault(t => string.Equals(t, label.Name, StringComparison.OrdinalIgnoreCase));
    }

    private static SlideKind? KindOf(string text, Shape shape)
    {
        if (shape == Shape.Heading) return SlideKind.Title;
        if (DeterministicSlidePlanner.ReadsAsQuote(text)) return SlideKind.Quote;
        var group = new MarkedGroup("", text, shape: shape);
        return new DeterministicSlidePlanner().Plan([group], null, new Dictionary<string, SlideRowOverride>()).Rows.FirstOrDefault()?.Kind;
    }

    /// <summary>The type a passage marked by hand starts as (null: a statement, left to the planner).</summary>
    public static string? GuessedType(string text, Shape shape, IReadOnlyList<string>? ownTypes = null)
    {
        if (OwnTypeOf(text, ownTypes ?? []) is { } own) return own;
        var kind = KindOf(text, shape);
        return kind is null or SlideKind.Body ? null : kind.Value.Raw();
    }

    /// <summary>The selection bar's types: the guess first, then the usual four; the rest in More.</summary>
    public static (List<string?> Shown, List<string> More) SelectionBarKinds(string? guess, IReadOnlyList<string>? ownTypes = null)
    {
        string[] shown = ["points", "scripture", "quote", "title"];
        var more = new[] { "lyrics", "body", "media" }.Concat(ownTypes ?? []);
        return ([guess, .. shown.Where(t => t != guess)], more.Where(t => t != guess).ToList());
    }

    public static string SelectionBarLabel(string? type)
    {
        if (type is null) return "Slide";
        if (type == SlideType.Points.Raw()) return "Point";
        if (ParseType(type) is { } known) return known.Label();
        return SlideKinds.Parse(type) is null ? type : type.Capitalized();
    }

    private static string LineText(NoteBlock block) =>
        string.Join("\n", Lines(block).Select(l => NoteReading.Text(l).TrimmingWhitespacesAndNewlines()).Where(t => t.Length > 0));

    /// <summary>A line of the order of service ("Offering", "Prayer:", "Pictures of camp"), not a slide.</summary>
    internal static bool IsRunSheet(string text)
    {
        var lower = text.ToLowerInvariant();
        foreach (var lead in new[] { "closing ", "opening " })
        {
            if (lower.StartsWith(lead, StringComparison.Ordinal)) lower = lower[lead.Length..];
        }
        if (lower.StartsWith("pictures of", StringComparison.Ordinal) || lower.StartsWith("photos of", StringComparison.Ordinal)) return true;
        return new[] { "intro", "introduction", "illustration", "offering", "prayer", "video", "announcements" }.Any(lead =>
        {
            if (!lower.StartsWith(lead, StringComparison.Ordinal)) return false;
            var after = lower[lead.Length..].FirstOrDefault(c => !char.IsWhiteSpace(c));
            return after == default || ":,–—-(.".Contains(after);
        });
    }

    /// <summary>"Video: Testimony https://…" or a linked "clip.mp4": a video slide to fill.</summary>
    internal static bool IsVideoLink(NoteBlock block)
    {
        if (IsVideoFileLink(block)) return true;
        if (block.Type != NoteBlockType.Paragraph) return false;
        var text = LineText(block);
        if (!text.StartsWith("video", StringComparison.OrdinalIgnoreCase) || !IsRunSheet(text)) return false;
        return DeterministicSlidePlanner.FirstLink(text) is not null || Lines(block).SelectMany(l => l).Any(r => !string.IsNullOrEmpty(r.Href));
    }

    internal static bool IsVideoFileLink(NoteBlock block)
    {
        if (block.Type != NoteBlockType.Paragraph || !Lines(block).SelectMany(l => l).Any(r => !string.IsNullOrEmpty(r.Href))) return false;
        var text = LineText(block);
        return !text.Contains('\n') && VideoFileRegex.IsMatch(text);
    }

    public const string VideoFilePattern = @"\.(mp4|mov|m4v|avi|wmv|webm|mkv|mpe?g)$";

    private static readonly Regex VideoFileRegex = new(VideoFilePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static bool ReadsAsListUnderHeading(IReadOnlyList<int> blocks, NoteBody body)
    {
        if (blocks.Count < 2 || body.Blocks[blocks[0]].Type != NoteBlockType.Paragraph || !IsListHeading(LineText(body.Blocks[blocks[0]]))) return false;
        return blocks.Skip(1).All(b => body.Blocks[b].Type is NoteBlockType.List or NoteBlockType.TaskList);
    }

    internal static bool IsListHeading(string text) =>
        !text.Contains('\n') && text.Length <= 80 && text.EndsWith(':') && !DeterministicSlidePlanner.IsPointLine(text);

    /// <summary>"(hold up the envelope)": a note to the speaker, not words for a slide.</summary>
    internal static bool IsAside(string text) => text.StartsWith('(') && text.EndsWith(')');

    private static bool IsSubtitle(string text) =>
        !text.Contains('\n') && text.Length <= 60 && !text.EndsWith('.') && !text.EndsWith('?') && !text.EndsWith('!')
        && !DeterministicSlidePlanner.IsPointLine(text) && DeterministicSlidePlanner.LabelledPoint(text) is null;

    private static bool ReadsAsVerse(string text) =>
        ScriptureReference.SplitText(text) is null && !DeterministicSlidePlanner.IsPointLine(text)
        && DeterministicSlidePlanner.LabelledPoint(text) is null;

    private static bool IsBold(NoteBlock block)
    {
        if (block.Type == NoteBlockType.Heading) return true;
        var runs = Lines(block).SelectMany(l => l).Where(r => r.Text.TrimmingWhitespacesAndNewlines().Length > 0).ToList();
        return runs.Count > 0 && runs.All(r => r.Bold == true);
    }

    /// <summary>Some lines bold and some not: the note uses bold to say which lines are slides.</summary>
    private static bool MixesBold(NoteBody body, IReadOnlyList<int> candidates, IReadOnlyList<string> texts)
    {
        var lines = candidates.Where(i => body.Blocks[i].Type != NoteBlockType.Heading && !ScriptureReference.IsReferenceLine(texts[i])).ToList();
        var bold = lines.Count(i => IsBold(body.Blocks[i]));
        return bold >= 2 && bold < lines.Count;
    }
}
