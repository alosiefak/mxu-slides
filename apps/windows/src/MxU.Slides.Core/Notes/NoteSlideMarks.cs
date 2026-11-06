using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Notes;

/// <summary>
/// Slide marks: the passages of a note that become slides. A mark is kept on the note's own runs
/// (their shared slideGroup, slideType, slideId and slideState fields), so a passage stays marked
/// as the note is edited, on either computer. Every run with the same group id is one passage,
/// even across blocks. Port of NoteSlideMarks.swift.
/// </summary>
public static partial class NoteSlideMarks
{
    /// <summary>The types a mark can name (its slideType). Own types are stored by their names instead.</summary>
    public enum SlideType { Scripture, Points, Quote, Title, Lyrics, Picture, Video, Body }

    public static string Raw(this SlideType type) => type.ToString().ToLowerInvariant();

    public static string Label(this SlideType type) => type == SlideType.Body ? "Statement" : type.Raw().Capitalized();

    public static SlideType? ParseType(string? raw) =>
        Enum.GetValues<SlideType>().Where(t => t.Raw() == raw).Select(t => (SlideType?)t).FirstOrDefault();

    public static readonly IReadOnlyList<SlideType> TextKinds =
        [SlideType.Body, SlideType.Points, SlideType.Scripture, SlideType.Quote, SlideType.Title, SlideType.Lyrics];

    /// <summary>A mark Make Slides suggested, not yet accepted.</summary>
    public const string Suggested = "suggested";

    /// <summary>A suggestion someone turned down: it reads as unmarked, and isn't suggested again.</summary>
    public const string Declined = "declined";

    /// <summary>The shape of the block a passage starts in, which hints at its kind.</summary>
    public enum Shape { Heading, List, Quote, Paragraph }

    /// <summary>A marked passage as the note holds it.</summary>
    public sealed class Group : IEquatable<Group>
    {
        public required string Id { get; init; }
        public string? Type { get; set; }

        /// <summary>The slide it made, once Make Slides has run.</summary>
        public string? SlideId { get; set; }

        public List<int> BlockIndexes { get; set; } = [];

        /// <summary>Its words, a line each; a stanza break where the passage skips a block.</summary>
        public string Text { get; set; } = "";

        public Shape Shape { get; set; } = Shape.Paragraph;

        public string? State { get; set; }

        /// <summary>Underlined words, a phrase each, to underline on the slide.</summary>
        public List<string> Underlines { get; set; } = [];

        /// <summary>Pictures and videos, by their note keys.</summary>
        public List<string> Media { get; set; } = [];

        public List<string> Links { get; set; } = [];

        public bool Equals(Group? other) =>
            other is not null && Id == other.Id && Type == other.Type && SlideId == other.SlideId && Text == other.Text
            && Shape == other.Shape && State == other.State && BlockIndexes.SequenceEqual(other.BlockIndexes)
            && Underlines.SequenceEqual(other.Underlines) && Media.SequenceEqual(other.Media) && Links.SequenceEqual(other.Links);

        public override bool Equals(object? obj) => Equals(obj as Group);

        public override int GetHashCode() => HashCode.Combine(Id, Text);

        public override string ToString() => $"{Id} {Type} [{string.Join(",", BlockIndexes)}] {Text}";
    }

    public static Shape ShapeOf(NoteBlock block) => block.Type switch
    {
        NoteBlockType.Heading => Shape.Heading,
        NoteBlockType.List or NoteBlockType.TaskList => Shape.List,
        NoteBlockType.Quote => Shape.Quote,
        _ => Shape.Paragraph,
    };

