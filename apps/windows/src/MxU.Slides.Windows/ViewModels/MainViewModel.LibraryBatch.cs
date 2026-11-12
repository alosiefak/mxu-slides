using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.Views;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// The library's actions on one song or many (picked with Ctrl+click and Shift+click): add to the
/// service, file into a folder, duplicate, apply a theme, favourite and tag, export, delete. And
/// the folders themselves: new, rename, move, delete. What each does to the documents is Core's
/// (LibraryBatch.cs, LibraryFolders); this saves the result, all in one batch so the lists redraw
/// once. Ports of the Mac's LibraryView menus (massEntryMenu, singleEntryMenu, folderMenu).
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// Asked before presentations are deleted: their names, and the services still listing some of
    /// them. True to go ahead. (The self-test answers it itself.)
    /// </summary>
    public Func<IReadOnlyList<string>, IReadOnlyList<LibraryBatch.InService>, bool> ConfirmDeletePresentations { get; set; } =
        (names, used) => LibraryDepthDialogs.ConfirmDeletePresentations(names, used);

    private List<Presentation> PresentationsById(IEnumerable<string> ids) =>
        [.. ids.Select(id => Library.Get<Presentation>(id)).OfType<Presentation>()];

    /// <summary>Adds each to the open service, in list order, after the selected row (the Mac's "Add 3 to Service").</summary>
    public void AddPresentationsToService(IReadOnlyList<string> ids)
    {
        if (SelectedService is null) NewService();
        if (SelectedService is not { } service) return;
        var added = 0;
        foreach (var p in PresentationsById(ids))
        {
            InsertServiceItem(service, new ServiceItem { Id = LibraryStore.NewId(), ItemKind = ServiceItemKind.Presentation, Name = p.Name, RefId = p.Id });
            added++;
        }
        if (added > 1) Notice = $"Added {added} items to {service.Name}.";
    }

    /// <summary>Files each into <paramref name="folder"/> (null or "" for none).</summary>
    public void MovePresentationsToFolder(IReadOnlyList<string> ids, string? folder)
    {
        using (BatchOfChanges())
            foreach (var p in PresentationsById(ids))
                if (Core.Library.LibraryBatch.File(p, folder)) Library.Save(p);
        // A folder that now holds something needn't be kept as an empty one.
        if (folder is { Length: > 0 }) KeepEmptyFolders(Settings.EmptyFolders(PresentationsList).Where(f => f != folder));
        Notice = ids.Count == 1
            ? (folder is { Length: > 0 } ? $"Moved to {folder.Replace("/", " › ")}." : "Moved out of its folder.")
            : $"Moved {ids.Count} items {(folder is { Length: > 0 } ? "to " + folder.Replace("/", " › ") : "out of their folders")}.";
    }

    public List<Presentation> DuplicatePresentations(IReadOnlyList<string> ids)
    {
        var copies = new List<Presentation>();
        using (BatchOfChanges())
            foreach (var p in PresentationsById(ids))
            {
                var copy = Core.Library.LibraryBatch.Duplicate(p, DateTimeOffset.Now);
                TransitionExtras.Copied(p, copy);
                Library.Save(copy);
                Catalog.Marks.Copy(p.Id, copy.Id);
                copies.Add(copy);
            }
        if (copies.Count == 1) Open(copies[0], null, null);
        else Notice = $"Made {copies.Count} copies.";
        return copies;
    }

    /// <summary>Gives each the theme (the Mac's Apply Theme… on several); the open one redraws at once.</summary>
    public void ApplyThemeToPresentations(IReadOnlyList<string> ids, string themeId)
    {
        var changed = 0;
        using (BatchOfChanges())
            foreach (var p in PresentationsById(ids))
                if (Core.Library.LibraryBatch.ApplyTheme(p, themeId))
                {
                    SavePresentation(p);
                    changed++;
                }
        if (CurrentPresentation is { } open && ids.Contains(open.Id))
        {
            CurrentTheme = Library.Get<Theme>(open.ThemeId);
            RebuildSlides();
            OnPropertyChanged(nameof(CurrentThemeChoice));
        }
        var name = Library.Get<Theme>(themeId)?.Name ?? "no theme";
        Notice = changed == 0 ? $"They already use {name}." : changed == 1 ? $"Now drawn with {name}." : $"{changed} presentations now drawn with {name}.";
    }

    public void SetFavorite(IEnumerable<(LibraryItemKind Kind, string Id)> items, bool favorite)
    {
        using (BatchOfChanges())
            foreach (var (kind, id) in items) Catalog.Marks.SetFavorite(kind, id, favorite);
    }

    public void SetTags(LibraryItemKind kind, string id, string typed) => Catalog.Marks.SetTags(kind, id, LibraryMarks.Parse(typed));

    public void Tag(IReadOnlyList<(LibraryItemKind Kind, string Id)> items, string tag, bool add)
    {
        using (BatchOfChanges()) Catalog.Marks.Tag(items, tag, add);
    }

    /// <summary>
    /// Moves each to the library's Trash, first saying which services still list them (their rows
    /// then say the presentation is missing). Several at once always ask. Returns whether it went ahead.
    /// </summary>
    public bool DeletePresentations(IReadOnlyList<string> ids)
    {
        var doomed = PresentationsById(ids);
        if (doomed.Count == 0) return false;
        var used = Core.Library.LibraryBatch.ServicesUsing(Library, ids);
        if ((doomed.Count > 1 || used.Count > 0) && !ConfirmDeletePresentations(doomed.Select(p => p.Name).ToList(), used)) return false;
        using (BatchOfChanges())
            foreach (var p in doomed)
            {
                Library.Delete<Presentation>(p.Id);
                Catalog.Marks.Forget(p.Id);
            }
        if (CurrentPresentation is { } open && doomed.Any(p => p.Id == open.Id))
        {
            CurrentPresentation = null;
            Slides.Clear();
            OnPropertyChanged(nameof(HasPresentation));
            OnPropertyChanged(nameof(CurrentTitle));
            OnPropertyChanged(nameof(CurrentSubtitle));
        }
        RefreshServiceItems();
        Notice = doomed.Count == 1 ? $"Moved \"{doomed[0].Name}\" to the library's Trash folder." : $"Moved {doomed.Count} items to the library's Trash folder.";
        return true;
    }

    // MARK: Folders

    private void KeepEmptyFolders(IEnumerable<string> folders)
    {
        (Settings.LibraryEmptyFolders ??= [])[PresentationsList] = [.. folders.Distinct(StringComparer.Ordinal)];
        Settings.Save();
    }

    /// <summary>A new folder inside <paramref name="parent"/>: kept empty until something is filed in it. Returns its path, or null for a name that can't be used.</summary>
    public string? NewPresentationFolder(IReadOnlyList<string> parent, string typed)
    {
        if (LibraryFolders.CleanName(typed) is not { } name) return null;
        var path = LibraryFolders.Join(parent, name);
        if (!LibraryFolders.Folders(Catalog.Presentations(), Settings.EmptyFolders(PresentationsList)).Contains(path))
            KeepEmptyFolders([.. Settings.EmptyFolders(PresentationsList), path]);
        RefreshLibrary();
        return path;
    }

    public void RenamePresentationFolder(IReadOnlyList<string> path, string typed) =>
        ApplyFolderMove(LibraryFolders.Rename(Catalog.Presentations(), Settings.EmptyFolders(PresentationsList), path, typed), "Renamed the folder.");

    public void MovePresentationFolder(IReadOnlyList<string> path, IReadOnlyList<string> into) =>
        ApplyFolderMove(LibraryFolders.Move(Catalog.Presentations(), Settings.EmptyFolders(PresentationsList), path, into), "Moved the folder.");

    /// <summary>Deletes a folder; what was in it moves up a level (nothing is thrown away).</summary>
    public void DeletePresentationFolder(IReadOnlyList<string> path)
    {
        var move = LibraryFolders.Delete(Catalog.Presentations(), Settings.EmptyFolders(PresentationsList), path);
        var count = move.Refiles.Count;
        ApplyFolderMove(move, count == 0 ? "Deleted the folder." : $"Deleted the folder; its {(count == 1 ? "item" : count + " items")} moved up a level.");
    }

    private void ApplyFolderMove(FolderMove move, string notice)
    {
        if (!move.Changed) return;
        using (BatchOfChanges())
            foreach (var refile in move.Refiles)
                if (Library.Get<Presentation>(refile.Entry.Id) is { } p && Core.Library.LibraryBatch.File(p, refile.Folder)) Library.Save(p);
        KeepEmptyFolders(move.Empties);
        // Back to the top: the folder being looked at may not be where it was any more.
        if (LibraryPath.Count > 0) LibraryPath = [];
        RefreshLibrary();
        Notice = notice;
    }
}
