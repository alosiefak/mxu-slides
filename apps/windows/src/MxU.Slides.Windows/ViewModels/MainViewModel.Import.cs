using CommunityToolkit.Mvvm.ComponentModel;
using MxU.Slides.Core.Model;
using MxU.Slides.PptxImport;
using MxU.Slides.ProImport;
using MxU.Slides.Windows.Infrastructure;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>Importing other apps' files: PowerPoint decks and ProPresenter (lyric files are in the Lyrics partial).</summary>
public sealed partial class MainViewModel
{
    /// <summary>True while an import runs in the background (the Import button is disabled).</summary>
    [ObservableProperty]
    public partial bool IsImporting { get; set; }

    /// <summary>Asks the operator a yes/no question (set by the main window). Without it, the answer is no.</summary>
    public Func<string, bool>? AskYesNo { get; set; }

    /// <summary>
    /// True for ProPresenter files. ".pro" is also ChordPro's extension: ProPresenter 7's
    /// .pro is binary (protobuf), ChordPro's is text, so the first bytes decide.
    /// </summary>
    public static bool IsProPresenterFile(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".probundle" or ".proplaylist" or ".protheme") return true;
        if (extension != ".pro") return Directory.Exists(path) && Path.GetExtension(path).Equals(".probundle", StringComparison.OrdinalIgnoreCase);
        try
        {
            using var file = File.OpenRead(path);
            var head = new byte[Math.Min(4096, (int)Math.Min(file.Length, 4096))];
            file.ReadExactly(head);
            if (head.Contains((byte)0)) return true;
            try
            {
                _ = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(head, 0, Math.Max(0, head.Length - 4));
                return false;
            }
            catch (System.Text.DecoderFallbackException)
            {
                return true;
            }
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Imports ProPresenter 7 presentations (.pro, .probundle), playlists (.proPlaylist, each
    /// becoming a service) and themes into the "ProPresenter Import" folder, asking before
    /// replacing anything already in the library.
    /// </summary>
    public async Task<ProImportResult?> ImportProPresenterAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || IsImporting) return null;
        FlushPendingSave();
        IsImporting = true;
        Notice = "Importing from ProPresenter…";
        var progress = new Progress<ProImportProgress>(p => Notice = $"Importing from ProPresenter: {p.Phase}"
            + (p.Detail.Length > 0 ? $" · {p.Detail}" : "") + (p.Total is { } total ? $" ({p.Completed} of {total})" : "") + "…");
        var options = new ProImportOptions { Policy = ImportConflictPolicy.Ask, ConfirmOverwrite = ConfirmOverwriteAsync };
        try
        {
            var result = await new ProPresenterImporter(Library).ImportFilesAsync(paths, options, progress);
            RefreshLibrary();
            RefreshServices(keepSelection: true);
            RefreshThemes();
            RefreshMedia();
            var documents = result.Documents.Concat(result.Playlists.SelectMany(p => p.Documents)).ToList();
            foreach (var warning in documents.SelectMany(d => d.Warnings.Select(w => $"{Path.GetFileName(d.SourcePath)}: {w}"))
                         .Concat(result.Playlists.SelectMany(p => p.Warnings)).Concat(result.Themes.SelectMany(t => t.Warnings)))
                Log.Info($"ProPresenter import: {warning}");
            if (documents.LastOrDefault(d => d.Imported)?.PresentationId is { } id && Library.Get<Presentation>(id) is { } opened)
                Open(opened, null, opened.DefaultArrangementId);

            var failures = result.Playlists.Where(p => p.FailureReason is not null).Select(p => $"{Path.GetFileName(p.SourcePath)} ({p.FailureReason})")
                .Concat(documents.Where(d => d.PresentationId is null).Select(d => Path.GetFileName(d.SourcePath)))
                .ToList();
            if (failures.Count > 0) Problem = $"Couldn't import {string.Join("; ", failures)}. Details are in the log.";
            var parts = new List<string>();
            var imported = documents.Count(d => d.Imported);
            if (imported > 0) parts.Add(imported == 1 ? "1 presentation" : $"{imported} presentations");
            var services = result.Playlists.Sum(p => p.Services.Count);
            if (services > 0) parts.Add(services == 1 ? "1 playlist as a service" : $"{services} playlists as services");
            var themes = result.Themes.Count(t => t.ThemeId is not null && t.Skipped is null);
            if (themes > 0) parts.Add(themes == 1 ? "1 theme" : $"{themes} themes");
            var media = documents.Sum(d => d.MediaImported) + result.Themes.Sum(t => t.MediaImported);
            if (media > 0) parts.Add(media == 1 ? "1 media file" : $"{media} media files");
            var kept = documents.Count(d => d.Skipped is not null) + result.Themes.Count(t => t.Skipped is not null);
            Notice = (parts.Count > 0 ? $"Imported {string.Join(", ", parts)} from ProPresenter" : "Nothing new imported from ProPresenter")
                + (kept > 0 ? $"; kept your copy of {kept}" : "") + ".";
            return result;
        }
        catch (Exception ex)
        {
            Log.Error("ProPresenter import failed", ex);
            Problem = $"ProPresenter import failed: {ex.Message}";
            Notice = null;
            return null;
        }
        finally
        {
            IsImporting = false;
        }
    }

