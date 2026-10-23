using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Tests.Scene;

// Ported from SlideSceneTests/HotKeyJumpTests.swift, plus the hot-key defaults.
public class HotKeyJumpTests
{
    private static Slide S(string id, string section) => new() { Id = id, Name = id, SectionId = section };

    private static Presentation Song() => new()
    {
        Id = "p", Name = "Song", PresentationKind = PresentationKind.Deck, ThemeId = "",
        Slides = [S("v1a", "v1"), S("v1b", "v1"), S("c1", "c"), S("v2a", "v2")],
        Sections = [new() { Id = "v1", Name = "Verse 1" }, new() { Id = "c", Name = "Chorus" }, new() { Id = "v2", Name = "Verse 2" }],
        Arrangements = [new() { Id = "sunday", Name = "Sunday", SectionIds = ["v1", "c", "v2", "c"] }],
    };

    [Fact]
    public void BlockSectionNamesAlignWithPillStarts()
    {
        var names = HotKeyJump.BlockSectionNames(Song(), "sunday");
        Assert.Equal([0, 2, 3, 4], names.Select(n => n.Start));
        Assert.Equal(["verse1", "chorus", "verse2", "chorus"], names.Select(n => n.NormalizedName));
        var plain = HotKeyJump.BlockSectionNames(Song());
        Assert.Equal([0, 2, 3], plain.Select(n => n.Start));
        Assert.Equal(["verse1", "chorus", "verse2"], plain.Select(n => n.NormalizedName));
    }

    [Fact]
    public void HotKeyJumpsToFirstMatchFromOutside()
    {
        Assert.Equal(2, HotKeyJump.JumpStart(Song(), "sunday", ["chorus"], 0));
        Assert.Equal(2, HotKeyJump.JumpStart(Song(), "sunday", ["chorus"], null));
        Assert.Equal(2, HotKeyJump.JumpStart(Song(), null, ["chorus", "chorus1"], null));
    }

    [Fact]
    public void HotKeyCyclesThroughRepeatedBlocks()
    {
        Assert.Equal(4, HotKeyJump.JumpStart(Song(), "sunday", ["chorus"], 2));
        Assert.Equal(2, HotKeyJump.JumpStart(Song(), "sunday", ["chorus"], 4));
    }

    [Fact]
    public void HotKeyMissesQuietly()
    {
        Assert.Null(HotKeyJump.JumpStart(Song(), "sunday", ["bridge"], 0));
        Assert.Null(HotKeyJump.JumpStart(Song(), null, [], 0));
    }

    [Fact]
    public void HotKeySkipsZeroWidthBlocks()
    {
        var withEmpty = Song();
        withEmpty.Sections!.Add(new PresentationSection { Id = "b", Name = "Bridge" });
        withEmpty.Arrangements = [new() { Id = "a", Name = "A", SectionIds = ["v1", "b", "c"] }];
        Assert.Null(HotKeyJump.JumpStart(withEmpty, "a", ["bridge"], 0));
        Assert.Equal(2, HotKeyJump.JumpStart(withEmpty, "a", ["chorus"], 0));
    }

    [Fact]
    public void DefaultLettersFollowTheMac()
    {
        var targets = HotKeyJump.HotKeyTargets(StarterContent.GroupPalette);
        Assert.Contains("chorus", targets["c"]);
        Assert.Contains("verse1", targets["a"]);
        Assert.Contains("verse2", targets["s"]);
        Assert.Contains("prechorus", targets["p"]);
        Assert.Equal("prechorus2", HotKeyJump.NormalizedName("Pre-Chorus 2"));
        Assert.Null(HotKeyJump.EffectiveHotKey(new GroupDefinition { Name = "Chorus", HotKey = "" }));
        Assert.Equal("q", HotKeyJump.EffectiveHotKey(new GroupDefinition { Name = "Chorus", HotKey = "Q" }));
    }
}
