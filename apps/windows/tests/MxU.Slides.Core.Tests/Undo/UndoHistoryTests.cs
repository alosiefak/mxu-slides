using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Undo;

namespace MxU.Slides.Core.Tests.Undo;

public sealed class UndoHistoryTests : UndoTest
{
    [Fact]
    public void AnEditUndoesAndRedoes()
    {
        SaveQuietly(Deck());
        var deck = Held();
        deck.Name = "Easter";
        Edit(deck);

        Assert.True(Scope.CanUndo);
        Assert.False(Scope.CanRedo);
        Assert.Equal("Undo Rename", Scope.UndoMenuText);

        Assert.True(Scope.Undo());
        Assert.Equal("Sunday", Held().Name);
        Assert.Equal("Sunday", OnDisk().Name);
        Assert.Equal("Redo Rename", Scope.RedoMenuText);

        Assert.True(Scope.Redo());
        Assert.Equal("Easter", Held().Name);
        Assert.Equal("Easter", OnDisk().Name);
    }

    [Fact]
    public void UndoKeepsTheObjectAWindowHolds()
    {
        SaveQuietly(Deck());
        var open = Held();
        open.Slides[0].Objects[0].Text = "Changed";
        Edit(open);
        Scope.Undo();
        // The main window's open presentation is still the library's, now holding the old text.
        Assert.Same(open, Held());
        Assert.Equal("One", open.Slides[0].Objects[0].Text);
    }

    [Fact]
    public void SavesOutsideAScopeAreNotRecorded()
    {
        Library.Save(Deck());
        SaveQuietly(Deck("other"));
        Assert.False(Scope.CanUndo);
        Assert.Null(Library.Undo.Current);
    }

    [Fact]
    public void SavingAnUnchangedDocumentIsNotAStep()
    {
        SaveQuietly(Deck());
        Edit(Held());
        Assert.False(Scope.CanUndo);
    }

    [Fact]
    public void RapidEditsCoalesceIntoOneStep()
    {
        SaveQuietly(Deck());
        foreach (var text in new[] { "O", "On", "One!", "One!!" })
        {
            Held().Slides[0].Objects[0].Text = text;
            Edit(Held());
            Clock.Advance(1);
        }
        Assert.Equal(["Typing"], Scope.UndoNames);
        Assert.True(Scope.Undo());
        Assert.Equal("One", TextOf("s1"));
        Assert.False(Scope.CanUndo);
        Assert.True(Scope.Redo());
        Assert.Equal("One!!", TextOf("s1"));
    }

    [Fact]
    public void DraggingCoalescesIntoOneMove()
    {
        SaveQuietly(Deck());
        // The first move of a themed box also gives it a size (the theme's): still "Move".
        var box = Held().Slides[0].Objects[0];
        (box.X, box.Y, box.Width, box.Height) = (10, 10, 400, 100);
        Edit(Held());
        for (var x = 20; x <= 60; x += 10)
        {
            Clock.Advance(0.05);
            box.X = x;
            Edit(Held());
        }
        Assert.Equal(["Move"], Scope.UndoNames);
        Scope.Undo();
        Assert.Null(Held().Slides[0].Objects[0].X);
    }

    [Fact]
    public void CoalescingBreaksOnAPauseAnotherObjectOrAStructuralChange()
    {
        SaveQuietly(Deck());
        void Type(string slide, string text)
        {
            Held().Slides.Single(s => s.Id == slide).Objects[0].Text = text;
            Edit(Held());
        }

        Type("s1", "a");
        Clock.Advance(6); // longer than the 5-second window
        Type("s1", "ab");
        Type("s2", "x"); // another box
        var deck = Held();
        deck.Slides.Add(Slide("s4", "Four")); // a structural change in between
        Edit(deck);
        Type("s2", "xy");

        var undos = 0;
        while (Scope.Undo()) undos++;
        Assert.Equal(5, undos);
        Assert.Equal(["One", "Two", "Three"], Held().Slides.Select(s => s.Objects[0].Text));
    }

    [Fact]
    public void ANewEditClearsRedo()
    {
        SaveQuietly(Deck());
        Held().Name = "A";
        Edit(Held());
        Scope.Undo();
        Assert.True(Scope.CanRedo);
        Held().Name = "B";
        Edit(Held());
        Assert.False(Scope.CanRedo);
    }

