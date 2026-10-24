using System.Globalization;

namespace MxU.Slides.Audio;

/// <summary>One meter reading: the average power (RMS) and the loudest sample, both 0…1.</summary>
public readonly record struct LevelReading(float Rms, float Peak)
{
    public static readonly LevelReading Silence = new(0, 0);
}

/// <summary>
/// Meter and fader maths shared by every strip: measuring buffers, the decibel face the
/// meters draw on, the fader taper, and how levels sum when sources land on one mix.
/// Port of AudioLevel.swift.
/// </summary>
public static class AudioLevel
{
    /// <summary>Left and right measured separately; a mono buffer reads the same on both sides.</summary>
    public static (LevelReading Left, LevelReading Right) MeasureSides(PcmBuffer buffer)
    {
        if (buffer.FrameLength == 0) return (LevelReading.Silence, LevelReading.Silence);
        var frames = buffer.FrameLength;
        LevelReading Side(float[] samples)
        {
            float sumSquares = 0, peak = 0;
            for (var frame = 0; frame < frames; frame++)
            {
                var sample = samples[frame];
                sumSquares += sample * sample;
                peak = MathF.Max(peak, MathF.Abs(sample));
            }
            return new LevelReading(MathF.Min(MathF.Sqrt(sumSquares / frames), 1), MathF.Min(peak, 1));
        }
        var left = Side(buffer.Data[0]);
        return (left, buffer.ChannelCount > 1 ? Side(buffer.Data[1]) : left);
    }

    /// <summary>One reading across every channel.</summary>
    public static LevelReading Measure(PcmBuffer buffer)
    {
        if (buffer.FrameLength == 0) return LevelReading.Silence;
        var frames = buffer.FrameLength;
        var channels = buffer.ChannelCount;
        float sumSquares = 0, peak = 0;
        for (var channel = 0; channel < channels; channel++)
        {
            var samples = buffer.Data[channel];
            for (var frame = 0; frame < frames; frame++)
            {
                var sample = samples[frame];
                sumSquares += sample * sample;
                var magnitude = MathF.Abs(sample);
                if (magnitude > peak) peak = magnitude;
            }
        }
        var rms = MathF.Sqrt(sumSquares / (frames * channels));
        return new LevelReading(MathF.Min(rms, 1), MathF.Min(peak, 1));
    }

    /// <summary>dBFS of a linear level (clipped at 0 dB); silence is minus infinity.</summary>
    public static float Decibels(float linear)
    {
        if (!(linear > 0)) return float.NegativeInfinity;
        return 20 * MathF.Log10(MathF.Min(linear, 1));
    }

    // The meter face: the top 24 dB take 62% of the height, -24…-60 dB the next 32%,
    // and a short "basement" down to -90 dB shows that something is there at all.
    internal const float FloorDecibels = -60;
    internal const float KneeDecibels = -24;
    internal const float KneeFraction = 0.38f;
    internal const float BasementDecibels = -90;
    internal const float BasementFraction = 0.06f;

    /// <summary>How far up the meter (0…1) a linear level draws.</summary>
    public static float MeterFraction(float linear)
    {
        if (!(linear > 0)) return 0;
        return MeterFractionForDecibels(Decibels(linear));
    }

    /// <summary>How far up the meter (0…1) a level in dBFS draws.</summary>
    public static float MeterFractionForDecibels(float db)
    {
        if (!(db > float.NegativeInfinity)) return 0;
        var clamped = MathF.Min(db, 0);
        if (clamped >= KneeDecibels)
            return KneeFraction + (clamped - KneeDecibels) / -KneeDecibels * (1 - KneeFraction);
        if (clamped >= FloorDecibels)
            return BasementFraction + (clamped - FloorDecibels) / (KneeDecibels - FloorDecibels) * (KneeFraction - BasementFraction);
        if (!(clamped > BasementDecibels)) return 0;
        return (clamped - BasementDecibels) / (FloorDecibels - BasementDecibels) * BasementFraction;
    }

    // The fader taper: the top 60% of travel covers 0…-30 dB (where mixing happens),
    // the next 35% -30…-60 dB, and the last 5% plunges to -90 dB and off.
    internal const float TaperKneeDecibels = -30;
    internal const float TaperKneeFraction = 0.4f;
    internal const float PlungeFraction = 0.05f;

    /// <summary>The linear gain for a fader position (0…1).</summary>
    public static float FaderGain(float fraction)
    {
        var f = MathF.Min(MathF.Max(fraction, 0), 1);
        if (!(f > 0)) return 0;
        float db;
        if (f >= TaperKneeFraction)
            db = (f - 1) / (1 - TaperKneeFraction) * -TaperKneeDecibels;
        else if (f >= PlungeFraction)
            db = TaperKneeDecibels - (TaperKneeFraction - f) / (TaperKneeFraction - PlungeFraction) * (TaperKneeDecibels - FloorDecibels);
        else
            db = FloorDecibels - (PlungeFraction - f) / PlungeFraction * (FloorDecibels - BasementDecibels);
        return MathF.Pow(10, db / 20);
    }

