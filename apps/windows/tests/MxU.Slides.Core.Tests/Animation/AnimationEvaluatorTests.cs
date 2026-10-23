using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Tests.Animation;

// Port of RenderEngineTests/AnimationEvaluatorTests.swift. The Mac cases that render
// pixels through Metal keep only their evaluator assertions (what the renderer is
// handed); the Compositor quad/mesh geometry cases are renderer work, not ported.
public class AnimationEvaluatorTests
{
    private static readonly SceneSize Canvas = new(192, 108);

    private static ItemContent Solid(SceneColor color) => new ItemContent.Shape(new ShapeStyle(SceneShapeKind.Rect, new SceneFill.Solid(color)));

    private static AnimatedItem Box(IReadOnlyList<SceneAnimationStep> steps, AnimationContext? context = null, string id = "box") => new()
    {
        Render = new RenderItem { Id = id, Frame = new SceneRect(76, 34, 40, 40), Content = Solid(SceneColor.White) },
        AnimationSteps = steps,
        AnimationContext = context ?? new AnimationContext(0),
    };

    private static SceneAnimationStep LinearIn(
        SceneStepAnimation animation, double duration = 2, SceneAnimationEdge? edge = null, double fromScale = 0.95, double softEdge = 0) => new()
    {
        Id = "in", Kind = SceneAnimationKind.Enter, Animation = animation, Group = SceneAnimationGroup.Auto, Duration = duration,
        Ramp = SceneAnimationRamp.None, Edge = edge, FromScale = fromScale, SoftEdge = softEdge,
    };

    private static AnimatedItem Plain(string id, SceneRect frame, ItemContent content) => new RenderItem { Id = id, Frame = frame, Content = content };

    [Fact]
    public void PendingHidesDoneSettles()
    {
        var item = Box([new SceneAnimationStep { Id = "in", Kind = SceneAnimationKind.Enter, Animation = SceneStepAnimation.Fade, Group = SceneAnimationGroup.Click(0), Duration = 1 }]);
        Assert.Empty(AnimationEvaluator.Resolve(item, Canvas, 5)); // In pending = not drawn
        var clicked = item with { AnimationContext = new AnimationContext(0, clickHostTimes: [10]) };
        var settled = AnimationEvaluator.Resolve(clicked, Canvas, 20);
        Assert.Single(settled);
        Assert.Equal(1, settled[0].Item.Render.Opacity);
        Assert.True(settled[0].Motion.IsIdentity);
        var mid = AnimationEvaluator.Resolve(clicked, Canvas, 10.5);
        Assert.Equal(0.75, mid[0].Item.Render.Opacity, 0.001); // fade at eased 0.75
    }

    [Fact]
    public void MoveFromEdgeAndScaleFoldIntoMotion()
    {
        var m = AnimationEvaluator.Resolve(Box([LinearIn(SceneStepAnimation.Move, edge: SceneAnimationEdge.Left)]), Canvas, 1)[0].Motion;
        Assert.Equal(-58, m.Translate.Dx, 0.001); // halfway back from just past the left edge (frame.maxX = 116)
        Assert.Equal(0, m.Translate.Dy);
        Assert.Equal(0.75, AnimationEvaluator.Resolve(Box([LinearIn(SceneStepAnimation.Scale, fromScale: 0.5)]), Canvas, 1)[0].Motion.Scale, 0.001);
        var w = AnimationEvaluator.Resolve(Box([LinearIn(SceneStepAnimation.Wipe, edge: SceneAnimationEdge.Top, softEdge: 4)]), Canvas, 0.5)[0].Motion.Wipe;
        Assert.Equal(SceneAnimationEdge.Top, w?.Edge);
        Assert.Equal(0.25, w?.Progress ?? 0, 0.001);
        Assert.Equal(4, w?.Feather);

        var @out = LinearIn(SceneStepAnimation.Move, edge: SceneAnimationEdge.Right) with { Kind = SceneAnimationKind.Exit };
        var o = AnimationEvaluator.Resolve(Box([@out]), Canvas, 1)[0].Motion;
        Assert.Equal((192 - 76) * 0.5, o.Translate.Dx, 0.001);

        var tilted = Box([]) with { Tilt = 30 };
        Assert.Equal(30, AnimationEvaluator.Resolve(tilted, Canvas, 0)[0].Motion.Tilt);

        tilted = tilted with { Swing = -20, TiltPivot = TiltPivot.Bottom };
        var still = AnimationEvaluator.Resolve(tilted, Canvas, 0)[0].Motion;
        Assert.Equal(-20, still.Swing);
        Assert.Equal(TiltPivot.Bottom, still.TiltPivot);
        Assert.False(new AnimationMotion { Swing = 5 }.IsIdentity);
    }

