using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Library;

/// <summary>
/// The run of show: which service rows are shown, which ones Next and auto-advance
/// walk, and in what order. Hidden rows (<see cref="ServiceItem.HiddenInPresenter"/>)
/// leave the walk and the visible list and wait in a "Hidden" group. A plan with several
/// times (9:00 and 11:00) can leave a row out of one time and reorder the rows per time.
/// Port of PresenterCore/Library/ServiceRunOrder.swift.
/// </summary>
public static class ServiceRunOrder
{
    /// <summary>Points a media row at another media item (Replace in This Service…), renaming it when a name is given.</summary>
    public static void ReplaceMedia(List<ServiceItem> items, string itemId, string mediaId, string? name)
    {
        foreach (var item in items.Where(i => i.Id == itemId && i.ItemKind == ServiceItemKind.Media))
        {
            item.RefId = mediaId;
            if (name is not null) item.Name = name;
        }
    }

    /// <summary>
    /// The row after <paramref name="itemId"/> that fires by itself: a presentation or a media item.
    /// Headers and music ("transport") rows are walked past, as on the Mac.
    /// </summary>
    public static ServiceItem? NextFireable(IReadOnlyList<ServiceItem> show, string itemId)
    {
        var index = show.ToList().FindIndex(i => i.Id == itemId);
        if (index < 0) return null;
        return show.Skip(index + 1).FirstOrDefault(i => i.ItemKind is ServiceItemKind.Presentation or ServiceItemKind.Media);
    }

    /// <summary>
    /// The rows in a plan time's order (its list of plan item ids). A row the plan doesn't know
    /// (added here) follows the planned row before it; rows before any planned row stay first.
    /// </summary>
    public static List<ServiceItem> Ordered(IReadOnlyList<ServiceItem> items, IReadOnlyList<string>? order)
    {
        if (order is not { Count: > 0 }) return [.. items];
        // The first time an id appears wins, like Swift's Dictionary(uniquingKeysWith: first).
        var position = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < order.Count; i++) position.TryAdd(order[i], i);
        var leading = new List<ServiceItem>();
        var buckets = new Dictionary<int, List<ServiceItem>>();
        int? current = null;
        foreach (var item in items)
        {
            if (item.MxuItemHexId is { } hexId && position.TryGetValue(hexId, out var slot))
            {
                current = slot;
                if (!buckets.TryGetValue(slot, out var bucket)) buckets[slot] = bucket = [];
                // The planned row leads its bucket; the local rows that followed it come after.
                bucket.Insert(0, item);
            }
            else if (current is { } open)
            {
                if (!buckets.TryGetValue(open, out var bucket)) buckets[open] = bucket = [];
                bucket.Add(item);
            }
            else
            {
                leading.Add(item);
            }
        }
        return [.. leading, .. buckets.Keys.Order().SelectMany(k => buckets[k])];
    }

    /// <summary>The rows the run order shows (hidden rows and rows left out of this time go).</summary>
    public static List<ServiceItem> Visible(IReadOnlyList<ServiceItem> items, string? timeHexId = null, IReadOnlyList<string>? order = null) =>
        Ordered([.. items.Where(i => i.IsInRunOfShow(timeHexId))], order);

    /// <summary>The rows waiting in the "Hidden (n)" group.</summary>
    public static List<ServiceItem> Hidden(IReadOnlyList<ServiceItem> items) => [.. items.Where(i => i.IsHidden())];

    /// <summary>The plan times whose run differs from the reference time's (the selected one, else the first).</summary>
    public static List<MxUPlanTime> DivergentTimes(
        IReadOnlyList<ServiceItem> items, IReadOnlyList<MxUPlanTime> times, IReadOnlyList<MxUPlanTimeOrder>? orders, string? selectedTimeHexId)
    {
        if (times.Count <= 1) return [];
        var referenceHexId = selectedTimeHexId ?? times[0].HexId;
        if (!times.Any(t => t.HexId == referenceHexId)) return [];
        List<string> Run(string hexId) =>
            [.. Visible(items, hexId, orders?.FirstOrDefault(o => o.TimeHexId == hexId)?.ItemHexIds).Select(i => i.Id)];
        var reference = Run(referenceHexId);
        return [.. times.Where(t => t.HexId != referenceHexId && !Run(t.HexId).SequenceEqual(reference))];
    }

    /// <summary>The rows that fire by themselves (presentations and media), in run order.</summary>
    public static List<ServiceItem> Fireable(IReadOnlyList<ServiceItem> items, string? timeHexId = null, IReadOnlyList<string>? order = null) =>
        [.. Visible(items, timeHexId, order).Where(i => i.ItemKind is ServiceItemKind.Presentation or ServiceItemKind.Media)];

    /// <summary>
    /// The kind of row a library document becomes when it's added to a service, or null when it
    /// can't be added. The Mac asks by DocumentKind; Windows asks by the document's type.
    /// </summary>
    public static ServiceItemKind? ItemKindAdding(Type documentType) =>
        documentType == typeof(Presentation) ? ServiceItemKind.Presentation
        : documentType == typeof(MediaItem) ? ServiceItemKind.Media
        : documentType == typeof(AudioItem) ? ServiceItemKind.Audio
        : documentType == typeof(Playlist) ? ServiceItemKind.Playlist
        : null;

    /// <summary>
    /// The rows the sidebar lists: headers, and the rows under headers that aren't collapsed.
    /// A hidden header ungroups its rows (they don't join the group above).
    /// </summary>
    public static List<ServiceItem> SidebarRows(
        IReadOnlyList<ServiceItem> items, IReadOnlySet<string> collapsedHeaders, string? timeHexId = null, IReadOnlyList<string>? order = null)
    {
        var result = new List<ServiceItem>();
        var inCollapsedGroup = false;
        foreach (var item in Ordered(items, order))
        {
            var shown = item.IsInRunOfShow(timeHexId);
            if (item.ItemKind == ServiceItemKind.Header)
            {
                inCollapsedGroup = shown && collapsedHeaders.Contains(item.Id);
                if (shown) result.Add(item);
            }
            else if (shown && !inCollapsedGroup)
            {
                result.Add(item);
            }
        }
        return result;
    }

    /// <summary>The row is in the "Hidden" group (Hide in its menu).</summary>
    public static bool IsHidden(this ServiceItem item) => item.HiddenInPresenter == true;

    /// <summary>The row belongs to this plan time (no time chosen: every row does).</summary>
    public static bool AppliesToTime(this ServiceItem item, string? timeHexId) =>
        timeHexId is null || !(item.ExcludedTimeHexIds ?? []).Contains(timeHexId);

    public static bool IsInRunOfShow(this ServiceItem item, string? timeHexId) => !item.IsHidden() && item.AppliesToTime(timeHexId);
}
