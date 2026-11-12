using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MxU.Slides.Core.Diagnostics;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.ViewModels;
using MxU.Slides.Windows.Views;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>
/// The second half of LibraryDepth: the Mac's newer library rules (folder moves, search-hit menus,
/// edited media files, a delete taking its file), music duplicates, backups and restores, files
/// handed over from Explorer, the welcome guide, and Diagnostics with Check this PC. Pictures
/// 112-… to 131-…. Nothing here touches the real library: the self-test runs on a throwaway one.
/// Check this PC is never run from here (it would start a second self-test); its rules and its
/// result page are checked instead.
/// </summary>
public sealed partial class SelfTest
{
    private async Task LibraryCareCheck()
    {
        await FolderMoveAndSearchHitsCheck();
        await MusicDuplicateCheck();
        await MediaFileEditCheck();
        await LiveVideoReloadCheck();
        await BackupRestoreCheck();
        await HandoffCheck();
        await GuideCheck();
        await DiagnosticsCheck();
    }

    // MARK: Folder moves and search hits (the Mac's LibraryView update)

    private async Task FolderMoveAndSearchHitsCheck()
    {
        var panel = window.LibraryPanelView;
        var lyrics = DepthSong("Goodness of God", "Verse 1\nI love You Lord\nOh Your mercy never fails me", folder: "Worship/Lyrics");
        DepthSong("Old Rugged Cross", "Verse 1\nOn a hill far away stood an old rugged cross", folder: "Archive");
        model.OpenLibraryFolder(["Worship"]);
        await Delay(300);
        var row = model.LibraryRows.OfType<LibraryFolderRowViewModel>().FirstOrDefault(f => f.Name == "Lyrics");
        Check("a folder inside a folder is listed there", row is not null);
        if (row is not null)
        {
            var menu = panel.OpenMenuFor(row);
            await Delay(400);
            var move = menu.Items.OfType<MenuItem>().FirstOrDefault(i => i.Header as string == "Move to Folder");
            var inside = move?.Items.OfType<MenuItem>().Select(i => (i.Header as string ?? "").Trim()).ToList() ?? [];
            Check("a folder's menu has Move to Folder (as on the Mac), with Top Level since it's inside another",
                move is { IsEnabled: true } && inside.FirstOrDefault() == "Top Level");
            Check("…offering the other folders but never itself", inside.Contains("Archive") && !inside.Any(h => h.EndsWith("Lyrics", StringComparison.Ordinal)));
            SaveElement(menu, "112-library-folder-move-menu");
            menu.IsOpen = false;
            model.MovePresentationFolder(["Worship", "Lyrics"], []);
            await Delay(300);
            Check("moving it to the top level refiles its songs", model.Library.Get<Presentation>(lyrics.Id)?.Folder == "Lyrics");
        }
        model.OpenLibraryFolder([]);

        // A search hit right-clicks like the song in its folder, with Show in Library first.
        model.LibrarySearch = "goodness of god";
        await Delay(400);
        var hit = model.LibraryItems.FirstOrDefault(e => e.Presentation.Id == lyrics.Id);
        if (hit is not null)
        {
            var menu = panel.OpenMenuFor(hit);
            await Delay(400);
            var headers = menu.Items.OfType<MenuItem>().Select(i => i.Header as string ?? "").ToList();
            Check("a song found by a search has Show in Library first, then its own menu",
                headers.FirstOrDefault() == "Show in Library" && headers.Contains("Add to Service") && headers.Contains("Move to Trash"));
            SaveElement(menu, "113-library-search-hit-menu");
            menu.IsOpen = false;
        }
        model.ShowInLibrary(lyrics.Id);
        await Delay(400);
        Check("Show in Library clears the search, opens its folder and selects it",
            model.LibrarySearch.Length == 0 && model.LibraryPath.SequenceEqual(["Lyrics"]) && model.SelectedLibraryEntry?.Presentation.Id == lyrics.Id);

        model.LibrarySearch = "deep blue";
        await Delay(400);
        if (model.LibraryRows.OfType<LibraryMediaHitViewModel>().FirstOrDefault() is { } media)
        {
            var menu = panel.OpenMenuFor(media);
            await Delay(400);
            var headers = menu.Items.OfType<MenuItem>().Select(i => (i.Header as string ?? "").Trim()).ToList();
            Check("a media search hit right-clicks like its tile (Show Now, Loop, favourite, folder, Move to Trash)",
                headers.FirstOrDefault() == "Show in Library" && headers.Contains("Show Now") && headers.Contains("Loop")
                && headers.Contains("Move to Folder") && headers.Contains("Move to Trash"));
            SaveElement(menu, "114-library-search-media-menu");
            menu.IsOpen = false;
        }
        model.LibrarySearch = "";
        model.OpenLibraryFolder([]);
        await Delay(200);
    }

