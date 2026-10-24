using MxU.Slides.Core.Media;

namespace MxU.Slides.Audio;

/// <summary>
/// A block of audio held one array per channel ("planar"), the shape the engine mixes
/// in. Port of the deinterleaved AVAudioPCMBuffer the Mac engine passes around:
/// <see cref="Capacity"/> frames are allocated, the first <see cref="FrameLength"/> are valid.
/// </summary>
public sealed class PcmBuffer
{
    public PcmBuffer(int sampleRate, int channels, int capacity)
    {
        if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));
        SampleRate = sampleRate;
        Capacity = Math.Max(0, capacity);
        Data = new float[channels][];
        for (var channel = 0; channel < channels; channel++) Data[channel] = new float[Capacity];
    }

    /// <summary>Wraps existing per-channel arrays without copying (the engine's own mix arrays).</summary>
    internal PcmBuffer(int sampleRate, float[][] data)
    {
        SampleRate = sampleRate;
        Data = data;
        Capacity = data.Length == 0 ? 0 : data.Min(channel => channel.Length);
    }

    public int SampleRate { get; }

    public int ChannelCount => Data.Length;

    public int Capacity { get; }

    /// <summary>How many frames (one sample per channel) are filled in.</summary>
    public int FrameLength
    {
        get => frameLength;
        set => frameLength = Math.Clamp(value, 0, Capacity);
    }

    private int frameLength;

    /// <summary>The samples, <c>Data[channel][frame]</c>.</summary>
    public float[][] Data { get; }

    public float this[int channel, int frame]
    {
        get => Data[channel][frame];
        set => Data[channel][frame] = value;
    }

    /// <summary>A copy that shares no arrays with this one.</summary>
    public PcmBuffer Copy()
    {
        var copy = new PcmBuffer(SampleRate, ChannelCount, Capacity) { FrameLength = FrameLength };
        for (var channel = 0; channel < ChannelCount; channel++)
            Array.Copy(Data[channel], copy.Data[channel], FrameLength);
        return copy;
    }

    /// <summary>Splits interleaved samples (L R L R …) into a new planar buffer.</summary>
    public static PcmBuffer FromInterleaved(ReadOnlySpan<float> samples, int channels, int sampleRate)
    {
        var frames = channels == 0 ? 0 : samples.Length / channels;
        var buffer = new PcmBuffer(sampleRate, channels, frames) { FrameLength = frames };
        for (var frame = 0; frame < frames; frame++)
            for (var channel = 0; channel < channels; channel++)
                buffer.Data[channel][frame] = samples[frame * channels + channel];
        return buffer;
    }

    /// <summary>Writes the valid frames interleaved into <paramref name="destination"/>.</summary>
    public void CopyInterleaved(Span<float> destination)
    {
        var channels = ChannelCount;
        for (var frame = 0; frame < FrameLength; frame++)
            for (var channel = 0; channel < channels; channel++)
                destination[frame * channels + channel] = Data[channel][frame];
    }

    /// <summary>The Core program-feed shape: interleaved, stamped on the host clock.</summary>
    public AudioBuffer ToAudioBuffer(double hostTime)
    {
        var interleaved = new float[FrameLength * ChannelCount];
        CopyInterleaved(interleaved);
        return new AudioBuffer(SampleRate, ChannelCount, interleaved, hostTime);
    }
}