    [Fact]
    public void MotionIdentityAndStretchKeystone()
    {
        // The non-Metal assertions of testQuadCornersSwingAndPivot and testStretchKeystoneStripMesh.
        Assert.False(new AnimationMotion { KeystoneBottom = 1.2 }.IsIdentity);
        Assert.False(new AnimationMotion { SkewY = 5 }.IsIdentity);
        Assert.True(new AnimationMotion { KeystoneTop = 0.5, KeystoneBottom = 1, KeystoneStretch = true }.UsesStretchKeystone);
        Assert.False(new AnimationMotion { KeystoneTop = 0.5, KeystoneStretch = false }.UsesStretchKeystone);
        Assert.False(new AnimationMotion { KeystoneStretch = true }.UsesStretchKeystone); // flat trapezoid = plain quad
    }

    [Fact]
    public void TextRangesSplitIntoHiddenBaseAndPiece()
    {
        var text = new StyledText { String = "one two\nthree", FontName = "Helvetica", FontSize = 40, Color = SceneColor.White };
        var r1 = new SceneAnimationRange(0, 4, 3);
        var r2 = new SceneAnimationRange(1, 0, 5);
        SceneAnimationStep[] steps =
        [
            new() { Id = "a", Kind = SceneAnimationKind.Enter, Animation = SceneStepAnimation.Fade, Group = SceneAnimationGroup.Click(0), Duration = 1, Ramp = SceneAnimationRamp.None, Ranges = [r1], PlaceholderUnderline = true },
            new() { Id = "b", Kind = SceneAnimationKind.Enter, Animation = SceneStepAnimation.Move, Group = SceneAnimationGroup.Click(1), Duration = 1, Ranges = [r2], Offset = new SceneVector(0, 20) },
        ];
        var item = Plain("t", new SceneRect(0, 0, 400, 200), new ItemContent.Text(text)) with { AnimationSteps = steps, AnimationContext = new AnimationContext(0) };

        var before = AnimationEvaluator.Resolve(item, new SceneSize(400, 200), 1);
        Assert.Single(before);
        Assert.Equal(2, before[0].Item.HiddenRuns.Count);
        Assert.True(before[0].Item.HiddenRuns.First(r => r.Column == 4).PlaceholderUnderline);

        item = item with { AnimationContext = new AnimationContext(0, clickHostTimes: [10]) };
        var running = AnimationEvaluator.Resolve(item, new SceneSize(400, 200), 10.5);
        Assert.Equal(2, running.Count);
        Assert.Equal([0, 4], running[0].Item.HiddenRuns.Select(r => r.Column).Order());
        Assert.False(running[0].Item.HiddenRuns.First(r => r.Column == 4).PlaceholderUnderline);
        Assert.Equal(0.5, running[1].Item.Render.Opacity, 0.001);

        Assert.Equal(["0/0/4", "1/0/5"], running[1].Item.HiddenRuns.Select(r => $"{r.Line}/{r.Column}/{r.Length}").Order());
        Assert.StartsWith("t#step", running[1].Item.Id);
    }

    [Fact]
    public void ComplementRunsMergeAndClamp()
    {
        var runs = AnimationEvaluator.ComplementRuns([new SceneAnimationRange(0, 2, 3), new SceneAnimationRange(0, 4, 10)], "abcdefghij\n");
        Assert.Single(runs);
        Assert.Equal(0, runs[0].Column);
        Assert.Equal(2, runs[0].Length); // kept 2…10 (merged, clamped) → hidden 0…2 only
    }