    // MARK: Music duplicates, and a delete taking its file

    private async Task MusicDuplicateCheck()
    {
        var tone = Path.Combine(depthScratch, "Offertory Pad.wav");
        WriteTone(tone, seconds: 3);
        var first = (await model.ImportAudioFilesAsync([tone])).FirstOrDefault();
        Check("importing music with the Import button adds the song", first is { Name: "Offertory Pad" });
        if (first is null) return;
        ExpectDialog("Add as New Copy", "115-music-duplicate-prompt");
        var again = (await model.ImportAudioFilesAsync([tone])).FirstOrDefault();
        Check("importing the same music again asks first, as for pictures and videos", LastHeadline == "“Offertory Pad.wav” is already in your library");
        Check("Add as New Copy makes a second song sharing the one file", again is { Name: "Offertory Pad 2" } && again.Id != first.Id && again.FileHash == first.FileHash);
        ExpectDialog("Use Existing", "");
        var reused = (await model.ImportAudioFilesAsync([tone])).FirstOrDefault();
        Check("Use Existing adds nothing", reused?.FileHash == first.FileHash && model.Library.All<AudioItem>().Count(a => a.FileHash == first.FileHash) == 2);

        // Deleting one copy keeps the file (the other uses it); deleting the last takes it to the library's Trash.
        model.DeleteSong(again!.Id);
        await Delay(500);
        Check("deleting one of two songs on the same file keeps the file", model.Library.Blobs.Find(first.FileHash) is not null);
        model.DeleteSong(first.Id);
        var gone = await Until(() => model.Library.Blobs.Find(first.FileHash) is null, TimeSpan.FromSeconds(5));
        Check("deleting the last song on a file moves the file to the library's Trash (the Mac's one Delete)",
            gone && Directory.EnumerateFiles(Path.Combine(model.Library.TrashDirectory, LibraryBackup.BlobsFolder), first.FileHash + "*").Any());
    }

    // MARK: A media file edited where it lives

    private async Task MediaFileEditCheck()
    {
        var source = Path.Combine(depthScratch, "Edited Welcome.png");
        WritePicture(source, Color.FromRgb(0x20, 0x30, 0x60), Color.FromRgb(0x90, 0xA0, 0xF0), 640, 360);
        var item = (await model.ImportMediaFilesAsync([source])).FirstOrDefault();
        if (item is null || model.Library.BlobPath(item) is not { } blob)
        {
            Check("a picture imports for the edit check", false);
            return;
        }
        model.CheckMediaFilesSoon();
        Check("the library notes the new file's time (without reading it)", await Until(() => model.MediaFileNoted(blob), TimeSpan.FromSeconds(8)));

        // Opened from the Blobs folder in a photo editor, touched up and saved over itself.
        WritePicture(blob, Color.FromRgb(0x60, 0x20, 0x20), Color.FromRgb(0xF0, 0xC0, 0x90), 800, 450);
        File.SetLastWriteTimeUtc(blob, DateTime.UtcNow.AddMinutes(-1)); // as if saved a minute ago: settled
        model.CheckMediaFilesSoon();
        var adopted = await Until(() => model.Library.Get<MediaItem>(item.Id)?.FileHash != item.FileHash, TimeSpan.FromSeconds(10));
        var after = model.Library.Get<MediaItem>(item.Id);
        Check("an edited library file is taken in under its new bytes (the Mac's \"edited file takes its new bytes\")", adopted);
        Check("…the item keeps its name, and takes the new size", after is { Name: "Edited Welcome", PixelWidth: 800 } && model.Library.BlobPath(after) is { } now && File.Exists(now) && !File.Exists(blob));
        model.ShowingMedia = true;
        await Step("116-media-edited-in-place", () => { }, settle: 700);

        // Deleting it (used nowhere) takes its file along, to the library's Trash.
        var hash = after?.FileHash ?? "";
        if (after is not null) await model.DeleteMediaAsync(after);
        Check("deleting media used nowhere moves its file to the library's Trash",
            await Until(() => model.Library.Blobs.Find(hash) is null, TimeSpan.FromSeconds(5)) && model.Notice?.Contains("and its file", StringComparison.Ordinal) == true);
    }

