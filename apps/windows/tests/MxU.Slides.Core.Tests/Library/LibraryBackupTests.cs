using System.IO.Compression;
using System.Text;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Library;

/// <summary>
/// Backups and restores. Ports of the Mac's BackupBundleTests.swift where the behaviour is the same
/// (a round trip, sections left out, keep mine and replace, fonts and usage and settings coming
/// along, an incomplete or damaged backup reported), plus Windows' own: the safety copy, newer-wins,
/// mirroring, a Mac backup refused, the old daily copies, and a hostile zip.
/// </summary>
public sealed class LibraryBackupTests : ScratchFolderTest
{
    private static Presentation Song(string id = "s1", string name = "Amazing Grace") => new()
    {
        Id = id, Name = name, PresentationKind = PresentationKind.Song, ThemeId = "t1",
        Slides = [new Slide { Id = "sl1", Name = "Verse 1", Objects = [new SlideObject { Id = "o1", ObjectKind = SlideObjectKind.Text, Name = "Lyrics", Text = "Amazing grace" }] }],
    };

    private static Theme Theme() => new() { Id = "t1", Name = "Default", FontFamily = "Helvetica Neue", FontSize = 96, TextColorHex = "#FFFFFF", BackgroundColorHex = "#000000" };

    private static Playlist Walkin() => new() { Id = "p1", Name = "Walk-in", Entries = [], PlaybackMode = PlaybackMode.LoopPlaylist, CrossfadeSeconds = 2 };

    /// <summary>The Mac test's library: a song, its theme, a playlist and a video with its file.</summary>
    private LibraryStore Populated(string name = "library")
    {
        var library = NewLibrary(name);
        library.Save(Song());
        library.Save(Theme());
        library.Save(Walkin());
        var hash = library.Blobs.Store(WriteFile($"{Guid.NewGuid():N}.mp4", "fake media bytes"));
        var media = Media("m1", hash, "Ocean");
        media.Tags = ["loop"];
        media.Favorite = true;
        library.Save(media);
        return library;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string BackupPath() => PathFor($"{Guid.NewGuid():N}.mxubackup");

    private static BackupWriteOptions WithMedia => new(IncludeMedia: true);

    [Fact]
    public void ExportWipeImportRoundTrips()
    {
        var source = Populated();
        var original = Core.Json.MxuJson.Serialize(source.Get<Presentation>("s1"));
        source.Extras.Save("slide-transitions", "sl1", new { kind = "push" });
        var backup = BackupPath();
        var manifest = LibraryBackup.Write(source, backup, WithMedia, cancel: Ct);
        Assert.Equal(1, manifest.DocumentCounts["Presentations"]);
        Assert.Equal(1, manifest.BlobCount);
        Assert.True(manifest.IncludesMedia);

        var fresh = NewLibrary("fresh");
        var result = LibraryBackup.Restore(fresh, backup, new BackupRestoreOptions(), safetyFolder: null, cancel: Ct);
        Assert.Equal(LibraryBackup.FormatVersion, result.Manifest.FormatVersion);
        Assert.Equal(1, result.Added[BackupSection.Presentations]);
        Assert.Equal(1, result.MediaFilesCopied);
        Assert.Empty(result.Warnings);
        Assert.Equal(original, Core.Json.MxuJson.Serialize(fresh.Get<Presentation>("s1")));
        Assert.Equal(["t1"], fresh.All<Theme>().Select(t => t.Id));
        Assert.Equal(["p1"], fresh.All<Playlist>().Select(p => p.Id));
        Assert.NotNull(fresh.BlobPath(fresh.Get<MediaItem>("m1")!)); // the file came too
        Assert.Equal(["sl1"], fresh.Extras.Owners("slide-transitions")); // and the Windows extras
        Assert.Contains(typeof(Presentation), result.ChangedKinds);
    }

    [Fact]
    public void ABackupIsOneZipWithTheLibrarysFoldersAndAManifest()
    {
        var backup = BackupPath();
        LibraryBackup.Write(Populated(), backup, new BackupWriteOptions(IncludeMedia: false, Settings: new Dictionary<string, byte[]> { ["timers.json"] = "[]"u8.ToArray() }), cancel: Ct);
        using var zip = ZipFile.OpenRead(backup);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("manifest.json", names);
        Assert.Contains("Presentations/s1.json", names);
        Assert.Contains("library.json", names);
        Assert.Contains("settings/timers.json", names);
        Assert.DoesNotContain(names, n => n.StartsWith("Blobs/", StringComparison.Ordinal)); // media left out when asked
        Assert.False(File.Exists(backup + ".partial"));
        var manifest = Encoding.UTF8.GetString(ReadEntry(zip, "manifest.json"));
        Assert.Contains("\"formatVersion\": 1", manifest);
        Assert.Matches("\"createdAt\": \"\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}Z\"", manifest); // whole seconds, as the Mac reads dates
    }

    [Fact]
    public void ImportOverLiveDataKeepsTheNewerEditByDefault()
    {
        var live = Populated();
        var backup = BackupPath();
        LibraryBackup.Write(live, backup, WithMedia, cancel: Ct);
        var song = live.Get<Presentation>("s1")!;
        song.Name = "Amazing Grace (My Chains Are Gone)";
        live.Save(song);

        var result = LibraryBackup.Restore(live, backup, new BackupRestoreOptions(), safetyFolder: null, cancel: Ct);
        Assert.Equal("Amazing Grace (My Chains Are Gone)", live.Get<Presentation>("s1")!.Name); // the edit made after the backup wins
        Assert.Single(live.All<Presentation>());
        Assert.Equal(1, result.Kept);
        Assert.Equal(3, result.Same); // the rest were the same, so nothing was written
    }

    [Fact]
    public void ABackupsNewerCopyWinsOverAnOlderOneHere()
    {
        var live = Populated();
        var path = live.DocumentPath(typeof(Presentation), "s1");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-3)); // the library's copy is old
        var other = Populated("other");
        var song = other.Get<Presentation>("s1")!;
        song.Name = "Edited on the other PC";
        other.Save(song);
        var backup = BackupPath();
        LibraryBackup.Write(other, backup, WithMedia, cancel: Ct);

