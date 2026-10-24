using MxU.Slides.Core.Stage;

namespace MxU.Slides.Core.Automation;

/// <summary>One LTC frame read off the wire: its label, its flags, and the sample it ended on.</summary>
public readonly record struct LtcFrame(TimecodeLabel Label, bool DropFrame = false, bool Reverse = false, int EndSample = 0);

/// <summary>
/// Decodes SMPTE linear timecode (LTC) from audio samples. LTC is a square-ish wave
/// carrying 80 bits per frame in biphase-mark code: every bit cell starts with an edge,
/// and a 1 has an extra edge in the middle. The decoder follows the signal's level,
/// measures the gaps between edges to tell long cells (0) from pairs of half cells (1),
/// and looks for the 16-bit sync word that ends each frame (either way round: tape
/// played backwards sends it reversed). It also works out the frame rate from how many
/// samples each frame takes. Feed it samples in any chunk size; it keeps its state.
/// Port of LTCDecoder in PresenterCore/Timeline/LTCDecoder.swift (the arithmetic is in
/// single precision, as on the Mac, so both read a signal the same way).
/// </summary>
public sealed class LtcDecoder
{
    public LtcDecoder(double sampleRate)
    {
        SampleRate = sampleRate;
        // A bit cell is 1/80 of a frame; start at a 27 fps guess and allow 18 to 40.
        period = sampleRate / (80 * 27);
        minPeriod = sampleRate / (80 * 40);
        maxPeriod = sampleRate / (80 * 18);
    }

    public double SampleRate { get; }

    // Level tracking: a slow DC filter, a decaying peak envelope, and which side of zero the wave is on.
    private float dc;
    private float envelope;
    private bool high;

    /// <summary>No edge has been seen since the signal (re)appeared, so the next crossing just sets the side.</summary>
    private bool idle = true;

    /// <summary>Below this peak level the input counts as silence.</summary>
    internal const float Gate = 0.003f;

    private int sinceEdge;

    /// <summary>The bit cell length in samples, learned from the signal.</summary>
    private double period;
    private readonly double minPeriod;
    private readonly double maxPeriod;
    private bool halfBit;

    /// <summary>A bit cell has started (an edge was seen), so the next edge ends a cell or half of one.</summary>
    private bool cellOpen;

    // The last 80 bits, oldest first: 64 in `older`, the newest 16 in `newer`.
    private ulong older;
    private ushort newer;
    private int bitCount;

    /// <summary>Samples decoded so far (frames' <see cref="LtcFrame.EndSample"/> count from here).</summary>
    public int SampleCount { get; private set; }

    private LtcFrame? lastForward;
    private double samplesPerFrame;
    private int measuredFrames;
    private int highestFrameLabel;

    /// <summary>The frame rate worked out so far (null until a frame has been read).</summary>
    public TimecodeRate? Rate { get; private set; }

    internal const ushort ForwardSync = 0xBFFC;
    internal const ulong ReverseSync = 0x3FFD;

    /// <summary>Decodes a chunk of mono samples (full scale ±1) and returns the frames that ended in it.</summary>
    public List<LtcFrame> Decode(ReadOnlySpan<float> samples)
    {
        var frames = new List<LtcFrame>();
        var decay = (float)Math.Pow(0.5, 1 / (0.02 * SampleRate));
        var dcRate = (float)(1 / (0.05 * SampleRate));
        foreach (var sample in samples)
        {
            SampleCount++;
            sinceEdge++;

            // A cell left open too long: the signal stopped mid-cell, so finish the bit.
            if (cellOpen && sinceEdge > period * 1.5)
            {
                cellOpen = false;
                idle = true;
                var bit = halfBit ? (ushort)1 : (ushort)0;
                halfBit = false;
                if (Push(bit) is { } frame) frames.Add(frame);
            }
            dc += (sample - dc) * dcRate;
            var value = sample - dc;
            if (Math.Abs(value) > envelope * 4 && Math.Abs(value) > Gate * 4)
            {
                // A sudden jump in level (a new source): start reading afresh.
                idle = true;
                cellOpen = false;
                halfBit = false;
            }
            envelope = Math.Max(Math.Abs(value), envelope * decay);
            if (!(envelope > Gate))
            {
                halfBit = false;
                idle = true;
                continue;
            }

            var threshold = envelope * 0.33f;
            if (high && value < -threshold)
                high = false;
            else if (!high && value > threshold)
                high = true;
            else if (idle && Math.Abs(value) > threshold)
                high = value > 0;
            else
                continue;
            idle = false;

            // Ignore a second crossing too soon after the last (noise on the edge).
            if (cellOpen && sinceEdge < period * 0.3) continue;
            if (Edge(sinceEdge) is { } decoded) frames.Add(decoded);
            sinceEdge = 0;
        }
        return frames;
    }

