using MxU.Slides.Core.Undo;

namespace MxU.Slides.Core.Tests.Undo;

/// <summary>Ported from the Mac's MenuCommandLogicTests.swift (undo routing).</summary>
public sealed class UndoMenuLogicTests
{
    [Fact]
    public void AnOpenEditorAlwaysTakesUndo()
    {
        Assert.Equal(UndoMenuLogic.Target.Editor, UndoMenuLogic.TargetFor(editorOpen: true, fieldEditorCan: true, journalCan: true));
        Assert.Equal(UndoMenuLogic.Target.Editor, UndoMenuLogic.TargetFor(editorOpen: true, fieldEditorCan: false, journalCan: false));
    }

    [Fact]
    public void ATextBoxComesBeforeTheJournal()
    {
        Assert.Equal(UndoMenuLogic.Target.ResponderChain, UndoMenuLogic.TargetFor(editorOpen: false, fieldEditorCan: true, journalCan: true));
        Assert.Equal(UndoMenuLogic.Target.Journal, UndoMenuLogic.TargetFor(editorOpen: false, fieldEditorCan: false, journalCan: true));
    }

    [Fact]
    public void NothingToUndo() =>
        Assert.Equal(UndoMenuLogic.Target.Nothing, UndoMenuLogic.TargetFor(editorOpen: false, fieldEditorCan: false, journalCan: false));

    [Fact]
    public void OnlyAnEditorWithNothingToUndoGreysTheMenuOut()
    {
        Assert.True(UndoMenuLogic.Disabled(editorOpen: true, editorCan: false));
        Assert.False(UndoMenuLogic.Disabled(editorOpen: true, editorCan: true));
        Assert.False(UndoMenuLogic.Disabled(editorOpen: false, editorCan: false));
    }
}
