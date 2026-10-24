using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Media;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Automation;

// The Mac's restart rules (ServiceControls.land / fire(mediaItem:) / fire(overlay:)) have no unit
// tests there; these check the C# port. The app restarts the players these name (MainViewModel.RestartVideo).
public class VideoRestartsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    private static MediaItem Video(string id, bool loops = false, MediaClassification classification = MediaClassification.Foreground) => new()
    {
        Id = id, Name = id, MediaKind = MediaKind.Video, Classification = classification, FileHash = "", FileName = id + ".mp4",
        FileStatus = MediaFileStatus.Ready, StatusDetail = "", Tags = [], Favorite = false, Collections = [], Loops = loops,
    };

    private static SlideObject Filled(string id, string mediaId, bool? loops = null) => new()
    {
        Id = id, ObjectKind = SlideObjectKind.Shape, Name = id, Text = "", Fill = new ObjectFill { FillKind = FillKind.Media, MediaId = mediaId, Loops = loops },
    };

    [Fact]
    public void FiringAVideoAgainRestartsItUnlessItIsBackgroundMedia()
    {
        // The bug this fixes: firing a video again within a moment of it ending found its player
        // still paused at the end, so it never played again.
        Assert.True(VideoRestarts.OnMediaFired(Video("bumper")));
        Assert.False(VideoRestarts.OnMediaFired(Video("loop", loops: true, classification: MediaClassification.Background)));
    }

    [Fact]
    public void ASlidesOwnNonLoopingVideosRestartWhenItLands()
    {
        var items = new Dictionary<string, MediaItem> { ["once"] = Video("once"), ["loop"] = Video("loop", loops: true) };
        var slide = new Slide
        {
            Id = "s", Name = "S",
            Objects = [Filled("a", "once"), Filled("b", "loop"), Filled("c", "loop", loops: false), Filled("d", "once", loops: true)],
        };
        var restart = VideoRestarts.OnSlideLanded(slide, null, null, items.GetValueOrDefault, _ => null, Now);
        Assert.Equal(["once", "loop"], restart); // its own setting wins over the media item's
    }

    [Fact]
    public void AForegroundBackgroundRestartsOnlyOnceItHasPlayedOut()
    {
        var items = new Dictionary<string, MediaItem> { ["bumper"] = Video("bumper") };
        var slide = new Slide { Id = "s", Name = "S", Objects = [], Background = new CueMedia { MediaId = "bumper", Layer = CueMediaLayer.Videos, Loops = false } };
        var finished = new MediaTransportState(10, 10, false, false, Now);
        var playing = new MediaTransportState(10, 4, true, false, Now);
        Assert.Equal(["bumper"], VideoRestarts.OnSlideLanded(slide, null, null, items.GetValueOrDefault, _ => finished, Now));
        Assert.Empty(VideoRestarts.OnSlideLanded(slide, null, null, items.GetValueOrDefault, _ => playing, Now)); // carries on from the slide before
        Assert.Empty(VideoRestarts.OnSlideLanded(slide, null, null, items.GetValueOrDefault, _ => null, Now)); // no player yet: it starts by itself
    }

    [Fact]
    public void ALoopingBackgroundIsNeverRestarted()
    {
        var items = new Dictionary<string, MediaItem> { ["waves"] = Video("waves", loops: true, classification: MediaClassification.Background) };
        var slide = new Slide { Id = "s", Name = "S", Objects = [], Background = new CueMedia { MediaId = "waves", Layer = CueMediaLayer.LoopingVideos } };
        var ended = new MediaTransportState(10, 10, false, false, Now);
        Assert.Empty(VideoRestarts.OnSlideLanded(slide, null, null, items.GetValueOrDefault, _ => ended, Now));
    }

    [Fact]
    public void AnOverlaysNonLoopingVideosRestartWhenItGoesUp()
    {
        var items = new Dictionary<string, MediaItem> { ["sting"] = Video("sting"), ["loop"] = Video("loop", loops: true) };
        var overlay = new Overlay { Id = "o", Name = "Sting", Objects = [Filled("a", "sting"), Filled("b", "loop"), Filled("c", "sting")] };
        Assert.Equal(["sting"], VideoRestarts.OnOverlayFired(overlay, items.GetValueOrDefault));
    }
}