    // MARK: A video on screen when its file is replaced

    private async Task LiveVideoReloadCheck()
    {
        var video = model.Library.All<MediaItem>().FirstOrDefault(m => m.MediaKind == MediaKind.Video && m.FileStatus == MediaFileStatus.Ready && model.Library.BlobPath(m) is not null);
        if (video is null || model.Library.BlobPath(video) is not { } oldFile)
        {
            Skip("a live video reloading on its new file", "no playable video in this run");
            return;
        }
        model.FireMedia(video);
        var playing = await Until(() => model.Media.PlayerFiles(video.Id).Any(), TimeSpan.FromSeconds(5));
        Check("a video goes on screen for the reload check", playing && model.Media.PlayerFiles(video.Id).All(f => string.Equals(Path.GetFullPath(f), Path.GetFullPath(oldFile), StringComparison.OrdinalIgnoreCase)));
        // The same film with a few bytes after its end: a different file that plays the same.
        var replacement = Path.Combine(depthScratch, "Replaced " + Path.GetFileName(video.FileName));
        File.Copy(oldFile, replacement, overwrite: true);
        await using (var stream = new FileStream(replacement, FileMode.Append)) stream.Write("MxU self-test"u8);
        model.ChooseMediaFile = _ => replacement;
        if (MediaUsage.Uses(model.Library, video.Id).Count > 0) ExpectDialog("Replace Everywhere…", "");
        var replaced = await model.ReplaceMediaFileEverywhereAsync(video);
        var newFile = model.Library.Get<MediaItem>(video.Id) is { } swapped ? model.Library.BlobPath(swapped) : null;
        var moved = await Until(() => newFile is not null && model.Media.PlayerFiles(video.Id).Any()
            && model.Media.PlayerFiles(video.Id).All(f => string.Equals(Path.GetFullPath(f), Path.GetFullPath(newFile), StringComparison.OrdinalIgnoreCase)), TimeSpan.FromSeconds(5));
        Check("a video on screen plays the new file at once when its file is replaced (no need to fire it again)", replaced && moved);
        await Step("117-video-reloaded-in-place", () => { }, settle: 900);
        model.ClearAllCommand.Execute(null);
        await Delay(300);
    }

    // MARK: Backups and restores

