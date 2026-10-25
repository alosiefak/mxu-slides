using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;
using MxU.Slides.Sync.Automerge;
using MxU.Slides.Sync.Engine;
using MxU.Slides.Sync.Format;
using MxU.Slides.Sync.Tests.Fakes;

namespace MxU.Slides.Sync.Tests;

/// <summary>The two-way bridge: the first sync, edits both ways, merging, and the backups.</summary>
public sealed class SyncEngineTests
{
    private static readonly MacKind Songs = Samples.Presentations;
    private static readonly MacKind Services = Samples.Services;

    [Fact]
    public void TheFirstSyncSendsWindowsDocumentsTheMacCanRead()
    {
        using var fixture = new SyncFixture();
        fixture.Store.Save(Samples.Song());
        fixture.Store.Save(Samples.Sunday());
        fixture.Sync();

        Assert.Equal(SyncState.UpToDate, fixture.Engine.Status.State);
        Assert.True(MxuJson.StructurallyEqual(Samples.Song(), fixture.MacRead<Presentation>(Songs, "song")));
        Assert.True(MxuJson.StructurallyEqual(Samples.Sunday(), fixture.MacRead<Service>(Services, "sunday")));
        Assert.True(File.Exists(Path.Combine(fixture.Shared, SharedFolder.MarkerName)));
        Assert.Contains(fixture.Engine.Log.Entries, e => e.Direction == SyncDirection.Out && e.Text.Contains("Amazing Grace"));
    }

    [Fact]
    public void TheFirstSyncBringsInTheMacsDocuments()
    {
        using var fixture = new SyncFixture();
        using (var mac = MacSimulator.Create(Samples.RichDeck(), Songs)) fixture.MacSave(mac, Songs, "rich");
        var heard = new List<string>();
        fixture.Store.Changed += (_, id) => heard.Add(id);
        fixture.Sync();
        Assert.True(MxuJson.StructurallyEqual(Samples.RichDeck(), fixture.Store.Get<Presentation>("rich")));
        Assert.Contains("rich", heard);
    }

    [Fact]
    public void TheFirstSyncBacksUpBothSidesFirst()
    {
        using var fixture = new SyncFixture();
        fixture.Store.Save(Samples.Song());
        using (var mac = MacSimulator.Create(Samples.RichDeck(), Songs)) fixture.MacSave(mac, Songs, "rich");
        var macBytes = fixture.MacBytes(Songs, "rich");
        fixture.Sync();
        var backup = Directory.GetDirectories(Path.Combine(fixture.Store.Root, "Library sync", "backups")).Single();
        Assert.True(File.Exists(Path.Combine(backup, "Windows", "Presentations", "song.json")));
        Assert.Equal(macBytes, File.ReadAllBytes(Path.Combine(backup, "Mac", "presentations", "rich.automerge")));
        Assert.False(File.Exists(Path.Combine(backup, "Mac", "presentations", "song.automerge")), "the backup is from before the sync");
    }

    [Fact]
    public void AnEmptyFolderBecomesAMacLibrary()
    {
        using var fixture = new SyncFixture(makeMacLibrary: false);
        fixture.Store.Save(Samples.Song());
        fixture.Sync();
        foreach (var kind in MacKind.All) Assert.True(Directory.Exists(Path.Combine(fixture.Shared, kind.DirectoryName)), kind.DirectoryName);
        Assert.True(fixture.MacHas(Songs, "song"));
    }

    [Fact]
    public void AFolderThatIsNeitherEmptyNorAMacLibraryIsLeftAlone()
    {
        using var fixture = new SyncFixture(makeMacLibrary: false);
        File.WriteAllText(Path.Combine(fixture.Shared, "Holiday photos.txt"), "not a library");
        fixture.Store.Save(Samples.Song());
        fixture.Sync();
        Assert.Equal(SyncState.NeedsAttention, fixture.Engine.Status.State);
        Assert.Single(Directory.EnumerateFileSystemEntries(fixture.Shared));
    }

    [Fact]
    public void TheSameDocumentOnBothSidesIsAdoptedAsItIs()
    {
        using var fixture = new SyncFixture();
        fixture.Store.Save(Samples.Song());
        using (var mac = MacSimulator.Create(Samples.Song(), Songs)) fixture.MacSave(mac, Songs, "song");
        var before = fixture.MacBytes(Songs, "song");
        fixture.Sync();
        Assert.Equal(before, fixture.MacBytes(Songs, "song"));
        Assert.Single(fixture.Store.All<Presentation>());
    }

