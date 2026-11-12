using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Notes;

/// <summary>
/// Bringing made slides up to date with their passage: the cards after a passage, which made
/// slides still exist, re-planning a passage like its slides were made, and the new words for
/// each slide (with slides added or taken away when the passage grew or shrank). Port of
/// NoteSlideUpdate.swift.
/// </summary>
public static class NoteSlideUpdate
{
    public static List<NoteSlideRef> CardsAfterGroup(string id, NoteBody body) =>
        NoteSlideMarks.Groups(body).FirstOrDefault(g => g.Id == id) is { } group ? CardsAfter(group, body) : [];

    /// <summary>The cards Make Slides put beside a passage (not ones placed by hand).</summary>
    public static List<NoteSlideRef> CardsAfter(NoteSlideMarks.Group group, NoteBody body) =>
        CardRange(group, body) is { } range
            ? Enumerable.Range(range.Start, range.End - range.Start).Select(i => body.Blocks[i].Slide)
                .OfType<NoteSlideRef>().Where(s => s.PlacedByHand != true).ToList()
            : [];

    /// <summary>Where a passage's made slides are in the deck (1-based), and which of them are gone.</summary>
    public sealed class MadeSlides
    {
        public Dictionary<string, int> Positions { get; } = [];
        public HashSet<string> Dead { get; } = [];
        public bool IsMade => Positions.Count > 0;
    }

    public static MadeSlides MadeSlidesOf(IEnumerable<string> ids, string? deckId, Func<string, Presentation?> lookup)
    {
        var made = new MadeSlides();
        var deck = deckId is null ? null : lookup(deckId);
        foreach (var id in ids)
        {
            var index = deck?.Slides.FindIndex(s => s.Id == id) ?? -1;
            if (index >= 0) made.Positions[id] = index + 1;
            else made.Dead.Add(id);
        }
        return made;
    }

    internal static (int Start, int End)? CardRangeOfGroup(string id, NoteBody body) =>
        NoteSlideMarks.Groups(body).FirstOrDefault(g => g.Id == id) is { } group ? CardRange(group, body) : null;

    /// <summary>
    /// The run of cards beside a passage: after it, or before it (where a marker was), preferring
    /// the run that holds the slide the passage is stamped with. End is exclusive.
    /// </summary>
    internal static (int Start, int End)? CardRange(NoteSlideMarks.Group group, NoteBody body)
    {
        if (group.BlockIndexes.Count == 0) return null;
        var first = group.BlockIndexes[0];
        var last = group.BlockIndexes[^1];
        var end = last + 1;
        while (end < body.Blocks.Count && body.Blocks[end].Type == NoteBlockType.SlideRef) end++;
        (int, int)? after = end > last + 1 ? (last + 1, end) : null;
        var start = first;
        while (start > 0 && body.Blocks[start - 1].Type == NoteBlockType.SlideRef) start--;
        (int, int)? before = start < first ? (start, first) : null;
        if (group.SlideId is { } stamped)
        {
            foreach (var run in new[] { before, after })
            {
                if (run is { } r && Enumerable.Range(r.Item1, r.Item2 - r.Item1).Any(i => body.Blocks[i].Slide?.SlideId == stamped)) return r;
            }
        }
        return after ?? before;
    }

    /// <summary>The passage planned again in the design its slides use.</summary>
    public static SlideRow? Replan(MarkedGroup group, IReadOnlyList<Slide> slides, Theme? theme, SlideOptions? options = null,
        Func<Scene.StyledText, Scene.SceneSize, bool>? overflows = null)
    {
        var @override = new SlideRowOverride(ThemeSlideName: slides.FirstOrDefault()?.ThemeSlideName);
        return new DeterministicSlidePlanner(options ?? new SlideOptions(), Overflows: overflows)
            .Plan([group], theme, new Dictionary<string, SlideRowOverride> { [group.Id] = @override }).Rows.FirstOrDefault();
    }

    /// <summary>The passage's words have moved on from its slides'.</summary>
    public static bool IsBehind(MarkedGroup group, IReadOnlyList<Slide> slides, Theme? theme, SlideOptions? options = null)
    {
        if (Replan(group, slides, theme, options) is not { } row || row.HoldsMedia) return false;
        return !Same(Texts(row), slides.Select(Boxes).ToList());
    }

