using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Notes;

// Photo spots themselves (PhotoSpots, PictureShape) live in Core/Library/PhotoSpots.cs, as on the Mac.

/// <summary>
/// Picture and video slides: showing media full screen, and the placeholder a slide is until its
/// media arrives. Port of the Slide extension at the end of the Mac's SlidePlanner.swift
/// (showMedia, waitsForMedia, needsMediaNote, fillPlaceholder).
/// </summary>
public static class SlideMediaFacts
{
    /// <summary>How a slide's notes start when it waits for media someone has to add.</summary>
    public const string NeedsMediaNote = "Needs media:";

    /// <summary>The media as a whole-slide cue (as one dropped onto the grid as a new slide), with the video's ending.</summary>
    public static void ShowMedia(this Slide slide, string mediaId, MediaKind kind, MediaScaleMode? scale, SlideOptions.VideoEnds videoEnd)
    {
        var cue = new CueMedia
        {
            MediaId = mediaId,
            Layer = kind == MediaKind.Image ? CueMediaLayer.StillGraphics : CueMediaLayer.Videos,
            Loops = kind == MediaKind.Video ? false : null,
            Classification = MediaClassification.Foreground,
        };
        if (kind == MediaKind.Image) cue.ScaleMode = scale == MediaScaleMode.Fill ? null : scale;
        slide.AutoAdvance = null;
        if (kind == MediaKind.Video)
        {
            switch (videoEnd)
            {
                case SlideOptions.VideoEnds.Next: slide.AutoAdvance = new AutoAdvance { DelaySeconds = 0, AfterPlayback = true }; break;
                case SlideOptions.VideoEnds.Loop: cue.Loops = true; break;
                case SlideOptions.VideoEnds.FadeToBlack: cue.EndFade = MediaEndFade.Black; break;
                case SlideOptions.VideoEnds.FadeToClear: cue.EndFade = MediaEndFade.Clear; break;
            }
        }
        slide.Background = cue;
    }

    /// <summary>A slide that is only a link or "Needs media:" note, waiting for its picture or video.</summary>
    public static bool WaitsForMedia(this Slide slide) =>
        slide.Objects.Count == 0 && slide.Background is null && slide.Notes is { } notes
        && (notes.StartsWith("http://", StringComparison.Ordinal) || notes.StartsWith("https://", StringComparison.Ordinal)
            || notes.StartsWith(NeedsMediaNote, StringComparison.Ordinal));

    /// <summary>Gives a waiting slide its media, dropping the "Needs media" note.</summary>
    public static void FillPlaceholder(this Slide slide, MediaItem item, SlideOptions options)
    {
        slide.ShowMedia(item.Id, item.MediaKind, options.PictureScale, options.VideoEnd);
        if (slide.Notes?.StartsWith(NeedsMediaNote, StringComparison.Ordinal) == true) slide.Notes = null;
    }
}
