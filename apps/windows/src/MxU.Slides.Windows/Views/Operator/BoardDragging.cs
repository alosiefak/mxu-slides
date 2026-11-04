using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MxU.Slides.Core.Automation;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// Dragging on a board (Phase 8): the alerts, the timers, the action combos and the scheduler's
/// triggers. Drag a row above another row to move it there (into that row's folder, if it's in
/// one), onto a folder's name to file it in that folder, or below the last row to move it to the
/// end; drag a folder to move it with everything in it. A line shows where it will land. Each drop
/// is one of the board's own moves (Core's <see cref="BoardDrops"/>), so nothing is ever lost or
/// doubled. Move up, Move down and Folder › in the right-click menus still work as before.
/// Port of the Mac's ControlBoardFolders.swift drops; nothing drags while volunteer mode is on.
/// </summary>
internal sealed class BoardDragging(string board, Func<bool> allowed, Action<BoardMove> apply)
{
    // Each board has its own format, so a timer can't be dropped on the alerts.
    private readonly string format = "mxu/board/" + board;
    private DropLine? line;
    private FrameworkElement? lineOn;
    private Point start;
    private FrameworkElement? pressed;

    /// <summary>Makes an element draggable as an item or a folder.</summary>
    public void Source(FrameworkElement element, BoardDrag drag)
    {
        element.PreviewMouseLeftButtonDown += (_, e) =>
        {
            pressed = IsControl(e.OriginalSource as DependencyObject) ? null : element;
            start = e.GetPosition(element);
        };
        element.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || pressed != element || !allowed()) return;
            var moved = e.GetPosition(element) - start;
            if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            pressed = null;
            var payload = drag is BoardDrag.Folder ? "folder:" + drag.Id : "item:" + drag.Id;
            try { DragDrop.DoDragDrop(element, new DataObject(format, payload), DragDropEffects.Move); }
            finally { ClearLine(); }
            e.Handled = true;
        };
    }

    /// <summary>
    /// Makes an element a place to drop: above a row, onto a folder's name, or (a whole list, under
    /// its rows) the end. The line shows on <paramref name="edge"/>, else where the drop lands.
    /// </summary>
    public void Target(FrameworkElement element, BoardDropTarget target, DropEdge? edge = null)
    {
        element.AllowDrop = true;
        element.DragOver += (_, e) =>
        {
            e.Handled = true;
            if (Move(e, target) is null)
            {
                e.Effects = DragDropEffects.None;
                ClearLine();
                return;
            }
            e.Effects = DragDropEffects.Move;
            // The same place as last time: leave the line be (drags report many moves a second).
            if (lineOn == element) return;
            ClearLine();
            line = DropLine.Show(element, edge ?? (target is BoardDropTarget.OntoFolder ? DropEdge.Around : DropEdge.Top));
            lineOn = element;
        };
        element.DragLeave += (_, _) => ClearLine();
        element.Drop += (_, e) =>
        {
            e.Handled = true;
            ClearLine();
            if (Move(e, target) is { } move) apply(move);
        };
    }

    /// <summary>The strip under the last row: drops there move to the end of the board.</summary>
    public Border EndStrip()
    {
        var strip = new Border { Height = 14, Background = Brushes.Transparent };
        Target(strip, new BoardDropTarget.End());
        return strip;
    }

    /// <summary>What dropping here would do, or null (another board's drag, or a row dropped on itself).</summary>
    private BoardMove? Move(DragEventArgs e, BoardDropTarget target)
    {
        if (!allowed() || e.Data.GetData(format) is not string payload) return null;
        BoardDrag? drag = payload.StartsWith("folder:", StringComparison.Ordinal) ? new BoardDrag.Folder(payload[7..])
            : payload.StartsWith("item:", StringComparison.Ordinal) ? new BoardDrag.Item(payload[5..]) : null;
        return drag is null ? null : BoardDrops.Resolve(drag, target);
    }

    private void ClearLine()
    {
        line?.Remove();
        line = null;
        lineOn = null;
    }

    /// <summary>Presses on a button, a text box or a picker work those, not a drag.</summary>
    private static bool IsControl(DependencyObject? node)
    {
        for (var current = node; current is not null; current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is ButtonBase or TextBoxBase or ComboBox or Slider) return true;
        return false;
    }
}
