using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MxU.Slides.Broadcast.Library;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.MotionTemplates;
using MxU.Slides.Core.Scene;
using MxU.Slides.MotionTemplates.AfterEffects;
using MxU.Slides.MotionTemplates.Export;
using MxU.Slides.MotionTemplates.Lottie;
using MxU.Slides.Windows.Infrastructure;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// One long motion-template job: a video being rendered from a template, or by After Effects.
/// It carries on if the window that started it closes (progress shows in the notice bar).
/// </summary>
public sealed class MotionTemplateJob
{
    private readonly CancellationTokenSource cancel = new();

    internal MotionTemplateJob(string title) => Title = title;

    public string Title { get; }

    /// <summary>How far it has got, 0 to 1 (null while that isn't known yet), and a line saying so. Raised on the UI thread.</summary>
    public double? Fraction { get; private set; }

    public string Status { get; private set; } = "Starting…";

    public event Action? Progressed;

    /// <summary>
    /// The library item made; null when nothing was added to the library (a video saved only as a
    /// file), or when it was cancelled or failed (<see cref="IsCancelled"/>, <see cref="Failure"/>). Never faults.
    /// </summary>
    public Task<MediaItem?> Done { get; internal set; } = Task.FromResult<MediaItem?>(null);

    public string? Failure { get; internal set; }

    /// <summary>It finished and did what it was asked.</summary>
    public bool Succeeded { get; internal set; }

    public bool IsCancelled => cancel.IsCancellationRequested;

    internal CancellationToken Token => cancel.Token;

    public void Cancel() => cancel.Cancel();

    internal void Report(double? fraction, string status)
    {
        Fraction = fraction;
        Status = status;
        Progressed?.Invoke();
    }
}

/// <summary>
/// Motion templates in the show (Phase 11): Lottie animations made in After Effects or
/// downloaded, played live with their transparency, their words and colours filled in per use.
/// <list type="bullet">
/// <item><b>The library</b> of templates and each use's values are Windows extras
/// (Core/MotionTemplates/MotionTemplateStore.cs), beside the ordinary documents a Mac shows
/// instead: a still overlay, or a rendered video.</item>
/// <item><b>Live.</b> A lower third goes up through <see cref="ForShow"/>: the program gets a copy of
/// the overlay whose picture is the live animation, drawn by <see cref="MotionPlayers"/> on its
/// own thread (Rendering/MediaHub.Motion.cs). Taking it down (any way: its tile, F4, Clear All, an
/// action) plays its Out: <see cref="SyncMotion"/> sees the overlay leaving and tells the
/// animation. A full-screen template's video plays as its animation instead whenever it's live.</item>
/// <item><b>Editing</b> a use's fields updates the animation on the screen at once; its still (or
/// video) for the Mac is made again a moment after the typing stops.</item>
/// </list>
/// </summary>
public sealed partial class MainViewModel
{
    public const string OverlayPicturePrefix = MotionTemplateStore.PicturePrefix + "overlay:";
    public const string BackgroundPicturePrefix = MotionTemplateStore.PicturePrefix + "bg:";

    /// <summary>Where a template's still for the Mac goes in the media library.</summary>
    public const string MotionStillsFolder = MotionTemplateStore.Pack + "/Stills";

    private MotionTemplateStore? motionStore;
    private FolderFonts? motionFonts;

    public MotionTemplateStore MotionStore => motionStore ??= new MotionTemplateStore(Library);

    /// <summary>Fonts for templates that use one Windows hasn't got: the library's fonts folder (which holds the fonts the app ships).</summary>
    public ILottieFonts MotionFonts => motionFonts ??= FolderFonts.OfLibrary(Library.Root);

    /// <summary>The templates playing on the program.</summary>
    public MotionPlayers MotionPlayers { get; private set; } = null!;

    /// <summary>The template job (a video render) going on now, if any. One at a time: a render keeps the PC busy.</summary>
    public MotionTemplateJob? MotionTemplateJob { get; private set; }

    /// <summary>Raised (on the UI thread) when the catalogue changes: an import, a removal, the starters arriving.</summary>
    public event Action? MotionTemplatesChanged;

