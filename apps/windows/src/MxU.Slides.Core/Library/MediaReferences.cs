using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Library;

/// <summary>
/// Which media items a document uses: on a slide (a media object, a picture fill, a
/// background, a "fire media" action), on a timeline, in a playlist or a service. Port of the
/// Mac's MediaReferences. Used to find who still needs a file (deleting, replacing, missing
/// files) and what to pack into a .mxuslides file. Empty ids are left out.
/// </summary>
public static class MediaReferences
{
    public static HashSet<string> Ids(IEnumerable<SlideObject> objects)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in objects)
        {
            Add(ids, o.MediaId);
            Add(ids, o.Fill?.MediaId);
        }
        return ids;
    }

    public static HashSet<string> Ids(IEnumerable<SlideAction>? actions)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in actions ?? []) Add(ids, action.MediaId);
        return ids;
    }

    public static HashSet<string> Ids(Slide slide)
    {
        var ids = Ids(slide.Objects);
        Add(ids, slide.Background?.MediaId);
        Add(ids, slide.BackgroundFill?.MediaId);
        ids.UnionWith(Ids(slide.Actions));
        return ids;
    }

    public static HashSet<string> Ids(Presentation presentation)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        Add(ids, presentation.Background?.MediaId);
        Add(ids, presentation.BackgroundFill?.MediaId);
        foreach (var section in presentation.Sections ?? []) Add(ids, section.Background?.MediaId);
        foreach (var slide in presentation.Slides) ids.UnionWith(Ids(slide));
        foreach (var timeline in presentation.Timelines ?? [])
        {
            foreach (var cue in timeline.Cues)
            {
                Add(ids, cue.MediaId);
                ids.UnionWith(Ids(cue.Actions));
            }
        }
        foreach (var cue in presentation.SongMap?.Recording ?? [])
        {
            Add(ids, cue.MediaId);
            ids.UnionWith(Ids(cue.Actions));
        }
        return ids;
    }

    public static HashSet<string> Ids(Overlay overlay) => Ids(overlay.Objects);

    public static HashSet<string> Ids(Theme theme)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slide in theme.Slides ?? []) ids.UnionWith(Ids(slide));
        return ids;
    }

    public static HashSet<string> Ids(ConfidenceLayout layout) => Ids(layout.Objects);

    public static HashSet<string> Ids(Playlist playlist) =>
        new(playlist.Entries.Where(e => e.RefKind == PlaylistRefKind.Media).Select(e => e.RefId).Where(id => id.Length > 0), StringComparer.Ordinal);

    /// <summary>The media rows of a service, including the rows only its versions (other times, other rooms) have.</summary>
    public static HashSet<string> Ids(Service service) =>
        new(AllItems(service).Where(i => i.ItemKind == ServiceItemKind.Media).Select(i => i.RefId).Where(id => id.Length > 0), StringComparer.Ordinal);

    public static HashSet<string> Ids(ActionCombo combo) => Ids(combo.Actions);

    public static HashSet<string> Ids(ScheduleTrigger trigger) => Ids(trigger.Actions);

    /// <summary>
    /// Every row any version of the service shows: the main list, each version's changed rows
    /// (a version can point a row at other media) and the rows a version adds. The Mac gets
    /// these from ServiceVersions.allItems; only which rows exist matters here, not their order.
    /// </summary>
    private static IEnumerable<ServiceItem> AllItems(Service service)
    {
        foreach (var item in service.Items) yield return item;
        foreach (var version in service.Versions ?? [])
        {
            foreach (var change in version.Changes ?? [])
            {
                if (service.Items.FirstOrDefault(i => i.Id == change.ItemId) is not { } item) continue;
                yield return new ServiceItem
                {
                    Id = item.Id,
                    Name = item.Name,
                    ItemKind = change.ItemKind ?? item.ItemKind,
                    RefId = change.RefId ?? item.RefId,
                };
            }
            foreach (var added in version.Added ?? []) yield return added;
        }
    }

    private static void Add(HashSet<string> ids, string? id)
    {
        if (!string.IsNullOrEmpty(id)) ids.Add(id);
    }
}
