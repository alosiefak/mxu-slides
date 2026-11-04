using System.Windows;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The media tile's Locate / Replace everywhere / Transcode commands. The work is in
/// MainViewModel.MissingMedia.cs. (Dropping files on the library and exporting are
/// Views/LibraryPanel.xaml.cs and MainWindow.LibraryPanel.cs since Phase 8.)
/// </summary>
public partial class MainWindow
{
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
