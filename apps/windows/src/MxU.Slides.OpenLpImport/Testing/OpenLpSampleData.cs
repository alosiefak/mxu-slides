using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace MxU.Slides.OpenLpImport.Testing;

/// <summary>Which OpenLP's table layout a made-up database copies.</summary>
public enum OpenLpShape
{
    /// <summary>OpenLP 2.0: the song book and song number are columns on the song itself.</summary>
    V20,

    /// <summary>OpenLP 2.4: song books moved to their own link table; authors got a type; songs got dates.</summary>
    V24,

    /// <summary>OpenLP 3.x: chords in the words, the forced split mark, favourites.</summary>
    V3,
}

/// <summary>
/// Builds a <b>made-up</b> OpenLP database for the unit tests and the app's self-test, with the
/// same SQLite library the importer reads with. There is no real church's data here and no real
/// song: every title, name and line below was written for this file, so nothing copyrighted is
/// checked in. The table layouts follow OpenLP's source (plugins/songs/lib/db.py and
/// plugins/custom/lib/db.py); the songs are chosen so that each one exercises one rule of the importer.
/// </summary>
public static class OpenLpSampleData
{
    public const string MorningLight = "Morning Light Has Come";
    public const string Yoruba = "Ọpẹ́ Ni Fún Ọlọ́run Mi";
    public const string Rivers = "Rivers Of Kindness";
    public const string Painted = "Painted Words";
    public const string MissingVerse = "Lanterns On The Hill";
    public const string PlainText = "An Old Plain Song";
    public const string Broken = "The Torn Page";
    public const string Empty = "A Song With No Words";
    public const string NoCredits = "Quiet Harbour";
    public const string EveryKind = "Every Kind Of Section";
    public const string Chords = "Strings And Keys";
    public const string ForcedSplit = "Two Halves";
    public const string Temporary = "Only For One Sunday";

    public const string Liturgy = "Call To Worship";
    public const string Notices = "Notices";
    public const string Greeting = "Ẹ Kú Àbọ̀";

    /// <summary>A line of the Yoruba song, with its under-dots and tone marks, for tests to look for.</summary>
    public const string YorubaLine = "Ẹ̀mí mi, yin Ọlọ́run";

    public sealed record Song(
        string Title, string Lyrics, string VerseOrder = "", string Copyright = "", string Ccli = "",
        string[]? Authors = null, string AlternateTitle = "", bool Temporary = false);

    /// <summary>OpenLP's lyrics XML for a list of (type, label, words).</summary>
    public static string Xml(params (string Type, string Label, string Text)[] verses) =>
        "<?xml version='1.0' encoding='UTF-8'?>\n" + new XElement("song", new XAttribute("version", "1.0"),
            new XElement("lyrics", verses.Select(verse => new XElement("verse",
                new XAttribute("type", verse.Type), new XAttribute("label", verse.Label), new XCData(verse.Text))))).ToString(SaveOptions.DisableFormatting);

