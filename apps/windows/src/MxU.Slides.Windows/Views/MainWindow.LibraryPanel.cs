using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The main window's part in the library's Phase 8 actions: what the library panel and the
/// media bin ask of the window (the lyrics editor, renaming, exporting), and picking media tiles
/// with Ctrl+click and Shift+click, with a batch menu when several are picked.
/// </summary>
public partial class MainWindow
{
    internal void EditLyricsFromLibrary(Presentation presentation)
    {
        if (model.CurrentPresentation?.Id != presentation.Id) model.Open(presentation, null, null);
        OpenLyricsEditor(presentation);
    }

    internal void RenamePresentationFromLibrary(Presentation presentation)
    {
        if (InputDialog.Ask(this, "Rename", "Name", presentation.Name) is { } name) model.RenamePresentation(presentation, name);
    }

    /// <summary>Export… for one presentation or several (Views/ExportWindow.cs asks how, then where).</summary>
    internal void ExportPresentations(IReadOnlyList<Presentation> presentations)
    {
        if (presentations.Count > 0) ExportWindow.Open(this, model, presentations);
    }

    internal async void ExportMediaFiles(IReadOnlyList<MediaItem> items)
    {
        var dialog = new OpenFolderDialog { Title = items.Count == 1 ? $"Copy “{items[0].Name}” to a folder" : $"Copy {items.Count} media files to a folder" };
        if (dialog.ShowDialog(this) == true) await model.ExportMediaFilesAsync(items, dialog.FolderName);
    }

    // MARK: Picking media tiles

    /// <summary>True when the click was Ctrl+click (add or take away one) or Shift+click (a run of them): it picks, and nothing goes live.</summary>
    private bool PickedMediaTile(object sender)
    {
        if (ContextOf<MediaTileViewModel>(sender) is not { } tile) return false;
        var modifiers = Keyboard.Modifiers;
        if (modifiers.HasFlag(ModifierKeys.Control)) model.PickMedia(tile, ListMultiSelect.Gesture.Toggle);
        else if (modifiers.HasFlag(ModifierKeys.Shift)) model.PickMedia(tile, ListMultiSelect.Gesture.Extend);
        else
        {
            // A plain click starts again from this tile (a later Shift+click spans from it), and goes on as before.
            model.PickMedia(tile, ListMultiSelect.Gesture.Replace);
            return false;
        }
        return true;
    }

    private const string MediaMenuMarker = "library-depth";

    /// <summary>
    /// The tile's menu, as it opens. With several tiles picked (this one among them) it gives way
    /// to the batch menu, opened in its place; otherwise it gains a favourite, tags, a folder,
    /// Duplicate and "Copy the file to…" (taken out again before it next opens).
    /// </summary>
    private void MediaMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || (menu.PlacementTarget as FrameworkElement)?.DataContext is not MediaTileViewModel tile) return;
        foreach (var old in menu.Items.OfType<FrameworkElement>().Where(i => Equals(i.Tag, MediaMenuMarker)).ToList()) menu.Items.Remove(old);
        var batch = model.MediaBatch(tile);
        if (batch.Count > 1)
        {
            menu.IsOpen = false;
            var many = new ContextMenu { PlacementTarget = menu.PlacementTarget, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
            LibraryMenus.ForMediaBatch(many, this, model, batch);
            many.IsOpen = true;
            return;
        }
        // After "Actions…", with the tile's other settings.
        var at = menu.Items.OfType<object>().ToList().FindIndex(i => i is MenuItem { Header: string h } && h.StartsWith("Actions", StringComparison.Ordinal)) + 1;
        if (at <= 0) at = menu.Items.Count;
        var added = new List<object> { EditorKit.Separator() };
        added.AddRange(LibraryMenus.ForOneMedia(this, model, tile.Item));
        foreach (var item in added)
        {
            if (item is FrameworkElement element) element.Tag = MediaMenuMarker;
            menu.Items.Insert(at++, item);
        }
    }
}
