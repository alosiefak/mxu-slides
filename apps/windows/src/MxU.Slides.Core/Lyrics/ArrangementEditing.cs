using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Lyrics;

/// <summary>
/// The arrangement editor's rules. An arrangement is a named order of a song's sections
/// ("Verse 1, Chorus, Verse 2, Chorus, Bridge, Chorus"): it repeats a chorus without copying its
/// slides, and each service row picks the one it plays. Arrangements are part of the shared
/// <see cref="Presentation"/> (its <c>arrangements</c> and <c>defaultArrangementId</c>), so a Mac
/// reads every change made here.
///
/// Port of the arrangement half of the Mac's Sources/AppModel.swift (addArrangement seeded from
/// the slides, updateArrangement, deleteArrangement), SlideEditorModel.swift (sectionName,
/// arrangedSlideCount), ServiceContinuousView.swift (ArrangementStrip's dropBlock and
/// baseRunSections) and SlideSceneBuilder.showsArrangements. Windows adds Duplicate and
/// typing the order as text (<see cref="SongEditing.ParseOrder"/>).
/// </summary>
public static class ArrangementEditing
{
    /// <summary>What a block whose section was deleted is called (the Mac's wording).</summary>
    public const string DeletedSection = "Deleted Section";

    public static Arrangement? Find(Presentation presentation, string? arrangementId) =>
        string.IsNullOrEmpty(arrangementId) ? null : presentation.Arrangements?.FirstOrDefault(a => a.Id == arrangementId);

    /// <summary>
    /// The song shows the arrangement controls: it has a section with slides, or already has
    /// arrangements (an import may bring them without sections on the slides).
    /// </summary>
    public static bool ShowsArrangements(Presentation presentation) =>
        SlideSceneBuilder.ArrangementBlockStarts(presentation).Count > 0 || presentation.Arrangements is { Count: > 0 };

    /// <summary>
    /// What a new arrangement starts as: each section once, in the order its slides first appear
    /// (sections no slide uses, and slides pointing at a section that isn't there, are left out).
    /// </summary>
    public static List<string> SeedSectionIds(Presentation presentation)
    {
        var known = (presentation.Sections ?? []).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>();
        foreach (var slide in presentation.Slides)
        {
            if (slide.SectionId is not { Length: > 0 } sectionId || !known.Contains(sectionId) || !seen.Add(sectionId)) continue;
            ordered.Add(sectionId);
        }
        return ordered;
    }

    /// <summary>"Arrangement 3" for a song that has two.</summary>
    public static string NextName(Presentation presentation) => $"Arrangement {(presentation.Arrangements?.Count ?? 0) + 1}";

    /// <summary>
    /// Adds an arrangement seeded from the slides (<see cref="SeedSectionIds"/>) at the end, and
    /// returns it. Null when the song has no sectioned slides to arrange (the Mac's guard).
    /// </summary>
    public static Arrangement? Add(Presentation presentation, string? id = null)
    {
        var seed = SeedSectionIds(presentation);
        if (seed.Count == 0) return null;
        var arrangement = new Arrangement { Id = id ?? LibraryStore.NewId(), Name = NextName(presentation), SectionIds = seed };
        presentation.Arrangements = [.. presentation.Arrangements ?? [], arrangement];
        return arrangement;
    }

