using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;
using RV = Pro.SerializationInterop.RVProtoData;
using MapState = MxU.Slides.ProImport.ProDocumentMapper.MapState;

namespace MxU.Slides.ProImport;

public sealed record ProPlaylistPresentationRef(string? AbsolutePath = null, string? RelativePath = null);

public sealed record ProMappedService(Service Service, List<string> Warnings);

public sealed record ProMediaBinFolder(string Path, List<string> WantIds);

/// <summary>ProPresenter playlists → MxU services, and media-bin playlists → media folders.</summary>
public static class ProPlaylistMapper
{
    public static (List<ProMappedService> Services, List<ProMediaWant> MediaWants) MapPresentationPlaylists(
        RV.PlaylistDocument doc, Func<ProPlaylistPresentationRef, string?> resolvePresentation)
    {
        var state = new MapState();
        var services = new List<ProMappedService>();
        foreach (var node in LeafPlaylists(doc.RootNode ?? new RV.Playlist()))
        {
            var warnings = new List<string>();
            var items = new List<ServiceItem>();

            void Append(RV.PlaylistItem item, int depth = 0)
            {
                if (depth >= 4) return;
                var itemId = Identity(item.Uuid, "item");
                switch (item.ItemTypeCase)
                {
                    case RV.PlaylistItem.ItemTypeOneofCase.Header:
                        items.Add(new ServiceItem
                        {
                            Id = itemId, ItemKind = ServiceItemKind.Header,
                            Name = item.Name.Length == 0 ? "Header" : item.Name, RefId = "",
                            ColorHex = item.Header.Color is { } color ? ProDocumentMapper.HexColor(color) : null,
                        });
                        break;
                    case RV.PlaylistItem.ItemTypeOneofCase.Presentation:
                    {
                        var presentation = item.Presentation;
                        if (resolvePresentation(PathRef(presentation.DocumentPath)) is not { } presentationId)
                        {
                            warnings.Add($"\"{ItemName(item, presentation)}\" was not found in the import and was skipped");
                            return;
                        }
                        var arrangement = presentation.Arrangement.Lower();
                        items.Add(new ServiceItem
                        {
                            Id = itemId, ItemKind = ServiceItemKind.Presentation, Name = ItemName(item, presentation),
                            RefId = presentationId, ArrangementId = arrangement.Length == 0 ? null : arrangement,
                        });
                        break;
                    }
                    case RV.PlaylistItem.ItemTypeOneofCase.Cue:
                        if (FirstMediaElement(item.Cue) is { } media)
                            items.Add(new ServiceItem
                            {
                                Id = itemId, ItemKind = ServiceItemKind.Media,
                                Name = item.Name.Length == 0 ? item.Cue.Name : item.Name, RefId = state.Want(media),
                            });
                        else
                            warnings.Add($"\"{item.Name}\" is a cue with no media and was skipped");
                        break;
                    case RV.PlaylistItem.ItemTypeOneofCase.Placeholder:
                        Append(item.Placeholder.LinkedData ?? new RV.PlaylistItem(), depth + 1);
                        break;
                    case RV.PlaylistItem.ItemTypeOneofCase.PlanningCenter:
                        Append(item.PlanningCenter.LinkedData ?? new RV.PlaylistItem(), depth + 1);
                        break;
                    default:
                        warnings.Add($"\"{item.Name}\" carries no content and was skipped");
                        break;
                }
            }
            foreach (var item in PlaylistItems(node).Where(i => !i.IsHidden)) Append(item);
            var service = new Service
            {
                Id = Identity(node.Uuid, "service"),
                Name = node.Name.Length == 0 ? "Imported Playlist" : node.Name,
                ServiceDate = "",
                Items = items,
            };
            services.Add(new ProMappedService(service, warnings));
        }
        return (services, state.Wants);
    }

    public static (List<ProMediaBinFolder> Folders, List<ProMediaWant> MediaWants, List<string> Warnings) MapMediaBinFolders(RV.PlaylistDocument doc)
    {
        var state = new MapState();
        var folders = new List<ProMediaBinFolder>();
        var warnings = new List<string>();
        foreach (var (node, path) in LeafPlaylistPaths(doc.RootNode ?? new RV.Playlist()))
        {
            var wantIds = new List<string>();
            foreach (var item in PlaylistItems(node).Where(i => !i.IsHidden))
            {
                if (item.ItemTypeCase == RV.PlaylistItem.ItemTypeOneofCase.Cue && FirstMediaElement(item.Cue) is { } media)
                    wantIds.Add(state.Want(media));
                else
                    warnings.Add($"media playlist \"{path}\": \"{item.Name}\" is not a media item and was skipped");
            }
            folders.Add(new ProMediaBinFolder(path, wantIds));
        }
        return (folders, state.Wants, warnings);
    }

