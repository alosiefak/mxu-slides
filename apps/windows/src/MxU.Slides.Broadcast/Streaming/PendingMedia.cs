using System.Buffers;
using System.Threading.Channels;
using MxU.Slides.Core.Media;

namespace MxU.Slides.Broadcast.Streaming;

/// <summary>
/// A program frame copied out of the app's buffer (the original is only valid during
/// <see cref="IProgramSink.WriteVideo"/>), waiting for the encoder thread. The pixels come from
/// a shared pool and must go back with <see cref="Return"/> once used or dropped.
/// </summary>
internal sealed class PendingVideoFrame
{
    public required byte[] Pixels { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public required int Stride { get; init; }

    public required double HostTime { get; init; }

    public static PendingVideoFrame Copy(VideoFrame frame)
    {
        var length = frame.Stride * (frame.Height - 1) + frame.Width * 4;
        var pixels = ArrayPool<byte>.Shared.Rent(length);
        frame.Pixels.Span[..length].CopyTo(pixels);
        return new PendingVideoFrame { Pixels = pixels, Width = frame.Width, Height = frame.Height, Stride = frame.Stride, HostTime = frame.HostTime };
    }

    public ReadOnlySpan<byte> Span => Pixels.AsSpan(0, Stride * (Height - 1) + Width * 4);

    public void Return() => ArrayPool<byte>.Shared.Return(Pixels);
}

/// <summary>Program audio copied out of the app's buffer, waiting for the encoder. Pooled like <see cref="PendingVideoFrame"/>.</summary>
internal sealed class PendingAudioBuffer
{
    public required float[] Samples { get; init; }

    public required int Length { get; init; }

    public required int SampleRate { get; init; }

    public required int Channels { get; init; }

    public required double HostTime { get; init; }

    public static PendingAudioBuffer Copy(AudioBuffer buffer)
    {
        var samples = ArrayPool<float>.Shared.Rent(buffer.Samples.Length);
        buffer.Samples.Span.CopyTo(samples);
        return new PendingAudioBuffer
        {
            Samples = samples, Length = buffer.Samples.Length, SampleRate = buffer.SampleRate, Channels = buffer.Channels, HostTime = buffer.HostTime,
        };
    }

    public ReadOnlySpan<float> Span => Samples.AsSpan(0, Length);

    public void Return() => ArrayPool<float>.Shared.Return(Samples);
}

internal static class PendingQueues
{
    /// <summary>
    /// A queue that keeps only the newest <paramref name="capacity"/> items: when full, the
    /// oldest is dropped (and <paramref name="dropped"/> called), so a slow encoder sees fresh
    /// frames and the app's frame thread never waits. The Mac's "bufferingNewest" streams.
    /// </summary>
    public static Channel<T> Newest<T>(int capacity, Action<T> dropped) =>
        Channel.CreateBounded(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        }, dropped);

    /// <summary>Returns every buffer still in a finished queue to the pool.</summary>
    public static void Drain(Channel<PendingVideoFrame>? frames, Channel<PendingAudioBuffer>? audio)
    {
        frames?.Writer.TryComplete();
        audio?.Writer.TryComplete();
        while (frames is not null && frames.Reader.TryRead(out var frame)) frame.Return();
        while (audio is not null && audio.Reader.TryRead(out var buffer)) buffer.Return();
    }
}
