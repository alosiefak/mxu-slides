using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Control;
using MxU.Slides.Core.Stage;

namespace MxU.Slides.Core.Tests.Automation;

/// <summary>Makes MTC test streams. Port of MTCSignal in PresenterCoreTests/MTCDecoderTests.swift.</summary>
internal static class MtcSignal
{
    public static int Code(TimecodeRate rate) => rate switch
    {
        TimecodeRate.Fps24 => 0,
        TimecodeRate.Fps25 => 1,
        TimecodeRate.Fps2997DropFrame => 2,
        _ => 3,
    };

    public static byte[] Pieces(TimecodeLabel label, TimecodeRate rate)
    {
        int[] values =
        [
            label.Frames & 0xF, label.Frames >> 4, label.Seconds & 0xF, label.Seconds >> 4,
            label.Minutes & 0xF, label.Minutes >> 4, label.Hours & 0xF, label.Hours >> 4 | Code(rate) << 1,
        ];
        return values.Select((value, offset) => (byte)(offset << 4 | value)).ToArray();
    }

    public static List<(byte[] Bytes, double Time)> Play(int from, int frames, TimecodeRate rate, double start = 10, bool reverse = false)
    {
        var packets = new List<(byte[], double)>();
        var quarter = 0;
        for (var pair = 0; pair < frames / 2; pair++)
        {
            var number = reverse ? from - 2 * pair : from + 2 * pair;
            var pieces = Pieces(rate.LabelForFrame(number), rate);
            foreach (var piece in reverse ? pieces.Reverse() : pieces)
            {
                packets.Add(([0xF1, piece], start + quarter / 4.0 / rate.Actual()));
                quarter++;
            }
        }
        return packets;
    }

    public static byte[] FullFrame(TimecodeLabel label, TimecodeRate rate, byte device = 0x7F) =>
        [0xF0, 0x7F, device, 0x01, 0x01, (byte)(Code(rate) << 5 | label.Hours), (byte)label.Minutes, (byte)label.Seconds, (byte)label.Frames, 0xF7];

    public static List<TimecodeHeard> Decode(MtcDecoder decoder, IEnumerable<(byte[] Bytes, double Time)> packets) =>
        packets.SelectMany(p => decoder.Receive(p.Bytes, p.Time)).ToList();
}

// Ported from PresenterCoreTests/MTCDecoderTests.swift (MTCDecoderTests and MTCClockTests).
public class MtcDecoderTests
{
    public static TheoryData<TimecodeRate> Rates => [TimecodeRate.Fps24, TimecodeRate.Fps25, TimecodeRate.Fps2997DropFrame, TimecodeRate.Fps30];

    [Theory]
    [MemberData(nameof(Rates))]
    public void QuarterFramesAssembleAtEveryRate(TimecodeRate rate)
    {
        var start = rate.FrameNumber(new TimecodeLabel(1, 2, 3, rate.Nominal() - 3));
        var packets = MtcSignal.Play(start, 6, rate);
        var heard = MtcSignal.Decode(new MtcDecoder(), packets);
        Assert.Equal(3, heard.Count);
        var first = heard[0];
        Assert.Equal(rate, first.Rate);
        Assert.False(first.Reverse);
        Assert.Equal(rate.LabelForFrame(start + 2), first.Label);
        Assert.Equal(packets[7].Time, first.HostTime);
        Assert.True(Math.Abs(first.Position - (rate.Seconds(rate.LabelForFrame(start)) + 1.75 / rate.Actual())) < 1e-9);
        Assert.Equal(rate.LabelForFrame(start + 4), heard[1].Label);
    }

    [Fact]
    public void JoiningMidFrameWaitsForAWholeOne()
    {
        var packets = MtcSignal.Play(300, 4, TimecodeRate.Fps30).Skip(3);
        Assert.Equal([TimecodeRate.Fps30.LabelForFrame(304)], MtcSignal.Decode(new MtcDecoder(), packets).Select(h => h.Label));
    }

    [Fact]
    public void AMissingPieceDropsThatFrameOnly()
    {
        var packets = MtcSignal.Play(300, 6, TimecodeRate.Fps30);
        packets.RemoveAt(4);
        Assert.Equal(new[] { 302, 304 }.Select(n => TimecodeRate.Fps30.LabelForFrame(n + 2)), MtcSignal.Decode(new MtcDecoder(), packets).Select(h => h.Label));
    }

    [Fact]
    public void BackwardsPiecesAssembleReversed()
    {
        var heard = MtcSignal.Decode(new MtcDecoder(), MtcSignal.Play(900, 4, TimecodeRate.Fps25, reverse: true));
        var first = heard[0];
        Assert.True(first.Reverse);
        Assert.Equal(TimecodeRate.Fps25.LabelForFrame(900), first.Label);
        Assert.True(Math.Abs(first.Position - 900.0 / 25) < 1e-9);
        Assert.Equal(2, heard.Count);
    }

    [Fact]
    public void FullFrameLocates()
    {
        var decoder = new MtcDecoder();
        var label = new TimecodeLabel(2, 0, 30, 12);
        var frame = Assert.Single(decoder.Receive(MtcSignal.FullFrame(label, TimecodeRate.Fps25, 0x00), 5));
        Assert.Equal(new TimecodeHeard(label, TimecodeRate.Fps25, false, TimecodeRate.Fps25.Seconds(label), 5), frame);

        var bytes = MtcSignal.FullFrame(label, TimecodeRate.Fps30);
        Assert.Empty(decoder.Receive([.. bytes[..4], 0xF8], 6));
        Assert.Equal(TimecodeRate.Fps30, decoder.Receive(bytes[4..], 6).FirstOrDefault()?.Rate);
        Assert.Empty(decoder.Receive([0xF0, 0x7E, 0x7F, 0x06, 0x01, 0xF7], 7));
    }