    [Fact]
    public void ItemMidMoveIsAtTheInterpolatedPosition()
    {
        // testItemMidMoveRendersAtInterpolatedPosition: halfway in, the box (centre 96) draws at 38; done, at home.
        var item = Box([LinearIn(SceneStepAnimation.Move, edge: SceneAnimationEdge.Left)]);
        var mid = AnimationEvaluator.Resolve(item, Canvas, 1)[0];
        Assert.Equal(38, 96 + mid.Motion.Translate.Dx, 0.001);
        Assert.True(AnimationEvaluator.Resolve(item, Canvas, 5)[0].Motion.IsIdentity);
    }

    [Fact]
    public void DrawTracesTheOutlineAndFillsLast()
    {
        // testDrawTracesTheOutlineClockwiseFromTopLeft: a quarter of the perimeter at a quarter of the step.
        var item = Plain("frame", new SceneRect(46, 24, 100, 60),
            new ItemContent.Shape(new ShapeStyle(SceneShapeKind.Rect, new SceneFill.Solid(SceneColor.Clear), new SceneStroke(SceneColor.White, 4))))
            with { AnimationSteps = [LinearIn(SceneStepAnimation.Draw)], AnimationContext = new AnimationContext(0) };
        var quarter = AnimationEvaluator.Resolve(item, Canvas, 0.5)[0].Item;
        Assert.Equal(new StrokeTrim(0, 0.25), quarter.StrokeTrim);
        Assert.Equal(0, quarter.FillOpacity);
        var done = AnimationEvaluator.Resolve(item, Canvas, 5)[0].Item;
        Assert.Null(done.StrokeTrim); // closed outline once done
        Assert.Equal(1, done.FillOpacity);

        var reversed = item with { AnimationSteps = [LinearIn(SceneStepAnimation.Draw) with { Reverse = true }] };
        Assert.True(AnimationEvaluator.Resolve(reversed, Canvas, 0.5)[0].Item.StrokeTrim?.Reversed);
    }

    [Fact]
    public void TypeRevealsCharactersInOrderWithACaret()
    {
        var text = new StyledText { String = "AAAAAAAA", FontName = "Helvetica-Bold", FontSize = 60, Alignment = SceneTextAlignment.Left, VerticalAlignment = SceneTextVerticalAlignment.Top };
        var item = Plain("t", new SceneRect(0, 0, 400, 120), new ItemContent.Text(text)) with { AnimationSteps = [LinearIn(SceneStepAnimation.Type)], AnimationContext = new AnimationContext(0) };
        var half = AnimationEvaluator.Resolve(item, new SceneSize(400, 120), 1);
        Assert.Equal([new HiddenTextRun(0, 4, 4, Caret: true)], half[0].Item.HiddenRuns); // first letters typed, tail not yet

        var runs = AnimationEvaluator.TypeRuns(0.5, [new SceneAnimationRange(0, 0, 8)], "AAAAAAAA", caret: true, reverse: false);
        Assert.Single(runs);
        Assert.Equal(4, runs[0].Column);
        Assert.Equal(4, runs[0].Length);
        Assert.True(runs[0].Caret);

        var two = AnimationEvaluator.TypeRuns(0.5, [new SceneAnimationRange(0, 0, 4), new SceneAnimationRange(1, 0, 4)], "AAAA\nBBBB", caret: false, reverse: false);
        Assert.Equal(["1:0:4"], two.Select(r => $"{r.Line}:{r.Column}:{r.Length}"));
    }

    private static SceneAnimationStep MorphStep(AnimatedItem target, double duration = 2) => new()
    {
        Id = "m", Kind = SceneAnimationKind.Morph, Animation = SceneStepAnimation.Fade, Group = SceneAnimationGroup.Auto,
        Duration = duration, Ramp = SceneAnimationRamp.None, ToItem = target,
    };

