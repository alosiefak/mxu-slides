using MxU.Slides.VideoIO.DeckLink;

namespace MxU.Slides.VideoIO.Tests.DeckLink;

/// <summary>Port of DeckLinkKitTests/DeckLinkEnumerationTests.swift. Without Desktop Video (CI, this test PC) everything comes back empty.</summary>
public sealed class DeckLinkEnumerationTests
{
    [Fact]
    public void RuntimeProbeAnswersWithoutCrashing() => _ = DeckLinkRuntime.IsAvailable;

    [Fact]
    public void EnumerationDegradesGracefully()
    {
        var devices = DeckLinkRuntime.Devices();
        if (!DeckLinkRuntime.IsAvailable) Assert.Empty(devices);
        Assert.All(devices, device => Assert.NotEmpty(device.Name));
    }

    [Fact]
    public void OpeningAMissingDeviceFailsWithAMessage()
    {
        Assert.Null(DeckLinkOutput.TryOpen(long.MinValue, 0, DeckLinkKeying.Off, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void ModeEnumerationDegradesGracefully() => Assert.Empty(DeckLinkRuntime.DisplayModes(long.MinValue));

    [Fact]
    public void WithoutTheDriverEveryQuestionHasAQuietAnswer()
    {
        if (DeckLinkRuntime.IsAvailable) Assert.Skip("Desktop Video is installed on this PC");
        Assert.Empty(DeckLinkRuntime.Devices());
        Assert.Equal(0, DeckLinkRuntime.ApiVersion);
        Assert.Null(DeckLinkRuntime.ApiVersionString);
        Assert.False(DeckLinkRuntime.RuntimeIsTooOld);
        Assert.Null(DeckLinkRuntime.ConfiguredOutputMode(1));
        Assert.False(DeckLinkRuntime.ReferenceLocked(1));
        Assert.Null(DeckLinkRuntime.OutputLinkConfiguration(1));
    }

    [Fact]
    public void DiscoveryFromAnStaThreadAnswersToo()
    {
        // The WPF UI thread is single-threaded-apartment; DeckLink calls hop to an MTA thread.
        IReadOnlyList<DeckLinkDevice>? devices = null;
        var thread = new Thread(() => devices = DeckLinkRuntime.Devices());
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.NotNull(devices);
    }

    [Fact]
    public void VersionsReadAsMajorMinorPoint()
    {
        Assert.Equal("16.0.1", DeckLinkRuntime.FormatVersion(0x10000100));
        Assert.Null(DeckLinkRuntime.FormatVersion(0));
        Assert.True(DeckLinkRuntime.IsTooOld(0x0F030000));
        Assert.False(DeckLinkRuntime.IsTooOld(0x10000000));
        Assert.False(DeckLinkRuntime.IsTooOld(0)); // no driver is "not installed", not "too old"
    }
}

/// <summary>Port of DeckLinkKitTests/DeckLinkInputTests.swift (the C shim's helpers are C# here).</summary>
public sealed class DeckLinkInputTests
{
    [Fact]
    public void OpeningAMissingInputFailsWithAMessage()
    {
        Assert.Null(DeckLinkInput.TryOpen(long.MinValue, 2, (in DeckLinkInputArrival _) => { }, _ => { }, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void AudioChannelsTakeTheMostTheSdkOffers()
    {
        Assert.Equal(2, DeckLinkInput.AudioChannels(0));
        Assert.Equal(2, DeckLinkInput.AudioChannels(2));
        Assert.Equal(8, DeckLinkInput.AudioChannels(8));
        Assert.Equal(16, DeckLinkInput.AudioChannels(16));
        Assert.Equal(16, DeckLinkInput.AudioChannels(64));
    }

    [Fact]
    public void OnlyANewModeRestartsTheStreams()
    {
        const uint p2997 = 0x48703239;
        const uint i5994 = 0x48693539;
        Assert.False(DeckLinkInput.FormatNeedsRestart(p2997, p2997));
        Assert.True(DeckLinkInput.FormatNeedsRestart(p2997, i5994));
        Assert.False(DeckLinkInput.FormatNeedsRestart(p2997, 0)); // an event with no mode changes nothing
    }

    [Fact]
    public void BlendAveragesEachLineWithTheNext()
    {
        byte[] source = [10, 10, 20, 20, 31, 31];
        var destination = new byte[6];
        DeckLinkFrameConverter.DeinterlaceBlend(source, 2, destination, 2, 2, 3);
        Assert.Equal([15, 15, 26, 26, 31, 31], destination);
    }

    private static byte[] Frame(int width, int height, Func<int, (byte Y, byte Cb, byte Cr)> row)
    {
        var bytes = new List<byte>();
        for (var line = 0; line < height; line++)
        {
            var (y, cb, cr) = row(line);
            for (var i = 0; i < width / 2; i++) bytes.AddRange([cb, y, cr, y]);
        }
        return [.. bytes];
    }

    private static int[] Pixel(byte[] bgra, int stride, int x, int y) => [.. bgra.AsSpan(y * stride + x * 4, 4).ToArray().Select(b => (int)b)];

    private static bool Near(int[] actual, int[] expected, int tolerance = 3) => actual.Zip(expected).All(p => Math.Abs(p.First - p.Second) <= tolerance);

    [Fact]
    public void VideoRangeHdConvertsToBgra()
    {
        const int width = 8, height = 720;
        var bytes = Frame(width, height, line => (line % 3) switch
        {
            0 => ((byte)16, (byte)128, (byte)128),
            1 => ((byte)235, (byte)128, (byte)128),
            _ => ((byte)63, (byte)102, (byte)240),
        });
        var output = new byte[width * 4 * height];
        Assert.True(new DeckLinkFrameConverter().ConvertToBgra(bytes, width * 2, width, height, false, output, width * 4));
        Assert.True(Near(Pixel(output, width * 4, 0, 0), [0, 0, 0, 255]));
        Assert.True(Near(Pixel(output, width * 4, 0, 1), [255, 255, 255, 255]));
        Assert.True(Near(Pixel(output, width * 4, 0, 2), [0, 0, 255, 255]));
    }

    [Fact]
    public void InterlacedFramesLoseTheComb()
    {
        const int width = 8, height = 720;
        var bytes = Frame(width, height, line => line % 2 == 0 ? ((byte)16, (byte)128, (byte)128) : ((byte)235, (byte)128, (byte)128));
        var output = new byte[width * 4 * height];
        new DeckLinkFrameConverter().ConvertToBgra(bytes, width * 2, width, height, true, output, width * 4);
        var upper = Pixel(output, width * 4, 0, 10);
        var lower = Pixel(output, width * 4, 0, 11);
        Assert.True(Near(upper, lower));
        Assert.InRange(upper[0], 101, 159);
    }

    [Fact]
    public void ADestinationTooSmallForBgraIsRefused()
    {
        // The Mac refuses a non-BGRA CVPixelBuffer; here a buffer sized for two bytes a pixel.
        var bytes = Enumerable.Repeat((byte)128, 8 * 8 * 2).ToArray();
        var destination = new byte[8 * 8 * 2];
        Assert.False(new DeckLinkFrameConverter().ConvertToBgra(bytes, 16, 8, 8, false, destination, 16));
        Assert.All(destination, b => Assert.Equal(0, b));
    }

    [Fact]
    public void SdUsesTheBt601Matrix()
    {
        // BT.601 red is Y=81 Cb=90 Cr=240; through the 709 matrix it would not come out pure red.
        var bytes = Frame(8, 480, _ => (81, 90, 240));
        var output = new byte[8 * 4 * 480];
        new DeckLinkFrameConverter().ConvertToBgra(bytes, 16, 8, 480, false, output, 32);
        Assert.True(Near(Pixel(output, 32, 0, 0), [0, 0, 255, 255]));
    }
}

/// <summary>Port of DeckLinkKitTests/DisplayModeMatchTests.swift.</summary>
public sealed class DisplayModeMatchTests
{
    private static DeckLinkDisplayMode Mode(string name, int width = 1920, int height = 1080, long duration = 1000, long scale = 30000, bool progressive = true) =>
        new(name, (uint)(name.GetHashCode() & 0xFFFF) + 1, width, height, duration, scale, progressive);

    [Fact]
    public void FractionalSiblingBeatsExactRate()
    {
        DeckLinkDisplayMode[] modes = [Mode("1080p29.97", duration: 1001, scale: 30000), Mode("1080p30", duration: 1000, scale: 30000)];
        Assert.Equal("1080p29.97", DeckLinkDisplayMode.BestMatch(1920, 1080, 30, modes)?.Name);
    }

    [Fact]
    public void FractionalSiblingFillsInWhenExactIsAbsent()
    {
        DeckLinkDisplayMode[] modes = [Mode("1080p25", duration: 1000, scale: 25000), Mode("1080p29.97", duration: 1001, scale: 30000)];
        Assert.Equal("1080p29.97", DeckLinkDisplayMode.BestMatch(1920, 1080, 30, modes)?.Name);
    }

    [Fact]
    public void ProgressiveBeatsInterlacedAtTheSameRate()
    {
        DeckLinkDisplayMode[] modes = [Mode("1080i29.97", duration: 1001, scale: 30000, progressive: false), Mode("1080p29.97", duration: 1001, scale: 30000)];
        Assert.Equal("1080p29.97", DeckLinkDisplayMode.BestMatch(1920, 1080, 30, modes)?.Name);
    }

    [Fact]
    public void GeometryMismatchYieldsNothing() =>
        Assert.Null(DeckLinkDisplayMode.BestMatch(1920, 1080, 30, [Mode("720p30", 1280, 720, duration: 1000, scale: 30000)]));

    [Fact]
    public void DistantRatesNeverMatch() =>
        Assert.Null(DeckLinkDisplayMode.BestMatch(1920, 1080, 30, [Mode("1080p25", duration: 1000, scale: 25000)]));
}

/// <summary>Port of DeckLinkKitTests/OutputTimingTallyTests.swift.</summary>
public sealed class OutputTimingTallyTests
{
    [Fact]
    public void OneLatchPerCompletionIsClean()
    {
        var timing = new DeckLinkOutputTiming();
        for (var frame = 1; frame <= 5; frame++)
        {
            var slot = timing.Latch();
            Assert.Equal(slot, timing.TallyCompletion(frame / 30.0));
        }
        Assert.Equal(5ul, timing.Completions);
        Assert.Equal(5.0 / 30, timing.LastCompletionHostTime);
        Assert.Equal(0ul, timing.Repeats);
        Assert.Equal(0ul, timing.Skips);
    }

    [Fact]
    public void ALateLatchIsAbsorbedOnceAFrameWaits()
    {
        var timing = new DeckLinkOutputTiming();
        var a = timing.Latch();
        Assert.Equal(a, timing.TallyCompletion(1));
        Assert.Equal(a, timing.TallyCompletion(2));
        Assert.Equal(1ul, timing.Repeats);
        var b = timing.Latch();
        var c = timing.Latch();
        Assert.Equal(b, timing.TallyCompletion(3));
        Assert.Equal(c, timing.TallyCompletion(4));
        var d = timing.Latch();
        var e = timing.Latch();
        Assert.Equal(d, timing.TallyCompletion(5));
        Assert.Equal(e, timing.TallyCompletion(6));
        Assert.Equal(1ul, timing.Repeats);
        Assert.Equal(0ul, timing.Skips);
    }

    [Fact]
    public void ANothingWaitingRepeatsAndAFullQueueSkipsItsOldest()
    {
        var timing = new DeckLinkOutputTiming();
        var shown = timing.Latch();
        timing.TallyCompletion(1);
        Assert.Equal(shown, timing.TallyCompletion(2));
        timing.Latch();
        var second = timing.Latch();
        var third = timing.Latch();
        Assert.Equal(1ul, timing.Repeats);
        Assert.Equal(1ul, timing.Skips);
        Assert.DoesNotContain(shown, new[] { second, third });
        Assert.Equal(second, timing.TallyCompletion(3));
        Assert.Equal(third, timing.TallyCompletion(4));
    }

    [Fact]
    public void PrerollBeforeTheFirstLatchIsNotARepeat()
    {
        var timing = new DeckLinkOutputTiming();
        timing.TallyCompletion(1);
        timing.TallyCompletion(2);
        Assert.Equal(2ul, timing.Completions);
        Assert.Equal(0ul, timing.Repeats);
    }

    [Fact]
    public void CardResultsAndScheduleJumpsCount()
    {
        var timing = new DeckLinkOutputTiming();
        timing.TallyCompletion(1, DeckLinkCompletion.Late);
        timing.TallyCompletion(2, DeckLinkCompletion.Dropped, jumped: true);
        timing.TallyCompletion(3, DeckLinkCompletion.Flushed);
        Assert.Equal(1ul, timing.Late);
        Assert.Equal(1ul, timing.Dropped);
        Assert.Equal(1ul, timing.Jumps);
    }

    [Fact]
    public void ACopyDoesNotFollowTheOriginal()
    {
        var timing = new DeckLinkOutputTiming();
        timing.Latch();
        var copy = timing.Copy();
        timing.Latch();
        timing.TallyCompletion(1);
        Assert.Single(copy.Waiting);
        Assert.Equal(0ul, copy.Completions);
    }
}
