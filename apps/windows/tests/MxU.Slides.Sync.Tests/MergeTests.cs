using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;
using MxU.Slides.Sync.Automerge;
using MxU.Slides.Sync.Format;
using MxU.Slides.Sync.Tests.Fakes;

namespace MxU.Slides.Sync.Tests;

/// <summary>
/// Edits made on Windows and on a Mac at the same time merge as two Macs' edits do. Each test
/// starts from one document, lets the Mac (the simulator) and Windows (the app's diff) change their
/// own copies, merges them both ways, and checks both copies agree and the Mac can read the result.
/// </summary>
public sealed class MergeTests
{
    private static readonly MacKind Kind = Samples.Presentations;

    private static Presentation Copy(Presentation song, Action<Presentation> change)
    {
        var copy = MxuJson.Clone(song);
        change(copy);
        return copy;
    }

    /// <summary>The Mac's copy and Windows' copy after each side's edit, merged both ways; returns what both read.</summary>
    private static Presentation Merge(Presentation start, Action<AmDocument> mac, Presentation windows)
    {
        using var shared = MacSimulator.Create(start, Kind);
        using var onMac = AmDocument.Load(shared.Save());
        using var onWindows = AmDocument.Load(shared.Save());
        mac(onMac);
        MacDocuments.Shared.Edit(onWindows, MacDocuments.ToJson(windows), Kind, DateTimeOffset.UtcNow);
        onMac.Merge(onWindows);
        onWindows.Merge(onMac);
        Assert.Equal(onMac.Heads, onWindows.Heads);
        var macRead = MacSimulator.Read<Presentation>(onMac, Kind);
        Assert.True(MxuJson.StructurallyEqual(macRead, MacSimulator.Read<Presentation>(onWindows, Kind)));
        return macRead;
    }

    [Fact]
    public void EditsToDifferentSlidesBothSurvive()
    {
        var song = Samples.Song();
        var merged = Merge(song,
            mac => MacSimulator.Update(mac, Copy(song, s => s.Slides[0].Objects[0].Text = "From the Mac"), Kind),
            Copy(song, s => s.Slides[3].Objects[0].Text = "From Windows"));
        Assert.Equal("From the Mac", merged.Slides[0].Objects[0].Text);
        Assert.Equal("From Windows", merged.Slides[3].Objects[0].Text);
    }

    [Fact]
    public void EditsToDifferentFieldsOfOneSlideBothSurvive()
    {
        var song = Samples.Song();
        var merged = Merge(song,
            mac => MacSimulator.Update(mac, Copy(song, s => s.Slides[2].Notes = "Mac notes"), Kind),
            Copy(song, s => s.Slides[2].Name = "Windows name"));
        Assert.Equal("Mac notes", merged.Slides[2].Notes);
        Assert.Equal("Windows name", merged.Slides[2].Name);
    }

    [Fact]
    public void SlidesAddedOnBothSidesAreAllKept()
    {
        var song = Samples.Song();
        var merged = Merge(song,
            mac => MacSimulator.UpdateSlideList(mac, Copy(song, s => s.Slides.Add(new Slide { Id = "mac", Name = "Mac", Objects = [] }))),
            Copy(song, s => s.Slides.Insert(0, new Slide { Id = "win", Name = "Windows", Objects = [] })));
        Assert.Equal(["win", "slide-1", "slide-2", "slide-3", "slide-4", "slide-5", "mac"], merged.Slides.Select(s => s.Id));
    }

    [Fact]
    public void ARenameOnTheMacAndASlideAddedOnWindowsBothSurviveAWholeValueWrite()
    {
        // The Mac's whole-value write puts fields into the slide maps already there, by position;
        // Windows' insertion is a real list insert, so the Mac's puts still land on the right slides.
        var song = Samples.Song();
        var merged = Merge(song,
            mac => MacSimulator.Update(mac, Copy(song, s => { s.Name = "Mac title"; s.Slides[1].Name = "Mac slide 2"; }), Kind),
            Copy(song, s => s.Slides.Insert(1, new Slide { Id = "win", Name = "Windows", Objects = [] })));
        Assert.Equal("Mac title", merged.Name);
        Assert.Equal(["slide-1", "win", "slide-2", "slide-3", "slide-4", "slide-5"], merged.Slides.Select(s => s.Id));
        Assert.Equal("Mac slide 2", merged.Slides[2].Name);
    }

    [Fact]
    public void TheSameFieldChangedOnBothSidesHasOneWinnerEverywhere()
    {
        var song = Samples.Song();
        var merged = Merge(song,
            mac => MacSimulator.Update(mac, Copy(song, s => s.Name = "Mac"), Kind),
            Copy(song, s => s.Name = "Windows"));
        Assert.Contains(merged.Name, new[] { "Mac", "Windows" });
    }

    [Fact]
    public void ASlideDeletedOnTheMacWhileEditedOnWindowsStaysDeleted()
    {
        // Automerge: deleting a list element wins over edits inside it (as between two Macs).
        var song = Samples.Song();
        var merged = Merge(song,
            mac => MacSimulator.UpdateSlideList(mac, Copy(song, s => s.Slides.RemoveAt(1))),
            Copy(song, s => s.Slides[1].Objects[0].Text = "Edited on Windows"));
        Assert.Equal(["slide-1", "slide-3", "slide-4", "slide-5"], merged.Slides.Select(s => s.Id));
    }

    [Fact]
    public void ASlideDeletedOnWindowsWhileTheMacEditsAnotherOne()
    {
        var song = Samples.Song();
        var merged = Merge(song,
            mac => MacSimulator.UpdateSlideList(mac, Copy(song, s => s.Slides[4].Notes = "Mac")),
            Copy(song, s => s.Slides.RemoveAt(0)));
        Assert.Equal(["slide-2", "slide-3", "slide-4", "slide-5"], merged.Slides.Select(s => s.Id));
        Assert.Equal("Mac", merged.Slides[3].Notes);
    }

    [Fact]
    public void AnArrangementReorderedOnBothSidesStaysReadable()
    {
        var song = Samples.Song();
        var merged = Merge(song,
            mac => MacSimulator.Update(mac, Copy(song, s => s.Arrangements![0].SectionIds = ["c", "v1", "c", "v2"]), Kind),
            Copy(song, s => s.Arrangements![0].SectionIds.Add("c")));
        Assert.NotEmpty(merged.Arrangements![0].SectionIds);
        Assert.All(merged.Arrangements[0].SectionIds, id => Assert.Contains(id, new[] { "v1", "c", "v2" }));
    }

    [Fact]
    public void AWindowsEditMadeFromAnOlderCopyStillMerges()
    {
        // The Windows editor still held the song as it was before a Mac change came in, and saved
        // over it. Applied from the copy it was made from (ForkAt), the Mac's change survives.
        var song = Samples.Song();
        using var replica = MacSimulator.Create(song, Kind);
        var before = replica.Heads;
        MacSimulator.Update(replica, Copy(song, s => s.Slides[0].Notes = "Mac"), Kind);
        using var fork = replica.ForkAt(before);
        MacDocuments.Shared.Edit(fork, MacDocuments.ToJson(Copy(song, s => s.Slides[4].Notes = "Windows")), Kind, DateTimeOffset.UtcNow);
        replica.Merge(fork);
        var merged = MacSimulator.Read<Presentation>(replica, Kind);
        Assert.Equal("Mac", merged.Slides[0].Notes);
        Assert.Equal("Windows", merged.Slides[4].Notes);
    }
}
