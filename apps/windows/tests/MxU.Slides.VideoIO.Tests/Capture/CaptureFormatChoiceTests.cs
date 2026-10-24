using MxU.Slides.VideoIO.Capture;
using Candidate = MxU.Slides.VideoIO.Capture.CaptureFormatChoice.Candidate;

namespace MxU.Slides.VideoIO.Tests.Capture;

/// <summary>Port of MediaEngineTests/CaptureFormatChoiceTests.swift, plus the Windows format mapping.</summary>
public sealed class CaptureFormatChoiceTests
{
    private const uint Yuv = 0x3276_7579; // '2vuy'
    private const uint Nv12 = 0x3432_3076; // '420v'

    private static Candidate Hd(int index, double[] rates, uint? subtype = null) => new(index, 1920, 1080, subtype ?? Yuv, rates);

    [Fact]
    public void ADefault25ModeMovesToTheThirtyClassAtTheSameSize()
    {
        var active = Hd(0, [25]);
        Assert.Equal(new CaptureFormatChoice(1, 29.97), CaptureFormatChoice.Best([active, Hd(1, [29.97, 30]), Hd(2, [50, 59.94, 60])], active));
    }

    [Fact]
    public void WithoutAThirtyClassTheFastestRateWins()
    {
        var active = Hd(0, [25]);
        Assert.Equal(new CaptureFormatChoice(1, 59.94), CaptureFormatChoice.Best([active, Hd(1, [50, 59.94, 60])], active));
    }

    [Fact]
    public void InputsOnOneDeviceShareTheFirstPick()
    {
        var shared = CaptureFormatChoice.SharedRates([("elgato", null), ("elgato", 30), ("elgato", 60), ("webcam", null)]);
        Assert.Equal(new Dictionary<string, double> { ["elgato"] = 30 }, shared);
    }

    [Fact]
    public void RatesPastSixtyAndOtherSizesAreIgnored()
    {
        var active = Hd(0, [30]);
        var uhd = new Candidate(1, 3840, 2160, Yuv, [60]);
        Assert.Equal(new CaptureFormatChoice(0, 30), CaptureFormatChoice.Best([active, uhd, Hd(2, [120])], active));
    }

    [Fact]
    public void ATieKeepsTheActivePixelFormatThenTheLowerIndex()
    {
        var active = Hd(2, [25], Nv12);
        Assert.Equal(new CaptureFormatChoice(1, 59.94),
            CaptureFormatChoice.Best([Hd(0, [59.94]), Hd(1, [59.94], Nv12), active, Hd(3, [59.94], Nv12)], active));
    }

    [Fact]
    public void ThePreferredRateWinsWhenTheDeviceOffersIt()
    {
        var active = Hd(0, [25]);
        Candidate[] formats = [active, Hd(1, [29.97002997, 30]), Hd(2, [59.94, 60])];
        Assert.Equal(new CaptureFormatChoice(1, 29.97002997), CaptureFormatChoice.Best(formats, active, preferred: 29.97));
        Assert.Equal(new CaptureFormatChoice(2, 60), CaptureFormatChoice.Best(formats, active, preferred: 60));
    }

    [Fact]
    public void APreferredRateTheDeviceLacksFallsBackToAutomatic()
    {
        var active = Hd(0, [25, 50]);
        Assert.Equal(new CaptureFormatChoice(0, 50), CaptureFormatChoice.Best([active], active, preferred: 59.94));
    }

    [Fact]
    public void RatesListsTheActiveSizeOnceEachSlowestFirst()
    {
        var active = Hd(0, [25]);
        var uhd = new Candidate(3, 3840, 2160, Yuv, [24]);
        var rates = CaptureFormatChoice.Rates([active, Hd(1, [59.94005994, 29.97002997]), Hd(2, [29.97, 120], Nv12), uhd], active);
        Assert.Equal(new[] { 25, 29.97, 59.94 }, rates);
    }

    [Fact]
    public void NoUsableRateAnswersNil()
    {
        var active = Hd(0, [120]);
        Assert.Null(CaptureFormatChoice.Best([active], active));
    }

    [Fact]
    public void WindowsSubtypesPackLikeFourCCs()
    {
        Assert.Equal(0x3231564Eu, CaptureFormatChoice.FourCC("NV12"));
        Assert.Equal(0x47504A4Du, CaptureFormatChoice.FourCC("MJPG"));
        Assert.Equal(CaptureFormatChoice.FourCC("RGB "), CaptureFormatChoice.FourCC("RGB"));
    }

    [Fact]
    public void AWindowsFormatBecomesACandidateWithItsOneRate()
    {
        var candidate = CaptureFormatChoice.CandidateFor(4, 1280, 720, "YUY2", 30000, 1001);
        Assert.Equal((4, 1280, 720), (candidate.Index, candidate.Width, candidate.Height));
        Assert.Equal(30000.0 / 1001, Assert.Single(candidate.FrameRates), 1e-9);
        Assert.Empty(CaptureFormatChoice.CandidateFor(0, 1, 1, "NV12", 30, 0).FrameRates);
    }

    [Fact]
    public void AWebcamListingPicksThirtyAtItsDefaultSize()
    {
        // What a typical USB webcam lists: MJPG and YUY2 at several sizes, one rate each.
        Candidate[] formats =
        [
            CaptureFormatChoice.CandidateFor(0, 640, 480, "YUY2", 30, 1),
            CaptureFormatChoice.CandidateFor(1, 1920, 1080, "MJPG", 30, 1),
            CaptureFormatChoice.CandidateFor(2, 1920, 1080, "MJPG", 15, 1),
            CaptureFormatChoice.CandidateFor(3, 1920, 1080, "YUY2", 5, 1),
            CaptureFormatChoice.CandidateFor(4, 1920, 1080, "MJPG", 60, 1),
        ];
        var active = formats[3];
        Assert.Equal(new CaptureFormatChoice(1, 30), CaptureFormatChoice.Best(formats, active));
        Assert.Equal(new[] { 5.0, 15, 30, 60 }, CaptureFormatChoice.Rates(formats, active));
    }
}
