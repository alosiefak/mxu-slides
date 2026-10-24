using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Tests.Scene;

// The inspector's mask rules (SlideObjectInspector.swift maskCandidates / isMatte / maskObjectBinding),
// and how SlideSceneBuilder wires them for the renderer (the scene half of RenderEngineTests/ObjectMaskTests.swift).
public class ObjectMasksTests
{
    private static SlideObject Shape(string id, string? maskedBy = null, MaskMode? mode = null, string name = "") => new()
    {
        Id = id, ObjectKind = SlideObjectKind.Shape, Name = name, Text = "", MaskObjectId = maskedBy, MaskMode = mode,
        X = 0, Y = 0, Width = 100, Height = 100,
        Fill = new ObjectFill { FillKind = FillKind.Solid, ColorHex = "#FFFFFFFF" },
    };

    [Fact]
    public void CandidatesAreEveryOtherObjectThatWouldNotMakeALoop()
    {
        var a = Shape("a");
        var b = Shape("b", maskedBy: "a"); // b is masked by a
        var c = Shape("c", maskedBy: "b"); // c by b, so by a through b
        var d = Shape("d");
        SlideObject[] objects = [a, b, c, d];
        Assert.Equal(["d"], ObjectMasks.Candidates(a, objects).Select(o => o.Id)); // b and c lead back to a
        Assert.Equal(["a", "d"], ObjectMasks.Candidates(b, objects).Select(o => o.Id)); // c is masked by b
        Assert.Equal(["a", "b", "d"], ObjectMasks.Candidates(c, objects).Select(o => o.Id)); // nothing is masked by c
    }

    [Fact]
    public void LongOrBrokenChainsStopSafely()
    {
        // A chain that already loops (a ↔ b) or names a missing object must not hang.
        var a = Shape("a", maskedBy: "b");
        var b = Shape("b", maskedBy: "a");
        var c = Shape("c", maskedBy: "gone");
        var target = Shape("t");
        Assert.Equal(["a", "b", "c"], ObjectMasks.Candidates(target, [a, b, c, target]).Select(o => o.Id));
    }

    [Fact]
    public void AChainBackToTheTargetIsALoop()
    {
        var a = Shape("a");
        var b = Shape("b", maskedBy: "a");
        var c = Shape("c", maskedBy: "b");
        SlideObject[] objects = [a, b, c];
        Assert.DoesNotContain(ObjectMasks.Candidates(b, objects), o => o.Id == "c"); // c is masked by b: b can't be masked by c
        Assert.Contains(ObjectMasks.Candidates(c, objects), o => o.Id == "a"); // a further matte up the chain is fine
        Assert.DoesNotContain(ObjectMasks.Candidates(c, objects), o => o.Id == "c");
    }

    [Fact]
    public void MattesAndSettingAMask()
    {
        var matte = Shape("m");
        var subject = Shape("s");
        SlideObject[] objects = [matte, subject];
        Assert.False(ObjectMasks.IsMatte(matte, objects));
        subject.MaskMode = MaskMode.Out;
        ObjectMasks.SetMask(subject, "m");
        Assert.Equal("m", subject.MaskObjectId);
        Assert.Equal(MaskMode.Out, subject.MaskMode);
        Assert.True(ObjectMasks.IsMatte(matte, objects));
        ObjectMasks.SetMask(subject, "");
        Assert.Null(subject.MaskObjectId);
        Assert.Null(subject.MaskMode); // clearing the mask clears its mode too
    }

    [Fact]
    public void UnnamedObjectsAreCalledByTheirKind()
    {
        Assert.Equal("Shape", ObjectMasks.DisplayName(Shape("s")));
        Assert.Equal("Plate", ObjectMasks.DisplayName(Shape("s", name: "Plate")));
        Assert.Equal("Text", ObjectMasks.FallbackName(new SlideObject { Id = "t", ObjectKind = SlideObjectKind.Text, Name = "", Text = "Hi" }));
        var picture = Shape("p");
        picture.Fill = new ObjectFill { FillKind = FillKind.Media, MediaId = "photo" };
        Assert.Equal("Media", ObjectMasks.FallbackName(picture));
    }

    [Fact]
    public void TheSceneHidesMattesAndClipsWhatTheyMask()
    {
        // ObjectMaskTests.swift's scene: a matte and a subject masked "in" and then "out".
        foreach (var mode in new[] { MaskMode.In, MaskMode.Out })
        {
            SlideObject[] objects = [Shape("matte"), Shape("subject", maskedBy: "matte", mode: mode)];
            var scene = new RenderScene(SceneSize.Hd);
            SlideSceneBuilder.AddObjects(scene, objects, LayerKind.Overlays, SceneSize.Hd, "ov-");
            var items = scene.AllItems.ToList();
            Assert.Equal("matte", items.Single(i => i.Id == "ov-matte").MatteGroup);
            var subject = items.Single(i => i.Id == "ov-subject");
            Assert.Equal("matte", subject.MaskedBy);
            Assert.Equal(mode == MaskMode.Out, subject.MaskOut);
        }
        // A mask naming an object that isn't there is ignored rather than hiding the subject.
        var orphan = new RenderScene(SceneSize.Hd);
        SlideSceneBuilder.AddObjects(orphan, [Shape("subject", maskedBy: "gone")], LayerKind.Overlays, SceneSize.Hd, "ov-");
        Assert.Null(orphan.AllItems.Single().MaskedBy);
    }
}
