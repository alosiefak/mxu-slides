using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Library;

/// <summary>
/// Versions of one service: the 9:00 and the 11:00, or North Campus and the Chapel. A version
/// isn't a copy of the service. It's a short list of differences from Main (rows it swaps, moves,
/// hides or adds), so a change to Main (a new song, a fixed typo) reaches every version, and each
/// version keeps only what it changed. Each computer chooses which version it runs; it's written
/// into the version (<see cref="ServiceVersion.Stations"/>) so the others can see who runs what.
/// <para>
/// Port of PresenterCore/Library/ServiceVersions.swift. Swift services are values, so
/// <c>edit(&amp;main…)</c> changes a copy; here <see cref="Edit"/> returns the new Main and leaves
/// the one passed in alone. <see cref="Create"/>, <see cref="Choose"/> and <see cref="UseMain"/>
/// change the service they're given (the app passes them a copy it then saves).
/// </para>
/// </summary>
public static class ServiceVersions
{
    /// <summary>
    /// The version this computer runs: the one naming this computer as a station, else the one
    /// with the name it picked last (another week's service), else none (Main).
    /// </summary>
    public static ServiceVersion? Running(Service service, string machineId, string? rememberedName)
    {
        if (service.Versions is not { Count: > 0 } versions) return null;
        if (versions.FirstOrDefault(v => v.Stations?.Any(s => s.Id == machineId) == true) is { } mine) return mine;
        if (rememberedName is null) return null;
        return versions.FirstOrDefault(v => string.Equals(v.Name, rememberedName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The service as a version sees it: Main's rows with the version's changes applied, its moved
    /// rows placed after their anchors, and its own rows added. The result has no versions.
    /// </summary>
    public static Service Apply(ServiceVersion version, Service main)
    {
        var service = MxuJson.Clone(main);
        service.Versions = null;
        var changes = version.Changes ?? [];
        var changeById = new Dictionary<string, ServiceItemChange>(StringComparer.Ordinal);
        foreach (var change in changes) changeById.TryAdd(change.ItemId, change);
        var added = new Dictionary<string, ServiceItem>(StringComparer.Ordinal);
        foreach (var item in version.Added ?? []) added.TryAdd(item.Id, item);

        var items = service.Items.Select(item => changeById.TryGetValue(item.Id, out var change) ? Changed(item, change) : item).ToList();
        var byId = new Dictionary<string, ServiceItem>(StringComparer.Ordinal);
        foreach (var item in items) byId.TryAdd(item.Id, item);

        // A change with "after" moves its row: take the movers out, then put each after its anchor
        // ("" = first). An anchor that's gone puts the row at the end.
        var movers = changes.Where(c => c.After is not null && (byId.ContainsKey(c.ItemId) || added.ContainsKey(c.ItemId))).ToList();
        var moving = movers.Select(c => c.ItemId).ToHashSet(StringComparer.Ordinal);
        items.RemoveAll(i => moving.Contains(i.Id));
        foreach (var change in movers)
        {
            var item = byId.GetValueOrDefault(change.ItemId) ?? MxuJson.Clone(added[change.ItemId]);
            if (change.After == "")
            {
                items.Insert(0, item);
            }
            else if (items.FindIndex(i => i.Id == change.After) is var anchor and >= 0)
            {
                items.Insert(anchor + 1, item);
            }
            else
            {
                items.Add(item);
            }
        }
        items.AddRange((version.Added ?? []).Where(i => !moving.Contains(i.Id)).Select(MxuJson.Clone));
        service.Items = items;
        return service;
    }

    /// <summary>
    /// An edit made while a version runs: <paramref name="mutate"/> changes the service as the
    /// version sees it, and the differences from Main are recorded in the version. The parts every
    /// version shares (the service's name and date, a row's colour or transition) still reach Main.
    /// Returns the new Main; with no such version it returns Main unchanged.
    /// </summary>
    public static Service Edit(Service main, string versionId, Action<Service> mutate)
    {
        var index = main.Versions?.FindIndex(v => v.Id == versionId) ?? -1;
        if (index < 0) return main;
        var versions = main.Versions!.Select(MxuJson.Clone).ToList();
        var edited = Apply(versions[index], main);
        mutate(edited);
        var recorded = Record(edited.Items, main.Items);
        versions[index].Added = recorded.Added.Count == 0 ? null : recorded.Added;
        versions[index].Changes = recorded.Changes.Count == 0 ? null : recorded.Changes;
        edited.Items = recorded.Main;
        edited.Versions = versions;
        return edited;
    }

    /// <summary>Every row any version runs (Main's and each version's), so nothing a version uses is tidied away.</summary>
    public static List<ServiceItem> AllItems(Service service) =>
        [.. service.Items, .. (service.Versions ?? []).SelectMany(v => Apply(v, service).Items)];

    /// <summary>Adds a version (it starts as Main: no changes) and returns its id.</summary>
    public static string Create(string name, Service service, string? id = null)
    {
        var version = new ServiceVersion { Id = id ?? Guid.NewGuid().ToString("D").ToUpperInvariant(), Name = name };
        service.Versions = [.. service.Versions ?? [], version];
        return version.Id;
    }

    /// <summary>
    /// This computer runs <paramref name="versionId"/> (null: Main): it leaves every other
    /// version's stations and joins that one's.
    /// </summary>
    public static void Choose(string? versionId, Service service, ServiceVersionStation computer)
    {
        if (service.Versions is not { } versions) return;
        foreach (var version in versions)
        {
            var before = version.Stations ?? [];
            var stations = before.Where(s => s.Id != computer.Id).ToList();
            if (version.Id == versionId) stations.Add(MxuJson.Clone(computer));
            if (!MxuJson.StructurallyEqual(stations, before)) version.Stations = stations.Count == 0 ? null : stations;
        }
    }

    /// <summary>"Use Main's": the version forgets what it changed about one row.</summary>
    public static void UseMain(string itemId, string versionId, Service service)
    {
        if (service.Versions?.FirstOrDefault(v => v.Id == versionId) is not { } version) return;
        var changes = version.Changes?.Where(c => c.ItemId != itemId).ToList();
        version.Changes = changes is { Count: 0 } ? null : changes;
    }

    /// <summary>The rows of Main this version changed (its own new rows don't count): they wear a "Changed" badge.</summary>
    public static HashSet<string> ChangedItemIds(ServiceVersion version)
    {
        var added = (version.Added ?? []).Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        var changed = (version.Changes ?? []).Select(c => c.ItemId).ToHashSet(StringComparer.Ordinal);
        changed.ExceptWith(added);
        return changed;
    }

    private static ServiceItem Changed(ServiceItem item, ServiceItemChange change)
    {
        item = MxuJson.Clone(item);
        if (change.Hidden is { } hidden) item.HiddenInPresenter = hidden ? true : null;
        if (change.ItemKind is { } kind) item.ItemKind = kind;
        if (change.RefId is { } refId) item.RefId = refId;
        if (change.Name is { } name) item.Name = name;
        if (change.ArrangementId is { } arrangement) item.ArrangementId = arrangement.Length == 0 ? null : arrangement;
        if (change.OutputPresetId is { } look) item.OutputPresetId = look.Length == 0 ? null : look;
        return item;
    }

    /// <summary>
    /// The differences between the rows a version shows and Main's: rows it added, changes to
    /// Main's rows (only moved rows record "after": the longest run still in Main's order stays
    /// put), Main's rows it dropped (recorded as hidden, so Unhide brings them back), and Main's
    /// rows with any shared fields the edit changed (a colour, a transition).
    /// </summary>
    internal static (List<ServiceItem> Added, List<ServiceItemChange> Changes, List<ServiceItem> Main) Record(
        IReadOnlyList<ServiceItem> edited, IReadOnlyList<ServiceItem> main)
    {
        var mainIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < main.Count; i++) mainIndex.TryAdd(main[i].Id, i);
        var inPlace = LongestInOrder([.. edited.Where(i => mainIndex.ContainsKey(i.Id)).Select(i => mainIndex[i.Id])]);
        var mainItems = main.Select(MxuJson.Clone).ToList();
        var added = new List<ServiceItem>();
        var changes = new List<ServiceItemChange>();
        var previous = "";
        foreach (var item in edited)
        {
            if (!mainIndex.TryGetValue(item.Id, out var index))
            {
                added.Add(MxuJson.Clone(item));
                changes.Add(new ServiceItemChange { ItemId = item.Id, After = previous });
                previous = item.Id;
                continue;
            }
            var origin = main[index];
            var change = new ServiceItemChange { ItemId = item.Id };
            if (!inPlace.Contains(index)) change.After = previous;
            if ((item.HiddenInPresenter ?? false) != (origin.HiddenInPresenter ?? false)) change.Hidden = item.HiddenInPresenter ?? false;
            if (item.ItemKind != origin.ItemKind) change.ItemKind = item.ItemKind;
            if (item.RefId != origin.RefId) change.RefId = item.RefId;
            if (item.Name != origin.Name) change.Name = item.Name;
            if (item.ArrangementId != origin.ArrangementId) change.ArrangementId = item.ArrangementId ?? "";
            if (item.OutputPresetId != origin.OutputPresetId) change.OutputPresetId = item.OutputPresetId ?? "";
            if (!IsEmpty(change)) changes.Add(change);
            // Main keeps its own version-specific fields; everything else the edit changed is shared.
            var shared = MxuJson.Clone(item);
            shared.HiddenInPresenter = origin.HiddenInPresenter;
            shared.ItemKind = origin.ItemKind;
            shared.RefId = origin.RefId;
            shared.Name = origin.Name;
            shared.ArrangementId = origin.ArrangementId;
            shared.OutputPresetId = origin.OutputPresetId;
            mainItems[index] = shared;
            previous = item.Id;
        }
        var kept = edited.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        changes.AddRange(main.Where(i => !kept.Contains(i.Id)).Select(i => new ServiceItemChange { ItemId = i.Id, Hidden = true }));
        return (added, changes, mainItems);
    }

    private static bool IsEmpty(ServiceItemChange change) =>
        change.After is null && change.Hidden is null && change.ItemKind is null && change.RefId is null
        && change.Name is null && change.ArrangementId is null && change.OutputPresetId is null;

    /// <summary>The Main positions of the longest run of rows the edit left in Main's order (they didn't move).</summary>
    private static HashSet<int> LongestInOrder(IReadOnlyList<int> indexes)
    {
        if (indexes.Count == 0) return [];
        var length = Enumerable.Repeat(1, indexes.Count).ToArray();
        var parent = Enumerable.Repeat(-1, indexes.Count).ToArray();
        for (var i = 0; i < indexes.Count; i++)
            for (var j = 0; j < i; j++)
                if (indexes[j] < indexes[i] && length[j] + 1 > length[i])
                {
                    length[i] = length[j] + 1;
                    parent[i] = j;
                }
        // Swift's max(by:) keeps the first of equal maxima.
        var at = 0;
        for (var i = 1; i < length.Length; i++)
            if (length[i] > length[at]) at = i;
        var output = new HashSet<int>();
        while (at >= 0)
        {
            output.Add(indexes[at]);
            at = parent[at];
        }
        return output;
    }
}
