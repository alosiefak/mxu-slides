using MxU.Slides.Core.Model;

namespace MxU.Slides.Broadcast.Streaming;

/// <summary>
/// The quality ladder offered for streams: a size and frame rate with the bitrate recommended
/// for SDR and for HDR. Port of StreamQualityRung in the Mac's StreamRecordSettings.swift
/// (the non-UI half).
/// </summary>
public sealed record StreamQualityRung(int Width, int Height, int Fps, int SdrKbps, int HdrKbps)
{
    public string Id => $"{Width}x{Height}@{Fps}";

    public static IReadOnlyList<StreamQualityRung> Rungs { get; } =
    [
        new(1280, 720, 30, 6000, 7500),
        new(1280, 720, 60, 8000, 10000),
        new(1920, 1080, 30, 10000, 12500),
        new(1920, 1080, 60, 12000, 15000),
        new(3840, 2160, 30, 35000, 44000),
        new(3840, 2160, 60, 40000, 50000),
    ];

    /// <summary>The rung a preset's size and rate sit on (1920×1080 at 30 when unset), if any.</summary>
    public static StreamQualityRung? Matching(StreamRecordPreset? preset)
    {
        if (preset is null) return null;
        return Rungs.FirstOrDefault(r =>
            r.Width == (preset.Width ?? 1920) && r.Height == (preset.Height ?? 1080) && r.Fps == (preset.FrameRate ?? 30));
    }

    /// <summary>
    /// The bitrate for any size: the rung whose height is nearest the picture's short side, at
    /// 30 or 60 fps (48 and up counts as 60). Used when a destination overrides the preset's size.
    /// </summary>
    public static int? RecommendedKbps(int width, int height, int fps, bool hdr)
    {
        var shortSide = Math.Min(width, height);
        var rateClass = fps >= 48 ? 60 : 30;
        var rung = Rungs.Where(r => r.Fps == rateClass).MinBy(r => Math.Abs(r.Height - shortSide));
        return rung is null ? null : hdr ? rung.HdrKbps : rung.SdrKbps;
    }
}