    /// <summary>The fader position (0…1) that gives a linear gain: the inverse of <see cref="FaderGain(float)"/>.</summary>
    public static float FaderFraction(float gain)
    {
        if (!(gain > 0)) return 0;
        var db = Decibels(gain);
        if (db >= TaperKneeDecibels)
            return 1 + db / -TaperKneeDecibels * (1 - TaperKneeFraction);
        if (db >= FloorDecibels)
            return TaperKneeFraction - (TaperKneeDecibels - db) / (TaperKneeDecibels - FloorDecibels) * (TaperKneeFraction - PlungeFraction);
        if (!(db > BasementDecibels)) return 0;
        return PlungeFraction * (db - BasementDecibels) / (FloorDecibels - BasementDecibels);
    }

    /// <summary>
    /// A level typed into a fader's box, in dB. A positive number means "that many dB down"
    /// (faders never boost); -90 or lower is off. Null when it isn't a number.
    /// </summary>
    public static float? FaderGain(string entry)
    {
        var cleaned = entry.Trim().Replace('−', '-').Replace("+", "");
        if (!float.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var db)) return null;
        if (db > 0) db = -db;
        if (!(db > BasementDecibels)) return 0;
        return MathF.Pow(10, db / 20);
    }

    /// <summary>Adds a source to a held reading: powers sum (RMS), the louder peak wins.</summary>
    public static LevelReading Combine(LevelReading held, LevelReading adding, float gain = 1)
    {
        var rms = held.Rms * held.Rms + adding.Rms * gain * adding.Rms * gain;
        return new LevelReading(MathF.Min(MathF.Sqrt(rms), 1), MathF.Min(MathF.Max(held.Peak, adding.Peak * gain), 1));
    }

    /// <summary><see cref="Combine(LevelReading, LevelReading, float)"/> side by side.</summary>
    public static IReadOnlyList<LevelReading> Combine(IReadOnlyList<LevelReading> held, IReadOnlyList<LevelReading> adding, float gain = 1)
    {
        var count = Math.Max(held.Count, adding.Count);
        var result = new LevelReading[count];
        for (var side = 0; side < count; side++)
            result[side] = Combine(
                side < held.Count ? held[side] : LevelReading.Silence,
                side < adding.Count ? adding[side] : LevelReading.Silence, gain);
        return result;
    }

    /// <summary>What each output hears: every mix's level folded in at its send gain.</summary>
    public static Dictionary<string, IReadOnlyList<LevelReading>> OutputLevels(
        IEnumerable<(IReadOnlyList<LevelReading> Level, IReadOnlyList<(string OutputId, float Gain)> Sends)> mixes)
    {
        var levels = new Dictionary<string, IReadOnlyList<LevelReading>>(StringComparer.Ordinal);
        foreach (var mix in mixes)
            foreach (var send in mix.Sends)
                levels[send.OutputId] = Combine(levels.GetValueOrDefault(send.OutputId) ?? [], mix.Level, send.Gain);
        return levels;
    }

    /// <summary>Both sides averaged, for a mono output.</summary>
    public static LevelReading Mono(IReadOnlyList<LevelReading> sides)
    {
        if (sides.Count <= 1) return sides.Count == 1 ? sides[0] : LevelReading.Silence;
        return new LevelReading((sides[0].Rms + sides[1].Rms) / 2, (sides[0].Peak + sides[1].Peak) / 2);
    }

    /// <summary>The louder of the sides, for a single-bar meter.</summary>
    public static LevelReading Louder(IReadOnlyList<LevelReading> sides) =>
        new(sides.Count == 0 ? 0 : sides.Max(s => s.Rms), sides.Count == 0 ? 0 : sides.Max(s => s.Peak));

    /// <summary>A source's sides after its gain and pan (the balance law: the far side dips).</summary>
    public static IReadOnlyList<LevelReading> Placed(IReadOnlyList<LevelReading> sides, float gain, float pan = 0)
    {
        var left = sides.Count > 0 ? sides[0] : LevelReading.Silence;
        var right = sides.Count > 1 ? sides[1] : left;
        pan = MathF.Min(MathF.Max(pan, -1), 1);
        var leftGain = gain * MathF.Min(1, 1 - pan);
        var rightGain = gain * MathF.Min(1, 1 + pan);
        return [new LevelReading(left.Rms * leftGain, left.Peak * leftGain), new LevelReading(right.Rms * rightGain, right.Peak * rightGain)];
    }

    /// <summary>The level on each device channel when several outputs share one interface.</summary>
    public static Dictionary<int, LevelReading> DeviceChannels(
        IEnumerable<(IReadOnlyList<LevelReading> Sides, IReadOnlyList<int> Channels)> outputs)
    {
        var levels = new Dictionary<int, LevelReading>();
        foreach (var output in outputs)
        {
            IReadOnlyList<LevelReading> readings = output.Channels.Count == 1 ? [Mono(output.Sides)] : output.Sides;
            for (var index = 0; index < Math.Min(output.Channels.Count, readings.Count); index++)
            {
                var channel = output.Channels[index];
                levels[channel] = Combine(levels.GetValueOrDefault(channel), readings[index]);
            }
        }
        return levels;
    }

    /// <summary>
    /// Where one channel strip lands: its routed mix at full send plus each send, each
    /// scaled by the channel's gain, the send and the mix fader. Silent strips land nowhere.
    /// </summary>
    public static Dictionary<string, IReadOnlyList<LevelReading>> ChannelLanding(
        IReadOnlyList<LevelReading> sides, float channelGain, string? routedMix,
        IReadOnlyDictionary<string, float> sends, Func<string, float> mixGain, float pan = 0)
    {
        var landing = new Dictionary<string, IReadOnlyList<LevelReading>>(StringComparer.Ordinal);
        if (!sides.Any(s => s.Peak > 0)) return landing;
        var targets = new Dictionary<string, float>(sends, StringComparer.Ordinal);
        if (routedMix is not null) targets[routedMix] = 1;
        foreach (var (mixId, sendGain) in targets)
        {
            var gain = channelGain * sendGain * mixGain(mixId);
            if (gain > 0) landing[mixId] = Placed(sides, gain, pan);
        }
        return landing;
    }
}

