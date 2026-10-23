using System.IO.Compression;
using MxU.Slides.Core.Library;

namespace MxU.Slides.PptxImport;

internal enum PptxImportErrorKind
{
    ExtractFailed,
    InvalidPackage,
}

internal sealed class PptxImportException : Exception
{
    private PptxImportException(PptxImportErrorKind kind, string message) : base(message) => Kind = kind;

    public PptxImportErrorKind Kind { get; }

    public static PptxImportException ExtractFailed(string detail) =>
        new(PptxImportErrorKind.ExtractFailed, detail.Length == 0 ? "the file could not be extracted" : detail);

    public static PptxImportException InvalidPackage(string detail) => new(PptxImportErrorKind.InvalidPackage, detail);
}

/// <summary>
/// Unpacks a .pptx into a fresh temp folder. Every entry is checked first: no
/// links, no names that leave the folder or mean something special to Windows,
/// no duplicates, and the total size is capped against the archive's own size so
/// a small file cannot expand into a zip bomb. Sizes are enforced on the bytes
/// actually inflated, not just the ones the headers declare.
/// </summary>
internal static class PptxArchive
{
    public const int MaxEntries = 20_000;
    public const long MaxTotalBytes = 4L << 30;
    public const long ExpansionHeadroom = 256L << 20;
    public const long MaxExpansionRatio = 100;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    public static string Extract(string path, CancellationToken cancellationToken = default)
    {
        var destination = Path.Combine(Path.GetTempPath(), $"pptx-{LibraryStore.NewId()}");
        Directory.CreateDirectory(destination);
        var succeeded = false;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            ZipArchive archive;
            try
            {
                archive = new ZipArchive(file, ZipArchiveMode.Read);
            }
            catch (InvalidDataException)
            {
                throw PptxImportException.ExtractFailed("the file is not a PowerPoint package (it may be damaged or password-protected)");
            }
            using (archive)
            {
                try
                {
                    ExtractEntries(archive, file.Length, destination, cancellationToken);
                }
                catch (InvalidDataException e)
                {
                    throw PptxImportException.ExtractFailed(e.Message);
                }
            }
            succeeded = true;
            return destination;
        }
        finally
        {
            if (!succeeded) TryDelete(destination);
        }
    }

    private static void ExtractEntries(ZipArchive archive, long archiveLength, string destination, CancellationToken cancellationToken)
    {
        if (archive.Entries.Count > MaxEntries)
            throw PptxImportException.ExtractFailed($"the file holds more than {MaxEntries:N0} parts");
        var allowance = Math.Min(MaxTotalBytes, ExpansionHeadroom + Math.Min(archiveLength, MaxTotalBytes) * MaxExpansionRatio);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long declared = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (!IsSafeRelativePath(name) || IsSymbolicLink(entry) || !names.Add(name.TrimEnd('/')))
                throw PptxImportException.ExtractFailed($"The archive contains an unsafe file: {entry.FullName}.");
            declared += entry.Length;
            if (entry.Length < 0 || entry.Length > allowance || declared > allowance) throw TooLarge();
        }

        var buffer = new byte[81_920];
        long written = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            var target = Path.Combine(destination, name.Replace('/', Path.DirectorySeparatorChar));
            if (name.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
            long copied = 0;
            int read;
            while ((read = input.Read(buffer)) > 0)
            {
                copied += read;
                if (copied > entry.Length || written + copied > allowance) throw TooLarge();
                output.Write(buffer, 0, read);
            }
            written += copied;
        }
    }

    private static PptxImportException TooLarge() =>
        PptxImportException.ExtractFailed("the file expands to more data than a presentation should — it may be damaged or a zip bomb");

    private static bool IsSymbolicLink(ZipArchiveEntry entry)
    {
        const int UnixFileType = 0xF000, UnixSymlink = 0xA000;
        return ((entry.ExternalAttributes >> 16) & UnixFileType) == UnixSymlink;
    }

    /// <summary>A forward-slash relative path whose every component is an ordinary Windows file name.</summary>
    public static bool IsSafeRelativePath(string path)
    {
        if (path.Length == 0 || path[0] == '/') return false;
        var components = path.EndsWith('/') ? path[..^1].Split('/') : path.Split('/');
        return components.All(IsSafeComponent);
    }

    private static bool IsSafeComponent(string name)
    {
        if (name.Length == 0 || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ')) return false;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        var stem = name.Split('.')[0].TrimEnd(' ');
        return !ReservedNames.Contains(stem);
    }

    public static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
