namespace MxU.Slides.Audio;

/// <summary>
/// A lip-sync delay (0–2000 ms) for a stream of buffers: each sample comes out that
/// much later. Changing the delay starts a fresh, silent ring, so it never replays stale
/// audio. Process buffers from one thread; the delay can be set from any. Port of AudioDelayLine.swift.
/// </summary>
public sealed class AudioDelayLine
{
    private readonly Lock gate = new();
    private double requestedMs;

    private float[][] rings = [];
    private int writeIndex;
    private int appliedSamples;
    private int appliedRate;

    public double DelayMilliseconds
    {
        get { lock (gate) return requestedMs; }
        set { lock (gate) requestedMs = Math.Clamp(value, 0, 2000); }
    }

    /// <summary>
    /// A delayed copy, leaving <paramref name="buffer"/> untouched (it may be shared with
    /// other consumers). With no delay the same buffer comes back and nothing is copied.
    /// </summary>
    public PcmBuffer DelayedCopy(PcmBuffer buffer)
    {
        if (!(DelayMilliseconds > 0 || appliedSamples > 0)) return buffer;
        var copy = buffer.Copy();
        Process(copy);
        return copy;
    }

    /// <summary>Delays the first two channels of <paramref name="buffer"/> in place.</summary>
    public void Process(PcmBuffer buffer)
    {
        var rate = buffer.SampleRate;
        if (rate <= 0) return;
        double ms;
        lock (gate) ms = requestedMs;
        var target = (int)(ms * rate / 1000);
        if (target != appliedSamples || rate != appliedRate)
        {
            appliedSamples = target;
            appliedRate = rate;
            writeIndex = 0;
            rings = target > 0 ? [new float[target], new float[target]] : [];
        }
        if (appliedSamples <= 0) return;
        var frames = buffer.FrameLength;
        var channels = Math.Min(2, buffer.ChannelCount);
        var nextIndex = writeIndex;
        for (var channel = 0; channel < channels; channel++)
        {
            var samples = buffer.Data[channel];
            var ring = rings[channel];
            var index = writeIndex;
            for (var frame = 0; frame < frames; frame++)
            {
                var delayed = ring[index];
                ring[index] = samples[frame];
                samples[frame] = delayed;
                if (++index == ring.Length) index = 0;
            }
            nextIndex = index;
        }
        writeIndex = nextIndex;
    }
}
