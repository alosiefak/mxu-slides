using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Tests.Scene;

// Port of the Mac's PresenterCoreTests/MediaCueRoutingTests.swift (the drop rules).
public class MediaCueRoutingTests
{
    private static MediaItem Item(MediaKind kind, MediaClassification classification) => new()
    {
        Id = "m1", Name = "Item", MediaKind = kind, Classification = classification,
        FileHash = "abc123", FileName = "item.bin", FileStatus = MediaFileStatus.Ready, StatusDetail = "",
        Loops = false,
    };

    [Fact]
    public void DropBetweenSlidesIsForegroundWhateverTheLibrarySays()
    {
        var media = MediaCueRouting.DroppedAsNewSlide(Item(MediaKind.Video, MediaClassification.Background));
        Assert.Equal(CueMediaLayer.Videos, media.Layer);
        Assert.Null(media.Mode);
        Assert.False(media.Loops, "a new-slide video plays once");
        Assert.Equal(MediaClassification.Foreground, media.Classification);
        var still = MediaCueRouting.DroppedAsNewSlide(Item(MediaKind.Image, MediaClassification.Background));
        Assert.Equal(CueMediaLayer.StillGraphics, still.Layer);
        Assert.Null(still.Loops);
        Assert.Equal(MediaClassification.Foreground, still.Classification);
    }

    [Fact]
    public void DroppedAsNewSlideByIdMatchesTheItemRule()
    {
        foreach (var kind in new[] { MediaKind.Image, MediaKind.Video })
        {
            var media = Item(kind, MediaClassification.Background);
            var byId = MediaCueRouting.DroppedAsNewSlide(media.Id, kind);
            var byItem = MediaCueRouting.DroppedAsNewSlide(media);
            Assert.Equal((byItem.MediaId, byItem.Layer, byItem.Loops, byItem.Classification, byItem.Mode),
                (byId.MediaId, byId.Layer, byId.Loops, byId.Classification, byId.Mode));
        }
    }
}
