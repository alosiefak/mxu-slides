using System.Collections.Concurrent;
using System.Diagnostics;
using MxU.Slides.Broadcast.Media;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Motion;

namespace MxU.Slides.Broadcast.Library;

/// <summary>How far a motion background's render has got.</summary>
public sealed record MotionRenderProgress(int Frames, int FrameCount, TimeSpan Elapsed)
{
    public double Fraction => FrameCount == 0 ? 0 : (double)Frames / FrameCount;

    public double FramesPerSecond => Elapsed.TotalSeconds > 0 ? Frames / Elapsed.TotalSeconds : 0;

    /// <summary>A guess at the time left, once a few frames have shown how fast this PC is.</summary>
    public TimeSpan? Remaining => Frames >= 3 && FramesPerSecond > 0 ? TimeSpan.FromSeconds((FrameCount - Frames) / FramesPerSecond) : null;
}

/// <summary>
/// Makes a motion background: draws every frame of a <see cref="MotionRecipe"/>'s loop with
/// <see cref="MotionRenderer"/>, encodes it to an H.264 MP4 (<see cref="RenderedVideoWriter"/>),
/// and files it into the media library as an ordinary video: a background, set to loop, in the
/// "Motion Backgrounds" folder. Its recipe is kept in the library's Windows extras, keyed by the
/// item's id, so it can be opened again and re-rendered.
/// <para>
/// <b>Two threads at once.</b> While the encoder thread compresses frame i, the drawing crew is
/// already on frame i + 1. They pass frames through a short queue of reusable buffers (a 4K
/// frame is 12 MB, so they aren't made afresh each time), so neither waits for the other more
/// than it must and memory stays flat however long the loop is.
/// </para>
/// </summary>
public static class MotionBackgroundMaker
{
    /// <summary>The media library folder new motion backgrounds go in.</summary>
    public const string Folder = "Motion Backgrounds";

    private const int Buffers = 3;

