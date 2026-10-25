using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Tests.Lyrics;

// The arrangement editor's rules: ported from SlideSceneTests/SlideSceneBuilderTests.swift
// (arrangementControlsNeedASectionedSlide, arrangementBlockStartsAlignWithPills' stray slide) and
// the behaviour of AppModel.addArrangement / updateArrangement / deleteArrangement and
// ArrangementStrip.dropBlock, which the Mac has no package tests for. Duplicate and typed orders
// are Windows additions.
public class ArrangementEditingTests
{
    private static Slide S(string id, string? section) => new() { Id = id, Name = id, SectionId = section };

    private static Presentation Song() => new()
    {
        Id = "p", Name = "Song", PresentationKind = PresentationKind.Song, ThemeId = "",
        Slides = [S("v1a", "v1"), S("v1b", "v1"), S("c1", "c"), S("v2a", "v2")],
        Sections = [new() { Id = "v1", Name = "Verse 1" }, new() { Id = "c", Name = "Chorus" }, new() { Id = "v2", Name = "Verse 2" }],
        Arrangements = [new() { Id = "sunday", Name = "Sunday", SectionIds = ["v1", "c", "v2", "c"] }],
    };

    [Fact]
    public void ArrangementControlsNeedASectionedSlide()
    {
        var song = Song();
        Assert.True(ArrangementEditing.ShowsArrangements(song));

        var unsectioned = Song();
        unsectioned.Arrangements = null;
        foreach (var slide in unsectioned.Slides) slide.SectionId = null;
        Assert.False(ArrangementEditing.ShowsArrangements(unsectioned));

        unsectioned.Arrangements = Song().Arrangements;
        Assert.True(ArrangementEditing.ShowsArrangements(unsectioned));
    }

    [Fact]
    public void ANewArrangementIsEachSectionOnceInSlideOrder()
    {
        var song = Song();
        song.Slides.Insert(2, S("ghost", "gone")); // a slide pointing at a section that isn't there
        song.Slides.Add(S("v1c", "v1"));            // Verse 1 again later: still once
        song.Sections!.Add(new() { Id = "unused", Name = "Bridge" });
        var added = ArrangementEditing.Add(song, id: "new");
        Assert.NotNull(added);
        Assert.Equal(["v1", "c", "v2"], added.SectionIds);
        Assert.Equal("Arrangement 2", added.Name);
        Assert.Equal(["sunday", "new"], song.Arrangements!.Select(a => a.Id));
    }

    [Fact]
    public void NoSectionsMeansNothingToArrange()
    {
        var song = Song();
        foreach (var slide in song.Slides) slide.SectionId = null;
        song.Arrangements = null;
        Assert.Null(ArrangementEditing.Add(song));
        Assert.Null(song.Arrangements);
    }

    [Fact]
    public void DeletingTheDefaultGoesBackToTheSlideOrder()
    {
        var song = Song();
        song.DefaultArrangementId = "sunday";
        Assert.True(ArrangementEditing.Delete(song, "sunday"));
        Assert.Null(song.DefaultArrangementId);
        Assert.Null(song.Arrangements);
        Assert.False(ArrangementEditing.Delete(song, "sunday"));
        // A service row still naming it plays the slides as written.
        Assert.Equal(4, SlideSceneBuilder.ArrangedSlides(song, "sunday").Count);
    }

    [Fact]
    public void RenamingTrimsAndIgnoresBlanks()
    {
        var song = Song();
        Assert.True(ArrangementEditing.Rename(song, "sunday", "  Easter  "));
        Assert.Equal("Easter", song.Arrangements![0].Name);
        Assert.False(ArrangementEditing.Rename(song, "sunday", "   "));
        Assert.Equal("Easter", song.Arrangements[0].Name);
        Assert.False(ArrangementEditing.Rename(song, "missing", "X"));
    }

    [Fact]
    public void DuplicateCopiesTheOrderUnderAFreeName()
    {
        var song = Song();
        var copy = ArrangementEditing.Duplicate(song, "sunday", id: "copy")!;
        Assert.Equal("Sunday copy", copy.Name);
        Assert.Equal(["v1", "c", "v2", "c"], copy.SectionIds);
        Assert.Equal(["sunday", "copy"], song.Arrangements!.Select(a => a.Id));
        // The copy is its own list: changing it leaves the original alone.
        ArrangementEditing.RemoveBlock(song, "copy", 0);
        Assert.Equal(4, song.Arrangements![0].SectionIds.Count);
        Assert.Equal("Sunday copy 2", ArrangementEditing.Duplicate(song, "sunday")!.Name);
        Assert.Equal(["sunday", song.Arrangements[1].Id, "copy"], song.Arrangements.Select(a => a.Id));
        Assert.Null(ArrangementEditing.Duplicate(song, "missing"));
    }

