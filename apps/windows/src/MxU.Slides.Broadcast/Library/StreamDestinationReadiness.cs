using MxU.Slides.Core.Model;

namespace MxU.Slides.Broadcast.Library;

/// <summary>
/// Whether a destination can stream at all: it needs a server URL, and a stream key unless it
/// is SRT. A stream preset refuses to start only when none of its destinations is ready (one
/// good destination is enough). Port of StreamDestinationReadiness.swift.
/// </summary>
public static class StreamDestinationReadiness
{
    /// <summary>The sentence shown for an unready destination, e.g. "Resi: no stream key"; null when it's ready.</summary>
    public static string? Problem(StreamPresetDestination destination)
    {
        if (string.IsNullOrWhiteSpace(destination.Url))
            return $"{destination.Name}: no server URL";
        if (destination.Transport != StreamTransport.Srt && string.IsNullOrEmpty(destination.StreamKey))
            return $"{destination.Name}: no stream key";
        return null;
    }

    public static bool IsReady(StreamPresetDestination destination) => Problem(destination) is null;

    public static IReadOnlyList<string> Problems(IEnumerable<StreamPresetDestination> destinations) =>
        destinations.Select(Problem).OfType<string>().ToList();

    /// <summary>Why a preset won't go live, or null when it can (record-only presets never refuse).</summary>
    public static string? StartRefusal(StreamPresetKind kind, IReadOnlyList<StreamPresetDestination> destinations)
    {
        if (kind != StreamPresetKind.Stream) return null;
        if (destinations.Count == 0) return "Won't go live: no destinations selected";
        if (!destinations.Any(IsReady))
            return "Won't go live: " + string.Join(" · ", Problems(destinations));
        return null;
    }
}
