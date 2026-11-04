using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Notes;

/// <summary>
/// The note beside the slides while presenting: the note's prose, its passages each with the
/// slides they made in a lane beside them, and the deck's other slides where they fall. The
/// live slide's row lights up as the service moves on. Port of NoteFollowAlong.swift.
/// </summary>
public static class NoteFollowAlong
{
    public sealed record Slot(string PresentationId, string SlideId);

    /// <summary>One line of a passage beside its slides: a slide's words, or a blank.</summary>
    public sealed record Row(string Id, string Kind, string? Words, List<Slot> Slides)
    {
        public bool Equals(Row? other) => other is not null && Id == other.Id && Kind == other.Kind && Words == other.Words && Slides.SequenceEqual(other.Slides);

        public override int GetHashCode() => HashCode.Combine(Id, Kind, Words);
    }

    public abstract record Item
    {
        /// <summary>Note text between passages.</summary>
        public sealed record Prose(List<int> Blocks) : Item
        {
            public bool Equals(Prose? other) => other is not null && Blocks.SequenceEqual(other.Blocks);

            public override int GetHashCode() => Blocks.Count;
        }

        /// <summary>A passage with its slides. Blocks is inclusive.</summary>
        public sealed record Passage(string GroupId, string Kind, int FirstBlock, int LastBlock, List<Row> Rows) : Item
        {
            public bool Equals(Passage? other) =>
                other is not null && GroupId == other.GroupId && Kind == other.Kind && FirstBlock == other.FirstBlock
                && LastBlock == other.LastBlock && Rows.SequenceEqual(other.Rows);

            public override int GetHashCode() => HashCode.Combine(GroupId, FirstBlock);
        }

        /// <summary>A slide on its own: a card placed by hand in the note, or one of the deck's slides the note never mentions.</summary>
        public sealed record SlideItem(Slot Slot, bool InNote) : Item;

        public string Id => this switch
        {
            Prose p => $"prose:{(p.Blocks.Count > 0 ? p.Blocks[0] : 0)}",
            Passage p => $"passage:{p.GroupId}",
            SlideItem s => $"slide:{s.Slot.SlideId}",
            _ => "",
        };

        public IEnumerable<Slot> Slots => this switch
        {
            Passage p => p.Rows.SelectMany(r => r.Slides),
            SlideItem s => [s.Slot],
            _ => [],
        };

        public IEnumerable<int> BlockIndexes => this switch
        {
            Prose p => p.Blocks,
            Passage p => Enumerable.Range(p.FirstBlock, p.LastBlock - p.FirstBlock + 1),
            _ => [],
        };
    }

    /// <summary>A line as the view draws it.</summary>
    public sealed record Line(string Id, LineContent Content);

    public abstract record LineContent
    {
        public sealed record ProseLine(List<int> Blocks) : LineContent;

        public sealed record RowLine(string PassageKind, Row Row, bool First, bool Last) : LineContent;

        public sealed record SlideLine(Slot Slot, bool InNote) : LineContent;
    }

    public static List<Line> Lines(IEnumerable<Item> items) => items.SelectMany(item => item switch
    {
        Item.Prose p => [new Line(item.Id, new LineContent.ProseLine(p.Blocks))],
        Item.Passage p => p.Rows.Select((row, index) =>
            new Line($"row:{row.Id}", new LineContent.RowLine(p.Kind, row, index == 0, index == p.Rows.Count - 1))).ToList(),
        Item.SlideItem s => [new Line(item.Id, new LineContent.SlideLine(s.Slot, s.InNote))],
        _ => new List<Line>(),
    }).ToList();

    /// <summary>Which line shows each slide, and each note block, so the view can scroll to the live one.</summary>
    public static (Dictionary<string, string> BySlide, Dictionary<int, string> ByBlock) LineIndex(IEnumerable<Item> items)
    {
        var bySlide = new Dictionary<string, string>();
        var byBlock = new Dictionary<int, string>();
        foreach (var item in items)
        {
            switch (item)
            {
                case Item.Prose p:
                    foreach (var block in p.Blocks) byBlock[block] = item.Id;
                    break;
                case Item.Passage p:
                    foreach (var row in p.Rows)
                    {
                        foreach (var slot in row.Slides) bySlide.TryAdd(slot.SlideId, $"row:{row.Id}");
                    }
                    if (p.Rows.Count > 0)
                    {
                        for (var block = p.FirstBlock; block <= p.LastBlock; block++) byBlock[block] = $"row:{p.Rows[0].Id}";
                    }
                    break;
                case Item.SlideItem s:
                    bySlide.TryAdd(s.Slot.SlideId, item.Id);
                    break;
            }
        }
        return (bySlide, byBlock);
    }