    public static IReadOnlyList<Song> Songs(OpenLpShape shape)
    {
        var songs = new List<Song>
        {
            // The ordinary song: verses, one chorus sung three times, a bridge, full credits.
            new(MorningLight, Xml(
                    ("v", "1", "Morning light has come again\nOver roof and road and rain\nEvery window starts to glow\nWith a kindness we can know"),
                    ("c", "1", "So we lift our voices high\nLike the swallows in the sky\nAll our thanks we gladly bring\nTo the Maker of the spring"),
                    ("v", "2", "Evening settles on the town\nLamps are lit as sun goes down\nStill the song is in our chest\nStill we sing it as we rest"),
                    ("b", "1", "Day and night\nNear and far\nYou are here\nWhere we are")),
                VerseOrder: "v1 c1 v2 c1 b1 c1", Copyright: "© 2019 Made-Up Hymns Ltd\nUsed for testing only", Ccli: "7654321",
                Authors: ["Ada Testwell", "Bayo Sample"], AlternateTitle: "Swallows In The Sky"),

            // Yoruba letters: under-dots (ẹ ọ ṣ) and tone marks, some written as letter + combining mark.
            new(Yoruba, Xml(
                    ("v", "1", YorubaLine + "\nỌpẹ́ ni fún Ọ lónìí\nṢé o gbọ́ orin wa\nÀwa ọmọ Rẹ ń kọrin"),
                    ("c", "1", "Ọpẹ́, ọpẹ́, ọpẹ́ o\nẸ̀yin ará, ẹ bá wa yọ̀")),
                VerseOrder: "v1 c1", Authors: ["Fọlákẹ́ Àpẹẹrẹ"]),

            // OpenLP's "new slide here if it doesn't fit" mark, on a line of its own.
            new(Rivers, Xml(
                    ("v", "1", "Rivers of kindness run through the valley\nCarrying songs to the sea\n[---]\nFields by the water wake in the morning\nGreen as a field can be"),
                    ("c", "1", "Run, river, run\nUnder the sun"))),

            // Formatting tags: built-in ones, a line break, a church's own {gold} tag, and a brace word that is no tag.
            new(Painted, Xml(
                    ("v", "1", "{r}Red is the{/r} first of the colours\n{st}Bold{/st} and {it}leaning{/it} the next{br}Then {y}yellow{/y} arrives"),
                    ("v", "2", "{gold}Gold is our own{/gold} little colour\nA {mystery} sits in the line"))),

            // The verse order names a verse the song doesn't have.
            new(MissingVerse, Xml(
                    ("v", "1", "Lanterns on the hill tonight\nSmall and steady, warm and bright"),
                    ("c", "1", "Carry the light along the lane\nCarry it home again")),
                VerseOrder: "v1 c1 v3 c1"),

            // Very old rows: plain words, no XML at all.
            new(PlainText, "The first plain verse is here\nWith a second line to hear\n\nThe second verse comes after\nWith a little bit of laughter"),

            // Damaged XML: must be reported and must not stop the import.
            new(Broken, "<?xml version='1.0' encoding='UTF-8'?><song version=\"1.0\"><lyrics><verse type=\"v\" label=\"1\"><![CDATA[Half a page"),

            new(Empty, Xml()),

            // No credits and no verse order: sections as stored, no arrangement.
            new(NoCredits, Xml(
                    ("v", "1", "Quiet harbour, evening tide\nLittle boats come in to hide"),
                    ("v", "2", "Morning harbour, open sea\nLittle boats go sailing free"))),

            // Every verse type OpenLP has, two choruses, and a type written in full.
            new(EveryKind, Xml(
                    ("i", "1", "Hum along before we start"),
                    ("v", "1", "This is where the story opens\nWith a line to set the scene"),
                    ("p", "1", "Now the tune begins to climb"),
                    ("c", "1", "Here is chorus number one\nSing it till the day is done"),
                    ("Chorus", "2", "Here is chorus number two\nSing it till the sky is blue"),
                    ("o", "1", "A spoken word between the songs"),
                    ("e", "1", "And softly now we close")),
                VerseOrder: "I1 V1 P1 C1 C2 O1 E1 C2"),
        };
        if (shape == OpenLpShape.V3)
        {
            // OpenLP 3: chords in square brackets, and the "always start a new slide" mark.
            songs.Add(new(Chords, Xml(
                    ("v", "1", "[C]Strings and [G/B]keys to[Am]gether\n[F]Play a tune that [C]lasts"),
                    ("c", "1", "[F]Louder [G]now and [C]brighter")),
                Ccli: "7654322"));
            songs.Add(new(ForcedSplit, Xml(
                    ("v", "1", "The first half stands alone\n[--}{--]\nThe second half replies"))));
            songs.Add(new(Temporary, Xml(("v", "1", "Made on the fly for a service file")), Temporary: true));
        }
        return songs;
    }

    public static IReadOnlyList<(string Title, string Text, string Credits)> CustomSlides { get; } =
    [
        (Liturgy, Xml(
            ("custom", "1", "Leader: This is the day we gather.\nAll: We are glad to be here."),
            ("custom", "2", "Leader: Let every voice be heard.\nAll: Let every heart be glad.")), "A made-up liturgy"),
        (Notices, Xml(
            ("custom", "1", "{st}This week{/st}\nChoir practice on Thursday at 6\n[---]\nBring a friend next Sunday")), ""),
        (Greeting, Xml(
            ("custom", "1", "Ẹ kú àbọ̀ sí ilé ìjọsìn\nInú wa dùn láti rí yín")), ""),
    ];

    /// <summary>Writes <c>songs\songs.sqlite</c> and <c>custom\custom.sqlite</c> under a folder, as OpenLP's data folder has them.</summary>
    public static string WriteDataFolder(string folder, OpenLpShape shape)
    {
        WriteSongs(Path.Combine(folder, "songs", "songs.sqlite"), shape);
        WriteCustom(Path.Combine(folder, "custom", "custom.sqlite"));
        return folder;
    }