    private async Task BackupRestoreCheck()
    {
        model.ClearAllCommand.Execute(null);
        var folder = Directory.CreateDirectory(Path.Combine(depthScratch, "backups")).FullName;
        var file = Path.Combine(folder, BackupFiles.SavedName(DateTimeOffset.Now));
        var keep = DepthSong("Restore Me", "Verse 1\nThis song is in the backup\nThen deleted, then restored");
        var edited = DepthSong("Edited After", "Verse 1\nThe backup has the first words");
        var manifest = await model.BackUpNowAsync(file, includeMedia: false);
        Check("Back Up Now writes one backup file of the library", manifest is not null && File.Exists(file) && new FileInfo(file).Length > 0);
        Check("…a zip with its manifest and the songs", ZipHas(file, "manifest.json") && ZipHas(file, $"{keep.Id}.json"));

        BackupWindow? shown = null;
        void OnBackups(object? sender, EventArgs e) => shown = sender as BackupWindow;
        BackupWindow.Opened += OnBackups;
        try { BackupWindow.Open(window, model); }
        finally { BackupWindow.Opened -= OnBackups; }
        await Delay(700);
        if (shown is not null)
        {
            Save(shown, "118-backups-window");
            shown.Close();
        }
        Check("the Backups window opens", shown is not null);

        // Since the backup: one song deleted, one edited.
        model.Library.Delete<Presentation>(keep.Id);
        var changed = model.Library.Get<Presentation>(edited.Id)!;
        changed.Name = "Edited After (newer)";
        model.Library.Save(changed);
        await Delay(1100); // a whole second: the restore compares saved times

        var restore = await RestoreWindow.OpenAsync(window, model, file);
        Check("Restore reads the backup and shows what's in it", restore is not null);
        if (restore is null) return;
        restore.Exclude(BackupSection.Settings); // this run's own settings stay as they are
        await Delay(600);
        Save(restore, "119-restore-window");
        await restore.RestoreAsync();
        var result = restore.Result;
        Check("restoring brings back what was deleted", model.Library.Get<Presentation>(keep.Id) is { Name: "Restore Me" });
        Check("…keeps an edit newer than the backup (newer wins)", model.Library.Get<Presentation>(edited.Id)?.Name == "Edited After (newer)");
        Check("…and takes a safety copy of the library first", result?.SafetyCopy is { } safety && File.Exists(safety));
        await Delay(400);
        Save(restore, "120-restore-done");
        restore.Close();
        model.Library.Delete<Presentation>(keep.Id);
        model.Library.Delete<Presentation>(edited.Id);

        Check("nightly backups follow the Mac (3 AM, the last 7), shown under the setting", ScheduledBackup.Keep == 7 && model.LastBackupLine.Length > 0);
        backupForHandoff = file;
    }

    private string? backupForHandoff;

