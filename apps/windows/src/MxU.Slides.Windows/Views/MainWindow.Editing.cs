using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The main window's editing keys: undo and redo, the clipboard, picking slides, find and
/// replace. A text box being typed in keeps its own cut, copy and paste; the canvas (in Edit
/// mode) works on objects; otherwise the keys work on the grid's picked slides.
/// </summary>
public partial class MainWindow
{
    /// <summary>Called once from the constructor.</summary>
    private void AttachEditing()
    {
        // This window's saves are undone in the main scope; each editor window has its own.
        UndoWindows.AttachMain(this, model.UndoScope);
    }

    /// <summary>Handles an editing key before the show's keys see it. True when it was taken.</summary>
    private bool HandleEditKeys(KeyEventArgs e)
    {
        if (UndoWindows.HandleKey(e, model.UndoScope, editor: false)) return true;
        var focused = Keyboard.FocusedElement;
        // A text box keeps its own clipboard; a list (the service, the library) isn't the slides.
        if (focused is TextBoxBase or PasswordBox or ComboBox or ListBoxItem or ListBox) return false;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var target = model.TargetFor(focused is SlideCanvasEditor);
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (key)
            {
                case Key.C:
                    model.Copy(target);
                    e.Handled = true;
                    return true;
                case Key.X:
                    model.Cut(target);
                    e.Handled = true;
                    return true;
                case Key.V:
                    _ = model.Paste(target);
                    e.Handled = true;
                    return true;
                case Key.D:
                    model.Duplicate(target);
                    e.Handled = true;
                    return true;
                case Key.A when model.IsEditing && target == EditTarget.Slides:
                    model.SelectAllSlidesCommand.Execute(null);
                    e.Handled = true;
                    return true;
            }
        }
        // Delete removes the picked slides, in Edit mode only: a stray key mid-service does nothing.
        if (key is Key.Delete && Keyboard.Modifiers == ModifierKeys.None && model.IsEditing && target == EditTarget.Slides)
        {
            model.DeletePickedSlides();
            e.Handled = true;
            return true;
        }
        return false;
    }

    /// <summary>A click on a tile with Ctrl or Shift picks it (MainViewModel.SlidePicks.cs). True when it did.</summary>
    private bool PickTile(SlideTileViewModel tile) =>
        model.PickTile(tile, Keyboard.Modifiers.HasFlag(ModifierKeys.Control), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));

    /// <summary>The grid's right-click menu: the clipboard and duplicate for the picked slides.</summary>
    private void AddSlideEditItems(ContextMenu menu)
    {
        var count = model.PickedSlideIds.Count;
        var slides = count == 1 ? "" : $" {count} Slides";
        menu.Items.Add(EditBar.Item($"Cut{slides}", "Ctrl+X", count > 0, () => model.Cut(EditTarget.Slides)));
        menu.Items.Add(EditBar.Item($"Copy{slides}", "Ctrl+C", count > 0, () => model.Copy(EditTarget.Slides)));
        menu.Items.Add(EditBar.Item("Paste", "Ctrl+V", AppClipboard.HasAnything(), () => _ = model.Paste(EditTarget.Slides)));
        menu.Items.Add(EditBar.Item($"Duplicate{slides}", "Ctrl+D", count > 0, () => model.Duplicate(EditTarget.Slides)));
        menu.Items.Add(EditBar.Item($"Delete{slides}", model.IsEditing ? "Delete" : null, count > 0, () => model.DeletePickedSlides()));
    }
}
