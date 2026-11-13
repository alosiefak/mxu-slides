using System.Collections.Concurrent;
using System.Diagnostics;
using MxU.Slides.Broadcast;
using MxU.Slides.Broadcast.Library;
using MxU.Slides.Broadcast.Media;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Motion;
using MxU.Slides.Core.Openers;
using MxU.Slides.MotionTemplates.Export;
using MxU.Slides.MotionTemplates.Lottie;

namespace MxU.Slides.MotionTemplates.Openers;

/// <summary>
/// A montage's music, as the video maker reads it: stereo sound from the start, a piece at a
/// time. (The app gives it the audio engine's file reader; the tests give it a made-up click track.)
/// </summary>
public interface IOpenerSound
{
    int SampleRate { get; }

    /// <summary>Fills <paramref name="interleavedStereo"/> (left, right, left…) with the next sound; returns the frames written, 0 at the end.</summary>
    int Read(Span<float> interleavedStereo);
}

/// <summary>
/// Makes an opener's video (Phase 13): draws every frame of an <see cref="OpenerPlan"/> with
/// <see cref="OpenerRenderer"/> and writes an H.264 MP4 through the app's own encoder, exactly as
/// the motion backgrounds and motion templates do (Broadcast/Library/MotionBackgroundMaker.cs,
/// Export/LottieVideoMaker.cs): the same writer, the same ".partial" name until the file is
/// complete, the same two threads (drawing frame <i>i</i> + 1 while the encoder takes frame
/// <i>i</i>) passing a few reusable pictures, all below normal priority so a show isn't disturbed.
/// <para>
/// The one new thing is <b>sound</b>, for a montage: each frame carries the stretch of music that
/// plays during it (faded out at the end by the plan), and the writer packs it beside the pictures.
/// </para>
/// </summary>
public static class OpenerVideoMaker
{
    private const int Buffers = 3;

    /// <summary>Where openers are written until they're filed: the motion backgrounds' work folder, in the library's spool.</summary>
    public static string WorkFolder(string libraryRoot) => MotionBackgroundMaker.WorkFolder(libraryRoot);

    /// <summary>
    /// Draws and encodes the opener to <paramref name="path"/>. Blocks the calling thread (use a
    /// thread of your own: a ten-minute countdown takes about as long to make as to play).
    /// <paramref name="width"/> and <paramref name="height"/> override the recipe's size (the
    /// tests and the self-test make small ones).
    /// </summary>
    /// <exception cref="OperationCanceledException">Cancelled; nothing is left behind.</exception>
    /// <exception cref="IOException">Drawing, encoding or writing failed; the message says why. Nothing is left behind.</exception>
    public static void Render(OpenerPlan plan, string path, ILottieFonts? fonts = null, IOpenerSound? sound = null,
        IProgress<MotionRenderProgress>? progress = null, CancellationToken cancellationToken = default, int? width = null, int? height = null)
    {
        var recipe = plan.Recipe;
        var (w, h) = (Math.Clamp((width ?? recipe.Width) & ~1, 16, 3840), Math.Clamp((height ?? recipe.Height) & ~1, 16, 2160));
        var frames = plan.FrameCount;
        var rate = recipe.FrameRate;
        var partial = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileNameWithoutExtension(path) + ".partial.mp4");

        // Leave a core for the encoder and one for everything else, as the motion backgrounds do.
        using var crew = new RenderCrew("Opener", Math.Max(1, Environment.ProcessorCount - 2));
        using var renderer = new OpenerRenderer(plan, w, h, crew, fonts);
        using var drawn = new LottieFrame(w, h);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var free = new BlockingCollection<byte[]>();
        for (var i = 0; i < Buffers; i++) free.Add(new byte[Nv12Over.FrameBytes(w, h)]);
        using var ready = new BlockingCollection<(byte[] Picture, int Frame, float[]? Sound)>(Buffers);
        RenderedVideoWriter writer;
        try
        {
            writer = new RenderedVideoWriter(partial, w, h, rate, LoopTiming.Bitrate(w, h, rate), sound?.SampleRate);
        }
        // Media Foundation's own failures (an internal exception type of the Broadcast project) and COM's.
        catch (Exception e) when (e is not (IOException or OutOfMemoryException))
        {
            throw new IOException($"Windows' encoders can't make a {w} × {h} video at {rate} fps: {e.Message}", e);
        }

