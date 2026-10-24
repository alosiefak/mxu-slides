using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using MxU.Slides.Core.Library;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The main window's library-exchange controls: dropping files on the library, exporting a
/// presentation as .mxuslides, and the media tile's Locate / Replace everywhere / Transcode
/// commands. The work is in MainViewModel.LibraryExchange.cs and MainViewModel.MissingMedia.cs.
/// </summary>
public partial class MainWindow
{
    // MARK: Dropping files on the library

    private void LibraryList_DragOver(object sender, DragEventArgs e)
    {
        // Files from Explorer are imported; the library's own drag (a song onto the service) is left alone.
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void LibraryList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        e.Handled = true;
        // After the drop returns, so Explorer isn't kept waiting while a question is asked.
        Dispatcher.BeginInvoke(async () => await ImportFiles(files), DispatcherPriority.Background);
    }

    // MARK: Export

    private async void ExportPresentation_Click(object sender, RoutedEventArgs e)
    {
        if (model.CurrentPresentation is not { } presentation) return;
        var dialog = new SaveFileDialog
        {
            Title = $"Export “{presentation.Name}”",
            FileName = SlidesPresentationFile.FileName(presentation),
            DefaultExt = "." + SlidesPresentationFile.FileExtension,
            Filter = $"MxU Slides presentation (*.{SlidesPresentationFile.FileExtension})|*.{SlidesPresentationFile.FileExtension}",
            AddExtension = true,
        };
        if (dialog.ShowDialog(this) == true) await model.ExportPresentationAsync(presentation, dialog.FileName);
    }

    // MARK: Media tiles

    private void MediaLocate_Click(object sender, RoutedEventArgs e) => LocateMissingMediaWindow.Open(this, model);

    private async void MediaReplace_Click(object sender, RoutedEventArgs e)
    {
        if (ContextOf<MediaTileViewModel>(sender) is { } tile) await model.ReplaceMediaFileEverywhereAsync(tile.Item);
    }

    private async void MediaTranscode_Click(object sender, RoutedEventArgs e)
    {
        if (ContextOf<MediaTileViewModel>(sender) is { } tile) await model.TranscodeMediaAsync(tile.Item);
    }
}
