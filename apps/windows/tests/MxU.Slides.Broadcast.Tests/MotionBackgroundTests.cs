using System.Runtime.InteropServices;
using MxU.Slides.Broadcast.Library;
using MxU.Slides.Broadcast.Media;
using MxU.Slides.Broadcast.Media.Interop;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Motion;

namespace MxU.Slides.Broadcast.Tests;

/// <summary>
/// Motion backgrounds as video files: a short render is a playable H.264 MP4 with every frame,
/// stamped at frame ÷ frame rate; what's in the file looks like what was drawn; it files into the
/// library as a looping background with its recipe; replacing keeps the item; cancelling
/// leaves nothing; and the encoder takes 4K at 60 frames a second. They keep every core busy, so
/// they run in the "Transcoding" collection, away from the real-time tests.
/// </summary>
[Collection("Transcoding")]
public sealed class MotionBackgroundTests : IDisposable
{
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "MotionBackground-" + Guid.NewGuid().ToString("N"));

    public MotionBackgroundTests() => Directory.CreateDirectory(scratch);

    public void Dispose()
    {
        try { Directory.Delete(scratch, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MotionRecipe Recipe(string style = "gradient-flow", int seconds = 10, int frameRate = 30) =>
        MotionRecipe.For(MotionStyles.Find(style)!) with { Seconds = seconds, FrameRate = frameRate };

    private string Render(MotionRecipe recipe, string name = "loop.mp4", int width = 256, int height = 144)
    {
        var path = Path.Combine(scratch, name);
        MotionBackgroundMaker.Render(recipe, path, cancellationToken: Ct, width: width, height: height);
        return path;
    }

    [Fact]
    public void AShortRenderIsAPlayableH264Mp4WithEveryFrame()
    {
        var path = Render(Recipe("aurora"));
        var probe = MediaProbe.Read(path);
        Assert.Equal((256, 144), (probe.Width, probe.Height));
        Assert.Equal(MF.MFVideoFormat_H264, probe.VideoSubtype);
        Assert.False(probe.HasAudio);
        Assert.Equal(300, probe.VideoSamples);
        Assert.Equal(299 / 30.0, probe.LastVideoTime, 3);
        // The file says it's exactly as long as the loop: a player that loops by the stated length
        // (the Mac's) must not cut it short. A fragmented MP4 said 9.0 here, leaving out its last fragment.
        Assert.Equal(10.0, probe.DurationSeconds, 2);
        // And the app's own import probe (Phase 5) finds it ready to play.
        var import = MediaImportProbe.ProbeVideo(path);
        Assert.Equal(MediaFileStatus.Ready, import.FileStatus);
        Assert.Equal(10.0, import.DurationSeconds ?? 0, 2);
        Assert.False(File.Exists(Path.Combine(scratch, "loop.partial.mp4")));
    }

    [Fact]
    public void EveryFrameIsStampedAtItsNumberOverTheFrameRate()
    {
        var path = Render(Recipe(frameRate: 60), "sixty.mp4", 128, 72);
        var times = SampleTimes(path);
        Assert.Equal(600, times.Count);
        // The MP4 keeps time in its own units, so a stamp can come back up to a tick (100 ns) off
        // the exact i ÷ 60 s, but never more, and never drifting.
        for (var i = 0; i < times.Count; i++) Assert.InRange(times[i] - i * 1e7 / 60, -1.0, 1.0);
    }

    [Fact]
    public void TheFileShowsWhatWasDrawn()
    {
        var recipe = Recipe("aurora");
        var path = Render(recipe, "aurora.mp4", 320, 180);
        using var crew = new RenderCrew("test", 2);
        var renderer = new MotionRenderer(recipe, 320, 180, crew);
        var drawn = new byte[renderer.FrameBytes(MotionPixelFormat.Bgra32)];
        renderer.RenderFrame(0, MotionPixelFormat.Bgra32, drawn);
        var decoded = FirstFrame(path, 320, 180);
        // H.264 at this bitrate loses a little; the picture must be the same within a few levels.
        double difference = 0;
        for (var i = 0; i < drawn.Length; i += 4)
            for (var c = 0; c < 3; c++)
                difference += Math.Abs(drawn[i + c] - decoded[i + c]);
        Assert.InRange(difference / (drawn.Length / 4 * 3), 0, 4);
    }

    [Fact]
    public void ARenderFilesIntoTheLibraryAsALoopingBackgroundWithItsRecipe()
    {
        var library = new LibraryStore(Path.Combine(scratch, "library")) { Prober = new MediaImportProbe() };
        var recipe = Recipe("bokeh") with { Palette = "Midnight", Colors = MotionPalette.Find("Midnight")!.Colors, Variation = 7 };
        var path = Render(recipe, MotionBackgroundMaker.FileName(recipe));
        var item = MotionBackgroundMaker.FileIntoLibrary(library, path, recipe);

        Assert.Equal((MediaKind.Video, MediaClassification.Background, true), (item.MediaKind, item.Classification, item.Loops));
        Assert.Equal((MotionBackgroundMaker.Folder, MediaFileStatus.Ready, "Bokeh · Midnight"), (item.Folder, item.FileStatus, item.Name));
        Assert.Equal("Bokeh · Midnight.mp4", item.FileName);
        Assert.Equal(10.0, item.DurationSeconds ?? 0, 2);
        Assert.NotNull(library.BlobPath(item));
        Assert.False(File.Exists(path), "the render is moved into the library, not left behind");

        var kept = MotionBackgroundMaker.RecipeOf(library, item);
        Assert.NotNull(kept);
        Assert.Equal(("bokeh", "Midnight", 7, "Bokeh · Midnight"), (kept.Style, kept.Palette, kept.Variation, kept.Name));
        Assert.Equal(recipe.Colors, kept.Colors);

        // The shared document has nothing in it the Mac doesn't know: the recipe lives in the extras folder.
        var json = File.ReadAllText(Directory.EnumerateFiles(library.Root, item.Id + ".json", SearchOption.AllDirectories)
            .First(f => !f.Contains(WindowsExtras.FolderName)));
        foreach (var windowsOnly in new[] { "variation", "intensity", "\"style\"", "recipe" }) Assert.DoesNotContain(windowsOnly, json);
    }

    [Fact]
    public void ReRenderingIntoAnItemKeepsItsIdAndUpdatesTheRecipe()
    {
        var library = new LibraryStore(Path.Combine(scratch, "library")) { Prober = new MediaImportProbe() };
        var first = MotionBackgroundMaker.FileIntoLibrary(library, Render(Recipe("waves"), "a.mp4"), Recipe("waves"));
        first.Name = "Sunday Opener";
        library.Save(first);
        var changed = Recipe("waves") with { Palette = "Ember", Colors = MotionPalette.Find("Ember")!.Colors };
        var again = MotionBackgroundMaker.FileIntoLibrary(library, Render(changed, "b.mp4"), changed, replaceId: first.Id);

        Assert.Equal(first.Id, again.Id);
        Assert.NotEqual(first.FileHash, again.FileHash);
        Assert.Equal("Sunday Opener", again.Name);
        Assert.Single(library.All<MediaItem>(), m => m.Folder == MotionBackgroundMaker.Folder);
        Assert.Equal("Ember", MotionBackgroundMaker.RecipeOf(library, again)?.Palette);
    }

    [Fact]
    public void ARecipeIsForgottenWhenItsFileIsReplacedByAnother()
    {
        var library = new LibraryStore(Path.Combine(scratch, "library")) { Prober = new MediaImportProbe() };
        var item = MotionBackgroundMaker.FileIntoLibrary(library, Render(Recipe()), Recipe());
        item.FileHash = new string('0', 64);
        library.Save(item);
        Assert.Null(MotionBackgroundMaker.RecipeOf(library, item));
    }

    [Fact]
    public void CancellingLeavesNoFileBehind()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var path = Path.Combine(scratch, "cancelled.mp4");
        var progress = new SyncProgress(p =>
        {
            if (p.Frames >= 20) cancel.Cancel();
        });
        Assert.ThrowsAny<OperationCanceledException>(() =>
            MotionBackgroundMaker.Render(Recipe(seconds: 30), path, progress, cancel.Token, 128, 72));
        Assert.Empty(Directory.EnumerateFiles(scratch));
    }

    [Fact]
    public void TheEncoderTakesFourKAtSixtyFramesASecond()
    {
        var path = Path.Combine(scratch, "uhd.mp4");
        using var crew = new RenderCrew("test");
        var renderer = new MotionRenderer(Recipe("film-wash", frameRate: 60) with { Height = 2160 }, 3840, 2160, crew);
        var nv12 = new byte[renderer.FrameBytes(MotionPixelFormat.Nv12)];
        using (var writer = new RenderedVideoWriter(path, 3840, 2160, 60, LoopTiming.Bitrate(3840, 2160, 60)))
        {
            for (var frame = 0; frame < 12; frame++)
            {
                renderer.RenderFrame(frame, MotionPixelFormat.Nv12, nv12);
                writer.Write(nv12, frame);
            }
            writer.Finish();
        }
        var probe = MediaProbe.Read(path);
        Assert.Equal((3840, 2160, 12), (probe.Width, probe.Height, probe.VideoSamples));
        Assert.Equal(11 / 60.0, probe.LastVideoTime, 3);
    }

    private sealed class SyncProgress(Action<MotionRenderProgress> report) : IProgress<MotionRenderProgress>
    {
        public void Report(MotionRenderProgress value) => report(value);
    }

    /// <summary>Every video sample's time stamp, in order, as Media Foundation reads them back.</summary>
    private static List<long> SampleTimes(string path)
    {
        MediaFoundation.EnsureStarted();
        MF.Check(MF.MFCreateSourceReaderFromURL(path, null, out var reader), "open");
        var times = new List<long>();
        try
        {
            while (true)
            {
                var hr = reader.ReadSample(MF.MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, out _, out var flags, out var time, out var sample);
                if (hr < 0) break;
                if (sample is not null)
                {
                    times.Add(time);
                    Marshal.ReleaseComObject(sample);
                }
                if ((flags & MF.MF_SOURCE_READERF_ENDOFSTREAM) != 0) break;
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(reader);
        }
        return times;
    }

    /// <summary>The file's first picture, decoded by Windows to BGRA.</summary>
    private static byte[] FirstFrame(string path, int width, int height)
    {
        MediaFoundation.EnsureStarted();
        var attributes = MediaFoundation.CreateAttributes(1);
        attributes.SetUINT32(MF.MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, 1);
        MF.Check(MF.MFCreateSourceReaderFromURL(path, attributes, out var reader), "open");
        MediaFoundation.Release(attributes);
        try
        {
            var type = MediaFoundation.CreateMediaType();
            type.SetGUID(MF.MF_MT_MAJOR_TYPE, MF.MFMediaType_Video);
            type.SetGUID(MF.MF_MT_SUBTYPE, MF.MFVideoFormat_RGB32);
            MF.Check(reader.SetCurrentMediaType(MF.MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, type), "RGB32");
            MediaFoundation.Release(type);
            while (true)
            {
                MF.Check(reader.ReadSample(MF.MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, out _, out var flags, out _, out var sample), "read");
                if (sample is null)
                {
                    if ((flags & MF.MF_SOURCE_READERF_ENDOFSTREAM) != 0) throw new InvalidOperationException("no pictures");
                    continue;
                }
                try
                {
                    var bytes = MediaFoundation.ReadBytes(sample);
                    var pixels = new byte[width * height * 4];
                    // RGB32 from the source reader is bottom-up when its stride is positive and the
                    // buffer isn't 2-D; it's top-down here (video processing gives a top-down picture),
                    // so copy straight across.
                    Buffer.BlockCopy(bytes, 0, pixels, 0, Math.Min(bytes.Length, pixels.Length));
                    return pixels;
                }
                finally
                {
                    Marshal.ReleaseComObject(sample);
                }
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(reader);
        }
    }
}
