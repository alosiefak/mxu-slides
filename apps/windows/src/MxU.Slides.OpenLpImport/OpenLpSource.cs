using System.IO.Compression;

namespace MxU.Slides.OpenLpImport;

/// <summary>What the operator pointed at is not something this importer can read. The message is shown as it is.</summary>
public sealed class OpenLpImportException(string message) : IOException(message);

/// <summary>
/// Finds OpenLP's database files in whatever the operator chose (a <c>.sqlite</c> file, OpenLP's
/// data folder, or a zip of either) and makes <b>working copies</b> of them in a fresh temp
/// folder. Everything after this reads the copies only.
///
/// Why copy? Opening a database can make SQLite tidy it up (finish a half-written change, add a
/// journal file beside it). Years of a church's songs are in that file, so the importer never
/// opens it as a database at all: it only copies it, the way Explorer would.
///
/// Dispose deletes the temp folder.
/// </summary>
internal sealed class OpenLpSource : IDisposable
{
    private const string SongsFile = "songs.sqlite";
    private const string CustomFile = "custom.sqlite";

    private readonly string workFolder;

    private OpenLpSource(string workFolder) => this.workFolder = workFolder;

    /// <summary>Working copies of the database files found, best guess first.</summary>
    public List<string> Databases { get; } = [];

    /// <summary>Things worth telling the operator (a second songs.sqlite that was left alone, say).</summary>
    public List<string> Notes { get; } = [];

    public static OpenLpSource Open(string path)
    {
        var source = new OpenLpSource(Path.Combine(Path.GetTempPath(), "mxu-openlp-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(source.workFolder);
        try
        {
            if (Directory.Exists(path)) source.AddFolder(path);
            else if (!File.Exists(path)) throw new OpenLpImportException($"\"{path}\" was not found.");
            else if (IsZip(path)) source.AddZip(path);
            else source.Copy(path);
            if (source.Databases.Count == 0)
                throw new OpenLpImportException("No OpenLP songs were found there. Look for a file called songs.sqlite: in OpenLP, Tools › Open Data Folder shows where it is.");
            return source;
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(workFolder)) Directory.Delete(workFolder, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A leftover temp folder is harmless.
        }
    }

    /// <summary>A zip starts with the letters "PK", whatever the file is called.</summary>
    private static bool IsZip(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return file.ReadByte() == 'P' && file.ReadByte() == 'K';
    }

    /// <summary>Copies one database file in, without ever opening the original for writing.</summary>
    private void Copy(string file)
    {
        var target = Path.Combine(workFolder, $"{Databases.Count}-{Path.GetFileName(file)}");
        // Read-only, and sharing with anything that has it open (OpenLP itself, if it wasn't closed).
        using (var from = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var to = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
            from.CopyTo(to);
        // A copy of a read-only file would be read-only too; SQLite needs to be able to open ours.
        File.SetAttributes(target, FileAttributes.Normal);
        Databases.Add(target);
    }

    /// <summary>
    /// OpenLP's data folder holds <c>songs\songs.sqlite</c> and <c>custom\custom.sqlite</c>. The
    /// operator may pick that folder, the <c>openlp</c> folder above it, or the <c>songs</c> folder
    /// inside it, so the two file names are looked for a few levels down.
    /// </summary>
    private void AddFolder(string folder)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true };
        foreach (var name in new[] { SongsFile, CustomFile })
        {
            // The one nearest the top wins; a deeper one is most likely an old backup.
            var found = Directory.EnumerateFiles(folder, name, options).OrderBy(file => file.Count(c => c is '\\' or '/')).ThenBy(file => file, StringComparer.OrdinalIgnoreCase).ToList();
            if (found.Count == 0) continue;
            Copy(found[0]);
            foreach (var other in found.Skip(1)) Notes.Add($"Another {name} was found and left alone: {other}");
        }
    }

    /// <summary>
    /// Takes the database files out of a zip. A zip can be built to trick a careless program: an
    /// entry named <c>..\..\somewhere\file</c> would be written outside the folder being unpacked
    /// into (the trick is called "zip slip"). So every entry's name is checked first, and one bad
    /// name refuses the whole zip. Only the databases are unpacked: a zip of the whole data folder
    /// can also hold gigabytes of pictures and videos this importer doesn't use.
    /// </summary>
    private void AddZip(string zip)
    {
        var unpacked = Path.Combine(workFolder, "zip");
        var root = Path.GetFullPath(unpacked) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(unpacked);
        try
        {
            using var archive = ZipFile.OpenRead(zip);
            foreach (var entry in archive.Entries)
            {
                var target = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('\\', '/')));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) && target + Path.DirectorySeparatorChar != root)
                    throw new OpenLpImportException($"The zip holds an unsafe file name ({entry.FullName}), so none of it was opened.");
            }
            var number = 0;
            foreach (var name in new[] { SongsFile, CustomFile })
            {
                var found = archive.Entries
                    .Where(entry => entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(entry => entry.FullName.Count(c => c is '\\' or '/')).ThenBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase).ToList();
                // A zip of just one database, under another name ("our songs.sqlite").
                if (found.Count == 0 && name == SongsFile)
                    found = archive.Entries.Where(entry => entry.Name.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase)
                        && !entry.Name.Equals(CustomFile, StringComparison.OrdinalIgnoreCase)).Take(1).ToList();
                if (found.Count == 0) continue;
                // Unpacked under a name of our own, so the entry's path is never used to write a file.
                var target = Path.Combine(unpacked, $"{number++}-{name}");
                found[0].ExtractToFile(target);
                File.SetAttributes(target, FileAttributes.Normal);
                Databases.Add(target);
                foreach (var other in found.Skip(1)) Notes.Add($"Another {name} was found in the zip and left alone: {other.FullName}");
            }
        }
        catch (InvalidDataException)
        {
            throw new OpenLpImportException("That zip file could not be opened: it may be damaged or only partly copied.");
        }
    }

    /// <summary>
    /// Where OpenLP keeps its data on this PC when it hasn't been moved: <c>%APPDATA%\openlp\data</c>.
    /// Null when there is no songs database there. (If the church moved the folder in OpenLP's
    /// settings, it has to be chosen by hand; OpenLP's Tools › Open Data Folder shows it.)
    /// </summary>
    public static string? FindOnThisPc(string? appData = null)
    {
        appData ??= Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(appData)) return null;
        var data = Path.Combine(appData, "openlp", "data");
        return File.Exists(Path.Combine(data, "songs", SongsFile)) ? data : null;
    }
}
