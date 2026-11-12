using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Library;

// Gentle repairs for a starter theme a library already holds: add the designs a newer build
// ships and fix the ones whose behaviour changed, without touching anything the user made or
// renamed. Port of the repair half of the Mac's StarterPacks.swift (addingLyricsDesigns,
// addingPhotoDesigns, shrinkingPoints, stayingPhotos), which AppModel.seedStarterPacks runs.
public static partial class StarterPacks
{
    /// <summary>The smallest size the Points design's words shrink to (canvas points).</summary>
    public const double PointsFloor = 64;

    /// <summary>The lyrics designs the slides lack, a Full Slide one first.</summary>
    public static List<Slide> AddingLyricsDesigns(this StarterPack pack, IReadOnlyList<Slide> slides) =>
        AddingDesigns(pack, $"{pack.ThemeId()}.lyrics", slides, fullSlideLeads: true);

    /// <summary>The photo designs the slides lack, each at the end of its folder.</summary>
    public static List<Slide> AddingPhotoDesigns(this StarterPack pack, IReadOnlyList<Slide> slides) =>
        AddingDesigns(pack, $"{pack.ThemeId()}.photo", slides, fullSlideLeads: false);

    // A design is missing when no slide has its name (any case): a design the user made and
    // called "Photo" keeps its place, and the pack's own isn't added beside it.
    private static List<Slide> AddingDesigns(StarterPack pack, string idPrefix, IReadOnlyList<Slide> slides, bool fullSlideLeads)
    {
        var result = slides.ToList();
        var designs = (pack.MakeTheme().Slides ?? []).Where(s => s.Id.StartsWith(idPrefix, StringComparison.Ordinal));
        foreach (var design in designs)
        {
            if (result.Any(s => string.Equals(s.Name, design.Name, StringComparison.OrdinalIgnoreCase))) continue;
            if (fullSlideLeads && design.Folder == "Full Slide")
            {
                result.Insert(0, design);
            }
            else if (design.Folder is { } folder && result.FindLastIndex(s => s.Folder == folder) is var last and >= 0)
            {
                result.Insert(last + 1, design);
            }
            else
            {
                result.Add(design);
            }
        }
        return result;
    }

    /// <summary>
    /// The Points design's words shrink to <see cref="PointsFloor"/> instead of running off the
    /// slide. Only a Points box that never chose a shrink setting is changed.
    /// </summary>
    public static List<Slide> ShrinkingPoints(this StarterPack pack, IReadOnlyList<Slide> slides)
    {
        var id = $"{pack.ThemeId()}.points.text";
        return slides.Select(slide =>
        {
            var index = slide.Objects.FindIndex(o => o.Id == id);
            if (index < 0 || slide.Objects[index].TextStyle is not { AutoShrink: null } style) return slide;
            var result = Json.MxuJson.Clone(slide);
            result.Objects[index].TextStyle!.AutoShrink = true;
            result.Objects[index].TextStyle!.MinFontSize = PointsFloor;
            return result;
        }).ToList();
    }

    /// <summary>
    /// A picture stays up until the next slide: the pack's photo designs lose any step that takes
    /// an object away (an older build's spots left on a click). Other slides are left alone.
    /// </summary>
    public static List<Slide> StayingPhotos(this StarterPack pack, IReadOnlyList<Slide> slides)
    {
        var prefix = $"{pack.ThemeId()}.photo";
        return slides.Select(slide =>
        {
            if (!slide.Id.StartsWith(prefix, StringComparison.Ordinal)) return slide;
            var leaves = slide.Objects.SelectMany(o => (o.AnimationSteps ?? []).Where(s => s.Kind == AnimationKind.Out).Select(s => s.Id)).ToHashSet();
            if (leaves.Count == 0) return slide;
            var result = Json.MxuJson.Clone(slide);
            foreach (var o in result.Objects)
                o.AnimationSteps = o.AnimationSteps?.Where(s => !leaves.Contains(s.Id)).ToList();
            result.AnimationOrder = result.AnimationOrder?.Where(s => !leaves.Contains(s)).ToList();
            return result;
        }).ToList();
    }
}