/// <summary>
/// The loudest reading per side since the meter last looked, so a meter drawn at 15 fps
/// never misses a peak between frames. Port of LevelWindow.
/// </summary>
public struct LevelWindow
{
    public LevelReading Left { get; private set; }
    public LevelReading Right { get; private set; }

    public readonly float Rms => MathF.Max(Left.Rms, Right.Rms);
    public readonly float Peak => MathF.Max(Left.Peak, Right.Peak);
    public readonly IReadOnlyList<LevelReading> Sides => [Left, Right];

    public void Fold(LevelReading level) => Fold(level, level);

    public void Fold(LevelReading left, LevelReading right)
    {
        Left = new LevelReading(MathF.Max(Left.Rms, left.Rms), MathF.Max(Left.Peak, left.Peak));
        Right = new LevelReading(MathF.Max(Right.Rms, right.Rms), MathF.Max(Right.Peak, right.Peak));
    }

    /// <summary>The window's reading, then starts a fresh window.</summary>
    public LevelReading Take()
    {
        var taken = new LevelReading(Rms, Peak);
        this = default;
        return taken;
    }
}

/// <summary>
/// The peak-hold tick above a meter. Without <see cref="HoldSeconds"/> it only climbs until
/// reset; with it, the tick falls to the current level once it has been held that long.
/// </summary>
public struct PeakHold
{
    private double heldAt;

    public PeakHold() : this(null) { }

    public PeakHold(double? holdSeconds)
    {
        HoldSeconds = holdSeconds;
        Decibels = float.NegativeInfinity;
        heldAt = double.NegativeInfinity;
    }

    public float Decibels { get; private set; }

    public double? HoldSeconds { get; set; }

    public void Update(float linear, double at = 0)
    {
        var db = AudioLevel.Decibels(linear);
        if (db >= Decibels || (HoldSeconds is { } hold && at - heldAt > hold))
        {
            Decibels = db;
            heldAt = at;
        }
    }

    public void Reset()
    {
        Decibels = float.NegativeInfinity;
        heldAt = double.NegativeInfinity;
    }
}

/// <summary>
/// How a meter bar moves: it jumps up instantly and falls at a steady dB-per-second
/// rate, settling to exactly zero instead of creeping forever. Port of MeterBallistics.
/// </summary>
public struct MeterBallistics : IEquatable<MeterBallistics>
{
    public const float Floor = 0.0001f;

    public MeterBallistics() : this(20) { }

    public MeterBallistics(float decayDecibelsPerSecond) => DecayDecibelsPerSecond = decayDecibelsPerSecond;

    public float Rms { get; private set; }
    public float Peak { get; private set; }

    public float DecayDecibelsPerSecond { get; set; }

    public void Update(float rms, float peak, double elapsed)
    {
        var factor = MathF.Pow(10, -DecayDecibelsPerSecond * (float)Math.Max(0, elapsed) / 20);
        Rms = Settled(MathF.Max(rms, Rms * factor));
        Peak = Settled(MathF.Max(peak, Peak * factor));
    }

    private static float Settled(float value) => value < Floor ? 0 : value;

    public readonly bool Equals(MeterBallistics other) =>
        Rms == other.Rms && Peak == other.Peak && DecayDecibelsPerSecond == other.DecayDecibelsPerSecond;

    public override readonly bool Equals(object? obj) => obj is MeterBallistics other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(Rms, Peak, DecayDecibelsPerSecond);

    public static bool operator ==(MeterBallistics left, MeterBallistics right) => left.Equals(right);

    public static bool operator !=(MeterBallistics left, MeterBallistics right) => !left.Equals(right);
}