    /// <summary>Swaps media placeholders for library ids, dropping items whose file wasn't found. Returns a copy.</summary>
    public static (Service Service, List<string> Warnings) ReplacingMediaIds(
        Service service, IReadOnlyDictionary<string, string> map, IReadOnlySet<string> audioIds)
    {
        var result = MxuJson.Clone(service);
        var warnings = new List<string>();
        result.Items = result.Items.Where(item =>
        {
            if (!item.RefId.StartsWith(ProDocumentMapper.PlaceholderPrefix, StringComparison.Ordinal)) return true;
            if (!map.TryGetValue(item.RefId, out var resolved))
            {
                warnings.Add($"\"{item.Name}\": its media file was not found — the item was dropped");
                return false;
            }
            item.RefId = resolved;
            if (item.ItemKind == ServiceItemKind.Media && audioIds.Contains(resolved)) item.ItemKind = ServiceItemKind.Audio;
            return true;
        }).ToList();
        return (result, warnings);
    }

    private static IEnumerable<RV.Playlist> Children(RV.Playlist node)
    {
        var children = node.Children.ToList();
        if (node.ChildrenTypeCase == RV.Playlist.ChildrenTypeOneofCase.Playlists) children.AddRange(node.Playlists.Playlists);
        return children;
    }

    internal static List<RV.Playlist> LeafPlaylists(RV.Playlist node)
    {
        if (node.ChildrenTypeCase == RV.Playlist.ChildrenTypeOneofCase.Items) return [node];
        var children = Children(node).ToList();
        if (children.Count == 0) return node.Type == RV.Playlist.Types.Type.Playlist ? [node] : [];
        return children.SelectMany(LeafPlaylists).ToList();
    }

    internal static List<(RV.Playlist Node, string Path)> LeafPlaylistPaths(RV.Playlist node)
    {
        static List<(RV.Playlist, string)> Walk(RV.Playlist node, List<string> above)
        {
            string Joined() => string.Join("/", above.Append(node.Name.Length == 0 ? "Playlist" : node.Name));
            if (node.ChildrenTypeCase == RV.Playlist.ChildrenTypeOneofCase.Items) return [(node, Joined())];
            var children = Children(node).ToList();
            if (children.Count == 0) return node.Type == RV.Playlist.Types.Type.Playlist ? [(node, Joined())] : [];
            var path = node.Name.Length == 0 ? above : [.. above, node.Name];
            return children.SelectMany(child => Walk(child, path)).ToList();
        }
        if (node.ChildrenTypeCase == RV.Playlist.ChildrenTypeOneofCase.Items) return Walk(node, []);
        return Children(node).SelectMany(root => Walk(root, [])).ToList();
    }

    private static IEnumerable<RV.PlaylistItem> PlaylistItems(RV.Playlist node) =>
        node.ChildrenTypeCase == RV.Playlist.ChildrenTypeOneofCase.Items ? node.Items.Items : [];

    private static string Identity(RV.UUID? uuid, string fallbackPrefix)
    {
        var raw = uuid.Lower();
        return raw.Length == 0 ? $"{fallbackPrefix}-{Guid.NewGuid().ToString().ToLowerInvariant()}" : raw;
    }

    private static string ItemName(RV.PlaylistItem item, RV.PlaylistItem.Types.Presentation presentation)
    {
        if (item.Name.Length > 0) return item.Name;
        var path = PathRef(presentation.DocumentPath);
        var file = path.RelativePath ?? path.AbsolutePath;
        return file is null ? "Presentation" : Path.GetFileNameWithoutExtension(file.Split('/', '\\').Last());
    }

    internal static ProPlaylistPresentationRef PathRef(RV.URL? url)
    {
        if (url is null) return new ProPlaylistPresentationRef();
        string? absolute = null, relative = null;
        if (url is { StorageCase: RV.URL.StorageOneofCase.AbsoluteString, AbsoluteString.Length: > 0 })
            absolute = SwiftCompat.UrlPath(url.AbsoluteString);
        if (url is { RelativeFilePathCase: RV.URL.RelativeFilePathOneofCase.Local } && url.Local.Path.Length > 0)
            relative = url.Local.Path;
        else if (url is { StorageCase: RV.URL.StorageOneofCase.RelativePath, RelativePath.Length: > 0 })
            relative = url.RelativePath;
        return new ProPlaylistPresentationRef(absolute, relative);
    }

    internal static RV.Media? FirstMediaElement(RV.Cue cue) =>
        cue.Actions.FirstOrDefault(a => a.ActionTypeDataCase == RV.Action.ActionTypeDataOneofCase.Media) is { } action
            ? action.Media.Element ?? new RV.Media()
            : null;
}
