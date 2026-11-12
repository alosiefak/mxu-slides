using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Tests.Animation;

// Port of RenderEngineTests/SceneTransitionEngineTests.swift, plus the engine half of
// AnimationEvaluatorTests.testFilmBurnTransitionSweepsAWarmLeak. Time is injected (seconds).
public class SceneTransitionEngineTests
{
    private static AnimatedScene Scene(IEnumerable<string> ids, LayerKind layer = LayerKind.Slide)
    {
        var scene = new AnimatedScene(new SceneSize(192, 108));
        foreach (var id in ids)
            scene.AddItem(new RenderItem { Id = id, Frame = new SceneRect(0, 0, 10, 10), Content = new ItemContent.Media($"media-{id}", SceneMediaScaleMode.Fill) }, layer);
        return scene;
    }

    private static List<AnimatedItem> SlideItems(AnimatedScene scene) => scene[LayerKind.Slide].Items;

    [Fact]
    public void DurationsReachThirtySeconds()
    {
        Assert.Equal(12, new SceneTransition(SceneTransitionKind.Dissolve, 12).Duration); // a typed length past the slider plays
        Assert.Equal(30, new SceneTransition(SceneTransitionKind.Dissolve, 45).Duration);
    }

    [Fact]
    public void ContentEditsDoNotAnimateMembershipDoes()
    {
        Assert.Empty(SceneTransitionEngine.ChangedLayers(Scene(["a"]), Scene(["a"]))); // same ids = a content edit, never a fade
        Assert.Equal(["slide"], SceneTransitionEngine.ChangedLayers(Scene(["a"]), Scene(["b"])));
    }

    [Fact]
    public void AlphaLayerDissolveFadesWholeStacks()
    {
        var composite = SceneTransitionEngine.Composite(
            Scene(["stay", "leave"]), Scene(["stay", "arrive"]),
            new SceneTransition(SceneTransitionKind.Dissolve, 1), 0.25, [LayerKind.Slide]);
        var layer = composite[LayerKind.Slide];
        Assert.Equal(0.25, layer.CrossfadeProgress ?? -1, 0.001);
        Assert.Equal(["stay::leaving", "leave::leaving"], layer.Items.Where(i => i.CrossfadeLeaving).Select(i => i.Id));
        Assert.Equal(["stay", "arrive"], layer.Items.Where(i => !i.CrossfadeLeaving).Select(i => i.Id));
        Assert.All(layer.Items, i => Assert.Equal(1, i.Render.Opacity)); // the view mixes; items stay full

        var flat = SceneTransitionEngine.Flattened(composite)[LayerKind.Slide];
        Assert.Null(flat.CrossfadeProgress);
        Assert.Equal(0.75, flat.Items.First(i => i.Id == "leave::leaving").Render.Opacity, 0.001);
        Assert.Equal(0.25, flat.Items.First(i => i.Id == "arrive").Render.Opacity, 0.001);
        Assert.Equal(["stay"], flat.Items.Where(i => i.Id.StartsWith("stay", StringComparison.Ordinal)).Select(i => i.Id)); // staying once
        Assert.Equal(1, flat.Items.First(i => i.Id == "stay").Render.Opacity, 0.001);
        Assert.DoesNotContain(flat.Items, i => i.CrossfadeLeaving);
    }

    [Fact]
    public void EachLayerRunsOnItsOwnLength()
    {
        AnimatedScene Both(string id)
        {
            var scene = Scene([id]);
            scene.AddItem(new RenderItem { Id = "live-media-loopingVideos", Frame = new SceneRect(0, 0, 10, 10), Content = new ItemContent.Media($"bg-{id}", SceneMediaScaleMode.Fill) }, LayerKind.LoopingVideos);
            return scene;
        }
        var mid = SceneTransitionEngine.Composite(Both("a"), Both("b"), new Dictionary<string, SceneTransition>
        {
            ["slide"] = new(SceneTransitionKind.Dissolve, 1),
            ["loopingVideos"] = new(SceneTransitionKind.Dissolve, 0.25),
        }, elapsed: 0.5);
        Assert.Equal(["live-media-loopingVideos"], mid[LayerKind.LoopingVideos].Items.Select(i => i.Id)); // landed already
        Assert.Equal(0.5, mid[LayerKind.Slide].CrossfadeProgress ?? -1, 0.001);
    }

