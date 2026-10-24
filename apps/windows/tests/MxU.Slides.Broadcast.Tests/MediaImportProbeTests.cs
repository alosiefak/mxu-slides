using MxU.Slides.Broadcast.Library;
using MxU.Slides.Broadcast.Media;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Broadcast.Tests;

/// <summary>
/// Transcoding keeps every core busy, which would make the real-time recorder and replay tests
/// miss their timing, so these run on their own rather than beside them.
/// </summary>
[CollectionDefinition("Transcoding", DisableParallelization = true)]
public sealed class TranscodingCollection;

/// <summary>
/// Looking inside imported files with Windows' own decoders, and transcoding the ones flagged.
/// Port of MediaImportTests.swift (fiftyMixedFilesImportOrganizedAndFlagged and friends), with
/// files Windows can make: PNGs, H.264 MP4s from the app's recorder, and uncompressed AVIs
/// standing in for the Mac's Motion JPEG movies.
/// </summary>
[Collection("Transcoding")]
public sealed class MediaImportProbeTests : IDisposable
{
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "MediaImportProbe-" + Guid.NewGuid().ToString("N"));

    public MediaImportProbeTests() => Directory.CreateDirectory(scratch);

    public void Dispose()
    {
        try { Directory.Delete(scratch, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string File(string name) => Path.Combine(scratch, name);

    [Fact]
    public void APictureReportsItsSize()
    {
        TestMediaFiles.WritePng(File("bg.png"), 64, 40);
        var probe = new MediaImportProbe().Probe(File("bg.png"));
        Assert.NotNull(probe);
        Assert.Equal((MediaKind.Image, MediaFileStatus.Ready, 64, 40), (probe.MediaKind, probe.FileStatus, probe.PixelWidth, probe.PixelHeight));
        Assert.Null(probe.SkipReason);
    }

    [Fact]
    public void APictureWindowsCantReadIsSkippedWithTheReason()
    {
        System.IO.File.WriteAllBytes(File("broken.png"), [1, 2, 3, 4, 5, 6, 7, 8]);
        var probe = new MediaImportProbe().Probe(File("broken.png"));
        Assert.NotNull(probe?.SkipReason);
        Assert.Contains("Windows can't read this picture", probe.SkipReason);
    }

    [Fact]
    public async Task AnH264VideoIsReadyWithItsLengthAndSize()
    {
        var path = await TestMovies.WriteAsync(File("clip.mp4"), frames: 30, withAudio: true);
        var probe = new MediaImportProbe().Probe(path);
        Assert.NotNull(probe);
        Assert.Equal(MediaFileStatus.Ready, probe.FileStatus);
        Assert.Equal("", probe.StatusDetail);
        Assert.Equal((160, 96), (probe.PixelWidth, probe.PixelHeight));
        Assert.InRange(probe.DurationSeconds ?? 0, 0.8, 1.3);
        Assert.True(probe.HasVideo);
        Assert.True(probe.HasAudio);
    }

    [Fact]
    public async Task AVideoWithoutSoundSaysSo()
    {
        var path = await TestMovies.WriteAsync(File("silent.mp4"), frames: 10);
        var probe = new MediaImportProbe().Probe(path)!;
        Assert.True(probe.HasVideo);
        Assert.False(probe.HasAudio);
    }

    [Fact]
    public void AnUncompressedAviDecodesButIsFlaggedForTranscoding()
    {
        TestMediaFiles.WriteUncompressedAvi(File("raw.avi"), 64, 48, 15);
        var probe = new MediaImportProbe().Probe(File("raw.avi"))!;
        Assert.Equal(MediaFileStatus.NeedsTranscode, probe.FileStatus);
        Assert.StartsWith("unsupported codec:", probe.StatusDetail);
        Assert.Equal((64, 48), (probe.PixelWidth, probe.PixelHeight));
    }

    [Fact]
    public void ACorruptVideoIsFlaggedNotRefused()
    {
        System.IO.File.WriteAllBytes(File("corrupt.mov"), [.. Enumerable.Range(0, 4096).Select(i => (byte)(i * 31 % 251))]);
        var probe = new MediaImportProbe().Probe(File("corrupt.mov"))!;
        Assert.Equal(MediaFileStatus.NeedsTranscode, probe.FileStatus);
        Assert.StartsWith("unreadable by Windows", probe.StatusDetail);
        Assert.Null(probe.DurationSeconds);
    }

    [Fact]
    public void TheJudgementFollowsTheMacsRules()
    {
        MediaPlayability.Findings Found(bool video = true, string codec = "H264", bool decodes = true, bool audio = false, bool audioDecodes = true) =>
            new(null, video, codec, decodes, audio, "AAC", audioDecodes);
        Assert.Equal((MediaFileStatus.Ready, ""), MediaPlayability.Judge(Found()));
        Assert.Equal((MediaFileStatus.Ready, ""), MediaPlayability.Judge(Found(codec: "HEVC")));
        Assert.Equal((MediaFileStatus.NeedsTranscode, "no video track"), MediaPlayability.Judge(Found(video: false)));
        Assert.StartsWith("unsupported codec: MJPG", MediaPlayability.Judge(Found(codec: "MJPG")).Detail);
        var prores = MediaPlayability.Judge(Found(codec: "apch", decodes: false));
        Assert.Equal(MediaFileStatus.NeedsTranscode, prores.Status);
        Assert.Contains("ProRes 422 HQ", prores.Detail);
        Assert.Contains("HEVC Video Extensions", MediaPlayability.Judge(Found(codec: "HEVC", decodes: false)).Detail);
        var silent = MediaPlayability.Judge(Found(audio: true, audioDecodes: false));
        Assert.Equal(MediaFileStatus.Ready, silent.Status);
        Assert.Contains("plays silently", silent.Detail);
        Assert.StartsWith("unreadable by Windows", MediaPlayability.Judge(new("bad header", false, "", false, false, "", false)).Detail);
    }

    [Fact]
    public void CodecNamesComeFromTheSubtype()
    {
        Assert.Equal("H264", MediaImportProbe.CodecName(new Guid("34363248-0000-0010-8000-00AA00389B71")));
        Assert.Equal("RGB24", MediaImportProbe.CodecName(new Guid("00000014-0000-0010-8000-00AA00389B71")));
        Assert.Equal("MPEG-2", MediaImportProbe.CodecName(new Guid("e06d8026-db46-11cf-b4d1-00805f6cbbea")));
        Assert.Equal("apch", MediaImportProbe.CodecName(new Guid("68637061-0000-0010-8000-00AA00389B71")));
    }

    [Fact]
    public async Task AFlaggedVideoTranscodesToAnH264CopyThatIsReady()
    {
        TestMediaFiles.WriteUncompressedAvi(File("raw.avi"), 128, 96, 30);
        var output = File("raw.mp4");
        var progress = new List<double>();
        MediaTranscoder.ToMp4(File("raw.avi"), output, new SyncProgress(progress.Add), TestContext.Current.CancellationToken);
        var probe = new MediaImportProbe().Probe(output)!;
        Assert.Equal(MediaFileStatus.Ready, probe.FileStatus);
        Assert.Equal((128, 96), (probe.PixelWidth, probe.PixelHeight));
        Assert.Equal(1, progress[^1]);
        Assert.InRange(probe.DurationSeconds ?? 0, 0.8, 1.2);
        await Task.CompletedTask;
    }

    /// <summary>Reports on the calling thread (Progress&lt;T&gt; would post to the thread pool).</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    [Fact]
    public async Task SoundSurvivesATranscode()
    {
        var path = await TestMovies.WriteAsync(File("talk.mp4"), frames: 30, withAudio: true);
        var output = File("talk-copy.mp4");
        MediaTranscoder.ToMp4(path, output, cancellationToken: TestContext.Current.CancellationToken);
        var probe = MediaProbe.Read(output);
        Assert.True(probe.HasAudio);
        Assert.True(probe.AudioSamples > 0);
        Assert.InRange(probe.LastAudioTime, 0.7, 1.3);
    }

    [Fact]
    public void ATranscodeOfAnUnreadableFileFailsWithTheReason()
    {
        System.IO.File.WriteAllBytes(File("junk.avi"), [1, 2, 3]);
        var failure = Assert.Throws<MediaTranscoder.TranscodeException>(() =>
            MediaTranscoder.ToMp4(File("junk.avi"), File("junk.mp4"), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("can't open", failure.Message);
        Assert.False(System.IO.File.Exists(File("junk.mp4")), "nothing half-made is left");
    }

    [Fact]
    public void BigPicturesAreShrunkToFourKAndKeptEven()
    {
        Assert.Equal((1920, 1080), MediaTranscoder.Fit(1920, 1080));
        Assert.Equal((640, 360), MediaTranscoder.Fit(641, 361));
        Assert.Equal((3840, 2160), MediaTranscoder.Fit(7680, 4320));
        Assert.Equal((128, 96), MediaTranscoder.Fit(32, 24));
    }

    /// <summary>MediaImportTests.fiftyMixedFilesImportOrganizedAndFlagged, at a size Windows can make quickly.</summary>
    [Fact]
    public async Task MixedFilesImportOrganisedAndFlaggedThenTranscode()
    {
        var folder = Directory.CreateDirectory(File("drop")).FullName;
        for (var i = 0; i < 5; i++) TestMediaFiles.WritePng(Path.Combine(folder, $"image-{i}.png"), 32, 32, red: (byte)(i * 40));
        for (var i = 0; i < 3; i++) await TestMovies.WriteAsync(Path.Combine(folder, $"clip-{i}.mp4"), frames: 6 + i);
        for (var i = 0; i < 2; i++) TestMediaFiles.WriteUncompressedAvi(Path.Combine(folder, $"legacy-{i}.avi"), 32, 32, 6 + i);
        System.IO.File.WriteAllBytes(Path.Combine(folder, "corrupt.mov"), [.. Enumerable.Range(0, 4096).Select(i => (byte)(i * 17 % 253))]);
        System.IO.File.WriteAllText(Path.Combine(folder, "notes.xyz"), "not media");

        var library = new LibraryStore(File("library")) { Prober = new MediaImportProbe() };
        library.Load();
        var files = MediaImporting.Files([folder]);
        Assert.Equal(12, files.Count);
        var results = files.Select(f => MediaImporting.AddMedia(library, f)).ToList();
        Assert.Equal(1, results.Count(r => r is null)); // notes.xyz
        var items = library.All<MediaItem>();
        Assert.Equal(11, items.Count);
        Assert.Equal(5, items.Count(m => m.MediaKind == MediaKind.Image));

        var flagged = items.Where(m => m.FileStatus == MediaFileStatus.NeedsTranscode).ToList();
        Assert.Equal(3, flagged.Count);
        Assert.All(flagged, m => Assert.NotEqual("", m.StatusDetail));
        var clips = items.Where(m => m.MediaKind == MediaKind.Video && m.FileStatus == MediaFileStatus.Ready).ToList();
        Assert.Equal(3, clips.Count);
        Assert.All(clips, m => Assert.True(m.PixelWidth == 160 && m.DurationSeconds > 0));

        var outcomes = new TranscodeQueue(library).Drain(TestContext.Current.CancellationToken);
        Assert.Equal(2, outcomes.Values.Count(s => s == MediaFileStatus.Ready));
        Assert.Equal(1, outcomes.Values.Count(s => s == MediaFileStatus.TranscodeFailed));
        var fixedItem = library.Get<MediaItem>(outcomes.First(o => o.Value == MediaFileStatus.Ready).Key)!;
        Assert.Equal("", fixedItem.StatusDetail);
        var copy = library.BlobPath(fixedItem);
        Assert.NotNull(copy);
        Assert.Equal(MediaFileStatus.Ready, MediaImportProbe.ProbeVideo(copy).FileStatus);
        var failed = library.Get<MediaItem>(outcomes.First(o => o.Value == MediaFileStatus.TranscodeFailed).Key)!;
        Assert.Contains("can't open", failed.StatusDetail);
        Assert.Empty(Directory.EnumerateFiles(library.BlobDirectory, "*.partial.*"));
    }
}
