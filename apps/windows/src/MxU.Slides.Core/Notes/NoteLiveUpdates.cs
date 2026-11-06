using System.Globalization;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Notes;

/// <summary>
/// A note that keeps working after its slides are made. Each edit to the note is compared with
/// what Make Slides made (the note's memory, madePassages): new words for a slide are an Update,
/// a new slide-like passage is an Add, a passage gone is a Remove. They wait in the note's
/// pendingUpdates until someone reviews them in "Changes from the Notes". Port of
/// NoteLiveUpdates.swift.
/// </summary>
public static class NoteLiveUpdates
{
    /// <summary>The build mode of a note whose marks were all made by hand: no Adds are offered.</summary>
    public const string ByHand = "hand";

    public sealed class Found
    {
        public List<NotePendingUpdate> Updates { get; } = [];

        /// <summary>The passage words each update was found for, so a skipped one comes back only when they change.</summary>
        public Dictionary<string, string> Texts { get; } = [];

        /// <summary>The note with lost marks found again, when any were.</summary>
        public NoteBody? Remarked { get; set; }
    }

    /// <summary>How a note's slides were picked, so later Adds are picked the same way.</summary>
    public static string BuildMode(NoteBody body, NoteSlideMarks.SuggestMode autoMark = NoteSlideMarks.SuggestMode.PickForMe)
    {
        if (autoMark != NoteSlideMarks.SuggestMode.PickForMe) return autoMark.Raw();
        var bare = Unmarked(body);
        var picked = NoteSlideMarks.Suggestions(bare, NoteSlideMarks.SuggestMode.PickForMe);
        return !picked.SequenceEqual(NoteSlideMarks.Suggestions(bare, NoteSlideMarks.SuggestMode.Structure))
               && picked.SequenceEqual(NoteSlideMarks.Suggestions(bare, NoteSlideMarks.SuggestMode.BoldLines))
            ? NoteSlideMarks.SuggestMode.BoldLines.Raw()
            : NoteSlideMarks.SuggestMode.Structure.Raw();
    }

