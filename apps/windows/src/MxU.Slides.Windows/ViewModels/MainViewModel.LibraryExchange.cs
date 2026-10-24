using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using SceneTransition = MxU.Slides.Windows.Rendering.SceneTransition;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.Views;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// Moving presentations between computers, Mac or Windows, as .mxuslides files: a
/// presentation with its themes, media and fonts in one file (see SlidesPresentationFile.cs).
/// Port of the Mac's PresentationFileImport.swift and the .mxuslides half of
/// PresentationExportPanel.swift. Unpacking, copying and zipping run off the UI thread; the
/// notice bar says what's happening and what came in.
/// </summary>
public sealed partial class MainViewModel
{
    public static bool IsPresentationFile(string path) =>
        Path.GetExtension(path).Equals("." + SlidesPresentationFile.FileExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>"Already in your library": Keep Both, Replace, or Skip (null). The self-test answers it itself.</summary>
    public Func<string, SlidesPresentationFile.Conflict?> AskPresentationConflict { get; set; } = LibraryDialogs.AskPresentationConflict;

    /// <summary>The library's fonts folder, where fonts that came in a .mxuslides go (and PowerPoint's embedded ones).</summary>
    public string LibraryFontsFolder => Path.Combine(Library.Root, FontSharing.LibraryFolderName);

    /// <summary>
    /// Imports .mxuslides files, asking about each one already in the library, then opens the
    /// last one. Returns the ids of the presentations that landed.
    /// </summary>
    public async Task<List<string>> ImportPresentationFilesAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || IsImporting) return [];
        FlushPendingSave();
        IsImporting = true;
        var lines = new List<string>();
        var warnings = new List<string>();
        var landed = new List<string>();
        var replaced = new List<string>();
        FontIndex? fonts = null;
        try
        {
            for (var index = 0; index < paths.Count; index++)
            {
                var path = paths[index];
                Notice = $"Importing {Path.GetFileNameWithoutExtension(path)}" + (paths.Count > 1 ? $" ({index + 1} of {paths.Count})…" : "…");
                var scratch = Path.Combine(Path.GetTempPath(), $"mxuslides-{Guid.NewGuid():N}");
                try
                {
                    var contents = await OffUiThread(() => SlidesPresentationFile.Read(path, scratch));
                    var conflict = SlidesPresentationFile.IsInLibrary(contents, Library)
                        ? AskPresentationConflict(contents.Manifest.Name)
                        : SlidesPresentationFile.Conflict.KeepBoth;
                    if (conflict is not { } choice)
                    {
                        lines.Add($"“{contents.Manifest.Name}” skipped");
                        continue;
                    }
                    var result = await OffUiThread(() =>
                    {
                        // Which fonts this PC has, read once per import (Windows', this user's, the library's).
                        fonts ??= FontIndex.Scan([FontSharing.SystemFontsFolder, FontSharing.UserFontsFolder, LibraryFontsFolder]);
                        return SlidesPresentationFile.ImportContents(contents, Library, choice, fonts.IsAvailable);
                    });
                    landed.Add(result.PresentationId);
                    var added = new List<string>();
                    if (result.ThemesAdded > 0) added.Add(Count(result.ThemesAdded, "theme"));
                    if (result.MediaAdded > 0) added.Add(Count(result.MediaAdded, "media file"));
                    if (result.FontsAdded > 0) added.Add(Count(result.FontsAdded, "font"));
                    lines.Add($"“{result.Name}”" + (added.Count > 0 ? " with " + string.Join(", ", added) : ""));
                    warnings.AddRange(result.Missing.Select(m => $"{result.Name}: the file didn't include {m}"));
                    if (choice == SlidesPresentationFile.Conflict.Replace) replaced.Add(result.PresentationId);
                }
                catch (SlidesPresentationFile.FileException e)
                {
                    warnings.Add($"{Path.GetFileName(path)}: {e.Message}");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    Log.Error($"Importing {path} failed", e);
                    warnings.Add($"{Path.GetFileName(path)}: {e.Message}");
                }
                finally
                {
                    TryDeleteFolder(scratch);
                }
            }
            RefreshThemes();
            RefreshLibrary();
            RefreshMedia();
            RefreshAudio();
            // A replaced presentation that's live shows its new slides at once, as an edit would.
            foreach (var id in replaced)
            {
                if (Library.Get<Presentation>(id) is not { } fresh) continue;
                Show.PresentationEdited(fresh);
                if (Show.Live?.Presentation?.Id == id) RenderOutputs(SceneTransition.Cut, refreshOnly: true);
            }
            if (landed.LastOrDefault() is { } last && Library.Get<Presentation>(last) is { } opened)
            {
                SelectedServiceItem = null;
                Open(opened, null, opened.DefaultArrangementId);
                suppressLibrarySelection = true;
                SelectedLibraryEntry = LibraryItems.FirstOrDefault(e => e.Presentation.Id == last);
                suppressLibrarySelection = false;
            }
            Notice = landed.Count > 0
                ? $"Imported {Count(landed.Count, "presentation")}: {string.Join("; ", lines)}."
                : lines.Count > 0 ? string.Join("; ", lines) + "." : null;
            if (warnings.Count > 0) Problem = string.Join("  ·  ", warnings);
            return landed;
        }
        finally
        {
            IsImporting = false;
        }
    }

    /// <summary>
    /// Writes a presentation, with its themes, media and the fonts it needs, as a .mxuslides
    /// file the Mac and other Windows PCs can import. Media whose file isn't on this PC is left
    /// out and named. Returns what was written, or null if it couldn't be.
    /// </summary>
    public async Task<SlidesPresentationFile.ExportSummary?> ExportPresentationAsync(Presentation presentation, string destination)
    {
        FlushPendingSave();
        // Copies, taken here, so editing can carry on while the file is written.
        if (SlidesPresentationFile.Bundle(Library, presentation.Id) is not { } bundle) return null;
        Notice = $"Saving “{Path.GetFileName(destination)}”…";
        try
        {
            var summary = await OffUiThread(() =>
            {
                var fonts = SlidesPresentationFile.FontFiles(bundle, FontIndex.Scan([LibraryFontsFolder, FontSharing.UserFontsFolder]));
                return SlidesPresentationFile.Write(bundle, Library.Blobs, fonts, destination);
            });
            Notice = $"Saved “{Path.GetFileName(destination)}”: {Count(bundle.Presentation.Slides.Count, "slide")}"
                + (summary.FontCount > 0 ? $", {Count(summary.FontCount, "font file")}" : "") + ".";
            if (summary.MissingFiles.Count > 0) Problem = $"Not on this PC, so left out: {string.Join(", ", summary.MissingFiles)}";
            return summary;
        }
        catch (Exception e) when (e is SlidesPresentationFile.FileException or IOException or UnauthorizedAccessException)
        {
            Log.Error($"Exporting {presentation.Name} failed", e);
            Notice = null;
            Problem = $"Couldn't save “{Path.GetFileName(destination)}”: {e.Message}";
            return null;
        }
    }

    private static string Count(int n, string thing) => $"{n} {thing}{(n == 1 ? "" : "s")}";

    private static void TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A leftover scratch folder in Temp is harmless.
        }
    }
}
