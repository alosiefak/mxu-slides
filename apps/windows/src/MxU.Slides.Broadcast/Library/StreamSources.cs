using System.Globalization;
using MxU.Slides.Broadcast.Control;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Broadcast.Library;

/// <summary>
/// What a preset streams: the live picture of a screen (the usual case), a chosen video from
/// the library, or the newest recording in a library folder (a "replay" of a pre-recorded
/// service). Works out which file that is and whether it can play, and the one-line summaries
/// the Stream &amp; Record window shows. Port of the source half of the Mac's ServiceControls
/// (resolveStreamSource, resolveSourceItem, fileSource, captureSourcePreview, captureRowDetail).
/// </summary>
public static class StreamSources
{
    /// <summary>The picture source for <see cref="StreamingController.SourceResolver"/>: the screen, a file, or why it can't start.</summary>
    public static ResolvedSource Resolve(LibraryStore library, StreamRecordPreset preset, DateTimeOffset now) =>
        SourceItem(library, preset, now) switch
        {
            null => new ResolvedSource.Screen(),
            { Problem: { } problem } => new ResolvedSource.Unavailable(problem),
            { Item: { } item } => FileSource(item, library.BlobPath(item)),
            _ => new ResolvedSource.Screen(),
        };

    /// <summary>The library video a preset streams; null for a screen source; a problem when there's nothing suitable.</summary>
    public static (MediaItem? Item, string? Problem)? SourceItem(LibraryStore library, StreamRecordPreset preset, DateTimeOffset now)
    {
        switch (preset.SourceKind ?? StreamSourceKind.Screen)
        {
            case StreamSourceKind.MediaItem:
                return library.Get<MediaItem>(preset.SourceMediaId) is { } chosen
                    ? (chosen, null)
                    : (null, "media item not found in the library");
            case StreamSourceKind.LatestRecording:
                var folder = preset.SourceFolder ?? RecordingLibrary.DefaultFolder;
                var items = library.All<MediaItem>();
                DateTimeOffset? cutoff = preset.SourceMaxAgeHours is { } hours ? now.AddHours(-hours) : null;
                if (RecordingLibrary.LatestRecording(items, folder, cutoff) is { } latest) return (latest, null);
                if (cutoff is not null && RecordingLibrary.LatestRecording(items, folder) is { } stale)
                    return (null, $"newest recording in “{folder}” is {AgeText(stale.RecordedAt ?? 0, now)} old — over the {preset.SourceMaxAgeHours ?? 0}h limit");
                return (null, $"no finished recording in “{folder}”");
            default:
                return null;
        }
    }

    /// <summary>Whether a library item can be streamed from its file: it must be a video, ready, and on this PC.</summary>
    public static ResolvedSource FileSource(MediaItem item, string? path)
    {
        if (item.MediaKind != MediaKind.Video) return new ResolvedSource.Unavailable($"“{item.Name}” isn't a video");
        if (item.FileStatus != MediaFileStatus.Ready) return new ResolvedSource.Unavailable($"“{item.Name}” isn't ready to play");
        if (path is null || !File.Exists(path)) return new ResolvedSource.Unavailable($"“{item.Name}”: file isn't on this PC");
        double? duration = item.DurationSeconds is { } full
            ? Math.Max(0, Math.Min(item.OutPoint ?? full, full) - Math.Max(item.InPoint ?? 0, 0))
            : null;
        return new ResolvedSource.MediaFile(path, item.Name, item.InPoint, item.OutPoint, duration);
    }

    /// <summary>"under an hour", "5h", "3 days".</summary>
    public static string AgeText(double recordedAt, DateTimeOffset now)
    {
        var hours = (int)((now.ToUnixTimeMilliseconds() / 1000.0 - recordedAt) / 3600);
        if (hours < 1) return "under an hour";
        if (hours < 48) return $"{hours}h";
        return $"{hours / 24} days";
    }

    /// <summary>A line under a preset: what it will restream, or why it won't go live.</summary>
    public sealed record Preview(string Text, bool IsProblem);

    /// <summary>Null when there's nothing to say (a screen source with ready destinations).</summary>
    public static Preview? CapturePreview(LibraryStore library, StreamRecordPreset preset, IReadOnlyList<StreamPresetDestination> destinations, DateTimeOffset now)
    {
        if (StreamDestinationReadiness.StartRefusal(preset.ResolvedKind(), destinations) is { } refusal)
            return new Preview(refusal, true);
        if (preset.ResolvedKind() == StreamPresetKind.Stream && StreamDestinationReadiness.Problems(destinations) is { Count: > 0 } skipped)
            return new Preview("Will skip — " + string.Join(" · ", skipped), true);
        switch (SourceItem(library, preset, now))
        {
            case null:
                return null;
            case { Problem: { } problem }:
                return new Preview($"Won't go live: {problem}", true);
            case { Item: { } item }:
                if (FileSource(item, library.BlobPath(item)) is ResolvedSource.Unavailable unavailable)
                    return new Preview($"Won't go live: {unavailable.Reason}", true);
                var text = $"Restreams “{item.Name}”";
                if (item.RecordedAt is { } recordedAt) text += $" · recorded {AgeText(recordedAt, now)} ago";
                return new Preview(text, false);
            default:
                return null;
        }
    }

    /// <summary>
    /// The video source as one phrase: "Screen · Stage", "Screen · Automatic", "Media file · Intro",
    /// "Latest recording · Recordings". <paramref name="screenName"/> names a screen id (null when unknown).
    /// </summary>
    public static string VideoSourceTitle(LibraryStore library, StreamRecordPreset preset, Func<string, string?> screenName) =>
        (preset.SourceKind ?? StreamSourceKind.Screen) switch
        {
            StreamSourceKind.MediaItem => "Media file · " + (library.Get<MediaItem>(preset.SourceMediaId)?.Name ?? "none chosen"),
            StreamSourceKind.LatestRecording => $"Latest recording · {preset.SourceFolder ?? RecordingLibrary.DefaultFolder}",
            _ => "Screen · " + (string.IsNullOrEmpty(preset.CanvasScreenId) ? "Automatic" : screenName(preset.CanvasScreenId) ?? "Missing screen"),
        };

    /// <summary>"1080p · 30 fps · 6.0 Mbps", as the Mac's quality summary.</summary>
    public static string EncoderSummary(StreamRecordPreset preset)
    {
        var width = preset.Width ?? 1920;
        var height = preset.Height ?? 1080;
        var text = $"{Math.Min(width, height)}p · {preset.FrameRate ?? 30} fps";
        if (preset.VideoBitrateKbps is { } kbps)
            text += kbps >= 1000
                ? " · " + (kbps / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " Mbps"
                : $" · {kbps} kbps";
        return text;
    }
}
