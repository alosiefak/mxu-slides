using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using MxU.Slides.Broadcast;
using MxU.Slides.Broadcast.Library;
using MxU.Slides.Broadcast.Media;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Motion;
using MxU.Slides.Core.MotionTemplates;
using MxU.Slides.MotionTemplates.Lottie;

namespace MxU.Slides.MotionTemplates.Export;

/// <summary>What a template's video file looks like.</summary>
/// <param name="BackgroundColor">"#RRGGBB" behind the design (H.264 has no transparency): black, or a key green.</param>
/// <param name="HoldSeconds">How long a template with markers holds between its In and its Out.</param>
/// <param name="Passes">How many times a template without markers plays (a background loop is one pass: the player loops it).</param>
public sealed record MotionExportOptions(int Width = 1920, int Height = 1080, int FrameRate = 30, string BackgroundColor = "#000000",
    double HoldSeconds = 4, int Passes = 1)
{
    /// <summary>Key green (the broadcast "chroma green", #00B140), for a lower third keyed over a camera in a vision mixer.</summary>
    public const string KeyGreen = "#00B140";

    /// <summary>Even sizes (NV12 needs them), between 16 and 4K, and a usable frame rate.</summary>
    public MotionExportOptions Normalized() => this with
    {
        Width = Math.Clamp(Width & ~1, 16, 3840),
        Height = Math.Clamp(Height & ~1, 16, 2160),
        FrameRate = Math.Clamp(FrameRate, 1, 60),
        BackgroundColor = MotionFields.NormalHex(BackgroundColor) ?? "#000000",
        HoldSeconds = Math.Clamp(HoldSeconds, 0, 600),
        Passes = Math.Clamp(Passes, 1, 100),
    };
}

/// <summary>
/// Renders a template, with its fields filled in, to an H.264 MP4 through the app's own encoder:
/// the motion backgrounds' writer (Broadcast/Media/RenderedVideoWriter.cs), the same way
/// Broadcast/Library/MotionBackgroundMaker.cs makes a background. Each frame is drawn by Skottie
/// at its exact moment (frame i at i ÷ frame rate), put over the chosen colour and turned into
/// NV12 (<see cref="Nv12Over"/>), while the encoder thread compresses the frame before: two
/// threads at once, passing a few reusable pictures between them, so memory stays flat.
/// <para>
/// What's in the file: a template with markers plays In, holds for
/// <see cref="MotionExportOptions.HoldSeconds"/>, then Out (a lower third for a vision mixer, over
/// key green); one without markers plays through <see cref="MotionExportOptions.Passes"/> times
/// (a background, which the player loops). It's written under a ".partial" name and only takes its
/// real name when it's complete; a cancelled or failed render leaves nothing behind.
/// </para>
/// </summary>
public static class LottieVideoMaker
{
    private const int Buffers = 3;

    /// <summary>Where renders are written until they're filed: the motion backgrounds' work folder, in the library's spool.</summary>
    public static string WorkFolder(string libraryRoot) => MotionBackgroundMaker.WorkFolder(libraryRoot);

    /// <summary>The frames (of the animation) the video shows, one per video frame.</summary>
    public static IReadOnlyList<double> Frames(MotionTimeline timeline, MotionExportOptions options) =>
        timeline.PlayThrough(options.HoldSeconds, options.FrameRate, options.Passes);

