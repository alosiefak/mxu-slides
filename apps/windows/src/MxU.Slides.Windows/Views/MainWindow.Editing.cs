using System.Windows.Input;
using MxU.Slides.Windows.Infrastructure;

namespace MxU.Slides.Windows.Views;

/// <summary>The main window's editing keys: undo and redo, the clipboard, find and replace.</summary>
public partial class MainWindow
{
    /// <summary>Called once from the constructor.</summary>
    private void AttachEditing()
    {
        // This window's saves are undone in the main scope; each editor window has its own.
        UndoWindows.AttachMain(this, model.UndoScope);
    }

    /// <summary>Handles an editing key before the show's keys see it. True when it was taken.</summary>
    private bool HandleEditKeys(KeyEventArgs e) => UndoWindows.HandleKey(e, model.UndoScope, editor: false);
}
