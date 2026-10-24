using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Library;

/// <summary>Port of MediaImportDedupeTests.swift.</summary>
public sealed class MediaImportDedupeTests
{
    private static MediaItem Media(string id, string name, string hash) => ScratchFolderTest.Media(id, hash, name, MediaKind.Image);

    private static AudioItem Audio(string id, string name, string hash) => ScratchFolderTest.Audio(id, hash, name);

    [Fact]
    public void TheSameFileReusesTheLibrarysItem()
    {
        var dedupe = new MediaImportDedupe([Media("m1", "Announce", "h1")], []);
        Assert.Equal(new MediaImportDedupe.Match.SameFile("m1"), dedupe.Find("h1", "Renamed on disk", MediaImportDedupe.Kind.Media));
    }

    [Fact]
    public void ADifferentFileUnderAnExistingNameAsks()
    {
        var dedupe = new MediaImportDedupe([Media("m1", "Announce", "h1")], []);
        Assert.Equal(new MediaImportDedupe.Match.SameName("m1", "Announce"), dedupe.Find("h2", " announce ", MediaImportDedupe.Kind.Media));
        Assert.Equal(new MediaImportDedupe.Match.New(), dedupe.Find("h2", "Other", MediaImportDedupe.Kind.Media));
    }

    [Fact]
    public void MediaAndAudioNeverMatchEachOther()
    {
        var dedupe = new MediaImportDedupe([Media("m1", "Song", "h1")], [Audio("a1", "Song", "h2")]);
        Assert.Equal(new MediaImportDedupe.Match.SameName("a1", "Song"), dedupe.Find("h1", "Song", MediaImportDedupe.Kind.Audio));
        Assert.Equal(new MediaImportDedupe.Match.SameFile("a1"), dedupe.Find("h2", "x", MediaImportDedupe.Kind.Audio));
        Assert.Equal(new MediaImportDedupe.Match.New(), dedupe.Find("h2", "x", MediaImportDedupe.Kind.Media));
    }

    [Fact]
    public void AFileMadeThisRunIsReusedByItsTwin()
    {
        var dedupe = new MediaImportDedupe([], []);
        dedupe.Note("new1", "h9", "Flyer", MediaImportDedupe.Kind.Media);
        Assert.Equal(new MediaImportDedupe.Match.SameFile("new1"), dedupe.Find("h9", "Flyer copy", MediaImportDedupe.Kind.Media));
    }

    [Fact]
    public void KeepBothNumbersTheNewItemLikeFinder()
    {
        var dedupe = new MediaImportDedupe([Media("m1", "Background", "h1")], []);
        Assert.Equal("Background 2", dedupe.AvailableName("Background", MediaImportDedupe.Kind.Media));
        dedupe.Note("m2", "h2", "Background 2", MediaImportDedupe.Kind.Media);
        Assert.Equal("Background 3", dedupe.AvailableName("Background", MediaImportDedupe.Kind.Media));
        Assert.Equal("Background 3", dedupe.AvailableName("Background 2", MediaImportDedupe.Kind.Media)); // a numbered name counts on
        Assert.Equal("Background 2", dedupe.AvailableName("Background", MediaImportDedupe.Kind.Audio)); // each kind numbers on its own
    }
}
