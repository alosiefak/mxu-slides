using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Library;

/// <summary>
/// Choosing several service rows at once, and what a right-click then works on. Port of
/// PresenterCore/ListMultiSelect.swift: a plain click replaces the selection, Ctrl-click (the
/// Mac's ⌘) adds or takes one row away, Shift-click spans from the anchor. The Windows list does
/// the clicking itself; these rules say what the result means.
/// </summary>
public static class ListMultiSelect
{
    public enum Gesture { Replace, Toggle, Extend }

    /// <summary>The chosen ids and the row a Shift-click spans from.</summary>
    public sealed record State(IReadOnlySet<string> Selected, string? Anchor)
    {
        public static readonly State Empty = new(new HashSet<string>(StringComparer.Ordinal), null);

        public bool Equals(State? other) => other is not null && Anchor == other.Anchor && Selected.SetEquals(other.Selected);

        public override int GetHashCode() => HashCode.Combine(Selected.Count, Anchor);
    }

    public static State Click(string id, Gesture gesture, IReadOnlyList<string> order, State state)
    {
        switch (gesture)
        {
            case Gesture.Replace:
                return new State(new HashSet<string>(StringComparer.Ordinal) { id }, id);
            case Gesture.Toggle:
            {
                var selected = new HashSet<string>(state.Selected, StringComparer.Ordinal);
                if (!selected.Add(id)) selected.Remove(id);
                // A second Ctrl-click deselects; the anchor still follows the click (the grid's rule).
                return new State(selected, id);
            }
            default:
            {
                var clicked = IndexOf(order, id);
                if (clicked < 0) return state;
                var anchorIndex = state.Anchor is { } anchor ? IndexOf(order, anchor) : -1;
                if (anchorIndex < 0) anchorIndex = clicked;
                var selected = new HashSet<string>(state.Selected, StringComparer.Ordinal);
                for (var i = Math.Min(anchorIndex, clicked); i <= Math.Max(anchorIndex, clicked); i++) selected.Add(order[i]);
                // Shift never moves the anchor, and a span adds to what was there.
                return new State(selected, state.Anchor ?? id);
            }
        }
    }

    /// <summary>
    /// The Finder's rule for a right-click: inside a multi-selection it works on the whole
    /// selection, in list order; anywhere else, on just the row clicked.
    /// </summary>
    public static List<string> Batch(string clicked, IReadOnlySet<string> selection, IReadOnlyList<string> order) =>
        selection.Count > 1 && selection.Contains(clicked) ? [.. order.Where(selection.Contains)] : [clicked];

    private static int IndexOf(IReadOnlyList<string> order, string id)
    {
        for (var i = 0; i < order.Count; i++)
            if (order[i] == id) return i;
        return -1;
    }
}

/// <summary>
/// The service list's selection rules. Port of PresenterCore/Library/RunOrderSelection.swift.
/// </summary>
public static class RunOrderSelection
{
    /// <summary>
    /// The row the slide area shows while several are chosen: the one it shows now while it stays
    /// chosen (Ctrl or Shift growing the selection never moves it), else the first chosen row that
    /// isn't a header. A header alone keeps what was shown; nothing chosen shows nothing.
    /// </summary>
    public static string? SurfaceItem(IReadOnlySet<string> selection, string? current, IReadOnlyList<ServiceItem> rows)
    {
        if (selection.Count == 0) return null;
        if (current is not null && selection.Contains(current)) return current;
        return rows.FirstOrDefault(r => selection.Contains(r.Id) && r.ItemKind != ServiceItemKind.Header)?.Id ?? current;
    }

    /// <summary>Clicking the row already shown, with no modifier, jumps back to it rather than changing anything.</summary>
    public static bool IsReselect(string pressed, string? current, bool ctrl, bool shift) => pressed == current && !ctrl && !shift;

    /// <summary>The rows a removal takes, split into this PC's own and the ones that came from a plan.</summary>
    public sealed record Removal(IReadOnlyList<ServiceItem> Local, IReadOnlyList<ServiceItem> Synced)
    {
        public List<string> Ids => [.. Local.Concat(Synced).Select(i => i.Id)];

        public int Count => Local.Count + Synced.Count;
    }

    /// <summary>
    /// The rows to remove, in the service's order. A row that came from Planning Center (it has a
    /// plan item id) is "synced"; a header always removes locally, even when it came from a plan
    /// (the header menu's rule). Ids that aren't in the service are skipped.
    /// </summary>
    public static Removal RemovalOf(IEnumerable<string> ids, IReadOnlyList<ServiceItem> items)
    {
        var targets = ids.ToHashSet(StringComparer.Ordinal);
        var local = new List<ServiceItem>();
        var synced = new List<ServiceItem>();
        foreach (var item in items.Where(i => targets.Contains(i.Id)))
            (item.MxuItemHexId is not null && item.ItemKind != ServiceItemKind.Header ? synced : local).Add(item);
        return new Removal(local, synced);
    }
}

/// <summary>
/// Changing several service rows at once (the bulk menu: Transition, Hide, Remove) and the
/// service menu's Set All Transitions. Port of AppModel.setServiceItemTransition and the bulk
/// half of ServicePlannerView.swift.
/// </summary>
public static class ServiceBulkEdit
{
    /// <summary>
    /// Gives the songs among <paramref name="itemIds"/> (null: every song in the service) how they
    /// hand over to the next item: a stop, a fade, an overlap or a crossfade. Only presentation rows
    /// have one. Returns how many rows changed.
    /// </summary>
    public static int SetTransition(List<ServiceItem> items, IReadOnlyCollection<string>? itemIds, SongTransition? transition)
    {
        var changed = 0;
        foreach (var item in items.Where(i => i.ItemKind == ServiceItemKind.Presentation))
        {
            if (itemIds is not null && !itemIds.Contains(item.Id)) continue;
            item.Transition = transition is null ? null : new SongTransition { Kind = transition.Kind, Seconds = transition.Seconds };
            changed++;
        }
        return changed;
    }

    /// <summary>Hides the rows (they wait in the Hidden group, out of the run of show). Returns how many were hidden now.</summary>
    public static int Hide(List<ServiceItem> items, IReadOnlyCollection<string> itemIds)
    {
        var changed = 0;
        foreach (var item in items.Where(i => itemIds.Contains(i.Id) && i.HiddenInPresenter != true))
        {
            item.HiddenInPresenter = true;
            changed++;
        }
        return changed;
    }

    /// <summary>Removes the rows; returns the ids removed, in the service's order.</summary>
    public static List<string> Remove(List<ServiceItem> items, IReadOnlyCollection<string> itemIds)
    {
        var removal = RunOrderSelection.RemovalOf(itemIds, items);
        var ids = removal.Ids.ToHashSet(StringComparer.Ordinal);
        items.RemoveAll(i => ids.Contains(i.Id));
        return removal.Ids;
    }

    /// <summary>The heading of the bulk menu: "3 Items".</summary>
    public static string BatchTitle(int count) => count == 1 ? "1 Item" : $"{count} Items";
}