    /// <summary>The slides brought up to date: their new words, slides to add after them, and slides to take away.</summary>
    public sealed record Result(List<Slide> Updated, List<Slide> Appended, List<string> RemovedIds);

    public static Result Updating(IReadOnlyList<Slide> slides, SlideRow row)
    {
        if (row.HoldsMedia)
        {
            // A picture or video slide keeps its media; only its Scale follows the settings.
            return new Result(slides.Select(original =>
            {
                var slide = MxuJson.Clone(original);
                var scale = row.Scale == MediaScaleMode.Fill ? null : row.Scale;
                if (row.Kind == SlideKind.Picture && slide.Background is { } background) background.ScaleMode = scale;
                if (row.Kind == SlideKind.Picture)
                {
                    var spotScale = row.ScaleChosen ? scale : scale?.InPhotoSpot();
                    foreach (var o in slide.Objects)
                    {
                        if (!PhotoSpots.IsPhotoSpot(o) || o.Fill is not { FillKind: FillKind.Media } fill) continue;
                        // A tall or wide picture keeps the Scale and Blur it was given.
                        if (!row.ScaleChosen && (spotScale ?? MediaScaleMode.Fill) == MediaScaleMode.Fill && fill.MediaScaleMode == MediaScaleMode.ScaleAndBlur) continue;
                        fill.MediaScaleMode = spotScale == MediaScaleMode.Fill ? null : spotScale;
                    }
                }
                return slide;
            }).ToList(), [], []);
        }
        var parts = Texts(row);
        var updated = new List<Slide>();
        var appended = new List<Slide>();
        var removed = new List<string>();
        for (var index = 0; index < slides.Count; index++)
        {
            var slide = slides[index];
            if (index >= parts.Count)
            {
                // A slide the passage no longer fills goes, unless it holds more than words.
                if (slide.Objects.All(o => o.ObjectKind == SlideObjectKind.Text)) removed.Add(slide.Id);
                else updated.Add(slide);
                continue;
            }
            // The passage now plans another design: the slide takes its boxes.
            if (row.Lyrics is null && row.ThemeSlideName is { } design && slide.ThemeSlideName is { } was && design != was)
            {
                var redesigned = MxuJson.Clone(slide);
                redesigned.Objects = [.. redesigned.Objects.Where(o => o.ObjectKind != SlideObjectKind.Text), .. row.Parts[index].Select(NoteSlideBuilder.TextObject)];
                redesigned.ThemeSlideName = design;
                updated.Add(redesigned);
                continue;
            }
            updated.Add(Applying(parts[index], slide, row.Lyrics is null ? row.Parts[index] : null));
        }
        for (var index = slides.Count; index < parts.Count; index++)
        {
            var objects = row.Lyrics is null
                ? row.Parts[index].Select(NoteSlideBuilder.TextObject).ToList()
                : parts[index].Select(p => new SlideObject { Id = SwiftString.Uuid(), ObjectKind = SlideObjectKind.Text, Name = p.Name, Text = p.Text }).ToList();
            var slide = new Slide { Id = SwiftString.Uuid(), Name = "", Objects = objects, ThemeSlideName = row.ThemeSlideName, ThemeId = slides.FirstOrDefault()?.ThemeId };
            // A new slide fires what its passage's first slide fires.
            slide.Actions = slides.FirstOrDefault()?.Actions?.Select(a =>
            {
                var copy = MxuJson.Clone(a);
                copy.Id = SwiftString.Uuid();
                return copy;
            }).ToList();
            appended.Add(slide);
        }
        return new Result(updated, appended, removed);
    }

    /// <summary>Each slide fires <paramref name="choice"/> as its output preset switch (and no other).</summary>
    public static List<Slide> ApplyingSwitch(SlidePresetChoice? choice, IEnumerable<Slide> slides) => slides.Select(original =>
    {
        var slide = MxuJson.Clone(original);
        var actions = (slide.Actions ?? []).Where(a => a.Kind != SlideActionKind.SwitchOutputPreset).ToList();
        if (choice is not null && !actions.Any(choice.Matches)) actions.Add(choice.Action());
        slide.Actions = actions.Count == 0 ? null : actions;
        return slide;
    }).ToList();