    public static string WriteSongs(string path, OpenLpShape shape, IReadOnlyList<Song>? songs = null)
    {
        songs ??= Songs(shape);
        using var connection = Create(path);
        var old = shape == OpenLpShape.V20;
        Run(connection, "CREATE TABLE songs (id INTEGER PRIMARY KEY, "
            + (old ? "song_book_id INTEGER, song_number VARCHAR(64), " : "create_date DATETIME, last_modified DATETIME, temporary BOOLEAN, ")
            + (shape == OpenLpShape.V3 ? "is_favourite BOOLEAN, " : "")
            + "title VARCHAR(255) NOT NULL, alternate_title VARCHAR(255), lyrics TEXT NOT NULL, verse_order VARCHAR(128), copyright VARCHAR(255), "
            + "comments TEXT, ccli_number VARCHAR(64), theme_name VARCHAR(128), search_title VARCHAR(255) NOT NULL, search_lyrics TEXT NOT NULL)");
        Run(connection, "CREATE TABLE authors (id INTEGER PRIMARY KEY, first_name VARCHAR(128), last_name VARCHAR(128), display_name VARCHAR(255) NOT NULL)");
        Run(connection, old
            ? "CREATE TABLE authors_songs (author_id INTEGER NOT NULL, song_id INTEGER NOT NULL, PRIMARY KEY (author_id, song_id))"
            : "CREATE TABLE authors_songs (author_id INTEGER NOT NULL, song_id INTEGER NOT NULL, author_type VARCHAR(255) NOT NULL DEFAULT '', PRIMARY KEY (author_id, song_id, author_type))");
        Run(connection, "CREATE TABLE song_books (id INTEGER PRIMARY KEY, name VARCHAR(128) NOT NULL, publisher VARCHAR(128))");
        if (!old) Run(connection, "CREATE TABLE songs_songbooks (songbook_id INTEGER NOT NULL, song_id INTEGER NOT NULL, entry VARCHAR(255) NOT NULL, PRIMARY KEY (songbook_id, song_id, entry))");
        Run(connection, "CREATE TABLE topics (id INTEGER PRIMARY KEY, name VARCHAR(128) NOT NULL)");
        Run(connection, "CREATE TABLE songs_topics (song_id INTEGER NOT NULL, topic_id INTEGER NOT NULL, PRIMARY KEY (song_id, topic_id))");
        Run(connection, shape == OpenLpShape.V3
            ? "CREATE TABLE media_files (id INTEGER PRIMARY KEY, song_id INTEGER, file_path VARCHAR(255) NOT NULL, file_hash VARCHAR(128), type VARCHAR(64) NOT NULL DEFAULT 'audio', weight INTEGER DEFAULT 0)"
            : "CREATE TABLE media_files (id INTEGER PRIMARY KEY, song_id INTEGER, file_name VARCHAR(255) NOT NULL, type VARCHAR(64) NOT NULL DEFAULT 'audio', weight INTEGER DEFAULT 0)");
        Run(connection, "INSERT INTO song_books (id, name, publisher) VALUES (1, 'The Made-Up Hymnal', 'Nobody & Sons')");

        // One transaction for all the rows: SQLite then writes to disk once, not once per song.
        Run(connection, "BEGIN");
        var authorIds = new Dictionary<string, long>();
        long songId = 0;
        foreach (var song in songs)
        {
            songId++;
            Run(connection,
                "INSERT INTO songs (id, title, alternate_title, lyrics, verse_order, copyright, comments, ccli_number, theme_name, search_title, search_lyrics"
                + (old ? ", song_book_id, song_number" : ", temporary") + ") VALUES ($id, $title, $alternate, $lyrics, $order, $copyright, '', $ccli, '', $search, '', "
                + (old ? "1, $number" : "$temporary") + ")",
                ("$id", songId), ("$title", song.Title), ("$alternate", song.AlternateTitle), ("$lyrics", song.Lyrics), ("$order", song.VerseOrder),
                ("$copyright", song.Copyright), ("$ccli", song.Ccli), ("$search", song.Title.ToLowerInvariant()),
                old ? ("$number", songId.ToString(System.Globalization.CultureInfo.InvariantCulture)) : ("$temporary", song.Temporary ? 1 : 0));
            if (!old) Run(connection, "INSERT INTO songs_songbooks (songbook_id, song_id, entry) VALUES (1, $song, $entry)", ("$song", songId), ("$entry", songId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            foreach (var author in song.Authors ?? [])
            {
                if (!authorIds.TryGetValue(author, out var authorId))
                {
                    authorIds[author] = authorId = authorIds.Count + 1;
                    var names = author.Split(' ', 2);
                    Run(connection, "INSERT INTO authors (id, first_name, last_name, display_name) VALUES ($id, $first, $last, $name)",
                        ("$id", authorId), ("$first", names[0]), ("$last", names.Length > 1 ? names[1] : ""), ("$name", author));
                }
                if (old)
                {
                    Run(connection, "INSERT INTO authors_songs (author_id, song_id) VALUES ($author, $song)", ("$author", authorId), ("$song", songId));
                }
                else
                {
                    // As OpenLP 3 stores a writer of both: one row for the words and one for the music.
                    foreach (var type in shape == OpenLpShape.V3 ? new[] { "words", "music" } : [""])
                        Run(connection, "INSERT INTO authors_songs (author_id, song_id, author_type) VALUES ($author, $song, $type)", ("$author", authorId), ("$song", songId), ("$type", type));
                }
            }
        }
        Run(connection, "COMMIT");
        return path;
    }

    public static string WriteCustom(string path)
    {
        using var connection = Create(path);
        Run(connection, "CREATE TABLE custom_slide (id INTEGER PRIMARY KEY, title VARCHAR(255) NOT NULL, text TEXT NOT NULL, credits TEXT, theme_name VARCHAR(128))");
        foreach (var slide in CustomSlides)
            Run(connection, "INSERT INTO custom_slide (title, text, credits, theme_name) VALUES ($title, $text, $credits, '')",
                ("$title", slide.Title), ("$text", slide.Text), ("$credits", slide.Credits));
        return path;
    }

    private static SqliteConnection Create(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (File.Exists(path)) File.Delete(path);
        // Pooling off, so the file is closed (and can be zipped, copied or deleted) as soon as this is disposed.
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static void Run(SqliteConnection connection, string sql, params (string Name, object Value)[] values)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }
}
