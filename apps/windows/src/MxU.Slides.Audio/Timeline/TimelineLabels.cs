using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Stage;

namespace MxU.Slides.Audio.Timeline;

/// <summary>Looks up the names a cue's label needs (each returns null for an id it doesn't know).</summary>
public sealed record TimelineNames(
    Func<string, string?> Overlay,
    Func<string, string?> Alert,
    Func<string, string?> LiveInput,
    Func<string, string?> Audio,
    Func<string, string?> Media)
{
    /// <summary>Knows no names (every label falls back to its kind: "Overlay", "Music"…).</summary>
    public static readonly TimelineNames None = new(_ => null, _ => null, _ => null, _ => null, _ => null);
}

/// <summary>
/// What the timeline editor writes on a cue: "Slide 3 · Chorus", the overlay's name, the
/// music clip's title, "Fire Action Combo +2", a stem's channel name. Port of
/// PresentationTimelineContext.label and slideWords in Sources/PresentationTimelineView.swift.
/// It lives here (not in Core) because clip titles use <see cref="TrackSets.ClipName"/>.
/// </summary>
public static class TimelineLabels
{
    /// <summary>The middle dot the Mac puts between a song's name and a stem's ("Way Maker · Keys").</summary>
    public const string Dot = " · ";

    public static string Label(TimelineCue cue, Presentation presentation, IReadOnlyList<TimelinePlayedBlock> blocks, TimelineNames names)
    {
        switch (cue.Kind())
        {
            case TimelineLane.Slide:
            {
                var section = presentation.Sections?.FirstOrDefault(s => s.Id == cue.SectionId)?.Name;
                var suffix = section is null ? "" : Dot + section;
                return PresentationTimelines.Occurrence(cue, blocks) is { } occurrence
                    ? $"Slide {occurrence + 1}{suffix}"
                    : "Missing slide" + suffix;
            }
            case TimelineLane.Overlays:
                return (cue.OverlayId is { } overlay ? names.Overlay(overlay) : null) ?? "Overlay";
            case TimelineLane.Alerts:
                return (cue.AlertId is { } alert ? names.Alert(alert) : null) ?? "Alert";
            case TimelineLane.VideoInput:
                return (cue.LiveInputId is { } input ? names.LiveInput(input) : null) ?? "Video Input";
            case TimelineLane.Audio:
                return cue.MediaId is { } music && names.Audio(music) is { } title ? TrackSets.ClipName(title) : "Music";
            case TimelineLane.Videos:
            case TimelineLane.StillGraphics:
            case TimelineLane.LoopingVideos:
                return (cue.MediaId is { } media ? names.Media(media) : null) ?? "Media";
            case TimelineLane.Actions:
            {
                var actions = cue.Actions ?? [];
                if (actions.Count == 0) return "No actions";
                return actions[0].Kind.DisplayName() + (actions.Count > 1 ? $" +{actions.Count - 1}" : "");
            }
            default:
            {
                if (cue.TrackChannelId() is null) return "Unknown layer";
                // A stem's item is named "Song · File"; the label is just the file's part.
                var name = (cue.MediaId is { } stem ? names.Audio(stem) : null) ?? "Stem";
                var parts = name.Split(Dot);
                return TrackSets.ClipName(parts[^1]);
            }
        }
    }

    /// <summary>
    /// The words on a slide cue's pin: as many of the slide's first words as fit in
    /// <paramref name="room"/> points, else its number in the played order ("?" when orphaned).
    /// </summary>
    public static string SlideWords(TimelineCue cue, Presentation presentation, IReadOnlyList<TimelinePlayedBlock> blocks, double room)
    {
        var number = PresentationTimelines.Occurrence(cue, blocks) is { } occurrence ? (occurrence + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : "?";
        if (room < 8) return "";
        if (presentation.Slides.FirstOrDefault(s => s.Id == cue.SlideId) is not { } slide) return number;
        var words = PresentationTimelines.LeadingWords(ConfidenceSceneBuilder.LyricText(slide), room);
        return words.Length == 0 ? number : words;
    }
}