    /// <summary>The note's marked passages, in the order they start.</summary>
    public static List<Group> Groups(NoteBody body)
    {
        var order = new List<string>();
        var groups = new Dictionary<string, Group>();
        Group Ensure(string id, NoteRun run, Shape shape)
        {
            if (!groups.TryGetValue(id, out var group))
            {
                group = new Group
                {
                    Id = id, Type = run.SlideType, SlideId = run.SlideId, Shape = shape,
                    State = run.SlideState == Suggested ? Suggested : null,
                };
                groups[id] = group;
                order.Add(id);
            }
            group.Type ??= run.SlideType;
            group.SlideId ??= run.SlideId;
            return group;
        }
        for (var index = 0; index < body.Blocks.Count; index++)
        {
            var block = body.Blocks[index];
            // A picture or video carries its own mark.
            if (block.MarkRun is { SlideGroup: { Length: > 0 } mediaGroup } markRun && markRun.SlideState != Declined && NoteMedia.Key(block) is { } key)
            {
                var group = Ensure(mediaGroup, markRun, Shape.Paragraph);
                if (group.BlockIndexes.Count == 0 || group.BlockIndexes[^1] != index) group.BlockIndexes.Add(index);
                group.Media.Add(key);
            }
            foreach (var line in Lines(block))
            {
                var lineText = new Dictionary<string, string>();
                var lineOrder = new List<string>();
                string? underlining = null;
                foreach (var run in line)
                {
                    if (run.SlideGroup is { Length: > 0 } id && run.SlideState != Declined)
                    {
                        var group = Ensure(id, run, ShapeOf(block));
                        if (!lineText.ContainsKey(id))
                        {
                            lineOrder.Add(id);
                            lineText[id] = "";
                        }
                        lineText[id] += run.Text;
                        if (run.Href is { Length: > 0 } href && (group.Links.Count == 0 || group.Links[^1] != href)) group.Links.Add(href);
                        if (run.Underline == true)
                        {
                            // Underlined runs side by side are one phrase.
                            if (underlining == id && group.Underlines.Count > 0) group.Underlines[^1] += run.Text;
                            else if (run.Text.TrimmingWhitespaces().Length > 0)
                            {
                                group.Underlines.Add(run.Text);
                                underlining = id;
                            }
                        }
                    }
                    if (run.Underline != true || run.SlideGroup != underlining) underlining = null;
                }
                foreach (var id in lineOrder)
                {
                    var group = groups[id];
                    // A passage that skips a block (a blank line between) reads as two stanzas.
                    var stanzaBreak = group.BlockIndexes.Count > 0 && group.BlockIndexes[^1] < index - 1;
                    if (group.BlockIndexes.Count == 0 || group.BlockIndexes[^1] != index) group.BlockIndexes.Add(index);
                    var text = lineText[id].TrimmingWhitespacesAndNewlines();
                    if (text.Length > 0) group.Text += (group.Text.Length == 0 ? "" : stanzaBreak ? "\n\n" : "\n") + text;
                }
            }
        }
        return order.Select(id => groups[id]).ToList();
    }

    /// <summary>Every suggestion accepted: the marks stay, no longer "suggested".</summary>
    public static NoteBody AcceptingSuggestions(NoteBody body) => Mapping(body, run =>
    {
        if (run.SlideState != Suggested) return run;
        var accepted = run.Copy();
        accepted.SlideState = null;
        return accepted;
    });

    /// <summary>A copy of the note with every run changed by <paramref name="transform"/> (a picture's mark too).</summary>
    public static NoteBody Mapping(NoteBody body, Func<NoteRun, NoteRun> transform) =>
        new() { Blocks = body.Blocks.Select(b => MappingRuns(b, transform)).ToList(), PlainText = body.PlainText };

    internal static NoteBlock MappingRuns(NoteBlock block, Func<NoteRun, NoteRun> transform, Action<NoteBlock>? also = null)
    {
        var copy = block.Copy();
        if (copy.MarkRun is { } mark) copy.MarkRun = transform(mark);
        copy.Runs = copy.Runs?.Select(transform).ToList();
        if (copy.Items is { } items)
        {
            foreach (var item in items)
            {
                item.Runs = item.Runs.Select(transform).ToList();
                item.Blocks = item.Blocks?.Select(b => MappingRuns(b, transform, also)).ToList();
            }
        }
        copy.Blocks = copy.Blocks?.Select(b => MappingRuns(b, transform, also)).ToList();
        also?.Invoke(copy);
        return copy;
    }

    public static int? BlockIndexOfGroup(string id, NoteBody body) =>
        Groups(body).FirstOrDefault(g => g.Id == id) is { BlockIndexes.Count: > 0 } group ? group.BlockIndexes[0] : null;

    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>A new passage id: six letters and digits.</summary>
    public static string MintGroupId() => string.Concat(Enumerable.Range(0, 6).Select(_ => Alphabet[Random.Shared.Next(Alphabet.Length)]));

