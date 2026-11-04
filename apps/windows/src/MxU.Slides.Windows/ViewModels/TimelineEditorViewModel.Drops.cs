using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Editing;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// Library items dragged from the main window's bins onto the lanes (a PORTING "not ported yet"
/// item until Phase 7): music onto the Music lane, media onto a media lane, an overlay onto the
/// Overlays lane, each landing as a cue where it was dropped. The cue is the same one the lane's
/// + menu makes (Core/Automation/Timelines/TimelineEditing.cs).
/// </summary>
public sealed partial class TimelineEditorViewModel
{
    /// <summary>Whether a dragged item can land on <paramref name="lane"/>.</summary>
    public bool TakesDrop(LibraryDrag drag, TimelineLane lane) => CanEdit && (lane, drag.Kind) switch
    {
        (TimelineLane.Audio, var kind) when kind == typeof(AudioItem) => true,
        (TimelineLane.Videos or TimelineLane.StillGraphics or TimelineLane.LoopingVideos, var kind) when kind == typeof(MediaItem) => true,
        (TimelineLane.Overlays, var kind) when kind == typeof(Overlay) => true,
        _ => false,
    };

    /// <summary>Adds the cue a dragged item makes, at <paramref name="at"/> seconds.</summary>
    public bool DropLibraryItem(LibraryDrag drag, TimelineLane lane, double at)
    {
        if (!TakesDrop(drag, lane)) return false;
        TimelineCue? cue = drag.Kind == typeof(AudioItem) && main.Library.Get<AudioItem>(drag.Id) is { } song ? TimelineEditing.MusicCue(song, at)
            : drag.Kind == typeof(MediaItem) && main.Library.Get<MediaItem>(drag.Id) is { } media ? TimelineEditing.MediaCue(media, at)
            : drag.Kind == typeof(Overlay) && main.Library.Get<Overlay>(drag.Id) is not null ? TimelineEditing.OverlayCue(drag.Id, at)
            : null;
        if (cue is null) return false;
        Add(cue);
        return true;
    }
}
