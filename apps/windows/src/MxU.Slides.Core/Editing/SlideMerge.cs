using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Editing;

/// <summary>
/// Merge Slides: picked slides joined into one, chords along. A port of the Mac's
/// SlideBulkEdit.mergeSlides (MxU's update of 2026-11, "Merge Slides joins selected slides into one,
/// chords along"):
/// <list type="bullet">
/// <item>the first picked slide in slide order survives, with its own settings (actions, group, design);</item>
/// <item>each other slide's lyric box (its first text box that isn't linked to a field) joins the
/// survivor's, on new lines, its chords and styled words moved down with it;</item>
/// <item>its other objects come along, except one the survivor already has (a reference line);</item>
/// <item>the other slides go.</item>
/// </list>
/// Windows: the engine engineer (wt\up-core) is porting the rest of SlideBulkEdit; integration keeps
/// one copy of this function.
/// </summary>
public static class SlideMerge
{
    /// <summary>Merges the slides; returns the survivor's id, or null when fewer than two are picked.</summary>
    public static string? MergeSlides(IReadOnlyCollection<string> slideIds, Presentation presentation)
    {
        var merging = presentation.Slides.Where(s => slideIds.Contains(s.Id)).ToList();
        if (merging.Count < 2) return null;
        static bool IsLyricBox(SlideObject o) => o.ObjectKind == SlideObjectKind.Text && o.TextLink is null;
        var survivor = merging[0];
        foreach (var slide in merging.Skip(1))
        {
            var joining = slide.Objects.FindIndex(IsLyricBox);
            for (var index = 0; index < slide.Objects.Count; index++)
            {
                var o = slide.Objects[index];
                if (index == joining && survivor.Objects.FirstOrDefault(IsLyricBox) is { } box)
                {
                    var offset = box.Text.Split('\n').Length;
                    box.Text += "\n" + o.Text;
                    var chords = (box.Chords ?? []).Concat((o.Chords ?? []).Select(c => new ChordPlacement { Line = c.Line + offset, Column = c.Column, Symbol = c.Symbol })).ToList();
                    box.Chords = chords.Count > 0 ? chords : null;
                    var runs = (box.StyleRuns ?? []).Concat((o.StyleRuns ?? []).Select(r =>
                    {
                        var moved = MxuJson.Clone(r);
                        moved.Line += offset;
                        return moved;
                    })).ToList();
                    box.StyleRuns = runs.Count > 0 ? runs : null;
                }
                else if (!survivor.Objects.Any(existing => SameApartFromId(o, existing)))
                {
                    survivor.Objects.Add(o);
                }
            }
        }
        presentation.Slides.RemoveAll(s => s != survivor && merging.Contains(s));
        return survivor.Id;
    }

    // The same object, on another slide: equal in every field but its id.
    private static bool SameApartFromId(SlideObject candidate, SlideObject existing)
    {
        var probe = MxuJson.Clone(candidate);
        probe.Id = existing.Id;
        return MxuJson.StructurallyEqual(probe, existing);
    }
}