    /// <summary>
    /// Imports a whole ProPresenter library (the folder holding Libraries, Playlists, Themes…):
    /// presentations, media, themes, props, messages, stage layouts and playlists. Earlier imports
    /// you haven't changed are refreshed; anything you edited here is kept.
    /// </summary>
    public async Task<ProWorkspaceImportResult?> ImportProPresenterLibraryAsync(string showDirectory)
    {
        if (IsImporting) return null;
        FlushPendingSave();
        IsImporting = true;
        Notice = "Importing your ProPresenter library…";
        var progress = new Progress<ProImportProgress>(p => Notice = $"Importing your ProPresenter library: {p.Phase}"
            + (p.Total is { } total ? $" ({p.Completed} of {total})" : "") + "…");
        try
        {
            var result = await new ProWorkspaceImporter(Library).ImportWorkspaceAsync(showDirectory,
                new WorkspaceImportOptions { Policy = ImportConflictPolicy.UpdateUnedited }, progress: progress);
            foreach (var warning in result.Warnings) Log.Info($"ProPresenter library import: {warning}");
            SaveProPresenterExtras(result);
            RefreshLibrary();
            RefreshServices(keepSelection: true);
            RefreshThemes();
            RefreshMedia();
            RefreshOverlays();
            RefreshStageLayouts();
            var parts = new List<string>
            {
                $"{result.Presentations.Count(p => p.Imported)} presentations",
                $"{result.ServicesImported.Count} playlists",
                $"{result.ThemesImported.Count} themes",
                $"{result.MediaImported} media files",
            };
            if (result.OverlaysImported.Count > 0) parts.Add($"{result.OverlaysImported.Count} props");
            if (result.LayoutsImported.Count > 0) parts.Add($"{result.LayoutsImported.Count} stage layouts");
            if (result.Combos.Count > 0) parts.Add($"{result.Combos.Count} macros");
            if (result.ScheduleTriggers.Count > 0) parts.Add($"{result.ScheduleTriggers.Count} calendar events");
            Notice = $"Imported {string.Join(", ", parts)} from ProPresenter"
                + (result.SkippedEdited.Count > 0 ? $"; kept {result.SkippedEdited.Count} you've changed here" : "")
                + (result.Warnings.Count > 0 ? ". Details are in the log." : ".");
            return result;
        }
        catch (Exception ex)
        {
            Log.Error("ProPresenter library import failed", ex);
            Problem = $"ProPresenter library import failed: {ex.Message}";
            Notice = null;
            return null;
        }
        finally
        {
            IsImporting = false;
        }
    }

