using System.IO.Compression;

namespace MxU.Slides.Core.Library;

/// <summary>
/// Unpacks a zip someone else made (a .mxuslides from another computer) without letting it
/// do harm: every name must stay inside the target folder and be an ordinary Windows file
/// name, links are refused, and the total unpacked size is capped against the zip's own size,
/// so a small file can't expand into a disk-filling "zip bomb". Sizes are counted on the bytes
/// actually inflated, not the ones the headers claim. The Mac's resource-fork entries
/// (__MACOSX/, ._name) are left out, as the Mac's own unzip (ditto) would turn them back into
/// file metadata rather than files.
/// </summary>
public static class SafeZip
{
    public sealed class UnsafeArchiveException(string message) : IOException(message);

    public const int MaxEntries = 20_000;
    public const long ExpansionHeadroom = 256L << 20;
    public const long MaxExpansionRatio = 100;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Unpacks <paramref name="archivePath"/> into <paramref name="destination"/> (which should be new and empty).</summary>
    /// <exception cref="InvalidDataException">It isn't a zip.</exception>
    /// <exception cref="UnsafeArchiveException">It is, but unpacking it would be unsafe.</exception>
    public static void Extract(string archivePath, string destination, CancellationToken cancellationToken = default)
    {
        using var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        if (archive.Entries.Count > MaxEntries) throw new UnsafeArchiveException($"The file holds more than {MaxEntries:N0} entries.");
        var allowance = ExpansionHeadroom + file.Length * MaxExpansionRatio;

        var wanted = new List<(ZipArchiveEntry Entry, string Name)>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long declared = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.StartsWith("__MACOSX/", StringComparison.Ordinal) || name.Split('/').Any(part => part.StartsWith("._", StringComparison.Ordinal)))
                continue;
            if (name is "" or "./" or "/") continue;
            if (!IsSafeRelativePath(name) || IsLink(entry) || !names.Add(name.TrimEnd('/')))
                throw new UnsafeArchiveException($"The file contains an unsafe entry: {entry.FullName}");
            declared += entry.Length;
            if (entry.Length < 0 || declared > allowance) throw TooLarge();
            wanted.Add((entry, name));
        }

        Directory.CreateDirectory(destination);
        var buffer = new byte[81_920];
        long written = 0;
        foreach (var (entry, name) in wanted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, name.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar));
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

    private static UnsafeArchiveException TooLarge() =>
        new("The file expands to far more data than it holds. It may be damaged, or a zip bomb.");

    private static bool IsLink(ZipArchiveEntry entry)
    {
        const int UnixFileType = 0xF000, UnixSymlink = 0xA000;
        return ((entry.ExternalAttributes >> 16) & UnixFileType) == UnixSymlink;
    }

    /// <summary>A forward-slash relative path whose every part is an ordinary Windows file name.</summary>
    internal static bool IsSafeRelativePath(string name)
    {
        if (name.StartsWith('/') || name.Contains(':')) return false;
        var parts = name.TrimEnd('/').Split('/');
        foreach (var part in parts)
        {
            if (part.Length == 0 || part is "." or "..") return false;
            if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.EndsWith('.') || part.EndsWith(' ')) return false;
            if (ReservedNames.Contains(part.Split('.')[0])) return false;
        }
        return true;
    }
}
