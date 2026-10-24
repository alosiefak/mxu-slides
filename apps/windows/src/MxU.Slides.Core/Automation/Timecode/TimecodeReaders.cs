namespace MxU.Slides.Core.Automation;

/// <summary>
/// A timecode input's reader: collects readings on the device's thread, hands them over
/// on the show's thread. Port of the TimecodeReader protocol in Sources/MTCInput.swift.
/// </summary>
public interface ITimecodeReader
{
    /// <summary>The readings since the last drain, and the loudest sample heard (for the meter).</summary>
    (IReadOnlyList<TimecodeHeard> Heard, float Peak) Drain();

    /// <summary>Changes the audio channel LTC is read from (MTC readers ignore it).</summary>
    void SetChannel(int channel) { }

    /// <summary>Lets go of the device.</summary>
    void Stop();
}

/// <summary>Opens the timecode inputs. The WPF app implements it over its audio capture and MIDI input.</summary>
public interface ITimecodeInputs
{
    /// <summary>Starts reading LTC from one channel (0-based) of an audio input; throws when the device can't be opened.</summary>
    ITimecodeReader OpenLtc(string deviceId, int channel);

    /// <summary>Starts reading MTC from a MIDI input.</summary>
    ITimecodeReader OpenMtc(string sourceId);
}

/// <summary>
/// The decoding half of an LTC input: feed it the channel's samples from the audio
/// callback (any thread) and it decodes them, timing each frame by where it ended in the
/// buffer. Thread-safe between <see cref="Read"/> and <see cref="Drain"/>. Port of
/// LTCReader.read in Sources/TimecodeInput.swift. <paramref name="onStop"/> releases the device.
/// </summary>
public sealed class LtcReader(Action? onStop = null, Action<int>? onChannel = null) : ITimecodeReader
{
    /// <summary>Readings kept between drains (about three seconds); older ones are dropped.</summary>
    public const int Backlog = 90;

    private readonly Lock gate = new();
    private readonly List<TimecodeHeard> heard = [];
    private float peak;
    private LtcDecoder decoder = new(48_000);

    /// <summary>
    /// Decodes a buffer of one channel's samples. <paramref name="hostSeconds"/> is the host
    /// time of the buffer's first sample (seconds, the same clock the show runs on).
    /// </summary>
    public void Read(ReadOnlySpan<float> samples, double sampleRate, double hostSeconds)
    {
        if (samples.IsEmpty) return;
        if (decoder.SampleRate != sampleRate) decoder = new LtcDecoder(sampleRate);
        var start = decoder.SampleCount;
        var frames = decoder.Decode(samples);
        var level = 0f;
        foreach (var sample in samples) level = Math.Max(level, Math.Abs(sample));
        var rate = decoder.Rate ?? Stage.TimecodeRate.Fps30;
        var fresh = frames.Select(f => TimecodeHeard.FromLtc(f, rate, hostSeconds + (f.EndSample - start) / sampleRate)).ToList();
        lock (gate)
        {
            heard.AddRange(fresh);
            if (heard.Count > Backlog) heard.RemoveRange(0, heard.Count - Backlog);
            peak = Math.Max(peak, level);
        }
    }

    public (IReadOnlyList<TimecodeHeard> Heard, float Peak) Drain()
    {
        lock (gate)
        {
            var result = (heard.ToList(), peak);
            heard.Clear();
            peak = 0;
            return result;
        }
    }

    public void SetChannel(int channel) => onChannel?.Invoke(channel);

    public void Stop() => onStop?.Invoke();
}

/// <summary>
/// The decoding half of an MTC input: feed it the MIDI bytes as they arrive. Its meter
/// reads full while timecode arrives. Port of MTCReader in Sources/MTCInput.swift.
/// </summary>
public sealed class MtcReader(Action? onStop = null) : ITimecodeReader
{
    private readonly Lock gate = new();
    private readonly List<TimecodeHeard> heard = [];
    private readonly MtcDecoder decoder = new();

    /// <summary>Reads MIDI bytes stamped with the host time they arrived (seconds).</summary>
    public void Read(IReadOnlyList<byte> bytes, double hostTime)
    {
        List<TimecodeHeard> frames;
        lock (gate) frames = decoder.Receive(bytes, hostTime);
        if (frames.Count == 0) return;
        lock (gate)
        {
            heard.AddRange(frames);
            if (heard.Count > LtcReader.Backlog) heard.RemoveRange(0, heard.Count - LtcReader.Backlog);
        }
    }

    public (IReadOnlyList<TimecodeHeard> Heard, float Peak) Drain()
    {
        lock (gate)
        {
            var result = (heard.ToList(), heard.Count == 0 ? 0f : 1f);
            heard.Clear();
            return result;
        }
    }

    public void Stop() => onStop?.Invoke();
}
