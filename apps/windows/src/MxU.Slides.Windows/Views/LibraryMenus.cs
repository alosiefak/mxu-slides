using System.Windows;
using System.Windows.Controls;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.ViewModels;

namespace MxU.Slides.Windows.Views;

/// <summary>
/// The right-click menus of the library list and the media bin, for one item or several picked
/// together, built as they open (what's in them depends on what was clicked and how many are
/// picked). Ports of the Mac's LibraryView menus: singleEntryMenu, massEntryMenu ("Add 3 to
/// Service", "Move to Folder ›", "Delete 3 Items") and folderMenu, with Windows' extras
/// (favourites and tags for songs, delete and move for folders, export for several).
/// </summary>
internal static class LibraryMenus
{
    private static Style Cascading => (Style)EditorKit.Styles["CascadingMenuItem"];

    public static MenuItem Item(string title, Action click, bool enabled = true) => EditorKit.Command(title, click, enabled);

    public static MenuItem Sub(string title, IEnumerable<object> children)
    {
        var item = new MenuItem { Header = title, Style = Cascading };
        foreach (var child in children) item.Items.Add(child);
        return item;
    }

    private static string Checked(string title, bool on) => (on ? "✓  " : "     ") + title;

    // MARK: Library rows

    /// <summary>The menu for songs and presentations: <paramref name="ids"/> are what it acts on, in list order.</summary>
    public static void ForPresentations(ContextMenu menu, MainWindow window, MainViewModel model, IReadOnlyList<string> ids)
    {
        var presentations = ids.Select(id => model.Library.Get<Presentation>(id)).OfType<Presentation>().ToList();
        ids = [.. presentations.Select(p => p.Id)];
        if (presentations.Count == 0) return;
        var many = presentations.Count > 1;
        var n = presentations.Count;
        var items = presentations.Select(p => (Kind: LibraryEntry.KindOf(p), p.Id)).ToList();

        menu.Items.Add(Item(many ? $"Add {n} to Service" : "Add to Service", () => model.AddPresentationsToService(ids)));
        if (!many)
        {
            var only = presentations[0];
            if (ChordProSong(only)) menu.Items.Add(Item("Edit Lyrics…", () => window.EditLyricsFromLibrary(only)));
            menu.Items.Add(Item("Rename…", () => window.RenamePresentationFromLibrary(only)));
        }
        menu.Items.Add(Item(many ? $"Duplicate {n}" : "Duplicate", () => model.DuplicatePresentations(ids)));
        menu.Items.Add(FolderMenu(window, model.PresentationFolderTree(), many ? $"Move {n} to Folder" : "Move to Folder",
            presentations.Select(p => p.Folder ?? "").Distinct().Count() == 1 ? presentations[0].Folder ?? "" : null,
            folder => model.MovePresentationsToFolder(ids, folder),
            name => model.NewPresentationFolder(model.LibraryPath, name)));
        menu.Items.Add(ThemeMenu(model, many ? $"Apply Theme to {n}" : "Apply Theme",
            presentations.Select(p => p.ThemeId).Distinct().Count() == 1 ? presentations[0].ThemeId : null,
            themeId => model.ApplyThemeToPresentations(ids, themeId)));
        menu.Items.Add(EditorKit.Separator());
        AddMarks(menu, window, model, items);
        menu.Items.Add(EditorKit.Separator());
        menu.Items.Add(Item(many ? $"Export {n}…" : "Export…", () => window.ExportPresentations(presentations)));
        menu.Items.Add(EditorKit.Separator());
        menu.Items.Add(Item(many ? $"Delete {n} Items" : "Move to Trash", () => model.DeletePresentations(ids)));
    }

    private static bool ChordProSong(Presentation p) => p.PresentationKind == PresentationKind.Song || p.Sections is { Count: > 0 };