    /// <summary>
    /// Draws and encodes the video to <paramref name="path"/>. Blocks the calling thread (use a
    /// thread of your own). Progress uses the motion backgrounds' report.
    /// </summary>
    /// <exception cref="OperationCanceledException">Cancelled; nothing is left behind.</exception>
    /// <exception cref="IOException">Drawing, encoding or writing failed; the message says why. Nothing is left behind.</exception>
    public static void Render(LottieDocument document, MotionTimeline timeline, MotionExportOptions options, string path,
        ILottieFonts? fonts = null, IProgress<MotionRenderProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        options = options.Normalized();
        var frames = Frames(timeline, options);
        if (frames.Count == 0) throw new IOException("The template has no frames to render");
        var background = int.Parse(options.BackgroundColor.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        var partial = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileNameWithoutExtension(path) + ".partial.mp4");
        var (width, height) = (options.Width, options.Height);

        using var crew = new RenderCrew("Motion template export", Math.Clamp(Environment.ProcessorCount / 2, 1, 4));
        LottieRenderer renderer;
        try { renderer = new LottieRenderer(document, fonts); }
        catch (LottieFormatException e) { throw new IOException(e.Message, e); }
        using var _ = renderer;
        using var drawn = new LottieFrame(width, height);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var free = new BlockingCollection<byte[]>();
        for (var i = 0; i < Buffers; i++) free.Add(new byte[Nv12Over.FrameBytes(width, height)]);
        using var ready = new BlockingCollection<(byte[] Picture, int Frame)>(Buffers);
        RenderedVideoWriter writer;
        try
        {
            writer = new RenderedVideoWriter(partial, width, height, options.FrameRate, LoopTiming.Bitrate(width, height, options.FrameRate));
        }
        // Media Foundation's own failures (an internal exception type of the Broadcast project) and COM's.
        catch (Exception e) when (e is not (IOException or OutOfMemoryException))
        {
            throw new IOException($"Windows' H.264 encoder can't make a {width} × {height} video at {options.FrameRate} fps: {e.Message}", e);
        }

        var clock = Stopwatch.StartNew();
        Exception? encodeFailure = null;
        var encoding = new Thread(() =>
        {
            try
            {
                var reported = TimeSpan.Zero;
                foreach (var (picture, frame) in ready.GetConsumingEnumerable())
                {
                    writer.Write(picture, frame);
                    free.Add(picture);
                    if (clock.Elapsed - reported >= TimeSpan.FromMilliseconds(100) || frame == frames.Count - 1)
                    {
                        reported = clock.Elapsed;
                        progress?.Report(new MotionRenderProgress(frame + 1, frames.Count, clock.Elapsed));
                    }
                }
            }
            catch (Exception error)
            {
                encodeFailure = error;
                stop.Cancel();
            }
        }) { IsBackground = true, Name = "Motion template encoder", Priority = ThreadPriority.BelowNormal };
        encoding.Start();

        var completed = false;
        try
        {
            try
            {
                for (var i = 0; i < frames.Count; i++)
                {
                    var picture = free.Take(stop.Token);
                    renderer.Render(frames[i], drawn);
                    Nv12Over.Convert(drawn.Pixels, width, height, drawn.Stride, background, picture, crew);
                    ready.Add((picture, i), stop.Token);
                }
            }
            finally
            {
                ready.CompleteAdding();
                encoding.Join();
            }
            if (encodeFailure is not null) throw new IOException($"Encoding the template's video failed: {encodeFailure.Message}", encodeFailure);
            writer.Finish();
            BroadcastLog.Info("motion", $"rendered template \"{document.Name}\" {width}x{height}@{options.FrameRate} {frames.Count} frames in {clock.Elapsed.TotalSeconds:F1} s ({frames.Count / clock.Elapsed.TotalSeconds:F1} fps)");
            File.Move(partial, path, overwrite: true);
            completed = true;
        }
        catch (OperationCanceledException) when (encodeFailure is not null)
        {
            throw new IOException($"Encoding the template's video failed: {encodeFailure.Message}", encodeFailure);
        }
        catch (Exception e) when (e is not (IOException or OperationCanceledException or OutOfMemoryException))
        {
            // Finishing the file failed (a full disk, say): one kind of error for the app to show.
            throw new IOException($"Writing the template's video failed: {e.Message}", e);
        }
        finally
        {
            writer.Dispose();
            if (!completed) TryDelete(partial);
        }
    }

    /// <summary>
    /// Files a finished video into the media library (folder "Motion Templates"): as a looping
    /// background, or as a foreground video that plays once. With <paramref name="replaceId"/> that
    /// item gets the new file instead (same id, so everything using it shows the new one). The
    /// rendered file is moved in and then removed. Returns the item.
    /// </summary>
    /// <exception cref="IOException">The file couldn't be read as a video.</exception>
    public static MediaItem FileIntoLibrary(LibraryStore library, string path, string name, bool background, string? replaceId = null)
    {
        try
        {
            if (replaceId is not null && library.Get<MediaItem>(replaceId) is { } existing)
            {
                if (MediaRelink.ReplaceFile(library, existing.Id, path) is null) throw new IOException("The rendered video can't be read");
                return library.Get<MediaItem>(existing.Id)!;
            }
            var probe = library.Prober?.Probe(path) ?? MediaImportProbe.ProbeVideo(path);
            var item = MediaImporting.AddMedia(library, path, name, background ? MediaClassification.Background : MediaClassification.Foreground, probe: probe)
                       ?? throw new IOException("The rendered video can't be read");
            item.Folder = MotionTemplateStore.Pack;
            item.Loops = background;
            library.Save(item);
            return item;
        }
        finally
        {
            TryDelete(path);
        }
    }

    /// <summary>A file name for a render: the name given, with the characters Windows forbids replaced.</summary>
    public static string FileName(string name)
    {
        var safe = name.Trim();
        foreach (var bad in Path.GetInvalidFileNameChars()) safe = safe.Replace(bad, '-');
        return (safe.Length == 0 ? "Motion template" : safe) + ".mp4";
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
