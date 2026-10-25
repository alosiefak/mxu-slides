using MxU.Slides.Core.Library;

namespace MxU.Slides.Core.Tests.Library;

// FolderTreeLogic.swift has no Mac test of its own (its menus are tested through the UI); these
// pin the rules the Stage panel's folder submenus rely on.
public class FolderTreeTests
{
    [Fact]
    public void PathsBecomeASortedTreeWithEveryFolderOnTheWay()
    {
        var tree = FolderTree.Tree(["Youth", "Sunday/Pastor", null, "", "Sunday", "Sunday/Band", "adults"]);
        Assert.Equal(["adults", "Sunday", "Youth"], tree.Select(n => n.Name)); // sorted ignoring case; blanks are no folder
        var sunday = tree.Single(n => n.Name == "Sunday");
        Assert.Equal("Sunday", sunday.Path);
        Assert.Equal(["Band", "Pastor"], sunday.Children.Select(c => c.Name));
        Assert.Equal("Sunday/Band", sunday.Children[0].Path);
        Assert.Empty(tree.Single(n => n.Name == "Youth").Children);
    }

    [Fact]
    public void ADeepPathAloneStillMakesItsParents()
    {
        var tree = FolderTree.Tree(["Choir/Easter/2026"]);
        Assert.Equal("Choir", tree.Single().Name);
        Assert.Equal("Choir/Easter/2026", tree.Single().Children.Single().Children.Single().Path);
    }

    [Fact]
    public void EntriesAreFiledDirectlyInTheirFolder()
    {
        var layouts = new[] { ("Band", "Sunday"), ("Pastor", "Sunday/Pastor"), ("Built-in", (string?)null) };
        Assert.Equal(["Band"], FolderTree.In(layouts, l => l.Item2, "Sunday").Select(l => l.Item1));
        Assert.Equal(["Built-in"], FolderTree.In(layouts, l => l.Item2, null).Select(l => l.Item1));
    }
}
