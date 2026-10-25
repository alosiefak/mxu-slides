using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Undo;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>
/// Ties the library's undo history (Core/Undo/UndoHistory.cs) to windows, the way the Mac gives
/// each window its own undo manager:
/// <list type="bullet">
/// <item>the window with the keyboard decides which scope a save goes to. A window with no scope of
/// its own (a rename box, a message) uses its owner's, so a rename typed into a dialog undoes in the
/// window that asked for it;</item>
/// <item>Ctrl+Z, Ctrl+Y and Ctrl+Shift+Z in an editor window undo its scope (UndoMenuLogic);</item>
/// <item>saves made in one piece of UI work (one click, one key press) are one step: the history
/// is told when the dispatcher has finished that work.</item>
/// </list>
/// <b>For an editor window</b>, one line does it: <c>UndoWindows.AttachEditor(this, library, "Theme editor", FlushEdits)</c>
/// in its constructor. Save through <see cref="LibraryStore"/> as usual; listen to
/// <see cref="UndoHistory.Restored"/> (or <see cref="LibraryStore.Changed"/>) to show what came back.
/// </summary>
public static class UndoWindows
{
    private static readonly ConditionalWeakTable<Window, UndoScope> Scopes = new();
    private static UndoHistory? history;

    /// <summary>Once, at start-up, on the UI thread.</summary>
    public static void Initialize(LibraryStore library)
    {
        history = library.Undo;
        var dispatcher = Dispatcher.CurrentDispatcher;
        // Normal priority runs before the next key press or click is handled, so two quick
        // clicks are two steps, while everything one handler saves is one.
        library.Undo.EndOfTurn = end => dispatcher.BeginInvoke(end, DispatcherPriority.Normal);
        // Keyboard focus moving into a window means it's the one being worked in now.
        EventManager.RegisterClassHandler(typeof(Window), Keyboard.GotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler((sender, _) => FocusWindow(sender as Window)), handledEventsToo: true);
    }

    /// <summary>The main window: its scope is the default for every window without one of its own.</summary>
    public static void AttachMain(Window window, UndoScope scope)
    {
        Scopes.AddOrUpdate(window, scope);
        window.Activated += (_, _) => FocusWindow(window);
        if (window.IsActive || history?.Focused is null) history?.Focus(scope);
    }

    /// <summary>
    /// Gives an editor window its own undo scope: Ctrl+Z, Ctrl+Y and Ctrl+Shift+Z undo and redo it,
    /// and closing the window forgets it. <paramref name="settle"/>, if given, runs before each undo
    /// or redo, to save anything the editor still has waiting.
    /// </summary>
    /// <param name="handleKeys">False for a window that answers Ctrl+Z itself (the timeline editor has its own history).</param>
    public static UndoScope AttachEditor(Window window, LibraryStore library, string name, Action? settle = null, bool handleKeys = true)
    {
        var scope = library.Undo.NewScope(name);
        Scopes.AddOrUpdate(window, scope);
        if (settle is not null) scope.Settling += settle;
        window.Activated += (_, _) => FocusWindow(window);
        window.Closed += (_, _) => scope.Dispose();
        if (handleKeys) window.PreviewKeyDown += (_, e) => HandleKey(e, scope, editor: true);
        if (window.IsActive) library.Undo.Focus(scope);
        return scope;
    }

    /// <summary>The scope a window's saves go to: its own, else its owner's, and so on.</summary>
    public static UndoScope? ScopeFor(Window? window)
    {
        for (var w = window; w is not null; w = w.Owner)
        {
            if (Scopes.TryGetValue(w, out var scope) && !IsClosed(scope)) return scope;
        }
        return null;
    }

    private static bool IsClosed(UndoScope scope) => history is { } h && !h.Scopes.Contains(scope);

    private static void FocusWindow(Window? window)
    {
        if (history is null || window is null) return;
        // Windows that never edit anything (outputs, pop-out previews) leave the focus where it was.
        if (ScopeFor(window) is { } scope) history.Focus(scope);
    }

    /// <summary>
    /// Handles Ctrl+Z, Ctrl+Y and Ctrl+Shift+Z for <paramref name="scope"/>, as the Mac routes them
    /// (UndoMenuLogic): an editor window always undoes its scope; elsewhere a text box being typed
    /// in keeps its own undo while it has something to undo. Returns true when the key was taken.
    /// </summary>
    public static bool HandleKey(KeyEventArgs e, UndoScope scope, bool editor)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        var undo = key == Key.Z && modifiers == ModifierKeys.Control;
        var redo = key == Key.Y && modifiers == ModifierKeys.Control || key == Key.Z && modifiers == (ModifierKeys.Control | ModifierKeys.Shift);
        if (!undo && !redo) return false;
        var box = Keyboard.FocusedElement as TextBoxBase;
        var fieldCan = box is { IsReadOnly: false } && (undo ? box.CanUndo : box.CanRedo);
        switch (UndoMenuLogic.TargetFor(editor, fieldCan, undo ? scope.CanUndo : scope.CanRedo))
        {
            case UndoMenuLogic.Target.ResponderChain:
                return false; // the text box undoes its own typing
            case UndoMenuLogic.Target.Editor or UndoMenuLogic.Target.Journal:
                Run(scope, undo);
                e.Handled = true;
                return true;
            default:
                e.Handled = true;
                return true;
        }
    }

    /// <summary>
    /// Undoes (or redoes) once. A text box with typing not yet sent to its document sends it first,
    /// so that typing is a step of its own and is the one undone. A problem is logged and shown,
    /// never thrown at the show.
    /// </summary>
    public static bool Run(UndoScope scope, bool undo)
    {
        if (Keyboard.FocusedElement is TextBox box)
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        try
        {
            return undo ? scope.Undo() : scope.Redo();
        }
        catch (Exception ex)
        {
            Log.Error($"{(undo ? "Undo" : "Redo")} in {scope.Name} failed", ex);
            MessageBox.Show($"Couldn't {(undo ? "undo" : "redo")}: {ex.Message}", "MxU Slides", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }
}
