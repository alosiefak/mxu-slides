using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Editing;

/// <summary>The four ways to move objects up and down the stack (the Mac's ArrangeAction layer order).</summary>
public enum LayerMove
{
    BringToFront,
    BringForward,
    SendBackward,
    SendToBack,
}

/// <summary>
/// The slide editor's layers: the objects list (top first, theme decor shown but fixed), moving
/// objects up and down the stack, and grouping. A port of SlideEditorModel.swift
/// (panelStackTopFirst, movePanelObject, bringForward, sendBackward, bringToFront, sendToBack,
/// groupSelection, ungroupSelection).
/// <para>
/// A slide's <c>objects</c> list is its stacking order: the first is drawn at the back. The Mac
/// moves one object at a time; here every selected object moves, keeping their order among
/// themselves, which is the same thing for one.
/// </para>
/// </summary>
public static class Layers
{
    /// <summary>
    /// The rows of the objects list, top first: the slide's own objects and, greyed, the theme
    /// design's decor between them, as they are drawn.
    /// </summary>
    public static IReadOnlyList<SlideSceneBuilder.ComposedEntry> TopFirst(IReadOnlyList<SlideObject> objects, Slide? template) =>
        [.. Enumerable.Reverse(SlideSceneBuilder.ComposedStack(objects, template))];

    /// <summary>
    /// Drops object <paramref name="id"/> just above <paramref name="beforeId"/> in the list (null: at
    /// the bottom). Theme decor can't move, so only the slide's own objects are reordered.
    /// Returns whether the order changed. The Mac's movePanelObject.
    /// </summary>
    public static bool MoveInList(Slide slide, Slide? template, string id, string? beforeId)
    {
        if (id == beforeId) return false;
        var items = TopFirst(slide.Objects, template).ToList();
        var from = items.FindIndex(e => e.Object.Id == id && !e.FromTheme);
        if (from < 0) return false;
        var item = items[from];
        items.RemoveAt(from);
        var target = beforeId is null ? -1 : items.FindIndex(e => e.Object.Id == beforeId);
        items.Insert(target < 0 ? items.Count : target, item);
        var order = items.Where(e => !e.FromTheme).Select(e => e.Object).Reverse().ToList();
        if (order.Select(o => o.Id).SequenceEqual(slide.Objects.Select(o => o.Id))) return false;
        slide.Objects = order;
        return true;
    }

    /// <summary>Whether a move would change anything (to grey out the menu).</summary>
    public static bool CanMove(IReadOnlyList<SlideObject> objects, IReadOnlySet<string> selected, LayerMove move)
    {
        var copy = objects.ToList();
        return Move(copy, selected, move);
    }

    /// <summary>Moves the selected objects up or down the stack. Returns whether the order changed.</summary>
    public static bool Move(List<SlideObject> objects, IReadOnlySet<string> selected, LayerMove move)
    {
        if (!objects.Any(o => selected.Contains(o.Id))) return false;
        var before = objects.Select(o => o.Id).ToList();
        switch (move)
        {
            case LayerMove.BringToFront:
            {
                var moving = objects.Where(o => selected.Contains(o.Id)).ToList();
                objects.RemoveAll(o => selected.Contains(o.Id));
                objects.AddRange(moving);
                break;
            }
            case LayerMove.SendToBack:
            {
                var moving = objects.Where(o => selected.Contains(o.Id)).ToList();
                objects.RemoveAll(o => selected.Contains(o.Id));
                objects.InsertRange(0, moving);
                break;
            }
            case LayerMove.BringForward:
                // From the top down, each selected object swaps with the unselected one just above it.
                for (var i = objects.Count - 2; i >= 0; i--)
                {
                    if (selected.Contains(objects[i].Id) && !selected.Contains(objects[i + 1].Id))
                        (objects[i], objects[i + 1]) = (objects[i + 1], objects[i]);
                }
                break;
            case LayerMove.SendBackward:
                for (var i = 1; i < objects.Count; i++)
                {
                    if (selected.Contains(objects[i].Id) && !selected.Contains(objects[i - 1].Id))
                        (objects[i], objects[i - 1]) = (objects[i - 1], objects[i]);
                }
                break;
        }
        return !before.SequenceEqual(objects.Select(o => o.Id));
    }

    /// <summary>Two or more objects can be grouped (the Mac's canGroup).</summary>
    public static bool CanGroup(IReadOnlySet<string> selected) => selected.Count >= 2;

    /// <summary>Some selected object is in a group (the Mac's canUngroup).</summary>
    public static bool CanUngroup(IReadOnlyList<SlideObject> objects, IReadOnlySet<string> selected) =>
        objects.Any(o => selected.Contains(o.Id) && !string.IsNullOrEmpty(o.GroupId));

    /// <summary>
    /// Puts the selected objects in one new group: clicking one selects them all, and they move
    /// together. Returns the group's id, or null when fewer than two are selected.
    /// </summary>
    public static string? Group(IReadOnlyList<SlideObject> objects, IReadOnlySet<string> selected, Func<string>? newId = null)
    {
        if (!CanGroup(selected)) return null;
        var group = (newId ?? Library.LibraryStore.NewId)();
        foreach (var o in objects.Where(o => selected.Contains(o.Id))) o.GroupId = group;
        return group;
    }

    /// <summary>Takes the selected objects out of their groups.</summary>
    public static bool Ungroup(IReadOnlyList<SlideObject> objects, IReadOnlySet<string> selected)
    {
        var changed = false;
        foreach (var o in objects.Where(o => selected.Contains(o.Id) && !string.IsNullOrEmpty(o.GroupId)))
        {
            o.GroupId = null;
            changed = true;
        }
        return changed;
    }

    /// <summary>The words for a move, as the Mac's menu has them.</summary>
    public static string Title(LayerMove move) => move switch
    {
        LayerMove.BringToFront => "Bring to Front",
        LayerMove.BringForward => "Bring Forward",
        LayerMove.SendBackward => "Send Backward",
        _ => "Send to Back",
    };
}
