using System.Text.RegularExpressions;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.PlanningCenter;

/// <summary>
/// Finds a plan's song in the library, so an import reuses the church's own slides instead of
/// making a second copy:
/// <list type="number">
/// <item>by CCLI number (the same number is the same song, so if several share it, the one whose
/// title and author also fit is taken, else the first by name);</item>
/// <item>then by title (the song's name or its CCLI title, ignoring case and punctuation, as
/// <see cref="ServiceLinkLogic.Normalize"/> does). Two different songs can share a title
/// ("Holy"), so when several fit, the author has to pick exactly one; otherwise there's no match
/// and the row is left for the operator to link.</item>
/// </list>
/// Windows only. The Mac's own rule (<see cref="ServiceLinkLogic.Resolve"/>) stops at any tie; an
/// import that makes songs needs the author tie-break so it doesn't make duplicates.
/// </summary>
public sealed class PlanSongMatcher
{
    private sealed record Entry(Presentation Song, string Name, string? CcliTitle, string? Author, int? Ccli);

    private readonly List<Entry> entries;

    public PlanSongMatcher(IEnumerable<Presentation> library) =>
        entries = [.. library.Select(p => new Entry(
            p, ServiceLinkLogic.Normalize(p.Name),
            p.Ccli?.SongTitle is { } title ? ServiceLinkLogic.Normalize(title) : null,
            p.Ccli?.Author is { } author ? ServiceLinkLogic.Normalize(author) : null,
            p.Ccli?.SongNumber))];

    /// <summary>What happened when looking a song up.</summary>
    public sealed record Match(Presentation? Song, bool Ambiguous);

    /// <summary>A song made during this import joins the candidates, so the same song twice in a plan is made once.</summary>
    public void Add(Presentation song) => entries.Add(new Entry(
        song, ServiceLinkLogic.Normalize(song.Name),
        song.Ccli?.SongTitle is { } title ? ServiceLinkLogic.Normalize(title) : null,
        song.Ccli?.Author is { } author ? ServiceLinkLogic.Normalize(author) : null,
        song.Ccli?.SongNumber));

    public Match Find(string title, string? author, int? ccli)
    {
        var wantedTitle = ServiceLinkLogic.Normalize(title);
        var wantedAuthor = author is null ? null : ServiceLinkLogic.Normalize(author);
        bool TitleFits(Entry e) => wantedTitle.Length > 0 && (e.Name == wantedTitle || e.CcliTitle == wantedTitle);
        bool AuthorFits(Entry e) => wantedAuthor is { Length: > 0 } && e.Author == wantedAuthor;

        if (ccli is { } number)
        {
            var same = entries.Where(e => e.Ccli == number).ToList();
            if (same.Count == 1) return new Match(same[0].Song, false);
            if (same.Count > 1)
            {
                var best = same.Where(TitleFits).ToList() is { Count: > 0 } titled ? titled : same;
                best = best.Where(AuthorFits).ToList() is { Count: > 0 } authored ? authored : best;
                return new Match(best.OrderBy(e => e.Song.Name, StringComparer.Ordinal).ThenBy(e => e.Song.Id, StringComparer.Ordinal).First().Song, false);
            }
        }

        var hits = entries.Where(TitleFits).ToList();
        if (hits.Count == 1)
        {
            // One title match, but both sides know a different CCLI number: a different song with the same name.
            var only = hits[0];
            return ccli is { } n && only.Ccli is { } m && n != m ? new Match(null, false) : new Match(only.Song, false);
        }
        if (hits.Count == 0) return new Match(null, false);
        var byAuthor = hits.Where(AuthorFits).ToList();
        return byAuthor.Count == 1 ? new Match(byAuthor[0].Song, false) : new Match(null, true);
    }
}

/// <summary>
/// Makes a library song from a Planning Center arrangement, through the same lyrics-to-slides
/// pipeline as typing or pasting lyrics (<see cref="SongEditing.Build"/>): its section labels
/// become sections, each stanza is cut into slides. The arrangement's sequence ("Verse 1, Chorus,
/// Verse 2, Chorus") becomes an arrangement of the song, named as in Planning Center.
/// Windows only.
/// </summary>
public static class PlanSongMaker
{
    /// <summary>The library folder new songs are filed in.</summary>
    public const string Folder = "Planning Center";

    /// <summary>
    /// The new song, or null when the arrangement has neither lyrics nor a chord chart. The words
    /// come from the lyrics when there are some (what the room should read); the chord chart is
    /// used only when there are no lyrics, as it can hold chord-only bars that aren't words.
    /// </summary>
    public static Presentation? Make(PcoSong song, PcoArrangement arrangement, string? key, string themeId, int linesPerSlide)
    {
        var fromChart = arrangement.Lyrics is null && arrangement.ChordChart is not null;
        if ((arrangement.Lyrics ?? arrangement.ChordChart) is not { } source) return null;
        var text = Cleaned(source);
        if (text.Trim().Length == 0) return null;

        var details = new SongEditing.Details(song.Title, song.Author, song.CcliNumber, song.Copyright,
            fromChart ? arrangement.ChordChartKey ?? key : key);
        var made = SongEditing.Build(null, text, Math.Clamp(linesPerSlide, 1, 12), details, themeId);
        made.Folder = Folder;
        made.Origin = new PresentationOrigin
        {
            Source = fromChart ? PresentationOriginSource.PlanningCenterChart : PresentationOriginSource.PlanningCenterLyrics,
            Detail = arrangement.Name,
            CreatedAt = DateTimeOffset.Now.ToString("O"),
        };
        if (made.Ccli is { } ccli) ccli.SongTitle = song.Title;

        var order = SectionIds(arrangement.Sequence, made.Sections ?? []);
        if (order.Count > 0)
        {
            var planned = new Arrangement { Id = LibraryStore.NewId(), Name = arrangement.Name, SectionIds = order };
            made.Arrangements = [.. made.Arrangements ?? [], planned];
            made.DefaultArrangementId = planned.Id;
        }
        return made;
    }

    // Planning Center's chord charts can hold layout commands on lines of their own.
    private static readonly Regex LayoutLine = new(@"^\s*(PAGE_BREAK|COLUMN_BREAK)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    private static string Cleaned(string text) => LayoutLine.Replace(text.ReplaceLineEndings("\n"), "");

    /// <summary>
    /// A Planning Center sequence as section ids of the song. Each label is read like a typed play
    /// order (<see cref="SongEditing.ParseOrder"/>: "V1", "Chorus", "Bridge x2"), and Planning
    /// Center's "Chorus 1" also finds a section called just "Chorus" (and "Chorus" finds "Chorus 1").
    /// Labels the lyrics have no section for (an "Instrumental", say) are skipped.
    /// </summary>
    public static List<string> SectionIds(IReadOnlyList<string> sequence, IReadOnlyList<PresentationSection> sections)
    {
        var ids = new List<string>();
        foreach (var label in sequence.Select(l => l.Trim()).Where(l => l.Length > 0))
        {
            var found = SongEditing.ParseOrder(label, sections);
            if (found.Count == 0 && Regex.Match(label, @"^(.*\D)\s*1$") is { Success: true } numbered)
                found = SongEditing.ParseOrder(numbered.Groups[1].Value.Trim(), sections);
            if (found.Count == 0) found = SongEditing.ParseOrder(label + " 1", sections);
            ids.AddRange(found);
        }
        return ids;
    }
}