    [Fact]
    public void ACustomLayerSitsAboveItsAnchorAndTransitionsAsMedia()
    {
        // Port of CustomLayerTests.swift (the scene half): a custom layer composites above the layer it names.
        static AnimatedScene WithCustom(string media)
        {
            var scene = new AnimatedScene(new SceneSize(192, 108));
            scene.InsertCustomLayer("wall", "LED Wall", "videos");
            scene.AddItem(new RenderItem { Id = "live-media-wall", Frame = new SceneRect(0, 0, 10, 10), Content = new ItemContent.Media(media, SceneMediaScaleMode.Fill) }, "wall");
            return scene;
        }
        var ids = WithCustom("a").Layers.Select(l => l.Id).ToList();
        Assert.Equal(ids.IndexOf("videos") + 1, ids.IndexOf("wall"));
        Assert.Equal(["wall"], SceneTransitionEngine.ChangedLayers(WithCustom("a"), WithCustom("b")));
        var engine = new SceneTransitionEngine(WithCustom("a"));
        engine.Push(WithCustom("b"), (LayerKind kind) => kind == LayerKind.Videos ? new SceneTransition(SceneTransitionKind.Dissolve, 1) : null, now: 0);
        var mid = engine.Scene(0.5).Layer("wall")!;
        Assert.Null(mid.CrossfadeProgress); // a media layer holds the old picture under the new one
        Assert.Equal(2, mid.Items.Count);
        var copy = WithCustom("a").Copy();
        Assert.Equal(ids, copy.Layers.Select(l => l.Id)); // copies keep the custom layer in its place
    }

    [Fact]
    public void FadeShowsOldThenPlateThenNew()
    {
        var transition = SceneTransition.Fade(SceneColor.Black, 1);
        var early = SlideItems(SceneTransitionEngine.Composite(Scene(["old"]), Scene(["new"]), transition, 0.25, [LayerKind.Slide]));
        Assert.Contains(early, i => i.Id is "old" or "old::leaving");
        Assert.DoesNotContain(early, i => i.Id == "new"); // first half never leaks the new content
        Assert.Contains(early, i => i.Id.StartsWith("transition-plate"));
        var late = SlideItems(SceneTransitionEngine.Composite(Scene(["old"]), Scene(["new"]), transition, 0.75, [LayerKind.Slide]));
        Assert.DoesNotContain(late, i => i.Id.StartsWith("old"));
        Assert.Contains(late, i => i.Id == "new");
    }

    [Fact]
    public void EngineHoldsOutgoingMediaUntilTheFadeLands()
    {
        var engine = new SceneTransitionEngine(Scene(["a"]));
        engine.Push(Scene(["b"]), new SceneTransition(SceneTransitionKind.Dissolve, 0.2), now: 0);
        Assert.Equal(["media-a"], engine.HeldMediaIds(0));

        Assert.Empty(engine.HeldMediaIds(0.3));
        Assert.Equal(["b"], SlideItems(engine.Scene(0.3)).Select(i => i.Id));
    }

    [Fact]
    public void ContentOnlyPushRidesThroughAnActiveFade()
    {
        var engine = new SceneTransitionEngine(Scene(["a"]));
        engine.Push(Scene(["b"]), new SceneTransition(SceneTransitionKind.Dissolve, 5), now: 0);
        engine.Push(Scene(["b"]), new SceneTransition(SceneTransitionKind.Dissolve, 5), now: 0);
        Assert.Equal(["media-a"], engine.HeldMediaIds(0)); // the fade keeps running
        Assert.True(SlideItems(engine.Scene(0)).Select(i => i.Id).ToHashSet().SetEquals(["a::leaving", "b"])); // both sides still render
    }

    [Fact]
    public void CutLandsInstantlyAndCancelsAnyFade()
    {
        var engine = new SceneTransitionEngine(Scene(["a"]));
        engine.Push(Scene(["b"]), new SceneTransition(SceneTransitionKind.Dissolve, 5), now: 0);
        engine.Push(Scene(["c"]), SceneTransition.Cut, now: 0);
        Assert.Empty(engine.HeldMediaIds(0));
        Assert.Equal(["c"], SlideItems(engine.Scene(0)).Select(i => i.Id));
    }