    /// <summary>The updates the note's edits make for its deck.</summary>
    public static Found Changes(
        NoteBody body, Presentation deck, IReadOnlyList<NoteMadePassage> memory, string? mode,
        IReadOnlyList<NoteSkippedUpdate>? skipped, SlideDesignMap designMap, Func<string, Theme?> theme,
        IReadOnlyDictionary<string, string>? mediaIds = null, bool refind = true)
    {
        skipped ??= [];
        var found = new Found();
        body = Filled(body);
        var inDeck = new Dictionary<string, Slide>();
        foreach (var slide in deck.Slides) inDeck.TryAdd(slide.Id, slide);
        var live = memory.Where(p => p.Slides.Any(s => inDeck.ContainsKey(s.SlideId))).ToList();

        // Passages whose marks are gone: found again by their words where possible.
        var markedIds = NoteSlideMarks.Groups(body).Select(g => g.Id).ToHashSet();
        var lost = live.Where(p => !markedIds.Contains(p.GroupId)).ToList();
        if (refind && lost.Count > 0)
        {
            var matched = NoteSlideMatch.MatchPassages(body, lost);
            if (matched.Matches.Count > 0)
            {
                body = matched.Body;
                found.Remarked = body;
            }
        }
        var marks = NoteSlideMarks.Groups(body);
        var byId = new Dictionary<string, NoteSlideMarks.Group>();
        foreach (var mark in marks) byId.TryAdd(mark.Id, mark);
        var memoryById = new Dictionary<string, NoteMadePassage>();
        foreach (var passage in memory) memoryById.TryAdd(passage.GroupId, passage);
        List<Slide> SlidesOf(NoteMadePassage passage) => passage.Slides.Select(s => inDeck.GetValueOrDefault(s.SlideId)).OfType<Slide>().ToList();

        foreach (var mark in marks)
        {
            if (!memoryById.TryGetValue(mark.Id, out var passage) || mark.Text == passage.Text) continue;
            var made = SlidesOf(passage);
            if (made.Count == 0) continue;
            var group = NoteSlideBuilder.Marked(mark);
            var rowTheme = theme(made[0].ThemeId ?? deck.ThemeId);
            if (NoteSlideUpdate.Replan(group, made, rowTheme, designMap.Options) is not { } row) continue;
            var choice = NoteSlideBuilder.PresetChoice(row.Kind, new NoteSlideBuilder.SwitchOverride.TypeDefault(), designMap);
            var result = NoteSlideUpdate.Updating(made, row);
            var updated = NoteSlideUpdate.ApplyingSwitch(choice, result.Updated);
            var appended = NoteSlideUpdate.ApplyingSwitch(choice, result.Appended);
            var remembered = new Dictionary<string, List<NoteMadeBox>>();
            foreach (var slide in passage.Slides) remembered.TryAdd(slide.SlideId, slide.Boxes);
            foreach (var after in updated)
            {
                if (!inDeck.TryGetValue(after.Id, out var before) || before.KeepWords == true) continue;
                var words = NoteSlideMatch.Boxes(after);
                // No update when the slide already says it, or still says what was made (the change is elsewhere).
                if (NoteSlideMatch.SameBoxes(words, remembered.GetValueOrDefault(after.Id)) || NoteSlideMatch.SameBoxes(words, NoteSlideMatch.Boxes(before))) continue;
                Add(new NotePendingUpdate
                {
                    Id = $"words:{after.Id}", Kind = NotePendingUpdates.Words, GroupId = mark.Id, SlideId = after.Id,
                    Before = NoteSlideMatch.Boxes(before), After = words, Proposed = after,
                }, mark.Text, found);
            }
            var previous = made[^1].Id;
            for (var index = 0; index < appended.Count; index++)
            {
                Add(new NotePendingUpdate
                {
                    Id = $"new:{mark.Id}:{index}", Kind = NotePendingUpdates.New, GroupId = mark.Id,
                    AfterSlideId = previous, Proposed = appended[index],
                }, mark.Text, found);
                previous = appended[index].Id;
            }
            foreach (var id in result.RemovedIds)
            {
                if (!inDeck.TryGetValue(id, out var slide)) continue;
                Add(new NotePendingUpdate
                {
                    Id = $"remove:{id}", Kind = NotePendingUpdates.Remove, GroupId = mark.Id, SlideId = id,
                    Before = NoteSlideMatch.Boxes(slide),
                }, mark.Text, found);
            }
        }

        // A made passage the note no longer has: its slides may go.
        foreach (var passage in live.Where(p => !byId.ContainsKey(p.GroupId)))
        {
            foreach (var slide in SlidesOf(passage))
            {
                Add(new NotePendingUpdate
                {
                    Id = $"remove:{slide.Id}", Kind = NotePendingUpdates.Remove, GroupId = passage.GroupId, SlideId = slide.Id,
                    Before = NoteSlideMatch.Boxes(slide),
                }, "", found);
            }
        }

        // New passages: marked by hand since, or slide-like words the note's build mode would pick.
        var fresh = body;
        var newGroups = marks.Where(m => !memoryById.ContainsKey(m.Id)).Select(m => m.Id).ToList();
        foreach (var candidate in Candidates(body, mode))
        {
            fresh = Marking(candidate, fresh);
            newGroups.Add(candidate.Id);
        }
        if (newGroups.Count > 0)
        {
            var freshMarks = NoteSlideMarks.Groups(fresh);
            var planned = NewSlides(NoteSlideBuilder.Groups(fresh).Where(g => newGroups.Contains(g.Id)).ToList(),
                deck.ThemeId, designMap, theme, mediaIds: mediaIds);
            foreach (var mark in freshMarks.Where(m => newGroups.Contains(m.Id)))
            {
                var after = LastSlideIdBefore(mark.Id, freshMarks, memoryById, deck);
                var own = planned.Slides.Where(s => planned.Group(s.Id) == mark.Id).ToList();
                for (var index = 0; index < own.Count; index++)
                {
                    Add(new NotePendingUpdate
                    {
                        Id = $"new:{mark.Id}:{index}", Kind = NotePendingUpdates.New, GroupId = mark.Id,
                        AfterSlideId = after, Proposed = own[index],
                    }, mark.Text, found);
                    after = own[index].Id;
                }
            }
        }
        // Updates someone skipped stay skipped while their passage reads the same.
        found.Updates.RemoveAll(update => skipped.Any(s => s.Id == update.Id && s.Text == found.Texts.GetValueOrDefault(update.Id)));
        return found;
    }

    private static void Add(NotePendingUpdate update, string text, Found found)
    {
        found.Updates.Add(update);
        found.Texts[update.Id] = text;
    }

