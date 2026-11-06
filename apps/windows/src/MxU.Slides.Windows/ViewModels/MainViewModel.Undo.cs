using CommunityToolkit.Mvvm.Input;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Undo;
using MxU.Slides.Windows.Infrastructure;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// The main window's undo and redo (Core/Undo/UndoHistory.cs). Every edit made here is saved
/// through the library, which records it in <see cref="UndoScope"/>; this part offers the
/// commands, keeps the menu words fresh, and puts the screen right after an undo: the grid,
/// the canvas, and anything live, re-rendered as after any edit.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The main window's undo scope. Editor windows have their own.</summary>
    public UndoScope UndoScope { get; private set; } = null!;

    private void InitializeUndo()
    {
        UndoWindows.Initialize(Library);
        // The stage display follows each song's capo (MainViewModel.Chords.cs).
        HookCapos();
        UndoScope = Library.Undo.NewScope("Main window");
        // Typing waits half a second before it's saved. Any undo (here or in an editor) saves it
        // first: here, so it's what's undone; in an editor, so it isn't lost under what comes back.
        Library.Undo.Settling += FlushPendingSave;
        UndoScope.Changed += OnUndoStacksChanged;
        Library.Undo.Restored += OnUndoRestored;
    }

    /// <summary>"Undo Move" for the Edit menu and the toolbar's tooltip.</summary>
    public string UndoMenuText => UndoScope.UndoMenuText;

    public string RedoMenuText => UndoScope.RedoMenuText;

    public bool CanUndoEdit => UndoScope.CanUndo;

    public bool CanRedoEdit => UndoScope.CanRedo;

    [RelayCommand]
    private void UndoEdit() => UndoWindows.Run(UndoScope, undo: true);

    [RelayCommand]
    private void RedoEdit() => UndoWindows.Run(UndoScope, undo: false);

    private void OnUndoStacksChanged()
    {
        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(OnUndoStacksChanged);
            return;
        }
        OnPropertyChanged(nameof(UndoMenuText));
        OnPropertyChanged(nameof(RedoMenuText));
        OnPropertyChanged(nameof(CanUndoEdit));
        OnPropertyChanged(nameof(CanRedoEdit));
    }

    /// <summary>
    /// Something was undone or redone, in this window or an editor. The library has already
    /// written it and told every list (<c>Library.Changed</c>); this updates what holds the
    /// documents themselves: the open presentation and its canvas, and the show.
    /// </summary>
    private void OnUndoRestored(UndoRestore restore)
    {
        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => OnUndoRestored(restore));
            return;
        }
        try
        {
            var picked = canvas?.Selection.ToList() ?? [];
            foreach (var id in restore.Ids<Presentation>())
            {
                if (Library.Get<Presentation>(id) is not { } presentation)
                {
                    if (CurrentPresentation?.Id == id) CloseCurrentPresentation();
                    continue;
                }
                Show.PresentationEdited(presentation);
                AutoAdvance.PresentationEdited(id);
                if (CurrentPresentation?.Id == id) ReopenAfterUndo(presentation, picked);
                else if (CurrentPresentation is null)
                {
                    // An undone delete: show what came back (the "moved to the Trash" notice is out of date).
                    Open(presentation, null, null);
                    Notice = $"\"{presentation.Name}\" is back from the Trash.";
                }
            }
            foreach (var id in restore.Ids<Theme>())
            {
                if (Library.Get<Theme>(id) is not { } theme) continue;
                Show.ThemeEdited(theme);
                if (CurrentTheme?.Id == id)
                {
                    CurrentTheme = theme;
                    RebuildSlides();
                }
            }
            foreach (var id in restore.Ids<Overlay>())
            {
                if (Library.Get<Overlay>(id) is not { } overlay) continue;
                if (IsOverlayLive(id)) Show.OverlayEdited(overlay);
                OverlayContentChanged?.Invoke(overlay);
            }
            if (SelectedService is { } service && restore.Ids<Service>().Contains(service.Id)) RefreshServiceItems();
            RenderOutputs(Rendering.SceneTransition.Cut, refreshOnly: true);
        }
        catch (Exception ex)
        {
            // The documents are back on disk; only the screen is behind. Never let that reach the show.
            Log.Error($"Showing an undo ({restore.Name}) failed", ex);
        }
    }

    /// <summary>The open presentation changed under the grid: rebuild it, keeping the slide and objects picked.</summary>
    private void ReopenAfterUndo(Presentation presentation, IReadOnlyList<string> picked)
    {
        Open(presentation, CurrentContextItemId, SelectedArrangement?.Id);
        LoadCanvas();
        if (canvas is not null && EditedSlide is { } slide)
            canvas.Select(picked.Where(id => slide.Objects.Any(o => o.Id == id)));
        RefreshSlideFields();
    }

    private void CloseCurrentPresentation()
    {
        pendingSave = null; // it's gone: saving it would bring it back
        CurrentPresentation = null;
        Slides.Clear();
        OnPropertyChanged(nameof(HasPresentation));
        OnPropertyChanged(nameof(CurrentTitle));
        OnPropertyChanged(nameof(CurrentSubtitle));
        RefreshServiceItems();
        LoadCanvas();
    }
}