    [Fact]
    public void NotesAndClockInTheSamePacketsLeaveBothReadersAlone()
    {
        var decoder = new MtcDecoder();
        var heard = new List<TimecodeHeard>();
        var notes = new List<MidiParse.ChannelMessage>();
        var index = 0;
        foreach (var packet in MtcSignal.Play(60, 2, TimecodeRate.Fps30))
        {
            byte[] bytes = [0x90, 19, (byte)(index + 1), 0xF8, .. packet.Bytes];
            heard.AddRange(decoder.Receive(bytes, packet.Time));
            notes.AddRange(MidiParse.ChannelMessages(bytes));
            index++;
        }
        Assert.Single(heard);
        Assert.Equal(8, notes.Count);
        Assert.All(notes, n => Assert.True(n.Status == 0x90 && n.Data1 == 19));
    }

    [Fact]
    public void AQuarterFrameSplitAcrossPacketsStillCounts()
    {
        var decoder = new MtcDecoder();
        var heard = new List<TimecodeHeard>();
        foreach (var packet in MtcSignal.Play(60, 2, TimecodeRate.Fps30))
        {
            heard.AddRange(decoder.Receive([packet.Bytes[0]], packet.Time));
            heard.AddRange(decoder.Receive([packet.Bytes[1]], packet.Time));
        }
        Assert.Single(heard);
    }

    private static void Feed(IEnumerable<(byte[] Bytes, double Time)> packets, TimecodeClock clock, MtcDecoder decoder)
    {
        foreach (var frame in MtcSignal.Decode(decoder, packets)) clock.Receive(frame);
    }

    [Fact]
    public void PlayingMtcLocksAndTracks()
    {
        var clock = new TimecodeClock();
        var packets = MtcSignal.Play(3000, 8, TimecodeRate.Fps30);
        Feed(packets, clock, new MtcDecoder());
        var reading = clock.ReadingAt(packets[^1].Time);
        Assert.Equal(TimecodeClock.State.Locked, reading.State);
        Assert.True(reading.Live);
        Assert.True(Math.Abs((reading.Position ?? 0) - (3006 + 1.75) / 30) < 1e-9);
        Assert.Equal(TimecodeRate.Fps30.LabelForFrame(3008), reading.Label);
    }

    [Fact]
    public void StoppedMtcFreewheelsThenHolds()
    {
        var clock = new TimecodeClock();
        var packets = MtcSignal.Play(3000, 8, TimecodeRate.Fps24);
        Feed(packets, clock, new MtcDecoder());
        var end = packets[^1].Time;
        Assert.Equal(TimecodeClock.State.Freewheel, clock.ReadingAt(end + 0.3).State);
        Assert.False(clock.ReadingAt(end + 0.3).Live);
        Assert.Equal(TimecodeClock.State.Stopped, clock.ReadingAt(end + 2).State);
    }

    [Fact]
    public void RunningBackwardsHolds()
    {
        var clock = new TimecodeClock();
        var packets = MtcSignal.Play(3000, 8, TimecodeRate.Fps30, reverse: true);
        Feed(packets, clock, new MtcDecoder());
        var reading = clock.ReadingAt(packets[^1].Time);
        Assert.True(reading.Reverse);
        Assert.False(reading.Live);
    }

    [Fact]
    public void ALocateWhilePlayingIsAJumpOnceQuarterFramesFollow()
    {
        var clock = new TimecodeClock();
        var decoder = new MtcDecoder();
        var rate = TimecodeRate.Fps30;
        var before = MtcSignal.Play(3000, 8, rate);
        Feed(before, clock, decoder);
        var at = before[^1].Time + 1.0 / 120;
        foreach (var frame in decoder.Receive(MtcSignal.FullFrame(rate.LabelForFrame(9000), rate), at)) clock.Receive(frame);
        var after = MtcSignal.Play(9000, 4, rate, at);
        Feed(after, clock, decoder);
        var reading = clock.ReadingAt(after[^1].Time);
        Assert.Equal(TimecodeClock.State.Locked, reading.State);
        Assert.True(Math.Abs((reading.Position ?? 0) - (9002 + 1.75) / 30) < 1e-9);
    }

    [Fact]
    public void MtcDrivesTheSameResolverAsLtc()
    {
        var rate = TimecodeRate.Fps25;
        List<TimecodeFollow.Song> songs = [new("a", 108_000, 60)];
        var clock = new TimecodeClock();
        var decoder = new MtcDecoder();
        var follow = new TimecodeFollow();
        var commands = new List<TimecodeFollow.Command>();
        foreach (var packet in MtcSignal.Play(3600 * 25 - 10, 40, rate))
        {
            foreach (var frame in decoder.Receive(packet.Bytes, packet.Time)) clock.Receive(frame);
            if (follow.Step(clock.ReadingAt(packet.Time), songs) is { } command) commands.Add(command);
        }
        var play = Assert.IsType<TimecodeFollow.Command.Play>(commands[0]);
        Assert.Equal("a", play.ItemId);
        Assert.True(play.From < 0);
        Assert.All(commands.Skip(1), c => Assert.IsType<TimecodeFollow.Command.Play>(c));
    }

    // C#: the MTC reader's meter reads full while timecode arrives.
    [Fact]
    public void TheMtcReaderCollectsAndMeters()
    {
        var reader = new MtcReader();
        foreach (var packet in MtcSignal.Play(300, 4, TimecodeRate.Fps30)) reader.Read(packet.Bytes, packet.Time);
        var (heard, peak) = reader.Drain();
        Assert.Equal((2, 1f), (heard.Count, peak));
        Assert.Equal(0f, reader.Drain().Peak);
    }
}