    public static void ForFolder(ContextMenu menu, MainWindow window, MainViewModel model, LibraryFolderRowViewModel folder)
    {
        menu.Items.Add(Item("Open", () => model.OpenLibraryFolder(folder.Path)));
        menu.Items.Add(Item("New Folder Inside…", () =>
        {
            if (InputDialog.Ask(window, "New Folder", $"A new folder inside “{folder.Name}”:") is { } name && model.NewPresentationFolder(folder.Path, name) is null)
                model.Problem = "A folder's name can't be empty or have a “/” in it.";
        }));
        menu.Items.Add(Item("Rename Folder…", () =>
        {
            if (InputDialog.Ask(window, "Rename Folder", "Name", folder.Name) is { } name) model.RenamePresentationFolder(folder.Path, name);
        }));
        menu.Items.Add(FolderMoveMenu(LibraryFolders.MoveTargets(model.PresentationFolderPaths(), string.Join("/", folder.Path)),
            target => model.MovePresentationFolder(folder.Path, FolderPaths.Components(target))));
        menu.Items.Add(EditorKit.Separator());
        menu.Items.Add(Item("Delete Folder…", () =>
        {
            var dialog = new ChoiceWindow(window, "Delete Folder", $"Delete the folder “{folder.Name}”?",
                folder.Count == 0 ? "It's empty." : $"Nothing is thrown away: its {(folder.Count == 1 ? "item moves" : folder.Count + " items move")} up a level.");
            dialog.AddButton("Cancel", isCancel: true);
            dialog.AddButton("Delete Folder", isDefault: true, accent: true);
            if (dialog.Ask() == "Delete Folder") model.DeletePresentationFolder(folder.Path);
        }));
    }

    public static void ForEmptySpace(ContextMenu menu, MainWindow window, MainViewModel model)
    {
        menu.Items.Add(Item(model.LibraryPath.Count == 0 ? "New Folder…" : "New Folder Here…", () =>
        {
            if (InputDialog.Ask(window, "New Folder", "Name") is { } name && model.NewPresentationFolder(model.LibraryPath, name) is null)
                model.Problem = "A folder's name can't be empty or have a “/” in it.";
        }));
        menu.Items.Add(Item(model.LibraryShowsAll ? "Show Folders" : "Show Everything", () => model.LibraryShowsAll = !model.LibraryShowsAll));
    }

    /// <summary>
    /// A picture, video or song that a search found: Show in Library (its bin or the Music tab), then
    /// the menu it has there (the Mac's search hits right-click like the item in its library).
    /// </summary>
    public static void ForMediaHit(ContextMenu menu, MainWindow window, MainViewModel model, LibraryMediaHitViewModel hit)
    {
        (LibraryItemKind Kind, string Id) marks = (hit.Entry.Kind, hit.Entry.Id);
        menu.Items.Add(Item("Show in Library", () => model.ShowLibraryHit(hit)));
        menu.Items.Add(EditorKit.Separator());
        if (hit.IsAudio)
        {
            if (model.Library.Get<AudioItem>(hit.Entry.Id) is { } song)
            {
                menu.Items.Add(Item("Play", () => model.AudioTileClickedCommand.Execute(new AudioTileViewModel(song))));
                menu.Items.Add(EditorKit.Separator());
            }
            AddMarks(menu, window, model, [marks]);
            return;
        }
        if (model.Library.Get<MediaItem>(hit.Entry.Id) is not { } media)
        {
            AddMarks(menu, window, model, [marks]);
            return;
        }
        menu.Items.Add(Item("Show Now", () => model.FireMedia(media)));
        menu.Items.Add(Item("Add to Service", () => model.AddMediaToService(media)));
        menu.Items.Add(EditorKit.Separator());
        menu.Items.Add(Item(Checked("Loop", media.Loops), () => model.SetMediaLoops(media, !media.Loops)));
        var background = media.Classification == MediaClassification.Background;
        menu.Items.Add(Item(background ? "Switch to Foreground" : "Switch to Background", () => model.SetMediaClassification(media,
            background ? MediaClassification.Foreground : MediaClassification.Background)));
        menu.Items.Add(EditorKit.Separator());
        foreach (var item in ForOneMedia(window, model, media)) menu.Items.Add(item);
        menu.Items.Add(EditorKit.Separator());
        menu.Items.Add(Item("Move to Trash", () => _ = model.DeleteMediaAsync(media)));
    }

