using CommunityToolkit.Mvvm.Input;
using MxU.Slides.Core.Editing;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>One row of the layers list: an object on the slide, or (greyed) a piece of the theme's design.</summary>
public sealed record LayerRow(string Id, string Name, SlideObjectKind Kind, bool FromTheme, bool Hidden, bool Locked, bool Selected, bool Grouped, bool ShowsMedia = false);

/// <summary>
/// The slide editor's layers and arranging (Core/Editing/Layers.cs has the rules): the objects
/// list top first, picking from it, renaming, hiding, locking, grouping, and moving objects up
/// and down the stack. The list is <see cref="LayerRows"/>; <see cref="LayersChanged"/> says when
/// to draw it again (Views/LayersPanel.cs).
/// </summary>
public sealed partial class MainViewModel
{
    private ObjectLocks? objectLocks;

    /// <summary>Which objects are locked (a Windows extra: Core/Editing/ObjectLocks.cs).</summary>
    public ObjectLocks Locks => objectLocks ??= new ObjectLocks(Library.Extras);

    /// <summary>The list changed: the slide, its objects, or which are picked.</summary>
    public event Action? LayersChanged;

    private void RaiseLayersChanged() => LayersChanged?.Invoke();

    /// <summary>The edited slide's objects, top first, with the theme's decor between them where it's drawn.</summary>
    public IReadOnlyList<LayerRow> LayerRows
    {
        get
        {
            if (!IsEditing || EditedSlide is not { } slide) return [];
            var selected = canvas?.Selection ?? [];
            return [.. Layers.TopFirst(slide.Objects, SlideSceneBuilder.ThemeSlide(slide, EditedSlideTheme)).Select(entry => new LayerRow(
                entry.Object.Id,
                string.IsNullOrWhiteSpace(entry.Object.Name) ? entry.Object.ObjectKind.ToString() : entry.Object.Name,
                entry.Object.ObjectKind,
                entry.FromTheme,
                entry.Object.Hidden == true,
                !entry.FromTheme && Locks.IsLocked(entry.Object.Id),
                !entry.FromTheme && selected.Contains(entry.Object.Id),
                !string.IsNullOrEmpty(entry.Object.GroupId),
                entry.Object.Fill?.FillKind == FillKind.Media || entry.Object.MediaId is not null))];
        }
    }

    /// <summary>Picks objects from the list (a group comes with its members, as a click on the canvas does).</summary>
    public void PickLayers(IEnumerable<string> ids)
    {
        if (canvas is null || EditedSlide is not { } slide) return;
        canvas.Select(EditorGeometry.ExpandSelectionToGroups(ids.ToHashSet(), slide.Objects));
        RaiseLayersChanged();
    }

    public void RenameObject(string id, string name)
    {
        if (EditedSlide?.Objects.FirstOrDefault(o => o.Id == id) is not { } o || string.IsNullOrWhiteSpace(name) || o.Name == name.Trim()) return;
        o.Name = name.Trim();
        AfterLayersEdit();
    }

    /// <summary>Hides an object from every screen (or shows it again), as the Mac's Hide/Show.</summary>
    public void ToggleHidden(string id)
    {
        if (EditedSlide?.Objects.FirstOrDefault(o => o.Id == id) is not { } o) return;
        o.Hidden = o.Hidden == true ? null : true;
        AfterLayersEdit();
    }

    public void ToggleLocked(string id)
    {
        Locks.Set(id, !Locks.IsLocked(id));
        canvas?.Refresh();
        RaiseLayersChanged();
    }

    /// <summary>Drops an object above another in the list (null: at the bottom).</summary>
    public void MoveLayer(string id, string? beforeId)
    {
        if (EditedSlide is not { } slide) return;
        if (Layers.MoveInList(slide, SlideSceneBuilder.ThemeSlide(slide, EditedSlideTheme), id, beforeId)) AfterLayersEdit();
    }

    public bool CanMoveLayers(LayerMove move) => EditedSlide is { } slide && canvas is { } editor && Layers.CanMove(slide.Objects, editor.Selection, move);

    public void MoveLayers(LayerMove move)
    {
        if (EditedSlide is not { } slide || canvas is null) return;
        if (Layers.Move(slide.Objects, canvas.Selection, move)) AfterLayersEdit();
    }

    [RelayCommand]
    private void BringForward() => MoveLayers(LayerMove.BringForward);

    [RelayCommand]
    private void SendBackward() => MoveLayers(LayerMove.SendBackward);

    public bool CanGroupObjects => canvas is { } editor && Layers.CanGroup(editor.Selection);

    public bool CanUngroupObjects => EditedSlide is { } slide && canvas is { } editor && Layers.CanUngroup(slide.Objects, editor.Selection);

    [RelayCommand]
    private void GroupObjects()
    {
        if (EditedSlide is not { } slide || canvas is null) return;
        if (Layers.Group(slide.Objects, canvas.Selection) is not null) AfterLayersEdit();
    }

    [RelayCommand]
    private void UngroupObjects()
    {
        if (EditedSlide is not { } slide || canvas is null) return;
        if (Layers.Ungroup(slide.Objects, canvas.Selection)) AfterLayersEdit();
    }

    /// <summary>An object's order, name, group or visibility changed: draw it, save it, list it.</summary>
    private void AfterLayersEdit()
    {
        canvas?.Refresh();
        SlideEdited();
        FlushPendingSave();
        RaiseLayersChanged();
    }
}
