using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Scene;

/// <summary>
/// The inspector's "Mask" section: one object can be clipped by another (its
/// <i>matte</i>), keeping what's inside the matte or cutting it out. The matte
/// itself is never drawn on an output. Port of the mask rules in the Mac's
/// SlideObjectInspector.swift (maskSection, maskCandidates, isMatte).
/// </summary>
public static class ObjectMasks
{
    /// <summary>
    /// The objects <paramref name="target"/> may be masked with: any other object,
    /// except one whose own chain of masks leads back to <paramref name="target"/>
    /// (that would be a loop). Chains longer than 16 are not followed.
    /// </summary>
    public static List<SlideObject> Candidates(SlideObject target, IReadOnlyList<SlideObject> objects)
    {
        var byId = new Dictionary<string, SlideObject>(StringComparer.Ordinal);
        foreach (var o in objects) byId.TryAdd(o.Id, o);
        return objects.Where(candidate =>
        {
            if (candidate.Id == target.Id) return false;
            var cursor = candidate.MaskObjectId;
            for (var hops = 0; !string.IsNullOrEmpty(cursor) && hops < 16; hops++)
            {
                if (cursor == target.Id) return false;
                cursor = byId.GetValueOrDefault(cursor)?.MaskObjectId;
            }
            return true;
        }).ToList();
    }

    /// <summary>True when another object uses <paramref name="o"/> as its mask (so it's hidden on outputs).</summary>
    public static bool IsMatte(SlideObject o, IReadOnlyList<SlideObject> objects) => objects.Any(other => other.MaskObjectId == o.Id);

    /// <summary>Masks <paramref name="o"/> with the object <paramref name="matteId"/>, or clears its mask (null or "").</summary>
    public static void SetMask(SlideObject o, string? matteId)
    {
        o.MaskObjectId = string.IsNullOrEmpty(matteId) ? null : matteId;
        if (o.MaskObjectId is null) o.MaskMode = null;
    }

    /// <summary>What an unnamed object is called in lists: Text, Shape, Media or Live Input.</summary>
    public static string FallbackName(SlideObject o)
    {
        var normalized = SlideObjectNormalization.Normalized(o);
        if (normalized.ObjectKind == SlideObjectKind.Text) return "Text";
        if (normalized.Fill is not { FillKind: FillKind.Media } fill) return "Shape";
        return string.IsNullOrEmpty(fill.MediaId) ? "Live Input" : "Media";
    }

    /// <summary>An object's name for lists, falling back to its kind.</summary>
    public static string DisplayName(SlideObject o) => string.IsNullOrWhiteSpace(o.Name) ? FallbackName(o) : o.Name;
}
