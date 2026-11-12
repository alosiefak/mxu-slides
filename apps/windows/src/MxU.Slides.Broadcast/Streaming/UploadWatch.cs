namespace MxU.Slides.Broadcast.Streaming;

/// <summary>
/// Notices an upload that has stopped moving, so the stream reconnects instead of sitting
/// "live" while nothing reaches the platform: nothing sent for <see cref="SilentSeconds"/>
/// seconds in a row, or more than <see cref="BacklogSeconds"/> seconds of the stream waiting to
/// be sent. Fed once a second with what left the PC and what is still queued. Not
/// thread-safe: one per connection, fed from one place. Port of UploadStall in the Mac's
/// StreamEngine/UploadWatch.swift.
/// </summary>
public sealed class UploadStall
{
    public const int SilentSeconds = 10;
    public const int BacklogSeconds = 20;

    private int silent;

    public int BytesOutPerSecond { get; private set; }

    public int QueuedBytes { get; private set; }

    public void Record(int bytesOutPerSecond, int queuedBytes)
    {
        BytesOutPerSecond = bytesOutPerSecond;
        QueuedBytes = queuedBytes;
        silent = bytesOutPerSecond > 0 ? 0 : silent + 1;
    }

    /// <summary>Why the upload counts as stalled (for the log and the status line), or null.</summary>
    public string? Stalled(int targetKbps)
    {
        if (silent >= SilentSeconds) return $"nothing sent for {silent}s";
        if (QueuedBytes > (long)targetKbps * 125 * BacklogSeconds) return $"upload {QueuedBytes / 1_000_000} MB behind";
        return null;
    }
}

/// <summary>One second of the network, as HaishinKit's NetworkMonitor reports it.</summary>
internal readonly record struct NetworkReport(int BytesOutPerSecond, int QueuedBytes);

internal enum NetworkEvent
{
    /// <summary>A normal second.</summary>
    Status,

    /// <summary>The queue grew every second for the last few: the upload can't keep up.</summary>
    InsufficientBandwidth,
}

/// <summary>
/// Turns the publisher's running byte counts into one report a second, and says when the
/// bandwidth is insufficient: the send queue grew in each of the last
/// <see cref="MeasureInterval"/> − 1 steps. Port of HaishinKit 2.2's NetworkMonitor (the Mac's
/// RTMPStream runs one; the Windows publisher has no monitor of its own).
/// </summary>
internal sealed class NetworkMonitor
{
    public const int MeasureInterval = 3;

    private readonly Queue<long> previousQueueBytesOut = new();
    private long previousTotalBytesOut;
    private bool started;

    public (NetworkEvent Event, NetworkReport Report) Collect(long totalBytesOut, long queueBytesOut, double seconds = 1)
    {
        var sent = started ? totalBytesOut - previousTotalBytesOut : 0;
        started = true;
        previousTotalBytesOut = totalBytesOut;
        var report = new NetworkReport(
            (int)Math.Clamp(sent / Math.Max(seconds, 1e-3), 0, int.MaxValue),
            (int)Math.Clamp(queueBytesOut, 0, int.MaxValue));
        previousQueueBytesOut.Enqueue(queueBytesOut);
        if (previousQueueBytesOut.Count < MeasureInterval) return (NetworkEvent.Status, report);
        var samples = previousQueueBytesOut.ToArray();
        previousQueueBytesOut.Dequeue();
        var growing = 0;
        for (var i = 0; i < samples.Length - 1; i++)
            if (samples[i] < samples[i + 1]) growing++;
        return (growing == MeasureInterval - 1 ? NetworkEvent.InsufficientBandwidth : NetworkEvent.Status, report);
    }
}

