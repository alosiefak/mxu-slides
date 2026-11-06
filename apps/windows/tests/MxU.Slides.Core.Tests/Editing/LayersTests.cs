using MxU.Slides.Core.Editing;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Tests.Library;

namespace MxU.Slides.Core.Tests.Editing;

public sealed class LayersTests
{
    private static SlideObject O(string id, SlideObjectKind kind = SlideObjectKind.Shape, string? group = null) =>
        new() { Id = id, ObjectKind = kind, Name = id, Text = "", GroupId = group };

    private static List<SlideObject> Stack(params string[] ids) => [.. ids.Select(id => O(id))];

    private static string[] Ids(IEnumerable<SlideObject> objects) => [.. objects.Select(o => o.Id)];

    [Theory]
    [InlineData(LayerMove.BringForward, new[] { "a", "c", "b", "d" })]
    [InlineData(LayerMove.SendBackward, new[] { "b", "a", "c", "d" })]
    [InlineData(LayerMove.BringToFront, new[] { "a", "c", "d", "b" })]
    [InlineData(LayerMove.SendToBack, new[] { "b", "a", "c", "d" })]
    public void OneObjectMovesAsOnTheMac(LayerMove move, string[] expected)
    {
        var objects = Stack("a", "b", "c", "d");
        Assert.True(Layers.Move(objects, new HashSet<string> { "b" }, move));
        Assert.Equal(expected, Ids(objects));
    }

    [Fact]
    public void SeveralMoveTogetherKeepingTheirOrder()
    {
        var objects = Stack("a", "b", "c", "d", "e");
        Layers.Move(objects, new HashSet<string> { "a", "c" }, LayerMove.BringForward);
        Assert.Equal(["b", "a", "d", "c", "e"], Ids(objects));
        Layers.Move(objects, new HashSet<string> { "a", "c" }, LayerMove.BringToFront);
        Assert.Equal(["b", "d", "e", "a", "c"], Ids(objects));
        Layers.Move(objects, new HashSet<string> { "a", "c" }, LayerMove.SendToBack);
        Assert.Equal(["a", "c", "b", "d", "e"], Ids(objects));
    }

    [Fact]
    public void NothingMovesPastTheEnds()
    {
        var objects = Stack("a", "b");
        Assert.False(Layers.CanMove(objects, new HashSet<string> { "b" }, LayerMove.BringForward));
        Assert.False(Layers.CanMove(objects, new HashSet<string> { "b" }, LayerMove.BringToFront));
        Assert.True(Layers.CanMove(objects, new HashSet<string> { "b" }, LayerMove.SendBackward));
        Assert.False(Layers.Move(objects, new HashSet<string> { "missing" }, LayerMove.SendToBack));
        Assert.Equal(["a", "b"], Ids(objects));
    }

    [Fact]
    public void TheListIsTopFirstWithTheThemesDecorFixedBetween()
    {
        var template = new Slide
        {
            Id = "design", Name = "Lyrics",
            Objects = [O("band"), new SlideObject { Id = "ph", ObjectKind = SlideObjectKind.Text, Name = "Lyrics", Text = "" }],
        };
        var slide = new Slide { Id = "s", Name = "", Objects = [new SlideObject { Id = "words", ObjectKind = SlideObjectKind.Text, Name = "Lyrics", Text = "Hi" }, O("logo")] };
        var rows = Layers.TopFirst(slide.Objects, template);
        Assert.Equal(["logo", "words", "band"], rows.Select(r => r.Object.Id));
        Assert.Equal([false, false, true], rows.Select(r => r.FromTheme));

        // Dragging the logo below the words in the list sends it behind them in the slide.
        Assert.True(Layers.MoveInList(slide, template, "logo", beforeId: "band"));
        Assert.Equal(["logo", "words"], Ids(slide.Objects));
        Assert.False(Layers.MoveInList(slide, template, "band", beforeId: null), "theme decor stays put");
        Assert.True(Layers.MoveInList(slide, template, "logo", beforeId: "words"));
        Assert.Equal(["words", "logo"], Ids(slide.Objects));
    }

    [Fact]
    public void GroupingAndUngrouping()
    {
        var objects = Stack("a", "b", "c");
        Assert.Null(Layers.Group(objects, new HashSet<string> { "a" }));
        var group = Layers.Group(objects, new HashSet<string> { "a", "c" }, () => "g");
        Assert.Equal("g", group);
        Assert.Equal(["g", null, "g"], objects.Select(o => o.GroupId));
        Assert.True(Layers.CanUngroup(objects, new HashSet<string> { "a" }));
        Assert.True(Layers.Ungroup(objects, new HashSet<string> { "a", "c" }));
        Assert.All(objects, o => Assert.Null(o.GroupId));
        Assert.False(Layers.Ungroup(objects, new HashSet<string> { "a" }));
    }
}

public sealed class ObjectLocksTests : ScratchFolderTest
{
    [Fact]
    public void ALockIsAWindowsExtraThatUndoesLikeAnEdit()
    {
        var library = NewLibrary();
        var scope = library.Undo.NewScope("Editor");
        var locks = new ObjectLocks(library.Extras);
        Assert.False(locks.IsLocked("logo"));
        using (scope.Use()) locks.Set("logo", true);
        Assert.True(locks.IsLocked("logo"));
        Assert.True(new ObjectLocks(new LibraryStore(library.Root).Extras).IsLocked("logo"), "kept beside the library");
        scope.Undo();
        Assert.False(locks.IsLocked("logo"));
        scope.Redo();
        Assert.True(locks.IsLocked("logo"));
        // A pasted copy of the object is locked too.
        library.Extras.CopyOwner("logo", "logo-copy");
        Assert.True(locks.IsLocked("logo-copy"));
    }
}