    /// <summary>The slide lane's width: about a third of the view, in steps of ten, between 80 and 220.</summary>
    public static double LaneWidth(double viewWidth)
    {
        if (viewWidth <= 0) return 160;
        return Math.Min(220, Math.Max(80, Math.Round(viewWidth * 0.3 / 10, MidpointRounding.AwayFromZero) * 10));
    }

    public static List<Item> Items(
        NoteBody body, Presentation? deck, IReadOnlyList<NoteMadePassage>? memory,
        Func<Slot, Slide?> slide, Func<MarkedGroup, IReadOnlyList<Slide>, SlideRow?> replan)
    {
        memory ??= [];
        var groups = new Dictionary<string, NoteSlideMarks.Group>();
        foreach (var group in NoteSlideMarks.Groups(body)) groups.TryAdd(group.Id, group);
        var covered = new HashSet<int>();
        var shown = new HashSet<Slot>();
        var placed = new List<(int First, Item Item)>();
        foreach (var panel in NotePanels.Panels(body))
        {
            var cards = panel.Cards.Select(index =>
            {
                if (body.Blocks[index].Slide is not { PresentationId: { } presentationId, SlideId: { } slideId }) return null;
                var slot = new Slot(presentationId, slideId);
                return shown.Add(slot) ? slot : null;
            }).OfType<Slot>().ToList();
            covered.UnionWith(panel.Cards);
            if (panel.GroupId is { } groupId && panel.Words is { } words && groups.TryGetValue(groupId, out var group))
            {
                var slots = cards.Where(s => slide(s) is not null).ToList();
                // A note whose made cards aren't saved finds its slides in its memory.
                if (slots.Count == 0 && deck is not null && memory.FirstOrDefault(m => m.GroupId == groupId) is { } made)
                    slots = made.Slides.Select(s => new Slot(deck.Id, s.SlideId)).Where(s => slide(s) is not null && shown.Add(s)).ToList();
                if (slots.Count == 0) continue;
                for (var b = words.First; b <= words.Last; b++) covered.Add(b);
                var marked = NoteSlideBuilder.Marked(group);
                var slides = slots.Select(slide).OfType<Slide>().ToList();
                var plan = replan(marked, slides);
                placed.Add((words.First, new Item.Passage(groupId, group.Type ?? "slide", words.First, words.Last, Rows(group, slots, slides, plan))));
            }
            else if (cards.FirstOrDefault() is { } slot && slide(slot) is not null)
            {
                placed.Add((panel.FirstBlock, new Item.SlideItem(slot, true)));
            }
        }
        var run = new List<int>();
        for (var index = 0; index < body.Blocks.Count; index++)
        {
            if (covered.Contains(index) || body.Blocks[index].Type == NoteBlockType.SlideRef)
            {
                if (run.Count > 0) placed.Add((run[0], new Item.Prose(run)));
                run = [];
            }
            else
            {
                run.Add(index);
            }
        }
        if (run.Count > 0) placed.Add((run[0], new Item.Prose(run)));
        var items = placed.Select((p, i) => (p, i)).OrderBy(x => x.p.First).ThenBy(x => x.i).Select(x => x.p.Item).ToList();
        return deck is null ? items : PlacingUnclaimed(deck, items);
    }