    [Fact]
    public void BlocksAddRemoveAndMoveLikeTheStripsDrop()
    {
        var song = Song();
        Assert.True(ArrangementEditing.AddBlock(song, "sunday", "v1"));
        Assert.Equal(["v1", "c", "v2", "c", "v1"], song.Arrangements![0].SectionIds);
        Assert.False(ArrangementEditing.AddBlock(song, "sunday", "nope"));

        Assert.True(ArrangementEditing.RemoveBlock(song, "sunday", 4));
        Assert.False(ArrangementEditing.RemoveBlock(song, "sunday", 9));

        // Dropping block 0 before block 3: it lands third (the strip's from < to rule).
        Assert.True(ArrangementEditing.MoveBlock(song, "sunday", 0, 3));
        Assert.Equal(["c", "v2", "v1", "c"], song.Arrangements[0].SectionIds);
        // Onto itself, or just after itself: nothing moves.
        Assert.False(ArrangementEditing.MoveBlock(song, "sunday", 1, 1));
        Assert.False(ArrangementEditing.MoveBlock(song, "sunday", 1, 2));
        // To the end (the count), and back to the front.
        Assert.True(ArrangementEditing.MoveBlock(song, "sunday", 0, 4));
        Assert.Equal(["v2", "v1", "c", "c"], song.Arrangements[0].SectionIds);
        Assert.True(ArrangementEditing.MoveBlock(song, "sunday", 3, 0));
        Assert.Equal(["c", "v2", "v1", "c"], song.Arrangements[0].SectionIds);
    }

    [Fact]
    public void TheOrderTextFoldsRepeatsAndReadsBack()
    {
        var song = Song();
        song.Arrangements![0].SectionIds = ["v1", "c", "c", "v2", "c", "c", "c"];
        var text = ArrangementEditing.OrderText(song, "sunday");
        Assert.Equal("Verse 1, Chorus x2, Verse 2, Chorus x3", text);
        Assert.Equal(song.Arrangements[0].SectionIds, SongEditing.ParseOrder(text, song.Sections!));
        // A long run is written in pieces ParseOrder can read (it reads at most x20).
        song.Arrangements[0].SectionIds = [.. Enumerable.Repeat("c", 25)];
        Assert.Equal("Chorus x20, Chorus x5", ArrangementEditing.OrderText(song, "sunday"));
    }

    [Fact]
    public void TypingAnOrderReplacesTheBlocksButNeverEmptiesThem()
    {
        var song = Song();
        Assert.True(ArrangementEditing.SetOrder(song, "sunday", "V1, C x2, V2"));
        Assert.Equal(["v1", "c", "c", "v2"], song.Arrangements![0].SectionIds);
        Assert.False(ArrangementEditing.SetOrder(song, "sunday", "Bridge, Tag")); // names nothing in this song
        Assert.Equal(4, song.Arrangements[0].SectionIds.Count);
        Assert.False(ArrangementEditing.SetOrder(song, "sunday", "Verse 1, Chorus, Chorus, Verse 2")); // the same blocks
    }

    [Fact]
    public void BlockNamesCountsAndTheLivePill()
    {
        var song = Song();
        Assert.Equal("Chorus", ArrangementEditing.SectionName(song, "c"));
        Assert.Equal("Deleted Section", ArrangementEditing.SectionName(song, "gone"));
        Assert.Equal(5, ArrangementEditing.ArrangedSlideCount(song, "sunday"));
        song.Sections!.Add(new() { Id = "b", Name = "Bridge" });
        Assert.True(ArrangementEditing.SectionIsEmpty(song, "b"));
        Assert.False(ArrangementEditing.SectionIsEmpty(song, "c"));

        var starts = SlideSceneBuilder.ArrangementBlockStarts(song, "sunday"); // [0, 2, 3, 4]
        Assert.Equal(0, ArrangementEditing.BlockIndex(starts, 1));
        Assert.Equal(1, ArrangementEditing.BlockIndex(starts, 2));
        Assert.Equal(3, ArrangementEditing.BlockIndex(starts, 4));
        Assert.Null(ArrangementEditing.BlockIndex([2], 0));
    }

    [Fact]
    public void BasePillsFollowTheSlides()
    {
        var song = Song();
        Assert.Equal(["Verse 1", "Chorus", "Verse 2"], ArrangementEditing.BaseRunSections(song).Select(s => s.Name));
        // A slide whose section has gone is skipped, as the pills skip it.
        song.Slides.Insert(2, S("x", "ghost"));
        Assert.Equal(["Verse 1", "Chorus", "Verse 2"], ArrangementEditing.BaseRunSections(song).Select(s => s.Name));
    }

    [Fact]
    public void TheDefaultIsOneThatExistsOrNone()
    {
        var song = Song();
        Assert.True(ArrangementEditing.SetDefault(song, "sunday"));
        Assert.Equal("sunday", song.DefaultArrangementId);
        Assert.False(ArrangementEditing.SetDefault(song, "sunday"));
        Assert.True(ArrangementEditing.SetDefault(song, "missing"));
        Assert.Null(song.DefaultArrangementId);
    }
}
