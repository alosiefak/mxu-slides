namespace MxU.Slides.Core.Undo;

/// <summary>
/// Who answers Ctrl+Z. A port of the Mac's UndoMenuLogic (MenuCommandLogic.swift):
/// <list type="bullet">
/// <item>in an editor window, the editor's own history, always;</item>
/// <item>otherwise a text box being typed in, when it has something to undo;</item>
/// <item>otherwise the window's undo scope (the Mac's journal);</item>
/// <item>otherwise nothing.</item>
/// </list>
/// </summary>
public static class UndoMenuLogic
{
    public enum Target
    {
        /// <summary>The editor window's scope.</summary>
        Editor,

        /// <summary>The main window's scope.</summary>
        Journal,

        /// <summary>The focused text box's own undo (the Mac's responder chain).</summary>
        ResponderChain,

        Nothing,
    }

    public static Target TargetFor(bool editorOpen, bool fieldEditorCan, bool journalCan) =>
        editorOpen ? Target.Editor
        : fieldEditorCan ? Target.ResponderChain
        : journalCan ? Target.Journal
        : Target.Nothing;

    /// <summary>The menu item is greyed out only in an editor with nothing to undo (elsewhere a text box may still take it).</summary>
    public static bool Disabled(bool editorOpen, bool editorCan) => editorOpen && !editorCan;
}
