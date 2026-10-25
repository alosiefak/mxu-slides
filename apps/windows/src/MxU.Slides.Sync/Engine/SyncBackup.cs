using MxU.Slides.Core.Library;
using MxU.Slides.Sync.Format;

namespace MxU.Slides.Sync.Engine;

/// <summary>
/// The backups made before the first sync, so neither side can lose anything to it: every
/// Windows document (and its Windows extras), and every document in the shared folder, of every
/// kind, copied into "Library sync/backups/&lt;date and time&gt;/Windows" and ".../Mac". Media files
/// aren't copied: sync only ever adds them, never changes or removes one.
/// </summary>
public static class SyncBackup
{
    public static string Make(LibraryStore store, SharedFolder shared, string backupsFolder, DateTimeOffset now)
    {
        var folder = Path.Combine(backupsFolder, now.LocalDateTime.ToString("yyyy-MM-dd HHmmss"));
        for (var n = 2; Directory.Exists(folder); n++)
            folder = Path.Combine(backupsFolder, $"{now.LocalDateTime:yyyy-MM-dd HHmmss} ({n})");
        var partial = folder + ".partial";
        if (Directory.Exists(partial)) Directory.Delete(partial, recursive: true);

        var windows = Path.Combine(partial, "Windows");
        foreach (var kind in MacKind.All.Where(k => k.WindowsType is not null))
            CopyFiles(Path.Combine(store.Root, LibraryStore.FolderFor(kind.WindowsType!)), Path.Combine(windows, LibraryStore.FolderFor(kind.WindowsType!)), "*.json");
        CopyTree(Path.Combine(store.Root, WindowsExtras.FolderName), Path.Combine(windows, WindowsExtras.FolderName), "*.json");
        CopyFiles(store.Root, windows, LibraryStore.ManifestFileName);

        var mac = Path.Combine(partial, "Mac");
        foreach (var kind in MacKind.All)
            CopyFiles(shared.KindDirectory(kind), Path.Combine(mac, kind.DirectoryName), "*.automerge");
        CopyFiles(shared.Root, mac, "manifest.json");

        Directory.Move(partial, folder);
        return folder;
    }

    private static void CopyFiles(string from, string to, string pattern)
    {
        if (!Directory.Exists(from)) return;
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, pattern))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
    }

    private static void CopyTree(string from, string to, string pattern)
    {
        if (!Directory.Exists(from)) return;
        foreach (var file in Directory.EnumerateFiles(from, pattern, SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
