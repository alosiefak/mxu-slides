using System.Windows;
using System.Windows.Controls;
using MxU.Slides.Core.Editing;
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
            await LayersChecks(copy);
            await DragDropChecks(copy);
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

    // MARK: Layers and arranging

    private async Task LayersChecks(Presentation song)
    {
        model.Open(model.Library.Get<Presentation>(song.Id)!, null, null);
        model.IsEditing = true;
        model.SelectedSlide = model.Slides[2];
        await Delay(300);
        model.AddBoxCommand.Execute(null);
        model.AddCircleCommand.Execute(null);
        model.FlushPendingSave();
        // What one click does is one step: let this piece of work end before the next "click".
        await Delay(100);
        var slide = model.CurrentPresentation!.Slides.First(s => s.Id == model.SelectedSlide!.Slide.Id);
        var box = slide.Objects[^2];
        var circle = slide.Objects[^1];
        var rows = model.LayerRows;
        Check("layers: the list is top first, the circle above the box", rows.Count >= 3 && rows[0].Id == circle.Id && rows[1].Id == box.Id);
        Check("layers: the theme's decor is listed, fixed", rows.Any(r => r.FromTheme) || slide.ThemeSlideName is null);

        model.PickLayers([box.Id]);
        Check("layers: picking a row selects the object on the canvas", window.CanvasEditor.Selection.SetEquals([box.Id]) && model.Inspector is { IsShape: true });
        model.MoveLayers(Core.Editing.LayerMove.BringForward);
        Check("layers: Bring Forward moves it above the circle", slide.Objects[^1].Id == box.Id && model.LayerRows[0].Id == box.Id);
        model.MoveLayers(Core.Editing.LayerMove.SendToBack);
        Check("layers: Send to Back puts it at the bottom", slide.Objects[0].Id == box.Id);
        model.MoveLayer(box.Id, circle.Id);
        Check("layers: dragging its row above the circle's moves it there", slide.Objects[^1].Id == box.Id);
        await Delay(100); // a step is named once its piece of work is over
        Check("layers: each move is a step called Arrange", model.UndoMenuText == "Undo Arrange");

        model.RenameObject(box.Id, "Banner");
        await Delay(100);
        Check("layers: renaming a row renames the object", slide.Objects.First(o => o.Id == box.Id).Name == "Banner" && model.UndoMenuText == "Undo Rename");
        model.ToggleHidden(circle.Id);
        Check("layers: the eye hides an object on every screen", circle.Hidden == true
            && !Core.Scene.SlideSceneBuilder.Scene(slide, model.CurrentTheme, model.CurrentPresentation).AllItems.Any(i => i.Id.Contains(circle.Id)));
        model.ToggleLocked(box.Id);
        Check("layers: the padlock locks an object (a Windows extra)", model.Locks.IsLocked(box.Id) && window.CanvasEditor.IsLocked?.Invoke(box.Id) == true);
        var x = box.X;
        window.CanvasEditor.Focus();
        window.CanvasEditor.Select([box.Id]);
        PressKey(window.CanvasEditor, System.Windows.Input.Key.Right);
        Check("layers: arrow keys leave a locked object where it is", box.X == x);
        model.ToggleLocked(box.Id);
        PressKey(window.CanvasEditor, System.Windows.Input.Key.Right);
        Check("layers: unlocked, an arrow key nudges it one point", box.X == x + 1);
        await Delay(100);
        model.ToggleLocked(box.Id); // locked again, for the picture

        model.PickLayers([box.Id, circle.Id]);
        model.GroupObjectsCommand.Execute(null);
        Check("layers: Group gives both one group", box.GroupId is { Length: > 0 } && box.GroupId == circle.GroupId);
        model.PickLayers([circle.Id]);
        Check("layers: picking one of a group picks the group", window.CanvasEditor.Selection.SetEquals([box.Id, circle.Id]));
        await Step("49-layers", () => { }, settle: 500);
        model.UngroupObjectsCommand.Execute(null);
        Check("layers: Ungroup takes them out", box.GroupId is null && circle.GroupId is null);
        model.ToggleLocked(box.Id);
        model.IsEditing = false;
    }

    /// <summary>Presses a key on an element, as the keyboard would.</summary>
    private static void PressKey(UIElement target, System.Windows.Input.Key key)
    {
        if (PresentationSource.FromVisual(target) is not { } source) return;
        target.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, 0, key)
        {
            RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent,
        });
    }

    // MARK: Drag and drop

    private async Task DragDropChecks(Presentation song)
    {
        model.Open(model.Library.Get<Presentation>(song.Id)!, null, null);
        model.IsEditing = true;
        await Delay(300);
        var deck = model.CurrentPresentation!;
        var ids = deck.Slides.Select(s => s.Id).ToList();

        // Two picked slides dragged to before the sixth.
        model.SelectedSlide = model.Slides[1];
        model.PickTile(model.Slides[1], ctrl: false, shift: false);
        model.PickTile(model.Slides[2], ctrl: true, shift: false);
        EditorDragDrop.PreviewGridDrop(window.Filmstrip, 5, onTile: false);
        await Step("50-drag-slides-line", () => { }, settle: 400);
        DropFeedback.For(window.Filmstrip)?.Clear();
        Check("drag: dragging two picked slides moves both, in order",
            model.MoveSlidesTo([ids[1], ids[2]], ids[5]) && deck.Slides.Select(s => s.Id).SequenceEqual(new[] { ids[0], ids[3], ids[4], ids[1], ids[2], ids[5] }.Concat(ids.Skip(6))));
        Check("drag: the moved slides join the group they land in", deck.Slides[3].SectionId == deck.Slides[5].SectionId);
        Check("drag: they stay picked", model.PickedSlideIds.SequenceEqual([ids[1], ids[2]]));
        await Delay(100);
        Check("drag: the move is one undo step (the Mac's \"Move Slide\")", model.UndoMenuText == "Undo Move Slide");
        model.UndoEditCommand.Execute(null);
        await Delay(200);
        Check("drag: undo puts them back", model.CurrentPresentation!.Slides.Select(s => s.Id).SequenceEqual(ids));

        // Media from the bin: on a tile it's the background, between tiles a new slide, on the canvas a picture.
        var media = model.MediaItems.FirstOrDefault(m => m.Item.MediaKind == MediaKind.Image)?.Item;
        if (media is null)
        {
            Skip("drag: media from the bin", "no pictures in the Media bin");
        }
        else
        {
            deck = model.CurrentPresentation!;
            EditorDragDrop.PreviewGridDrop(window.Filmstrip, 3, onTile: true);
            await Step("51-drag-media-on-tile", () => { }, settle: 300);
            DropFeedback.For(window.Filmstrip)?.Clear();
            Check("drag: media dropped on a slide becomes its background", model.DropMediaOnSlide(ids[3], media.Id)
                && model.CurrentPresentation!.Slides.First(s => s.Id == ids[3]).Background?.MediaId == media.Id);
            var count = model.CurrentPresentation!.Slides.Count;
            model.InsertMediaSlides([media], ids[2]);
            var made = model.CurrentPresentation.Slides[2];
            Check("drag: media dropped between slides makes a slide there", model.CurrentPresentation.Slides.Count == count + 1
                && made.Background?.MediaId == media.Id && made.Name == media.Name);

            model.SelectedSlide = model.Slides.First(t => t.Slide.Id == ids[4]);
            await Delay(200);
            var slide = model.CurrentPresentation.Slides.First(s => s.Id == ids[4]);
            var before = slide.Objects.Count;
            model.DropMediaOnCanvas(media.Id, new Core.Scene.EditorGeometry.Point(400, 300));
            Check("drag: media dropped on an empty part of the canvas is a picture box there", slide.Objects.Count == before + 1
                && slide.Objects[^1].Fill?.MediaId == media.Id);
            model.AddBoxCommand.Execute(null);
            var box = slide.Objects[^1];
            model.DropMediaOnCanvas(media.Id, new Core.Scene.EditorGeometry.Point((box.X ?? 0) + 50, (box.Y ?? 0) + 50));
            Check("drag: media dropped on a shape fills it", box.Fill?.FillKind == FillKind.Media && box.Fill.MediaId == media.Id);
            await Step("52-drag-media-on-canvas", () => { }, settle: 500);

            // The service list: media dropped at the top, then dragged to the end; an overlay can't go in.
            if (model.SelectedService is { } service)
            {
                model.IsEditing = false;
                var rows = service.Items.Count;
                EditorDragDrop.PreviewServiceDrop(window.ServiceList, 0);
                await Step("53-drag-into-service", () => { }, settle: 300);
                DropFeedback.For(window.ServiceList)?.Clear();
                Check("drag: media dropped into the service is a row where it was dropped",
                    model.DropIntoService(new LibraryDrag(typeof(MediaItem), media.Id), 0) && model.Library.Get<Service>(service.Id)!.Items[0].RefId == media.Id);
                var row = model.Library.Get<Service>(service.Id)!.Items[0].Id;
                model.MoveServiceItemBefore(row, null);
                Check("drag: a row dragged to the end lands there", model.Library.Get<Service>(service.Id)!.Items[^1].Id == row);
                Check("drag: an overlay can't go into a service", model.Overlays.FirstOrDefault() is not { } overlay
                    || !model.DropIntoService(new LibraryDrag(typeof(Overlay), overlay.Overlay.Id), 0));
                model.RemoveServiceItemCommand.Execute(model.ServiceItems.FirstOrDefault(r => r.Item.Id == row));
                Check("drag: the service is as it was", model.Library.Get<Service>(service.Id)!.Items.Count == rows);
            }
        }
        model.IsEditing = false;
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