    public static Slide Applying(IReadOnlyList<NoteMadeBox> boxes, Slide slide) => Applying(boxes.Select(b => (b.Name, b.Text)).ToList(), slide, null);

    private static Slide Applying(IReadOnlyList<(string Name, string Text)> part, Slide original, IReadOnlyList<SlideBox>? boxes)
    {
        var slide = MxuJson.Clone(original);
        var remaining = part.ToList();
        foreach (var o in slide.Objects)
        {
            if (o.ObjectKind != SlideObjectKind.Text) continue;
            var match = remaining.FindIndex(p => string.Equals(p.Name, o.Name, StringComparison.OrdinalIgnoreCase));
            if (match < 0) continue;
            // New words take the planned underlines; unchanged words keep the slide's own styling.
            if (boxes is not null && o.Text != remaining[match].Text)
                o.StyleRuns = boxes.FirstOrDefault(b => b.Name == remaining[match].Name)?.StyleRuns?.Select(MxuJson.Clone).ToList();
            o.Text = remaining[match].Text;
            remaining.RemoveAt(match);
        }
        foreach (var box in remaining)
            slide.Objects.Add(new SlideObject { Id = SwiftString.Uuid(), ObjectKind = SlideObjectKind.Text, Name = box.Name, Text = box.Text });
        return slide;
    }

    /// <summary>The cards after a passage, with new ones added and gone ones taken away.</summary>
    public static NoteBody SyncingCards(string id, IReadOnlyList<NoteSlideRef> refs, IReadOnlySet<string> ids, NoteBody body)
    {
        if (NoteSlideMarks.Groups(body).FirstOrDefault(g => g.Id == id) is not { BlockIndexes.Count: > 0 } group) return body;
        var synced = body.Copy();
        var last = group.BlockIndexes[^1];
        int end;
        if (CardRange(group, synced) is { } range)
        {
            // After the last card Make Slides placed, before any placed by hand.
            var lastMade = Enumerable.Range(range.Start, range.End - range.Start).LastOrDefault(i => synced.Blocks[i].Slide?.PlacedByHand != true, -1);
            end = lastMade >= 0 ? lastMade + 1 : range.End;
        }
        else
        {
            end = last + 1;
        }
        synced.Blocks.InsertRange(end, refs.Select(r => new NoteBlock { Type = NoteBlockType.SlideRef, Slide = r.Copy() }));
        synced.Blocks.RemoveAll(b => b.Type == NoteBlockType.SlideRef && b.Slide?.SlideId is { } slideId && ids.Contains(slideId));
        synced.PlainText = NoteReading.PlainText(synced.Blocks);
        return synced;
    }

    /// <summary>Each planned slide's boxes: (name, words).</summary>
    internal static List<List<(string Name, string Text)>> Texts(SlideRow row)
    {
        if (row.Lyrics is { } lyrics)
            return Reflow.Build(lyrics, row.ThemeSlideName ?? "Lyrics").Slides
                .Select(s => s.Objects.Where(o => o.ObjectKind == SlideObjectKind.Text).Select(o => (o.Name, o.Text)).ToList()).ToList();
        return row.Parts.Select(p => p.Select(b => (b.Name, b.Text)).ToList()).ToList();
    }

    internal static List<(string Name, string Text)> Boxes(Slide slide) =>
        slide.Objects.Where(o => o.ObjectKind == SlideObjectKind.Text).Select(o => (o.Name, o.Text)).ToList();

    /// <summary>The same slides' words, box for box, whatever order the boxes are in.</summary>
    private static bool Same(List<List<(string Name, string Text)>> a, List<List<(string Name, string Text)>> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Count != b[i].Count) return false;
            var x = a[i].OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
            var y = b[i].OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
            for (var j = 0; j < x.Count; j++)
            {
                if (x[j].Name != y[j].Name || x[j].Text != y[j].Text) return false;
            }
        }
        return true;
    }
}
