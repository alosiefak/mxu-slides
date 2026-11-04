using System.Text.Json.Nodes;
using MxU.Slides.Core.Model;
using MxU.Slides.Sync.Automerge;
using MxU.Slides.Sync.Engine;
using MxU.Slides.Sync.Format;
using MxU.Slides.Sync.Tests.Fakes;

namespace MxU.Slides.Sync.Tests;

/// <summary>Deletions, a folder that goes away, documents this version can't read, media, and never writing what the Mac can't read.</summary>
public sealed class SyncSafetyTests
{
    private static readonly MacKind Songs = Samples.Presentations;

    private static void MacMakes(SyncFixture fixture, Presentation song)
    {
        using var mac = MacSimulator.Create(song, Songs);
        fixture.MacSave(mac, Songs, song.Id);
    }

    private static string MacFile(SyncFixture fixture, string id) => Path.Combine(fixture.Shared, Songs.DirectoryName, id + ".automerge");

    // MARK: Deletions

    [Fact]
    public void ADeletionOnWindowsReachesTheFolderAndAnOldCopyDoesNotBringItBack()
    {
        using var fixture = new SyncFixture();
        MacMakes(fixture, Samples.Song());
        fixture.Sync();
        var old = fixture.MacBytes(Songs, "song");
        fixture.Store.Delete<Presentation>("song");
        fixture.Sync();
        Assert.False(fixture.MacHas(Songs, "song"));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(fixture.Store.Root, "Library sync", "trash"), "*song.automerge", SearchOption.AllDirectories));

        // A backup made again on the Mac still has the old copy.
        File.WriteAllBytes(MacFile(fixture, "song"), old);
        fixture.Sync();
        Assert.Null(fixture.Store.Get<Presentation>("song"));
    }

    [Fact]
    public void ADocumentDeletedHereButChangedOnTheMacComesBack()
    {
        using var fixture = new SyncFixture();
        MacMakes(fixture, Samples.Song());
        fixture.Sync();
        var old = fixture.MacBytes(Songs, "song");
        fixture.Store.Delete<Presentation>("song");
        fixture.Sync();
        // The Mac, which still had it, changed it since.
        using (var mac = AmDocument.Load(old))
        {
            var song = MacSimulator.Read<Presentation>(mac, Songs);
            song.MusicKey = "E";
            MacSimulator.Update(mac, song, Songs);
            fixture.MacSave(mac, Songs, "song");
        }
        fixture.Sync();
        Assert.Equal("E", fixture.Store.Get<Presentation>("song")?.MusicKey);
    }

    [Fact]
    public void ADeletionOnTheMacReachesWindowsOnceItHasLasted()
    {
        using var fixture = new SyncFixture();
        MacMakes(fixture, Samples.Song());
        fixture.Sync();
        File.Delete(MacFile(fixture, "song"));
        fixture.Sync();
        Assert.NotNull(fixture.Store.Get<Presentation>("song")); // not yet: a cloud folder may be swapping the file
        fixture.Clock += TimeSpan.FromSeconds(10);
        fixture.Sync();
        Assert.Null(fixture.Store.Get<Presentation>("song"));
        Assert.NotEmpty(Directory.GetFiles(fixture.Store.TrashDirectory, "*song.json", SearchOption.AllDirectories));
    }

    [Fact]
    public void AFileSwappedOutAndBackIsNotADeletion()
    {
        using var fixture = new SyncFixture();
        MacMakes(fixture, Samples.Song());
        fixture.Sync();
        var bytes = fixture.MacBytes(Songs, "song");
        File.Delete(MacFile(fixture, "song"));
        fixture.Sync();
        File.WriteAllBytes(MacFile(fixture, "song"), bytes);
        fixture.Clock += TimeSpan.FromSeconds(10);
        fixture.Sync();
        Assert.NotNull(fixture.Store.Get<Presentation>("song"));
    }

    [Fact]
    public void AnEditHereWinsOverADeletionOnTheMac()
    {
        using var fixture = new SyncFixture();
        MacMakes(fixture, Samples.Song());
        fixture.Sync();
        File.Delete(MacFile(fixture, "song"));
        fixture.WindowsEdit<Presentation>("song", s => s.MusicKey = "A");
        fixture.Sync(twice: true);
        Assert.Equal("A", fixture.Store.Get<Presentation>("song")!.MusicKey);
        Assert.Equal("A", fixture.MacRead<Presentation>(Songs, "song").MusicKey);
    }

    [Fact]
    public void ADocumentOnlyWindowsWroteIsPutBackNotDeleted()
    {
        // A backup made again on the Mac leaves out what the Mac hadn't imported yet.
        using var fixture = new SyncFixture();
        fixture.Store.Save(Samples.Song());
        fixture.Sync();
        File.Delete(MacFile(fixture, "song"));
        fixture.Sync(twice: true);
        Assert.NotNull(fixture.Store.Get<Presentation>("song"));
        Assert.True(fixture.MacHas(Songs, "song"));
    }

    [Fact]
    public void ManyDocumentsGoneAtOnceAreHeldToAsk()
    {
        using var fixture = new SyncFixture();
        for (var i = 0; i < 12; i++) MacMakes(fixture, Samples.Song($"song-{i}", $"Song {i}"));
        fixture.Sync();
        foreach (var file in Directory.GetFiles(Path.Combine(fixture.Shared, Songs.DirectoryName))) File.Delete(file);
        fixture.Sync(twice: true);
        Assert.Equal(SyncState.NeedsAttention, fixture.Engine.Status.State);
        Assert.Equal(AttentionKind.HeldDeletions, fixture.Engine.Attention!.Kind);
        Assert.Equal(12, fixture.Engine.Attention.Items.Count);
        Assert.Equal(12, fixture.Store.All<Presentation>().Count);

        fixture.Engine.KeepHeldDeletions();
        Assert.True(fixture.Engine.WaitIdle(TimeSpan.FromSeconds(30)));
        Assert.Null(fixture.Engine.Attention);
        Assert.Equal(12, Directory.GetFiles(Path.Combine(fixture.Shared, Songs.DirectoryName)).Length);
        Assert.Equal(12, fixture.Store.All<Presentation>().Count);
    }

    [Fact]
    public void HeldDeletionsAreMadeWhenAsked()
    {
        using var fixture = new SyncFixture();
        for (var i = 0; i < 12; i++) MacMakes(fixture, Samples.Song($"song-{i}", $"Song {i}"));
        fixture.Sync();
        foreach (var file in Directory.GetFiles(Path.Combine(fixture.Shared, Songs.DirectoryName))) File.Delete(file);
        fixture.Sync(twice: true);
        fixture.Engine.ApplyHeldDeletions();
        Assert.True(fixture.Engine.WaitIdle(TimeSpan.FromSeconds(30)));
        Assert.Empty(fixture.Store.All<Presentation>());
    }

    [Fact]
    public void AKindFolderThatHasGoneDeletesNothing()
    {
        using var fixture = new SyncFixture();
        MacMakes(fixture, Samples.Song());
        fixture.Sync();
        Directory.Delete(Path.Combine(fixture.Shared, Songs.DirectoryName), recursive: true);
        fixture.Sync(twice: true);
        Assert.NotNull(fixture.Store.Get<Presentation>("song"));
    }

    // MARK: A folder that goes away

    [Fact]
    public void AFolderThatGoesOfflinePausesAndCarriesOnLater()
    {
        using var fixture = new SyncFixture();
        MacMakes(fixture, Samples.Song());
        fixture.Sync();
        var away = fixture.Shared + " (unplugged)";
        Directory.Move(fixture.Shared, away);

        fixture.WindowsEdit<Presentation>("song", s => s.MusicKey = "Bb");
        fixture.Sync(twice: true);
        Assert.Equal(SyncState.WaitingForFolder, fixture.Engine.Status.State);
        Assert.NotNull(fixture.Store.Get<Presentation>("song"));

        Directory.Move(away, fixture.Shared);
        fixture.Sync(twice: true);
        Assert.Equal(SyncState.UpToDate, fixture.Engine.Status.State);
        Assert.Equal("Bb", fixture.MacRead<Presentation>(Songs, "song").MusicKey);
        Assert.NotNull(fixture.Store.Get<Presentation>("song"));
    }

    // MARK: What this version can't read, and never writing what the Mac can't

    [Fact]
    public void ADocumentWithAnUnknownEnumValueStopsSyncAndIsLeftUntouched()
    {
        using var fixture = new SyncFixture();
        MacMakes(fixture, Samples.Song());
        // A newer Mac's song with a kind this version doesn't know.
        using (var newer = MacSimulator.Create(Samples.Song("medley", "Medley"), Songs))
        {
            newer.Apply(new AmEdit().Put(AmPath.Root, "presentationKind", AmWrite.String("medley")), null, DateTimeOffset.UtcNow);
            fixture.MacSave(newer, Songs, "medley");
        }
        var before = fixture.MacBytes(Songs, "medley");
        fixture.Store.Save(Samples.Sunday());
        fixture.Sync();

        Assert.Equal(SyncState.NeedsAttention, fixture.Engine.Status.State);
        var asked = fixture.Engine.Attention!;
        Assert.Equal(AttentionKind.Unreadable, asked.Kind);
        Assert.Contains("medley", Assert.Single(asked.Items).Reason);
        Assert.Null(fixture.Store.Get<Presentation>("medley"));
        Assert.Equal(before, fixture.MacBytes(Songs, "medley"));

        fixture.Engine.LeaveOutUnreadable();
        Assert.True(fixture.Engine.WaitIdle(TimeSpan.FromSeconds(30)));
        Assert.Equal(SyncState.UpToDate, fixture.Engine.Status.State);
        Assert.NotNull(fixture.Store.Get<Presentation>("song"));
        Assert.True(fixture.MacHas(Samples.Services, "sunday"));
        Assert.Equal(before, fixture.MacBytes(Songs, "medley"));
    }

    [Fact]
    public void ANewerBackupFormatStopsBeforeAnythingIsWritten()
    {
        using var fixture = new SyncFixture();
        File.WriteAllText(Path.Combine(fixture.Shared, "manifest.json"), """{"formatVersion":2,"createdAt":"2027-01-01T00:00:00Z","documentCounts":{},"blobCount":0}""");
        fixture.Store.Save(Samples.Song());
        fixture.Sync();
        Assert.Equal(AttentionKind.NewerFormat, fixture.Engine.Attention!.Kind);
        Assert.False(fixture.MacHas(Songs, "song"));
        Assert.False(File.Exists(Path.Combine(fixture.Shared, SharedFolder.MarkerName)));
    }

    [Fact]
    public void ANewerWindowsSchemaInTheFolderStopsSync()
    {
        using var fixture = new SyncFixture();
        new SharedFolder(fixture.Shared).WriteMarker(MacSchema.Shared.Version + 1, "a newer PC");
        fixture.Store.Save(Samples.Song());
        fixture.Sync();
        Assert.Equal(AttentionKind.NewerFormat, fixture.Engine.Attention!.Kind);
        Assert.False(fixture.MacHas(Songs, "song"));
    }

    [Fact]
    public void ADocumentTheMacCouldNotReadIsNeverSent()
    {
        using var fixture = new SyncFixture();
        var song = Samples.Song();
        song.Slides[0].Objects[0].X = double.NaN; // the Mac's encoder refuses NaN, and so would its decoder
        fixture.Store.Save(song);
        fixture.Store.Save(Samples.Sunday());
        fixture.Sync();
        Assert.False(fixture.MacHas(Songs, "song"));
        Assert.True(fixture.MacHas(Samples.Services, "sunday"));
        Assert.Contains(fixture.Engine.Log.Entries, e => e.Direction == SyncDirection.Problem && e.Text.Contains("Didn't send"));
    }

    [Fact]
    public void AnUnknownEnumValueIsNeverWritten()
    {
        // However a value got into a Windows document, the writer refuses an enum case the Mac doesn't know.
        var json = MacDocuments.ToJson(Samples.Song());
        json["presentationKind"] = "medley";
        Assert.Throws<SchemaViolationException>(() => MacDocuments.Shared.Create(json, Songs, DateTimeOffset.UtcNow));
        using var document = MacSimulator.Create(Samples.Song(), Songs);
        var heads = document.Heads;
        var slides = (JsonArray)json["slides"]!;
        json["presentationKind"] = "song";
        ((JsonObject)slides[0]!)["transition"] = new JsonObject { ["transitionKind"] = "push" };
        Assert.Throws<SchemaViolationException>(() => MacDocuments.Shared.Edit(document, json, Songs, DateTimeOffset.UtcNow));
        Assert.Equal(heads, document.Heads);
    }

    [Fact]
    public void AConflictCopyIsMergedBackIntoItsDocument()
    {
        using var fixture = new SyncFixture();
        MacMakes(fixture, Samples.Song());
        fixture.Sync();
        // Two computers wrote the file at once; the cloud client kept both.
        using (var copy = fixture.MacOpen(Songs, "song"))
        {
            var song = MacSimulator.Read<Presentation>(copy, Songs);
            song.Slides[1].Notes = "Only in the copy";
            MacSimulator.Update(copy, song, Songs);
            File.WriteAllBytes(MacFile(fixture, "song (MacBook's conflicted copy 2026-10-25)"), copy.Save());
        }
        fixture.Sync();
        fixture.Sync();
        Assert.Equal("Only in the copy", fixture.Store.Get<Presentation>("song")!.Slides[1].Notes);
        Assert.Equal("Only in the copy", fixture.MacRead<Presentation>(Songs, "song").Slides[1].Notes);
        Assert.False(File.Exists(MacFile(fixture, "song (MacBook's conflicted copy 2026-10-25)")));
        Assert.Single(fixture.Store.All<Presentation>());
    }

    // MARK: Media

    [Fact]
    public void MediaFilesTravelBothWaysAndAHalfCopiedOneWaits()
    {
        using var fixture = new SyncFixture();
        var picture = Path.Combine(fixture.Root, "picture.png");
        File.WriteAllBytes(picture, [1, 2, 3, 4]);
        var windowsHash = fixture.Store.Blobs.Store(picture);

        var macBytes = new byte[] { 9, 8, 7 };
        var macHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(macBytes));
        File.WriteAllBytes(Path.Combine(fixture.Shared, "blobs", macHash + ".jpg"), macBytes);
        var halfHash = new string('a', 64);
        File.WriteAllBytes(Path.Combine(fixture.Shared, "blobs", halfHash + ".mp4"), [0]);

        fixture.Sync();
        Assert.True(fixture.Engine.WaitIdle(TimeSpan.FromSeconds(30)));
        Assert.True(File.Exists(Path.Combine(fixture.Shared, "blobs", windowsHash + ".png")));
        Assert.NotNull(fixture.Store.Blobs.Find(macHash));
        Assert.Null(fixture.Store.Blobs.Find(halfHash));
    }
}
