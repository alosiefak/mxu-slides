using System.Windows.Threading;
using MxU.Slides.Core.Library;
using MxU.Slides.Windows.Infrastructure;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// Backups: made by hand, or by themselves on a schedule (keeping the last few), and restoring one
/// after a look at what's in it. The file and the restore rules are Core's (LibraryBackup.cs, a
/// port of the Mac's BackupBundle; BackupFiles.cs for the schedule). The work runs off the UI
/// thread, on the library's own below-normal thread or a thread of its own, never the shared pool.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Where automatic backups (and safety copies before a restore) go.</summary>
    public string BackupFolder => Settings.AutoBackupFolder is { Length: > 0 } folder ? folder : Library.BackupDirectory;

    private int backupsRunning;
    private DispatcherTimer? backupTimer;

    public bool IsBackingUp => backupsRunning > 0;

    /// <summary>"Last backup today at 09:30" (or that there's none), for Settings and the Backups window.</summary>
    public string LastBackupLine
    {
        get
        {
            var last = BackupFiles.List(BackupFolder).FirstOrDefault(e => e.Kind is BackupFiles.Kind.Automatic or BackupFiles.Kind.Saved);
            return last is null ? "No backup has been made yet."
                : last.Created.Date == DateTimeOffset.Now.Date ? $"Last backup today at {last.Created:HH:mm}."
                : $"Last backup {last.Created:ddd d MMM} at {last.Created:HH:mm}.";
        }
    }

    /// <summary>The app's name and version, written into each backup.</summary>
    private static string AppLabel => $"MxU Slides for Windows {typeof(MainViewModel).Assembly.GetName().Version?.ToString(3)}";

    /// <summary>
    /// Makes a backup now, into <paramref name="destination"/>. The documents are read on the
    /// library's own thread, so the window and the show carry on. Null when it failed (the problem is shown).
    /// </summary>
    public async Task<BackupManifest?> BackUpNowAsync(string destination, bool includeMedia, IProgress<BackupProgress>? progress = null)
    {
        backupsRunning++;
        OnPropertyChanged(nameof(IsBackingUp));
        try
        {
            var settings = AppSettings.PortableForBackup();
            var manifest = await OnLibraryThread(() => LibraryBackup.Write(Library, destination,
                new BackupWriteOptions(includeMedia, settings, AppLabel, Environment.MachineName), progress));
            Log.Info($"Backed up the library to {destination} ({manifest.DocumentTotal} documents, {manifest.BlobCount} media files)");
            return manifest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Error($"Backup to {destination} failed", ex);
            Problem = $"The backup couldn't be saved: {ex.Message}";
            return null;
        }
        finally
        {
            backupsRunning--;
            OnPropertyChanged(nameof(IsBackingUp));
            OnPropertyChanged(nameof(LastBackupLine));
        }
    }

    /// <summary>
    /// Starts automatic backups: one shortly after launch if it's due, then a look every half hour
    /// (a PC left running all week still gets its daily backup). Called once the window is up.
    /// </summary>
    public void StartAutomaticBackups()
    {
        if (backupTimer is not null) return;
        var atLaunch = true;
        backupTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(20) };
        backupTimer.Tick += async (_, _) =>
        {
            backupTimer.Interval = TimeSpan.FromMinutes(30);
            var launch = atLaunch;
            atLaunch = false;
            await AutomaticBackupIfDueAsync(launch);
        };
        backupTimer.Start();
    }

    /// <summary>Makes an automatic backup when one is due (and the show allows it), then trims the old ones. Returns where it went.</summary>
    public async Task<string?> AutomaticBackupIfDueAsync(bool atLaunch)
    {
        if (IsBackingUp) return null;
        var folder = BackupFolder;
        var last = await OffUiThread(() => BackupFiles.LastAutomatic(folder));
        if (!BackupFiles.IsDue(Settings.AutoBackup, last, DateTimeOffset.Now, atLaunch)) return null;
        // A big one (with the media) waits until nothing is on screen; the next look tries again.
        if (BackupFiles.ShouldWait(Settings.AutoBackupIncludesMedia, !Show.IsEmpty)) return null;
        var path = Path.Combine(folder, BackupFiles.AutomaticName(DateTimeOffset.Now));
        if (await BackUpNowAsync(path, Settings.AutoBackupIncludesMedia) is null) return null;
        var keep = Math.Max(1, Settings.AutoBackupKeep);
        var gone = await OffUiThread(() => BackupFiles.Prune(folder, keep));
        if (gone > 0) Log.Info($"Removed {gone} old automatic backup{(gone == 1 ? "" : "s")}");
        return path;
    }

    // MARK: Restoring

    /// <summary>What a backup holds, and how it differs from the library now (nothing changes).</summary>
    public Task<BackupScan> ScanBackupAsync(string path) => OffUiThread(() => LibraryBackup.Scan(path, Library));

    /// <summary>Why a restore can't start now, or null when it can.</summary>
    public string? RestoreBlocked() =>
        IsBackingUp ? "A backup is being made: try again when it has finished."
        : !Show.IsEmpty ? "Something is on the screens. Clear them first (F1): a restore can change what's showing."
        : null;

    /// <summary>
    /// Restores a backup: first a safety copy of the library as it is (in the backups folder), then
    /// the backup's documents, media files, fonts and Windows extras as <paramref name="options"/>
    /// say. Settings are staged for the next launch. Every list redraws once, at the end.
    /// </summary>
    public async Task<BackupRestoreResult?> RestoreBackupAsync(string path, BackupRestoreOptions options, IProgress<BackupProgress>? progress = null)
    {
        if (RestoreBlocked() is { } reason)
        {
            Problem = reason;
            return null;
        }
        backupsRunning++;
        OnPropertyChanged(nameof(IsBackingUp));
        try
        {
            var settings = AppSettings.PortableForBackup();
            var folder = BackupFolder;
            BackupRestoreResult result;
            using (BatchOfChanges())
                result = await OffUiThread(() => LibraryBackup.Restore(Library, path, options, folder, settings, Catalog.Usage, progress));
            var staged = result.Settings.Count > 0 ? AppSettings.StageFromBackup(result.Settings, options.Policy) : 0;
            AfterRestore(result);
            Log.Info($"Restored {path}: {result.DocumentsLanded} documents, {result.MediaFilesCopied} media files; safety copy {result.SafetyCopy}");
            Notice = string.Join(" ", RestoreSummary(result, staged).Take(3));
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BackupException or InvalidOperationException)
        {
            Log.Error($"Restoring {path} failed", ex);
            Problem = ex is BackupException ? ex.Message : $"The backup couldn't be restored: {ex.Message}";
            return null;
        }
        finally
        {
            backupsRunning--;
            OnPropertyChanged(nameof(IsBackingUp));
        }
    }

    /// <summary>What changed underneath: forget what was read from the old files, and redraw everything.</summary>
    private void AfterRestore(BackupRestoreResult result)
    {
        Catalog.Marks.Reload();
        Catalog.Index.Forget();
        if (result.FontsCopied > 0) Rendering.FontMap.ActivateStored(Library.Root);
        foreach (var item in Library.All<Core.Model.MediaItem>()) Media.ForgetFile(item.Id);
        RefreshThemes();
        RefreshLibrary();
        RefreshServices(keepSelection: true);
        RefreshMedia();
        RefreshOverlays();
        RefreshAudio();
        RefreshCombos();
        RefreshStageLayouts();
        RefreshHotKeys();
        if (CurrentPresentation is { } open)
        {
            if (Library.Get<Core.Model.Presentation>(open.Id) is { } restored) Open(restored, CurrentContextItemId, SelectedArrangement?.Id);
            else
            {
                CurrentPresentation = null;
                Slides.Clear();
                OnPropertyChanged(nameof(HasPresentation));
            }
        }
        OnPropertyChanged(nameof(LastBackupLine));
    }

    /// <summary>The Mac's summary lines ("3 new — Presentations", "2 already here — replaced…"), with the safety copy's place.</summary>
    public static IReadOnlyList<string> RestoreSummary(BackupRestoreResult result, int settingsStaged)
    {
        var lines = new List<string>();
        foreach (var section in BackupSections.All)
            if (result.Added.TryGetValue(section, out var added)) lines.Add($"{added} new — {section.Title()}");
        if (result.Replaced > 0) lines.Add($"{result.Replaced} already here — replaced with the backup's copy");
        if (result.Kept > 0) lines.Add($"{result.Kept} already here — kept yours");
        if (result.Removed > 0) lines.Add($"{result.Removed} not in the backup — moved to the Trash");
        if (result.Same > 0) lines.Add($"{result.Same} already the same");
        if (result.MediaFilesCopied > 0) lines.Add($"{result.MediaFilesCopied} media files");
        if (result.FontsCopied > 0) lines.Add($"{result.FontsCopied} imported fonts");
        if (result.ExtrasCopied > 0) lines.Add($"{result.ExtrasCopied} Windows-only additions");
        if (settingsStaged > 0) lines.Add($"{settingsStaged} settings files — quit and reopen MxU Slides to apply them");
        if (lines.Count == 0) lines.Add("Nothing needed changing: the library already matches the backup.");
        if (result.SafetyCopy is { } safety) lines.Add($"Your library as it was is kept in “{Path.GetFileName(safety)}”.");
        return lines;
    }

    /// <summary>Runs on the library's own background thread (below normal priority, in turn with its other chores).</summary>
    private static Task<T> OnLibraryThread<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        LibraryChores.Queue.Async(() =>
        {
            try { done.SetResult(work()); }
            catch (Exception ex) { done.SetException(ex); }
        });
        return done.Task;
    }
}