    /// <summary>
    /// What the importer maps but leaves to the app: macros become action combos (with their
    /// collections as folders on the combo board) and calendar events become schedule triggers.
    /// Ones already in the library are kept as they are.
    /// </summary>
    private void SaveProPresenterExtras(ProWorkspaceImportResult result)
    {
        foreach (var combo in result.Combos.Where(c => Library.Get<ActionCombo>(c.Id) is null)) Library.Save(combo);
        if (result.ComboFolders.Count > 0)
        {
            var board = Library.Get<ControlBoard>(Core.Automation.ControlBoardLogic.ComboBoardId)
                ?? new ControlBoard { Id = Core.Automation.ControlBoardLogic.ComboBoardId };
            foreach (var folder in result.ComboFolders.Where(f => board.Folders.All(existing => existing.Name != f.Name)))
            {
                var id = Core.Automation.ControlBoardLogic.AddFolder(board, folder.Name);
                board.Folders.First(f => f.Id == id).ItemIds.AddRange(folder.ComboIds);
            }
            Library.Save(board);
        }
        foreach (var trigger in result.ScheduleTriggers.Where(t => Library.Get<ScheduleTrigger>(t.Id) is null)) Library.Save(trigger);
        if (result.Combos.Count + result.ScheduleTriggers.Count > 0)
            Log.Info($"ProPresenter library import: {result.Combos.Count} macros as action combos, {result.ScheduleTriggers.Count} calendar events as schedule triggers");
    }

    /// <summary>Called on the import's worker thread; the question is asked on the UI thread.</summary>
    private ValueTask<bool> ConfirmOverwriteAsync(ImportConflict conflict, CancellationToken cancellationToken)
    {
        var question = $"\"{conflict.Label}\" is already in your library"
            + (conflict.EditedSinceImport ? " and has been changed here" : "")
            + ". Replace it with the ProPresenter version?";
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || AskYesNo is not { } ask) return ValueTask.FromResult(false);
        return new ValueTask<bool>(dispatcher.InvokeAsync(() => ask(question)).Task);
    }

    /// <summary>
    /// Imports .pptx decks (or folders of them) into the "PowerPoint Import" folder,
    /// then opens the last one. Importing the same file again replaces it in place.
    /// </summary>
    public async Task<IReadOnlyList<PptxImportResult>> ImportPowerPointAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || IsImporting) return [];
        FlushPendingSave();
        IsImporting = true;
        Notice = "Importing PowerPoint…";
        // Created here, so progress reports arrive on the UI thread.
        var progress = new Progress<PptxImportProgress>(p => Notice = $"Importing {Path.GetFileName(p.SourcePath)}: {p.Phase switch
        {
            PptxImportPhase.Slides => $"slide {p.Completed} of {p.Total}",
            PptxImportPhase.Media => $"media {p.Completed} of {p.Total}",
            PptxImportPhase.Saving => "saving",
            _ => "reading",
        }}…");
        try
        {
            var results = await PptxImporter.ImportItemsAsync(paths, Library, progress: progress);
            foreach (var result in results)
            {
                if (!result.Succeeded) Log.Warn($"PowerPoint import of {result.SourcePath} failed: {result.FailureReason}");
                foreach (var warning in result.Warnings) Log.Info($"PowerPoint import {Path.GetFileName(result.SourcePath)}: {warning}");
            }
            RefreshLibrary();
            if (results.LastOrDefault(r => r.Succeeded)?.PresentationId is { } id && Library.Get<Presentation>(id) is { } deck)
                Open(deck, null, deck.DefaultArrangementId);

            var failed = results.Where(r => !r.Succeeded).Select(r => $"{Path.GetFileName(r.SourcePath)} ({r.FailureReason})").ToList();
            if (failed.Count > 0) Problem = $"Couldn't import {string.Join("; ", failed)}";
            var missingFonts = results.SelectMany(r => r.MissingFonts).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Notice = PptxImporter.Summary(results)
                + (missingFonts.Count > 0 ? $". Not installed, so shown in a stand-in font: {string.Join(", ", missingFonts)}" : "")
                + (results.Any(r => r.Warnings.Count > 0) ? ". Details are in the log." : ".");
            return results;
        }
        catch (Exception ex)
        {
            Log.Error("PowerPoint import failed", ex);
            Problem = $"PowerPoint import failed: {ex.Message}";
            Notice = null;
            return [];
        }
        finally
        {
            IsImporting = false;
        }
    }
}