    /// <summary>A passage's rows: each part of its plan with the slides that show it, and its blank slides where they fall.</summary>
    internal static List<Row> Rows(NoteSlideMarks.Group group, IReadOnlyList<Slot> slots, IReadOnlyList<Slide> slides, SlideRow? plan)
    {
        var kind = plan?.Kind.Raw() ?? group.Type ?? SlideKind.Body.Raw();
        var parts = plan is null ? [] : NoteSlideUpdate.Texts(plan);
        var partWords = parts.Count > 1 ? parts.Select(Words).ToList() : [group.Text];
        var partRows = partWords.Select((words, index) => new Row($"{group.Id}:{index}", kind, words, [])).ToList();
        var texts = slides.Select(s => Words(NoteSlideUpdate.Boxes(s))).ToList();
        var blank = slides.Select(IsBlank).ToList();
        var part = new Dictionary<int, int>();
        var claimed = new HashSet<int>();
        // A slide whose words match a part shows that part; the rest go in order.
        for (var index = 0; index < slides.Count; index++)
        {
            if (blank[index]) continue;
            var match = Enumerable.Range(0, partWords.Count).FirstOrDefault(p => !claimed.Contains(p) && partWords[p] == texts[index], -1);
            if (match < 0) continue;
            part[index] = match;
            claimed.Add(match);
        }
        var ordinal = 0;
        for (var index = 0; index < slides.Count; index++)
        {
            if (blank[index]) continue;
            if (!part.ContainsKey(index))
            {
                if (ordinal < partWords.Count && !claimed.Contains(ordinal))
                {
                    part[index] = ordinal;
                    claimed.Add(ordinal);
                }
                else
                {
                    part[index] = 0;
                }
            }
            ordinal++;
        }
        for (var index = 0; index < slides.Count; index++)
        {
            if (!blank[index]) partRows[part.GetValueOrDefault(index)].Slides.Add(slots[index]);
        }
        var result = new List<Row>();
        var emitted = 0;
        for (var index = 0; index < slides.Count; index++)
        {
            if (blank[index])
            {
                result.Add(new Row(slots[index].SlideId, SlideKind.Blank.Raw(), null, [slots[index]]));
            }
            else
            {
                var target = part.GetValueOrDefault(index);
                while (emitted <= target)
                {
                    result.Add(partRows[emitted]);
                    emitted++;
                }
            }
        }
        result.AddRange(partRows.Skip(emitted));
        return result.Select(row => row.Slides.Count > 0 ? row with { Id = row.Slides[0].SlideId } : row).ToList();
    }

    /// <summary>A slide with no media and no words.</summary>
    public static bool IsBlank(Slide slide) =>
        slide.Background is null && slide.Objects.All(o => o.ObjectKind == SlideObjectKind.Text && Lyrics.SwiftString.TrimmingWhitespacesAndNewlines(o.Text).Length == 0);

    internal static string Words(IEnumerable<(string Name, string Text)> boxes) =>
        string.Join("\n", boxes.Select(b => Lyrics.SwiftString.TrimmingWhitespacesAndNewlines(b.Text)).Where(t => t.Length > 0));

    /// <summary>The deck's slides no item shows, each after the slide before it (or before the first, or at the end).</summary>
    internal static List<Item> PlacingUnclaimed(Presentation deck, List<Item> items)
    {
        var holder = new Dictionary<string, int>();
        for (var index = 0; index < items.Count; index++)
        {
            foreach (var slot in items[index].Slots)
            {
                if (slot.PresentationId == deck.Id) holder.TryAdd(slot.SlideId, index);
            }
        }
        int? firstHolder = holder.Count == 0 ? null : holder.Values.Min();
        var after = new Dictionary<int, List<Item>>();
        var before = new List<Item>();
        var atEnd = new List<Item>();
        int? lastHolder = null;
        foreach (var slide in deck.Slides)
        {
            if (holder.TryGetValue(slide.Id, out var index))
            {
                lastHolder = index;
                continue;
            }
            var item = new Item.SlideItem(new Slot(deck.Id, slide.Id), false);
            if (lastHolder is { } last)
            {
                if (!after.TryGetValue(last, out var list)) after[last] = list = [];
                list.Add(item);
            }
            else if (firstHolder is not null) before.Add(item);
            else atEnd.Add(item);
        }
        var result = new List<Item>();
        for (var index = 0; index < items.Count; index++)
        {
            if (index == firstHolder) result.AddRange(before);
            result.Add(items[index]);
            result.AddRange(after.GetValueOrDefault(index) ?? []);
        }
        return [.. result, .. atEnd];
    }
}
