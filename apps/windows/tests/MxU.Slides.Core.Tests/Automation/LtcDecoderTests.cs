using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Stage;

namespace MxU.Slides.Core.Tests.Automation;

/// <summary>
/// Makes and reads LTC test signals. Port of LTCSignal in PresenterCoreTests/LTCDecoderTests.swift.
/// The WAV fixtures are read from the Mac's test folder (apps/mac/Packages/PresenterCore/Tests/Fixtures/ltc),
/// so both apps decode the same recordings.
/// </summary>
internal static class LtcSignal
{
    public const double SampleRate = 48_000.0;

    public static bool[] Bits(TimecodeLabel label, bool dropFrame = false)
    {
        var bits = new bool[80];
        void Put(int value, int start, int width)
        {
            for (var i = 0; i < width; i++) bits[start + i] = ((value >> i) & 1) == 1;
        }
        Put(label.Frames % 10, 0, 4);
        Put(label.Frames / 10, 8, 2);
        bits[10] = dropFrame;
        Put(label.Seconds % 10, 16, 4);
        Put(label.Seconds / 10, 24, 3);
        Put(label.Minutes % 10, 32, 4);
        Put(label.Minutes / 10, 40, 3);
        Put(label.Hours % 10, 48, 4);
        Put(label.Hours / 10, 56, 2);
        int[] sync = [0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 1];
        for (var offset = 0; offset < sync.Length; offset++) bits[64 + offset] = sync[offset] == 1;
        return bits;
    }

    public static TimecodeLabel Label(int n, int nominal)
    {
        var seconds = n / nominal;
        return new TimecodeLabel(seconds / 3600, seconds / 60 % 60, seconds % 60, n % nominal);
    }

    /// <summary>Writes biphase-mark LTC, as a square wave, the way the Swift test's writer does.</summary>
    public sealed class Writer(double fps, float amplitude = 0.125f)
    {
        public List<float> Samples { get; } = [];
        private float level = 1;
        private double clock;

        public void Frames(IEnumerable<TimecodeLabel> labels, bool dropFrame = false, bool reverse = false)
        {
            foreach (var label in labels)
            {
                var cells = Bits(label, dropFrame);
                if (reverse) Array.Reverse(cells);
                foreach (var bit in cells)
                {
                    var cell = SampleRate / (80 * fps);
                    level = -level;
                    if (bit)
                    {
                        Write(cell / 2);
                        level = -level;
                        Write(cell / 2);
                    }
                    else
                    {
                        Write(cell);
                    }
                }
            }
        }

        public void Silence(double seconds) => Samples.AddRange(new float[(int)(seconds * SampleRate)]);

        private void Write(double duration)
        {
            var end = clock + duration;
            var count = (int)Math.Round(end, MidpointRounding.AwayFromZero) - (int)Math.Round(clock, MidpointRounding.AwayFromZero);
            for (var i = 0; i < count; i++) Samples.Add(level * amplitude);
            clock = end;
        }
    }

    private static string FixtureFolder([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "..", "mac", "Packages", "PresenterCore", "Tests", "Fixtures", "ltc"));

    /// <summary>A 16-bit PCM fixture's samples, scaled to ±1.</summary>
    public static float[] Fixture(string name)
    {
        var data = File.ReadAllBytes(Path.Combine(FixtureFolder(), name + ".wav"));
        var offset = 12;
        while (offset + 8 <= data.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(data, offset, 4);
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4, 4));
            if (id == "data")
            {
                var count = Math.Min(size, data.Length - offset - 8) / 2;
                var samples = new float[count];
                for (var i = 0; i < count; i++) samples[i] = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset + 8 + 2 * i, 2)) / 32768f;
                return samples;
            }
            offset += 8 + size + size % 2;
        }
        throw new InvalidDataException(name);
    }

    public static (List<LtcFrame> Frames, LtcDecoder Decoder) Decode(IReadOnlyList<float> signal, int chunk = 1024)
    {
        var samples = signal.Concat(new float[2400]).ToArray();
        var decoder = new LtcDecoder(SampleRate);
        var frames = new List<LtcFrame>();
        for (var start = 0; start < samples.Length; start += chunk)
            frames.AddRange(decoder.Decode(samples.AsSpan(start, Math.Min(chunk, samples.Length - start))));
        return (frames, decoder);
    }
}

// Ported from PresenterCoreTests/LTCDecoderTests.swift.
public class LtcDecoderTests
{
    private static List<string> Texts(IEnumerable<LtcFrame> frames) => frames.Select(f => f.Label.Text()).ToList();

