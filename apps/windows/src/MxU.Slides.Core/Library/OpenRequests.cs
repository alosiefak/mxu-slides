using System.Buffers.Binary;
using System.Text.Json;

namespace MxU.Slides.Core.Library;

/// <summary>
/// Files the app is asked to open from outside: double-clicked in Explorer, chosen with "Open
/// with", or dropped on its icon. Windows starts a new copy of the app with the file's path on the
/// command line; if a copy is already running, the new one hands the paths to it and goes (the
/// app's FileHandoff.cs, through a named pipe), so the file lands in the library already open.
/// The Mac gets the same from macOS as <c>application(_:open:)</c> (PresentationFileOpener).
/// <para>This is the part with rules: which arguments are files, what each kind of file is, and
/// the little message one copy sends the other.</para>
/// </summary>
public static class OpenRequests
{
    public enum Kind
    {
        /// <summary>An MxU Slides presentation (.mxuslides), from a Mac or another PC.</summary>
        SlidesFile,
        PowerPoint,
        /// <summary>ProPresenter's .pro (or a ChordPro .pro: the importer tells them apart), bundles and playlists.</summary>
        ProPresenter,
        /// <summary>Lyrics: text, ChordPro, SongSelect.</summary>
        Lyrics,
        /// <summary>A library backup (.mxubackup): opens the restore preview, never restores by itself.</summary>
        Backup,
        Unsupported,
    }

    /// <summary>The most files one message carries (a drop of more is cut there, rather than flooding the import).</summary>
    public const int MaxFiles = 200;

    private const int MaxMessageBytes = 1 << 20;

    public static Kind KindOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        "." + SlidesPresentationFile.FileExtension => Kind.SlidesFile,
        ".pptx" => Kind.PowerPoint,
        ".pro" or ".probundle" or ".proplaylist" => Kind.ProPresenter,
        ".txt" or ".cho" or ".chopro" or ".chordpro" or ".crd" => Kind.Lyrics,
        "." + LibraryBackup.FileExtension => Kind.Backup,
        _ => Kind.Unsupported,
    };

    /// <summary>
    /// The files among the command-line arguments: everything that isn't a switch ("--self-test"
    /// and the folder after it are the self-test's, not a file to import), made a full path against
    /// the folder the new copy was started in, each once.
    /// </summary>
    public static IReadOnlyList<string> Files(IReadOnlyList<string> args, string workingFolder)
    {
        var files = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i].Trim().Trim('"');
            if (arg.Length == 0) continue;
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (arg.Equals("--self-test", StringComparison.OrdinalIgnoreCase)) i++; // its folder
                continue;
            }
            string full;
            try { full = Path.GetFullPath(arg, workingFolder); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (!files.Contains(full, StringComparer.OrdinalIgnoreCase)) files.Add(full);
            if (files.Count == MaxFiles) break;
        }
        return files;
    }

    /// <summary>
    /// The message for the running copy: its length (4 bytes, little-endian), then
    /// <c>{"files":[…]}</c> in UTF-8. A length first means the reader knows when it has it all.
    /// </summary>
    public static byte[] Encode(IReadOnlyList<string> files)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new Message(files.Take(MaxFiles).ToList()));
        var bytes = new byte[4 + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, json.Length);
        json.CopyTo(bytes, 4);
        return bytes;
    }

    /// <summary>Reads one message (null when it isn't one, or is implausibly big: anything can connect to a pipe).</summary>
    public static IReadOnlyList<string>? Decode(Stream stream)
    {
        var header = new byte[4];
        if (!ReadExactly(stream, header)) return null;
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxMessageBytes) return null;
        var body = new byte[length];
        if (!ReadExactly(stream, body)) return null;
        try
        {
            var message = JsonSerializer.Deserialize<Message>(body);
            return message?.Files?.Where(f => !string.IsNullOrWhiteSpace(f) && Path.IsPathFullyQualified(f)).Take(MaxFiles).ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The pipe's name for a copy of the app (one per data folder, like its single-copy lock).</summary>
    public static string PipeName(string instanceName) => "MxU.Slides.Windows.Open." + instanceName.Split('.').Last();

    private static bool ReadExactly(Stream stream, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var got = stream.Read(buffer, read, buffer.Length - read);
            if (got == 0) return false;
            read += got;
        }
        return true;
    }

    private sealed record Message(List<string> Files)
    {
        [System.Text.Json.Serialization.JsonPropertyName("files")]
        public List<string> Files { get; init; } = Files;
    }
}