    [Fact]
    public void AnEditAfterAnUndoStartsAFreshStep()
    {
        SaveQuietly(Deck());
        Held().Slides[0].Objects[0].Text = "old";
        Edit(Held());
        Clock.Advance(10);
        Held().Slides[0].Objects[0].Text = "new";
        Edit(Held());
        Scope.Undo();
        Assert.Equal("old", TextOf("s1"));
        // Same box, within the window of the step under it: but an undo happened, so it's new.
        Held().Slides[0].Objects[0].Text = "newer";
        Edit(Held());
        Assert.Equal(2, Scope.UndoNames.Count);
        Scope.Undo();
        Assert.Equal("old", TextOf("s1"));
    }

    [Fact]
    public void ScopesAreKeptApart()
    {
        SaveQuietly(Deck());
        SaveQuietly(Deck("song"));
        var editor = Library.Undo.NewScope("Slide editor");

        Held().Name = "Main's edit";
        Edit(Held());
        Held("song").Name = "Editor's edit";
        Edit(Held("song"), editor);

        Assert.Equal(["Rename"], Scope.UndoNames);
        Assert.Equal(["Rename"], editor.UndoNames);
        editor.Undo();
        Assert.Equal("Sunday", Held("song").Name);
        Assert.Equal("Main's edit", Held().Name);
        Assert.True(Scope.CanUndo);
        Assert.False(editor.CanUndo);
    }

    [Fact]
    public void UndoingInOneScopeKeepsALaterEditFromAnother()
    {
        SaveQuietly(Deck());
        var editor = Library.Undo.NewScope("Slide editor");
        Held().Slides[0].Objects[0].Text = "Main typed this";
        Edit(Held());
        Held().Slides[1].Objects[0].Text = "The editor typed this";
        Edit(Held(), editor);

        Scope.Undo();
        Assert.Equal("One", TextOf("s1"));
        Assert.Equal("The editor typed this", TextOf("s2"));

        editor.Undo();
        Assert.Equal("Two", TextOf("s2"));
    }

    [Fact]
    public void ALaterEditToTheSameThingWins()
    {
        SaveQuietly(Deck());
        Held().Name = "Main's name";
        Edit(Held());
        // A remote renames it again: undoing the main window's rename mustn't throw that away.
        Held().Name = "Remote's name";
        SaveQuietly(Held());
        Assert.False(Scope.Undo());
        Assert.Equal("Remote's name", Held().Name);
    }

    [Fact]
    public void SeveralDocumentsChangedInOneStepUndoTogether()
    {
        SaveQuietly(Deck());
        SaveQuietly(Service("service", "deck"));
        using (Scope.Use())
        using (Scope.BeginGroup("Rename"))
        {
            Held().Name = "Easter";
            Library.Save(Held());
            var service = Library.Get<Service>("service")!;
            service.Items[0].Name = "Easter";
            Library.Save(service);
        }
        Assert.Equal(["Rename"], Scope.UndoNames);
        Scope.Undo();
        Assert.Equal("Sunday", Held().Name);
        Assert.Equal("deck", Library.Get<Service>("service")!.Items[0].Name);
        Scope.Redo();
        Assert.Equal("Easter", Library.Get<Service>("service")!.Items[0].Name);
    }

    [Fact]
    public void SavesInOnePieceOfUiWorkAreOneStep()
    {
        var pending = new Queue<Action>();
        Library.Undo.EndOfTurn = pending.Enqueue;
        SaveQuietly(Deck());
        SaveQuietly(Service("service", "deck"));

        // One click: the presentation and its service row are renamed.
        using (Scope.Use())
        {
            Held().Name = "Easter";
            Library.Save(Held());
            var service = Library.Get<Service>("service")!;
            service.Items[0].Name = "Easter";
            Library.Save(service);
        }
        while (pending.TryDequeue(out var end)) end();

        Assert.Single(Scope.UndoNames);
        Scope.Undo();
        Assert.Equal("Sunday", Held().Name);
        Assert.Equal("deck", Library.Get<Service>("service")!.Items[0].Name);
    }