    private readonly Dictionary<string, MotionTemplateUse> pendingUses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DispatcherTimer> useSaveTimers = new(StringComparer.Ordinal);
    private readonly HashSet<string> notMotion = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Hash, string Stamp), LottieDocument> filledIn = [];

    private void StartMotionTemplates()
    {
        MotionPlayers = new MotionPlayers(MotionFonts);
        Media.Motions = MotionPlayers;
        var dispatcher = Dispatcher.CurrentDispatcher;
        MotionPlayers.Finished += id => dispatcher.BeginInvoke(() => OnMotionFinished(id));
        MotionPlayers.Failed += (id, why) => dispatcher.BeginInvoke(() =>
        {
            Log.Warn($"Motion template {id} stopped drawing: {why}");
            Problem = $"A motion template couldn't be drawn ({why}). It shows as its still picture instead.";
        });
        Show.Changed += SyncMotion;
        // Which media items are templates is remembered (SyncMotion asks on every change); a change to the extras forgets it.
        Library.Extras.Changed += (kind, owner) =>
        {
            if (kind == MotionTemplateStore.UseKind) dispatcher.BeginInvoke(() => notMotion.Remove(owner));
            if (kind == MotionTemplateStore.CatalogueKind) dispatcher.BeginInvoke(() => MotionTemplatesChanged?.Invoke());
        };
        Library.Changed += (type, id) =>
        {
            if (type == typeof(MediaItem)) dispatcher.BeginInvoke(() => notMotion.Remove(id));
        };
        // The starter templates are written into the library off the UI thread, the first time.
        _ = OffUiThread(() =>
        {
            try { StarterMotionTemplates.Seed(MotionStore); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn($"Couldn't add the starter motion templates: {ex.Message}"); }
            return true;
        });
    }

    // MARK: The catalogue

    /// <summary>
    /// Imports Lottie files (.json, .lottie) into the catalogue, off the UI thread. Each result is
    /// the template made, or why the file couldn't be used.
    /// </summary>
    public async Task<IReadOnlyList<(string File, MotionTemplateInfo? Template, string? Problem)>> ImportMotionTemplatesAsync(IEnumerable<string> paths)
    {
        var files = paths.ToList();
        var results = await OffUiThread(() => files.Select(path =>
        {
            try
            {
                var (document, name) = LottieFiles.Read(path);
                var use = LottieStills.SuggestUse(document, MotionFonts);
                return (path, (MotionTemplateInfo?)MotionStore.Add(document, name, Path.GetFileName(path), suggestedUse: use), (string?)null);
            }
            catch (Exception ex) when (ex is LottieFormatException or IOException or UnauthorizedAccessException)
            {
                return (path, null, ex.Message);
            }
        }).ToList());
        var added = results.Count(r => r.Item2 is not null);
        var failed = results.Where(r => r.Item3 is not null).ToList();
        if (failed.Count > 0) Problem = $"Couldn't import {Path.GetFileName(failed[0].path)}: {failed[0].Item3}";
        else if (added > 0) Notice = added == 1 ? $"Imported the motion template “{results[0].Item2!.Name}”." : $"Imported {added} motion templates.";
        MotionTemplatesChanged?.Invoke();
        return [.. results.Select(r => (r.path, r.Item2, r.Item3))];
    }

    public void RemoveMotionTemplate(string id)
    {
        MotionStore.Remove(id);
        MotionTemplatesChanged?.Invoke();
    }

    // MARK: Filling in

    /// <summary>A template's animation with <paramref name="values"/> put in (pictures from the media library). Remembered, as it's asked for on every keystroke.</summary>
    public LottieDocument FilledIn(string lottieHash, LottieDocument lottie, IReadOnlyDictionary<string, string> values)
    {
        var stamp = WindowsExtras.Fingerprint(values.OrderBy(v => v.Key, StringComparer.Ordinal).ToList());
        if (filledIn.TryGetValue((lottieHash, stamp), out var known)) return known;
        var document = MotionFields.Apply(lottie, values, ImageUri);
        if (filledIn.Count > 32) filledIn.Clear();
        filledIn[(lottieHash, stamp)] = document;
        return document;
    }

    /// <summary>A picture field's value is a media library item: its file, as a data URI the animation can hold.</summary>
    private string? ImageUri(string value)
    {
        if (Library.Get<MediaItem>(value) is not { MediaKind: MediaKind.Image } item || Library.BlobPath(item) is not { } path) return null;
        try { return LottieFiles.DataUri(File.ReadAllBytes(path), item.FileName); }
        catch (IOException) { return null; }
    }

    // MARK: Uses

    /// <summary>The template an overlay plays (with any values still being typed), or null.</summary>
    public MotionTemplateUse? MotionUseOf(Overlay overlay) =>
        pendingUses.TryGetValue(overlay.Id, out var pending) ? pending : MotionStore.UseOf(overlay);

    /// <summary>The template a full-screen video plays as (with any values still being typed), or null.</summary>
    public MotionTemplateUse? MotionUseOf(MediaItem item) =>
        pendingUses.TryGetValue(item.Id, out var pending) ? pending : MotionStore.UseOf(item);

    /// <summary>
    /// Adds a lower-third use of <paramref name="template"/> to the library: its still for the Mac
    /// (the settled design as a PNG with alpha) and the overlay showing it, in the "Motion
    /// Templates" pack. The work is done off the UI thread. Null (with a problem shown) on failure.
    /// </summary>
    public async Task<Overlay?> AddMotionOverlayAsync(MotionTemplateInfo template, Dictionary<string, string> values, MotionPlayback playback, string name)
    {
        try
        {
            var overlay = await OffUiThread(() =>
            {
                var lottie = MotionStore.Lottie(template.LottieHash) ?? throw new IOException("The template's animation isn't in the library any more");
                var use = new MotionTemplateUse
                {
                    TemplateId = template.Id, LottieHash = template.LottieHash, Use = MotionUse.Overlay, Values = new(values), Playback = playback,
                    FallbackMediaId = "",
                };
                var timeline = MotionTimeline.For(lottie, playback);
                var still = AddStill(MotionFields.Apply(lottie, values, ImageUri), timeline, name);
                var overlay = MotionTemplateStore.FallbackOverlay(name, still.Id, timeline, MotionTemplateStore.Fit(lottie.Width, lottie.Height, SceneSize.Hd));
                Library.Save(overlay);
                use = use with { FallbackMediaId = still.Id };
                MotionStore.SaveUse(overlay, use with { FallbackStamp = MotionTemplateStore.FallbackStamp(use) });
                return overlay;
            });
            SelectedOverlayPack = MotionTemplateStore.Pack;
            RefreshOverlays();
            SelectedOverlay = Overlays.FirstOrDefault(o => o.Overlay.Id == overlay.Id);
            Notice = $"Added “{overlay.Name}” to the Overlays bin (pack “{MotionTemplateStore.Pack}”). It animates on Windows; a Mac shows its still.";
            return overlay;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or LottieFormatException)
        {
            Log.Error($"Adding the motion template “{name}” failed", ex);
            Problem = $"Couldn't add “{name}”: {ex.Message}";
            return null;
        }
    }

    /// <summary>The settled design as a PNG with alpha, filed in the media library: what a Mac shows. Off the UI thread.</summary>
    private MediaItem AddStill(LottieDocument filled, MotionTimeline timeline, string name)
    {
        var file = StillFile(filled, timeline);
        try
        {
            var still = MediaImporting.AddMedia(Library, file, $"{name} (still)", MediaClassification.Foreground)
                        ?? throw new IOException("The still picture couldn't be made");
            still.Folder = MotionStillsFolder;
            Library.Save(still);
            return still;
        }
        finally
        {
            TryDelete(file);
        }
    }

    private string StillFile(LottieDocument filled, MotionTimeline timeline)
    {
        var (w, h) = LottieRenderer.FittedSize(filled, 1920, 1080);
        var file = Path.Combine(LottieVideoMaker.WorkFolder(Library.Root), $"still-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(file, LottieStills.Png(filled, timeline.SettledFrame, w, h, MotionFonts));
        return file;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Adds a full-screen use of <paramref name="template"/>: renders its video (what a Mac plays)
    /// and files it into the Media bin as a looping background. Returns the job (null when another
    /// is running); await its <see cref="MotionTemplateJob.Done"/>.
    /// </summary>
    public MotionTemplateJob? AddMotionBackground(MotionTemplateInfo template, Dictionary<string, string> values, MotionPlayback playback, string name,
        string backgroundColor = "#000000", (int Width, int Height)? size = null)
    {
        return RunMotionJob($"Making “{name}”", async job =>
        {
            var item = await OffUiThread<MediaItem>(() =>
            {
                var lottie = MotionStore.Lottie(template.LottieHash) ?? throw new IOException("The template's animation isn't in the library any more");
                var use = new MotionTemplateUse
                {
                    TemplateId = template.Id, LottieHash = template.LottieHash, Use = MotionUse.Background, Values = new(values), Playback = playback,
                    FallbackMediaId = "", BackgroundColor = MotionFields.NormalHex(backgroundColor) ?? "#000000",
                };
                var file = RenderBackgroundVideo(lottie, use, name, job, size);
                var item = LottieVideoMaker.FileIntoLibrary(Library, file, name, background: true);
                use = use with { FallbackMediaId = item.Id };
                MotionStore.SaveUse(item, use with { FallbackStamp = MotionTemplateStore.FallbackStamp(use) });
                return item;
            });
            RefreshMedia();
            SelectedMedia = MediaItems.FirstOrDefault(m => m.Item.Id == item.Id);
            Notice = $"Added “{item.Name}” to the Media bin as a background. It plays live on Windows; a Mac plays its video.";
            return item;
        });
    }

    /// <summary>A background's video: one pass of its loop (no markers) or In, a 10 s hold and Out, over its colour. Off the UI thread.</summary>
    private string RenderBackgroundVideo(LottieDocument lottie, MotionTemplateUse use, string name, MotionTemplateJob job, (int Width, int Height)? size = null)
    {
        var filled = MotionFields.Apply(lottie, use.Values, ImageUri);
        var timeline = MotionTimeline.For(lottie, use.Playback);
        var (w, h) = size ?? LottieRenderer.FittedSize(lottie, 1920, 1080);
        var path = Path.Combine(LottieVideoMaker.WorkFolder(Library.Root), LottieVideoMaker.FileName(name));
        var progress = new Progress<MotionRenderProgress>(p => job.Report(p.Fraction, $"{job.Title}: {p.Fraction:P0}"));
        LottieVideoMaker.Render(filled, timeline, new MotionExportOptions(w & ~1, h & ~1, 30, use.BackgroundColor, HoldSeconds: 10), path, MotionFonts, progress, job.Token);
        return path;
    }

    /// <summary>
    /// A use's values or playback changed (in the bin, or the template window). The animation on the
    /// screen changes at once; the change is saved, and the Mac's still made again, a moment after
    /// the changes stop.
    /// </summary>
    public void ChangeMotionUse(Overlay overlay, MotionTemplateUse use) => ChangeUse(overlay.Id, use, OverlayPicturePrefix + overlay.Id);

    public void ChangeMotionUse(MediaItem item, MotionTemplateUse use) => ChangeUse(item.Id, use, BackgroundPicturePrefix + item.Id);

    private void ChangeUse(string ownerId, MotionTemplateUse use, string pictureId)
    {
        var before = pendingUses.TryGetValue(ownerId, out var pending) ? pending : null;
        pendingUses[ownerId] = use;
        if (MotionPlayers[pictureId] is { } live && MotionStore.Lottie(use.LottieHash) is { } lottie)
        {
            MotionPlayers.Update(pictureId, FilledIn(use.LottieHash, lottie, use.Values));
            if (live.Timeline.Playback != use.Playback) MotionPlayers.Update(pictureId, MotionTimeline.For(lottie, use.Playback));
        }
        if (before is null || !before.Values.SequenceEqual(use.Values) || before.Playback != use.Playback) MotionUseChanged?.Invoke(ownerId);
        if (!useSaveTimers.TryGetValue(ownerId, out var timer))
        {
            timer = new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.Background, (_, _) => _ = SaveUseAsync(ownerId), Dispatcher.CurrentDispatcher);
            useSaveTimers[ownerId] = timer;
        }
        timer.Stop();
        timer.Start();
    }

    /// <summary>Raised when a use's values change (the owner's id), so an open window can follow.</summary>
    public event Action<string>? MotionUseChanged;

    /// <summary>Saves any use changes still waiting for their pause (closing a window, quitting).</summary>
    public async Task FlushMotionUsesAsync()
    {
        foreach (var owner in pendingUses.Keys.ToList()) await SaveUseAsync(owner);
    }

    /// <summary>Saves a use's new values and makes its fallback (still or video) again, off the UI thread.</summary>
    private async Task SaveUseAsync(string ownerId)
    {
        if (useSaveTimers.Remove(ownerId, out var timer)) timer.Stop();
        if (!pendingUses.TryGetValue(ownerId, out var use)) return;
        try
        {
            if (Library.Get<Overlay>(ownerId) is { } overlay)
            {
                await OffUiThread(() =>
                {
                    var lottie = MotionStore.Lottie(use.LottieHash) ?? throw new IOException("The template's animation isn't in the library any more");
                    var saved = use;
                    if (MotionTemplateStore.FallbackIsStale(use) && Library.Get<MediaItem>(use.FallbackMediaId) is not null)
                    {
                        // A new still for the Mac, in the same media item (so the overlay itself doesn't change).
                        var file = StillFile(MotionFields.Apply(lottie, use.Values, ImageUri), MotionTimeline.For(lottie, use.Playback));
                        try { MediaRelink.ReplaceFile(Library, use.FallbackMediaId, file); }
                        finally { TryDelete(file); }
                        saved = use with { FallbackStamp = MotionTemplateStore.FallbackStamp(use) };
                    }
                    // Fingerprinted by the overlay as it is now, with its new still.
                    MotionStore.SaveUse(overlay, saved);
                    return true;
                });
                if (pendingUses.TryGetValue(ownerId, out var latest) && ReferenceEquals(latest, use)) pendingUses.Remove(ownerId);
                foreach (var tile in Overlays.Where(t => t.Overlay.Id == ownerId)) tile.Preview = OverlayTileViewModel.PreviewScene(tile.Overlay);
                Media.Invalidate(use.FallbackMediaId);
            }
            else if (Library.Get<MediaItem>(ownerId) is { } item)
            {
                if (!MotionTemplateStore.FallbackIsStale(use))
                {
                    MotionStore.SaveUse(item, use);
                    pendingUses.Remove(ownerId);
                    return;
                }
                // A background's video takes a while: it's a job, with progress in the notice bar.
                var job = RunMotionJob($"Updating “{item.Name}”", async job =>
                {
                    var remade = await OffUiThread<MediaItem>(() =>
                    {
                        var lottie = MotionStore.Lottie(use.LottieHash) ?? throw new IOException("The template's animation isn't in the library any more");
                        var file = RenderBackgroundVideo(lottie, use, item.Name, job);
                        var replaced = LottieVideoMaker.FileIntoLibrary(Library, file, item.Name, background: true, replaceId: item.Id);
                        MotionStore.SaveUse(replaced, use with { FallbackStamp = MotionTemplateStore.FallbackStamp(use) });
                        return replaced;
                    });
                    if (pendingUses.TryGetValue(ownerId, out var latest) && ReferenceEquals(latest, use)) pendingUses.Remove(ownerId);
                    MediaFilesChanged([remade.Id]);
                    return remade;
                });
                // Another job is running: try again when it's done.
                if (job is null) ChangeUse(ownerId, use, BackgroundPicturePrefix + ownerId);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or LottieFormatException)
        {
            Log.Error("Saving a motion template's values failed", ex);
            Problem = $"Couldn't save the template's new values: {ex.Message}";
        }
    }

    /// <summary>Runs a template job (one at a time), reporting it in the notice bar. Null when another is running.</summary>
    private MotionTemplateJob? RunMotionJob(string title, Func<MotionTemplateJob, Task<MediaItem?>> work)
    {
        if (MotionTemplateJob is not null) return null;
        var job = new MotionTemplateJob(title);
        MotionTemplateJob = job;
        var lastNotice = DateTime.MinValue;
        job.Progressed += () =>
        {
            if (DateTime.UtcNow - lastNotice < TimeSpan.FromSeconds(0.5)) return;
            lastNotice = DateTime.UtcNow;
            Notice = job.Status;
        };
        job.Done = Run();
        return job;

        async Task<MediaItem?> Run()
        {
            Notice = $"{title}…";
            try
            {
                var made = await work(job);
                job.Succeeded = true;
                return made;
            }
            catch (OperationCanceledException)
            {
                Notice = $"Stopped. Nothing was added.";
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or LottieFormatException)
            {
                Log.Error($"{title} failed", ex);
                job.Failure = ex.Message;
                Notice = null;
                Problem = $"{title} failed: {ex.Message}";
                return null;
            }
            finally
            {
                MotionTemplateJob = null;
            }
        }
    }

    // MARK: Live

    /// <summary>
    /// What to put on the program for <paramref name="overlay"/>: itself, or, for a motion template,
    /// its live copy (MotionTemplateStore.LiveCopy). With <paramref name="upAt"/> the animation
    /// starts (again) from its In at that moment; without, the one already playing carries on.
    /// </summary>
    internal Overlay ForShow(Overlay overlay, double? upAt)
    {
        if (MotionUseOf(overlay) is not { } use || MotionStore.Lottie(use.LottieHash) is not { } lottie) return overlay;
        var id = OverlayPicturePrefix + overlay.Id;
        var timeline = MotionTimeline.For(lottie, use.Playback);
        if (upAt is { } at || MotionPlayers[id] is null)
        {
            // Drawn at the size it shows on the slide (1920 × 1080 for a full-frame lower third).
            var still = overlay.Objects.FirstOrDefault(o => o.MediaId == use.FallbackMediaId);
            var (w, h) = LottieRenderer.FittedSize(lottie, (int)Math.Clamp(still?.Width ?? 1920, 16, 3840), (int)Math.Clamp(still?.Height ?? 1080, 16, 2160));
            MotionPlayers.Start(id, FilledIn(use.LottieHash, lottie, use.Values), timeline, w, h, upAt ?? ShowState.HostClock());
        }
        return MotionTemplateStore.LiveCopy(overlay, use, id, timeline);
    }

    /// <summary>
    /// Keeps the playing templates in step with the show, on every change to it: an overlay being
    /// taken down (however it happened) plays its Out; one that has gone stops; a full-screen
    /// template's video just put up starts its animation, and stops it when it goes.
    /// </summary>
    private void SyncMotion()
    {
        var now = ShowState.HostClock();
        foreach (var motion in MotionPlayers.Playing)
        {
            if (motion.Id.StartsWith(OverlayPicturePrefix, StringComparison.Ordinal))
            {
                var overlayId = motion.Id[OverlayPicturePrefix.Length..];
                if (!Show.LiveOverlays.Any(o => o.Id == overlayId)) MotionPlayers.Stop(motion.Id);
                else if (Show.IsLeaving(overlayId) && motion.DownAt is null) MotionPlayers.TakeDown(motion.Id, now);
            }
            else if (motion.Id.StartsWith(BackgroundPicturePrefix, StringComparison.Ordinal))
            {
                var mediaId = motion.Id[BackgroundPicturePrefix.Length..];
                if (Show.LiveMedia.Values.Any(c => c.MediaId == mediaId)) continue;
                MotionPlayers.Stop(motion.Id);
                Media.StandIn(mediaId, null);
            }
        }
        foreach (var cue in Show.LiveMedia.Values)
        {
            var id = BackgroundPicturePrefix + cue.MediaId;
            if (MotionPlayers[id] is not null || notMotion.Contains(cue.MediaId)) continue;
            if (Library.Get<MediaItem>(cue.MediaId) is not { MediaKind: MediaKind.Video } item || MotionUseOf(item) is not { } use
                || MotionStore.Lottie(use.LottieHash) is not { } lottie)
            {
                notMotion.Add(cue.MediaId);
                continue;
            }
            // A background never takes itself down: "once" holds its last frame instead.
            var playback = use.Playback == MotionPlayback.Once ? MotionPlayback.HoldLast : use.Playback;
            var (w, h) = LottieRenderer.FittedSize(lottie, 1920, 1080);
            MotionPlayers.Start(id, FilledIn(use.LottieHash, lottie, use.Values), MotionTimeline.For(lottie, playback), w, h, now);
            Media.StandIn(item.Id, id);
        }
    }

    /// <summary>A template set to play once has played through: it takes itself down (its Out has played already).</summary>
    private void OnMotionFinished(string id)
    {
        if (!id.StartsWith(OverlayPicturePrefix, StringComparison.Ordinal)) return;
        var overlayId = id[OverlayPicturePrefix.Length..];
        if (!Show.LiveOverlays.Any(o => o.Id == overlayId) || Show.IsLeaving(overlayId)) return;
        Show.DismissOverlay(overlayId);
        RenderOutputs(Rendering.SceneTransition.Cut, refreshOnly: true);
        MarkLiveOverlays();
    }

    // MARK: The Overlays bin

    /// <summary>The selected overlay in the bin is a motion template (its fields are the template's).</summary>
    [ObservableProperty]
    public partial bool SelectedOverlayIsMotion { get; set; }

    /// <summary>
    /// The bin's fields for a motion template overlay: one per text field of its template, which
    /// change the use's values (and the animation, if it's up) as you type.
    /// </summary>
    private void AddMotionFields(OverlayTileViewModel? tile)
    {
        SelectedOverlayIsMotion = false;
        if (tile is null || MotionUseOf(tile.Overlay) is not { } use || MotionStore.Lottie(use.LottieHash) is not { } lottie) return;
        SelectedOverlayIsMotion = true;
        var overlay = tile.Overlay;
        foreach (var field in MotionFields.Discover(lottie).Where(f => f.Kind == MotionFieldKind.Text))
        {
            var standIn = new SlideObject
            {
                Id = field.Id, ObjectKind = SlideObjectKind.Text, Name = field.Label,
                Text = use.Values.TryGetValue(field.Id, out var value) ? value : field.Default,
            };
            OverlayTextFields.Add(new SlideTextFieldViewModel(standIn, () =>
            {
                if (MotionUseOf(overlay) is not { } current) return;
                ChangeMotionUse(overlay, current with { Values = new(current.Values) { [field.Id] = standIn.Text } });
            }));
        }
    }

    /// <summary>Opens the template window on the selected motion template overlay, to change its fields, colours and playback.</summary>
    [RelayCommand]
    private void EditMotionTemplate()
    {
        if (System.Windows.Application.Current?.MainWindow is not { } owner || SelectedOverlay?.Overlay is not { } overlay) return;
        Views.MotionTemplatesWindow.Open(owner, this, editing: overlay);
    }

    /// <summary>Opens the motion templates window (Overlays › Motion templates…).</summary>
    [RelayCommand]
    private void OpenMotionTemplates()
    {
        if (System.Windows.Application.Current?.MainWindow is not { } owner) return;
        Views.MotionTemplatesWindow.Open(owner, this);
    }

    /// <summary>Puts a motion template overlay up or takes it down, as clicking its tile does.</summary>
    public void SetMotionOverlayLive(Overlay overlay, bool up) => SetOverlayLive(overlay, up);

    // MARK: Video files

    /// <summary>
    /// Renders a template with its fields filled in to an H.264 MP4: into the Media bin (a looping
    /// background, or a foreground video), and/or a copy at <paramref name="copyTo"/>. Returns the
    /// job (null when another is running).
    /// </summary>
    public MotionTemplateJob? ExportMotion(string name, LottieDocument filled, MotionTimeline timeline, MotionExportOptions options,
        string? copyTo, bool addToMedia, bool background)
    {
        return RunMotionJob($"Rendering “{name}”", async job =>
        {
            var item = await OffUiThread<MediaItem?>(() =>
            {
                var path = Path.Combine(LottieVideoMaker.WorkFolder(Library.Root), LottieVideoMaker.FileName(name));
                var progress = new Progress<MotionRenderProgress>(p => job.Report(p.Fraction, $"{job.Title}: {p.Fraction:P0}"));
                LottieVideoMaker.Render(filled, timeline, options, path, MotionFonts, progress, job.Token);
                if (copyTo is not null) File.Copy(path, copyTo, overwrite: true);
                if (!addToMedia)
                {
                    TryDelete(path);
                    return null;
                }
                return LottieVideoMaker.FileIntoLibrary(Library, path, name, background);
            });
            if (item is null)
            {
                Notice = $"Saved “{Path.GetFileName(copyTo)}”.";
                return null;
            }
            RefreshMedia();
            SelectedMedia = MediaItems.FirstOrDefault(m => m.Item.Id == item.Id);
            Notice = $"Rendered “{item.Name}” into the Media bin" + (copyTo is null ? "." : $", and saved a copy as {Path.GetFileName(copyTo)}.");
            return item;
        });
    }

    // MARK: After Effects

    /// <summary>The After Effects the bridge uses: the one chosen by hand in Settings, else the newest installed. Null when there's none.</summary>
    public AfterEffectsInstall? AfterEffects =>
        (Settings.AfterEffectsAerender is { } chosen ? AfterEffectsInstall.FromAerender(chosen) : null) ?? AfterEffectsInstall.Find().FirstOrDefault();

    /// <summary>Remembers an aerender.exe chosen by hand (for this PC).</summary>
    public AfterEffectsInstall? ChooseAfterEffects(string aerender)
    {
        if (AfterEffectsInstall.FromAerender(aerender) is not { } install) return null;
        Settings.AfterEffectsAerender = aerender;
        Settings.Save();
        return install;
    }

    private string AfterEffectsWork
    {
        get
        {
            var path = Path.Combine(RecordingLibrary.SpoolDirectory(Library.Root), "aftereffects");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    /// <summary>The compositions in a project, read by After Effects (off the UI thread).</summary>
    public Task<AeProject> ReadAfterEffectsProjectAsync(AfterEffectsInstall install, string aep, CancellationToken cancellationToken) =>
        OffUiThread(() => new AfterEffectsBridge(install, AfterEffectsWork).ReadProject(aep, cancellationToken));

    /// <summary>
    /// Renders a composition with After Effects (making <paramref name="changes"/> in a copy first) and
    /// files the video into the Media bin. Returns the job (null when another is running).
    /// </summary>
    public MotionTemplateJob? RenderAfterEffects(AfterEffectsInstall install, string aep, string composition, IReadOnlyList<AeChange> changes,
        string name, bool background)
    {
        var outputModule = Settings.AfterEffectsOutputModule ?? Aerender.H264;
        return RunMotionJob($"After Effects is rendering “{name}”", async job =>
        {
            var item = await OffUiThread<MediaItem>(() =>
            {
                var output = Path.Combine(AfterEffectsWork, LottieVideoMaker.FileName(name));
                var progress = new Progress<AerenderStatus>(s => job.Report(s.Fraction,
                    s.TotalFrames is { } total ? $"{job.Title}: frame {s.FramesDone} of {total}" : $"{job.Title}: {s.LastLine ?? "starting"}"));
                var result = new AfterEffectsBridge(install, AfterEffectsWork).Render(aep, composition, changes, output, progress, job.Token, outputModule);
                job.Token.ThrowIfCancellationRequested();
                if (!result.Succeeded) throw new IOException(result.Problem);
                return AfterEffectsBridge.FileIntoLibrary(Library, output, name, background);
            });
            RefreshMedia();
            SelectedMedia = MediaItems.FirstOrDefault(m => m.Item.Id == item.Id);
            Notice = item.FileStatus == MediaFileStatus.Ready
                ? $"After Effects rendered “{item.Name}”: it's in the Media bin (folder “{AfterEffectsBridge.MediaFolder}”)."
                : $"After Effects rendered “{item.Name}”, but Windows can't play it as it is ({item.StatusDetail}). Right-click its tile to transcode it.";
            return item;
        });
    }
}