    /// <summary>The passage's runs remember the slide it made (and a suggestion becomes a mark).</summary>
    public static NoteBody Stamping(string slideId, string group, NoteBody body) => Mapping(body, run =>
    {
        if (run.SlideGroup != group) return run;
        var stamped = run.Copy();
        stamped.SlideId = slideId;
        if (stamped.SlideState == Suggested) stamped.SlideState = null;
        return stamped;
    });

    /// <summary>
    /// The made slides' cards put into the note after their passages (or in place of the "[slide]"
    /// marker above one), and each passage stamped with its first slide.
    /// </summary>
    public static NoteBody PlacingMadeSlides(IReadOnlyList<(string GroupId, IReadOnlyList<NoteSlideRef> Refs)> made, NoteBody body)
    {
        var placed = body.Copy();
        var known = Groups(body);
        var placements = made
            .Select(item => (Group: known.FirstOrDefault(g => g.Id == item.GroupId), Item: item))
            .Where(p => p.Group is { BlockIndexes.Count: > 0 })
            .Select(p => (Last: p.Group!.BlockIndexes[^1], First: p.Group.BlockIndexes[0], p.Item)).ToList();
        foreach (var placement in placements.OrderByDescending(p => p.Last))
        {
            var cards = placement.Item.Refs.Select(r => new NoteBlock { Type = NoteBlockType.SlideRef, Slide = r.Copy() }).ToList();
            if (MarkerIndexAbove(placement.First, placed) is { } marker)
            {
                placed.Blocks.InsertRange(placement.First, cards);
                placed.Blocks.RemoveAt(marker);
            }
            else
            {
                placed.Blocks.InsertRange(placement.Last + 1, cards);
            }
            if (placement.Item.Refs.FirstOrDefault()?.SlideId is { } slideId) placed = Stamping(slideId, placement.Item.GroupId, placed);
        }
        placed.PlainText = NoteReading.PlainText(placed.Blocks);
        return placed;
    }

    /// <summary>
    /// Marks the note as it's laid out: each heading, paragraph, list and quote a passage, or the
    /// passages between its "[slide]" markers. Lyrics (two section labels or more) are one passage.
    /// </summary>
    public static NoteBody MarkingByStructure(NoteBody body)
    {
        if (body.Blocks.Any(NoteSlides.StartsSlide)) return MarkingByMarkers(body);
        var marked = body.Copy();
        var labels = body.Blocks.Where(b => b.Type is NoteBlockType.Paragraph or NoteBlockType.Heading)
            .Count(b => Reflow.LabelName(NoteReading.Text(b.Runs)) is not null);
        var lyricsGroup = labels >= 2 ? MintGroupId() : null;
        for (var index = 0; index < marked.Blocks.Count; index++)
        {
            var block = marked.Blocks[index];
            if (!IsPassageType(block) || NoteSlides.IsMarker(block, NoteSlides.DefaultMarker)
                || NoteReading.Text(block).TrimmingWhitespacesAndNewlines().Length == 0
                || !Lines(block).SelectMany(l => l).All(r => r.SlideGroup is null)) continue;
            marked.Blocks[index] = Marking(block, lyricsGroup ?? MintGroupId());
        }
        return marked;
    }

    public static bool IsPassageType(NoteBlock block) =>
        block.Type is NoteBlockType.Heading or NoteBlockType.Paragraph or NoteBlockType.List or NoteBlockType.TaskList or NoteBlockType.Quote;

    /// <summary>Each run of blocks under a "[slide]" or "Slide 2:" marker becomes a passage.</summary>
    public static NoteBody MarkingByMarkers(NoteBody body)
    {
        var marked = body.Copy();
        foreach (var passage in MarkerPassages(body))
        {
            var group = MintGroupId();
            foreach (var index in passage) marked.Blocks[index] = Marking(marked.Blocks[index], group);
        }
        return marked;
    }

    /// <summary>The marker a passage's first block sits under (past blank lines and asides), if any.</summary>
    internal static int? MarkerIndexAbove(int first, NoteBody body)
    {
        if (first < 0 || first >= body.Blocks.Count || NoteSlides.InlineMarkerLabelLength(body.Blocks[first]) is not null) return null;
        for (var index = first - 1; index >= 0; index--)
        {
            var block = body.Blocks[index];
            if (NoteSlides.IsMarker(block, NoteSlides.DefaultMarker)) return index;
            var text = NoteReading.Text(block).TrimmingWhitespacesAndNewlines();
            if (block.Type != NoteBlockType.Paragraph || !(text.Length == 0 || IsAside(text))) return null;
        }
        return null;
    }