    private static bool ZipHas(string zip, string name)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zip);
            return archive.Entries.Any(e => e.FullName.EndsWith(name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return false;
        }
    }

    // MARK: Files from Explorer

    private async Task HandoffCheck()
    {
        // A second copy started by a double-click hands its files over a pipe to the one running.
        var name = "selftest-" + Guid.NewGuid().ToString("N");
        IReadOnlyList<string>? received = null;
        using (FileHandoff.Listen(name, files => received = files))
        {
            var sent = await Task.Run(() => FileHandoff.SendToRunningCopy(name, [@"C:\Songs\Amazing Grace.mxuslides", @"C:\Backups\Library.mxubackup"]));
            await Until(() => received is not null, TimeSpan.FromSeconds(5));
            Check("a double-clicked file reaches the copy already running (named pipe, this user only)",
                sent && received?.SequenceEqual([@"C:\Songs\Amazing Grace.mxuslides", @"C:\Backups\Library.mxubackup"]) == true);
        }

        // A .mxuslides file opened from Explorer is imported.
        var song = DepthSong("From Explorer", "Verse 1\nDouble-clicked in a folder\nOpened in MxU Slides");
        var path = Path.Combine(depthScratch, SlidesPresentationFile.FileName(song));
        if (SlidesPresentationFile.Bundle(model.Library, song.Id) is { } bundle) SlidesPresentationFile.Write(bundle, model.Library.Blobs, [], path);
        model.Library.Delete<Presentation>(song.Id);
        window.OpenFromOutside([path, Path.Combine(depthScratch, "missing.mxuslides")]);
        var imported = await Until(() => model.Library.All<Presentation>().Any(p => p.Name == "From Explorer"), TimeSpan.FromSeconds(8));
        Check("a .mxuslides file opened from Explorer is imported", File.Exists(path) && imported);
        Check("…and a file that isn't there is reported, not a crash", model.Problem?.Contains("missing.mxuslides", StringComparison.Ordinal) == true);
        model.Problem = null;

        // A .mxubackup opens the restore window (shown, then closed: nothing restored).
        if (backupForHandoff is { } backup)
        {
            RestoreWindow? opened = null;
            void OnRestore(object? sender, EventArgs e) => opened = sender as RestoreWindow;
            RestoreWindow.Opened += OnRestore;
            try
            {
                window.OpenFromOutside([backup]);
                await Until(() => opened is not null, TimeSpan.FromSeconds(8));
            }
            finally { RestoreWindow.Opened -= OnRestore; }
            Check("a .mxubackup opened from Explorer shows what it would restore", opened is not null);
            opened?.Close();
        }
        foreach (var p in model.Library.All<Presentation>().Where(p => p.Name == "From Explorer").ToList()) model.Library.Delete<Presentation>(p.Id);
    }

    // MARK: The welcome guide

    private async Task GuideCheck()
    {
        var steps = new List<OnboardingFlow.Step>();
        void OnOpened(object? sender, EventArgs e)
        {
            if (sender is not OnboardingWindow guide) return;
            guide.ContentRendered += async (_, _) =>
            {
                var n = 121;
                foreach (var _ in OnboardingFlow.Steps)
                {
                    await Delay(500);
                    steps.Add(guide.Flow.Current);
                    Save(guide, $"{n++}-guide-{guide.Flow.Current.ToString().ToLowerInvariant()}");
                    if (guide.Flow.IsLastStep) break;
                    guide.Next();
                }
                guide.Close();
            };
        }
        OnboardingWindow.Opened += OnOpened;
        try { OnboardingWindow.Open(window, model); }
        finally { OnboardingWindow.Opened -= OnOpened; }
        Check("the welcome guide walks through welcome, screens, look, songs and Help", steps.SequenceEqual(OnboardingFlow.Steps));
        await Delay(200);
    }

    // MARK: Diagnostics, Report a problem, Check this PC

    private async Task DiagnosticsCheck()
    {
        var diagnostics = DiagnosticsWindow.Open(window, model);
        await Delay(300);
        if (diagnostics.Gathered is { } gathered) await Task.WhenAny(gathered, Task.Delay(TimeSpan.FromSeconds(30)));
        Check("Diagnostics gathers this PC's facts and health checks without holding up the app", diagnostics.Gathered?.IsCompleted == true);
        await Delay(300);
        Save(diagnostics, "126-diagnostics-this-pc");
        diagnostics.ShowPage(DiagnosticsWindow.Page.Logs);
        await Delay(500);
        Save(diagnostics, "127-diagnostics-logs");
        diagnostics.ShowPage(DiagnosticsWindow.Page.Check);
        await Delay(400);
        Save(diagnostics, "128-diagnostics-check-this-pc");

        // Check this PC: never while anything is on the screens.
        var song = model.Library.All<Presentation>().FirstOrDefault(p => p.PresentationKind == PresentationKind.Song);
        if (song is not null)
        {
            model.Open(song, null, null);
            model.GoLiveCommand.Execute(model.Slides.FirstOrDefault());
            await Delay(400);
            Check("Check this PC refuses while something is on an output, saying why", PcCheckRunner.Blocked(model)?.Contains("Clear them first", StringComparison.Ordinal) == true);
            model.ClearAllCommand.Execute(null);
            await Delay(600);
        }
        var sandbox = Path.Combine(Path.GetTempPath(), "MxU Slides PC check example", "throwaway library");
        var env = PcCheckRunner.ChildEnvironment(sandbox, PcCheck.QuickSections);
        var realData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MxU Slides");
        var realLocal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MxU Slides");
        Check("its copy of the app gets a throwaway library of its own (never the real one), marked as a sandbox",
            env["MXU_SLIDES_DATA"]!.StartsWith(sandbox, StringComparison.Ordinal) && env["MXU_SLIDES_LOCAL"]!.StartsWith(sandbox, StringComparison.Ordinal)
            && env[SelfTestSandbox.MarkerVariable] == "1" && SelfTestSandbox.Problem(env["MXU_SLIDES_DATA"], env["MXU_SLIDES_LOCAL"], "1", realData, realLocal, Path.GetTempPath()) is null);
        Check("the self-test refuses a real library (or none set) when started by hand",
            SelfTestSandbox.Problem(realData, realLocal, null, realData, realLocal, Path.GetTempPath()) is not null
            && SelfTestSandbox.Problem(null, null, null, realData, realLocal, Path.GetTempPath()) is not null);
        Check("this run itself is on a throwaway library", SelfTestSandbox.Problem(Environment.GetEnvironmentVariable("MXU_SLIDES_DATA"),
            Environment.GetEnvironmentVariable("MXU_SLIDES_LOCAL"), Environment.GetEnvironmentVariable(SelfTestSandbox.MarkerVariable), realData, realLocal, Path.GetTempPath()) is null);
        var mutexFree = await Task.Run(() =>
        {
            // The scripts hold the heavy-tests lock while this runs, so Check this PC would wait its turn.
            using var mutex = new Mutex(false, PcCheckRunner.HeavyTestLock, out _);
            return true;
        });
        Check("Check this PC waits on the same lock as the scripts' heavy test runs", mutexFree && PcCheckRunner.HeavyTestLock == @"Local\MxU Slides heavy tests");

        // Its result page, from a report as the copy writes it (a canned one: no second self-test from here).
        const string canned = "PASS  screens: audience output shows the slide\nPASS  transitions: a dissolve runs at 60 fps\nSKIP  NDI output (NDI runtime not installed)\n"
            + "PASS  recording: an MP4 is written\nFAIL  sound: the walk-in playlist plays on the chosen speaker\nRESULT: FAIL (1 of 5)\n";
        var lines = PcCheck.Parse(canned);
        var areas = PcCheck.Summarize(lines);
        diagnostics.ShowCheckResult(new PcCheckRunner.Result(Path.Combine(depthScratch, "pc check"), canned, lines, areas, PcCheck.Passed(canned), PcCheck.Finished(canned)));
        await Delay(500);
        Check("its result says one line per area", areas.Count == 5 && areas.Any(a => a.Name == "Sound" && a.State == PcCheck.AreaState.Problem)
            && areas.Any(a => a.Name == "NDI and DeckLink" && a.State == PcCheck.AreaState.NotAvailable));
        Save(diagnostics, "129-diagnostics-check-result");
        diagnostics.Close();

        // Report a problem: one zip, scrubbed, saved (here to the run's own folder, never Downloads), never sent.
        var reports = Directory.CreateDirectory(Path.Combine(depthScratch, "reports")).FullName;
        string? saved = null;
        void OnReport(object? sender, EventArgs e)
        {
            if (sender is not ReportProblemWindow report) return;
            report.Folder = reports;
            report.ContentRendered += async (_, _) =>
            {
                report.Type("Self-test: the projector went black during the second song.");
                await report.SaveAsync();
                saved = report.Saved;
                await Delay(300);
                Save(report, "130-report-a-problem");
                report.Close();
            };
        }
        ReportProblemWindow.Opened += OnReport;
        try { ReportProblemWindow.Open(window, model); }
        finally { ReportProblemWindow.Opened -= OnReport; }
        Check("Report a problem saves one zip where it's told", saved is not null && File.Exists(saved) && Path.GetDirectoryName(saved) == reports);
        if (saved is not null && File.Exists(saved))
        {
            var text = ZipText(saved);
            var home = $@"\Users\{Environment.UserName}\";
            Check("…with the note, the system facts and the logs, and no Windows user name in its paths",
                text.Contains("projector went black", StringComparison.Ordinal) && !text.Contains(home, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static string ZipText(string zip)
    {
        using var archive = ZipFile.OpenRead(zip);
        var all = new System.Text.StringBuilder();
        foreach (var entry in archive.Entries.Where(e => e.Length < 4_000_000))
        {
            using var reader = new StreamReader(entry.Open());
            all.AppendLine(reader.ReadToEnd());
        }
        return all.ToString();
    }

    /// <summary>Waits (without blocking the window) until <paramref name="done"/> is true, or the time is up.</summary>
    private static async Task<bool> Until(Func<bool> done, TimeSpan limit)
    {
        var until = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < until)
        {
            if (done()) return true;
            await Task.Delay(100);
        }
        return done();
    }
}
