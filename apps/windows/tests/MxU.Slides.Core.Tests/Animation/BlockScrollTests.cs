using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Tests.Animation;

// Port of RenderEngineTests/BlockScrollTests.swift. TextRasterizer.blockHeight is the
// injected measurer (a fake line count × size here); the path-ticker and Metal cases aren't ported.
public class BlockScrollTests
{
    private static readonly SceneSize Canvas = new(192, 108);

    private static readonly TextBlockHeight BlockHeight = (text, _) => text.String.Split('\n').Length * text.FontSize * 1.2;

    private static AnimatedItem Credits(SceneBlockScroll scroll, double? anchor = 0, AnimationContext? context = null) => new()
    {
        Render = new RenderItem
        {
            Id = "credits", Frame = new SceneRect(20, 30, 152, 40),
            Content = new ItemContent.Text(new StyledText { String = string.Join("\n", Enumerable.Repeat("Line of credits", 12)), FontSize = 8 }),
        },
        Scroll = scroll,
        TickerAnchorHostTime = anchor,
        AnimationContext = context,
    };

    [Fact]
    public void ContinuousRollWrapsAndRampEasesEachPass()
    {
        var scroll = new SceneBlockScroll(50, SceneScrollAxis.Up);
        Assert.Equal(50, BlockScrollRoll.Pose(scroll, 3, 100, 60, anchored: true));
        var eased = scroll with { Ramp = SceneAnimationRamp.EaseInOut };
        Assert.Equal(50, BlockScrollRoll.Pose(eased, 1, 100, 60, anchored: true));
        Assert.True(BlockScrollRoll.Pose(eased, 0.5, 100, 60, anchored: true) < 25);
        Assert.Equal(0, BlockScrollRoll.Pose(new SceneBlockScroll(0), 9, 100, 60, anchored: true));
    }

    [Fact]
    public void FinitePassesRestOrLeaveOnlyWhenAnchored()
    {
        var scroll = new SceneBlockScroll(50, SceneScrollAxis.Up, Passes: 1);
        Assert.Null(BlockScrollRoll.Pose(scroll, 5, 100, 60, anchored: true)); // gone
        scroll = scroll with { RestAtEnd = true };
        Assert.Equal(60, BlockScrollRoll.Pose(scroll, 5, 100, 60, anchored: true));
        Assert.Equal(50, BlockScrollRoll.Pose(scroll, 5, 100, 60, anchored: false));
    }

    [Fact]
    public void RollExpandsTheBlockAndClipsToTheFrame()
    {
        var item = Credits(new SceneBlockScroll(40, SceneScrollAxis.Up, FadeTowardTop: 0.25));
        var blockHeight = Math.Ceiling(BlockHeight(((ItemContent.Text)item.Render.Content).Styled, 152));
        Assert.True(blockHeight > 40); // twelve lines overflow a 40-unit box

        var start = AnimationEvaluator.Resolve(item, Canvas, 0, BlockHeight);
        Assert.Single(start);
        Assert.Equal(blockHeight, start[0].Item.Render.Frame.Height, 0.001);
        Assert.Equal(40, start[0].Motion.Translate.Dy, 0.001);
        var clip = start[0].Motion.Clip!;
        Assert.Equal(-40 / blockHeight, clip.MinV, 0.001);
        Assert.Equal(40 / blockHeight, clip.MaxV - clip.MinV, 0.001);
        Assert.Equal(0.25 * 40 / blockHeight, clip.FeatherMinV, 0.001);
        Assert.Equal(0, clip.FeatherMaxV);
        Assert.Null(start[0].Item.Scroll); // the raster never sees the clock
        Assert.Equal(SceneTextVerticalAlignment.Top, Assert.IsType<ItemContent.Text>(start[0].Item.Render.Content).Styled.VerticalAlignment);

        Assert.Equal(0, AnimationEvaluator.Resolve(item, Canvas, 1, BlockHeight)[0].Motion.Translate.Dy, 0.001);

        var settled = AnimationEvaluator.Resolve(Credits(new SceneBlockScroll(40), context: AnimationContext.Settled), Canvas, 7, BlockHeight);
        Assert.Single(settled);
        Assert.Null(settled[0].Motion.Clip);
        Assert.Equal(40, settled[0].Item.Render.Frame.Height);
    }

    [Fact]
    public void FiniteRollLeavesOrRestsAndSidewaysKeepsTheFrame()
    {
        var one = new SceneBlockScroll(400, SceneScrollAxis.Up, Passes: 1);
        Assert.Empty(AnimationEvaluator.Resolve(Credits(one), Canvas, 60, BlockHeight)); // rolled off after its pass
        var rested = AnimationEvaluator.Resolve(Credits(one with { RestAtEnd = true }), Canvas, 60, BlockHeight);
        Assert.Single(rested);
        Assert.Equal(40, rested[0].Motion.Translate.Dy + rested[0].Item.Render.Frame.Height, 0.001);

        var left = AnimationEvaluator.Resolve(Credits(new SceneBlockScroll(152, SceneScrollAxis.Left)), Canvas, 0.5, BlockHeight);
        Assert.Equal((152, 40), (left[0].Item.Render.Frame.Width, left[0].Item.Render.Frame.Height)); // sideways rolls keep the frame-sized block
        Assert.Equal(76, left[0].Motion.Translate.Dx, 0.001);
        Assert.Equal(-0.5, left[0].Motion.Clip?.MinU ?? 9, 0.001);
    }
}