    /// <summary>
    /// Where renders are written until they're finished: a folder inside the library's spool, on
    /// the same disk as the library. Not the spool itself, which the recorder sweeps for its own
    /// unfinished recordings at every launch.
    /// </summary>
    public static string WorkFolder(string libraryRoot)
    {
        var path = Path.Combine(RecordingLibrary.SpoolDirectory(libraryRoot), "motion");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Draws and encodes the whole loop to <paramref name="path"/>. Blocks the calling thread
    /// (a background one: a 4K60 loop takes minutes). The file is written under a ".partial"
    /// name and only takes its real name when it's complete, so a cancelled or crashed render
    /// never leaves a broken file that looks finished. <paramref name="width"/> and
    /// <paramref name="height"/> override the recipe's size (the tests and the self-test make tiny ones).
    /// </summary>
    /// <exception cref="OperationCanceledException">Cancelled; nothing is left behind.</exception>
    /// <exception cref="IOException">Encoding or writing failed; the message says why. Nothing is left behind.</exception>
    public static void Render(MotionRecipe recipe, string path, IProgress<MotionRenderProgress>? progress = null,
        CancellationToken cancellationToken = default, int? width = null, int? height = null)
    {
        recipe = recipe.Normalized();
        var partial = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileNameWithoutExtension(path) + ".partial.mp4");
        // Leave a core for the encoder and one for everything else; the crew's threads run below
        // normal priority, so a render during a service doesn't make the screens stutter.
        using var crew = new RenderCrew("Motion background", Math.Max(1, Environment.ProcessorCount - 2));
        var renderer = new MotionRenderer(recipe, width ?? recipe.Width, height ?? recipe.Height, crew);
        var frames = renderer.FrameCount;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var free = new BlockingCollection<byte[]>();
        for (var i = 0; i < Buffers; i++) free.Add(new byte[renderer.FrameBytes(MotionPixelFormat.Nv12)]);
        using var ready = new BlockingCollection<(byte[] Picture, int Frame)>(Buffers);
        RenderedVideoWriter writer;
        try
        {
            writer = new RenderedVideoWriter(partial, renderer.Width, renderer.Height, recipe.FrameRate,
                LoopTiming.Bitrate(renderer.Width, renderer.Height, recipe.FrameRate));
        }
        catch (Exception e) when (e is MediaFoundationException or System.Runtime.InteropServices.COMException)
        {
            throw new IOException($"Windows' H.264 encoder can't make a {renderer.Width} × {renderer.Height} video at {recipe.FrameRate} fps: {e.Message}", e);
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
                    // Every tenth of a second is plenty for a progress bar (a small loop does hundreds a second).
                    if (clock.Elapsed - reported >= TimeSpan.FromMilliseconds(100) || frame == frames - 1)
                    {
                        reported = clock.Elapsed;
                        progress?.Report(new MotionRenderProgress(frame + 1, frames, clock.Elapsed));
                    }
                }
            }
            catch (Exception error)
            {
                encodeFailure = error;
                stop.Cancel();
            }
        }) { IsBackground = true, Name = "Motion background encoder", Priority = ThreadPriority.BelowNormal };
        encoding.Start();

        var completed = false;
        try
        {
            try
            {
                for (var frame = 0; frame < frames; frame++)
                {
                    var picture = free.Take(stop.Token);
                    renderer.RenderFrame(frame, MotionPixelFormat.Nv12, picture);
                    ready.Add((picture, frame), stop.Token);
                }
            }
            finally
            {
                ready.CompleteAdding();
                encoding.Join();
            }
            if (encodeFailure is not null) throw new IOException($"Encoding the motion background failed: {encodeFailure.Message}", encodeFailure);
            writer.Finish();
            BroadcastLog.Info("motion", $"rendered {recipe.Style} {renderer.Width}x{renderer.Height}@{recipe.FrameRate} {frames} frames in {clock.Elapsed.TotalSeconds:F1} s ({frames / clock.Elapsed.TotalSeconds:F1} fps, {writer.EncoderName})");
            File.Move(partial, path, overwrite: true);
            completed = true;
        }
        catch (OperationCanceledException) when (encodeFailure is not null)
        {
            throw new IOException($"Encoding the motion background failed: {encodeFailure.Message}", encodeFailure);
        }
        catch (Exception e) when (e is MediaFoundationException or System.Runtime.InteropServices.COMException)
        {
            // Finishing the file failed (a full disk, say): one kind of error for the app to show.
            throw new IOException($"Writing the motion background failed: {e.Message}", e);
        }
        finally
        {
            writer.Dispose();
            if (!completed) TryDelete(partial);
        }
    }

    /// <summary>
    /// Files a finished render into the library and remembers its recipe. With
    /// <paramref name="replaceId"/> the existing item gets the new file instead (same id, so every
    /// slide and service using it shows the new version); otherwise a new item is made. The
    /// rendered file is moved or copied in and then removed. Returns the item.
    /// </summary>
    /// <exception cref="IOException">The file couldn't be read as a video.</exception>
    public static MediaItem FileIntoLibrary(LibraryStore library, string path, MotionRecipe recipe, string? replaceId = null)
    {
        recipe = recipe.Normalized();
        try
        {
            MediaItem? item;
            if (replaceId is not null && library.Get<MediaItem>(replaceId) is { } existing)
            {
                if (MediaRelink.ReplaceFile(library, existing.Id, path) is null) throw new IOException("The rendered video can't be read");
                item = library.Get<MediaItem>(existing.Id)!;
            }
            else
            {
                var probe = library.Prober?.Probe(path) ?? MediaImportProbe.ProbeVideo(path);
                item = MediaImporting.AddMedia(library, path, recipe.Name ?? recipe.SuggestedName(), MediaClassification.Background, probe: probe)
                    ?? throw new IOException("The rendered video can't be read");
                item.Folder = Folder;
                item.Loops = true;
                library.Save(item);
            }
            SaveRecipe(library, item, recipe with { Name = item.Name });
            return item;
        }
        finally
        {
            TryDelete(path);
        }
    }

    /// <summary>
    /// The recipe a motion background was made from, or null if it wasn't made here or its file
    /// has since been replaced by another (on a Mac, say), when the recipe no longer describes it.
    /// </summary>
    public static MotionRecipe? RecipeOf(LibraryStore library, MediaItem item) =>
        library.Extras.Get<MotionRecipe>(MotionRecipe.ExtraKind, item.Id, basis: item.FileHash);

    /// <summary>Keeps <paramref name="recipe"/> with <paramref name="item"/>, fingerprinted by the file it made.</summary>
    public static void SaveRecipe(LibraryStore library, MediaItem item, MotionRecipe recipe) =>
        library.Extras.Save(MotionRecipe.ExtraKind, item.Id, recipe, basis: item.FileHash);

    /// <summary>A file name for the render: the background's name, with the characters Windows forbids replaced.</summary>
    public static string FileName(MotionRecipe recipe)
    {
        var name = (recipe.Name ?? recipe.SuggestedName()).Trim();
        foreach (var bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '-');
        return (name.Length == 0 ? "Motion background" : name) + ".mp4";
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
