using MxU.Slides.Core.Stage;

namespace MxU.Slides.Core.Automation;

/// <summary>Where incoming timecode comes from: LTC on an audio input, or MTC on a MIDI input.</summary>
public enum TimecodeSource { Ltc, Mtc }

public static class TimecodeSources
{
    public static string Raw(this TimecodeSource source) => source == TimecodeSource.Mtc ? "mtc" : "ltc";

    public static TimecodeSource? Parse(string? raw) => raw switch
    {
        "ltc" => TimecodeSource.Ltc,
        "mtc" => TimecodeSource.Mtc,
        _ => null,
    };
}

/// <summary>
/// One timecode reading, from either source: the label, the rate, the direction, and the
/// position in seconds it stood for at <see cref="HostTime"/> (the host clock, seconds).
/// </summary>
public sealed record TimecodeHeard(TimecodeLabel Label, TimecodeRate Rate, bool Reverse, double Position, double HostTime)
{
    /// <summary>An LTC frame is heard when it ends, so (played forwards) it stands for the start of the next frame.</summary>
    public static TimecodeHeard FromLtc(LtcFrame frame, TimecodeRate rate, double hostTime) =>
        new(frame.Label, rate, frame.Reverse, rate.Seconds(frame.Label) + (frame.Reverse ? 0 : 1 / rate.Actual()), hostTime);

    /// <summary>Where this reading puts the timecode at another host time, running at normal speed.</summary>
    internal double PositionAt(double time) => Position + (time - HostTime) * (Reverse ? -1 : 1);

    /// <summary>The next reading carries on from this one (same direction and rate family, within a frame and a half).</summary>
    internal bool Continues(TimecodeHeard next) =>
        next.Reverse == Reverse && next.Rate.Nominal() == Rate.Nominal()
        && Math.Abs(next.Position - PositionAt(next.HostTime)) <= 1.5 / Rate.Actual();
}

/// <summary>
/// Decodes MIDI timecode from a MIDI input's bytes. While playing, MTC sends quarter-frame
/// messages (F1 nn): eight of them, each carrying a nibble of the time, make up one
/// label over two frames. When a sequencer jumps it sends a full frame (a SysEx message).
/// Realtime bytes and other messages in the same stream are skipped. Port of
/// PresenterCore/Timeline/MTCDecoder.swift.
/// </summary>
public sealed class MtcDecoder
{
    private readonly int[] pieces = new int[8];
    private int? lastPiece;
    private bool reverse;

    /// <summary>How many quarter frames in a row arrived in order (eight make a whole label).</summary>
    private int run;

    private bool quarterFramePending;

    private List<byte>? sysex;

    /// <summary>The rate code in MTC's hours byte.</summary>
    public static TimecodeRate RateForCode(int code) => (code & 3) switch
    {
        0 => TimecodeRate.Fps24,
        1 => TimecodeRate.Fps25,
        2 => TimecodeRate.Fps2997DropFrame,
        _ => TimecodeRate.Fps30,
    };

    /// <summary>Reads bytes from a MIDI input (any split: a message may straddle calls) and returns the readings they completed.</summary>
    public List<TimecodeHeard> Receive(IReadOnlyList<byte> bytes, double hostTime)
    {
        var heard = new List<TimecodeHeard>();
        foreach (var b in bytes)
        {
            if (b >= 0xF8) continue;
            if (quarterFramePending)
            {
                quarterFramePending = false;
                if (b < 0x80)
                {
                    if (QuarterFrame(b, hostTime) is { } frame) heard.Add(frame);
                    continue;
                }
            }
            if (sysex is { } message)
            {
                if (b == 0xF7)
                {
                    sysex = null;
                    if (FullFrame(message, hostTime) is { } frame) heard.Add(frame);
                    continue;
                }
                if (b < 0x80)
                {
                    message.Add(b);
                    if (message.Count > 16) sysex = null;
                    continue;
                }
                sysex = null;
            }
            switch (b)
            {
                case 0xF0:
                    sysex = [];
                    break;
                case 0xF1:
                    quarterFramePending = true;
                    break;
            }
        }
        return heard;
    }

    private TimecodeHeard? QuarterFrame(byte data, double hostTime)
    {
        var piece = data >> 4;
        pieces[piece] = data & 0x0F;
        try
        {
            if (lastPiece is not { } last)
            {
                run = 1;
                return null;
            }
            var forward = piece == (last + 1) % 8;
            if (forward || piece == (last + 7) % 8)
            {
                run = reverse == !forward ? run + 1 : 2;
                reverse = !forward;
            }
            else
            {
                run = 1;
            }
            if (run < 8 || piece != (reverse ? 0 : 7)) return null;
            var rate = RateForCode(pieces[7] >> 1);
            var label = new TimecodeLabel(
                pieces[6] | (pieces[7] & 1) << 4, pieces[4] | (pieces[5] & 3) << 4,
                pieces[2] | (pieces[3] & 3) << 4, pieces[0] | (pieces[1] & 1) << 4);
            if (!Valid(label, rate)) return null;

            // The label was sent at the first quarter frame and arrives complete two frames later.
            var quarters = reverse ? 0.0 : 7.0;
            var now = reverse ? label : rate.LabelForFrame(rate.FrameNumber(label) + 2);
            return new TimecodeHeard(now, rate, reverse, rate.Seconds(label) + quarters / 4 / rate.Actual(), hostTime);
        }
        finally
        {
            lastPiece = piece;
        }
    }

    private TimecodeHeard? FullFrame(List<byte> message, double hostTime)
    {
        if (message.Count != 8 || message[0] != 0x7F || message[2] != 0x01 || message[3] != 0x01) return null;
        var rate = RateForCode(message[4] >> 5);
        var label = new TimecodeLabel(message[4] & 0x1F, message[5], message[6], message[7]);
        if (!Valid(label, rate)) return null;
        lastPiece = null;
        run = 0;
        return new TimecodeHeard(label, rate, false, rate.Seconds(label), hostTime);
    }

    private static bool Valid(TimecodeLabel label, TimecodeRate rate) =>
        label.Hours < 24 && label.Minutes < 60 && label.Seconds < 60 && label.Frames < rate.Nominal();
}
