using MxU.Slides.Audio.Decoding;
using MxU.Slides.Broadcast.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Openers;
using MxU.Slides.MotionTemplates.Openers;
using MxU.Slides.Windows.Infrastructure;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// One opener being made: its recipe, how far it's got, and how to stop it. Like a motion
/// background's job, it carries on if the window that started it is closed (progress shows in the
/// notice bar), and a window opened meanwhile picks it up again.
/// </summary>
public sealed class OpenerJob
{
    private readonly CancellationTokenSource cancel = new();

    internal OpenerJob(OpenerRecipe recipe, MediaItem? replacing)
    {
        Recipe = recipe;
        Replacing = replacing;
    }

    public OpenerRecipe Recipe { get; }

    /// <summary>The item getting the new file, or null for a new item.</summary>
    public MediaItem? Replacing { get; }

    /// <summary>The latest progress (on the UI thread).</summary>
    public MotionRenderProgress? Latest { get; private set; }

    /// <summary>Progress, raised on the UI thread.</summary>
    public event Action<MotionRenderProgress>? Progressed;

    /// <summary>The item made (or re-made); null when it was cancelled or failed (then <see cref="Failure"/> says why). Never faults.</summary>
    public Task<MediaItem?> Done { get; internal set; } = Task.FromResult<MediaItem?>(null);

    /// <summary>Why it failed, or null.</summary>
    public string? Failure { get; internal set; }

    internal CancellationToken Token => cancel.Token;

    public void Cancel() => cancel.Cancel();

    internal void Report(MotionRenderProgress progress)
    {
        Latest = progress;
        Progressed?.Invoke(progress);
    }
}

