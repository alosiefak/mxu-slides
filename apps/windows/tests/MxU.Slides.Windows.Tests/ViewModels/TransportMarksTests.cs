using MxU.Slides.Core.Media;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Tests.ViewModels;

// The transport strip's next-mark caption (MxU's update #12). Which marks the scrub bar draws is
// Core's VideoMarks.Points (Core.Tests' VideoMarksTests).
public class TransportMarksTests
{
    [Fact]
    public void TheCaptionNamesTheNextMarkAndTheTimeUntilIt()
    {
        Assert.Equal("· Next: Hide QR 0:14", MainViewModel.NextMarkCaption(new VideoMarks.Point("b", "Hide QR", 20), 5.5));
        Assert.Equal("· Next: A very long mark… 1:05", MainViewModel.NextMarkCaption(new VideoMarks.Point("l", "A very long mark name", 70), 5));
    }
}
