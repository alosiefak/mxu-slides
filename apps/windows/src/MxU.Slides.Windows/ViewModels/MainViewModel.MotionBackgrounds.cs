using MxU.Slides.Broadcast.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Motion;
using MxU.Slides.Windows.Infrastructure;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// One motion background being made: its recipe, how far it's got, and how to stop it. It
/// carries on if the window that started it is closed (progress shows in the notice bar), and
/// a window opened meanwhile picks it up again.
/// </summary>
public sealed class MotionBackgroundJob
{
    private readonly CancellationTokenSource cancel = new();

    internal MotionBackgroundJob(MotionRecipe recipe, MediaItem? replacing)
    {
        Recipe = recipe;
        Replacing = replacing;
    }

    public MotionRecipe Recipe { get; }

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

    public bool IsCancelled => cancel.IsCancellationRequested;

    internal CancellationToken Token => cancel.Token;

    public void Cancel() => cancel.Cancel();

    internal void Report(MotionRenderProgress progress)
    {
        Latest = progress;
        Progressed?.Invoke(progress);
    }
}

/// <summary>
/// Making motion backgrounds (Media › Motion background…; Views/MotionBackgroundWindow.cs). The
/// drawing and encoding are MotionBackgroundMaker's, on threads of their own; this part starts a
/// job, reports it in the notice bar and puts the result in the Media bin. One at a time: a
/// render keeps every core busy.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The motion background being made right now, if any.</summary>
    public MotionBackgroundJob? MotionJob { get; private set; }

    /// <summary>The recipe <paramref name="item"/> was made from, or null when it wasn't made here (or its file has been replaced since).</summary>
    public MotionRecipe? MotionRecipeOf(MediaItem item) => MotionBackgroundMaker.RecipeOf(Library, item);

    /// <summary>
    /// Starts making a motion background from <paramref name="recipe"/>: a new item, or a new file
    /// for <paramref name="replacing"/> (same id, so everywhere it's used shows the new version).
    /// <paramref name="size"/> overrides the picture size (the self-test makes tiny ones). Returns
    /// the job; await its <see cref="MotionBackgroundJob.Done"/>. Null when one is already running.
    /// </summary>
    public MotionBackgroundJob? MakeMotionBackground(MotionRecipe recipe, MediaItem? replacing = null, (int Width, int Height)? size = null)
    {
        if (MotionJob is not null) return null;
        recipe = recipe.Normalized();
        var job = new MotionBackgroundJob(recipe, replacing);
        MotionJob = job;
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
                var path = Path.Combine(MotionBackgroundMaker.WorkFolder(Library.Root), MotionBackgroundMaker.FileName(recipe));
                await OffUiThread(() =>
                {
                    MotionBackgroundMaker.Render(recipe, path, progress, job.Token, size?.Width, size?.Height);
                    return true;
                });
                var item = await OffUiThread(() => MotionBackgroundMaker.FileIntoLibrary(Library, path, recipe, replacing?.Id));
                if (replacing is not null)
                {
                    MediaFilesChanged([item.Id]);
                    // Replacing asked for the new look everywhere, so a copy playing on the screens switches now.
                    Media.ReloadSilentVideos(item.Id);
                }
                else
                {
                    RefreshMedia();
                }
                SelectedMedia = MediaItems.FirstOrDefault(m => m.Item.Id == item.Id);
                Notice = replacing is not null
                    ? $"“{item.Name}” has its new look, everywhere it's used."
                    : $"Made “{item.Name}”: it's in the Media bin (folder “{MotionBackgroundMaker.Folder}”), set to loop.";
                return item;
            }
            catch (OperationCanceledException)
            {
                Notice = $"Stopped making “{title}”. Nothing was added.";
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or AggregateException)
            {
                Log.Error($"Making the motion background “{title}” failed", ex);
                job.Failure = ex.Message;
                Notice = null;
                Problem = $"Couldn't make “{title}”: {ex.Message}";
                return null;
            }
            finally
            {
                MotionJob = null;
            }
        }
    }

    /// <summary>"about 2 minutes", "40 seconds".</summary>
    internal static string Describe(TimeSpan time) => time.TotalSeconds switch
    {
        < 55 => $"{Math.Max(1, (int)Math.Round(time.TotalSeconds / 5) * 5)} seconds",
        < 90 => "about a minute",
        _ => $"about {(int)Math.Round(time.TotalMinutes)} minutes",
    };
}
