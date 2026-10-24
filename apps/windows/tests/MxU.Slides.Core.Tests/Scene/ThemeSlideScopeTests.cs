using System.Reflection;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Tests.Scene;

// Theme.scoped(toSlideFolder:) / scoped(toSlideID:) (PresenterCore/ThemeSlideScope.swift): the looks a screen can show.
public class ThemeSlideScopeTests
{
    private static Theme Pack() => new()
    {
        Id = "digital", Name = "Digital", FontFamily = "Helvetica", FontSize = 72, TextColorHex = "#FFFFFFFF", BackgroundColorHex = "#000000FF",
        Slides =
        [
            new Slide { Id = "full", Name = "Lyrics", Objects = [] },
            new Slide { Id = "lower", Name = "Lyrics", Folder = "Lower Thirds", Objects = [] },
            new Slide { Id = "side", Name = "Lyrics", Folder = "Side Thirds", Objects = [] },
            new Slide { Id = "lower-2", Name = "Scripture", Folder = "lower thirds", Objects = [] },
        ],
    };

    [Fact]
    public void AFolderScopeKeepsOnlyThatFoldersDesignsIgnoringCase()
    {
        var pack = Pack();
        var lower = pack.ScopedToSlideFolder("LOWER THIRDS");
        Assert.Equal(["lower", "lower-2"], lower.Slides!.Select(s => s.Id));
        Assert.Equal(4, pack.Slides!.Count); // the library's theme is left alone
        Assert.Same(pack, pack.ScopedToSlideFolder("Nope")); // a folder with no designs keeps the whole theme
        Assert.Same(pack, pack.ScopedToSlideFolder(null));
        // A lyric slide now finds the lower-third "Lyrics" design.
        Assert.Equal("lower", SlideSceneBuilder.ThemeSlide(new Slide { Id = "s", Name = "V1", ThemeSlideName = "Lyrics", Objects = [] }, lower)?.Id);
    }

    [Fact]
    public void ASlideScopePinsOneDesign()
    {
        var pack = Pack();
        Assert.Equal(["side"], pack.ScopedToSlideId("side").Slides!.Select(s => s.Id));
        Assert.Same(pack, pack.ScopedToSlideId("missing"));
        Assert.True(pack.HasSlide("side"));
        Assert.True(pack.HasSlideFolder("side thirds"));
        Assert.False(pack.HasSlideFolder("Center"));
    }

    [Fact]
    public void FoldersAreListedOnceInOrder() =>
        Assert.Equal(["Lower Thirds", "Side Thirds"], Pack().SlideFolders());

    [Fact]
    public void AScopedThemeCopiesEveryThemeField()
    {
        // Guards the hand-written copy in ThemeSlideScope against the schema gaining a field.
        var pack = Pack();
        var scoped = pack.ScopedToSlideId("side");
        foreach (var property in typeof(Theme).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.Name != nameof(Theme.Slides)))
            Assert.Equal(property.GetValue(pack), property.GetValue(scoped));
    }
}