        LibraryBackup.Restore(live, backup, new BackupRestoreOptions(), safetyFolder: null, cancel: Ct);
        Assert.Equal("Edited on the other PC", live.Get<Presentation>("s1")!.Name);
    }

    [Fact]
    public void ANonBackupIsRejected()
    {
        var library = NewLibrary();
        var junk = PathFor("junk");
        Directory.CreateDirectory(junk);
        Assert.Throws<BackupException>(() => LibraryBackup.Restore(library, junk, new BackupRestoreOptions(), null, cancel: Ct));
        var text = WriteFile("notes.mxubackup", "not a zip");
        Assert.Contains("isn't an MxU Slides library backup", Assert.Throws<BackupException>(() => LibraryBackup.Scan(text)).Message);
        Assert.Throws<BackupException>(() => LibraryBackup.Scan(PathFor("missing.mxubackup")));
    }

    [Fact]
    public void AMacBackupIsRecognisedAndExplained()
    {
        var mac = PathFor("Library 2026-10-25.mxubackup");
        Directory.CreateDirectory(Path.Combine(mac, "presentations"));
        File.WriteAllText(Path.Combine(mac, "manifest.json"), "{\"formatVersion\":1,\"createdAt\":\"2026-10-25T09:00:00Z\",\"documentCounts\":{},\"blobCount\":0}");
        File.WriteAllText(Path.Combine(mac, "presentations", "s1.automerge"), "automerge bytes");
        var error = Assert.Throws<BackupException>(() => LibraryBackup.Scan(mac));
        Assert.Contains("made on a Mac", error.Message);
    }

    [Fact]
    public void ABackupFromANewerAppIsRefused()
    {
        var path = BackupPath();
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            Write(zip, "manifest.json", "{\"formatVersion\":2,\"createdAt\":\"2030-01-01T00:00:00Z\",\"documentCounts\":{},\"blobCount\":0}");
        Assert.Contains("newer MxU Slides (format 2)", Assert.Throws<BackupException>(() => LibraryBackup.Scan(path)).Message);
    }

    [Fact]
    public void UncheckedSectionsStayOut()
    {
        var backup = BackupPath();
        LibraryBackup.Write(Populated(), backup, WithMedia, cancel: Ct);
        var scan = LibraryBackup.Scan(backup);
        Assert.Equal(1, scan.Count(BackupSection.Presentations));
        Assert.Equal(1, scan.Count(BackupSection.Media));
        Assert.Equal(1, scan.Count(BackupSection.MediaFiles));
        Assert.Equal(0, scan.Count(BackupSection.Overlays));

        var fresh = NewLibrary("fresh");
        var result = LibraryBackup.Restore(fresh, backup, new BackupRestoreOptions(Excluded: new HashSet<BackupSection> { BackupSection.Themes, BackupSection.Media }), null, cancel: Ct);
        Assert.Equal(["s1"], fresh.All<Presentation>().Select(p => p.Id));
        Assert.Empty(fresh.All<Theme>());
        Assert.Empty(fresh.All<MediaItem>());
        Assert.Equal(0, result.MediaFilesCopied); // no media items, so no media files
    }

    [Fact]
    public void KeepMineSkipsReplaceRollsBackAndMirrorRemovesTheRest()
    {
        var live = Populated();
        var backup = BackupPath();
        LibraryBackup.Write(live, backup, WithMedia, cancel: Ct);
        var song = live.Get<Presentation>("s1")!;
        song.Name = "Edited here";
        live.Save(song);
        live.Save(Song("s2", "Made after the backup"));

        var kept = LibraryBackup.Restore(live, backup, new BackupRestoreOptions(BackupPolicy.KeepMine), null, cancel: Ct);
        Assert.Equal(1, kept.Kept);
        Assert.Equal("Edited here", live.Get<Presentation>("s1")!.Name);

        var replaced = LibraryBackup.Restore(live, backup, new BackupRestoreOptions(BackupPolicy.Replace), null, cancel: Ct);
        Assert.Equal(1, replaced.Replaced);
        Assert.Equal("Amazing Grace", live.Get<Presentation>("s1")!.Name);
        Assert.Equal(2, live.All<Presentation>().Count); // Replace leaves alone what the backup hasn't got

        var mirrored = LibraryBackup.Restore(live, backup, new BackupRestoreOptions(BackupPolicy.Mirror), null, cancel: Ct);
        Assert.Equal(1, mirrored.Removed);
        Assert.Equal(["s1"], live.All<Presentation>().Select(p => p.Id));
        Assert.Single(Directory.GetFiles(Path.Combine(live.TrashDirectory, "Presentations"))); // to the Trash, never deleted outright
    }

    [Fact]
    public void ARestoreKeepsASafetyCopyOfTheLibraryFirst()
    {
        var live = Populated();
        var backup = BackupPath();
        LibraryBackup.Write(Populated("other"), backup, WithMedia, cancel: Ct);
        var song = live.Get<Presentation>("s1")!;
        song.Name = "Only in the live library";
        live.Save(song);

        var safety = PathFor("Backups");
        var result = LibraryBackup.Restore(live, backup, new BackupRestoreOptions(BackupPolicy.Replace), safety, cancel: Ct);
        Assert.NotNull(result.SafetyCopy);
        Assert.StartsWith(BackupFiles.SafetyPrefix, Path.GetFileName(result.SafetyCopy));
        Assert.Equal("Amazing Grace", live.Get<Presentation>("s1")!.Name);

        // The safety copy puts it back as it was.
        LibraryBackup.Restore(live, result.SafetyCopy!, new BackupRestoreOptions(BackupPolicy.Replace), null, cancel: Ct);
        Assert.Equal("Only in the live library", live.Get<Presentation>("s1")!.Name);
    }

    [Fact]
    public void NoSafetyCopyMeansNoRestore()
    {
        var live = Populated();
        var backup = BackupPath();
        LibraryBackup.Write(Populated("other"), backup, WithMedia, cancel: Ct);
        var song = live.Get<Presentation>("s1")!;
        song.Name = "Mine";
        live.Save(song);
        // A file where the safety folder should be: the copy can't be written.
        var blocked = WriteFile("blocked", "a file, not a folder");
        Assert.ThrowsAny<IOException>(() => LibraryBackup.Restore(live, backup, new BackupRestoreOptions(BackupPolicy.Replace), blocked, cancel: Ct));
        Assert.Equal("Mine", live.Get<Presentation>("s1")!.Name); // nothing was replaced
    }

    [Fact]
    public void TheScanSaysWhatsNewSameAndDifferentBeforeAnythingChanges()
    {
        var live = Populated();
        var backup = BackupPath();
        LibraryBackup.Write(live, backup, WithMedia, cancel: Ct);
        var song = live.Get<Presentation>("s1")!;
        song.Name = "Changed";
        live.Save(song);
        live.Save(Song("s2", "Here only"));
        live.Delete<Theme>("t1");

        var before = File.ReadAllText(live.DocumentPath(typeof(Presentation), "s1"));
        var scan = LibraryBackup.Scan(backup, live);
        Assert.Equal(new BackupComparison(InBackup: 1, New: 0, Same: 0, Different: 1, OnlyHere: 1), scan.Compared[BackupSection.Presentations]);
        Assert.Equal(1, scan.Compared[BackupSection.Themes].New);
        Assert.Equal(1, scan.Compared[BackupSection.Playlists].Same);
        Assert.Equal(before, File.ReadAllText(live.DocumentPath(typeof(Presentation), "s1"))); // looking changes nothing
    }

    [Fact]
    public void FontsUsageAndSettingsRideAlong()
    {
        var library = Populated();
        Directory.CreateDirectory(Path.Combine(library.Root, "fonts"));
        File.WriteAllText(Path.Combine(library.Root, "fonts", "file.ttf"), "bytes");
        var usage = new LibraryUsage(library.Root);
        usage.Touch("s1", DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        usage.SaveNow();
        var settings = new Dictionary<string, byte[]> { ["settings.json"] = "{\"LinesPerSlide\":3}"u8.ToArray() };
        var backup = BackupPath();
        var manifest = LibraryBackup.Write(library, backup, new BackupWriteOptions(IncludeMedia: true, Settings: settings), cancel: Ct);
        Assert.Equal(1, manifest.FontCount);
        Assert.Equal(1, manifest.SettingsCount);

        var fresh = NewLibrary("fresh");
        var freshUsage = new LibraryUsage(fresh.Root);
        var result = LibraryBackup.Restore(fresh, backup, new BackupRestoreOptions(), null, usage: freshUsage, cancel: Ct);
        Assert.Equal(1, result.FontsCopied);
        Assert.True(File.Exists(Path.Combine(fresh.Root, "fonts", "file.ttf")));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), freshUsage.LastUsed("s1"));
        Assert.Equal("{\"LinesPerSlide\":3}", Encoding.UTF8.GetString(result.Settings["settings.json"])); // handed back to be applied at the next launch
    }

    [Fact]
    public void ARestoredLibraryKeepsItsOwnId()
    {
        var backup = BackupPath();
        LibraryBackup.Write(Populated(), backup, WithMedia, cancel: Ct);
        var fresh = NewLibrary("fresh");
        var id = File.ReadAllText(Path.Combine(fresh.Root, LibraryStore.ManifestFileName));
        LibraryBackup.Restore(fresh, backup, new BackupRestoreOptions(BackupPolicy.Mirror), null, cancel: Ct);
        Assert.Equal(id, File.ReadAllText(Path.Combine(fresh.Root, LibraryStore.ManifestFileName)));
    }

    [Fact]
    public void AnIncompleteBackupAndABadDocumentAreReported()
    {
        var backup = BackupPath();
        LibraryBackup.Write(Populated(), backup, WithMedia, cancel: Ct);
        using (var zip = ZipFile.Open(backup, ZipArchiveMode.Update))
        {
            zip.GetEntry("Themes/t1.json")!.Delete();
            zip.GetEntry("Playlists/p1.json")!.Delete();
            Write(zip, "Playlists/p1.json", "not json");
        }
        var fresh = NewLibrary("fresh");
        var result = LibraryBackup.Restore(fresh, backup, new BackupRestoreOptions(), null, cancel: Ct);
        Assert.Equal(["s1"], fresh.All<Presentation>().Select(p => p.Id));
        Assert.Contains(result.Warnings, w => w.Contains("Themes lists 1 documents but holds 0"));
        Assert.Contains(result.Warnings, w => w.StartsWith("Playlists/p1.json could not be imported", StringComparison.Ordinal));
    }

    [Fact]
    public void ADamagedMediaFileIsLeftOut()
    {
        var backup = BackupPath();
        var source = Populated();
        LibraryBackup.Write(source, backup, WithMedia, cancel: Ct);
        var blob = Path.GetFileName(source.BlobPath(source.Get<MediaItem>("m1")!)!);
        using (var zip = ZipFile.Open(backup, ZipArchiveMode.Update))
        {
            zip.GetEntry($"Blobs/{blob}")!.Delete();
            Write(zip, $"Blobs/{blob}", "different bytes under the same hash");
        }
        var fresh = NewLibrary("fresh");
        var result = LibraryBackup.Restore(fresh, backup, new BackupRestoreOptions(), null, cancel: Ct);
        Assert.Equal(0, result.MediaFilesCopied);
        Assert.Contains(result.Warnings, w => w.Contains("is damaged"));
        Assert.Null(fresh.BlobPath(fresh.Get<MediaItem>("m1")!));
    }

    [Fact]
    public void AHostileEntryCantWriteOutsideTheLibrary()
    {
        Assert.Null(LibraryBackup.SafeRelative("Blobs/../../evil.exe"));
        Assert.Null(LibraryBackup.SafeRelative("Windows extras/../../../x.json"));
        Assert.Null(LibraryBackup.SafeRelative("fonts/CON"));
        Assert.Null(LibraryBackup.SafeRelative("C:/x"));
        Assert.Equal(Path.Combine("Windows extras", "kind", "a.json"), LibraryBackup.SafeRelative("Windows extras/kind/a.json"));

        var backup = BackupPath();
        LibraryBackup.Write(Populated(), backup, new BackupWriteOptions(IncludeMedia: false), cancel: Ct);
        using (var zip = ZipFile.Open(backup, ZipArchiveMode.Update)) Write(zip, "Windows extras/../../escaped.json", "{}");
        var fresh = NewLibrary("fresh");
        LibraryBackup.Restore(fresh, backup, new BackupRestoreOptions(), null, cancel: Ct);
        Assert.False(File.Exists(Path.Combine(Scratch, "escaped.json")));
    }

    [Fact]
    public void AnOldDailyCopyCanBeRestored()
    {
        var live = Populated();
        var daily = Path.Combine(live.BackupDirectory, "2026-10-24");
        // The old daily copy's layout (LibraryStore.BackupIfDue): the document folders and library.json.
        foreach (var folder in new[] { "Presentations", "Themes" })
        {
            Directory.CreateDirectory(Path.Combine(daily, folder));
            foreach (var file in Directory.GetFiles(Path.Combine(live.Root, folder), "*.json")) File.Copy(file, Path.Combine(daily, folder, Path.GetFileName(file)));
        }
        File.Copy(Path.Combine(live.Root, "library.json"), Path.Combine(daily, "library.json"));
        var scan = LibraryBackup.Scan(daily);
        Assert.True(scan.IsDailyCopy);
        Assert.Equal(new DateTime(2026, 10, 24), scan.Manifest.Created!.Value.LocalDateTime.Date);

        var song = live.Get<Presentation>("s1")!;
        song.Name = "Changed today";
        live.Save(song);
        LibraryBackup.Restore(live, daily, new BackupRestoreOptions(BackupPolicy.Replace), null, cancel: Ct);
        Assert.Equal("Amazing Grace", live.Get<Presentation>("s1")!.Name);
        Assert.Contains(BackupFiles.List(live.BackupDirectory), e => e.Kind == BackupFiles.Kind.DailyCopy);
    }

    // MARK: Automatic backups

    [Fact]
    public void AutomaticBackupsAreDueByTheirFrequency()
    {
        var now = new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);
        Assert.True(BackupFiles.IsDue(BackupFrequency.Daily, null, now, atLaunch: false));
        Assert.False(BackupFiles.IsDue(BackupFrequency.Daily, now.AddHours(-1), now, atLaunch: true));
        Assert.True(BackupFiles.IsDue(BackupFrequency.Daily, now.AddDays(-1), now, atLaunch: false));
        Assert.False(BackupFiles.IsDue(BackupFrequency.Weekly, now.AddDays(-6), now, atLaunch: true));
        Assert.True(BackupFiles.IsDue(BackupFrequency.Weekly, now.AddDays(-7), now, atLaunch: false));
        Assert.True(BackupFiles.IsDue(BackupFrequency.EveryLaunch, now, now, atLaunch: true));
        Assert.False(BackupFiles.IsDue(BackupFrequency.EveryLaunch, null, now, atLaunch: false));
        Assert.False(BackupFiles.IsDue(BackupFrequency.Off, null, now, atLaunch: true));
        Assert.True(BackupFiles.ShouldWait(includeMedia: true, showIsLive: true));
        Assert.False(BackupFiles.ShouldWait(includeMedia: false, showIsLive: true));
    }

    [Fact]
    public void PruningKeepsTheNewestAutomaticOnesAndNeverOnesSavedByHand()
    {
        var folder = PathFor("Backups");
        Directory.CreateDirectory(folder);
        var start = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        for (var day = 0; day < 6; day++) File.WriteAllText(Path.Combine(folder, BackupFiles.AutomaticName(start.AddDays(day))), "x");
        for (var n = 0; n < 7; n++) File.WriteAllText(Path.Combine(folder, BackupFiles.SafetyCopyName(start.AddHours(n))), "x");
        File.WriteAllText(Path.Combine(folder, "My Easter copy.mxubackup"), "x");

        Assert.Equal(2 + 2, BackupFiles.Prune(folder, keep: 4));
        var left = BackupFiles.List(folder);
        Assert.Equal(4, left.Count(e => e.Kind == BackupFiles.Kind.Automatic));
        Assert.Equal(BackupFiles.SafetyCopiesKept, left.Count(e => e.Kind == BackupFiles.Kind.SafetyCopy));
        Assert.Contains(left, e => e.Kind == BackupFiles.Kind.Saved);
        Assert.Equal(start.AddDays(5).LocalDateTime, BackupFiles.LastAutomatic(folder)!.Value.LocalDateTime, TimeSpan.FromSeconds(1));
        Assert.Equal(left.OrderByDescending(e => e.Created), left); // newest first
    }

    [Fact]
    public void PortableSettingsLeaveThisPcsOwnBehind()
    {
        var keys = new HashSet<string> { "LinesPerSlide", "MidiMap" };
        var mine = "{\"LinesPerSlide\":2,\"Screens\":[1],\"LibraryPath\":\"C:\\\\mine\"}"u8.ToArray();
        var portable = Encoding.UTF8.GetString(BackupSettings.Portable(mine, keys)!);
        Assert.Contains("LinesPerSlide", portable);
        Assert.DoesNotContain("Screens", portable);
        Assert.DoesNotContain("LibraryPath", portable);

        var incoming = "{\"LinesPerSlide\":4,\"MidiMap\":{},\"LibraryPath\":\"D:\\\\theirs\"}"u8.ToArray();
        var applied = Encoding.UTF8.GetString(BackupSettings.Apply(mine, incoming, keys, BackupPolicy.NewerWins)!);
        Assert.Contains("\"LinesPerSlide\": 4", applied);
        Assert.Contains("C:\\\\mine", applied); // a key that mustn't travel is never taken
        var kept = Encoding.UTF8.GetString(BackupSettings.Apply(mine, incoming, keys, BackupPolicy.KeepMine)!);
        Assert.Contains("\"LinesPerSlide\": 2", kept);
        Assert.Contains("MidiMap", kept); // keep mine still takes what this PC hadn't set
    }

    private static void Write(ZipArchive zip, string name, string text)
    {
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(Encoding.UTF8.GetBytes(text));
    }

    private static byte[] ReadEntry(ZipArchive zip, string name)
    {
        using var stream = zip.GetEntry(name)!.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
