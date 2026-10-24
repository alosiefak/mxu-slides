using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Scene;

// The slide-timer rules from SlideScene/SlideSceneBuilder.swift (autoAdvanceTarget,
// effectiveAutoAdvance, fillMediaID), kept beside the rest of the builder's port.
public static partial class SlideSceneBuilder
{
    /// <summary>
    /// Where an auto-advance goes from <paramref name="occurrence"/> in <paramref name="count"/>
    /// arranged slides: the next one, else back to the first for a "Slide Show" loop (a
    /// walk-in announcement loop), else nowhere (the service moves on to its next item).
    /// </summary>
    public static int? AutoAdvanceTarget(int occurrence, int count, bool loopToStart)
    {
        if (count <= 0 || occurrence < 0 || occurrence >= count) return null;
        var next = occurrence + 1;
        if (next < count) return next;
        return loopToStart ? 0 : null;
    }

    /// <summary>A slide's own timer beats its presentation's ("Slide Show": every slide, the same delay).</summary>
    public static AutoAdvance? EffectiveAutoAdvance(Slide slide, Presentation? presentation) => slide.AutoAdvance ?? presentation?.AutoAdvance;

    /// <summary>
    /// The library media a slide object shows (a shape filled with an image or video, or an old
    /// media object). Live inputs and screen captures aren't library media, so they're left out.
    /// </summary>
    public static string? FillMediaId(SlideObject source)
    {
        var o = SlideObjectNormalization.Normalized(source);
        if (o.ObjectKind != SlideObjectKind.Shape || o.Fill is not { FillKind: FillKind.Media } fill) return null;
        if (!string.IsNullOrEmpty(fill.ScreenSourceId) || !string.IsNullOrEmpty(fill.LiveInputId)
            || fill.CaptureSourceKind is not null || !string.IsNullOrEmpty(fill.CaptureSourceId))
            return null;
        return string.IsNullOrEmpty(fill.MediaId) ? null : fill.MediaId;
    }
}
