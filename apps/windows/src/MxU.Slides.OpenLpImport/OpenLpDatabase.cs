using System.Text;
using Microsoft.Data.Sqlite;

namespace MxU.Slides.OpenLpImport;

/// <summary>
/// Reads one of OpenLP's database files. OpenLP keeps its songs in SQLite: a whole database
/// (tables, rows, indexes) in a single ordinary file, read by a small library inside the program
/// instead of by a database server. The library here is the Microsoft.Data.Sqlite package.
///
/// OpenLP changed its tables over the years (2.0, 2.4, 2.9, 3.x), so nothing here assumes a
/// column exists: it asks the file which tables and columns it has, and uses only those.
///
/// The file opened is always a <b>copy</b> in a temp folder (see <see cref="OpenLpSource"/>),
/// never the church's own file.
/// </summary>
internal sealed class OpenLpDatabase : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly HashSet<string> tables = new(StringComparer.OrdinalIgnoreCase);

    private OpenLpDatabase(SqliteConnection connection) => this.connection = connection;

    /// <summary>Opens a database file, or throws <see cref="SqliteException"/> when it isn't one.</summary>
    public static OpenLpDatabase Open(string path)
    {
        // Pooling off: when this is disposed the file is really closed, so the temp copy can be deleted.
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        }.ToString());
        try
        {
            connection.Open();
            var database = new OpenLpDatabase(connection);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
            using var reader = command.ExecuteReader();
            while (reader.Read()) database.tables.Add(reader.GetString(0));
            return database;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public bool HasSongs => tables.Contains("songs");

    public bool HasCustomSlides => tables.Contains("custom_slide");

    public void Dispose() => connection.Dispose();

    /// <summary>The column names of a table (empty when the table isn't there).</summary>
    private HashSet<string> Columns(string table)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!tables.Contains(table)) return columns;
        using var command = connection.CreateCommand();
        // Table names can't be passed as parameters; ours are fixed words from this file, never user text.
        command.CommandText = $"PRAGMA table_info(\"{table}\")";
        using var reader = command.ExecuteReader();
        while (reader.Read()) columns.Add(reader.GetString(1));
        return columns;
    }

    /// <summary>
    /// A column's value as text, whatever SQLite stored it as. SQLite lets any column hold any
    /// kind of value: a CCLI number may be a number in one row and text in the next, and old
    /// databases sometimes hold text as raw bytes.
    /// </summary>
    private static string? Text(SqliteDataReader reader, int ordinal)
    {
        if (ordinal < 0 || reader.IsDBNull(ordinal)) return null;
        var value = reader.GetValue(ordinal);
        return value switch
        {
            string text => text,
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            IFormattable number => number.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
    }

    /// <summary>
    /// Every song, in title order. A row that can't be read is reported through
    /// <paramref name="problem"/> and skipped: one bad song must not stop the rest.
    /// </summary>
    public List<OpenLpSong> ReadSongs(Action<string, string> problem)
    {
        var songs = new List<OpenLpSong>();
        var columns = Columns("songs");
        if (!columns.Contains("id") || !columns.Contains("lyrics")) return songs;
        var authors = ReadAuthors(problem);

        // Ask only for the columns this version of OpenLP has; the others come back as NULL.
        string Column(string name) => columns.Contains(name) ? $"\"{name}\"" : "NULL";
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT id, {Column("title")}, {Column("alternate_title")}, lyrics, {Column("verse_order")}, "
            + $"{Column("copyright")}, {Column("ccli_number")}, {Column("temporary")} FROM songs ORDER BY {Column("title")}, id";
        using var reader = command.ExecuteReader();
        while (true)
        {
            long id = -1;
            try
            {
                if (!reader.Read()) break;
                id = reader.GetInt64(0);
                // "Temporary" songs are ones OpenLP made up on the fly from a service file; they aren't in the church's song list.
                if (Text(reader, 7) is "1" or "True" or "true") continue;
                songs.Add(new OpenLpSong(
                    id, Text(reader, 1) ?? "", Text(reader, 2), Text(reader, 3), Text(reader, 4), Text(reader, 5), Text(reader, 6),
                    authors.TryGetValue(id, out var names) ? names : []));
            }
            catch (Exception error) when (error is SqliteException or InvalidCastException or InvalidOperationException or FormatException)
            {
                problem($"song number {id} in OpenLP", $"its row could not be read ({error.Message})");
                if (error is SqliteException) break; // the file itself is damaged from here on
            }
        }
        return songs;
    }

    /// <summary>Each song's authors' names, by song id.</summary>
    private Dictionary<long, List<string>> ReadAuthors(Action<string, string> problem)
    {
        var bySong = new Dictionary<long, List<string>>();
        var author = Columns("authors");
        var link = Columns("authors_songs");
        if (!author.Contains("id") || !link.Contains("author_id") || !link.Contains("song_id")) return bySong;
        var name = author.Contains("display_name") ? "a.display_name" : "NULL";
        var first = author.Contains("first_name") ? "a.first_name" : "NULL";
        var last = author.Contains("last_name") ? "a.last_name" : "NULL";
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT l.song_id, {name}, {first}, {last} FROM authors_songs l JOIN authors a ON a.id = l.author_id ORDER BY l.song_id, l.rowid";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var display = Text(reader, 1)?.Trim();
                if (string.IsNullOrEmpty(display)) display = $"{Text(reader, 2)} {Text(reader, 3)}".Trim();
                if (display.Length == 0) continue;
                var songId = reader.GetInt64(0);
                if (!bySong.TryGetValue(songId, out var names)) bySong[songId] = names = [];
                // OpenLP 3 lists a person once for the words and once for the music.
                if (!names.Contains(display, StringComparer.OrdinalIgnoreCase)) names.Add(display);
            }
        }
        catch (Exception error) when (error is SqliteException or InvalidCastException or InvalidOperationException or FormatException)
        {
            problem("Authors", $"the authors could not be read, so songs come in without them ({error.Message})");
        }
        return bySong;
    }

    /// <summary>Every custom slide, in title order.</summary>
    public List<OpenLpCustomSlide> ReadCustomSlides(Action<string, string> problem)
    {
        var slides = new List<OpenLpCustomSlide>();
        var columns = Columns("custom_slide");
        if (!columns.Contains("id") || !columns.Contains("text")) return slides;
        string Column(string name) => columns.Contains(name) ? $"\"{name}\"" : "NULL";
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id, {Column("title")}, text, {Column("credits")} FROM custom_slide ORDER BY {Column("title")}, id";
        using var reader = command.ExecuteReader();
        while (true)
        {
            long id = -1;
            try
            {
                if (!reader.Read()) break;
                id = reader.GetInt64(0);
                slides.Add(new OpenLpCustomSlide(id, Text(reader, 1) ?? "", Text(reader, 2), Text(reader, 3)));
            }
            catch (Exception error) when (error is SqliteException or InvalidCastException or InvalidOperationException or FormatException)
            {
                problem($"custom slide number {id} in OpenLP", $"its row could not be read ({error.Message})");
                if (error is SqliteException) break;
            }
        }
        return slides;
    }
}
