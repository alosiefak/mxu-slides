using System.Windows;
using MxU.Slides.Core.Library;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// Files the app is asked to open from outside: double-clicked in Explorer, "Open with", or handed
/// over by a second copy of the app (Infrastructure/FileHandoff.cs). The window comes to the front;
/// presentations, PowerPoint, ProPresenter and lyrics files go through the usual import (the same
/// path a file dropped on the library takes); a backup opens the restore preview, and never
/// restores by itself. The Mac's twin is PresentationFileOpener.
/// </summary>
public partial class MainWindow
{
    internal async void OpenFromOutside(IReadOnlyList<string> files)
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        // Windows lets a window take the front only in a moment it allows: briefly topmost does it.
        Topmost = true;
        Topmost = false;
        Focus();
        if (files.Count == 0) return;

        var present = files.Where(File.Exists).ToList();
        var missing = files.Except(present).Select(Path.GetFileName).ToList();
        var backups = present.Where(f => OpenRequests.KindOf(f) == OpenRequests.Kind.Backup).ToList();
        var imports = present.Where(f => OpenRequests.KindOf(f) is not (OpenRequests.Kind.Backup or OpenRequests.Kind.Unsupported)).ToList();
        var unsupported = present.Where(f => OpenRequests.KindOf(f) == OpenRequests.Kind.Unsupported).Select(Path.GetFileName).ToList();
        if (missing.Count > 0 || unsupported.Count > 0)
            model.Problem = string.Join(" ", new[]
            {
                missing.Count > 0 ? $"Couldn't find {string.Join(", ", missing)}." : null,
                unsupported.Count > 0 ? $"MxU Slides doesn't open {string.Join(", ", unsupported)}." : null,
            }.OfType<string>());
        if (imports.Count > 0) await ImportFiles(imports);
        foreach (var backup in backups) await RestoreWindow.OpenAsync(this, model, backup);
    }

    // MARK: From the welcome guide

    /// <summary>Import…, from the welcome guide (over it, as it's on screen).</summary>
    internal async void ImportFromGuide(Window owner)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import songs and presentations",
            Multiselect = true,
            Filter = $"Everything MxU Slides can import|*.mxuslides;*.pptx;{ProPresenterPatterns};{LyricsPatterns}|All files|*.*",
        };
        if (dialog.ShowDialog(owner) == true) await ImportFiles(dialog.FileNames);
    }

    /// <summary>A new song from pasted words, from the welcome guide.</summary>
    internal void NewSongFromGuide(Window owner)
    {
        model.FlushPendingSave();
        var editor = new LyricsEditorWindow(model, null) { Owner = owner };
        if (editor.ShowDialog() == true && editor.Result is { } song) model.SaveSong(song, isNew: true);
    }

    private void Help_Click(object sender, RoutedEventArgs e) => HelpMenu.Open(this, model, HelpButton);
}