    /// <summary>
    /// Windows: a copy of an arrangement, named "Sunday copy" ("Sunday copy 2" if that's taken),
    /// right after the original. Handy for a one-week variation that keeps the usual order intact.
    /// </summary>
    public static Arrangement? Duplicate(Presentation presentation, string arrangementId, string? id = null)
    {
        if (Find(presentation, arrangementId) is not { } original || presentation.Arrangements is not { } list) return null;
        var names = list.Select(a => a.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var name = $"{original.Name} copy";
        for (var n = 2; names.Contains(name); n++) name = $"{original.Name} copy {n}";
        var copy = new Arrangement { Id = id ?? LibraryStore.NewId(), Name = name, SectionIds = [.. original.SectionIds] };
        list.Insert(list.IndexOf(original) + 1, copy);
        return copy;
    }

    /// <summary>Renames an arrangement; a blank name is ignored and the name is trimmed (the Mac's binding). False when nothing changed.</summary>
    public static bool Rename(Presentation presentation, string arrangementId, string name)
    {
        var trimmed = name.TrimmingWhitespaces();
        if (trimmed.Length == 0 || Find(presentation, arrangementId) is not { } arrangement || arrangement.Name == trimmed) return false;
        arrangement.Name = trimmed;
        return true;
    }

    /// <summary>
    /// Deletes an arrangement. When it was the song's default the song goes back to its slide
    /// order (the Mac clears <c>defaultArrangementId</c> with it). A service row still naming it
    /// plays the slide order too: <see cref="SlideSceneBuilder.ArrangedSlides"/> ignores a missing id.
    /// </summary>
    public static bool Delete(Presentation presentation, string arrangementId)
    {
        if (presentation.Arrangements is not { } list || list.RemoveAll(a => a.Id == arrangementId) == 0) return false;
        if (presentation.DefaultArrangementId == arrangementId) presentation.DefaultArrangementId = null;
        if (list.Count == 0) presentation.Arrangements = null;
        return true;
    }

    /// <summary>Makes an arrangement (or none: the slide order) what the song plays when a service row doesn't pick one.</summary>
    public static bool SetDefault(Presentation presentation, string? arrangementId)
    {
        var id = Find(presentation, arrangementId)?.Id;
        if (presentation.DefaultArrangementId == id) return false;
        presentation.DefaultArrangementId = id;
        return true;
    }

    /// <summary>Adds a section at the end of an arrangement (the strip's + and the editor's Add Section).</summary>
    public static bool AddBlock(Presentation presentation, string arrangementId, string sectionId)
    {
        if (Find(presentation, arrangementId) is not { } arrangement || presentation.Sections?.Any(s => s.Id == sectionId) != true) return false;
        arrangement.SectionIds.Add(sectionId);
        return true;
    }

    /// <summary>Removes the block at a position (one repeat of a section, not every one).</summary>
    public static bool RemoveBlock(Presentation presentation, string arrangementId, int index)
    {
        if (Find(presentation, arrangementId) is not { } arrangement || index < 0 || index >= arrangement.SectionIds.Count) return false;
        arrangement.SectionIds.RemoveAt(index);
        return true;
    }

    /// <summary>
    /// Moves the block at <paramref name="from"/> so it lands before the block now at
    /// <paramref name="before"/> (the count to move it to the end). Dropping a block on itself or
    /// just after itself changes nothing. Port of ArrangementStrip.dropBlock.
    /// </summary>
    public static bool MoveBlock(Presentation presentation, string arrangementId, int from, int before)
    {
        if (Find(presentation, arrangementId) is not { } arrangement) return false;
        var ids = arrangement.SectionIds;
        if (from < 0 || from >= ids.Count || from == before || from + 1 == before) return false;
        var block = ids[from];
        ids.RemoveAt(from);
        var target = from < before ? before - 1 : before;
        ids.Insert(Math.Clamp(target, 0, ids.Count), block);
        return true;
    }

    /// <summary>
    /// Replaces an arrangement's blocks with a typed order ("V1, C, V2, C x2", read by
    /// <see cref="SongEditing.ParseOrder"/>). An order that names no section is refused, so a typo
    /// never empties an arrangement. False when nothing changed.
    /// </summary>
    public static bool SetOrder(Presentation presentation, string arrangementId, string order)
    {
        if (Find(presentation, arrangementId) is not { } arrangement) return false;
        var ids = SongEditing.ParseOrder(order, presentation.Sections ?? []);
        if (ids.Count == 0 || ids.SequenceEqual(arrangement.SectionIds)) return false;
        arrangement.SectionIds = ids;
        return true;
    }

    /// <summary>A block's name: its section's, or "Deleted Section" when that section has gone.</summary>
    public static string SectionName(Presentation presentation, string sectionId) =>
        presentation.Sections?.FirstOrDefault(s => s.Id == sectionId)?.Name ?? DeletedSection;

    /// <summary>The section has no slides yet: its pill is dimmed, and playing it shows nothing.</summary>
    public static bool SectionIsEmpty(Presentation presentation, string sectionId) => !presentation.Slides.Any(s => s.SectionId == sectionId);

    /// <summary>How many slides the arrangement plays ("12 slides" under the editor's list).</summary>
    public static int ArrangedSlideCount(Presentation presentation, string arrangementId) =>
        SlideSceneBuilder.ArrangedSlides(presentation, arrangementId).Count;

    /// <summary>
    /// The pills shown when no arrangement is picked: one per run of slides in a section, in slide
    /// order (Verse 1, Chorus, Verse 2 for slides written that way). Port of baseRunSections.
    /// </summary>
    public static List<PresentationSection> BaseRunSections(Presentation presentation)
    {
        var sections = presentation.Sections ?? [];
        return [.. SlideSceneBuilder.ArrangementBlockStarts(presentation)
            .Select(start => sections.FirstOrDefault(s => s.Id == presentation.Slides[start].SectionId))
            .OfType<PresentationSection>()];
    }

    /// <summary>The block (pill) a played slide falls in, from the blocks' first slides; null before the first.</summary>
    public static int? BlockIndex(IReadOnlyList<int> starts, int occurrence)
    {
        int? found = null;
        for (var i = 0; i < starts.Count; i++)
            if (starts[i] <= occurrence) found = i;
        return found;
    }

    /// <summary>
    /// The arrangement as the order text the song editor and the arrangement editor show:
    /// section names in order, a run of the same section folded ("Verse 1, Chorus x2, Bridge").
    /// <see cref="SongEditing.ParseOrder"/> reads it back to the same blocks.
    /// </summary>
    public static string OrderText(Presentation presentation, string? arrangementId) => SongEditing.OrderText(presentation, arrangementId);
}