/// <summary>
/// HaishinKit's StreamVideoAdaptiveBitRateStrategy, which the Mac's Adaptive mode uses. When
/// the upload can't keep up, the video bitrate drops to what actually left the PC last second
/// (less the audio), never below a tenth of the full rate; after
/// <see cref="SufficientBandwidthCountsThreshold"/> good seconds in a row it climbs back a tenth
/// of the full rate, and so on until it is full again. (HaishinKit also halves the frame rate
/// after seconds with nothing sent at all; Windows keeps the frame rate, as the stall check
/// reconnects soon after.)
/// </summary>
internal sealed class AdaptiveBitRateStrategy(int maximumVideoBitRate, int audioBitRate)
{
    public const int SufficientBandwidthCountsThreshold = 15;

    private int sufficientBandwidthCounts;
    private int zeroBytesOutPerSecondCounts;

    public int MaximumVideoBitRate { get; } = maximumVideoBitRate;

    /// <summary>The bitrate (bits a second) to use after this second, given the one in use.</summary>
    public int Adjust(NetworkEvent networkEvent, NetworkReport report, int bitRate)
    {
        switch (networkEvent)
        {
            case NetworkEvent.Status:
                if (bitRate == MaximumVideoBitRate) return bitRate;
                if (SufficientBandwidthCountsThreshold <= sufficientBandwidthCounts)
                {
                    sufficientBandwidthCounts = 0;
                    return Math.Min(bitRate + MaximumVideoBitRate / 10, MaximumVideoBitRate);
                }
                sufficientBandwidthCounts++;
                return bitRate;
            default:
                sufficientBandwidthCounts = 0;
                if (report.BytesOutPerSecond > 0)
                {
                    var measured = (int)Math.Min((long)report.BytesOutPerSecond * 8 / (zeroBytesOutPerSecondCounts + 1), int.MaxValue);
                    zeroBytesOutPerSecondCounts = 0;
                    return Math.Max(measured - audioBitRate, MaximumVideoBitRate / 10);
                }
                zeroBytesOutPerSecondCounts++;
                return bitRate;
        }
    }
}

/// <summary>
/// Watches one RTMP connection's upload, once a second: it keeps the <see cref="UploadStall"/>
/// check, and in Adaptive mode (a floor was given) it steps the video bitrate down when the
/// upload can't keep up and back up when it can, never below the floor. The stream's video
/// pump reads <see cref="VideoBitRate"/> and gives it to the encoder. Port of the Mac's
/// UploadWatch actor, which HaishinKit calls as its bitrate strategy.
/// </summary>
internal sealed class UploadWatch
{
    private readonly NetworkMonitor monitor = new();
    private readonly AdaptiveBitRateStrategy? adaptive;
    private readonly int floor;
    private volatile int videoBitRate;

    /// <param name="videoBitRate">The full video bitrate, in bits a second.</param>
    /// <param name="floorBitRate">The lowest Adaptive may go, or null for Constant.</param>
    /// <param name="audioBitRate">The audio's bitrate, which shares the upload.</param>
    public UploadWatch(int videoBitRate, int? floorBitRate, int audioBitRate = 128_000)
    {
        MaximumVideoBitRate = videoBitRate;
        this.videoBitRate = videoBitRate;
        floor = Math.Min(floorBitRate ?? videoBitRate, videoBitRate);
        adaptive = floorBitRate is null ? null : new AdaptiveBitRateStrategy(videoBitRate, audioBitRate);
    }

    public int MaximumVideoBitRate { get; }

    public bool IsAdaptive => adaptive is not null;

    public UploadStall Stall { get; } = new();

    /// <summary>The video bitrate wanted now (bits a second). Read from any thread.</summary>
    public int VideoBitRate => videoBitRate;

    public int VideoKbps => videoBitRate / 1000;

    /// <summary>One second of the network: the publisher's total bytes sent and bytes still queued.</summary>
    public void Tick(long totalBytesOut, long queuedBytes, double seconds = 1)
    {
        var (networkEvent, report) = monitor.Collect(totalBytesOut, queuedBytes, seconds);
        if (adaptive is not null)
            videoBitRate = Math.Max(adaptive.Adjust(networkEvent, report, videoBitRate), floor);
        Stall.Record(report.BytesOutPerSecond, report.QueuedBytes);
    }
}