    [Fact]
    public void AtTheFirstSyncTheCopyChangedLaterWins()
    {
        using var fixture = new SyncFixture();
        // The Mac's copy was last changed long ago; this PC's file is new.
        using (var mac = MacSimulator.Create(Samples.Song(name: "Mac title"), Songs, DateTimeOffset.FromUnixTimeSeconds(1_000_000_000)))
            fixture.MacSave(mac, Songs, "song");
        fixture.Store.Save(Samples.Song(name: "Windows title"));
        fixture.Sync();
        Assert.Equal("Windows title", fixture.Store.Get<Presentation>("song")!.Name);
        Assert.Equal("Windows title", fixture.MacRead<Presentation>(Songs, "song").Name);

        using var later = new SyncFixture();
        later.Store.Save(Samples.Song(name: "Windows title"));
        File.SetLastWriteTimeUtc(later.Store.DocumentPath(typeof(Presentation), "song"), new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        using (var mac = MacSimulator.Create(Samples.Song(name: "Mac title"), Songs)) later.MacSave(mac, Songs, "song");
        later.Sync();
        Assert.Equal("Mac title", later.Store.Get<Presentation>("song")!.Name);
    }

    [Fact]
    public void AWindowsEditGoesOut()
    {
        using var fixture = new SyncFixture();
        fixture.Store.Save(Samples.Song());
        fixture.Sync();
        fixture.WindowsEdit<Presentation>("song", s => s.Slides[2].Objects[0].Text = "Edited on Windows");
        fixture.Sync();
        Assert.Equal("Edited on Windows", fixture.MacRead<Presentation>(Songs, "song").Slides[2].Objects[0].Text);
        Assert.Contains(fixture.Engine.Log.Entries, e => e.Text.StartsWith("Sent changes to 'Amazing Grace'"));
    }

    [Fact]
    public void AMacEditComesIn()
    {
        using var fixture = new SyncFixture();
        fixture.Store.Save(Samples.Song());
        fixture.Sync();
        fixture.MacEdit<Presentation>(Songs, "song", s => s.MusicKey = "G");
        var heard = 0;
        fixture.Store.Changed += (_, id) => heard += id == "song" ? 1 : 0;
        fixture.Sync();
        Assert.Equal("G", fixture.Store.Get<Presentation>("song")!.MusicKey);
        Assert.Equal(1, heard);
    }

    [Fact]
    public void EditsMadeOnBothSidesAtOnceMerge()
    {
        using var fixture = new SyncFixture();
        fixture.Store.Save(Samples.Song());
        fixture.Sync();
        fixture.MacEdit<Presentation>(Songs, "song", s => s.Slides[0].Objects[0].Text = "Mac words");
        fixture.WindowsEdit<Presentation>("song", s => s.Slides[4].Objects[0].Text = "Windows words");
        fixture.Sync();
        var windows = fixture.Store.Get<Presentation>("song")!;
        var mac = fixture.MacRead<Presentation>(Songs, "song");
        Assert.True(MxuJson.StructurallyEqual(windows, mac));
        Assert.Equal("Mac words", mac.Slides[0].Objects[0].Text);
        Assert.Equal("Windows words", mac.Slides[4].Objects[0].Text);
    }

    [Fact]
    public void AnEditSavedFromAnOlderCopyKeepsTheMacsChange()
    {
        using var fixture = new SyncFixture();
        fixture.Store.Save(Samples.Song());
        fixture.Sync();
        // The editor is holding the song as it was...
        var held = MxuJson.Clone(fixture.Store.Get<Presentation>("song")!);
        // ...when a Mac change comes in...
        fixture.MacEdit<Presentation>(Songs, "song", s => s.Slides[0].Notes = "Mac notes");
        fixture.Sync();
        Assert.Equal("Mac notes", fixture.Store.Get<Presentation>("song")!.Slides[0].Notes);
        // ...and then saves its own edit over it.
        held.Slides[3].Notes = "Windows notes";
        fixture.Store.Save(held);
        fixture.Sync();
        var merged = fixture.Store.Get<Presentation>("song")!;
        Assert.Equal("Mac notes", merged.Slides[0].Notes);
        Assert.Equal("Windows notes", merged.Slides[3].Notes);
        Assert.True(MxuJson.StructurallyEqual(merged, fixture.MacRead<Presentation>(Songs, "song")));
    }

    [Fact]
    public void AMacWriteInTheMiddleOfSendingIsTakenInAndTheWindowsEditIsNotDoubled()
    {
        using var fixture = new SyncFixture();
        fixture.Store.Save(Samples.Song());
        fixture.Sync();
        fixture.WindowsEdit<Presentation>("song", s => s.Slides.Insert(1, new Slide { Id = "new", Name = "New", Objects = [] }));
        var once = false;
        fixture.Engine.BeforeSharedWrite = _ =>
        {
            if (once) return;
            once = true;
            fixture.MacEdit<Presentation>(Songs, "song", s => s.Slides[^1].Notes = "Mac, meanwhile");
        };
        fixture.Sync();
        var mac = fixture.MacRead<Presentation>(Songs, "song");
        Assert.Equal(["slide-1", "new", "slide-2", "slide-3", "slide-4", "slide-5"], mac.Slides.Select(s => s.Id));
        Assert.Equal("Mac, meanwhile", mac.Slides[^1].Notes);
        Assert.True(MxuJson.StructurallyEqual(mac, fixture.Store.Get<Presentation>("song")));
    }

    [Fact]
    public void AWindowsEditTakenInBeforeTheAppClosedIsNotDoubledAfterwards()
    {
        using var fixture = new SyncFixture();
        fixture.Store.Save(Samples.Song());
        fixture.Sync();
        fixture.WindowsEdit<Presentation>("song", s => s.Slides.Insert(1, new Slide { Id = "new", Name = "New", Objects = [] }));
        // The edit is in this PC's copy, but the folder can't be written; then the app closes.
        fixture.Engine.BeforeSharedWrite = _ => throw new IOException("the network share dropped");
        fixture.Sync();
        Assert.DoesNotContain(fixture.MacRead<Presentation>(Songs, "song").Slides, s => s.Id == "new");
        fixture.Engine.Dispose();
        // Next time, the user has edited the song again.
        fixture.WindowsEdit<Presentation>("song", s => s.Slides[0].Notes = "Later");
        var again = fixture.NewEngine();
        again.SyncNowAndWait();
        var mac = fixture.MacRead<Presentation>(Songs, "song");
        Assert.Equal(["slide-1", "new", "slide-2", "slide-3", "slide-4", "slide-5"], mac.Slides.Select(s => s.Id));
        Assert.Equal("Later", mac.Slides[0].Notes);
    }

    [Fact]
    public void ASaveSyncHasNotSeenIsNeverOverwritten()
    {
        // The library's own guard: sync saves only over the file it read.
        using var fixture = new SyncFixture();
        fixture.Store.Save(Samples.Song());
        var read = File.ReadAllBytes(fixture.Store.DocumentPath(typeof(Presentation), "song"));
        fixture.WindowsEdit<Presentation>("song", s => s.Name = "Saved a moment ago");
        Assert.False(fixture.Store.SaveIfUnchanged(Samples.Song(name: "From the Mac"), read));
        Assert.Equal("Saved a moment ago", fixture.Store.Get<Presentation>("song")!.Name);
    }

    [Fact]
    public void SyncsOwnSaveDoesNotEchoBack()
    {
        using var fixture = new SyncFixture();
        fixture.Store.Save(Samples.Song());
        fixture.Sync();
        fixture.MacEdit<Presentation>(Songs, "song", s => s.MusicKey = "D");
        fixture.Sync();
        var bytes = fixture.MacBytes(Songs, "song");
        fixture.Sync();
        Assert.Equal(bytes, fixture.MacBytes(Songs, "song"));
    }

    [Fact]
    public void ARestartedEngineCarriesOnWhereItLeftOff()
    {
        using var fixture = new SyncFixture();
        fixture.Store.Save(Samples.Song());
        fixture.Sync();
        fixture.Engine.Dispose();
        // Edited on both sides while sync wasn't running.
        fixture.WindowsEdit<Presentation>("song", s => s.Slides[1].Notes = "While away");
        fixture.MacEdit<Presentation>(Songs, "song", s => s.Slides[2].Notes = "Mac while away");
        var again = fixture.NewEngine();
        again.SyncNowAndWait();
        var merged = fixture.Store.Get<Presentation>("song")!;
        Assert.Equal("While away", merged.Slides[1].Notes);
        Assert.Equal("Mac while away", merged.Slides[2].Notes);
        Assert.Single(Directory.GetDirectories(Path.Combine(fixture.Store.Root, "Library sync", "backups")));
    }

    [Fact]
    public void ChoosingAnotherFolderStartsAfresh()
    {
        using var fixture = new SyncFixture();
        fixture.Store.Save(Samples.Song());
        fixture.Sync();
        fixture.Engine.Dispose();
        var other = Path.Combine(fixture.Root, "Other");
        Directory.CreateDirectory(other);
        var again = fixture.NewEngine(other);
        again.SyncNowAndWait();
        Assert.True(File.Exists(Path.Combine(other, Songs.DirectoryName, "song.automerge")));
        Assert.Equal(2, Directory.GetDirectories(Path.Combine(fixture.Store.Root, "Library sync", "backups")).Length);
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(fixture.Store.Root, "Library sync"), "state.json.*.old"));
    }

    [Fact]
    public void StationDocumentsStayOnEachComputer()
    {
        using var fixture = new SyncFixture();
        fixture.Store.Save(new OutputPreset { Id = "preset", Name = "Sunday screens" });
        fixture.Sync();
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Shared, "output-presets")));
    }

    [Fact]
    public void PausedSyncDoesNothingUntilResumed()
    {
        using var fixture = new SyncFixture();
        fixture.Engine.Pause();
        fixture.Store.Save(Samples.Song());
        fixture.Engine.SyncNowAndWait();
        Assert.False(fixture.MacHas(Songs, "song"));
        Assert.Equal(SyncState.Paused, fixture.Engine.Status.State);
        fixture.Engine.Resume();
        fixture.Engine.SyncNowAndWait();
        Assert.True(fixture.MacHas(Songs, "song"));
    }
}