    /// <summary>The runs cut after <paramref name="count"/> characters, a run split in two if need be.</summary>
    internal static (List<NoteRun> Head, List<NoteRun> Tail) Splitting(IReadOnlyList<NoteRun> runs, int count)
    {
        var head = new List<NoteRun>();
        var tail = new List<NoteRun>();
        var left = count;
        foreach (var run in runs)
        {
            if (left <= 0)
            {
                tail.Add(run.Copy());
                continue;
            }
            if (run.Text.Length <= left)
            {
                head.Add(run.Copy());
                left -= run.Text.Length;
                continue;
            }
            head.Add(run.WithText(run.Text[..left]));
            tail.Add(run.WithText(run.Text[left..]));
            left = 0;
        }
        return (head, tail);
    }

    /// <summary>
    /// The passages under markers: the words after each marker (to a blank line, for a "Slide 3:"
    /// one), a video each, and pictures in a row together. Asides in brackets stay out.
    /// </summary>
    internal static List<List<int>> MarkerPassages(NoteBody body)
    {
        var passages = new List<List<int>>();
        int? words = null, pictures = null;
        var underMarker = false;
        var took = false;
        var endsAtEmptyLine = false;
        for (var index = 0; index < body.Blocks.Count; index++)
        {
            var block = body.Blocks[index];
            var oneLine = NoteSlides.InlineMarkerLabelLength(block) is not null;
            if (oneLine || NoteSlides.IsMarker(block, NoteSlides.DefaultMarker))
            {
                underMarker = true;
                took = false;
                // "[slide]" runs to the next marker; "Slide 3:" to the next blank line once it has words.
                endsAtEmptyLine = !string.Equals(NoteReading.Text(block).TrimmingWhitespacesAndNewlines(), NoteSlides.DefaultMarker, StringComparison.OrdinalIgnoreCase);
                words = null;
                pictures = null;
                if (!oneLine) continue;
            }
            if (!underMarker) continue;
            if (NoteMedia.Blocks(new NoteBody { Blocks = [block], PlainText = "" }).Count > 0)
            {
                if (block.SlideGroup is not null) continue;
                took = true;
                if (!NoteMedia.IsVideo(block) && pictures is { } row)
                {
                    passages[row].Add(index);
                }
                else
                {
                    passages.Add([index]);
                    pictures = NoteMedia.IsVideo(block) ? null : passages.Count - 1;
                }
                continue;
            }
            var text = NoteReading.Text(block).TrimmingWhitespacesAndNewlines();
            if (block.Type == NoteBlockType.Paragraph && text.Length == 0)
            {
                if (took && endsAtEmptyLine) underMarker = false;
                continue;
            }
            pictures = null;
            if (!IsPassageType(block) || text.Length == 0 || IsAside(text) || !Lines(block).SelectMany(l => l).All(r => r.SlideGroup is null)) continue;
            took = true;
            if (IsVideoFileLink(block))
            {
                passages.Add([index]);
            }
            else if (words is { } at)
            {
                passages[at].Add(index);
            }
            else
            {
                passages.Add([index]);
                words = passages.Count - 1;
            }
        }
        return passages;
    }

    /// <summary>Marks blocks as one passage, and stamps it with its slide when it has one.</summary>
    public static NoteBody Marking(IReadOnlyList<int> blockIndexes, string group, string? slideId, NoteBody body)
    {
        var marked = body.Copy();
        foreach (var index in blockIndexes)
        {
            if (index >= 0 && index < marked.Blocks.Count) marked.Blocks[index] = Marking(marked.Blocks[index], group);
        }
        return slideId is null ? marked : Stamping(slideId, group, marked);
    }

