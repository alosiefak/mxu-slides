using MxU.Slides.Core.Media;

namespace MxU.Slides.Core.Tests.Media;

// Ported from RenderEngine/Tests/DropoutWatchTests.swift.
public class DropoutWatchTests
{
    [Fact]
    public void ARunIsReportedOnceWhenItEnds()
    {
        var watch = new DropoutWatch();
        Assert.Null(watch.Update(false, 1));
        Assert.Null(watch.Update(true, 2));
        Assert.Null(watch.Update(true, 2.25));
        var run = watch.Update(false, 2.5);
        Assert.Equal(2, run?.Start);
        Assert.Equal(0.5, run?.Seconds);
        Assert.Null(watch.Update(false, 3));
    }
}
