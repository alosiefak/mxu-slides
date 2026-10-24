using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using static MxU.Slides.Core.Tests.Animation.AnimationFixtures;

namespace MxU.Slides.Core.Tests.Animation;

// The overlay editor's pictures must match what ShowState puts on the program for a live overlay.
public class OverlayPreviewTests
{
    private static SlideObject Plate(string id, List<AnimationStep>? steps = null, string? maskedBy = null) => new()
    {
        Id = id, ObjectKind = SlideObjectKind.Shape, Name = id, Text = "", X = 100, Y = 800, Width = 600, Height = 160,
        Fill = new ObjectFill { FillKind = FillKind.Solid, ColorHex = "#FFFFFFFF" }, AnimationSteps = steps, MaskObjectId = maskedBy,
    };

    private static Overlay Overlay(params SlideObject[] objects) => new() { Id = "ov", Name = "Lower third", Objects = [.. objects] };

    private static double? OpacityOf(AnimatedScene scene, string id, double at) =>
        AnimationEvaluator.Resolve(scene, at).Layers.SelectMany(l => l.Items).FirstOrDefault(r => r.Item.Id == id)?.Item.Render.Opacity;

    [Fact]
    public void ItemsMatchWhatTheShowPutsOnTheProgram()
    {
        var overlay = Overlay(Plate("plate", [Step("in", AnimationTrigger.WithPrevious, duration: 0.5)]));
        var show = new ShowState();
        show.Fire(overlay, atHostTime: 10);
        var live = show.AnimatedScene()[LayerKind.Overlays].Items;
        var preview = new AnimatedScene(SceneSize.Hd);
        OverlayPreview.AddItems(preview, overlay, new AnimationContext(10), 10);
        Assert.Equal(live.Select(i => i.Id), preview[LayerKind.Overlays].Items.Select(i => i.Id));
        Assert.Equal(live.Select(i => i.Render), preview[LayerKind.Overlays].Items.Select(i => i.Render));
    }

    [Fact]
    public void PlayThroughGoesUpHoldsAndComesDown()
    {
        var overlay = Overlay(Plate("plate",
        [
            Step("in", AnimationTrigger.WithPrevious, duration: 0.5),
            Step("out", AnimationTrigger.OnDismiss, AnimationKind.Out, duration: 0.5),
        ]));
        Assert.Equal(0.5 + AnimationSequence.PreviewExitHold + 0.5, OverlayPreview.Duration(overlay), 6);
        var scene = OverlayPreview.PlayThrough(overlay, SceneSize.Hd, startedAt: 100);
        const string id = "overlay-ov-plate";
        Assert.True(OpacityOf(scene, id, 100.0) is null or < 0.05, "starts hidden");
        Assert.True(OpacityOf(scene, id, 100.9) > 0.95, "up and holding");
        Assert.True(OpacityOf(scene, id, 102.5) is null or < 0.05, "taken down at the end");

        var frozen = OverlayPreview.PlayThrough(overlay, SceneSize.Hd, startedAt: 100, time: 0.9);
        Assert.True(OpacityOf(frozen, id, 500) > 0.95, "scrubbing freezes the picture at that moment");
    }

    [Fact]
    public void AStillOverlayShowsItsMattesAsGhosts()
    {
        var overlay = Overlay(Plate("matte"), Plate("text", maskedBy: "matte"));
        var still = OverlayPreview.Still(overlay, OverlayPreview.Plain(SceneSize.Hd, SceneColor.Black));
        var items = still[LayerKind.Overlays].Items;
        Assert.Equal("matte", items.Single(i => i.Id == "overlay-ov-matte").MatteGroup); // still hidden on its own
        Assert.Equal("matte", items.Single(i => i.Id == "overlay-ov-text").MaskedBy);
        var ghost = items.Single(i => i.Id.Contains("matte-ghost"));
        Assert.Null(ghost.MatteGroup); // the ghost is drawn
        Assert.Equal(new SceneRect(100, 800, 600, 160), ghost.Frame);
        Assert.DoesNotContain(OverlayPreview.Still(overlay, OverlayPreview.Plain(SceneSize.Hd, SceneColor.Black), showMattes: false)[LayerKind.Overlays].Items,
            i => i.Id.Contains("matte-ghost"));
    }

    [Fact]
    public void AnOverlayOnAnotherLayerIsDrawnThere()
    {
        var overlay = Overlay(Plate("plate"));
        overlay.Layer = LayerKind.StillGraphics.Raw();
        var still = OverlayPreview.Still(overlay, OverlayPreview.Plain(SceneSize.Hd, SceneColor.Black));
        Assert.Single(still[LayerKind.StillGraphics].Items);
        Assert.Empty(still[LayerKind.Overlays].Items);
    }

    [Fact]
    public void TheLiveBackdropLeavesOutTheOverlayBeingEdited()
    {
        var overlay = Overlay(Plate("plate"));
        var other = new Overlay { Id = "other", Name = "Bug", Objects = [Plate("logo")] };
        var show = new ShowState();
        show.Fire(overlay);
        show.Fire(other);
        var backdrop = OverlayPreview.Without(show.Scene(), overlay);
        Assert.Equal(["overlay-other-logo"], backdrop.AllItems.Select(i => i.Id));
    }
}
