using System.Globalization;
using System.Text;

namespace MxU.Slides.Broadcast.Hls;

/// <summary>
/// The live HLS playlist the ingest reads: the last <see cref="Window"/> segments and the
/// sequence number of the first. Each upload adds a segment; the oldest falls off and the
/// media sequence advances, so the server always knows which segments are new.
/// Port of HLSPlaylist.swift.
/// </summary>
internal sealed class HlsPlaylist(int mediaSequence = 0)
{
    public const int Window = 5;

    public sealed record Segment(string Name, double Duration);

    private readonly List<Segment> segments = [];

    public int MediaSequence { get; private set; } = mediaSequence;

    public IReadOnlyList<Segment> Segments => segments;

    /// <summary>The sequence number the next segment will get.</summary>
    public int NextSequence => MediaSequence + segments.Count;

    public void Add(string name, double duration)
    {
        segments.Add(new Segment(name, duration));
        while (segments.Count > Window)
        {
            segments.RemoveAt(0);
            MediaSequence++;
        }
    }

    public string Render()
    {
        var target = Math.Max(1, segments.Count == 0 ? 1 : (int)Math.Ceiling(segments.Max(s => s.Duration)));
        var builder = new StringBuilder()
            .Append("#EXTM3U\n")
            .Append("#EXT-X-VERSION:3\n")
            .Append(CultureInfo.InvariantCulture, $"#EXT-X-TARGETDURATION:{target}\n")
            .Append(CultureInfo.InvariantCulture, $"#EXT-X-MEDIA-SEQUENCE:{MediaSequence}\n");
        foreach (var segment in segments)
        {
            builder.Append(CultureInfo.InvariantCulture, $"#EXTINF:{segment.Duration:0.000},\n");
            builder.Append(segment.Name).Append('\n');
        }
        return builder.ToString();
    }
}

/// <summary>
/// What survives a reconnect of one HLS session: segment names stay unique (a timestamp plus
/// a counter) and the media sequence never goes backwards, which the ingest requires.
/// Thread-safe. Port of HLSContinuity.
/// </summary>
internal sealed class HlsContinuity(long? epochMillis = null)
{
    private readonly Lock gate = new();
    private int nextIndex;
    private int sequence;

    public long EpochMillis { get; } = epochMillis ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public string ClaimSegmentName()
    {
        lock (gate)
        {
            var name = $"seg-{EpochMillis}-{nextIndex}.ts";
            nextIndex++;
            return name;
        }
    }

    public int ResumeSequence
    {
        get { lock (gate) return sequence; }
    }

    public void RecordSequence(int value)
    {
        lock (gate) sequence = Math.Max(sequence, value);
    }
}
