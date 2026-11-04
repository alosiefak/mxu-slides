using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Notes;

public static partial class NoteSlides
{
    /// <summary>What Make Slides made from a note: the note as marked, the new slides, and which passage each came from.</summary>
    public sealed class Made
    {
        public required NoteBody Body { get; init; }
        public List<Slide> NewSlides { get; init; } = [];
        public List<PresentationSection> Sections { get; init; } = [];
        public Arrangement? Arrangement { get; init; }
        public Dictionary<string, string> GroupBySlideId { get; init; } = [];
        public List<string> GroupOrder { get; init; } = [];

        /// <summary>True when the note had no marks and was marked by its structure.</summary>
        public bool? MarkedByStructure { get; init; }
    }

    /// <summary>The reason an updated slide's history gives.</summary>
    public const string UpdatedReason = "Updated from the notes";

    /// <summary>
    /// Makes the slides for a note in one step (the Mac's "Make Slides" with no sheet): its
    /// suggestions accepted, or, with no marks at all, marked by its structure first.
    /// </summary>
    public static Made Make(
        NoteBody body, string themeId, SlideDesignMap designMap, Func<string, Theme?> theme,
        ISlidePlanner? planner = null, IReadOnlyDictionary<string, string>? mediaIds = null)
    {
        planner ??= new DeterministicSlidePlanner(designMap.Options);
        bool? markedByStructure = null;
        if (NoteSlideMarks.Groups(body).Any(g => g.State == NoteSlideMarks.Suggested))
        {
            body = NoteSlideMarks.AcceptingSuggestions(body);
        }
        else if (NoteSlideMarks.Groups(body).Count == 0)
        {
            body = MarkingByStructure(body, designMap.Options.OwnTypes.Select(t => t.Name).ToList());
            markedByStructure = true;
        }
        var groups = NoteSlideBuilder.Groups(body);
        var built = NoteLiveUpdates.NewSlides(groups, themeId, designMap, theme, planner, mediaIds);
        return new Made
        {
            Body = body, NewSlides = built.Slides, Sections = built.Sections, Arrangement = built.Arrangement,
            GroupBySlideId = built.GroupBySlideId, GroupOrder = groups.Select(g => g.Id).ToList(), MarkedByStructure = markedByStructure,
        };
    }

    public static NoteBody MarkingByStructure(NoteBody body, IReadOnlyList<string> ownTypes) =>
        NoteSlideMarks.AcceptingSuggestions(NoteSlideMarks.Suggesting(body, NoteSlideMarks.SuggestMode.Structure, ownTypes));

    /// <summary>How blank slides look: the design map's Blank design (in its theme) and switch, else the deck theme's blank design.</summary>
    public static NoteSlideBuilder.BlankStyle BlankStyle(SlideDesignMap map, string deckThemeId, Func<string, Theme?> theme)
    {
        var actions = map.PresetFor(SlideKind.Blank) is { } preset ? new[] { preset.Action() } : [];
        if (map.EntryFor(SlideKind.Blank) is { } entry && theme(entry.ThemeId) is not null)
            return new NoteSlideBuilder.BlankStyle(entry.ThemeId == deckThemeId ? null : entry.ThemeId, entry.ThemeSlideName, actions);
        return new NoteSlideBuilder.BlankStyle(null, NoteSlideBuilder.DesignName(SlideKind.Blank, theme(deckThemeId)), actions);
    }
}

/// <summary>The kinds of pending update a note can hold for its deck.</summary>
public static class NotePendingUpdates
{
    /// <summary>A slide to add.</summary>
    public const string New = "new";

    /// <summary>New words for a slide.</summary>
    public const string Words = "words";

    /// <summary>A slide whose passage is gone.</summary>
    public const string Remove = "remove";

    public const string Orphan = "orphan";
}
