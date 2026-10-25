using MxU.Slides.Core.Model;
using MxU.Slides.Core.Undo;

namespace MxU.Slides.Core.Tests.Undo;

/// <summary>What a plain save is called on the Edit menu.</summary>
public sealed class UndoDescriptionTests : UndoTest
{
    private string NameOf(Action<Presentation> edit)
    {
        SaveQuietly(Deck());
        var deck = Held();
        edit(deck);
        Edit(deck);
        return Scope.UndoName ?? "(nothing)";
    }

    [Fact]
    public void Moving() => Assert.Equal("Move", NameOf(d => d.Slides[0].Objects[0].X = 5));

    [Fact]
    public void Rotating() => Assert.Equal("Rotate", NameOf(d => d.Slides[0].Objects[0].RotationDegrees = 15));

    [Fact]
    public void ResizingABoxThatHadASize()
    {
        SaveQuietly(Deck());
        var o = Held().Slides[0].Objects[0];
        (o.X, o.Y, o.Width, o.Height) = (0, 0, 100, 100);
        SaveQuietly(Held());
        o.Width = 200;
        Edit(Held());
        Assert.Equal("Resize", Scope.UndoName);
    }

    [Fact]
    public void Words()
    {
        Assert.Equal("Typing", NameOf(d => d.Slides[0].Objects[0].Text = "Hi"));
    }

    [Fact]
    public void Renaming() => Assert.Equal("Rename", NameOf(d => d.Name = "Easter"));

    [Fact]
    public void OneProperty() => Assert.Equal("Change Opacity", NameOf(d => d.Slides[0].Objects[0].Opacity = 0.5));

    [Fact]
    public void APropertyWithAFriendlierName() => Assert.Equal("Change Design", NameOf(d => d.Slides[1].ThemeSlideName = "Lyrics"));

    [Fact]
    public void SeveralProperties() => Assert.Equal("Edit Presentation", NameOf(d =>
    {
        d.Slides[0].Objects[0].Opacity = 0.5;
        d.Slides[0].Notes = "Pause here";
    }));

    [Fact]
    public void Slides()
    {
        Assert.Equal("Add Slide", NameOf(d => d.Slides.Add(Slide("s4", "Four"))));
    }

    [Fact]
    public void DeletingSlides() => Assert.Equal("Delete Slides", NameOf(d => d.Slides.RemoveRange(0, 2)));

    [Fact]
    public void ArrangingObjects() => Assert.Equal("Arrange", NameOf(d =>
    {
        d.Slides[0].Objects.Add(new SlideObject { Id = "box", ObjectKind = SlideObjectKind.Shape, Name = "Box", Text = "" });
        SaveQuietly(d);
        d.Slides[0].Objects.Reverse();
    }));

    [Fact]
    public void ServiceRows()
    {
        SaveQuietly(Service("service", "a", "b"));
        var service = Library.Get<Service>("service")!;
        service.Items.RemoveAt(0);
        Edit(service);
        Assert.Equal("Remove Service Item", Scope.UndoName);
    }

    [Fact]
    public void MediaAndOtherKinds()
    {
        Edit(Media("m1", "hash"));
        Assert.Equal("Add Media", Scope.UndoName);
    }

    [Fact]
    public void MenuWords()
    {
        Assert.Equal("Undo", Scope.UndoMenuText);
        Assert.Equal("Redo", Scope.RedoMenuText);
    }
}