    // MARK: Media tiles

    /// <summary>
    /// The media tile's menu when several tiles are picked and this is one of them: everything in it
    /// acts on all of them. (One tile keeps its own menu, with the favourite, tags and folder added.)
    /// </summary>
    public static void ForMediaBatch(ContextMenu menu, MainWindow window, MainViewModel model, IReadOnlyList<MediaItem> items)
    {
        var n = items.Count;
        var marks = items.Select(i => (LibraryItemKind.Media, i.Id)).ToList();
        menu.Items.Add(Item($"Add {n} to Service", () => model.AddMediaItemsToService(items)));
        menu.Items.Add(MediaFolderMenu(window, model, $"Move {n} to Folder", items));
        menu.Items.Add(Item($"Duplicate {n}", () => model.DuplicateMedia(items)));
        menu.Items.Add(EditorKit.Separator());
        AddMarks(menu, window, model, marks);
        menu.Items.Add(EditorKit.Separator());
        menu.Items.Add(Item($"Copy the {n} Files To…", () => window.ExportMediaFiles(items)));
        menu.Items.Add(Item("Unpick All", model.ClearMediaPicks));
        menu.Items.Add(EditorKit.Separator());
        menu.Items.Add(Item($"Delete {n} Items", () => _ = model.DeleteMediaItemsAsync(items)));
    }

    /// <summary>The extra items a single tile's menu gets: favourite, tags, folder, duplicate, copy the file out.</summary>
    public static IEnumerable<object> ForOneMedia(MainWindow window, MainViewModel model, MediaItem item)
    {
        var holder = new ContextMenu();
        AddMarks(holder, window, model, [(LibraryItemKind.Media, item.Id)]);
        var made = holder.Items.Cast<object>().ToList();
        holder.Items.Clear();
        return
        [
            .. made,
            MediaFolderMenu(window, model, "Move to Folder", [item]),
            Item("Duplicate", () => model.DuplicateMedia([item])),
            Item("Copy the File To…", () => window.ExportMediaFiles([item])),
        ];
    }

    private static MenuItem MediaFolderMenu(MainWindow window, MainViewModel model, string title, IReadOnlyList<MediaItem> items) =>
        FolderMenu(window, LibraryFolders.Tree(model.MediaBinFolders()), title,
            items.Select(i => i.Folder ?? "").Distinct().Count() == 1 ? items[0].Folder ?? "" : null,
            folder => model.MoveMediaToFolder(items, folder),
            model.NewMediaFolder);

    // MARK: Pieces

