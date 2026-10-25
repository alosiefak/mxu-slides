using System.Windows;
using System.Windows.Controls;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.Views;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>
/// Phase 7's editing core, driven the way its keys and buttons drive it: undo and redo (typing,
/// moving, deleting a slide, a rename that touches a service, a live slide, two windows' scopes),
/// and the clipboard, arranging, drag and drop, text tools and chords. Every check works on a
/// scratch copy of the sample song, which is put in the Trash at the end.
/// </summary>
public sealed partial class SelfTest
{
    private async Task EditingCheck()
    {
        var history = model.Library.Undo;
        var song = model.Library.All<Presentation>().FirstOrDefault(p => p.Name == "Amazing Grace");
        Check("editing: the sample song is there to copy", song is not null);
        if (song is null) return;
        // The main window has the keyboard, as it would while the operator edits.
        history.Focus(model.UndoScope);
        model.UndoScope.Clear();
        model.SelectedServiceItem = null;
        model.Open(song, null, null);
        model.DuplicatePresentationCommand.Execute(null);
        var copy = model.CurrentPresentation!;
        await Delay(200);
        try
        {
            await UndoChecks(copy);
        }
        finally
        {
            model.ClearAllCommand.Execute(null);
            model.IsEditing = false;
            using (history.Unrecorded())
            {
                if (model.Library.Get<Presentation>(copy.Id) is not null) model.Library.Delete<Presentation>(copy.Id);
            }
            model.UndoScope.Clear();
        }
    }

    // MARK: Undo and redo

