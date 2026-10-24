using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Tests.Scene;

// Port of the GroupPalette hot-key tests in PresenterCoreTests/KeyboardControlTests.swift.
public class GroupPaletteTests
{
    [Fact]
    public void EffectiveHotKeyFallsBackToSeeds()
    {
        Assert.Equal("c", HotKeyJump.EffectiveHotKey(new GroupDefinition { Id = "chorus", Name = "Chorus", ColorHex = "#CC004EFF" }));
        Assert.Equal("s", HotKeyJump.EffectiveHotKey(new GroupDefinition { Id = "v2", Name = "Verse 2", ColorHex = "#005999FF" }));
    }

    [Fact]
    public void EmptyHotKeyIsExplicitlyNone()
    {
        var chorus = new GroupDefinition { Id = "chorus", Name = "Chorus", ColorHex = "#CC004EFF", HotKey = "" };
        Assert.Null(HotKeyJump.EffectiveHotKey(chorus));
        chorus.HotKey = "Q";
        Assert.Equal("q", HotKeyJump.EffectiveHotKey(chorus));
    }

    [Fact]
    public void FillUnsetHotKeysIsFirstImportWins()
    {
        var palette = GroupPalettes.Defaults();
        palette.Groups[7].HotKey = "";
        palette.Groups[12].HotKey = "q";
        GroupPalettes.FillUnsetHotKeys(palette, new Dictionary<string, string>
        {
            ["chorus"] = "y", ["bridge"] = "w", ["tag"] = "t", ["intro"] = "j", ["unknowngroup"] = "u", ["vamp"] = "5",
        });
        Assert.Equal("", palette.Groups[7].HotKey);
        Assert.Equal("q", palette.Groups[12].HotKey);
        Assert.Null(palette.Groups.First(g => g.Name == "Tag").HotKey);
        Assert.Equal("j", palette.Groups.First(g => g.Name == "Intro").HotKey);
        Assert.Null(palette.Groups.First(g => g.Name == "Vamp").HotKey);
        Assert.Equal("j", HotKeyJump.EffectiveHotKey(palette.Groups.First(g => g.Name == "Intro")));
    }

    [Fact]
    public void HotKeyTargetsPreservePaletteOrderAndDedupe()
    {
        var targets = HotKeyJump.HotKeyTargets(GroupPalettes.Defaults().Groups);
        Assert.Equal(["verse", "verse1"], targets["a"]);
        Assert.Equal(["chorus", "chorus1"], targets["c"]);
        Assert.DoesNotContain(targets.Values, names => names.Contains("blank"));
    }

    [Fact]
    public void ThePaletteIsALibraryDocument()
    {
        var root = Path.Combine(Path.GetTempPath(), "mxu-palette-" + Guid.NewGuid().ToString("N"));
        try
        {
            var library = new LibraryStore(root);
            library.Load();
            Assert.Equal(StarterContent.GroupPalette.Count, GroupPalettes.Current(library).Groups.Count);
            var palette = GroupPalettes.Defaults();
            palette.Groups[0].HotKey = "v";
            library.Save(palette);
            var reopened = new LibraryStore(root);
            reopened.Load();
            Assert.Equal("v", GroupPalettes.Current(reopened).Groups[0].HotKey);
            Assert.Equal("#CC004EFF", GroupPalettes.ColorsByNormalizedName(palette)["chorus"]);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }
}