    /// <summary>A passage the note's build mode would pick that isn't marked yet, with a stable id from its words.</summary>
    internal sealed record Candidate(string Id, IReadOnlyList<int> Blocks, string? Type);

    internal static List<Candidate> Candidates(NoteBody body, string? mode)
    {
        if (mode is null || NoteSlideMarks.ParseMode(mode) is not { } suggest) return [];
        var held = Enumerable.Range(0, body.Blocks.Count).Where(i => IsMarked(body.Blocks[i])).ToHashSet();
        var bare = Unmarked(body);
        var suggested = NoteSlideMarks.Suggestions(bare, suggest).Concat(NoteSlideMarks.StrongSuggestionsOutsideMarkers(bare))
            .Select((s, order) => (s, order)).OrderBy(p => p.s.Blocks[0]).ThenBy(p => p.order).Select(p => p.s);
        return suggested.Select(passage =>
        {
            var blocks = passage.Blocks.Where(b => !held.Contains(b)).ToList();
            if (blocks.Count == 0) return null;
            var words = string.Join("\n", blocks.Select(b => NoteReading.Text(body.Blocks[b])));
            var key = string.Join("\n", blocks.Select(b => NoteMedia.Key(body.Blocks[b])).OfType<string>());
            // Part of the passage is marked already: guess the type of what's left.
            var type = blocks.Count == passage.Blocks.Count
                ? passage.Type
                : NoteSlideMarks.GuessedType(words, NoteSlideMarks.ShapeOf(body.Blocks[blocks[0]]));
            return new Candidate("add-" + Fingerprint(NoteSlideMatch.Normalized(words) + key), blocks, type);
        }).OfType<Candidate>().ToList();
    }

    internal static NoteBody Marking(Candidate candidate, NoteBody body)
    {
        var marked = body.Copy();
        foreach (var index in candidate.Blocks) marked.Blocks[index] = NoteSlideMarks.Marking(marked.Blocks[index], candidate.Id, candidate.Type);
        return marked;
    }