    [Fact]
    public void ADeletedDocumentComesBackOnUndo()
    {
        SaveQuietly(Deck());
        var changes = new List<string>();
        Library.Changed += (_, id) => changes.Add(id);
        using (Scope.Use()) Library.Delete<Presentation>("deck");
        Assert.Null(Library.Get<Presentation>("deck"));
        Assert.Equal("Undo Delete Presentation", Scope.UndoMenuText);

        Scope.Undo();
        Assert.Equal(["One", "Two", "Three"], Held().Slides.Select(s => s.Objects[0].Text));
        Assert.Equal("Sunday", OnDisk().Name);
        Assert.Equal(["deck", "deck"], changes);

        Scope.Redo();
        Assert.Null(Library.Get<Presentation>("deck"));
        Assert.False(File.Exists(Library.DocumentPath(typeof(Presentation), "deck")));
        // Deleting is still never permanent: the deleted copy is in the Trash.
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(Library.TrashDirectory, "Presentations")));
    }

    [Fact]
    public void UndoingAnAddMovesItToTheTrash()
    {
        Edit(Deck("new"));
        Assert.Equal("Undo Add Presentation", Scope.UndoMenuText);
        Scope.Undo();
        Assert.Null(Library.Get<Presentation>("new"));
        Scope.Redo();
        Assert.NotNull(Library.Get<Presentation>("new"));
    }

    [Fact]
    public void WindowsExtrasUndoWithTheirDocument()
    {
        SaveQuietly(Deck());
        using (Scope.Use())
        {
            using var group = Scope.BeginGroup("Transition");
            Held().Slides[0].Transition = new Transition { TransitionKind = TransitionKind.Dissolve, DurationSeconds = 1 };
            Library.Save(Held());
            Library.Extras.Save("slide transitions", "s1", new { kind = "push" });
        }
        Assert.Equal(["Transition"], Scope.UndoNames);
        Scope.Undo();
        Assert.Null(Held().Slides[0].Transition);
        Assert.Empty(Library.Extras.Owners("slide transitions"));
        Scope.Redo();
        Assert.NotNull(Held().Slides[0].Transition);
        Assert.Equal(["s1"], Library.Extras.Owners("slide transitions"));
    }

    [Fact]
    public void AStepThatCanNoLongerApplyIsDroppedAndTheOneUnderItUndone()
    {
        SaveQuietly(Deck());
        SaveQuietly(Deck("gone"));
        Held().Name = "Live";
        Edit(Held());
        Held("gone").Name = "Doomed";
        Edit(Held("gone"));
        // Deleted elsewhere since (a remote): its step has nothing left to put back.
        using (Library.Undo.Unrecorded()) Library.Delete<Presentation>("gone");

        Assert.True(Scope.Undo());
        Assert.Equal("Sunday", Held().Name);
        Assert.False(Scope.CanUndo);
        Assert.True(Scope.Redo());
        Assert.Equal("Live", Held().Name);
        Assert.False(Scope.CanRedo);
    }

    [Fact]
    public void SavesWhileRestoringAreNotRecorded()
    {
        SaveQuietly(Deck());
        SaveQuietly(Deck("echo"));
        Held().Name = "Easter";
        Edit(Held());
        var restoring = false;
        Library.Undo.Restored += _ =>
        {
            restoring = Library.Undo.IsRestoring;
            // A handler that saves as a side effect (re-rendering what's live, say).
            Held("echo").Name = "Touched";
            using (Scope.Use()) Library.Save(Held("echo"));
        };
        Scope.Undo();
        Assert.True(restoring);
        Assert.False(Scope.CanUndo);
        Assert.True(Scope.CanRedo);
    }

    [Fact]
    public void OnlyTheNewestStepsAreKept()
    {
        Library.Undo.Capacity = 2;
        SaveQuietly(Deck());
        foreach (var name in new[] { "A", "B", "C", "D" })
        {
            Held().Name = name;
            Edit(Held());
            Clock.Advance(10);
        }
        Assert.True(Scope.Undo());
        Assert.True(Scope.Undo());
        Assert.False(Scope.Undo());
        Assert.Equal("B", Held().Name);
    }

    [Fact]
    public async Task TheFocusedWindowsScopeRecordsOnlyItsOwnThread()
    {
        SaveQuietly(Deck());
        Library.Undo.Focus(Scope);
        Held().Name = "Clicked";
        Library.Save(Held());
        Assert.Single(Scope.UndoNames);

        // A background import saves on another thread: not the window's edit.
        await Task.Run(() => Library.Save(Deck("imported")), TestContext.Current.CancellationToken);
        Assert.Single(Scope.UndoNames);

        using (Library.Undo.Unrecorded())
        {
            Held().Name = "Housekeeping";
            Library.Save(Held());
        }
        Assert.Single(Scope.UndoNames);
    }

    [Fact]
    public void UseBeatsTheFocusedScope()
    {
        SaveQuietly(Deck());
        var editor = Library.Undo.NewScope("Editor");
        Library.Undo.Focus(Scope);
        using (editor.Use())
        {
            Held().Name = "Saved by the editor's timer";
            Library.Save(Held());
        }
        Assert.Empty(Scope.UndoNames);
        Assert.Single(editor.UndoNames);
    }

    [Fact]
    public void AGroupNamesItsStepAndCoalescesByItsKey()
    {
        SaveQuietly(Deck());
        var box = Held().Slides[0].Objects[0];
        for (var i = 1; i <= 3; i++)
        {
            using (Scope.Use())
            using (Scope.BeginGroup("Nudge", coalesceKey: "nudge:" + box.Id))
            {
                box.X = i;
                Library.Save(Held());
            }
            Clock.Advance(0.2);
        }
        Assert.Equal(["Nudge"], Scope.UndoNames);
        Scope.Undo();
        Assert.Null(Held().Slides[0].Objects[0].X);
    }

    [Fact]
    public void AGroupWithoutAKeyNeverCoalesces()
    {
        SaveQuietly(Deck());
        for (var i = 0; i < 2; i++)
        {
            using (Scope.Use())
            using (Scope.BeginGroup("Paste"))
            {
                Held().Slides.Add(Slide($"p{i}", "Pasted"));
                Library.Save(Held());
            }
        }
        Assert.Equal(["Paste", "Paste"], Scope.UndoNames);
    }

    [Fact]
    public void NothingIsUndoneWhileAGroupIsOpen()
    {
        SaveQuietly(Deck());
        using (Scope.Use())
        {
            using var drag = Scope.BeginGroup("Move");
            Held().Slides[0].Objects[0].X = 5;
            Library.Save(Held());
            Assert.True(Scope.InGroup);
            Assert.False(Scope.Undo());
        }
        Assert.True(Scope.Undo());
    }

    [Fact]
    public void SettlingSavesWhatsWaitingBeforeUndoing()
    {
        SaveQuietly(Deck());
        Held().Name = "Saved";
        Edit(Held());
        Clock.Advance(10);
        // Typed, but the autosave hasn't happened yet.
        Held().Slides[0].Objects[0].Text = "Waiting";
        Scope.Settling += () => Edit(Held());

        Scope.Undo();
        Assert.Equal("One", TextOf("s1"));
        Assert.Equal("Saved", Held().Name);
        Scope.Undo();
        Assert.Equal("Sunday", Held().Name);
    }

    [Fact]
    public void ARestoreNamesWhatItChanged()
    {
        SaveQuietly(Deck());
        SaveQuietly(Service("service", "deck"));
        UndoRestore? seen = null;
        Library.Undo.Restored += r => seen = r;
        using (Scope.Use())
        using (Scope.BeginGroup("Rename"))
        {
            Held().Name = "Easter";
            Library.Save(Held());
            var service = Library.Get<Service>("service")!;
            service.Name = "Easter service";
            Library.Save(service);
        }
        Scope.Undo();
        Assert.NotNull(seen);
        Assert.True(seen.Undone);
        Assert.Equal("Rename", seen.Name);
        Assert.Same(Scope, seen.Scope);
        Assert.Equal(["deck"], seen.Ids<Presentation>());
        Assert.Equal(["service"], seen.Ids<Service>());
    }

    [Fact]
    public void AClosedScopeForgetsItsSteps()
    {
        SaveQuietly(Deck());
        var editor = Library.Undo.NewScope("Editor");
        Library.Undo.Focus(editor);
        Held().Name = "x";
        Edit(Held(), editor);
        editor.Dispose();
        Assert.False(editor.CanUndo);
        Assert.Null(Library.Undo.Focused);
        Assert.DoesNotContain(editor, Library.Undo.Scopes);
        Held().Name = "y";
        Edit(Held(), editor);
        Assert.False(editor.CanUndo);
    }

    [Fact]
    public void ADocumentFirstWrittenElsewhereUndoesToWhatItSaid()
    {
        // As a Mac or an older version wrote it: other spacing, so not this app's bytes.
        var path = Library.DocumentPath(typeof(Presentation), "mac");
        File.WriteAllText(path, MxuJson.Serialize(Deck("mac"), indented: false));
        Library.Load();
        Held("mac").Name = "Edited on Windows";
        Edit(Held("mac"));
        Scope.Undo();
        Assert.Equal("Sunday", Held("mac").Name);
        Assert.Equal("Sunday", OnDisk("mac").Name);
    }

    [Fact]
    public void ReorderingSlidesUndoes()
    {
        SaveQuietly(Deck());
        var deck = Held();
        var moved = deck.Slides[0];
        deck.Slides.RemoveAt(0);
        deck.Slides.Add(moved);
        Edit(deck);
        Assert.Equal("Undo Move Slide", Scope.UndoMenuText);
        Scope.Undo();
        Assert.Equal(["s1", "s2", "s3"], Held().Slides.Select(s => s.Id));
    }
}
