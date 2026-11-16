using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.OpenLpImport;

/// <summary>How far an import has got, for the progress bar.</summary>
public sealed record OpenLpImportProgress(string Phase, int Completed, int Total);

/// <summary>Something the operator should know about one song (or about the import as a whole).</summary>
public sealed record OpenLpWarning(string Title, string Message)
{
    public override string ToString() => $"{Title}: {Message}";
}

/// <summary>What an import did. The summary window shows exactly this.</summary>
public sealed class OpenLpImportResult
{
    /// <summary>How many songs and custom slides the database held (temporary songs aside).</summary>
    public int SongsFound { get; internal set; }
    public int CustomSlidesFound { get; internal set; }

    /// <summary>The ids of the documents saved into the library, in the order they were saved.</summary>
    public List<string> SongIds { get; } = [];
    public List<string> CustomSlideIds { get; } = [];

    /// <summary>Titles left out because the library already had them.</summary>
    public List<string> SkippedDuplicates { get; } = [];

    /// <summary>Titles that could not be imported at all (their reasons are in <see cref="Warnings"/>).</summary>
    public List<string> NotImported { get; } = [];

    public List<OpenLpWarning> Warnings { get; } = [];

    public int SongsImported => SongIds.Count;
    public int CustomSlidesImported => CustomSlideIds.Count;

    /// <summary>One line, for the status bar.</summary>
    public string Headline()
    {
        var parts = new List<string>();
        if (SongsImported > 0) parts.Add(SongsImported == 1 ? "1 song" : $"{SongsImported} songs");
        if (CustomSlidesImported > 0) parts.Add(CustomSlidesImported == 1 ? "1 custom slide" : $"{CustomSlidesImported} custom slides");
        return (parts.Count > 0 ? $"Imported {string.Join(" and ", parts)} from OpenLP" : "Nothing new imported from OpenLP")
            + (SkippedDuplicates.Count > 0 ? $"; {SkippedDuplicates.Count} already in the library" : "")
            + (NotImported.Count > 0 ? $"; {NotImported.Count} could not be imported" : "") + ".";
    }

    /// <summary>The whole report as plain text: what the summary window's Copy button puts on the clipboard.</summary>
    public string Report()
    {
        var text = new StringBuilder();
        text.AppendLine("OpenLP import");
        text.AppendLine($"Songs imported: {SongsImported} of {SongsFound}");
        text.AppendLine($"Custom slides imported: {CustomSlidesImported} of {CustomSlidesFound}");
        text.AppendLine($"Already in the library (skipped): {SkippedDuplicates.Count}");
        text.AppendLine($"Could not be imported: {NotImported.Count}");
        if (SkippedDuplicates.Count > 0)
        {
            text.AppendLine().AppendLine("Already in the library:");
            foreach (var title in SkippedDuplicates) text.AppendLine($"  {title}");
        }
        if (Warnings.Count > 0)
        {
            text.AppendLine().AppendLine("To check:");
            foreach (var warning in Warnings) text.AppendLine($"  {warning}");
        }
        return text.ToString();
    }
}

/// <summary>
/// Brings a church's OpenLP songs and custom slides into a library. The steps:
/// <list type="number">
/// <item>copy the database files to a temp folder (<see cref="OpenLpSource"/>);</item>
/// <item>read the rows (<see cref="OpenLpDatabase"/>);</item>
/// <item>turn each row into a song or a presentation (<see cref="OpenLpSongMapper"/>);</item>
/// <item>leave out what the library already has, and save the rest.</item>
/// </list>
/// A song that can't be read becomes a line in the result, never an error that stops the others.
/// </summary>
public sealed class OpenLpImporter(LibraryStore library)
{
    /// <summary>The look new songs get (the app passes its "default theme" setting).</summary>
    public string ThemeId { get; init; } = "";

    /// <summary>How many lines a lyric slide holds (the app passes its own setting).</summary>
    public int LinesPerSlide { get; init; } = 2;

    /// <summary>Where OpenLP keeps its data on this PC, or null (see <see cref="OpenLpSource.FindOnThisPc"/>).</summary>
    public static string? FindOnThisPc(string? appData = null) => OpenLpSource.FindOnThisPc(appData);

