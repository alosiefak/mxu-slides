using MxU.Slides.Core.Automation;

namespace MxU.Slides.Audio.Multitrack;

/// <summary>
/// Building Core's <see cref="MultitrackStem"/> with the Mac's defaults (the record itself
/// lives in Core with <see cref="MultitrackSong"/> and <see cref="MultitrackTransition"/>,
/// since the timeline makes them and the tracks player plays them).
/// </summary>
public static class MultitrackStems
{
    /// <param name="at">Where on the song it starts (seconds).</param>
    /// <param name="inPoint">How far into the file it starts (seconds).</param>
    /// <param name="duration">How long it plays; null = to the end of the file.</param>
    public static MultitrackStem Make(string path, string channelId, double at = 0, double? inPoint = null, double? duration = null,
        ClipEnvelope? envelope = null) => new(path, channelId, at, inPoint, duration, envelope);
}
