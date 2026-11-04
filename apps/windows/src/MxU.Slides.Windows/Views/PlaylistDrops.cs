using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MxU.Slides.Core.Editing;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// Dragging songs within a playlist (a PORTING "not ported yet" item until Phase 7): a song's row
/// drags to a new place, with a line where it will land, and a song dragged in from the Music tab
/// goes in where it's dropped. The Mac does this in its playlist editor (AppModel.handlePlaylistDrop,
/// movePlaylistEntries); the moves themselves are PlaylistLibrary's, ported in Phase 5.
/// </summary>
public static class PlaylistDrops
{
    private const string EntryFormat = "mxu/playlist-entry";

    /// <summary>Makes a song's row draggable within its playlist.</summary>
    public static void Draggable(FrameworkElement row, string entryId)
    {
        row.Tag = entryId;
        Point pressedAt = default;
        var pressed = false;
        row.PreviewMouseLeftButtonDown += (_, e) =>
        {
            pressed = e.ClickCount == 1;
            pressedAt = e.GetPosition(row);
        };
        row.PreviewMouseLeftButtonUp += (_, _) => pressed = false;
        row.MouseMove += (_, e) =>
        {
            if (!pressed || e.LeftButton != MouseButtonState.Pressed) return;
            var moved = e.GetPosition(row) - pressedAt;
            if (Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            pressed = false;
            var data = new DataObject();
            data.SetData(EntryFormat, entryId);
            DragDrop.DoDragDrop(row, data, DragDropEffects.Move);
        };
    }

    /// <summary>Lets a playlist's song list take drops: its own rows moving, and songs from the Music tab.</summary>
    public static void Attach(Panel list, string playlistId, MainViewModel model)
    {
        list.AllowDrop = true;
        list.Background ??= System.Windows.Media.Brushes.Transparent;
        list.DragOver += (_, e) =>
        {
            var takes = e.Data.GetDataPresent(EntryFormat) || EditorDragDrop.LibraryItem(e.Data)?.Kind == typeof(AudioItem);
            e.Effects = takes ? e.Data.GetDataPresent(EntryFormat) ? DragDropEffects.Move : DragDropEffects.Copy : DragDropEffects.None;
            if (takes)
            {
                var (_, _, y) = Spot(list, e);
                DropFeedback.For(list)?.HorizontalLine(y, 0, list.ActualWidth);
            }
            e.Handled = true;
        };
        list.DragLeave += (_, _) => DropFeedback.For(list)?.Clear();
        list.Drop += (_, e) =>
        {
            DropFeedback.For(list)?.Clear();
            var (before, index, _) = Spot(list, e);
            if (e.Data.GetData(EntryFormat) is string entryId)
            {
                if (entryId != before) model.MoveTrackBefore(playlistId, entryId, before);
            }
            else if (EditorDragDrop.LibraryItem(e.Data) is { } song && song.Kind == typeof(AudioItem))
            {
                model.InsertIntoPlaylist(playlistId, song.Id, index);
            }
            e.Handled = true;
        };
    }

    /// <summary>The row a drop lands before (null: the end), its position among the rows, and where the line goes.</summary>
    private static (string? BeforeEntryId, int Index, double LineY) Spot(Panel list, DragEventArgs e)
    {
        var point = e.GetPosition(list);
        var rows = list.Children.OfType<FrameworkElement>().Where(c => c.Tag is string && c.IsVisible).ToList();
        for (var i = 0; i < rows.Count; i++)
        {
            var top = rows[i].TranslatePoint(new Point(0, 0), list).Y;
            if (point.Y < top + rows[i].ActualHeight / 2) return ((string)rows[i].Tag, i, top - 1);
        }
        var last = rows.LastOrDefault();
        return (null, rows.Count, last is null ? point.Y : last.TranslatePoint(new Point(0, last.ActualHeight), list).Y);
    }
}