    [Fact]
    public void MorphTweensFrameAndColorAndCrossFadesShapeKind()
    {
        var start = Plain("s", new SceneRect(20, 20, 40, 40), Solid(new SceneColor(1, 0, 0)));
        var end = Plain("s", new SceneRect(120, 40, 60, 60), new ItemContent.Shape(new ShapeStyle(new SceneShapeKind.Ellipse(), new SceneFill.Solid(new SceneColor(0, 0, 1)))));
        var item = start with { AnimationSteps = [MorphStep(end)], AnimationContext = new AnimationContext(0) };

        var state = AnimationEvaluator.MorphStateOf(item, item.AnimationContext, 1);
        Assert.Equal(95, state.Base.Render.Frame.X + state.Base.Render.Frame.Width / 2, 0.6);
        Assert.Equal(50, state.Base.Render.Frame.Width, 0.6);
        Assert.NotNull(state.Partner); // rectangle → ellipse can't tween: cross-fade
        Assert.Equal(0.5, state.Base.Render.Opacity, 0.01);
        Assert.Equal(0.5, state.Partner.Render.Opacity, 0.01);

        // Mid-way both colours show; done, only the blue end state at its frame.
        var mid = AnimationEvaluator.Resolve(item, Canvas, 1);
        Assert.Equal(["s", "s#morph"], mid.Select(r => r.Item.Id));
        var done = AnimationEvaluator.Resolve(item, Canvas, 5);
        Assert.Single(done);
        Assert.Equal(end.Render.Frame, done[0].Item.Render.Frame);
        Assert.True(AnimationEvaluator.ContentEquals(end.Render.Content, done[0].Item.Render.Content));

        var rounded = end with { Render = end.Render with { Content = new ItemContent.Shape(new ShapeStyle(new SceneShapeKind.RoundedRectangle(20), new SceneFill.Solid(new SceneColor(0, 0, 1)))) } };
        var item2 = start with { AnimationSteps = [MorphStep(rounded)], AnimationContext = new AnimationContext(0) };
        var s2 = AnimationEvaluator.MorphStateOf(item2, item2.AnimationContext, 1);
        Assert.Null(s2.Partner);
        var kind = Assert.IsType<SceneShapeKind.RoundedRectangle>(Assert.IsType<ItemContent.Shape>(s2.Base.Render.Content).Style.Kind);
        Assert.Equal(10, kind.CornerRadius, 0.2);
    }

    [Fact]
    public void TextMorphScalesOnTheQuadAndChainedMorphsFold()
    {
        var text = new StyledText { String = "Hi", FontName = "Helvetica", FontSize = 40 };
        var a = Plain("t", new SceneRect(0, 0, 100, 40), new ItemContent.Text(text));
        var b = a with { Render = a.Render with { Frame = new SceneRect(50, 20, 200, 80) } };
        var c = a with { Render = a.Render with { Frame = new SceneRect(300, 0, 100, 40) } };
        var item = a with
        {
            AnimationSteps =
            [
                new() { Id = "m1", Kind = SceneAnimationKind.Morph, Animation = SceneStepAnimation.Fade, Group = SceneAnimationGroup.Auto, Duration = 2, Ramp = SceneAnimationRamp.None, ToItem = b },
                new() { Id = "m2", Kind = SceneAnimationKind.Morph, Animation = SceneStepAnimation.Fade, Group = SceneAnimationGroup.Click(0), Duration = 2, Ramp = SceneAnimationRamp.None, ToItem = c },
            ],
            AnimationContext = new AnimationContext(0),
        };
        var mid = AnimationEvaluator.MorphStateOf(item, item.AnimationContext, 1);
        Assert.Equal(a.Render.Frame, mid.Base.Render.Frame); // text keeps its raster frame…
        Assert.Equal(1.5, mid.Motion.Scale, 0.001); // …and scales on the quad
        Assert.Equal(1.5, mid.Motion.ScaleY ?? 0, 0.001);
        Assert.Equal(50, mid.Motion.Translate.Dx, 0.001); // center 50 → 100
        Assert.Null(mid.Partner); // same text: it tweens, no cross-fade

        item = item with { AnimationContext = new AnimationContext(0, clickHostTimes: [10]) };
        Assert.Equal(b.Render.Frame, AnimationEvaluator.MorphStateOf(item, item.AnimationContext, 9).Base.Render.Frame);
        var second = AnimationEvaluator.MorphStateOf(item, item.AnimationContext, 11);
        Assert.Equal(b.Render.Frame, second.Base.Render.Frame); // chained morph starts from the previous end state
        Assert.Equal((350 - 150) * 0.5, second.Motion.Translate.Dx, 0.001);
    }

