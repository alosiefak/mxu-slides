using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.ProImport.Tests;

// Port of the hot-key tests in PresenterCoreTests/ImportLedgerTests.swift, plus the library round trip.
public class ProGroupHotKeysTests
{
    private static ImportLedger Ledger(Dictionary<string, string>? stamping = null)
    {
        var ledger = new ImportLedger { Id = "import-ledger", Entries = [] };
        foreach (var (name, letter) in stamping ?? []) ledger.Entries.Add(new ImportLedgerEntry { DocId = ProGroupHotKeys.LedgerPrefix + name, Hash = letter });
        return ledger;
    }

    private static int Index(GroupPalette palette, string name) => palette.Groups.FindIndex(g => g.Name == name);

    [Fact]
    public void KeepMineFillsUnsetOnly()
    {
        var palette = GroupPalettes.Defaults();
        palette.Groups[7].HotKey = "";
        var stamps = ProGroupHotKeys.Apply(palette, new Dictionary<string, string> { ["chorus"] = "y", ["intro"] = "j" }, ImportConflictPolicy.KeepMine, Ledger());
        Assert.Equal("", palette.Groups[7].HotKey);
        Assert.Equal("j", palette.Groups[Index(palette, "Intro")].HotKey);
        Assert.Equal(["hotkey.intro"], stamps.Select(s => s.DocId));
        Assert.Equal(["j"], stamps.Select(s => s.Hash));
    }

    [Fact]
    public void UpdateUneditedOverwritesOnlyStampedLetters()
    {
        var palette = GroupPalettes.Defaults();
        var intro = Index(palette, "Intro");
        var bridge = Index(palette, "Bridge");
        palette.Groups[intro].HotKey = "j";
        palette.Groups[bridge].HotKey = "q";
        var stamps = ProGroupHotKeys.Apply(palette, new Dictionary<string, string> { ["intro"] = "k", ["bridge"] = "w" },
            ImportConflictPolicy.UpdateUnedited, Ledger(new() { ["intro"] = "j" }));
        Assert.Equal("k", palette.Groups[intro].HotKey);
        Assert.Equal("q", palette.Groups[bridge].HotKey);
        Assert.Equal(["hotkey.intro"], stamps.Select(s => s.DocId));
    }

    [Fact]
    public void ReplaceWinsOutrightAndSeedEqualClearsToDefault()
    {
        var palette = GroupPalettes.Defaults();
        var chorus = Index(palette, "Chorus");
        var tag = Index(palette, "Tag");
        palette.Groups[chorus].HotKey = "";
        palette.Groups[tag].HotKey = "y";
        ProGroupHotKeys.Apply(palette, new Dictionary<string, string> { ["chorus"] = "c", ["tag"] = "t" }, ImportConflictPolicy.Replace, Ledger());
        Assert.Null(palette.Groups[chorus].HotKey);
        Assert.Null(palette.Groups[tag].HotKey);
        Assert.Equal("c", HotKeyJump.EffectiveHotKey(palette.Groups[chorus]));
    }

    [Fact]
    public void ImportingIntoTheLibrarySavesThePaletteAndStampsTheLedger()
    {
        using var library = new TestLibrary();
        Assert.Equal(1, ProGroupHotKeys.Apply(library.Store, new Dictionary<string, string> { ["intro"] = "j", ["chorus"] = "c" }, ImportConflictPolicy.UpdateUnedited));
        Assert.Equal("j", GroupPalettes.Current(library.Store).Groups.First(g => g.Name == "Intro").HotKey);
        Assert.Contains(ImportWriter.LoadLedger(library.Store).Entries, e => e.DocId == "hotkey.intro" && e.Hash == "j");

        // The operator changes it; the next import (Update unedited) leaves their letter alone.
        var palette = GroupPalettes.Current(library.Store);
        palette.Groups.First(g => g.Name == "Intro").HotKey = "u";
        library.Store.Save(palette);
        Assert.Equal(0, ProGroupHotKeys.Apply(library.Store, new Dictionary<string, string> { ["intro"] = "k" }, ImportConflictPolicy.UpdateUnedited));
        Assert.Equal("u", GroupPalettes.Current(library.Store).Groups.First(g => g.Name == "Intro").HotKey);
    }

    [Fact]
    public void AWholeImportsHotKeysMergeFirstOneWins()
    {
        var documents = new[]
        {
            new ProDocumentSummary { SourcePath = "a.pro", Name = "A", GroupHotKeys = new Dictionary<string, string> { ["verse1"] = "q", ["tag"] = "t" } },
            new ProDocumentSummary { SourcePath = "b.pro", Name = "B", GroupHotKeys = new Dictionary<string, string> { ["tag"] = "y" } },
        };
        var merged = ProGroupHotKeys.Merged(new Dictionary<string, string> { ["verse1"] = "w" }, documents);
        Assert.Equal("w", merged["verse1"]);
        Assert.Equal("t", merged["tag"]);
    }
}