    [Fact]
    public void MediaFilledShapesRideTheMediaContracts()
    {
        static AnimatedScene FillScene(string mediaId)
        {
            var scene = new AnimatedScene(new SceneSize(192, 108));
            scene.AddItem(new RenderItem
            {
                Id = "s", Frame = new SceneRect(0, 0, 100, 100),
                Content = new ItemContent.Shape(new ShapeStyle(SceneShapeKind.Rect, new SceneFill.Media(mediaId, SceneMediaScaleMode.Fill))),
            }, LayerKind.Slide);
            return scene;
        }
        Assert.Equal(["slide"], SceneTransitionEngine.ChangedLayers(FillScene("clip-a"), FillScene("clip-b"))); // a fill media swap is a content change
        var engine = new SceneTransitionEngine(FillScene("clip-a"));
        engine.Push(FillScene("clip-b"), new SceneTransition(SceneTransitionKind.Dissolve, 1), now: 0);
        Assert.Equal(["clip-a"], engine.HeldMediaIds(0)); // the outgoing fill player holds through the fade
    }

    [Fact]
    public void StableItemIdMediaSwapStillAnimates()
    {
        static AnimatedScene MediaScene(string mediaId)
        {
            var scene = new AnimatedScene(new SceneSize(192, 108));
            scene.AddItem(new RenderItem { Id = "live-media-loopingVideos", Frame = new SceneRect(0, 0, 192, 108), Content = new ItemContent.Media(mediaId, SceneMediaScaleMode.Fill) }, LayerKind.LoopingVideos);
            return scene;
        }
        Assert.Equal(["loopingVideos"], SceneTransitionEngine.ChangedLayers(MediaScene("clip-a"), MediaScene("clip-b")));
        var mid = SceneTransitionEngine.Composite(MediaScene("clip-a"), MediaScene("clip-b"),
            new SceneTransition(SceneTransitionKind.Dissolve, 1), 0.5, [LayerKind.LoopingVideos])[LayerKind.LoopingVideos].Items;
        Assert.Equal(2, mid.Count); // both clips render mid-fade
        Assert.Equal(2, mid.Select(i => i.Id).Distinct().Count()); // no colliding item ids
        Assert.Equal(1, mid.First(i => i.Id.EndsWith("::leaving")).Render.Opacity, 0.001); // the outgoing clip holds under
        Assert.Empty(SceneTransitionEngine.ChangedLayers(MediaScene("clip-a"), MediaScene("clip-a")));
    }

    [Fact]
    public void MediaClearFadesOutInsteadOfHoldAndPop()
    {
        var old = new AnimatedScene(new SceneSize(192, 108));
        old.AddItem(new RenderItem { Id = "live-media-videos", Frame = new SceneRect(0, 0, 192, 108), Content = new ItemContent.Media("intro", SceneMediaScaleMode.Fill) }, LayerKind.Videos);
        var mid = SceneTransitionEngine.Composite(old, new AnimatedScene(new SceneSize(192, 108)),
            new SceneTransition(SceneTransitionKind.Dissolve, 1), 0.5, [LayerKind.Videos])[LayerKind.Videos].Items;
        Assert.Single(mid);
        Assert.Equal(0.5, mid[0].Render.Opacity, 0.001); // the clear FADES
    }

    [Fact]
    public void FilmBurnTransitionSweepsAWarmLeak()
    {
        static AnimatedScene Gray(string id)
        {
            var scene = new AnimatedScene(new SceneSize(192, 108));
            scene.AddItem(new RenderItem
            {
                Id = id, Frame = new SceneRect(0, 0, 192, 108),
                Content = new ItemContent.Shape(new ShapeStyle(SceneShapeKind.Rect, new SceneFill.Solid(new SceneColor(0.2, 0.2, 0.2)))),
            }, LayerKind.Slide);
            return scene;
        }
        var engine = new SceneTransitionEngine(Gray("a"));
        engine.Push(Gray("b"), new SceneTransition(SceneTransitionKind.FilmBurn, 4), now: 0);
        var items = SlideItems(engine.Scene(2));
        Assert.Contains(items, i => i.Effects.Any(e => e is AnimationEffect.Burn)); // burn passes ride both sides
        var plate = items.First(i => i.Id.StartsWith("transition-plate")); // warm plate at the peak
        var color = Assert.IsType<SceneFill.Solid>(Assert.IsType<ItemContent.Shape>(plate.Render.Content).Style.Fill).Color;
        Assert.True(color.Red > color.Blue + 0.05); // warm: red over blue at mid-sweep
        Assert.DoesNotContain(SlideItems(engine.Scene(10)), i => i.Id.StartsWith("transition-plate"));
    }
}