    [Fact]
    public void EnterFromCustomStartPoseTweensIn()
    {
        var home = Plain("b", new SceneRect(76, 34, 40, 40), Solid(SceneColor.White));
        var from = home with { Render = home.Render with { Frame = new SceneRect(-60, 34, 20, 20) } };
        var item = home with
        {
            AnimationSteps = [new() { Id = "in", Kind = SceneAnimationKind.Enter, Animation = SceneStepAnimation.Move, Group = SceneAnimationGroup.Auto, Duration = 2, Ramp = SceneAnimationRamp.None, FromItem = from }],
            AnimationContext = new AnimationContext(0),
        };
        var mid = AnimationEvaluator.Resolve(item, Canvas, 1)[0];
        Assert.Equal(new SceneRect(8, 34, 30, 30), mid.Item.Render.Frame); // centred on (23, 49), not home yet
        Assert.Equal(SceneVector.Zero, mid.Motion.Translate);
        Assert.Equal(home.Render.Frame, AnimationEvaluator.Resolve(item, Canvas, 3)[0].Item.Render.Frame); // home
    }

    [Fact]
    public void BlurBurnGlitchResolveIntoTheChainAndSettleClean()
    {
        foreach (var animation in new[] { SceneStepAnimation.Blur, SceneStepAnimation.Burn, SceneStepAnimation.Glitch })
        {
            var item = Box([LinearIn(animation)]);
            var mid = AnimationEvaluator.Resolve(item, Canvas, 1);
            Assert.Single(mid);
            Assert.NotEmpty(mid[0].Item.Effects); // rides the effect slot mid-way
            var done = AnimationEvaluator.Resolve(item, Canvas, 5);
            Assert.Empty(done[0].Item.Effects); // leaves no pass once settled
            Assert.Equal(1, done[0].Item.Render.Opacity);
        }

        var blurred = Box([LinearIn(SceneStepAnimation.Blur) with { Amount = 24 }]);
        var radius = Assert.IsType<AnimationEffect.Blur>(AnimationEvaluator.Resolve(blurred, Canvas, 0.5)[0].Item.Effects[0]).Radius;
        Assert.Equal(18, radius, 0.001); // 75% radius a quarter of the way in
    }

    [Fact]
    public void ColorEmphasisTintsOnTheEnvelopeAndSettlesClean()
    {
        var step = new SceneAnimationStep
        {
            Id = "em", Kind = SceneAnimationKind.Emphasis, Animation = SceneStepAnimation.Color, Group = SceneAnimationGroup.Auto,
            Duration = 2, Ramp = SceneAnimationRamp.None, Amount = 1, Color = new SceneColor(1, 0, 0),
        };
        var item = Box([step]);

        var mid = AnimationEvaluator.Resolve(item, Canvas, 1);
        Assert.Single(mid);
        var tint = Assert.IsType<AnimationEffect.Tint>(mid[0].Item.Effects[0]); // color emphasis rides the effect slot mid-step
        Assert.Equal(new SceneColor(1, 0, 0), tint.Color);
        Assert.Equal(1, tint.Amount, 0.01); // sin(π/2) peak × amount 1

        var early = Assert.IsType<AnimationEffect.Tint>(AnimationEvaluator.Resolve(item, Canvas, 0.5)[0].Item.Effects[0]);
        Assert.Equal(Math.Sin(0.25 * Math.PI), early.Amount, 0.01);

        var done = AnimationEvaluator.Resolve(item, Canvas, 5);
        Assert.Empty(done[0].Item.Effects);
        Assert.Equal(1, done[0].Item.Render.Opacity);

        Assert.Empty(AnimationEvaluator.Resolve(Box([step with { Color = null }]), Canvas, 1)[0].Item.Effects);
    }

