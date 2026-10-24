using MxU.Slides.Broadcast.Control;
using MxU.Slides.Broadcast.Library;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Broadcast.Tests;

/// <summary>Port of RecordingLibraryTests.swift.</summary>
public sealed class RecordingLibraryTests : IDisposable
{
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "RecordingLibrary-" + Guid.NewGuid().ToString("N"));

    public RecordingLibraryTests() => Directory.CreateDirectory(scratch);

    public void Dispose()
    {
        try { Directory.Delete(scratch, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static MediaItem Item(string? folder, double? recordedAt, MediaFileStatus status = MediaFileStatus.Ready, string name = "clip") => new()
    {
        Id = Guid.NewGuid().ToString(), Name = name, MediaKind = MediaKind.Video, Classification = MediaClassification.Foreground,
        FileHash = "hash", FileName = $"{name}.mp4", FileStatus = status, StatusDetail = "", Folder = folder, RecordedAt = recordedAt,
    };

    [Fact]
    public void NewestReadyRecordingInFolderWins()
    {
        var items = new[]
        {
            Item("Recordings", 100, name: "early"),
            Item("Recordings", 300, name: "latest"),
            Item("Recordings", 200, name: "middle"),
        };
        Assert.Equal("latest", RecordingLibrary.LatestRecording(items, "Recordings")?.Name);
    }

    [Fact]
    public void ImportedMediaNeverWins()
    {
        var items = new[] { Item("Recordings", null, name: "imported"), Item("Recordings", 100, name: "recorded") };
        Assert.Equal("recorded", RecordingLibrary.LatestRecording(items, "Recordings")?.Name);
    }

    [Fact]
    public void UnreadyAndForeignFoldersAreExcluded()
    {
        var items = new[]
        {
            Item("Recordings", 900, MediaFileStatus.NeedsTranscode, "flagged"),
            Item("Backgrounds", 800, name: "elsewhere"),
            Item("Recordings Archive", 700, name: "sibling"),
            Item("Recordings/2026", 100, name: "nested"),
        };
        Assert.Equal("nested", RecordingLibrary.LatestRecording(items, "Recordings")?.Name);
    }

    [Fact]
    public void FreshnessGuardExcludesStaleRecordings()
    {
        var lastWeek = Item("Recordings", 1_000, name: "last-week");
        var cutoff = DateTimeOffset.FromUnixTimeSeconds(2_000);
        Assert.Null(RecordingLibrary.LatestRecording([lastWeek], "Recordings", cutoff));
        var today = Item("Recordings", 3_000, name: "today");
        Assert.Equal("today", RecordingLibrary.LatestRecording([lastWeek, today], "Recordings", cutoff)?.Name);
        Assert.Equal("last-week", RecordingLibrary.LatestRecording([lastWeek], "Recordings")?.Name);
    }

    [Fact]
    public void EmptyFolderResolvesNull() =>
        Assert.Null(RecordingLibrary.LatestRecording([Item("Backgrounds", 100)], "Recordings"));

    [Fact]
    public void RecordingsAreCapturesOrAnythingUnderRecordings()
    {
        Assert.True(RecordingLibrary.IsRecording(Item("Sermons", 100)));
        Assert.True(RecordingLibrary.IsRecording(Item("Recordings/2026", null)));
        Assert.False(RecordingLibrary.IsRecording(Item("Recordings Archive", null)));
        Assert.False(RecordingLibrary.IsRecording(Item(null, null)));
    }

    [Fact]
    public async Task ExportCopyIsTheSameBytesAndEditable()
    {
        var blob = Path.Combine(scratch, "abc.mp4");
        await File.WriteAllTextAsync(blob, "recording", Ct);
        File.SetAttributes(blob, FileAttributes.ReadOnly);
        var output = Path.Combine(scratch, "Quick Record.mp4");
        await File.WriteAllTextAsync(output, "older", Ct);
        File.SetAttributes(output, FileAttributes.ReadOnly);

        RecordingLibrary.ExportCopy(blob, output);
        Assert.Equal("recording", await File.ReadAllTextAsync(output, Ct));
        Assert.False(File.GetAttributes(output).HasFlag(FileAttributes.ReadOnly), "the copy can be edited");
        Assert.True(File.GetAttributes(blob).HasFlag(FileAttributes.ReadOnly), "the library's own file is left alone");
        File.SetAttributes(blob, FileAttributes.Normal);
    }

    [Fact]
    public async Task AdoptionMovesSpoolFileAndCreatesRecordingItem()
    {
        var root = Path.Combine(scratch, "Library");
        var store = new LibraryStore(root);
        store.Load();
        var spool = RecordingLibrary.SpoolDirectory(root);
        var file = await TestMovies.WriteAsync(Path.Combine(spool, "Sunday 2026-08-10 at 9.00.00 AM.mp4"), frames: 8);
        var startedAt = DateTimeOffset.FromUnixTimeSeconds(1_754_000_000);
        var exportDir = Path.Combine(root, "dropbox-sync");

        var item = RecordingLibrary.Adopt(store, file, "Recordings", startedAt, "preset-1", exportDir);

        Assert.False(File.Exists(file), "the spool file moved into the library");
        Assert.NotNull(store.BlobPath(item));
        var exported = Path.Combine(exportDir, item.FileName);
        Assert.True(File.Exists(exported));
        Assert.False(File.GetAttributes(exported).HasFlag(FileAttributes.ReadOnly));
        Assert.Equal(MediaFileStatus.Ready, item.FileStatus);
        Assert.Equal("Recordings", item.Folder);
        Assert.Equal(startedAt.ToUnixTimeSeconds(), item.RecordedAt);
        Assert.Equal("preset-1", item.RecordedByPresetId);
        Assert.Equal(MediaKind.Video, item.MediaKind);
        Assert.True(item.DurationSeconds > 0);
        Assert.Equal((160, 96), (item.PixelWidth, item.PixelHeight));
        Assert.Equal("Sunday 2026-08-10 at 9.00.00 AM", item.Name);

        var reloaded = new LibraryStore(root);
        reloaded.Load();
        var loaded = reloaded.Get<MediaItem>(item.Id)!;
        Assert.Equal(item.RecordedAt, loaded.RecordedAt);
        Assert.Equal(item.Id, RecordingLibrary.LatestRecording([loaded], "Recordings")?.Id);
    }

    [Fact]
    public async Task AdoptingTheSameFileAgainFinishesTheJobWithoutADuplicate()
    {
        var root = Path.Combine(scratch, "Library");
        var store = new LibraryStore(root);
        store.Load();
        var spool = RecordingLibrary.SpoolDirectory(root);
        var file = await TestMovies.WriteAsync(Path.Combine(spool, "Take.mp4"), frames: 4);
        var copy = Path.Combine(scratch, "Take copy.mp4");
        File.Copy(file, copy);
        var first = RecordingLibrary.Adopt(store, file, "Recordings", DateTimeOffset.Now, null);
        // As if the app had stopped after saving the item but before moving the file.
        File.Move(copy, Path.Combine(spool, "Take.mp4"));
        var adopted = RecordingLibrary.AdoptLeftovers(store, spool);
        Assert.Equal(first.Id, Assert.Single(adopted).Id);
        Assert.Single(store.All<MediaItem>());
        Assert.Empty(Directory.GetFiles(spool));
    }
}

/// <summary>Port of the source half of the Mac's ServiceControls (resolveSourceItem, fileSource, captureSourcePreview).</summary>
public sealed class StreamSourcesTests : IDisposable
{
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "StreamSources-" + Guid.NewGuid().ToString("N"));
    private readonly LibraryStore store;
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    public StreamSourcesTests()
    {
        store = new LibraryStore(scratch);
        store.Load();
    }

    public void Dispose()
    {
        try { Directory.Delete(scratch, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private MediaItem SaveVideo(string name, double? recordedAt, string? folder = "Recordings", MediaKind kind = MediaKind.Video,
        MediaFileStatus status = MediaFileStatus.Ready, bool withFile = true)
    {
        var item = RecordingLibraryTests.Item(folder, recordedAt, status, name);
        item.MediaKind = kind;
        item.FileHash = Guid.NewGuid().ToString("N");
        item.DurationSeconds = 600;
        store.Save(item);
        if (withFile)
        {
            Directory.CreateDirectory(store.BlobDirectory);
            File.WriteAllText(Path.Combine(store.BlobDirectory, item.FileHash + ".mp4"), "video");
        }
        return item;
    }

    private static StreamRecordPreset Preset(StreamSourceKind? kind = null) =>
        new() { Id = "p", Name = "Replay", PresetKind = StreamPresetKind.Stream, SourceKind = kind };

    [Fact]
    public void AScreenSourceStreamsTheScreen()
    {
        Assert.IsType<ResolvedSource.Screen>(StreamSources.Resolve(store, Preset(), Now));
        Assert.IsType<ResolvedSource.Screen>(StreamSources.Resolve(store, Preset(StreamSourceKind.Screen), Now));
    }

    [Fact]
    public void AChosenVideoStreamsItsFileBetweenItsInAndOutPoints()
    {
        var item = SaveVideo("Intro", null, folder: null);
        item.InPoint = 10;
        item.OutPoint = 70;
        store.Save(item);
        var preset = Preset(StreamSourceKind.MediaItem);
        preset.SourceMediaId = item.Id;
        var file = Assert.IsType<ResolvedSource.MediaFile>(StreamSources.Resolve(store, preset, Now));
        Assert.Equal(store.BlobPath(item), file.Path);
        Assert.Equal((10.0, 70.0, 60.0), (file.InPoint, file.OutPoint, file.DurationSeconds));
        Assert.Equal("Restreams “Intro”", StreamSources.CapturePreview(store, preset, [Ready()], Now)?.Text);

        preset.SourceMediaId = "gone";
        Assert.Equal("media item not found in the library", Assert.IsType<ResolvedSource.Unavailable>(StreamSources.Resolve(store, preset, Now)).Reason);
    }

    [Fact]
    public void OnlyAReadyVideoOnThisPcCanBeStreamed()
    {
        Assert.Equal("“Logo” isn't a video", Reason(SaveVideo("Logo", null, kind: MediaKind.Image)));
        Assert.Equal("“Raw” isn't ready to play", Reason(SaveVideo("Raw", null, status: MediaFileStatus.NeedsTranscode)));
        Assert.Equal("“Away”: file isn't on this PC", Reason(SaveVideo("Away", null, withFile: false)));

        string Reason(MediaItem item) =>
            Assert.IsType<ResolvedSource.Unavailable>(StreamSources.FileSource(item, store.BlobPath(item))).Reason;
    }

    [Fact]
    public void TheLatestRecordingIsTheNewestReadyOneWithinTheAgeLimit()
    {
        var preset = Preset(StreamSourceKind.LatestRecording);
        Assert.Equal("no finished recording in “Recordings”", Assert.IsType<ResolvedSource.Unavailable>(StreamSources.Resolve(store, preset, Now)).Reason);

        SaveVideo("Last week", Now.AddDays(-7).ToUnixTimeSeconds());
        var sunday = SaveVideo("Sunday", Now.AddHours(-5).ToUnixTimeSeconds());
        Assert.Equal(store.BlobPath(sunday), Assert.IsType<ResolvedSource.MediaFile>(StreamSources.Resolve(store, preset, Now)).Path);
        Assert.Equal("Restreams “Sunday” · recorded 5h ago", StreamSources.CapturePreview(store, preset, [Ready()], Now)?.Text);

        preset.SourceMaxAgeHours = 2;
        Assert.Equal("newest recording in “Recordings” is 5h old — over the 2h limit",
            Assert.IsType<ResolvedSource.Unavailable>(StreamSources.Resolve(store, preset, Now)).Reason);
        Assert.Equal(new StreamSources.Preview("Won't go live: newest recording in “Recordings” is 5h old — over the 2h limit", true),
            StreamSources.CapturePreview(store, preset, [Ready()], Now));

        preset.SourceFolder = "Sermons";
        preset.SourceMaxAgeHours = null;
        Assert.Equal("no finished recording in “Sermons”", Assert.IsType<ResolvedSource.Unavailable>(StreamSources.Resolve(store, preset, Now)).Reason);
    }

    [Fact]
    public void PreviewsWarnAboutDestinationsFirst()
    {
        var preset = Preset();
        Assert.Equal(new StreamSources.Preview("Won't go live: no destinations selected", true), StreamSources.CapturePreview(store, preset, [], Now));
        var unkeyed = Ready();
        unkeyed.StreamKey = null;
        unkeyed.Name = "Resi";
        Assert.Equal(new StreamSources.Preview("Will skip — Resi: no stream key", true), StreamSources.CapturePreview(store, preset, [Ready(), unkeyed], Now));
        Assert.Null(StreamSources.CapturePreview(store, preset, [Ready()], Now));
    }

    [Fact]
    public void SummariesReadLikeTheMac()
    {
        Assert.Equal("under an hour", StreamSources.AgeText(Now.AddMinutes(-20).ToUnixTimeSeconds(), Now));
        Assert.Equal("47h", StreamSources.AgeText(Now.AddHours(-47).ToUnixTimeSeconds(), Now));
        Assert.Equal("3 days", StreamSources.AgeText(Now.AddDays(-3).ToUnixTimeSeconds(), Now));
        Assert.Equal("1080p · 30 fps · 6.0 Mbps", StreamSources.EncoderSummary(new StreamRecordPreset { Id = "a", Name = "A", VideoBitrateKbps = 6000 }));
        Assert.Equal("720p · 60 fps · 800 kbps", StreamSources.EncoderSummary(new StreamRecordPreset { Id = "a", Name = "A", Width = 1280, Height = 720, FrameRate = 60, VideoBitrateKbps = 800 }));
        var stage = new StreamRecordPreset { Id = "a", Name = "A", CanvasScreenId = "S" };
        Assert.Equal("Screen · Stage", StreamSources.VideoSourceTitle(store, stage, id => id == "S" ? "Stage" : null));
        Assert.Equal("Screen · Automatic", StreamSources.VideoSourceTitle(store, Preset(), _ => null));
        Assert.Equal("Latest recording · Recordings", StreamSources.VideoSourceTitle(store, Preset(StreamSourceKind.LatestRecording), _ => null));
    }

    private static StreamPresetDestination Ready() => new()
    {
        Id = Guid.NewGuid().ToString(), Name = "YouTube", Transport = StreamTransport.Rtmps, Url = "rtmps://a.rtmps.youtube.com/live2", StreamKey = "k",
    };
}