    /// <summary>FNV-1a, 64 bits, as hex: the same words give the same id on any computer.</summary>
    internal static string Fingerprint(string text)
    {
        var hash = 0xcbf29ce484222325UL;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash = unchecked(hash * 0x100000001b3UL);
        }
        return hash.ToString("x", CultureInfo.InvariantCulture);
    }

    /// <summary>The last made slide of the passages before this one: where its new slides go.</summary>
    internal static string? LastSlideIdBefore(string groupId, IReadOnlyList<NoteSlideMarks.Group> marks, IReadOnlyDictionary<string, NoteMadePassage> memory, Presentation deck)
    {
        var index = marks.ToList().FindIndex(m => m.Id == groupId);
        if (index < 0) return null;
        for (var at = index - 1; at >= 0; at--)
        {
            var ids = (memory.GetValueOrDefault(marks[at].Id)?.Slides ?? []).Select(s => s.SlideId).ToHashSet();
            if (deck.Slides.LastOrDefault(s => ids.Contains(s.Id)) is { } last) return last.Id;
        }
        return null;
    }

    /// <summary>
    /// The slides for passages, each in the design (and theme) the design map gives its type,
    /// with blanks, switches and the designs' own actions.
    /// </summary>
    /// <param name="overrides">What someone chose for a passage in Make Slides (its kind, design or lines a slide), over the plan and the design map.</param>
    /// <param name="switches">A passage's switch chosen in Make Slides, over its type's.</param>
    public static NoteSlideBuilder.Built NewSlides(
        IReadOnlyList<MarkedGroup> groups, string deckThemeId, SlideDesignMap designMap, Func<string, Theme?> theme,
        ISlidePlanner? planner = null, IReadOnlyDictionary<string, string>? mediaIds = null,
        IReadOnlyDictionary<string, SlideRowOverride>? overrides = null,
        IReadOnlyDictionary<string, NoteSlideBuilder.SwitchOverride>? switches = null)
    {
        planner ??= new DeterministicSlidePlanner(designMap.Options);
        var rows = new List<SlideRow>();
        var rowThemeIds = new Dictionary<string, string>();
        var blanks = new Dictionary<string, BlankPlacement>();
        foreach (var group in groups)
        {
            var rowThemeId = deckThemeId;
            var chosen = overrides?.GetValueOrDefault(group.Id);
            string? design = chosen?.ThemeSlideName;
            // Planned once to learn its type, then again in that type's design (unless one was chosen).
            var typed = new Dictionary<string, SlideRowOverride>();
            if (chosen?.Kind is { } kind) typed[group.Id] = new SlideRowOverride(kind);
            if (design is null
                && planner.Plan([group], theme(rowThemeId), typed).Rows.FirstOrDefault() is { } first
                && designMap.EntryForType(first.TypeKey) is { } entry && theme(entry.ThemeId) is not null)
            {
                rowThemeId = entry.ThemeId;
                design = entry.ThemeSlideName;
            }
            var rowOverrides = new Dictionary<string, SlideRowOverride> { [group.Id] = new(chosen?.Kind, design, chosen?.LinesPerSlide) };
            if (planner.Plan([group], theme(rowThemeId), rowOverrides).Rows.FirstOrDefault() is not { } row) continue;
            rows.Add(row);
            if (rowThemeId != deckThemeId && rowThemeId.Length > 0) rowThemeIds[group.Id] = rowThemeId;
            if (designMap.Blanks != BlankPlacement.None) blanks[group.Id] = designMap.Blanks;
        }
        if (rows.Count == 0) return new NoteSlideBuilder.Built();
        var built = NoteSlideBuilder.Build(new SlidePlan(rows), blanks, NoteSlides.BlankStyle(designMap, deckThemeId, theme),
            designMap.Options, key => mediaIds?.GetValueOrDefault(key));
        NoteSlideBuilder.AttachingSwitches(built, rows, designMap, switches ?? new Dictionary<string, NoteSlideBuilder.SwitchOverride>());
        foreach (var slide in built.Slides)
        {
            if (built.GroupBySlideId.TryGetValue(slide.Id, out var group) && rowThemeIds.TryGetValue(group, out var own)) slide.ThemeId = own;
        }
        NoteSlideBuilder.AttachingDesignActions(built, deckThemeId, theme);
        return built;
    }

    /// <summary>The deck, memory and note after one update is applied.</summary>
    public sealed record Applied(Presentation Deck, List<NoteMadePassage> Memory, NoteBody? Body);

    public static Applied Applying(NotePendingUpdate update, Presentation deck, IReadOnlyList<NoteMadePassage> memory, NoteBody body, string? mode, string now)
    {
        var newDeck = MxuJson.Clone(deck);
        var newMemory = memory.Select(MxuJson.Clone).ToList();
        NoteBody? newBody = null;
        body = Filled(body);
        switch (update.Kind)
        {
            case NotePendingUpdates.Words:
            {
                if (update.SlideId is not { } slideId || update.After is not { } after) break;
                var index = deck.Slides.FindIndex(s => s.Id == slideId);
                if (index < 0 || deck.Slides[index].KeepWords == true) break;
                var current = deck.Slides[index];
                var next = NoteSlideUpdate.Applying(after, current);
                // The slide's old words go into its history, so the update can be undone.
                if (!MxuJson.StructurallyEqual(next, current))
                    newDeck.Slides[index] = SlideHistory.Pushing(current, next, NoteSlides.UpdatedReason, now);
                Remember(slideId, after, update.GroupId, body, newMemory);
                break;
            }
            case NotePendingUpdates.Remove or NotePendingUpdates.Orphan:
            {
                if (update.SlideId is not { } slideId) break;
                newDeck.Slides.RemoveAll(s => s.Id == slideId);
                foreach (var passage in newMemory) passage.Slides.RemoveAll(s => s.SlideId == slideId);
                var marked = NoteSlideMarks.Groups(body).Select(g => g.Id).ToHashSet();
                newMemory.RemoveAll(p => p.Slides.Count == 0 && !marked.Contains(p.GroupId));
                break;
            }
            case NotePendingUpdates.New:
            {
                if (update.Proposed is not { } proposed || deck.Slides.Any(s => s.Id == proposed.Id)) break;
                var marked = body;
                if (!NoteSlideMarks.Groups(body).Any(g => g.Id == update.GroupId))
                {
                    // An Add for words the note hasn't marked yet: they're marked now.
                    if (Candidates(body, mode).FirstOrDefault(c => c.Id == update.GroupId) is not { } candidate) break;
                    marked = Marking(candidate, body);
                    newBody = marked;
                }
                var own = (memory.FirstOrDefault(p => p.GroupId == update.GroupId)?.Slides ?? []).Select(s => s.SlideId).ToHashSet();
                int? at = update.AfterSlideId is { } afterId && deck.Slides.FindIndex(s => s.Id == afterId) is var found and >= 0 ? found : null;
                at ??= deck.Slides.FindLastIndex(s => own.Contains(s.Id)) is var lastOwn and >= 0 ? lastOwn : null;
                newDeck.Slides.Insert(at is { } i ? i + 1 : 0, MxuJson.Clone(proposed));
                Remember(proposed.Id, NoteSlideMatch.Boxes(proposed), update.GroupId, marked, newMemory, newDeck);
                break;
            }
        }
        return new Applied(newDeck, newMemory, newBody);
    }

    private static void Remember(string slideId, List<NoteMadeBox> boxes, string groupId, NoteBody body, List<NoteMadePassage> memory, Presentation? deck = null)
    {
        var mark = NoteSlideMarks.Groups(body).FirstOrDefault(g => g.Id == groupId);
        var index = memory.FindIndex(p => p.GroupId == groupId);
        if (index >= 0)
        {
            var slide = memory[index].Slides.FindIndex(s => s.SlideId == slideId);
            if (slide >= 0) memory[index].Slides[slide].Boxes = boxes;
            else memory[index].Slides.Add(new NoteMadeSlide { SlideId = slideId, Boxes = boxes });
            if (deck is not null)
            {
                var order = new Dictionary<string, int>();
                for (var i = 0; i < deck.Slides.Count; i++) order.TryAdd(deck.Slides[i].Id, i);
                memory[index].Slides = memory[index].Slides.OrderBy(s => order.GetValueOrDefault(s.SlideId, int.MaxValue)).ToList();
            }
        }
        else
        {
            var made = deck?.Slides.FirstOrDefault(s => s.Id == slideId);
            var passage = new NoteMadePassage
            {
                GroupId = groupId, Text = mark?.Text ?? "", ThemeSlideName = made?.ThemeSlideName, ThemeId = made?.ThemeId,
                Slides = [new NoteMadeSlide { SlideId = slideId, Boxes = boxes }],
            };
            // In the note's order of passages.
            var marks = NoteSlideMarks.Groups(body).Select(g => g.Id).ToList();
            int Position(string id) => marks.IndexOf(id) is var p and >= 0 ? p : marks.Count;
            var position = Position(groupId);
            var at = memory.FindIndex(p => Position(p.GroupId) > position);
            memory.Insert(at >= 0 ? at : memory.Count, passage);
        }
    }

    /// <summary>The memory's passage words brought up to the note's, except passages with updates still waiting.</summary>
    public static List<NoteMadePassage> SettlingTexts(IReadOnlyList<NoteMadePassage> memory, NoteBody body, IReadOnlyList<NotePendingUpdate> waiting)
    {
        var marks = new Dictionary<string, string>();
        foreach (var group in NoteSlideMarks.Groups(Filled(body))) marks.TryAdd(group.Id, group.Text);
        var busy = waiting.Select(w => w.GroupId).ToHashSet();
        return memory.Select(original =>
        {
            var passage = MxuJson.Clone(original);
            if (!busy.Contains(passage.GroupId) && marks.TryGetValue(passage.GroupId, out var text)) passage.Text = text;
            return passage;
        }).ToList();
    }

    /// <summary>The updates found now, to skip: a note opened on a deck someone already edited by hand starts from here.</summary>
    public static List<NoteSkippedUpdate> Baseline(Found found) =>
        found.Updates.Select(u => new NoteSkippedUpdate { Id = u.Id, Text = found.Texts.GetValueOrDefault(u.Id) ?? "" }).ToList();

    public static List<NoteSkippedUpdate> Skipping(string id, string text, IReadOnlyList<NoteSkippedUpdate> skipped) =>
        [.. skipped.Where(s => s.Id != id), new NoteSkippedUpdate { Id = id, Text = text }];

    /// <summary>The note's memory right after Make Slides: each passage, its slides and their words.</summary>
    public static List<NoteMadePassage> Memory(NoteBody body, Presentation deck, IReadOnlyDictionary<string, string> groupBySlideId) =>
        NoteSlideMarks.Groups(body).Select(group =>
        {
            var slides = deck.Slides.Where(s => groupBySlideId.GetValueOrDefault(s.Id) == group.Id).ToList();
            if (slides.Count == 0) return null;
            return new NoteMadePassage
            {
                GroupId = group.Id, Text = group.Text, ThemeSlideName = slides[0].ThemeSlideName, ThemeId = slides[0].ThemeId,
                Slides = slides.Select(s => new NoteMadeSlide { SlideId = s.Id, Boxes = NoteSlideMatch.Boxes(s) }).ToList(),
            };
        }).OfType<NoteMadePassage>().ToList();

    /// <summary>
    /// The note as it's shown beside its deck: the made slides' cards after their passages (the
    /// saved note doesn't keep them; they're drawn from the memory), each passage stamped with its first slide.
    /// </summary>
    public static NoteBody DisplayBody(NoteBody body, IReadOnlyList<NoteMadePassage> memory, Presentation? deck)
    {
        if (deck is null) return body;
        var shown = SavedBody(body, deck.Id);
        if (memory.Count == 0) return shown;
        var byGroup = new Dictionary<string, HashSet<string>>();
        foreach (var passage in memory) byGroup.TryAdd(passage.GroupId, passage.Slides.Select(s => s.SlideId).ToHashSet());
        var marks = NoteSlideMarks.Groups(shown);
        for (var m = marks.Count - 1; m >= 0; m--)
        {
            var mark = marks[m];
            if (!byGroup.TryGetValue(mark.Id, out var ids) || mark.BlockIndexes.Count == 0) continue;
            var slides = deck.Slides.Where(s => ids.Contains(s.Id)).ToList();
            if (slides.Count == 0) continue;
            var cards = slides.Select(s => NoteSlides.DropRef(deck.Id, s.Id, _ => deck)).OfType<NoteSlideRef>()
                .Select(r => new NoteBlock { Type = NoteBlockType.SlideRef, Slide = r }).ToList();
            var at = mark.BlockIndexes[^1] + 1;
            while (at < shown.Blocks.Count && shown.Blocks[at].Type == NoteBlockType.SlideRef) at++;
            shown.Blocks.InsertRange(at, cards);
            shown = NoteSlideMarks.Stamping(slides[0].Id, mark.Id, shown);
        }
        shown.PlainText = NoteReading.PlainText(shown.Blocks);
        return shown;
    }

    /// <summary>The note as it's saved: no cards for the made slides, no slide stamps on its runs.</summary>
    public static NoteBody SavedBody(NoteBody shown, string? deckId)
    {
        var saved = deckId is null ? shown.Copy() : StrippingMadeCards(shown, deckId);
        if (saved.Blocks.Any(HasSlideId)) saved = new NoteBody { Blocks = saved.Blocks.Select(Unstamped).ToList(), PlainText = saved.PlainText };
        return saved;
    }

    private static bool HasSlideId(NoteBlock block) =>
        block.SlideId is not null || block.MarkRun?.SlideId is not null
        || NoteSlideMarks.Lines(block).SelectMany(l => l).Any(r => r.SlideId is not null)
        || (block.Blocks ?? []).Any(HasSlideId);

    private static NoteBlock Unstamped(NoteBlock block)
    {
        if (block.Type == NoteBlockType.SlideRef) return block.Copy();
        return NoteSlideMarks.MappingRuns(block, run =>
        {
            var clear = run.Copy();
            clear.SlideId = null;
            return clear;
        }, b => b.SlideId = null);
    }

    public static NoteBody StrippingMadeCards(NoteBody body, string deckId)
    {
        if (!body.Blocks.Any(b => IsMadeCard(b, deckId))) return body.Copy();
        var saved = body.Copy();
        saved.Blocks.RemoveAll(b => IsMadeCard(b, deckId));
        saved.PlainText = NoteReading.PlainText(saved.Blocks);
        return saved;
    }

    internal static bool IsMadeCard(NoteBlock block, string deckId) =>
        block.Type == NoteBlockType.SlideRef && block.Slide?.PresentationId == deckId && block.Slide?.PlacedByHand != true;

    public static List<(string GroupId, List<string> SlideIds)> Passages(IReadOnlyList<NoteMadePassage> memory) =>
        memory.Select(p => (p.GroupId, p.Slides.Select(s => s.SlideId).ToList())).ToList();

    /// <summary>A cell in the deck's grid beside the note: a slide (by index), or an Add waiting there.</summary>
    public abstract record Cell
    {
        public sealed record SlideCell(int Index) : Cell;

        public sealed record AddCell(string UpdateId) : Cell;
    }

    /// <summary>The deck's slides with each waiting Add placed after the slide it follows.</summary>
    public static List<Cell> Cells(IReadOnlyList<string> slideIds, IReadOnlyList<NotePendingUpdate> updates)
    {
        var adds = updates.Where(u => u.Kind == NotePendingUpdates.New).ToList();
        var placed = new HashSet<string>();
        List<Cell> After(string? id)
        {
            var output = new List<Cell>();
            foreach (var add in adds)
            {
                if (add.AfterSlideId != id || placed.Contains(add.Id)) continue;
                placed.Add(add.Id);
                output.Add(new Cell.AddCell(add.Id));
                // Adds that follow this Add come right after it.
                if (add.Proposed is { } proposed) output.AddRange(After(proposed.Id));
            }
            return output;
        }
        var cells = After(null);
        var seen = new HashSet<string>();
        for (var index = 0; index < slideIds.Count; index++)
        {
            cells.Add(new Cell.SlideCell(index));
            if (seen.Add(slideIds[index])) cells.AddRange(After(slideIds[index]));
        }
        cells.AddRange(adds.Where(a => !placed.Contains(a.Id)).Select(a => (Cell)new Cell.AddCell(a.Id)));
        return cells;
    }

    public enum Change { Kept, Removed, Added }

    /// <summary>Word by word, what an update keeps, takes out and puts in (the longest words in common kept).</summary>
    public static List<(string Text, Change Change)> WordDiff(string before, string after)
    {
        var a = before.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var b = after.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var table = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
        {
            for (var j = b.Length - 1; j >= 0; j--)
                table[i, j] = a[i] == b[j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
        }
        var output = new List<(string Text, Change Change)>();
        void Push(string word, Change change)
        {
            if (output.Count > 0 && output[^1].Change == change) output[^1] = (output[^1].Text + " " + word, change);
            else output.Add((word, change));
        }
        int x = 0, y = 0;
        while (x < a.Length || y < b.Length)
        {
            if (x < a.Length && y < b.Length && a[x] == b[y])
            {
                Push(a[x], Change.Kept);
                x++;
                y++;
            }
            else if (x < a.Length && (y == b.Length || table[x + 1, y] >= table[x, y + 1]))
            {
                Push(a[x], Change.Removed);
                x++;
            }
            else
            {
                Push(b[y], Change.Added);
                y++;
            }
        }
        return output;
    }

    /// <summary>The note with every mark taken off, as the suggester sees it fresh.</summary>
    internal static NoteBody Unmarked(NoteBody body) =>
        new()
        {
            Blocks = body.Blocks.Select(block => NoteSlideMarks.MappingRuns(block, run =>
            {
                var clear = run.Copy();
                clear.SlideGroup = null;
                clear.SlideType = null;
                clear.SlideId = null;
                clear.SlideState = null;
                return clear;
            }, b =>
            {
                b.SlideGroup = null;
                b.SlideType = null;
                b.SlideId = null;
                b.SlideState = null;
            })).ToList(),
            PlainText = body.PlainText,
        };

    internal static bool IsMarked(NoteBlock block) =>
        block.SlideGroup is not null || block.MarkRun?.SlideGroup is not null
        || NoteSlideMarks.Lines(block).SelectMany(l => l).Any(r => r.SlideGroup is not null);

    /// <summary>
    /// A block partly in a passage (the mark stopped short of the line's end) is marked whole, so
    /// words typed at the end of a marked line join its passage.
    /// </summary>
    internal static NoteBody Filled(NoteBody body)
    {
        var filled = body.Copy();
        for (var index = 0; index < filled.Blocks.Count; index++)
        {
            var block = filled.Blocks[index];
            var runs = NoteSlideMarks.Lines(block).SelectMany(l => l).ToList();
            var marked = runs.Where(r => r.SlideGroup is not null).ToList();
            if (marked.Count == 0 || marked[0].SlideGroup is not { } group || marked[0].SlideState == NoteSlideMarks.Declined
                || !marked.All(r => r.SlideGroup == group)
                || !runs.Any(r => r.SlideGroup is null && Lyrics.SwiftString.TrimmingWhitespaces(r.Text).Length > 0)) continue;
            var type = marked.Select(r => r.SlideType).FirstOrDefault(t => t is not null);
            filled.Blocks[index] = NoteSlideMarks.Marking(block, group, type, marked[0].SlideState);
        }
        return filled;
    }
}