    /// <summary>
    /// Imports from a <c>.sqlite</c> file, an OpenLP data folder or a zip of either. It runs on a
    /// background thread, so the window stays alive. Throws <see cref="OpenLpImportException"/>
    /// only when nothing at all could be read (wrong file, damaged zip).
    /// </summary>
    public Task<OpenLpImportResult> ImportAsync(string path, IProgress<OpenLpImportProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Factory.StartNew(() => Import(path, progress, cancellationToken), cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    internal OpenLpImportResult Import(string path, IProgress<OpenLpImportProgress>? progress, CancellationToken cancellationToken)
    {
        var result = new OpenLpImportResult();
        progress?.Report(new OpenLpImportProgress("Reading OpenLP's database", 0, 0));
        var songs = new List<OpenLpSong>();
        var customSlides = new List<OpenLpCustomSlide>();
        void Problem(string title, string message) => result.Warnings.Add(new OpenLpWarning(title, message));

        using (var source = OpenLpSource.Open(path))
        {
            foreach (var note in source.Notes) Problem("OpenLP data", note);
            var understood = 0;
            foreach (var file in source.Databases)
            {
                try
                {
                    using var database = OpenLpDatabase.Open(file);
                    if (!database.HasSongs && !database.HasCustomSlides) continue;
                    understood++;
                    // Only the first songs database and the first custom database are read.
                    if (database.HasSongs && songs.Count == 0) songs = database.ReadSongs(Problem);
                    if (database.HasCustomSlides && customSlides.Count == 0) customSlides = database.ReadCustomSlides(Problem);
                }
                catch (SqliteException error)
                {
                    Problem("OpenLP data", $"a database file could not be opened ({error.Message})");
                }
            }
            if (understood == 0)
                throw new OpenLpImportException("That is not an OpenLP songs database. Look for a file called songs.sqlite: in OpenLP, Tools › Open Data Folder shows where it is.");
        }
        result.SongsFound = songs.Count;
        result.CustomSlidesFound = customSlides.Count;

        // What the library already holds, so a second import of the same database adds nothing.
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var existing in library.All<Presentation>())
            foreach (var key in DuplicateKeys(existing)) known.Add(key);

        var total = songs.Count + customSlides.Count;
        var done = 0;
        void Save(string title, string what, Func<OpenLpMapped> map, List<string> ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new OpenLpImportProgress(title, done++, total));
            try
            {
                var mapped = map();
                foreach (var warning in mapped.Warnings) Problem(title, warning);
                if (mapped.Presentation is not { } presentation)
                {
                    result.NotImported.Add(title);
                    return;
                }
                if (MatchKey(presentation) is { } match && known.Contains(match))
                {
                    result.SkippedDuplicates.Add(presentation.Name);
                    return;
                }
                library.Save(presentation);
                foreach (var key in DuplicateKeys(presentation)) known.Add(key);
                ids.Add(presentation.Id);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Whatever went wrong with this one, the next one still gets its turn.
                Problem(title, $"this {what} could not be imported ({error.Message})");
                result.NotImported.Add(title);
            }
        }

        foreach (var song in songs)
            Save(song.Title.Trim().Length > 0 ? song.Title.Trim() : $"song number {song.Id} in OpenLP", "song",
                () => OpenLpSongMapper.MapSong(song, ThemeId, LinesPerSlide), result.SongIds);
        foreach (var custom in customSlides)
            Save(custom.Title.Trim().Length > 0 ? custom.Title.Trim() : $"custom slide number {custom.Id} in OpenLP", "custom slide",
                () => OpenLpSongMapper.MapCustom(custom, ThemeId), result.CustomSlideIds);
        progress?.Report(new OpenLpImportProgress("Done", total, total));
        return result;
    }

    // MARK: Duplicates
    //
    // Two songs are "the same" when they have the same title and the same CCLI number, or, for a
    // song without a CCLI number, the same title and the same first line of words. Each document
    // in the library is boiled down to one or two short strings (keys); a new song is a duplicate
    // when the key it would be matched on is already in the set.

    /// <summary>Both keys a document can be found by.</summary>
    internal static IEnumerable<string> DuplicateKeys(Presentation presentation)
    {
        var title = Plain(presentation.Name);
        if (presentation.Ccli?.SongNumber is { } number) yield return $"{title}\nccli:{number}";
        yield return $"{title}\nline:{Plain(FirstLine(presentation))}";
    }

    /// <summary>The one key an incoming document is matched on: CCLI number if it has one, else its first line.</summary>
    internal static string? MatchKey(Presentation presentation) => DuplicateKeys(presentation).FirstOrDefault();

    /// <summary>The first line of words on the first slide that has any.</summary>
    internal static string FirstLine(Presentation presentation) =>
        presentation.Slides
            .SelectMany(slide => slide.Objects.Where(o => o.ObjectKind == SlideObjectKind.Text))
            .SelectMany(o => (o.Text ?? "").Split('\n'))
            .FirstOrDefault(line => line.Trim().Length > 0) ?? "";

    /// <summary>Text made comparable: capitals, extra spaces and the two ways of writing an accented letter no longer matter.</summary>
    internal static string Plain(string text) =>
        Regex.Replace(text.Normalize(NormalizationForm.FormC), @"\s+", " ").Trim().ToLowerInvariant();
}
