using System.IO.Compression;
using System.Security.Cryptography;
using MxU.Slides.Core.Model;
using MxU.Slides.OpenLpImport.Testing;

namespace MxU.Slides.OpenLpImport.Tests;

/// <summary>
/// The whole import, from a made-up database built on the spot (OpenLpSampleData) into a
/// library in a temp folder. No real OpenLP data is ever opened.
/// </summary>
public class OpenLpImporterTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Task<OpenLpImportResult> Import(TestLibrary library, string path) =>
        new OpenLpImporter(library.Store).ImportAsync(path, cancellationToken: Token);

    private static List<string> Names(TestLibrary library) => library.Store.All<Presentation>().Select(p => p.Name).ToList();

    /// <summary>Every file under a folder with a hash of its bytes: a fingerprint of the whole folder.</summary>
    private static Dictionary<string, string> Fingerprint(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(
            file => Path.GetRelativePath(folder, file),
            file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))) + " " + File.GetLastWriteTimeUtc(file).Ticks);

    [Theory]
    [InlineData(OpenLpShape.V20, 10)]
    [InlineData(OpenLpShape.V24, 10)]
    [InlineData(OpenLpShape.V3, 12)]
    public async Task EachOpenLpLayoutImports(OpenLpShape shape, int songsFound)
    {
        using var folder = new TempFolder();
        using var library = new TestLibrary();
        var data = OpenLpSampleData.WriteDataFolder(folder.Combine("data"), shape);

        var result = await Import(library, data);

        // The damaged song and the empty one are reported; everything else arrives.
        Assert.Equal(songsFound, result.SongsFound);
        Assert.Equal(songsFound - 2, result.SongsImported);
        Assert.Equal(3, result.CustomSlidesFound);
        Assert.Equal(3, result.CustomSlidesImported);
        Assert.Equal([OpenLpSampleData.Empty, OpenLpSampleData.Broken], result.NotImported);
        Assert.Empty(result.SkippedDuplicates);
        Assert.Contains(result.Warnings, w => w.Title == OpenLpSampleData.Broken && w.Message.Contains("damaged XML"));
        Assert.Contains(result.Warnings, w => w.Title == OpenLpSampleData.Empty && w.Message.Contains("no words"));
        Assert.Contains(result.Warnings, w => w.Title == OpenLpSampleData.Painted && w.Message.Contains("{mystery}"));
        Assert.Contains(result.Warnings, w => w.Title == OpenLpSampleData.MissingVerse && w.Message.Contains("\"v3\""));
        Assert.Equal(4, result.Warnings.Count);

        var names = Names(library);
        Assert.Equal(songsFound - 2 + 3, names.Count);
        Assert.Contains(OpenLpSampleData.MorningLight, names);
        Assert.DoesNotContain(OpenLpSampleData.Temporary, names);
        Assert.Equal(shape == OpenLpShape.V3, names.Contains(OpenLpSampleData.Chords));

        // Authors come through whichever way this layout links them (and once each, though OpenLP 3 lists them twice).
        var song = library.Store.All<Presentation>().Single(p => p.Name == OpenLpSampleData.MorningLight);
        Assert.Equal("Ada Testwell, Bayo Sample", song.Ccli?.Author);
        Assert.Equal(7654321, song.Ccli?.SongNumber);
        Assert.Equal(PresentationKind.Song, song.PresentationKind);
        Assert.Null(song.Folder);

        var custom = library.Store.All<Presentation>().Where(p => p.Folder == OpenLpSongMapper.CustomFolder).Select(p => p.Name).ToList();
        Assert.Equal(3, custom.Count);
        Assert.Contains(OpenLpSampleData.Liturgy, custom);
    }

    [Fact]
    public async Task ImportingTheSameDatabaseTwiceAddsNothing()
    {
        using var folder = new TempFolder();
        using var library = new TestLibrary();
        var data = OpenLpSampleData.WriteDataFolder(folder.Combine("data"), OpenLpShape.V3);

        var first = await Import(library, data);
        var before = Names(library);
        var second = await Import(library, data);

        Assert.Equal(before, Names(library));
        Assert.Equal(0, second.SongsImported);
        Assert.Equal(0, second.CustomSlidesImported);
        Assert.Equal(first.SongsImported + first.CustomSlidesImported, second.SkippedDuplicates.Count);
        Assert.Contains(OpenLpSampleData.MorningLight, second.SkippedDuplicates);
        Assert.Contains(OpenLpSampleData.Liturgy, second.SkippedDuplicates);
        Assert.StartsWith("Nothing new imported from OpenLP; 13 already in the library; 2 could not be imported.", second.Headline());
    }

    [Fact]
    public async Task ASurvivingDuplicateIsFoundAfterTheLibraryIsReloaded()
    {
        using var folder = new TempFolder();
        using var libraryFolder = new TempFolder();
        var data = OpenLpSampleData.WriteDataFolder(folder.Combine("data"), OpenLpShape.V24);
        var store = new Core.Library.LibraryStore(libraryFolder.Path);
        store.Load();
        var first = await new OpenLpImporter(store).ImportAsync(data, cancellationToken: Token);

        // A fresh start of the app: the library is read back from disk.
        var reloaded = new Core.Library.LibraryStore(libraryFolder.Path);
        reloaded.Load();
        var second = await new OpenLpImporter(reloaded).ImportAsync(data, cancellationToken: Token);

        Assert.Equal(0, second.SongsImported + second.CustomSlidesImported);
        Assert.Equal(first.SongsImported + first.CustomSlidesImported, reloaded.All<Presentation>().Count);
    }

    [Fact]
    public async Task DuplicatesMatchOnTitleAndCcliOrOnTitleAndFirstLine()
    {
        using var folder = new TempFolder();
        using var library = new TestLibrary();
        static OpenLpSampleData.Song Make(string title, string firstLine, string ccli = "") =>
            new(title, OpenLpSampleData.Xml(("v", "1", firstLine + "\nand a second line")), Ccli: ccli);
        var path = OpenLpSampleData.WriteSongs(folder.Combine("songs.sqlite"), OpenLpShape.V3,
        [
            Make("Same Title", "These are the words", "111"),
            Make("same  title", "Words that were changed since", "111"),       // same title + CCLI: a duplicate, whatever the words
            Make("Same Title", "A different song entirely", "222"),            // same title, another CCLI: its own song
            Make("Same Title", "Nobody numbered this one"),                    // no CCLI, a first line of its own: its own song
            Make("SAME TITLE", "nobody  numbered this one"),                   // no CCLI, same first line: a duplicate
            Make("Other Title", "These are the words", "111"),                 // another title: its own song
        ]);

        var result = await Import(library, path);

        Assert.Equal(4, result.SongsImported);
        // Songs are read in title order, so which of a pair arrives first isn't the order above:
        // what matters is that each pair leaves one song behind.
        Assert.Equal(2, result.SkippedDuplicates.Count);
        var kept = library.Store.All<Presentation>().Select(p => (OpenLpImporter.Plain(p.Name), p.Ccli?.SongNumber)).Order().ToList();
        Assert.Equal([("other title", 111), ("same title", null), ("same title", 111), ("same title", 222)], kept);
        Assert.Equal(4, library.Store.All<Presentation>().Count);
    }

    [Fact]
    public async Task ASongAlreadyInTheLibraryFromElsewhereIsNotDoubled()
    {
        using var folder = new TempFolder();
        using var library = new TestLibrary();
        // Typed in by hand earlier, with different capitals in its title.
        var existing = Core.Lyrics.LyricTextImporter.MakePresentation("Quiet harbour, evening tide\nLittle boats come in to hide", "quiet HARBOUR");
        library.Store.Save(existing);
        var path = OpenLpSampleData.WriteSongs(folder.Combine("songs.sqlite"), OpenLpShape.V24);

        var result = await Import(library, path);

        Assert.Contains(OpenLpSampleData.NoCredits, result.SkippedDuplicates);
        Assert.Single(library.Store.All<Presentation>(), p => p.Name.Equals(OpenLpSampleData.NoCredits, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task YorubaTextSurvivesTheWholeTripThroughSqliteAndTheLibraryFile()
    {
        using var folder = new TempFolder();
        using var libraryFolder = new TempFolder();
        var data = OpenLpSampleData.WriteDataFolder(folder.Combine("data"), OpenLpShape.V24);
        var store = new Core.Library.LibraryStore(libraryFolder.Path);
        store.Load();
        await new OpenLpImporter(store).ImportAsync(data, cancellationToken: Token);

        // Read back from disk, as the app would after a restart.
        var reloaded = new Core.Library.LibraryStore(libraryFolder.Path);
        reloaded.Load();
        var song = reloaded.All<Presentation>().Single(p => string.Equals(p.Name, OpenLpSampleData.Yoruba, StringComparison.Ordinal));
        var words = string.Join("\n", song.Slides.SelectMany(slide => slide.Objects).Select(o => o.Text));
        Assert.Contains(OpenLpSampleData.YorubaLine, words, StringComparison.Ordinal);
        Assert.Contains("Ṣé o gbọ́ orin wa", words, StringComparison.Ordinal);
        Assert.Equal("Fọlákẹ́ Àpẹẹrẹ", song.Ccli?.Author, StringComparer.Ordinal);

        var greeting = reloaded.All<Presentation>().Single(p => string.Equals(p.Name, OpenLpSampleData.Greeting, StringComparison.Ordinal));
        Assert.Equal("Ẹ kú àbọ̀ sí ilé ìjọsìn\nInú wa dùn láti rí yín", greeting.Slides.Single().Objects.Single().Text, StringComparer.Ordinal);
    }

    [Fact]
    public async Task TheChurchsFilesAreNeverChanged()
    {
        using var folder = new TempFolder();
        using var library = new TestLibrary();
        var data = OpenLpSampleData.WriteDataFolder(folder.Combine("data"), OpenLpShape.V3);
        var before = Fingerprint(data);

        await Import(library, data);
        await Import(library, Path.Combine(data, "songs", "songs.sqlite"));

        // Same files, same bytes, same dates: nothing rewritten, and no journal file left beside them.
        Assert.Equal(before, Fingerprint(data));
    }

    [Fact]
    public async Task AReadOnlyDatabaseFileStillImports()
    {
        using var folder = new TempFolder();
        using var library = new TestLibrary();
        var path = OpenLpSampleData.WriteSongs(folder.Combine("songs.sqlite"), OpenLpShape.V24);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var result = await Import(library, path);
            Assert.Equal(8, result.SongsImported);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task TheTempCopiesAreCleanedUp()
    {
        using var folder = new TempFolder();
        using var library = new TestLibrary();
        var zip = folder.Combine("data.zip");
        OpenLpSampleData.WriteDataFolder(folder.Combine("data"), OpenLpShape.V24);
        ZipFile.CreateFromDirectory(folder.Combine("data"), zip);
        var before = Directory.GetDirectories(Path.GetTempPath(), "mxu-openlp-*").Where(d => !Path.GetFileName(d).StartsWith("mxu-openlp-test-", StringComparison.Ordinal)).ToHashSet();

        await Import(library, zip);

        // Other tests run at the same time and have working folders of their own, so only folders
        // this import could have left behind are looked for: none that weren't there before may hold our zip's copies.
        var left = Directory.GetDirectories(Path.GetTempPath(), "mxu-openlp-*")
            .Where(d => !Path.GetFileName(d).StartsWith("mxu-openlp-test-", StringComparison.Ordinal) && !before.Contains(d))
            .Where(d => Directory.Exists(Path.Combine(d, "zip")) && File.Exists(Path.Combine(d, "zip", "1-custom.sqlite")));
        Assert.Empty(left);
    }

    [Fact]
    public async Task ASingleSongsFileOrCustomFileImportsByItself()
    {
        using var folder = new TempFolder();
        using var library = new TestLibrary();
        // The name doesn't matter: the tables inside say what it is.
        var songs = OpenLpSampleData.WriteSongs(folder.Combine("church backup.db"), OpenLpShape.V24);
        var custom = OpenLpSampleData.WriteCustom(folder.Combine("custom.sqlite"));

        var fromSongs = await Import(library, songs);
        Assert.Equal((8, 0, 0), (fromSongs.SongsImported, fromSongs.CustomSlidesFound, fromSongs.CustomSlidesImported));
        var fromCustom = await Import(library, custom);
        Assert.Equal((0, 0, 3), (fromCustom.SongsFound, fromCustom.SongsImported, fromCustom.CustomSlidesImported));
    }

    [Theory]
    [InlineData("")]            // the data folder itself
    [InlineData("openlp/data")] // the folder above it
    public async Task AZipOfTheDataFolderImports(string prefix)
    {
        using var folder = new TempFolder();
        using var library = new TestLibrary();
        var data = OpenLpSampleData.WriteDataFolder(folder.Combine("data"), OpenLpShape.V3);
        File.WriteAllText(Path.Combine(data, "songs", "notes.txt"), "not a database");
        var zip = folder.Combine("OpenLP data.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var file in Directory.EnumerateFiles(data, "*", SearchOption.AllDirectories))
                archive.CreateEntryFromFile(file, (prefix.Length > 0 ? prefix + "/" : "") + Path.GetRelativePath(data, file).Replace('\\', '/'));
        }

        var result = await Import(library, zip);

        Assert.Equal(10, result.SongsImported);
        Assert.Equal(3, result.CustomSlidesImported);
    }

    [Fact]
    public async Task AZipOfJustSongsSqliteImports()
    {
        using var folder = new TempFolder();
        using var library = new TestLibrary();
        var songs = OpenLpSampleData.WriteSongs(folder.Combine("songs.sqlite"), OpenLpShape.V20);
        var zip = folder.Combine("songs.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) archive.CreateEntryFromFile(songs, "songs.sqlite");

        var result = await Import(library, zip);

        Assert.Equal(8, result.SongsImported);
    }

    [Theory]
    [InlineData("../escaped.sqlite")]
    [InlineData("songs/../../songs.sqlite")]
    [InlineData("..\\..\\songs.sqlite")]
    public async Task AZipThatTriesToWriteOutsideItsFolderIsRefusedWhole(string badName)
    {
        using var folder = new TempFolder();
        using var library = new TestLibrary();
        var songs = OpenLpSampleData.WriteSongs(folder.Combine("inner", "songs.sqlite"), OpenLpShape.V24);
        var zip = folder.Combine("unsafe.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(songs, "songs/songs.sqlite");
            archive.CreateEntryFromFile(songs, badName);
        }

        var error = await Assert.ThrowsAsync<OpenLpImportException>(() => Import(library, zip));

        Assert.Contains("unsafe", error.Message);
        Assert.Empty(library.Store.All<Presentation>());
        Assert.False(File.Exists(folder.Combine("escaped.sqlite")));
        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "escaped.sqlite")));
    }

    [Fact]
    public async Task SomethingThatIsNotAnOpenLpDatabaseIsRefusedWithAPlainMessage()
    {
        using var folder = new TempFolder();
        using var library = new TestLibrary();
        var text = folder.Combine("songs.sqlite");
        File.WriteAllText(text, "This is not a database at all, just some words in a file with a hopeful name.");
        var emptyFolder = Directory.CreateDirectory(folder.Combine("empty")).FullName;
        var brokenZip = folder.Combine("broken.zip");
        File.WriteAllBytes(brokenZip, [(byte)'P', (byte)'K', 3, 4, 0, 0, 0]);

        var notDatabase = await Assert.ThrowsAsync<OpenLpImportException>(() => Import(library, text));
        Assert.Contains("songs.sqlite", notDatabase.Message);
        await Assert.ThrowsAsync<OpenLpImportException>(() => Import(library, emptyFolder));
        await Assert.ThrowsAsync<OpenLpImportException>(() => Import(library, brokenZip));
        await Assert.ThrowsAsync<OpenLpImportException>(() => Import(library, folder.Combine("missing.sqlite")));
        Assert.Empty(library.Store.All<Presentation>());
    }

    [Fact]
    public async Task ADatabaseOfSomeOtherProgramIsRefused()
    {
        using var folder = new TempFolder();
        using var library = new TestLibrary();
        var path = folder.Combine("other.sqlite");
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE recipes (id INTEGER PRIMARY KEY, name TEXT)";
            command.ExecuteNonQuery();
        }
        await Assert.ThrowsAsync<OpenLpImportException>(() => Import(library, path));
    }

    [Fact]
    public async Task ASongsTableWithOnlyTheBareColumnsStillImports()
    {
        // A layout this importer has never seen: only id and lyrics are truly needed.
        using var folder = new TempFolder();
        using var library = new TestLibrary();
        var path = folder.Combine("songs.sqlite");
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE songs (id INTEGER PRIMARY KEY, title TEXT, lyrics TEXT, ccli_number INTEGER);"
                + "INSERT INTO songs (title, lyrics, ccli_number) VALUES ('Bare Bones', 'Just the words\nand nothing more', 31415);"
                + "INSERT INTO songs (title, lyrics, ccli_number) VALUES (NULL, x'4279746573206e6f742074657874', NULL)";
            command.ExecuteNonQuery();
        }

        var result = await Import(library, path);

        Assert.Equal(2, result.SongsImported);
        var song = library.Store.All<Presentation>().Single(p => p.Name == "Bare Bones");
        Assert.Equal(31415, song.Ccli?.SongNumber); // stored as a number, not text
        Assert.Contains(library.Store.All<Presentation>(), p => p.Name.StartsWith("Untitled song", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProgressCountsEverySongAndTheReportListsWhatToCheck()
    {
        using var folder = new TempFolder();
        using var library = new TestLibrary();
        var data = OpenLpSampleData.WriteDataFolder(folder.Combine("data"), OpenLpShape.V24);
        var seen = new List<OpenLpImportProgress>();
        var progress = new Recorder(seen);

        var result = await new OpenLpImporter(library.Store).ImportAsync(data, progress, Token);

        lock (seen)
        {
            Assert.Equal(new OpenLpImportProgress("Done", 13, 13), seen[^1]);
            Assert.Contains(seen, p => p.Phase == OpenLpSampleData.MorningLight && p.Total == 13);
        }
        Assert.Equal("Imported 8 songs and 3 custom slides from OpenLP; 2 could not be imported.", result.Headline());
        var report = result.Report();
        Assert.Contains("Songs imported: 8 of 10", report);
        Assert.Contains("Custom slides imported: 3 of 3", report);
        Assert.Contains($"{OpenLpSampleData.Broken}: the words could not be read", report);
    }

    private sealed class Recorder(List<OpenLpImportProgress> seen) : IProgress<OpenLpImportProgress>
    {
        public void Report(OpenLpImportProgress value)
        {
            lock (seen) seen.Add(value);
        }
    }

    [Fact]
    public void OpenLpIsFoundInAppDataOnlyWhenItsSongsAreThere()
    {
        // A pretend %APPDATA%: the real one on this PC is never looked at by a test.
        using var folder = new TempFolder();
        Assert.Null(OpenLpImporter.FindOnThisPc(folder.Path));
        Directory.CreateDirectory(folder.Combine("openlp", "data", "songs"));
        Assert.Null(OpenLpImporter.FindOnThisPc(folder.Path));
        File.WriteAllText(folder.Combine("openlp", "data", "songs", "songs.sqlite"), "");
        Assert.Equal(folder.Combine("openlp", "data"), OpenLpImporter.FindOnThisPc(folder.Path));
    }

    [Fact]
    public void TheDuplicateKeyIgnoresCapitalsSpacesAndHowAnAccentIsWritten()
    {
        // "é" as one character, and as "e" followed by a combining accent.
        Assert.Equal(OpenLpImporter.Plain("Caf\u00E9  SONG"), OpenLpImporter.Plain(" cafe\u0301 song"));
    }
}
