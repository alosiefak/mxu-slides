using MxU.Slides.Core.Model;

namespace MxU.Slides.Broadcast.Media;

/// <summary>
/// Whether a video will play well in a service on Windows, decided from what the probe found.
/// The Mac does the same with its list of codecs AVFoundation plays (H.264, HEVC, ProRes):
/// anything else is flagged "needs transcode" at import, so it's found on Tuesday, not on
/// Sunday morning. Windows' list is the codecs PCs decode in hardware and the app's player
/// handles: H.264, HEVC and Windows Media (VC-1). A file Windows can decode but whose codec
/// isn't on the list can be transcoded to H.264 here; a file Windows has no decoder for can't
/// be (decoding is the first half of transcoding), so its note says what to do instead.
/// </summary>
public static class MediaPlayability
{
    private static readonly HashSet<string> PlayableCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "H264", "AVC1", "HEVC", "H265", "HVC1", "HEV1", "WMV1", "WMV2", "WMV3", "WVC1",
    };

    /// <summary>What a video probe found, before it's turned into a status.</summary>
    public sealed record Findings(
        string? OpenError,
        bool HasVideo,
        string Codec,
        bool VideoDecodes,
        bool HasAudio,
        string AudioCodec,
        bool AudioDecodes);

    public static bool IsPlayableCodec(string codec) => PlayableCodecs.Contains(codec);

    /// <summary>The item's status and note for a video with these findings.</summary>
    public static (MediaFileStatus Status, string Detail) Judge(Findings found)
    {
        if (found.OpenError is { } error) return (MediaFileStatus.NeedsTranscode, $"unreadable by Windows: {error}");
        if (!found.HasVideo) return (MediaFileStatus.NeedsTranscode, "no video track");
        if (!found.VideoDecodes) return (MediaFileStatus.NeedsTranscode, NoDecoder(found.Codec));
        if (!IsPlayableCodec(found.Codec))
            return (MediaFileStatus.NeedsTranscode, $"unsupported codec: {found.Codec}. Transcode Now makes an H.264 copy that plays everywhere");
        if (found.HasAudio && !found.AudioDecodes)
            return (MediaFileStatus.Ready, $"its sound ({found.AudioCodec}) can't be decoded on Windows, so it plays silently");
        return (MediaFileStatus.Ready, "");
    }

    /// <summary>The note for a video Windows can't decode at all, with what to do about it.</summary>
    public static string NoDecoder(string codec)
    {
        var name = Describe(codec);
        var advice = codec.ToUpperInvariant() switch
        {
            "HEVC" or "H265" or "HVC1" or "HEV1" => "Install “HEVC Video Extensions” from the Microsoft Store, or export it as H.264 MP4",
            "APCH" or "APCN" or "APCS" or "APCO" or "AP4H" or "AP4X" => "Export it as H.264 MP4 on the Mac it came from",
            "VP90" or "VP80" => "Install “VP9 Video Extensions” from the Microsoft Store, or export it as H.264 MP4",
            "AV01" => "Install “AV1 Video Extension” from the Microsoft Store, or export it as H.264 MP4",
            _ => "Export it as H.264 MP4 on the computer it came from",
        };
        return $"Windows has no decoder for {name} video, so it can't play or transcode it. {advice}";
    }

    /// <summary>A friendlier name for the codecs people meet ("apch" is ProRes 422 HQ).</summary>
    public static string Describe(string codec) => codec.ToUpperInvariant() switch
    {
        "H264" or "AVC1" => "H.264",
        "HEVC" or "H265" or "HVC1" or "HEV1" => "HEVC (H.265)",
        "APCH" => "ProRes 422 HQ",
        "APCN" => "ProRes 422",
        "APCS" => "ProRes 422 LT",
        "APCO" => "ProRes 422 Proxy",
        "AP4H" => "ProRes 4444",
        "AP4X" => "ProRes 4444 XQ",
        "VP90" => "VP9",
        "VP80" => "VP8",
        "AV01" => "AV1",
        "MJPG" => "Motion JPEG",
        "AVDN" => "DNxHD",
        _ => codec,
    };
}