    /// <summary>A block marked into a passage; a "Slide 2:" label at its start stays out of the passage's words.</summary>
    public static NoteBlock Marking(NoteBlock block, string group, string? type = null, string? state = null)
    {
        NoteRun Mark(NoteRun run)
        {
            var marked = run.Copy();
            marked.SlideGroup = group;
            if (type is not null) marked.SlideType = type;
            marked.SlideState = state;
            return marked;
        }
        if (NoteSlides.InlineMarkerLabelLength(block) is { } label && block.Runs is { } runs)
        {
            var (head, tail) = Splitting(runs, label);
            var copy = block.Copy();
            copy.Runs = [.. head, .. tail.Select(Mark)];
            return copy;
        }
        var result = block.Copy();
        if (result.MarkRun is { } markRun) result.MarkRun = Mark(markRun);
        result.Runs = result.Runs?.Select(Mark).ToList();
        if (result.Items is { } items)
        {
            foreach (var item in items)
            {
                item.Runs = item.Runs.Select(Mark).ToList();
                item.Blocks = item.Blocks?.Select(b => Marking(b, group, type, state)).ToList();
            }
        }
        result.Blocks = result.Blocks?.Select(b => Marking(b, group, type, state)).ToList();
        return result;
    }

    /// <summary>A passage's words rewritten (from Make Slides): the new words in its first run, its other runs gone, emptied blocks too.</summary>
    public static NoteBody ReplacingText(string id, string text, NoteBody body)
    {
        var placed = false;
        var blocks = new List<NoteBlock>();
        foreach (var block in body.Blocks)
        {
            if (Replacing(id, text, block, ref placed) is { } kept) blocks.Add(kept);
        }
        return new NoteBody { Blocks = blocks, PlainText = body.PlainText };
    }

    private static NoteBlock? Replacing(string id, string text, NoteBlock original, ref bool placed)
    {
        var block = original.Copy();
        if (block.Runs is { } runs)
        {
            block.Runs = ReplacingRuns(id, text, runs, ref placed);
            if (block.Runs.Count == 0 && block.Type is NoteBlockType.Paragraph or NoteBlockType.Heading) return null;
        }
        if (block.Items is { } items)
        {
            var kept = new List<NoteListItem>();
            foreach (var item in items)
            {
                item.Runs = ReplacingRuns(id, text, item.Runs, ref placed);
                if (item.Blocks is { } inner)
                {
                    var keptInner = new List<NoteBlock>();
                    foreach (var b in inner)
                    {
                        if (Replacing(id, text, b, ref placed) is { } k) keptInner.Add(k);
                    }
                    item.Blocks = keptInner;
                }
                if (item.Runs.Count > 0 || (item.Blocks ?? []).Count > 0) kept.Add(item);
            }
            block.Items = kept;
            if (kept.Count == 0) return null;
        }
        if (block.Blocks is { } blocks)
        {
            var kept = new List<NoteBlock>();
            foreach (var b in blocks)
            {
                if (Replacing(id, text, b, ref placed) is { } k) kept.Add(k);
            }
            block.Blocks = kept;
            if (kept.Count == 0 && block.Type == NoteBlockType.Quote) return null;
        }
        return block;
    }

    private static List<NoteRun> ReplacingRuns(string id, string text, IReadOnlyList<NoteRun> runs, ref bool placed)
    {
        var result = new List<NoteRun>();
        foreach (var run in runs)
        {
            if (run.SlideGroup != id)
            {
                result.Add(run);
                continue;
            }
            if (placed) continue;
            placed = true;
            result.Add(run.WithText(text));
        }
        return result;
    }

    /// <summary>The passage unmarked: its runs keep their words and lose the mark.</summary>
    public static NoteBody Unmarking(string id, NoteBody body) => Mapping(body, run =>
    {
        if (run.SlideGroup != id) return run;
        var clear = run.Copy();
        clear.SlideGroup = null;
        clear.SlideType = null;
        clear.SlideId = null;
        clear.SlideState = null;
        return clear;
    });

    /// <summary>A block's lines of runs: a paragraph's one, a list's items (and what's inside them), a quote's paragraphs.</summary>
    internal static List<List<NoteRun>> Lines(NoteBlock block) => block.Type switch
    {
        NoteBlockType.Heading or NoteBlockType.Paragraph => [block.Runs ?? []],
        NoteBlockType.List or NoteBlockType.TaskList => (block.Items ?? [])
            .SelectMany(item => new[] { item.Runs }.Concat((item.Blocks ?? []).SelectMany(Lines))).ToList(),
        NoteBlockType.Quote => (block.Blocks ?? []).SelectMany(Lines).ToList(),
        _ => [],
    };
}