    /// <summary>Favourite, Tags… (one item) and Tag › (add or take off one tag on all of them).</summary>
    private static void AddMarks(ContextMenu menu, MainWindow window, MainViewModel model, IReadOnlyList<(LibraryItemKind Kind, string Id)> items)
    {
        var marks = model.Catalog.Marks;
        var allFavorite = items.All(i => marks.IsFavorite(i.Kind, i.Id));
        menu.Items.Add(Item(allFavorite ? (items.Count > 1 ? "Remove from Favourites" : "Remove Favourite") : (items.Count > 1 ? $"Add {items.Count} to Favourites" : "Add to Favourites"),
            () => model.SetFavorite(items, !allFavorite)));
        if (items.Count == 1)
        {
            var (kind, id) = items[0];
            menu.Items.Add(Item("Tags…", () =>
            {
                if (InputDialog.Ask(window, "Tags", "Tags, separated by commas (Easter, Christmas, Hymn):", string.Join(", ", marks.Tags(kind, id))) is { } typed)
                    model.SetTags(kind, id, typed);
            }));
        }
        var tags = model.LibraryTags();
        var children = new List<object>();
        foreach (var tag in tags)
        {
            var all = items.All(i => marks.Tags(i.Kind, i.Id).Contains(tag, StringComparer.OrdinalIgnoreCase));
            children.Add(Item(Checked(tag, all), () => model.Tag(items, tag, add: !all)));
        }
        if (children.Count > 0) children.Add(EditorKit.Separator());
        children.Add(Item("New Tag…", () =>
        {
            if (InputDialog.Ask(window, "New Tag", "Tag") is { } typed && LibraryMarks.Parse(typed) is [var tag, ..]) model.Tag(items, tag, add: true);
        }));
        menu.Items.Add(Sub("Tag", children));
    }

    /// <summary>
    /// "Move to Folder ›": none, then each folder (one with folders inside opens to them, its own
    /// name first), then New Folder…. The folder it's in now is ticked.
    /// </summary>
    private static MenuItem FolderMenu(Window owner, IReadOnlyList<FolderNode> tree, string title, string? current, Action<string?> pick,
        Func<string, string?>? newFolder)
    {
        var children = new List<object> { Item(Checked("None", current == ""), () => pick(null)) };
        if (tree.Count > 0) children.Add(EditorKit.Separator());
        children.AddRange(Nodes(tree));
        if (newFolder is not null)
        {
            children.Add(EditorKit.Separator());
            children.Add(Item("New Folder…", () =>
            {
                if (InputDialog.Ask(owner, "New Folder", "Name") is { } name && newFolder(name) is { } made) pick(made);
            }));
        }
        return Sub(title, children);

        IEnumerable<object> Nodes(IReadOnlyList<FolderNode> nodes)
        {
            foreach (var node in nodes)
            {
                var path = node.Path;
                if (node.Children.Count == 0) yield return Item(Checked(node.Name, current == path), () => pick(path));
                else
                    yield return Sub(node.Name, [Item(Checked($"“{node.Name}”", current == path), () => pick(path)), EditorKit.Separator(), .. Nodes(node.Children)]);
            }
        }
    }

    /// <summary>
    /// A folder's "Move to Folder" submenu (the Mac's folderMoveMenu): Top Level when it's inside another
    /// folder, then the folders it can go into as a tree, where it is now ticked and not offered.
    /// Greyed when there's nowhere to go.
    /// </summary>
    private static MenuItem FolderMoveMenu(FolderMoveTargets targets, Action<string> pick)
    {
        var children = new List<object>();
        if (targets.TopLevel) children.Add(Item("Top Level", () => pick("")));
        if (targets.TopLevel && targets.Folders.Count > 0) children.Add(EditorKit.Separator());
        children.AddRange(Nodes(LibraryFolders.Tree(targets.Folders)));
        var menu = Sub("Move to Folder", children);
        menu.IsEnabled = targets.Any;
        return menu;

        IEnumerable<object> Nodes(IReadOnlyList<FolderNode> nodes)
        {
            foreach (var node in nodes)
            {
                var path = node.Path;
                var here = path == targets.Current;
                if (node.Children.Count == 0) yield return Item(Checked(node.Name, here), () => pick(path), enabled: !here);
                else yield return Sub(node.Name, [Item(Checked("“" + node.Name + "”", here), () => pick(path), enabled: !here), EditorKit.Separator(), .. Nodes(node.Children)]);
            }
        }
    }

    private static MenuItem ThemeMenu(MainViewModel model, string title, string? current, Action<string> pick) =>
        Sub(title, model.Themes.Where(t => t.Id.Length > 0).Select(theme => (object)Item(Checked(theme.Name, current == theme.Id), () => pick(theme.Id))));
}
