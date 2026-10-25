using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.PlanningCenter;

/// <summary>How new songs are made, and which time zone a plan's date is read in (the church's: this PC's).</summary>
public sealed record PlanImportOptions(string ThemeId, int LinesPerSlide, TimeZoneInfo TimeZone);

/// <summary>What an import did, in words for the notice bar, plus the numbers behind them for tests.</summary>
public sealed record PlanImportSummary(
    string ServiceId,
    bool Updated,
    int Rows,
    int SongsMatched,
    int SongsMade,
    IReadOnlyList<string> SongsUnlinked,
    IReadOnlyList<string> MediaToAdd,
    int Added = 0,
    int Removed = 0,
    int Changed = 0,
    int KeptYourChanges = 0)
{
    public string Text
    {
        get
        {
            var parts = new List<string>();
            if (Updated)
            {
                parts.Add(Added + Removed + Changed == 0 ? "Already up to date" : $"{Added} added, {Removed} removed, {Changed} changed");
                if (KeptYourChanges > 0) parts.Add($"kept your changes to {KeptYourChanges} row{(KeptYourChanges == 1 ? "" : "s")}");
            }
            else
            {
                parts.Add($"{Rows} row{(Rows == 1 ? "" : "s")}");
            }
            if (SongsMatched > 0) parts.Add($"{SongsMatched} song{(SongsMatched == 1 ? "" : "s")} from your library");
            if (SongsMade > 0) parts.Add($"{SongsMade} new song{(SongsMade == 1 ? "" : "s")} made from Planning Center's lyrics");
            if (SongsUnlinked.Count > 0) parts.Add($"to link by hand: {string.Join(", ", SongsUnlinked)}");
            if (MediaToAdd.Count > 0) parts.Add($"media to add: {string.Join(", ", MediaToAdd)}");
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// Turns a Planning Center plan into a service, or brings a service made from one up to date.
/// <list type="bullet">
/// <item><b>Rows.</b> A header stays a header. A song becomes a row playing a library song: one
/// the library already has (<see cref="PlanSongMatcher"/>) or one made from the arrangement's
/// lyrics (<see cref="PlanSongMaker"/>). Media and other items (a welcome, the sermon) become
/// "info" rows, the kind the Mac uses for a plan row with nothing in the library to play yet:
/// the media is listed as "to add", since Planning Center's files aren't downloaded.</item>
/// <item><b>Lengths and dates.</b> Each item's length becomes the row's Duration (the service
/// tracking timers count it down), and the plan's date the service's date.</item>
/// <item><b>Planning Center's ids</b> go into the fields the shared format has for them
/// (<c>mxuPcoItemId</c>, <c>mxuPcoSongId</c>, …), with the song's title, CCLI number, author,
/// key and notes. The plan's own id is kept in a <see cref="PlanLink"/>.</item>
/// <item><b>Importing again</b> updates the service in place (<see cref="Merge"/>).</item>
/// </list>
/// Pure library work with no network or UI: the app fetches the plan first (PlanningCenterClient),
/// then runs this on a background thread. Windows only.
/// </summary>
public static class PlanImporter
{
    /// <summary>One plan item as the import would have it, before it's merged with an existing service.</summary>
    public sealed record Row(PcoPlanItem Source, ServiceItem Item, Presentation? NewSong, bool Matched, bool Ambiguous);

    /// <summary>Imports or re-imports a plan and saves the service, its new songs and its link.</summary>
    public static PlanImportSummary Import(LibraryStore library, PcoPlanContents contents, PlanImportOptions options, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.Now;
        var rows = Rows(contents, library.All<Presentation>(), options);
        var name = ServiceName(contents);
        var date = contents.Plan.LocalDate(options.TimeZone) ?? at.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        Service service;
        PlanImportSummary summary;
        if (PlanLink.Find(library, contents.Plan.Id) is { } existing)
        {
            var merged = Merge(existing.Service, existing.Link, rows, name, date, library);
            service = merged.Service;
            summary = Summarize(service, rows, updated: true) with
            {
                Added = merged.Added, Removed = merged.Removed, Changed = merged.Changed, KeptYourChanges = merged.Kept,
            };
        }
        else
        {
            service = new Service { Id = LibraryStore.NewId(), Name = name, ServiceDate = date, Items = [.. rows.Select(r => r.Item)] };
            summary = Summarize(service, rows, updated: false);
        }

        // Only the songs a row ends up playing are saved: a re-import that kept the operator's own
        // song for a row doesn't leave an unused copy behind.
        var used = service.Items.Select(i => i.RefId).ToHashSet();
        foreach (var song in rows.Select(r => r.NewSong).OfType<Presentation>().DistinctBy(s => s.Id).Where(s => used.Contains(s.Id)))
            library.Save(song);
        library.Save(service);
        PlanLink.Save(library, service.Id, LinkFor(contents, service, rows, name, date, at));
        return summary with { ServiceId = service.Id };
    }

    /// <summary>"Sunday Morning · Week 3: Grace", or with an untitled plan "Sunday Morning · November 1, 2026".</summary>
    public static string ServiceName(PcoPlanContents contents)
    {
        var detail = contents.Plan.DisplayTitle ?? contents.Plan.Dates;
        return detail is null ? contents.ServiceTypeName : $"{contents.ServiceTypeName} · {detail}";
    }

    /// <summary>Every plan item as a service row, in plan order. New songs are made but not saved.</summary>
    public static List<Row> Rows(PcoPlanContents contents, IEnumerable<Presentation> library, PlanImportOptions options)
    {
        var matcher = new PlanSongMatcher(library);
        // The same song (and arrangement) twice in one plan is made once.
        var made = new Dictionary<string, Presentation>();
        var rows = new List<Row>();
        foreach (var source in contents.Items)
        {
            var item = new ServiceItem
            {
                Id = LibraryStore.NewId(),
                ItemKind = ServiceItemKind.Info,
                Name = source.Title,
                RefId = "",
                Duration = source.LengthSeconds ?? source.Arrangement?.LengthSeconds,
                MxuKind = source.RawType,
                MxuPcoItemId = source.Id,
                MxuDescription = source.Description,
                MxuServicePosition = source.ServicePosition,
                MxuNotes = source.Notes.Count == 0 ? null : [.. source.Notes.Select(n => new MxUPlanItemNote { Label = n.Category, Text = n.Content })],
            };
            Presentation? newSong = null;
            bool matched = false, ambiguous = false;
            switch (source.Type)
            {
                case PcoItemType.Header:
                    item.ItemKind = ServiceItemKind.Header;
                    item.Duration = null;
                    break;
                case PcoItemType.Song when source.Song is { } song:
                    item.MxuPcoSongId = song.Id;
                    item.MxuPcoArrangementId = source.Arrangement?.Id;
                    item.MxuPcoKeyId = source.Key?.Id;
                    item.MxuSongTitle = song.Title;
                    item.MxuCcliNumber = song.CcliNumber;
                    item.MxuSongAuthor = song.Author;
                    item.MxuArrangementName = source.Arrangement?.Name;
                    item.MxuSongKey = source.KeyName ?? source.Key?.StartingKey;
                    item.MxuBpm = source.Arrangement?.Bpm;
                    item.MxuTimeSignature = source.Arrangement?.Meter;
                    var found = matcher.Find(song.Title, song.Author, song.CcliNumber);
                    ambiguous = found.Ambiguous;
                    Presentation? playing = found.Song;
                    if (playing is not null)
                    {
                        matched = !made.ContainsValue(playing);
                    }
                    else if (!ambiguous && source.Arrangement is { } arrangement)
                    {
                        var key = $"{song.Id}/{arrangement.Id}";
                        if (!made.TryGetValue(key, out playing)
                            && PlanSongMaker.Make(song, arrangement, item.MxuSongKey, options.ThemeId, options.LinesPerSlide) is { } fresh)
                        {
                            made[key] = playing = fresh;
                            matcher.Add(fresh);
                        }
                        newSong = playing;
                    }
                    if (playing is not null)
                    {
                        item.ItemKind = ServiceItemKind.Presentation;
                        item.RefId = playing.Id;
                        item.Name = playing.Name;
                        item.ArrangementId = ServiceLinkLogic.MatchArrangement(source.Arrangement?.Name, playing.Arrangements);
                    }
                    break;
            }
            rows.Add(new Row(source, item, newSong, matched, ambiguous));
        }
        return rows;
    }

    private static PlanImportSummary Summarize(Service service, List<Row> rows, bool updated)
    {
        var final = service.Items.Where(i => i.MxuPcoItemId is not null).ToDictionary(i => i.MxuPcoItemId!, i => i);
        var songs = rows.Where(r => r.Source.Type == PcoItemType.Song && final.ContainsKey(r.Source.Id)).ToList();
        bool Plays(Row r) => final[r.Source.Id].ItemKind == ServiceItemKind.Presentation;
        return new PlanImportSummary(
            service.Id, updated, service.Items.Count,
            SongsMatched: songs.Count(r => r.Matched && Plays(r)),
            SongsMade: songs.Where(r => r.NewSong is not null && final[r.Source.Id].RefId == r.NewSong.Id).Select(r => r.NewSong!.Id).Distinct().Count(),
            SongsUnlinked: [.. songs.Where(r => !Plays(r)).Select(r => r.Ambiguous ? $"{r.Source.Title} (more than one in your library)" : r.Source.Title)],
            MediaToAdd: [.. rows.Where(r => r.Source.Type == PcoItemType.Media && final.ContainsKey(r.Source.Id))
                .Select(r => r.Source.MediaTitles.Count > 0 ? string.Join(", ", r.Source.MediaTitles) : r.Source.Title)]);
    }

    /// <summary>What Planning Center gave this time, to compare against next time. It records the plan's values, not the operator's.</summary>
    private static PlanLink LinkFor(PcoPlanContents contents, Service service, List<Row> rows, string name, string date, DateTimeOffset at)
    {
        var ids = service.Items.Where(i => i.MxuPcoItemId is not null).GroupBy(i => i.MxuPcoItemId!).ToDictionary(g => g.Key, g => g.First().Id);
        var items = rows.Where(r => ids.ContainsKey(r.Source.Id)).Select(r => new PlanLinkedItem(
            r.Source.Id, ids[r.Source.Id], r.Item.Name, r.Item.ItemKind, r.Item.RefId, r.Item.ArrangementId, r.Item.Duration, r.Item.MxuPcoSongId));
        // Rows the operator removed are remembered too, so the next import doesn't bring them back.
        var removed = rows.Where(r => !ids.ContainsKey(r.Source.Id)).Select(r => new PlanLinkedItem(
            r.Source.Id, "", r.Item.Name, r.Item.ItemKind, r.Item.RefId, r.Item.ArrangementId, r.Item.Duration, r.Item.MxuPcoSongId));
        return new PlanLink(contents.Plan.ServiceTypeId, contents.ServiceTypeName, contents.Plan.Id, name, date, at, [.. items, .. removed]);
    }

    // MARK: Importing again

    /// <summary>A merged service and what changed.</summary>
    public sealed record Merged(Service Service, int Added, int Removed, int Changed, int Kept);

    /// <summary>
    /// Brings a service made from a plan up to date with the plan, keeping what the operator did.
    /// It's a three-way comparison for every row: what the last import set (in <paramref name="link"/>),
    /// what the row says now, and what Planning Center says now.
    /// <list type="bullet">
    /// <item>A field the operator hasn't touched since the last import takes Planning Center's new
    /// value; a field they changed (a renamed row, another song, another arrangement, a hidden row,
    /// a length) keeps theirs. Row ids stay the same, so service versions and timelines still find them.</item>
    /// <item>A row's song stays as it is while Planning Center's song for it is unchanged, even if the
    /// library now has a second copy that makes matching ambiguous.</item>
    /// <item>New plan items are added. A plan item the operator deleted stays deleted.</item>
    /// <item>A row Planning Center dropped is removed, unless the operator changed it.</item>
    /// <item>Rows the operator added stay, after the row they followed.</item>
    /// <item>The order follows the plan's, unless the operator reordered the plan's rows: then theirs is kept
    /// and new items go in after the item they follow in the plan.</item>
    /// <item>The service's name and date follow the plan's unless the operator changed them.</item>
    /// </list>
    /// </summary>
    public static Merged Merge(Service current, PlanLink link, IReadOnlyList<Row> fresh, string freshName, string freshDate, LibraryStore? library = null)
    {
        var service = MxuJson.Clone(current);
        var last = link.Items.GroupBy(i => i.PcoItemId).ToDictionary(g => g.Key, g => g.First());
        var freshById = fresh.GroupBy(r => r.Source.Id).ToDictionary(g => g.Key, g => g.First());
        var currentByPco = service.Items.Where(i => i.MxuPcoItemId is not null).GroupBy(i => i.MxuPcoItemId!).ToDictionary(g => g.Key, g => g.First());
        int added = 0, removed = 0, changed = 0, kept = 0;

        // 1. Each plan item: update the row it made, add it, or leave it out (the operator deleted it).
        var planRows = new Dictionary<string, ServiceItem>();
        foreach (var row in fresh)
        {
            var id = row.Source.Id;
            if (currentByPco.TryGetValue(id, out var existing))
            {
                var (item, edited, keptOwn) = Updated(existing, last.GetValueOrDefault(id), row.Item, library);
                planRows[id] = item;
                if (edited) changed++;
                if (keptOwn) kept++;
            }
            else if (!last.ContainsKey(id))
            {
                planRows[id] = row.Item;
                added++;
            }
        }

        // 2. Rows whose plan item is gone: removed if untouched, kept (as the operator's own) if changed.
        var orphans = new HashSet<string>();
        foreach (var (id, item) in currentByPco.Where(p => !freshById.ContainsKey(p.Key)))
        {
            if (last.TryGetValue(id, out var was) && Untouched(item, was))
            {
                removed++;
            }
            else
            {
                orphans.Add(item.Id);
                kept++;
            }
        }

        // 3. The order of the plan's rows.
        var lastOrder = link.Items.Where(i => i.ServiceItemId.Length > 0).Select(i => i.PcoItemId).ToList();
        var currentOrder = service.Items.Where(i => i.MxuPcoItemId is not null && !orphans.Contains(i.Id)).Select(i => i.MxuPcoItemId!).ToList();
        var reordered = !currentOrder.Where(lastOrder.Contains).SequenceEqual(lastOrder.Where(currentOrder.Contains));
        List<string> order;
        if (!reordered)
        {
            order = [.. fresh.Select(r => r.Source.Id).Where(planRows.ContainsKey)];
        }
        else
        {
            order = [.. currentOrder.Where(planRows.ContainsKey)];
            var freshOrder = fresh.Select(r => r.Source.Id).ToList();
            foreach (var id in freshOrder.Where(id => planRows.ContainsKey(id) && !order.Contains(id)))
            {
                var before = freshOrder.Take(freshOrder.IndexOf(id)).LastOrDefault(order.Contains);
                order.Insert(before is null ? 0 : order.IndexOf(before) + 1, id);
            }
        }

        // 4. The operator's own rows (and kept orphans) ride along after the plan row they followed.
        var anchored = new Dictionary<string, List<ServiceItem>>();
        var leading = new List<ServiceItem>();
        string? anchor = null;
        foreach (var item in service.Items)
        {
            if (item.MxuPcoItemId is { } pco && !orphans.Contains(item.Id))
            {
                if (planRows.ContainsKey(pco)) anchor = pco;
                continue;
            }
            if (anchor is null) leading.Add(item);
            else (anchored.TryGetValue(anchor, out var list) ? list : anchored[anchor] = []).Add(item);
        }
        var items = new List<ServiceItem>(leading);
        foreach (var id in order)
        {
            items.Add(planRows[id]);
            if (anchored.TryGetValue(id, out var following)) items.AddRange(following);
        }
        // Anything anchored to a row that's gone now follows the row before it (never lost).
        foreach (var (id, following) in anchored.Where(p => !order.Contains(p.Key))) items.AddRange(following);
        service.Items = items;

        if (service.Name == link.ImportedName) service.Name = freshName;
        if (service.ServiceDate == link.ImportedDate) service.ServiceDate = freshDate;
        return new Merged(service, added, removed, changed, kept);
    }

    /// <summary>Whether a row still reads exactly as the last import left it.</summary>
    private static bool Untouched(ServiceItem item, PlanLinkedItem was) =>
        item.Name == was.Name && item.ItemKind == was.ItemKind && item.RefId == was.RefId && item.ArrangementId == was.ArrangementId
        && item.Duration == was.Duration && item.HiddenInPresenter != true && item.OutputPresetId is null;

    /// <summary>One row merged: the row, whether Planning Center changed anything on it, whether the operator's changes were kept.</summary>
    private static (ServiceItem Item, bool Changed, bool KeptOwn) Updated(ServiceItem current, PlanLinkedItem? was, ServiceItem fresh, LibraryStore? library)
    {
        var item = MxuJson.Clone(current);
        var changed = false;
        var keptOwn = false;

        // Takes Planning Center's new value where the operator left the field as the last import set it.
        // With no record of the last import (it was made on another PC, say), the operator's values stay.
        T Take<T>(T now, T before, T incoming, bool known)
        {
            if (EqualityComparer<T>.Default.Equals(now, incoming)) return now;
            if (known && EqualityComparer<T>.Default.Equals(now, before))
            {
                changed = true;
                return incoming;
            }
            keptOwn = true;
            return now;
        }

        var known = was is not null;
        was ??= new PlanLinkedItem(fresh.MxuPcoItemId!, item.Id, item.Name, item.ItemKind, item.RefId, item.ArrangementId, item.Duration, item.MxuPcoSongId);

        // The song. While Planning Center's song for the row is the same one, the row keeps the library
        // song it plays (if the library still has it): a second copy appearing in the library mustn't
        // move it. When Planning Center swaps the song, the row follows unless the operator chose one.
        var keepsSong = item.ItemKind == ServiceItemKind.Presentation && fresh.MxuPcoSongId == was.PcoSongId
            && (library is null || library.Get<Presentation>(item.RefId) is not null);
        if (keepsSong)
        {
            if ((item.ItemKind, item.RefId) != (was.ItemKind, was.RefId)) keptOwn = true;
        }
        else
        {
            (item.ItemKind, item.RefId) = Take((item.ItemKind, item.RefId), (was.ItemKind, was.RefId), (fresh.ItemKind, fresh.RefId), known);
        }
        // The arrangement only follows Planning Center's when the row plays the song Planning Center's is for.
        if (item.RefId == fresh.RefId) item.ArrangementId = Take(item.ArrangementId, was.ArrangementId, fresh.ArrangementId, known);
        item.Name = Take(item.Name, was.Name, fresh.Name, known);
        item.Duration = Take(item.Duration, was.Duration, fresh.Duration, known);
        if (item.HiddenInPresenter == true || item.OutputPresetId is not null) keptOwn = true;

        // Plan facts nobody edits here are always Planning Center's.
        if (PlanFacts.Of(item) != PlanFacts.Of(fresh) || !MxuJson.StructurallyEqual(item.MxuNotes, fresh.MxuNotes)) changed = true;
        PlanFacts.Of(fresh).CopyTo(item);
        item.MxuNotes = fresh.MxuNotes;
        return (item, changed, keptOwn);
    }

    /// <summary>The Planning Center facts on a row: refreshed on every import, never edited here.</summary>
    private sealed record PlanFacts(
        string? Kind, string? SongId, string? ArrangementId, string? KeyId, string? SongTitle, int? Ccli, string? Author,
        string? ArrangementName, string? SongKey, string? Description, string? ServicePosition, double? Bpm, string? TimeSignature)
    {
        public static PlanFacts Of(ServiceItem i) => new(
            i.MxuKind, i.MxuPcoSongId, i.MxuPcoArrangementId, i.MxuPcoKeyId, i.MxuSongTitle, i.MxuCcliNumber, i.MxuSongAuthor,
            i.MxuArrangementName, i.MxuSongKey, i.MxuDescription, i.MxuServicePosition, i.MxuBpm, i.MxuTimeSignature);

        public void CopyTo(ServiceItem i)
        {
            i.MxuKind = Kind;
            i.MxuPcoSongId = SongId;
            i.MxuPcoArrangementId = ArrangementId;
            i.MxuPcoKeyId = KeyId;
            i.MxuSongTitle = SongTitle;
            i.MxuCcliNumber = Ccli;
            i.MxuSongAuthor = Author;
            i.MxuArrangementName = ArrangementName;
            i.MxuSongKey = SongKey;
            i.MxuDescription = Description;
            i.MxuServicePosition = ServicePosition;
            i.MxuBpm = Bpm;
            i.MxuTimeSignature = TimeSignature;
        }
    }
}