/// <summary>
/// Making openers (Media › Make an opener…; Views/OpenerWindow.cs): bumpers, countdowns, montages
/// cut to music and announcement loops. The drawing and encoding are OpenerVideoMaker's, on
/// threads of their own below normal priority; this part reads the music, starts a job, reports it
/// in the notice bar and puts the result in the Media bin. One at a time, and not while a motion
/// background is being made: each keeps every core busy.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The opener being made right now, if any.</summary>
    public OpenerJob? OpenerJob { get; private set; }

    /// <summary>The recipe <paramref name="item"/> was made from, or null when it wasn't made here (or its file has been replaced since).</summary>
    public OpenerRecipe? OpenerRecipeOf(MediaItem item) => OpenerVideoMaker.RecipeOf(Library, item);

    /// <summary>
    /// True when something is on the screens: making an opener then shares the PC with a running
    /// show. It's drawn below normal priority so the show comes first, but the window says so.
    /// </summary>
    public bool ShowIsLive => !Show.IsEmpty;

    /// <summary>
    /// Reads a music file and finds its length and beats, off the UI thread (a five-minute song
    /// takes a second or two). Null when the file can't be read as sound.
    /// </summary>
    public Task<BeatAnalysis?> AnalyseMusicAsync(string path, CancellationToken cancellationToken = default) => OffUiThread(() => AnalyseMusic(path, cancellationToken));

    private static BeatAnalysis? AnalyseMusic(string path, CancellationToken cancellationToken)
    {
        try
        {
            // 22 kHz is plenty for finding beats, and half the work of 48.
            using var reader = MusicFileReader.Open(path, 22_050);
            var detector = new BeatDetector(reader.SampleRate);
            var stereo = new float[8192 * 2];
            var mono = new float[8192];
            // Only as much as a montage can use.
            var most = (long)(OpenerRecipe.MaxMontageSeconds + 5) * reader.SampleRate;
            long total = 0;
            while (total < most && !cancellationToken.IsCancellationRequested)
            {
                var frames = reader.Read(stereo);
                if (frames <= 0) break;
                for (var i = 0; i < frames; i++) mono[i] = (stereo[i * 2] + stereo[i * 2 + 1]) * 0.5f;
                detector.Add(mono.AsSpan(0, frames));
                total += frames;
            }
            return total == 0 ? null : detector.Finish();
        }
        catch (IOException ex)
        {
            Log.Warn($"Couldn't read the music “{Path.GetFileName(path)}”: {ex.Message}");
            return null;
        }
    }

    /// <summary>The audio engine's file reader, as the video maker takes a montage's music.</summary>
    private sealed class MusicSound(MusicFileReader reader) : IOpenerSound, IDisposable
    {
        public int SampleRate => reader.SampleRate;

        public int Read(Span<float> interleavedStereo) => reader.Read(interleavedStereo);

        public void Dispose() => reader.Dispose();
    }

    /// <summary>
    /// Starts making an opener from <paramref name="recipe"/>: a new item, or a new file for
    /// <paramref name="replacing"/> (same id, so everywhere it's used shows the new version).
    /// <paramref name="size"/> overrides the picture size (the self-test makes small ones). Returns
    /// the job; await its <see cref="OpenerJob.Done"/>. Null when an opener or a motion background
    /// is already being made.
    /// </summary>
    public OpenerJob? MakeOpener(OpenerRecipe recipe, MediaItem? replacing = null, (int Width, int Height)? size = null)
    {
        if (OpenerJob is not null || MotionJob is not null) return null;
        recipe = recipe.Normalized();
        var job = new OpenerJob(recipe, replacing);
        OpenerJob = job;
        job.Done = Run();
        return job;

        async Task<MediaItem?> Run()
        {
            var title = replacing?.Name ?? recipe.Name ?? recipe.SuggestedName();
            Notice = $"Making “{title}”…";
            var lastNotice = DateTime.MinValue;
            // Created here, on the UI thread, so its reports arrive here too.
            var progress = new Progress<MotionRenderProgress>(p =>
            {
                job.Report(p);
                if (DateTime.UtcNow - lastNotice < TimeSpan.FromSeconds(0.5)) return;
                lastNotice = DateTime.UtcNow;
                Notice = $"Making “{title}”: {p.Fraction:P0}" + (p.Remaining is { } left ? $", {Describe(left)} left" : "") + ".";
            });
            try
            {
                var path = Path.Combine(OpenerVideoMaker.WorkFolder(Library.Root), OpenerVideoMaker.FileName(recipe with { Name = title }));
                var fonts = MotionFonts;
                await OffUiThread(() =>
                {
                    // A montage's music is read twice: once now for its beats, then again as the video is written.
                    var music = recipe is { Kind: OpenerKind.Montage, Music: { } track } ? AnalyseMusic(track, job.Token) : null;
                    if (recipe is { Kind: OpenerKind.Montage, Music: { } missing } && music is null)
                        throw new IOException($"the music “{Path.GetFileName(missing)}” can't be read");
                    job.Token.ThrowIfCancellationRequested();
                    var plan = OpenerPlan.For(recipe, music?.Music);
                    using var sound = music is null ? null : new MusicSound(MusicFileReader.Open(recipe.Music!));
                    OpenerVideoMaker.Render(plan, path, fonts, sound, progress, job.Token, size?.Width, size?.Height);
                    return true;
                });
                var item = await OffUiThread(() => OpenerVideoMaker.FileIntoLibrary(Library, path, recipe, replacing?.Id));
                if (replacing is not null)
                {
                    MediaFilesChanged([item.Id]);
                    // Replacing asked for the new version everywhere, so a copy looping on the screens switches now.
                    Media.ReloadSilentVideos(item.Id);
                }
                else
                {
                    RefreshMedia();
                }
                SelectedMedia = MediaItems.FirstOrDefault(m => m.Item.Id == item.Id);
                Notice = replacing is not null
                    ? $"“{item.Name}” has been made again, everywhere it's used."
                    : $"Made “{item.Name}”: it's in the Media bin (folder “{OpenerRecipe.Folder}”)" + (recipe.Loops ? ", set to loop." : ".");
                return item;
            }
            catch (OperationCanceledException)
            {
                Notice = $"Stopped making “{title}”. Nothing was added.";
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or AggregateException)
            {
                Log.Error($"Making the opener “{title}” failed", ex);
                job.Failure = ex.Message;
                Notice = null;
                Problem = $"Couldn't make “{title}”: {ex.Message}";
                return null;
            }
            finally
            {
                OpenerJob = null;
            }
        }
    }
}
