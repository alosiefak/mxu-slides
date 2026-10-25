using System.Runtime.InteropServices;
using MxU.Slides.Broadcast.Library;
using MxU.Slides.Broadcast.Media;
using MxU.Slides.Broadcast.Media.Interop;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.MotionTemplates;
using MxU.Slides.MotionTemplates.Export;
using MxU.Slides.MotionTemplates.Lottie;

namespace MxU.Slides.MotionTemplates.Tests;

// Templates to H.264 MP4 (Phase 11), through the app's own encoder: the file plays, has every
// frame of the play-through at its moment, shows the design over the chosen colour, files into
// the library, and a cancel leaves nothing. Encoding keeps cores busy, so these run one at a time.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EncodingCollection
{
    public const string Name = "Encoding";
}

[Collection(EncodingCollection.Name)]
public sealed class VideoExportTests : IDisposable
{
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "MotionExport-" + Guid.NewGuid().ToString("N"));

    public VideoExportTests() => Directory.CreateDirectory(scratch);

    public void Dispose()
    {
        try { Directory.Delete(scratch, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MotionTimeline Strap() => MotionTimeline.For(StarterMotionTemplates.NameStrap(), MotionPlayback.Loop);

    [Fact]
    public void ALowerThirdBecomesAPlayableH264FileWithEveryFrame()
    {
        var path = Path.Combine(scratch, "strap.mp4");
        var options = new MotionExportOptions(320, 180, 30, MotionExportOptions.KeyGreen, HoldSeconds: 1);
        LottieVideoMaker.Render(StarterMotionTemplates.NameStrap(), Strap(), options, path, LottieRendererTests.ShippedFonts, cancellationToken: Ct);

        // In (48 frames at 60 = 0.8 s), a 1 s hold, Out (0.8 s): 24 + 30 + 24 video frames at 30.
        var expected = LottieVideoMaker.Frames(Strap(), options).Count;
        Assert.Equal(78, expected);
        var (width, height, subtype, samples, duration) = Probe(path);
        Assert.Equal((320, 180), (width, height));
        Assert.Equal(MF.MFVideoFormat_H264, subtype);
        Assert.Equal(expected, samples);
        Assert.Equal(78 / 30.0, duration, 2);
        // The app's import probe finds it ready to play.
        Assert.Equal(MediaFileStatus.Ready, MediaImportProbe.ProbeVideo(path).FileStatus);
        Assert.False(File.Exists(Path.Combine(scratch, "strap.partial.mp4")));
    }

    [Fact]
    public void TheFileShowsTheDesignOverTheChosenColour()
    {
        // A template holding still from its first frame, so the file's first picture is the settled design.
        var b = new LottieBuilder("Plate", 320, 180, 30, 30);
        b.Layer(b.ShapeLayer("Plate", LottieBuilder.Transform(LottieBuilder.Static(160, 150, 0)),
            LottieBuilder.Group("Plate", LottieBuilder.Rect(0, 0, 200, 40), LottieBuilder.Fill("#FFFFFF"))));
        var document = b.Document();
        var path = Path.Combine(scratch, "plate.mp4");
        LottieVideoMaker.Render(document, MotionTimeline.For(document, MotionPlayback.HoldLast), new MotionExportOptions(320, 180, 30, "#0000FF"), path, cancellationToken: Ct);
        var first = FirstFrame(path, 320, 180);
        // Where there's no design: the blue behind it. On the plate: white. H.264 loses a little, so within a few levels.
        var (bb, bg, br) = (first[(20 * 320 + 20) * 4], first[(20 * 320 + 20) * 4 + 1], first[(20 * 320 + 20) * 4 + 2]);
        Assert.True(bb > 220 && bg < 30 && br < 30, $"background is {br},{bg},{bb}");
        var (pb, pg, pr) = (first[(150 * 320 + 160) * 4], first[(150 * 320 + 160) * 4 + 1], first[(150 * 320 + 160) * 4 + 2]);
        Assert.True(pb > 225 && pg > 225 && pr > 225, $"plate is {pr},{pg},{pb}");
    }

    [Fact]
    public void ARenderFilesIntoTheLibraryAsALoopingBackgroundOrAForegroundVideo()
    {
        var library = new LibraryStore(Path.Combine(scratch, "library")) { Prober = new MediaImportProbe() };
        var card = StarterMotionTemplates.TitleCard();
        var path = Path.Combine(scratch, LottieVideoMaker.FileName("Title Card: Sunday"));
        Assert.Equal("Title Card- Sunday.mp4", Path.GetFileName(path));
        LottieVideoMaker.Render(card, MotionTimeline.For(card, MotionPlayback.Loop), new MotionExportOptions(256, 144, 30), path, cancellationToken: Ct);
        var item = LottieVideoMaker.FileIntoLibrary(library, path, "Title Card", background: true);
        Assert.Equal((MediaKind.Video, MediaClassification.Background, true, MotionTemplateStore.Pack), (item.MediaKind, item.Classification, item.Loops, item.Folder));
        Assert.Equal(8.0, item.DurationSeconds ?? 0, 1);
        Assert.False(File.Exists(path));

        var again = Path.Combine(scratch, "again.mp4");
        LottieVideoMaker.Render(card, MotionTimeline.For(card, MotionPlayback.Loop), new MotionExportOptions(320, 180, 30), again, cancellationToken: Ct);
        var replaced = LottieVideoMaker.FileIntoLibrary(library, again, "Title Card", background: true, replaceId: item.Id);
        Assert.Equal(item.Id, replaced.Id);
        Assert.NotEqual(item.FileHash, replaced.FileHash);

        var strap = Path.Combine(scratch, "strap.mp4");
        LottieVideoMaker.Render(StarterMotionTemplates.NameStrap(), Strap(), new MotionExportOptions(256, 144, 30, HoldSeconds: 0), strap, cancellationToken: Ct);
        var foreground = LottieVideoMaker.FileIntoLibrary(library, strap, "Strap over black", background: false);
        Assert.Equal((MediaClassification.Foreground, false), (foreground.Classification, foreground.Loops));
    }

    [Fact]
    public void CancellingLeavesNothingBehind()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var path = Path.Combine(scratch, "cancelled.mp4");
        var progress = new SyncProgress(p =>
        {
            if (p.Frames >= 5) cancel.Cancel();
        });
        Assert.ThrowsAny<OperationCanceledException>(() => LottieVideoMaker.Render(StarterMotionTemplates.NameStrap(), Strap(),
            new MotionExportOptions(320, 180, 30, HoldSeconds: 60), path, progress: progress, cancellationToken: cancel.Token));
        Assert.Empty(Directory.EnumerateFiles(scratch));
    }

    [Fact]
    public void OddSizesAndSillyValuesAreMadeUsable()
    {
        var options = new MotionExportOptions(1921, 1081, 500, "nope", HoldSeconds: -3, Passes: 0).Normalized();
        Assert.Equal(new MotionExportOptions(1920, 1080, 60, "#000000", 0, 1), options);
    }

    [Theory]
    [InlineData(0, 0, 0, 0, 0x000000, 16, 128, 128)]       // see-through over black: black
    [InlineData(0, 0, 0, 0, 0xFFFFFF, 235, 128, 128)]      // see-through over white: white
    [InlineData(255, 255, 255, 255, 0x000000, 235, 128, 128)] // opaque white over black: white
    [InlineData(0, 0, 128, 128, 0x0000FF, 0, 0, 0)]        // half red over blue: a mix (checked below)
    public void OverAColourIntoNv12(byte b, byte g, byte r, byte a, int background, int y, int u, int v)
    {
        var bgra = new byte[2 * 2 * 4];
        for (var i = 0; i < 4; i++) (bgra[i * 4], bgra[i * 4 + 1], bgra[i * 4 + 2], bgra[i * 4 + 3]) = (b, g, r, a);
        var nv12 = new byte[Nv12Over.FrameBytes(2, 2)];
        Nv12Over.Convert(bgra, 2, 2, 8, background, nv12);
        if (a == 128)
        {
            // Half red (premultiplied 128) over blue: R 128, G 0, B 127.
            Assert.Equal(Luma(128, 0, 127), nv12[0]);
            return;
        }
        Assert.Equal([(byte)y, (byte)y, (byte)y, (byte)y, (byte)u, (byte)v], nv12);
    }

    private static byte Luma(int r, int g, int b) => (byte)(((11967 * r + 40252 * g + 4063 * b + 32768) >> 16) + 16);

    private sealed class SyncProgress(Action<MotionRenderProgress> report) : IProgress<MotionRenderProgress>
    {
        public void Report(MotionRenderProgress value) => report(value);
    }

    /// <summary>Size, codec, how many pictures and how long, as Media Foundation reads the file back.</summary>
    private static (int Width, int Height, Guid Subtype, int Samples, double Duration) Probe(string path)
    {
        MediaFoundation.EnsureStarted();
        MF.Check(MF.MFCreateSourceReaderFromURL(path, null, out var reader), "open");
        try
        {
            var value = new PropVariant();
            double duration = 0;
            if (reader.GetPresentationAttribute(MF.MF_SOURCE_READER_MEDIASOURCE, MF.MF_PD_DURATION, ref value) >= 0)
            {
                duration = value.hVal / 10_000_000.0;
                MF.PropVariantClear(ref value);
            }
            MF.Check(reader.GetCurrentMediaType(MF.MF_SOURCE_READER_FIRST_VIDEO_STREAM, out var type), "type");
            type.GetUINT64(MF.MF_MT_FRAME_SIZE, out var size);
            type.GetGUID(MF.MF_MT_SUBTYPE, out var subtype);
            Marshal.ReleaseComObject(type);
            var samples = 0;
            while (true)
            {
                if (reader.ReadSample(MF.MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, out _, out var flags, out _, out var sample) < 0) break;
                if (sample is not null)
                {
                    samples++;
                    Marshal.ReleaseComObject(sample);
                }
                if ((flags & MF.MF_SOURCE_READERF_ENDOFSTREAM) != 0) break;
            }
            return ((int)(size >> 32), (int)(size & 0xFFFFFFFF), subtype, samples, duration);
        }
        finally
        {
            Marshal.FinalReleaseComObject(reader);
        }
    }

    /// <summary>The file's first picture, decoded by Windows to BGRA (as Broadcast.Tests' motion background tests read theirs).</summary>
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
