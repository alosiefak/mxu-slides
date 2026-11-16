using MxU.Slides.Core.Model;
using MxU.Slides.OpenLpImport;
using MxU.Slides.Windows.Infrastructure;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// Importing a church's songs from OpenLP (Phase 15). The reading and mapping are in the
/// MxU.Slides.OpenLpImport project; this is only the app's side of it: run it off the UI thread,
/// show how far it has got, refresh the lists, and say what happened. Windows-only: the Mac has no twin.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// Imports songs and custom slides from an OpenLP <c>.sqlite</c> file, data folder or zip.
    /// Returns what happened (the window shows it), or null when nothing could be read at all,
    /// in which case <see cref="Problem"/> says why. Songs already in the library are left out, so
    /// running it twice is safe. Like the ProPresenter import, it is not one undo step: imported
    /// songs can be deleted from the library (they go to its Trash).
    /// </summary>
    public async Task<OpenLpImportResult?> ImportOpenLpAsync(string path, IProgress<OpenLpImportProgress>? progress = null)
    {
        if (IsImporting) return null;
        FlushPendingSave();
        IsImporting = true;
        Notice = "Importing from OpenLP…";
        // Made here, on the UI thread, so each report arrives back on the UI thread.
        var reports = new Progress<OpenLpImportProgress>(p =>
        {
            Notice = p.Total > 0 ? $"Importing from OpenLP: {p.Completed} of {p.Total}…" : $"Importing from OpenLP: {p.Phase}…";
            progress?.Report(p);
        });
        try
        {
            var importer = new OpenLpImporter(Library) { ThemeId = Settings.DefaultThemeId, LinesPerSlide = Settings.LinesPerSlide };
            var result = await importer.ImportAsync(path, reports);
            foreach (var warning in result.Warnings) Log.Info($"OpenLP import: {warning}");
            RefreshLibrary();
            // Open the first song that came in, so there is something to look at straight away.
            if (result.SongIds.Concat(result.CustomSlideIds).Select(id => Library.Get<Presentation>(id)).FirstOrDefault(p => p is not null) is { } first)
                Open(first, null, first.DefaultArrangementId);
            Notice = result.Headline();
            return result;
        }
        catch (OpenLpImportException ex)
        {
            // The operator chose something that isn't OpenLP's data: the message is written for them.
            Log.Warn($"OpenLP import: {ex.Message}");
            Problem = ex.Message;
            Notice = null;
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("OpenLP import failed", ex);
            Problem = $"OpenLP import failed: {ex.Message}";
            Notice = null;
            return null;
        }
        finally
        {
            IsImporting = false;
        }
    }
}