    private async Task UndoChecks(Presentation copy)
    {
        var history = model.Library.Undo;
        var scope = model.UndoScope;
        Check("undo: the main window has its own scope", UndoWindows.ScopeFor(window) == scope);
        Check("undo: duplicating a song is a step (\"Undo Add Presentation\")", model.UndoMenuText == "Undo Add Presentation");

        // Typing: several keystrokes, saved after the pause, are one "Typing" step.
        model.IsEditing = true;
        model.SelectedSlide = model.Slides[1];
        var field = model.SlideTextFields[0];
        var words = field.Text;
        field.Text = words + " (edited)";
        field.Text = words + " (edited again)";
        model.FlushPendingSave();
        await Delay(150);
        Check("undo: typing is one step called Typing", model.UndoMenuText == "Undo Typing" && scope.UndoNames.Count == 2);
        await Step("40-undo-typed", () => { }, settle: 400);
        await SaveMenu(EditBar.BuildMenu(model), "40-undo-edit-menu");
        model.UndoEditCommand.Execute(null);
        await Delay(200);
        Check("undo: Undo puts the words back, on screen and on disk",
            model.SlideTextFields.FirstOrDefault()?.Text == words && OnDisk(copy.Id)?.Slides[1].Objects.Any(o => o.Text == words) == true);
        Check("undo: Redo is offered for what was undone", model.RedoMenuText == "Redo Typing");
        await Step("41-undo-undone", () => { }, settle: 400);
        model.RedoEditCommand.Execute(null);
        await Delay(150);
        Check("undo: Redo types it again", model.SlideTextFields.FirstOrDefault()?.Text == words + " (edited again)");
        model.UndoEditCommand.Execute(null);

        // A live slide: undoing an edit to it re-renders the audience, as the edit did.
        model.GoLiveCommand.Execute(model.Slides[1]);
        await Delay(1200);
        model.SelectedSlide = model.Slides[1];
        model.SlideTextFields[0].Text = "Words typed while live";
        model.FlushPendingSave();
        await Delay(300);
        Check("undo: the audience shows the edit to the live slide", AudienceText().Contains("Words typed while live"));
        model.UndoEditCommand.Execute(null);
        await Delay(400);
        Check("undo: undoing it re-renders the live slide", !AudienceText().Contains("Words typed while live")
            && AudienceText().Contains(words.Split('\n')[0]));
        await Snapshot("42-undo-live-audience", model.Show.Scene());
        model.ClearAllCommand.Execute(null);

        // Moving: a box aligned to the left edge is a "Move"; undo puts it back.
        model.SelectedSlide = model.Slides[2];
        model.AddBoxCommand.Execute(null);
        model.FlushPendingSave();
        await Delay(150);
        Check("undo: adding a box is \"Add Object\"", model.UndoMenuText == "Undo Add Object");
        var box = model.CurrentPresentation!.Slides.First(s => s.Id == model.SelectedSlide!.Slide.Id).Objects[^1];
        var startX = box.X;
        model.AlignCommand.Execute("Left");
        model.FlushPendingSave();
        await Delay(150);
        Check("undo: aligning is a Move", model.UndoMenuText == "Undo Move" && box.X == 0);
        model.UndoEditCommand.Execute(null);
        await Delay(150);
        var boxNow = model.CurrentPresentation!.Slides.First(s => s.Id == model.SelectedSlide!.Slide.Id).Objects[^1];
        Check("undo: the box goes back where it was", boxNow.Id == box.Id && boxNow.X == startX);
        model.UndoEditCommand.Execute(null);
        await Delay(150);
        Check("undo: and undoing the add removes it", !model.CurrentPresentation!.Slides.First(s => s.Id == model.SelectedSlide!.Slide.Id).Objects.Any(o => o.Id == box.Id));

        // A slide deleted, then undone, comes back in its place.
        model.SelectedSlide = model.Slides[3];
        var deletedId = model.SelectedSlide.Slide.Id;
        var count = model.CurrentPresentation!.Slides.Count;
        model.DeleteSlideCommand.Execute(null);
        await Delay(150);
        Check("undo: deleting a slide is \"Undo Delete Slide\"", model.UndoMenuText == "Undo Delete Slide" && model.CurrentPresentation.Slides.Count == count - 1);
        model.UndoEditCommand.Execute(null);
        await Delay(200);
        Check("undo: the slide comes back in its place", model.CurrentPresentation.Slides.Count == count
            && model.CurrentPresentation.Slides.FindIndex(s => s.Id == deletedId) == 3);

        // One command that saves several documents is one step: a rename reaches the service too.
        if (model.SelectedService is { } service)
        {
            model.AddCurrentToServiceCommand.Execute(null);
            await Delay(150);
            var name = copy.Name;
            model.RenamePresentation(model.CurrentPresentation, "Renamed in the self-test");
            await Delay(150);
            var row = model.Library.Get<Service>(service.Id)?.Items.FirstOrDefault(i => i.RefId == copy.Id);
            Check("undo: renaming renames the service row too", row?.Name == "Renamed in the self-test");
            Check("undo: the rename and its service row are one step", model.UndoMenuText == "Undo Rename");
            model.UndoEditCommand.Execute(null);
            await Delay(200);
            row = model.Library.Get<Service>(service.Id)?.Items.FirstOrDefault(i => i.RefId == copy.Id);
            Check("undo: one undo puts back both names", model.CurrentPresentation?.Name == name && row?.Name == name);
            model.UndoEditCommand.Execute(null); // the row added to the service
            await Delay(150);
            Check("undo: and the service row goes again", model.Library.Get<Service>(service.Id)?.Items.Any(i => i.RefId == copy.Id) == false);
        }

        // Scopes are kept apart: an edit in the overlay editor is undone there, not here.
        var overlay = model.Overlays.FirstOrDefault()?.Overlay;
        if (overlay is not null)
        {
            var editor = OverlayEditorWindow.Open(window, model, overlay.Id);
            await Delay(700);
            var editorScope = UndoWindows.ScopeFor(editor);
            Check("undo: an editor window has a scope of its own", editorScope is not null && editorScope != scope);
            if (editorScope is not null)
            {
                var mainSteps = scope.UndoNames.Count;
                var oldName = overlay.Name;
                using (editorScope.Use())
                {
                    overlay.Name = oldName + " (renamed in the editor)";
                    model.SaveOverlayNow(overlay);
                }
                await Delay(150);
                Check("undo: the editor's edit is the editor's step, not the main window's",
                    editorScope.UndoMenuText == "Undo Rename" && scope.UndoNames.Count == mainSteps);
                UndoWindows.Run(editorScope, undo: true);
                await Delay(200);
                Check("undo: the editor's undo puts it back", model.Library.Get<Overlay>(overlay.Id)?.Name == oldName);
                Save(editor, "43-undo-overlay-editor");
            }
            editor.Close();
            history.Focus(scope);
            await Delay(200);
            Check("undo: closing the editor forgets its scope", editorScope is null || !history.Scopes.Contains(editorScope));
        }

        // Deleting the whole song, then undoing, brings it back.
        model.IsEditing = false;
        model.DeletePresentationCommand.Execute(null);
        await Delay(200);
        Check("undo: moving a song to the Trash is \"Undo Delete Presentation\"",
            model.UndoMenuText == "Undo Delete Presentation" && model.Library.Get<Presentation>(copy.Id) is null);
        model.UndoEditCommand.Execute(null);
        await Delay(300);
        Check("undo: the song comes back, and opens", model.Library.Get<Presentation>(copy.Id) is not null && model.CurrentPresentation?.Id == copy.Id);
        await Step("44-undo-restored-song", () => { }, settle: 500);
    }

    /// <summary>The library's copy as it is on disk now.</summary>
    private Presentation? OnDisk(string id)
    {
        var path = model.Library.DocumentPath(typeof(Presentation), id);
        return File.Exists(path) ? Core.Json.MxuJson.Deserialize<Presentation>(File.ReadAllBytes(path)) : null;
    }

    /// <summary>Opens a menu where it can't be clicked, takes its picture, and closes it.</summary>
    private async Task SaveMenu(ContextMenu menu, string name)
    {
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Absolute;
        menu.HorizontalOffset = window.Left + 40;
        menu.VerticalOffset = window.Top + 80;
        menu.IsOpen = true;
        await Delay(400);
        SaveElement(menu, name);
        menu.IsOpen = false;
    }
}
