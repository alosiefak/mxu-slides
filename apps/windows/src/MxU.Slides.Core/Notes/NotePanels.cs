using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Notes;

/// <summary>
/// A deck's slides as cards in its note: which slides have no card yet, and the cards kept in
/// step when slides are added or deleted in the deck. Port of NoteDeckCards.swift.
/// </summary>
public static class NoteDeckCards
{
    /// <summary>The deck's slides that no card in the note shows.</summary>
    public static List<Slide> Unplaced(Presentation deck, NoteBody body)
    {
        var carded = body.Blocks.Where(b => b.Type == NoteBlockType.SlideRef && b.Slide?.PresentationId == deck.Id)
            .Select(b => b.Slide?.SlideId).OfType<string>().ToHashSet();
        return deck.Slides.Where(s => !carded.Contains(s.Id)).ToList();
    }

    /// <summary>
    /// Cards for slides added to the deck (each after the card of the slide before it), and the
    /// hand-placed cards of slides deleted from it taken out. The same note when nothing changes.
    /// </summary>
    public static NoteBody Syncing(Presentation deck, IReadOnlySet<string> added, IReadOnlySet<string> removed, NoteBody body)
    {
        var blocks = body.Blocks.Select(b => b.Copy()).ToList();
        var changed = false;
        if (removed.Count > 0)
        {
            var before = blocks.Count;
            blocks.RemoveAll(block => block.Type == NoteBlockType.SlideRef && block.Slide is { } slide && slide.PresentationId == deck.Id
                && slide.PlacedByHand == true && slide.SlideId is { } id && removed.Contains(id));
            changed = blocks.Count != before;
        }
        List<int> CardIndexes(string slideId) => Enumerable.Range(0, blocks.Count)
            .Where(i => blocks[i].Type == NoteBlockType.SlideRef && blocks[i].Slide?.PresentationId == deck.Id && blocks[i].Slide?.SlideId == slideId).ToList();
        for (var index = 0; index < deck.Slides.Count; index++)
        {
            var slide = deck.Slides[index];
            if (!added.Contains(slide.Id) || CardIndexes(slide.Id).Count > 0
                || NoteSlides.HandRef(deck.Id, slide.Id, _ => deck) is not { } reference) continue;
            var card = new NoteBlock { Type = NoteBlockType.SlideRef, Slide = reference };
            var anchor = deck.Slides.Take(index).Reverse().Select(s => CardIndexes(s.Id)).Where(c => c.Count > 0).Select(c => (int?)c[^1]).FirstOrDefault();
            var firstCard = blocks.FindIndex(b => b.Type == NoteBlockType.SlideRef && b.Slide?.PresentationId == deck.Id);
            if (anchor is { } at) blocks.Insert(at + 1, card);
            else if (firstCard >= 0) blocks.Insert(firstCard, card);
            else blocks.Add(card);
            changed = true;
        }
        if (!changed) return body;
        return new NoteBody { Blocks = blocks, PlainText = NoteReading.PlainText(blocks) };
    }
}

/// <summary>
/// The note read as panels beside the deck: each passage with the cards of the slides it made,
/// and each card placed by hand on its own. Port of NotePanels.swift.
/// </summary>
public static class NotePanels
{
    public sealed record Panel(string Id, string? GroupId, string? Type, bool Made, (int First, int Last)? Words, IReadOnlyList<int> Cards, int Lines = 0)
    {
        public bool PlacedByHand => GroupId is null;
        public int FirstBlock => Math.Min(Words?.First ?? int.MaxValue, Cards.Count > 0 ? Cards[0] : int.MaxValue);
        public int LastBlock => Math.Max(Words?.Last ?? 0, Cards.Count > 0 ? Cards[^1] : 0);

        public bool Equals(Panel? other) =>
            other is not null && Id == other.Id && GroupId == other.GroupId && Type == other.Type && Made == other.Made
            && Words == other.Words && Cards.SequenceEqual(other.Cards) && Lines == other.Lines;

        public override int GetHashCode() => HashCode.Combine(Id, GroupId);
    }

