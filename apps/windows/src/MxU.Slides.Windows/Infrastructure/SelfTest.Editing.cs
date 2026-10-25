using System.Windows;
using System.Windows.Controls;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.ViewModels;
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
            await ClipboardChecks(copy);
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

    // MARK: The clipboard

    private async Task ClipboardChecks(Presentation song)
    {
        // The clipboard belongs to whoever uses this PC: put back what was on it.
        var kept = Clipboard.ContainsText() ? Clipboard.GetText() : null;
        try
        {
            model.UndoScope.Clear();
            model.SelectedServiceItem = null;
            model.Open(model.Library.Get<Presentation>(song.Id)!, null, null);
            model.IsEditing = false;
            await Delay(300);

            // Pick two slides with Ctrl+click, copy them, and paste them after the first slide.
            model.SelectedSlide = model.Slides[1];
            model.PickTile(model.Slides[1], ctrl: false, shift: false);
            model.PickTile(model.Slides[3], ctrl: true, shift: false);
            Check("clipboard: Ctrl+click picks a second slide", model.PickedSlideIds.Count == 2 && model.Slides[1].IsSelected && model.Slides[3].IsSelected);
            await Step("45-clipboard-picked", () => { }, settle: 300);
            var picked = model.PickedSlideIds.ToList();
            var pickedWords = picked.Select(id => model.CurrentPresentation!.Slides.First(x => x.Id == id).Objects[0].Text).ToList();
            Check("clipboard: Copy puts the slides on the clipboard, with their words as plain text",
                model.Copy(EditTarget.Slides) && AppClipboard.Slides()?.Slides.Count == 2 && Clipboard.GetText().Length > 0);
            var count = model.CurrentPresentation!.Slides.Count;
            model.SelectedSlide = model.Slides[0];
            await model.Paste(EditTarget.Slides);
            await Delay(200);
            var deck = model.CurrentPresentation!;
            var pasted = deck.Slides.Skip(1).Take(2).ToList();
            Check("clipboard: Paste puts them after the selected slide, with fresh ids",
                deck.Slides.Count == count + 2 && pasted.All(s => !picked.Contains(s.Id)) && pasted.Select(s => s.Objects[0].Text).SequenceEqual(pickedWords));
            Check("clipboard: pasted slides join the group of the slide they land after", pasted.All(s => s.SectionId == deck.Slides[0].SectionId));
            Check("clipboard: the pasted slides are picked", model.PickedSlideIds.SequenceEqual(pasted.Select(s => s.Id)));
            Check("clipboard: the paste is one undo step", model.UndoMenuText == "Undo Add Slides");
            await Step("45-clipboard-pasted", () => { }, settle: 400);
            model.UndoEditCommand.Execute(null);
            await Delay(200);
            Check("clipboard: undoing the paste takes both away", model.CurrentPresentation!.Slides.Count == count);

            // Across presentations: into the welcome deck, which keeps its own groups.
            var welcome = model.Library.All<Presentation>().FirstOrDefault(p => p.Name == "Welcome to MxU Slides");
            if (welcome is not null)
            {
                model.Open(welcome, null, null);
                model.SelectedSlide = model.Slides[^1];
                var before = welcome.Slides.Count;
                await model.Paste(EditTarget.Slides);
                await Delay(200);
                Check("clipboard: slides paste into another presentation", model.CurrentPresentation?.Slides.Count == before + 2);
                await Step("46-clipboard-other-deck", () => { }, settle: 400);
                model.UndoEditCommand.Execute(null);
                await Delay(200);
                Check("clipboard: and undo takes them out of it again", model.CurrentPresentation?.Slides.Count == before);
                model.Open(model.Library.Get<Presentation>(song.Id)!, null, null);
            }

            // Objects on the canvas: copy a box, paste it back (it steps aside) and onto another slide (it doesn't).
            model.IsEditing = true;
            model.SelectedSlide = model.Slides[2];
            model.AddBoxCommand.Execute(null);
            model.FlushPendingSave();
            var slide = model.CurrentPresentation!.Slides.First(s => s.Id == model.SelectedSlide!.Slide.Id);
            var box = slide.Objects[^1];
            Check("clipboard: Copy takes the selected object", model.Copy(EditTarget.Objects) && AppClipboard.Objects()?.Objects.Count == 1);
            await model.Paste(EditTarget.Objects);
            await Delay(200);
            slide = model.CurrentPresentation!.Slides.First(s => s.Id == slide.Id);
            var again = slide.Objects[^1];
            Check("clipboard: pasted back onto its slide, the copy steps 24 down and right",
                again.Id != box.Id && again.X == box.X + 24 && again.Y == box.Y + 24);
            await Step("47-clipboard-objects", () => { }, settle: 400);
            model.SelectedSlide = model.Slides[4];
            await model.Paste(EditTarget.Objects);
            await Delay(200);
            var elsewhere = model.CurrentPresentation!.Slides.First(s => s.Id == model.SelectedSlide!.Slide.Id).Objects[^1];
            Check("clipboard: pasted onto another slide, it keeps its place", elsewhere.X == box.X && elsewhere.Id != box.Id);

            // Text from another app: a text box on the canvas, and slides in the grid of a song.
            Clipboard.SetText("Welcome, friends");
            await model.Paste(EditTarget.Objects);
            await Delay(200);
            var typed = model.CurrentPresentation!.Slides.First(s => s.Id == model.SelectedSlide!.Slide.Id).Objects[^1];
            Check("clipboard: plain text pasted on a slide becomes a text box", typed.ObjectKind == SlideObjectKind.Text && typed.Text == "Welcome, friends");
            Clipboard.SetText("Chorus\nHow great is our God\nSing with me\nHow great is our God\nAll will see");
            count = model.CurrentPresentation.Slides.Count;
            model.SelectedSlide = model.Slides[1];
            await model.Paste(EditTarget.Slides);
            await Delay(200);
            var added = model.CurrentPresentation.Slides.Count - count;
            var chorus = model.CurrentPresentation.Sections?.FirstOrDefault(s => s.Name == "Chorus");
            Check("clipboard: lyrics pasted into a song become slides, in a Chorus group", added >= 2 && chorus is not null
                && model.CurrentPresentation.Slides.Count(s => s.SectionId == chorus.Id) >= 2);
            await Step("48-clipboard-lyrics", () => model.IsEditing = false, settle: 500);

            // Cut, then Duplicate.
            model.SelectedSlide = model.Slides[1];
            count = model.CurrentPresentation.Slides.Count;
            var cutId = model.SelectedSlide.Slide.Id;
            Check("clipboard: Cut removes the slide and keeps it on the clipboard",
                model.Cut(EditTarget.Slides) && model.CurrentPresentation.Slides.Count == count - 1 && AppClipboard.Slides()?.Slides[0].Id == cutId);
            model.SelectedSlide = model.Slides[0];
            Check("clipboard: Duplicate copies the slide after itself",
                model.Duplicate(EditTarget.Slides) && model.CurrentPresentation.Slides.Count == count
                && model.CurrentPresentation.Slides[1].Objects.FirstOrDefault()?.Text == model.CurrentPresentation.Slides[0].Objects.FirstOrDefault()?.Text);
        }
        finally
        {
            try
            {
                if (kept is not null) Clipboard.SetText(kept);
                else Clipboard.Clear();
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
            }
            model.IsEditing = false;
        }
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
