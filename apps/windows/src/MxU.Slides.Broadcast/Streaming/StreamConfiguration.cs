namespace MxU.Slides.Broadcast.Streaming;

/// <summary>How a stream reaches its server.</summary>
public enum StreamEndpointKind
{
    Rtmp,
    Rtmps,

    /// <summary>Kept so presets from the Mac load, but not available on Windows yet (no SRT library).</summary>
    Srt,

    /// <summary>YouTube-style HLS ingest: MPEG-TS segments and a playlist PUT over HTTPS.</summary>
    Hls,
}

/// <summary>The video codec a stream asks for.</summary>
public enum StreamCodec
{
    H264,

    /// <summary>HLS only on the Mac. Windows streams H.264 instead (see <see cref="StreamSession"/>).</summary>
    Hevc,
}

/// <summary>
/// Where one stream goes: a server URL and, for RTMP, the stream key. The key is a secret;
/// this object only ever lives in memory (the library keeps keys in <c>StreamKeyVault</c>).
/// Port of StreamEndpoint in the Mac's StreamConfiguration.swift.
/// </summary>
public sealed record StreamEndpoint
{
    public StreamEndpoint(
        string name, StreamEndpointKind kind, string url, string streamKey = "", string? backupUrl = null, string? id = null)
    {
        Id = id ?? Guid.NewGuid().ToString().ToUpperInvariant();
        Name = name;
        Kind = kind;
        Url = url;
        StreamKey = streamKey;
        BackupUrl = backupUrl;
    }

    /// <summary>Stable id (the library destination's id when it came from one).</summary>
    public string Id { get; init; }

    public string Name { get; init; }

    public StreamEndpointKind Kind { get; init; }

    /// <summary>The server: rtmp(s)://host[:port]/app for RTMP, the upload URL template for HLS.</summary>
    public string Url { get; init; }

    /// <summary>The RTMP stream key (secret). Empty for HLS and SRT.</summary>
    public string StreamKey { get; init; }

    /// <summary>HLS only: a second ingest the uploader fails over to.</summary>
    public string? BackupUrl { get; init; }

    /// <summary>Never prints the stream key, so endpoints are safe to log.</summary>
    public override string ToString() => $"{Name} ({Kind}) {Url}";
}

/// <summary>
/// The picture a stream sends: size, frame rate, bitrate and codec. Port of
/// StreamVideoConfiguration. <see cref="Hdr"/> is carried for parity with the Mac,
/// but Windows has no HDR program feed, so it is always streamed as SDR.
/// </summary>
public sealed record StreamVideoConfiguration(
    int Width,
    int Height,
    int FrameRate = 30,
    int BitrateKbps = 4500,
    StreamCodec Codec = StreamCodec.H264,
    bool Hdr = false)
{
    /// <summary>The encoders need even sizes (4:2:0 colour halves both axes).</summary>
    public int EncodedWidth => Math.Max(2, Width & ~1);

    public int EncodedHeight => Math.Max(2, Height & ~1);

    public int SafeFrameRate => Math.Clamp(FrameRate, 1, 240);
}