    [Fact]
    public void ReadsLtcgenAtEveryRate()
    {
        (string Name, string First, string Last, TimecodeRate Rate)[] cases =
        [
            ("ltc-24", "10:00:00:00", "10:00:00:11", TimecodeRate.Fps24),
            ("ltc-25", "02:30:00:00", "02:30:00:11", TimecodeRate.Fps25),
            ("ltc-30", "01:00:00:00", "01:00:00:11", TimecodeRate.Fps30),
        ];
        foreach (var (name, first, last, rate) in cases)
        {
            var (frames, decoder) = LtcSignal.Decode(LtcSignal.Fixture(name));
            Assert.True(frames.Count == 12, name);
            Assert.Equal(first, Texts(frames)[0]);
            Assert.Equal(last, Texts(frames)[^1]);
            Assert.All(frames, f => Assert.False(f.Reverse || f.DropFrame));
            Assert.Equal(rate, decoder.Rate);
        }
    }

    [Fact]
    public void ReadsDropFrameAcrossTheMinute()
    {
        var (frames, decoder) = LtcSignal.Decode(LtcSignal.Fixture("ltc-2997df"));
        Assert.Equal(
        [
            "00:00:59:25", "00:00:59:26", "00:00:59:27", "00:00:59:28", "00:00:59:29",
            "00:01:00:02", "00:01:00:03", "00:01:00:04", "00:01:00:05", "00:01:00:06", "00:01:00:07", "00:01:00:08",
        ], Texts(frames));
        Assert.All(frames, f => Assert.True(f.DropFrame));
        Assert.Equal(TimecodeRate.Fps2997DropFrame, decoder.Rate);
        var rate = TimecodeRate.Fps2997DropFrame;
        Assert.Equal(rate.FrameNumber(frames[4].Label) + 1, rate.FrameNumber(frames[5].Label));
    }

    [Fact]
    public void ReadsQuietSignalAt36dBDown()
    {
        var (frames, _) = LtcSignal.Decode(LtcSignal.Fixture("ltc-30-quiet36"));
        Assert.Equal("03:00:00:00", Texts(frames)[0]);
        Assert.Equal(12, frames.Count);
    }

    [Fact]
    public void ReadsReverseAndSaysSo()
    {
        var (frames, _) = LtcSignal.Decode(LtcSignal.Fixture("ltc-30-reverse"));
        Assert.True(frames.Count >= 10);
        Assert.All(frames, f => Assert.True(f.Reverse));
        Assert.Equal(["01:00:10:00", "01:00:09:29", "01:00:09:28"], Texts(frames).Take(3));
    }

    [Fact]
    public void ChunkSizeDoesNotMatter()
    {
        var samples = LtcSignal.Fixture("ltc-25");
        Assert.Equal(Texts(LtcSignal.Decode(samples, 4096).Frames), Texts(LtcSignal.Decode(samples, 1).Frames));
    }

    [Fact]
    public void OwnWriterMatchesTheReference()
    {
        var writer = new LtcSignal.Writer(30);
        writer.Frames(Enumerable.Range(108_000, 12).Select(n => LtcSignal.Label(n, 30)));
        var frames = LtcSignal.Decode(writer.Samples).Frames;
        Assert.Equal("01:00:00:00", Texts(frames)[0]);
        Assert.Equal(12, frames.Count);
        Assert.Equal(1600, frames[5].EndSample - frames[4].EndSample);
    }

    [Fact]
    public void TellsThirtyFromTwentyNineNineSeven()
    {
        foreach (var (fps, rate) in new[] { (30.0, TimecodeRate.Fps30), (30000.0 / 1001, TimecodeRate.Fps2997) })
        {
            var writer = new LtcSignal.Writer(fps);
            writer.Frames(Enumerable.Range(0, 90).Select(n => LtcSignal.Label(n, 30)));
            Assert.Equal(rate, LtcSignal.Decode(writer.Samples).Decoder.Rate);
        }
    }

    [Fact]
    public void SurvivesDropoutsAndJumps()
    {
        var writer = new LtcSignal.Writer(30);
        writer.Frames(Enumerable.Range(0, 10).Select(n => LtcSignal.Label(n, 30)));
        writer.Silence(0.3);
        writer.Frames(Enumerable.Range(10, 10).Select(n => LtcSignal.Label(n, 30)));
        writer.Frames(Enumerable.Range(620, 10).Select(n => LtcSignal.Label(n, 30)));
        var numbers = LtcSignal.Decode(writer.Samples).Frames.Select(f => TimecodeRate.Fps30.FrameNumber(f.Label)).ToList();
        Assert.Equal(Enumerable.Range(0, 10), numbers.Take(10));
        Assert.Contains(19, numbers);
        Assert.Equal(Enumerable.Range(620, 10), numbers.TakeLast(10));
    }