    [Fact]
    public void MorphBlendRidesBothSidesAndPeaksMidway()
    {
        var start = Plain("s", new SceneRect(20, 20, 40, 40), Solid(SceneColor.White));
        var end = start with { Render = start.Render with { Content = new ItemContent.Shape(new ShapeStyle(new SceneShapeKind.Ellipse(), new SceneFill.Solid(SceneColor.White))) } };
        var item = start with { AnimationSteps = [MorphStep(end) with { Animation = SceneStepAnimation.Glitch, Amount = 1 }], AnimationContext = new AnimationContext(0) };
        var mid = AnimationEvaluator.MorphStateOf(item, item.AnimationContext, 1);
        Assert.Contains(mid.Base.Effects, e => e is AnimationEffect.Glitch { Amount: > 0.9 });
        Assert.Contains(mid.Partner!.Effects, e => e is AnimationEffect.Glitch);
        var early = AnimationEvaluator.MorphStateOf(item, item.AnimationContext, 0.1);
        Assert.True(Assert.IsType<AnimationEffect.Glitch>(early.Base.Effects[0]).Amount < 0.2);
        Assert.Empty(AnimationEvaluator.MorphStateOf(item, item.AnimationContext, 5).Base.Effects);
    }

    [Fact]
    public void SettledContextShowsTheEndOfTheSequence()
    {
        var start = Plain("s", new SceneRect(20, 20, 40, 40), Solid(SceneColor.White));
        var end = start with { Render = start.Render with { Frame = new SceneRect(120, 40, 60, 60) } };
        var item = start with
        {
            AnimationSteps =
            [
                new() { Id = "in", Kind = SceneAnimationKind.Enter, Animation = SceneStepAnimation.Move, Group = SceneAnimationGroup.Click(2), Duration = 1 },
                new() { Id = "m", Kind = SceneAnimationKind.Morph, Animation = SceneStepAnimation.Fade, Group = SceneAnimationGroup.Click(4), Duration = 1, ToItem = end },
                new() { Id = "out", Kind = SceneAnimationKind.Exit, Animation = SceneStepAnimation.Fade, Group = SceneAnimationGroup.Exit, Duration = 1 },
            ],
            AnimationContext = AnimationContext.Settled,
        };
        var resolved = AnimationEvaluator.Resolve(item, Canvas, 0);
        Assert.Single(resolved); // shown (In done, Out on dismiss pending)
        Assert.Equal(end.Render.Frame, resolved[0].Item.Render.Frame); // morph folded
        Assert.True(resolved[0].Motion.IsIdentity);

        var leaves = start with
        {
            AnimationSteps = [new() { Id = "out", Kind = SceneAnimationKind.Exit, Animation = SceneStepAnimation.Fade, Group = SceneAnimationGroup.Click(0), Duration = 1 }],
            AnimationContext = AnimationContext.Settled,
        };
        Assert.Empty(AnimationEvaluator.Resolve(leaves, Canvas, 0));
    }

    [Fact]
    public void MovingPiecesLeaveTheirMatteBehind()
    {
        // testMovingItemKeepsItsMatteInPlace (the renderer half is pixels): a matte member's range
        // piece and morph partner never join the matte group themselves.
        var text = new StyledText { String = "one two" };
        var item = Plain("t", new SceneRect(0, 0, 400, 200), new ItemContent.Text(text)) with
        {
            AnimationSteps = [new() { Id = "a", Kind = SceneAnimationKind.Enter, Animation = SceneStepAnimation.Fade, Group = SceneAnimationGroup.Auto, Duration = 2, Ranges = [new SceneAnimationRange(0, 4, 3)] }],
            AnimationContext = new AnimationContext(0),
        };
        item = item with { Render = item.Render with { MatteGroup = "t" } };
        var resolved = AnimationEvaluator.Resolve(item, new SceneSize(400, 200), 1);
        Assert.Equal("t", resolved[0].Item.Render.MatteGroup);
        Assert.Null(resolved[1].Item.Render.MatteGroup);
    }
}
