using MxU.Slides.Core.Model;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Tests.ViewModels;

// The transport strip's video marks (MxU's update #12): which marks the scrub bar draws, and the
// next-mark caption. MarksInPass is the Mac's VideoMarks.points, so its cases are the Mac's
// (VideoMarksTests.swift, pointsAreWindowSecondsInTimeOrderAndDropMarksOutsideTheTrim).
public class TransportMarksTests
{
    private static readonly List<VideoMark> Marks =
    [
        new() { Id = "c", Name = "Band walk-on", At = 40 },
        new() { Id = "a", Name = "Show QR", At = 5 },
        new() { Id = "b", Name = "Hide QR", At = 20 },
        new() { Id = "d", Name = "Show QR", At = 50 },
    ];

    [Fact]
    public void MarksAreSecondsInThePassInTimeOrderAndThoseOutsideTheTrimAreLeftOut()
    {
        Assert.Equal(["a", "b", "c", "d"], MainViewModel.MarksInPass(Marks, null, 60).Select(m => m.Id));

        var red = MainViewModel.MarksInPass([new VideoMark { Id = "r", Name = "Red", At = 3, ColorHex = "#FF3B30" }], null, 60);
        Assert.Equal("#FF3B30", red.Single().ColorHex);

        var trimmed = MainViewModel.MarksInPass(Marks, 10, 35);
        Assert.Equal(["b", "c"], trimmed.Select(m => m.Id));
        Assert.Equal([10.0, 30.0], trimmed.Select(m => m.At));
        Assert.Equal(20, Marks.Single(m => m.Id == "b").At); // the item's own marks are left as they are
    }

    [Fact]
    public void NoMarksDrawNothing()
    {
        Assert.Empty(MainViewModel.MarksInPass(null, null, 60));
        Assert.Empty(MainViewModel.MarksInPass(Marks, null, 0));
    }

    [Fact]
    public void TheCaptionNamesTheNextMarkAndTheTimeUntilIt()
    {
        Assert.Equal("· Next: Hide QR 0:14", MainViewModel.NextMarkCaption(new VideoMark { Name = "Hide QR", At = 20 }, 5.5));
        Assert.Equal("· Next: A very long mark… 1:05", MainViewModel.NextMarkCaption(new VideoMark { Name = "A very long mark name", At = 70 }, 5));
    }
}
