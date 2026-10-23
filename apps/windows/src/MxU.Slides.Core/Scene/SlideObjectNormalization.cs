using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Scene;

/// <summary>
/// Folds legacy object shapes into the current ones before rendering: media
/// and live-input objects become shapes with a media fill, and the old
/// "builds" fields become animation steps. Port of SlideObjectNormalization.swift.
/// </summary>
public static class SlideObjectNormalization
{
    public static bool NeedsNormalization(SlideObject o) =>
        o.ObjectKind is SlideObjectKind.Media or SlideObjectKind.LiveInput
        || o.Builds is not null
        || o.TextLink?.IncludeBuilds is not null;

    public static SlideObject Normalized(SlideObject o)
    {
        if (!NeedsNormalization(o)) return o;
        var n = MxuJson.Clone(o);
        if (n.Builds is { } legacySteps)
        {
            n.AnimationSteps ??= legacySteps;
            n.Builds = null;
        }
        if (n.TextLink is { IncludeBuilds: { } legacyInclude } link)
        {
            link.IncludeSteps ??= legacyInclude;
            link.IncludeBuilds = null;
        }
        if (o.ObjectKind is not (SlideObjectKind.Media or SlideObjectKind.LiveInput)) return n;

        var wasLiveInput = o.ObjectKind == SlideObjectKind.LiveInput;
        n.ObjectKind = SlideObjectKind.Shape;
        if (n.Fill is null)
        {
            var fill = new ObjectFill
            {
                FillKind = FillKind.Media,
                MediaId = o.MediaId,
                MediaScaleMode = o.MediaScaleMode,
                MediaSourceRect = wasLiveInput ? null : o.MediaSourceRect is null ? null : MxuJson.Clone(o.MediaSourceRect),
                Loops = o.Loops,
                CaptureSourceKind = o.CaptureSourceKind,
                CaptureSourceId = o.CaptureSourceId,
                ScreenSourceId = o.ScreenSourceId,
                LiveInputId = o.LiveInputId,
            };
            if (wasLiveInput && !HasLiveSource(fill) && string.IsNullOrEmpty(fill.MediaId))
            {
                fill.CaptureSourceKind ??= CaptureSourceKind.Camera;
                fill.CaptureSourceId ??= "";
            }
            n.Fill = fill;
        }
        n.MediaId = null;
        n.MediaScaleMode = null;
        n.MediaSourceRect = null;
        n.Loops = null;
        n.CaptureSourceKind = null;
        n.CaptureSourceId = null;
        n.ScreenSourceId = null;
        n.LiveInputId = null;
        return n;
    }

    private static bool HasLiveSource(ObjectFill fill) =>
        !string.IsNullOrEmpty(fill.LiveInputId)
        || !string.IsNullOrEmpty(fill.CaptureSourceId)
        || fill.CaptureSourceKind is not null
        || !string.IsNullOrEmpty(fill.ScreenSourceId);

    public static List<SlideObject> Normalized(IEnumerable<SlideObject> objects) => objects.Select(Normalized).ToList();
}