    public static List<Panel> Panels(NoteBody body)
    {
        var claimed = new HashSet<int>();
        var result = new List<Panel>();
        var groups = NoteSlideMarks.Groups(body);
        for (var position = 0; position < groups.Count; position++)
        {
            var group = groups[position];
            if (group.BlockIndexes.Count == 0) continue;
            var first = group.BlockIndexes[0];
            var last = group.BlockIndexes[^1];
            var range = NoteSlideUpdate.CardRange(group, body);
            var cards = range is { } r
                ? Enumerable.Range(r.Start, r.End - r.Start).Where(i => body.Blocks[i].Slide?.PlacedByHand != true && !claimed.Contains(i)).ToList()
                : [];
            // Cards that run on into the next passage's own slides belong to it.
            if (position + 1 < groups.Count && groups[position + 1].SlideId is { } stamp && stamp != group.SlideId)
            {
                var cut = cards.FindIndex(i => body.Blocks[i].Slide?.SlideId == stamp);
                if (cut >= 0) cards = cards.Take(cut).ToList();
            }
            claimed.UnionWith(cards);
            var lines = group.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            result.Add(new Panel(group.Id, group.Id, group.Type, group.SlideId is not null, (first, last), cards, lines));
        }
        for (var index = 0; index < body.Blocks.Count; index++)
        {
            var block = body.Blocks[index];
            if (block.Type != NoteBlockType.SlideRef || claimed.Contains(index)) continue;
            result.Add(new Panel($"card:{block.Slide?.SlideId ?? index.ToString(System.Globalization.CultureInfo.InvariantCulture)}", null, null, true, null, [index]));
        }
        return result.Select((p, i) => (p, i)).OrderBy(x => x.p.FirstBlock).ThenBy(x => x.i).Select(x => x.p).ToList();
    }

    /// <summary>"Slide 3", "Slides 3 to 5", "Slides 2, 4": a panel's slides by their numbers.</summary>
    public static string? Title(IEnumerable<int> slideNumbers)
    {
        var numbers = slideNumbers.Order().ToList();
        if (numbers.Count == 0) return null;
        if (numbers.Count == 1) return $"Slide {numbers[0]}";
        if (numbers[^1] - numbers[0] == numbers.Count - 1) return $"Slides {numbers[0]} to {numbers[^1]}";
        return "Slides " + string.Join(", ", numbers);
    }

    public static string FoldedTitle(int lines) => lines == 1 ? "1 line" : $"{lines} lines";
}

/// <summary>A note's passages read once per version of the note, for the views that ask often. Port of NotePassages.swift.</summary>
public sealed class NotePassages
{
    public IReadOnlyList<MarkedGroup> Groups { get; }

    /// <summary>Each passage's made cards, by its id.</summary>
    public IReadOnlyDictionary<string, List<NoteSlideRef>> Cards { get; }

    public NotePassages(NoteBody body)
    {
        var marks = NoteSlideMarks.Groups(body);
        Groups = marks.Select(NoteSlideBuilder.Marked).ToList();
        var cards = new Dictionary<string, List<NoteSlideRef>>();
        foreach (var mark in marks) cards.TryAdd(mark.Id, NoteSlideUpdate.CardsAfter(mark, body));
        Cards = cards;
    }

    /// <summary>The passage that made a slide, with its cards.</summary>
    public (MarkedGroup Group, List<NoteSlideRef> Cards)? PassageContainingSlide(string slideId)
    {
        foreach (var group in Groups)
        {
            var cards = Cards.GetValueOrDefault(group.Id) ?? [];
            if (cards.Any(c => c.SlideId == slideId)) return (group, cards);
        }
        return null;
    }
}

/// <summary>Keeps the last note's passages, so asking again for an unchanged note costs nothing.</summary>
public sealed class NotePassagesMemo
{
    private NoteBody? body;
    private NotePassages? held;

    /// <summary>How many times it read a note (for tests).</summary>
    public int Parses { get; private set; }

    public NotePassages Passages(NoteBody body)
    {
        NotePassages current;
        if (held is not null && Equals(this.body, body))
        {
            current = held;
        }
        else
        {
            current = new NotePassages(body);
            held = current;
            Parses++;
        }
        this.body = body.Copy();
        return current;
    }
}