        var clock = Stopwatch.StartNew();
        Exception? encodeFailure = null;
        var encoding = new Thread(() =>
        {
            try
            {
                var reported = TimeSpan.Zero;
                foreach (var (picture, frame, music) in ready.GetConsumingEnumerable())
                {
                    writer.Write(picture, frame);
                    if (music is not null) writer.WriteAudio(music);
                    free.Add(picture);
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
        }) { IsBackground = true, Name = "Opener encoder", Priority = ThreadPriority.BelowNormal };
        encoding.Start();

        var completed = false;
        long soundFrames = 0;
        try
        {
            try
            {
                for (var i = 0; i < frames; i++)
                {
                    var picture = free.Take(stop.Token);
                    renderer.Render(i, drawn);
                    Nv12Over.Convert(drawn.Pixels, w, h, drawn.Stride, 0x000000, picture, crew);
                    ready.Add((picture, i, sound is null ? null : SoundFor(plan, sound, i, ref soundFrames)), stop.Token);
                }
            }
            finally
            {
                ready.CompleteAdding();
                encoding.Join();
            }
            if (encodeFailure is not null) throw new IOException($"Encoding the opener failed: {encodeFailure.Message}", encodeFailure);
            writer.Finish();
            BroadcastLog.Info("opener", $"rendered {recipe.Kind} {w}x{h}@{rate} {frames} frames in {clock.Elapsed.TotalSeconds:F1} s ({frames / clock.Elapsed.TotalSeconds:F1} fps{(sound is null ? "" : ", with sound")})");
            File.Move(partial, path, overwrite: true);
            completed = true;
        }
        catch (OperationCanceledException) when (encodeFailure is not null)
        {
            throw new IOException($"Encoding the opener failed: {encodeFailure.Message}", encodeFailure);
        }
        catch (Exception e) when (e is not (IOException or OperationCanceledException or OutOfMemoryException))
        {
            throw new IOException($"Making the opener failed: {e.Message}", e);
        }
        finally
        {
            writer.Dispose();
            if (!completed) TryDelete(partial);
        }
    }

    /// <summary>
    /// The music that plays during frame <paramref name="frame"/>: from the sample the last frame
    /// stopped at up to this frame's end (worked out with whole numbers, so nothing drifts over a
    /// long montage), at the plan's loudness for that moment. Silence once the music has run out.
    /// </summary>
    private static float[] SoundFor(OpenerPlan plan, IOpenerSound sound, int frame, ref long soundFrames)
    {
        var upTo = (frame + 1L) * sound.SampleRate / plan.Recipe.FrameRate;
        var count = (int)(upTo - soundFrames);
        var samples = new float[count * 2];
        var got = 0;
        while (got < count)
        {
            var read = sound.Read(samples.AsSpan(got * 2));
            if (read <= 0) break;
            got += read;
        }
        for (var i = 0; i < got; i++)
        {
            var gain = (float)plan.MusicGain((soundFrames + i) / (double)sound.SampleRate);
            if (gain >= 1) continue;
            samples[i * 2] *= gain;
            samples[i * 2 + 1] *= gain;
        }
        soundFrames = upTo;
        return samples;
    }

    /// <summary>
    /// Files a finished opener into the media library (folder "Openers") as the kind the bin
    /// already has for it: an announcement loop as a looping background, the others as videos that
    /// play once. Its recipe is kept in the library's Windows extras, fingerprinted by the file, so
    /// the window can open it again. With <paramref name="replaceId"/> that item gets the new file
    /// instead (same id, so everything using it shows the new one). Returns the item.
    /// </summary>
    /// <exception cref="IOException">The file couldn't be read as a video.</exception>
    public static MediaItem FileIntoLibrary(LibraryStore library, string path, OpenerRecipe recipe, string? replaceId = null)
    {
        recipe = recipe.Normalized();
        try
        {
            MediaItem item;
            if (replaceId is not null && library.Get<MediaItem>(replaceId) is { } existing)
            {
                if (MediaRelink.ReplaceFile(library, existing.Id, path) is null) throw new IOException("The opener's video can't be read");
                item = library.Get<MediaItem>(existing.Id)!;
            }
            else
            {
                var probe = library.Prober?.Probe(path) ?? MediaImportProbe.ProbeVideo(path);
                item = MediaImporting.AddMedia(library, path, recipe.Name ?? recipe.SuggestedName(), recipe.Classification, probe: probe)
                       ?? throw new IOException("The opener's video can't be read");
                item.Folder = OpenerRecipe.Folder;
                item.Loops = recipe.Loops;
                library.Save(item);
            }
            library.Extras.Save(OpenerRecipe.ExtraKind, item.Id, recipe with { Name = item.Name }, basis: item.FileHash);
            return item;
        }
        finally
        {
            TryDelete(path);
        }
    }

    /// <summary>
    /// The recipe an opener was made from, or null if it wasn't made here or its file has since
    /// been replaced by another (on a Mac, say), when the recipe no longer describes it.
    /// </summary>
    public static OpenerRecipe? RecipeOf(LibraryStore library, MediaItem item) =>
        library.Extras.Get<OpenerRecipe>(OpenerRecipe.ExtraKind, item.Id, basis: item.FileHash);

    /// <summary>A file name for the render: the opener's name, with the characters Windows forbids replaced.</summary>
    public static string FileName(OpenerRecipe recipe) => LottieVideoMaker.FileName(recipe.Name ?? recipe.SuggestedName());

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
