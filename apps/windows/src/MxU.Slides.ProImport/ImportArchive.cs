using System.IO.Compression;

namespace MxU.Slides.ProImport;

/// <summary>
/// Extracts ProPresenter's zip packages (.probundle, .proPlaylist, .protheme) into a fresh temp folder,
/// refusing anything that could reach outside it: symbolic links, device or pipe entries, and paths
/// that climb out of the folder. macOS resource-fork entries (__MACOSX, ._*) are left out, as ditto does.
/// </summary>
internal static class ImportArchive
{
    public sealed class UnsafeArchiveException(string message) : IOException(message);

    private const int FileTypeMask = 0xF000;
    private const int RegularFile = 0x8000;
    private const int Directory = 0x4000;

    public static string Extract(string archivePath)
    {
        var destination = Path.Combine(Path.GetTempPath(), "probundle-" + Guid.NewGuid().ToString().ToUpperInvariant());
        System.IO.Directory.CreateDirectory(destination);
        var succeeded = false;
        try
        {
            var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                foreach (var entry in archive.Entries)
                {
                    var name = entry.FullName.Replace('\\', '/');
                    if (name.StartsWith("__MACOSX/", StringComparison.Ordinal) || Path.GetFileName(name.TrimEnd('/')).StartsWith("._", StringComparison.Ordinal))
                        continue;
                    var type = (entry.ExternalAttributes >> 16) & FileTypeMask;
                    if (type is not (0 or RegularFile or Directory))
                        throw new UnsafeArchiveException($"The archive contains an unsafe file: {name}.");
                    var target = Path.GetFullPath(Path.Combine(root, name));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) && target + Path.DirectorySeparatorChar != root)
                        throw new UnsafeArchiveException($"The archive contains an unsafe file: {name}.");
                    if (name.EndsWith('/') || type == Directory)
                    {
                        System.IO.Directory.CreateDirectory(target);
                        continue;
                    }
                    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                }
            }
            ValidateExtractedFiles(destination);
            succeeded = true;
            return destination;
        }
        finally
        {
            if (!succeeded) TryDelete(destination);
        }
    }

    /// <summary>Checked before reading imported content: only plain files and folders, no links or reparse points.</summary>
    public static void ValidateExtractedFiles(string root)
    {
        IEnumerable<FileSystemInfo> entries;
        try
        {
            entries = new DirectoryInfo(root).EnumerateFileSystemInfos("*", new EnumerationOptions
            {
                RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = 0,
            }).ToList();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new UnsafeArchiveException("The extracted archive could not be inspected.");
        }
        foreach (var entry in entries)
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) || entry.LinkTarget is not null
                || entry.Attributes.HasFlag(FileAttributes.Device))
                throw new UnsafeArchiveException($"The archive contains an unsafe file: {entry.Name}.");
        }
    }

    public static void TryDelete(string? directory)
    {
        if (directory is null) return;
        try
        {
            if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A leftover temp folder is harmless.
        }
    }
}