    private LtcFrame? Edge(int interval)
    {
        var gap = (double)interval;
        try
        {
            if (!(cellOpen && gap < period * 2.5))
            {
                halfBit = false;
                return null;
            }
            if (gap > period * 0.75)
            {
                // A whole cell: a 0.
                halfBit = false;
                period = Math.Min(Math.Max((period * 3 + gap) / 4, minPeriod), maxPeriod);
                return Push(0);
            }
            // Half a cell: the second half makes a 1.
            period = Math.Min(Math.Max((period * 3 + gap * 2) / 4, minPeriod), maxPeriod);
            if (halfBit)
            {
                halfBit = false;
                return Push(1);
            }
            halfBit = true;
            return null;
        }
        finally
        {
            cellOpen = true;
        }
    }

    private LtcFrame? Push(ushort bit)
    {
        older = (older >> 1) | ((ulong)(newer & 1) << 63);
        newer = (ushort)((newer >> 1) | (bit << 15));
        bitCount++;
        if (bitCount < 80) return null;
        if (newer == ForwardSync) return Frame(older, reverse: false);
        if ((older & 0xFFFF) == ReverseSync)
        {
            // Played backwards: the bits arrived last-first, so read the register in reverse.
            ulong data = 0;
            for (var bitIndex = 0; bitIndex < 64; bitIndex++)
                if (RegisterBit(79 - bitIndex)) data |= 1UL << bitIndex;
            return Frame(data, reverse: true);
        }
        return null;
    }

    private bool RegisterBit(int index) =>
        index < 64 ? ((older >> index) & 1) == 1 : ((newer >> (index - 64)) & 1) == 1;

    private LtcFrame? Frame(ulong data, bool reverse)
    {
        int Field(int start, int width) => (int)((data >> start) & ((1UL << width) - 1));
        int[] units = [Field(0, 4), Field(16, 4), Field(32, 4), Field(48, 4)];
        if (!units.All(u => u <= 9)) return null;
        var label = new TimecodeLabel(
            Field(56, 2) * 10 + units[3], Field(40, 3) * 10 + units[2],
            Field(24, 3) * 10 + units[1], Field(8, 2) * 10 + units[0]);
        if (label.Hours >= 24 || label.Minutes >= 60 || label.Seconds >= 60 || label.Frames >= 30) return null;
        bitCount = 0;
        var decoded = new LtcFrame(label, Field(10, 1) == 1, reverse, SampleCount);
        DetectRate(decoded);
        return decoded;
    }

    /// <summary>
    /// The rate: drop-frame says 29.97 DF outright; otherwise the samples per frame (once
    /// eight frames are measured, 60 to tell 29.97 from 30), else the highest frame label seen.
    /// </summary>
    private void DetectRate(LtcFrame frame)
    {
        highestFrameLabel = Math.Max(highestFrameLabel, frame.Label.Frames);
        if (lastForward is { } last && !frame.Reverse)
        {
            var samples = (double)(frame.EndSample - last.EndSample);
            // A plausible frame (60 to 100 cells): average it in.
            if (samples > period * 60 && samples < period * 100)
            {
                measuredFrames++;
                var weight = 1 / (double)Math.Min(measuredFrames, 128);
                samplesPerFrame += (samples - samplesPerFrame) * weight;
            }
        }
        if (frame.DropFrame)
        {
            Rate = TimecodeRate.Fps2997DropFrame;
        }
        else if (measuredFrames >= 8)
        {
            var fps = SampleRate / samplesPerFrame;
            Rate = fps < 24.5 ? TimecodeRate.Fps24
                : fps < 27.5 ? TimecodeRate.Fps25
                : measuredFrames >= 60 && fps < 29.985 ? TimecodeRate.Fps2997
                : TimecodeRate.Fps30;
        }
        else
        {
            Rate = highestFrameLabel >= 25 ? TimecodeRate.Fps30 : highestFrameLabel == 24 ? TimecodeRate.Fps25 : Rate ?? TimecodeRate.Fps30;
        }
        lastForward = frame.Reverse ? null : frame;
    }
}

/// <summary>Timeline timecode starts, which are stored in 30 fps frames, read as a label at any rate.</summary>
public static class TimecodeStarts
{
    /// <summary>Port of TimecodeRate.label(startFrames:): whole seconds kept, the frames scaled to this rate.</summary>
    public static TimecodeLabel LabelForStartFrames(this TimecodeRate rate, int startFrames)
    {
        var fps = PresentationTimelines.FramesPerSecond;
        var total = Math.Max(startFrames, 0);
        var seconds = total / fps;
        return new TimecodeLabel(seconds / 3600, seconds / 60 % 60, seconds % 60, total % fps * rate.Nominal() / fps);
    }
}
