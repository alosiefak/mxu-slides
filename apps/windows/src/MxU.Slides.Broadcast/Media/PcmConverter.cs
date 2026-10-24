namespace MxU.Slides.Broadcast.Media;

/// <summary>
/// Prepares program audio (interleaved 32-bit float, any rate and channel count) for the
/// Windows AAC encoder, which only takes 16-bit PCM at 44.1 or 48 kHz in mono or stereo: keeps
/// the first two channels (as the Mac's converter does for <c>min(2, channels)</c>), resamples
/// other rates to 48 kHz with a simple linear interpolator that carries its position across
/// buffers, and clamps to 16 bits. Not thread-safe; one per encoder.
/// </summary>
internal sealed class PcmConverter
{
    private readonly int inputChannels;
    private readonly double step;
    private double phase;
    private float[] previous;
    private bool primed;

    public PcmConverter(int inputSampleRate, int inputChannels)
        : this(inputSampleRate, inputChannels,
            inputSampleRate is 44100 or 48000 ? inputSampleRate : 48000,
            Math.Min(2, Math.Max(1, inputChannels)))
    {
    }

    /// <summary>
    /// Converts to a fixed output format (a recording's audio track is set up before any audio
    /// arrives). Mono input is copied to both channels of a stereo output.
    /// </summary>
    public PcmConverter(int inputSampleRate, int inputChannels, int outputSampleRate, int outputChannels)
    {
        this.inputChannels = Math.Max(1, inputChannels);
        InputSampleRate = inputSampleRate;
        OutputChannels = Math.Max(1, outputChannels);
        OutputSampleRate = outputSampleRate;
        step = (double)inputSampleRate / OutputSampleRate;
        previous = new float[OutputChannels];
    }

    private int Source(int channel) => Math.Min(channel, inputChannels - 1);

    public int InputSampleRate { get; }

    public int OutputSampleRate { get; }

    public int OutputChannels { get; }

    /// <summary>Converts one buffer; returns interleaved 16-bit samples (possibly empty while resampling primes).</summary>
    public short[] Convert(ReadOnlySpan<float> interleaved)
    {
        var frames = interleaved.Length / inputChannels;
        if (frames == 0) return [];
        if (OutputSampleRate == InputSampleRate)
        {
            var output = new short[frames * OutputChannels];
            for (var f = 0; f < frames; f++)
                for (var c = 0; c < OutputChannels; c++)
                    output[f * OutputChannels + c] = ToInt16(interleaved[f * inputChannels + Source(c)]);
            return output;
        }
        return Resample(interleaved, frames);
    }

    private short[] Resample(ReadOnlySpan<float> interleaved, int frames)
    {
        if (!primed)
        {
            for (var c = 0; c < OutputChannels; c++) previous[c] = interleaved[Source(c)];
            primed = true;
        }
        var results = new List<short>((int)(frames / step * OutputChannels) + OutputChannels * 2);
        // "phase" is the read position measured from the previous buffer's last frame (index -1 here).
        while (phase < frames)
        {
            var index = (int)Math.Floor(phase) - 1;
            var fraction = (float)(phase - Math.Floor(phase));
            for (var c = 0; c < OutputChannels; c++)
            {
                var a = index < 0 ? previous[c] : interleaved[index * inputChannels + Source(c)];
                var b = interleaved[(index + 1) * inputChannels + Source(c)];
                results.Add(ToInt16(a + (b - a) * fraction));
            }
            phase += step;
        }
        phase -= frames;
        for (var c = 0; c < OutputChannels; c++) previous[c] = interleaved[(frames - 1) * inputChannels + Source(c)];
        return [.. results];
    }

    private static short ToInt16(float sample) => (short)Math.Clamp(MathF.Round(sample * 32767f), -32768f, 32767f);
}

/// <summary>
/// Gives each AAC frame its time. An AAC frame is always 1024 samples, so frame n plays at
/// anchor + n × 1024 / rate. The anchor is taken from the first buffer and moved only when the
/// incoming audio drifts more than 0.25 s from where the count says it should be (a gap in
/// the program audio, say), so small jitter in buffer timestamps never reaches the stream.
/// Port of the timing half of the Mac's HLSAudioEncoder.
/// </summary>
internal sealed class AacPacketClock(int sampleRate)
{
    public const double PacketFrames = 1024;

    private double? anchor;
    private long packetsOut;

    public int SampleRate { get; } = sampleRate;

    /// <summary>Call with each input buffer's host time before encoding it.</summary>
    public void Observe(double incomingSeconds, long pendingFrames = 0)
    {
        // pendingFrames: samples already handed to the encoder that haven't come out yet.
        var expected = (anchor ?? incomingSeconds) + (packetsOut * PacketFrames + pendingFrames) / SampleRate;
        if (anchor is null || Math.Abs(incomingSeconds - expected) > 0.25)
            anchor = incomingSeconds - (packetsOut * PacketFrames + pendingFrames) / SampleRate;
    }

    /// <summary>The time of the next encoded frame (and counts it).</summary>
    public double Next()
    {
        var seconds = (anchor ?? 0) + packetsOut * PacketFrames / SampleRate;
        packetsOut++;
        return seconds;
    }
}
