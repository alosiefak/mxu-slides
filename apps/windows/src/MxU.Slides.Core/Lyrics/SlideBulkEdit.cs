using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Lyrics;

/// <summary>
/// Edits across several slides of a deck. So far: Merge Slides, the Core half of MxU's
/// "Merge Slides joins selected slides into one, chords along" (the menu belongs to the slide
/// editor). Port of SlideBulkEdit.mergeSlides in the Mac's SlideScene/SlideBulkEdit.swift.
/// </summary>
public static class SlideBulkEdit
{
    /// <summary>
    /// Joins the slides into the first of them in slide order: each one's lyric box (the first
    /// unlinked text box) is added to the survivor's as new lines, its chords and style runs
    /// moved down with it; its other objects come along unless the survivor already has an
    /// identical one (a shared reference box comes once). The survivor keeps its own slide
    /// settings. Returns the survivor's id, or null when fewer than two of the slides exist.
    /// </summary>
    public static string? MergeSlides(IReadOnlyList<string> slideIds, Presentation presentation)
    {
        var targets = slideIds.ToHashSet(StringComparer.Ordinal);
        var merging = presentation.Slides.Where(s => targets.Contains(s.Id)).ToList();
        if (merging.Count < 2) return null;
        var survivorIndex = presentation.Slides.FindIndex(s => s.Id == merging[0].Id);
        static bool IsLyricBox(SlideObject o) => o.ObjectKind == SlideObjectKind.Text && o.TextLink is null;
        var survivor = MxuJson.Clone(merging[0]);
        foreach (var slide in merging.Skip(1))
        {
            var joining = slide.Objects.FindIndex(IsLyricBox);
            for (var index = 0; index < slide.Objects.Count; index++)
            {
                var o = slide.Objects[index];
                var boxIndex = survivor.Objects.FindIndex(IsLyricBox);
                if (index == joining && boxIndex >= 0)
                {
                    var box = survivor.Objects[boxIndex];
                    var offset = box.Text.Split('\n').Length;
                    box.Text += "\n" + o.Text;
                    var chords = (box.Chords ?? []).Concat((o.Chords ?? []).Select(c => new ChordPlacement { Line = c.Line + offset, Column = c.Column, Symbol = c.Symbol })).ToList();
                    box.Chords = chords.Count == 0 ? null : chords;
                    var runs = (box.StyleRuns ?? []).Concat((o.StyleRuns ?? []).Select(run =>
                    {
                        var moved = MxuJson.Clone(run);
                        moved.Line += offset;
                        return moved;
                    })).ToList();
                    box.StyleRuns = runs.Count == 0 ? null : runs;
                }
                else
                {
                    // The same object but for its id (a reference box every slide carries) comes once.
                    var duplicate = survivor.Objects.Any(existing =>
                    {
                        var probe = MxuJson.Clone(o);
                        probe.Id = existing.Id;
                        return MxuJson.StructurallyEqual(probe, existing);
                    });
                    if (!duplicate) survivor.Objects.Add(MxuJson.Clone(o));
                }
            }
        }
        presentation.Slides[survivorIndex] = survivor;
        var gone = merging.Skip(1).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        presentation.Slides.RemoveAll(s => gone.Contains(s.Id));
        return survivor.Id;
    }
}
