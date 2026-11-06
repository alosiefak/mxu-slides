using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Media;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Core.Tests.Library;

namespace MxU.Slides.Core.Tests.Media;

// A media item's effects (the Mac's applyingMediaEffects) and its Windows look (fit, zoom, crop,
// volume) on the full-screen media in a scene.
public class MediaDressingTests : ScratchFolderTest
{
    private static AnimatedScene SceneWith(params (string Id, ItemContent Content)[] items)
    {
        var scene = new AnimatedScene(SceneSize.Hd);
        foreach (var (id, content) in items)
            scene.AddItem(new RenderItem { Id = id, Frame = new SceneRect(0, 0, 1920, 1080), Content = content }, LayerKind.LoopingVideos);
        return scene;
    }

    [Fact]
    public void AMediaItemsEffectsGoOnItsPictureWhereverItShows()
    {
        var scene = SceneWith(("bg", new ItemContent.Media("m1", SceneMediaScaleMode.Fill)), ("other", new ItemContent.Media("m2", SceneMediaScaleMode.Fill)));
        var effects = new List<Effect> { new() { EffectKind = EffectKind.Blur, Radius = 20 }, new() { EffectKind = EffectKind.Vignette, Amount = 0.5 } };
        var dressed = MediaDressing.Dress(scene, id => id == "m1" ? new MediaDress(effects, MediaLook.Default) : null);
        var bg = dressed[LayerKind.LoopingVideos].Items[0];
        Assert.Equal(20, bg.Render.BlurRadius); // blur travels as the item's blur, as a slide object's does
        Assert.IsType<AnimationEffect.Vignette>(Assert.Single(bg.Effects));
        Assert.Empty(dressed[LayerKind.LoopingVideos].Items[1].Effects);
        Assert.Empty(scene[LayerKind.LoopingVideos].Items[0].Effects); // the scene given is left as it was
    }

    [Fact]
    public void NothingToDressGivesTheSameScene()
    {
        var scene = SceneWith(("bg", new ItemContent.Media("m1", SceneMediaScaleMode.Fill)));
        Assert.Same(scene, MediaDressing.Dress(scene, _ => null));
        Assert.Same(scene, MediaDressing.Dress(scene, _ => new MediaDress(null, MediaLook.Default)));
    }

    [Fact]
    public void ALookSetsTheFitTheZoomAndTheCrop()
    {
        var scene = SceneWith(("bg", new ItemContent.Media("m1", SceneMediaScaleMode.Fill)));
        var look = new MediaLook(MediaLookFit.Fit, Scale: 1.5, Crop: new MediaCrop(0.1, 0, 0.1, 0.2));
        var item = MediaDressing.Dress(scene, _ => new MediaDress(null, look))[LayerKind.LoopingVideos].Items[0];
        var media = Assert.IsType<ItemContent.Media>(item.Render.Content);
        Assert.Equal(SceneMediaScaleMode.Fit, media.ScaleMode);
        Assert.Equal(new SceneSourceRect(0.1, 0, 0.8, 0.8), media.SourceRect);
        Assert.Equal(new SceneRect(-480, -270, 2880, 1620), item.Render.Frame); // half as big again, about the middle
    }

    [Fact]
    public void ShapesFilledWithMediaKeepTheirOwnFit()
    {
        var scene = new AnimatedScene(SceneSize.Hd);
        scene.AddItem(new RenderItem
        {
            Id = "box", Frame = new SceneRect(0, 0, 400, 300),
            Content = new ItemContent.Shape(new ShapeStyle(new SceneShapeKind.Rectangle(), new SceneFill.Media("m1", SceneMediaScaleMode.Fit))),
        }, LayerKind.Slide);
        Assert.Same(scene, MediaDressing.Dress(scene, _ => new MediaDress(null, new MediaLook(MediaLookFit.Stretch))));
    }

    [Fact]
    public void LooksAreTidiedAndTheDefaultIsNoLookAtAll()
    {
        Assert.True(MediaLook.Default.IsDefault);
        Assert.True(new MediaLook(Crop: MediaCrop.None).IsDefault);
        var wild = new MediaLook(MediaLookFit.Fill, Scale: 99, Crop: new MediaCrop(0.8, -1, 0.3, 0.2), Volume: 5).Tidied();
        Assert.Equal(MediaLook.MaximumScale, wild.Scale);
        Assert.Equal(1, wild.Volume);
        Assert.Equal(new MediaCrop(0.45, 0, 0.3, 0.2), wild.Crop); // under half from each edge, so something is always left
        Assert.Null(new MediaLook(Crop: new MediaCrop(0, 0, 0.00001, 0)).Tidied().Crop);
    }

    [Fact]
    public void ALookIsKeptBesideTheLibraryAndDroppedWhenTheFileChanges()
    {
        var library = NewLibrary();
        var looks = new MediaLookExtras(library.Extras);
        var item = Media("m1", "hash-one");
        Assert.True(looks.For(item).IsDefault);
        looks.Save(item, new MediaLook(MediaLookFit.Fit, Volume: 0.5));
        Assert.Equal(MediaLookFit.Fit, looks.For(item).Fit);
        Assert.Equal(0.5, new MediaLookExtras(NewLibrary().Extras).For(item).Volume); // read back from disk

        // A Mac replaces the picture: the crop and zoom were made for the old one, so they go.
        var replaced = Media("m1", "hash-two");
        Assert.True(looks.For(replaced).IsDefault);

        looks.Save(item, MediaLook.Default); // back to how a Mac shows it: nothing kept
        Assert.Empty(library.Extras.Owners(MediaLookExtras.Kind));

        looks.Save(item, new MediaLook(Scale: 1.2));
        var copy = Media("m2", "hash-one");
        looks.Copied(item, copy);
        Assert.Equal(1.2, looks.For(copy).Scale);
    }
}