    [Fact]
    public void SilenceAndHumBelowTheGateReadNothing()
    {
        var writer = new LtcSignal.Writer(30, 0.001f);
        writer.Frames(Enumerable.Range(0, 20).Select(n => LtcSignal.Label(n, 30)));
        Assert.Empty(LtcSignal.Decode(writer.Samples).Frames);
        Assert.Empty(LtcSignal.Decode(new float[48_000]).Frames);
    }

    [Fact]
    public void NoiseOnAQuietSignal()
    {
        var writer = new LtcSignal.Writer(25, 0.016f);
        writer.Frames(Enumerable.Range(0, 25).Select(n => LtcSignal.Label(n, 25)));
        uint seed = 1;
        var noisy = writer.Samples.Select(sample =>
        {
            seed = unchecked(seed * 1_664_525 + 1_013_904_223);
            return sample + ((seed >> 8) / (float)(1 << 24) - 0.5f) * 0.002f;
        }).ToList();
        Assert.True(LtcSignal.Decode(noisy).Frames.Count >= 24);
    }

    [Fact]
    public void LabelsFromFrameNumbersRoundTrip()
    {
        foreach (var rate in TimecodeRates.All)
            foreach (var n in new[] { 0, 1, 1_797, 1_798, 1_800, 17_981, 17_982, 107_892, 107_999, 2_000_000 })
                Assert.True(n == rate.FrameNumber(rate.LabelForFrame(n)), $"{rate} {n}");
        Assert.Equal("00:01:00;02", TimecodeRate.Fps2997DropFrame.LabelForFrame(1_800).Text(dropFrame: true));
        Assert.Equal(new TimecodeLabel(1, 0, 0, 12), TimecodeRate.Fps25.LabelForSeconds(3600.5));
    }

    [Fact]
    public void SecondsFromLabels()
    {
        var label = new TimecodeLabel(1, 0, 0, 15);
        Assert.Equal(3600.5, TimecodeRate.Fps30.Seconds(label));
        Assert.Equal(3600.6, TimecodeRate.Fps25.Seconds(label));
        Assert.True(Math.Abs(TimecodeRate.Fps2997.Seconds(label) - 108_015 / (30000.0 / 1001)) < 1e-9);
        Assert.True(Math.Abs(TimecodeRate.Fps2997DropFrame.Seconds(new TimecodeLabel(1, 0, 0, 0)) - 3600) < 0.01);
        Assert.Equal(label with { Frames = 12 }, TimecodeRate.Fps25.LabelForStartFrames(108_015));
        Assert.Equal("01:00:00:15", label.Text());
        Assert.Equal("01:00:00;15", label.Text(dropFrame: true));
    }

    // C#: the reader times each frame by where it ended in the buffer.
    [Fact]
    public void TheReaderTimesFramesWithinTheBuffer()
    {
        var writer = new LtcSignal.Writer(30);
        writer.Frames(Enumerable.Range(0, 6).Select(n => LtcSignal.Label(n, 30)));
        var reader = new LtcReader();
        reader.Read(writer.Samples.ToArray(), LtcSignal.SampleRate, 100);
        // The last frame's final bit is read at the next edge, or when the signal stops.
        Assert.Equal(5, reader.Drain().Heard.Count);
        reader.Read(new float[2400], LtcSignal.SampleRate, 100 + writer.Samples.Count / LtcSignal.SampleRate);
        var (heard, peak) = reader.Drain();
        Assert.Equal(0f, peak);
        var last = Assert.Single(heard);
        Assert.Equal(5, last.Label.Frames);

        var fresh = new LtcReader();
        fresh.Read([.. writer.Samples, .. new float[2400]], LtcSignal.SampleRate, 100);
        (heard, peak) = fresh.Drain();
        Assert.Equal(0.125f, peak);
        Assert.Equal(6, heard.Count);
        Assert.True(Math.Abs(heard[4].HostTime - heard[3].HostTime - 1.0 / 30) < 1e-9);
        Assert.True(heard[0].HostTime > 100 && heard[0].HostTime < 100 + 2.0 / 30);
        Assert.Empty(fresh.Drain().Heard);
    }
}
