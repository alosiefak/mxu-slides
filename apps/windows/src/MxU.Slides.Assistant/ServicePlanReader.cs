using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.PlanningCenter;
using MxU.Slides.Core.Scripture;

namespace MxU.Slides.Assistant;

/// <summary>
/// Order of service from anything: the words of a bulletin, an email or some notes become a
/// service plan the operator reviews and then adds as an ordinary service.
/// <list type="bullet">
/// <item><b>Two readers, one result.</b> <see cref="ReadPlainly"/> needs no AI: one line is one
/// row. <see cref="Prepare"/> and <see cref="Read"/> ask the assistant, which copes with prose
/// ("after the offering we'll sing…"). Both give a <see cref="Plan"/>.</item>
/// <item><b>Songs are matched, never invented.</b> A song row is looked up in the church's library
/// with the matcher the Planning Center import uses (<see cref="PlanSongMatcher"/>). One that
/// isn't there is listed as missing and becomes a plain row to link later; no lyrics are made.</item>
/// <item><b>Scripture is a reference only.</b> A reading becomes a row named by the reference as
/// the app's own parser reads it. The verses are shown from the Bible window on the day.</item>
/// </list>
/// What <see cref="Build"/> makes is a normal shared service: headers, song rows and plain
/// ("info") rows, the kinds a Mac already knows.
/// </summary>
public static partial class ServicePlanReader
{
    public const string Header = "header", Song = "song", Scripture = "scripture", Item = "item";

    /// <summary>The only row kinds the assistant may name.</summary>
    public static readonly IReadOnlyList<string> Kinds = [Header, Song, Scripture, Item];

    /// <param name="Song">The library's song for a song row; null when it isn't in the library.</param>
    /// <param name="Ambiguous">Several library songs share the title and nothing picked one.</param>
    public sealed record Row(string Kind, string Title, string? Author = null, int? Minutes = null, PassageReference? Reference = null, Presentation? Song = null, bool Ambiguous = false);

    public sealed record Plan(string Name, string? Date, IReadOnlyList<Row> Rows)
    {
        /// <summary>Songs named in the plan that the library doesn't have (or has several of): for the operator to add or link.</summary>
        public IReadOnlyList<string> MissingSongs =>
            [.. Rows.Where(r => r.Kind == ServicePlanReader.Song && r.Song is null).Select(r => r.Ambiguous ? $"{r.Title} (more than one in your library)" : r.Title).Distinct()];

        public int SongsMatched => Rows.Count(r => r.Song is not null);
    }

    public sealed record Prepared(AssistantRequest Request, string? PrivacyWarning);

    public static string Schema => JsonSerializer.Serialize(new
    {
        type = "object",
        properties = new
        {
            name = new { type = "string" },
            date = new { type = new[] { "string", "null" } },
            items = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        kind = new { type = "string", @enum = Kinds },
                        title = new { type = "string" },
                        author = new { type = new[] { "string", "null" } },
                        reference = new { type = new[] { "string", "null" } },
                        minutes = new { type = new[] { "integer", "null" } },
                    },
                    required = new[] { "kind", "title", "author", "reference", "minutes" },
                    additionalProperties = false,
                },
            },
        },
        required = new[] { "name", "date", "items" },
        additionalProperties = false,
    });

    private const string Instructions =
        """
        You help a church's slide operator turn a bulletin, an email or planning notes into an order of service: the rows of the service, in the order they happen. A person reviews it before anything is saved.

        Each row has a kind:
        - header: a part of the service that groups the rows after it ("Worship", "The Word").
        - song: something the congregation sings. title is the song's title only; author is its writer or artist if the text gives one, else null.
        - scripture: a Bible reading. title is what the text calls it ("Old Testament reading"); reference is the passage in the usual form ("Psalm 23", "John 3:16-18").
        - item: anything else (welcome, announcements, prayer, offering, sermon, communion, video).

        minutes is the row's planned length when the text gives one (from "5 min", or from start times: the gap to the next row), else null. reference is null except for scripture.
        name is a short name for the service ("Sunday Morning"); date is the service's date as YYYY-MM-DD if the text gives one, else null.

        Use only what the text says. Do not add rows it doesn't mention, do not guess songs, and never write lyrics or Bible text. Leave out lines shown as "(private: not sent)", page furniture, addresses and staff lists. You can check whether a song is in the church's library with search_library (kind "presentations"); use the library's spelling of a title when it is clearly the same song.
        """;

    /// <summary>Builds the job. Prayer-request lines are held back first (<see cref="PrayerPrivacy"/>).</summary>
    public static Prepared Prepare(string text, LibraryFacts facts)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var safe = PrayerPrivacy.Strip(lines);
        return new Prepared(new AssistantRequest
        {
            Feature = "Order of service", Instructions = Instructions, Schema = Schema, Tools = [facts.Tools[0]],
            Ask = "The text:\n" + string.Join("\n", safe.Lines),
        }, safe.Warning);
    }

    /// <summary>
    /// Reads the assistant's reply, keeping only rows that fit (a real kind, a title), and matches
    /// each song to the library. The reply's song titles are only ever looked up, never made.
    /// </summary>
    public static Plan Read(JsonElement reply, IEnumerable<Presentation> library)
    {
        var matcher = new PlanSongMatcher(library);
        var rows = new List<Row>();
        if (reply.ValueKind != JsonValueKind.Object) return new Plan("Order of service", null, rows);
        if (reply.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray().Take(200))
            {
                if (item.ValueKind != JsonValueKind.Object || Text(item, "kind") is not { } kind || !Kinds.Contains(kind)) continue;
                if (Text(item, "title")?.Trim() is not { Length: > 0 } title) continue;
                int? minutes = item.TryGetProperty("minutes", out var m) && m.ValueKind == JsonValueKind.Number && m.TryGetInt32(out var n) && n is > 0 and <= 600 ? n : null;
                rows.Add(Finished(kind, Clip(title), Text(item, "author"), minutes, Text(item, "reference"), matcher));
            }
        }
        var date = Text(reply, "date") is { } d && DateOnly.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? d : null;
        return new Plan(Clip(Text(reply, "name")?.Trim() is { Length: > 0 } name ? name : "Order of service"), date, rows);
    }

    /// <summary>
    /// The reader that needs no AI: every line is a row. A line in capitals or ending in a colon
    /// is a header; a line that is a Bible reference is a reading; a line whose title is a song in
    /// the library (or that says "Song:" or "Hymn:") is a song; the rest are plain rows. A start
    /// time ("10:05") or a length ("5 min") on the line gives the row its planned length.
    /// </summary>
    public static Plan ReadPlainly(string text, IEnumerable<Presentation> library)
    {
        var matcher = new PlanSongMatcher(library);
        var lines = text.ReplaceLineEndings("\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var safe = PrayerPrivacy.Strip(lines);
        string? name = null, date = null;
        var rows = new List<(Row Row, int? StartsAt)>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (safe.Withheld.Contains(i)) continue;
            var line = lines[i];
            if (date is null && DateIn(line) is { } found) date = found;
            if (i == 0 && lines.Count > 1 && line.Length <= 70 && ServiceTitle().IsMatch(line))
            {
                name = line;
                continue;
            }

            int? startsAt = null;
            if (StartTime().Match(line) is { Success: true } time)
            {
                var hour = int.Parse(time.Groups[1].Value, CultureInfo.InvariantCulture) % 12 + (time.Groups[3].Value.StartsWith("p", StringComparison.OrdinalIgnoreCase) ? 12 : 0);
                startsAt = hour * 60 + int.Parse(time.Groups[2].Value, CultureInfo.InvariantCulture);
                line = line[time.Length..].Trim();
            }
            line = Bullet().Replace(line, "");
            int? minutes = null;
            if (Length().Match(line) is { Success: true } length)
            {
                minutes = int.Parse(length.Groups[1].Value, CultureInfo.InvariantCulture);
                line = line.Remove(length.Index, length.Length).Trim(' ', '-', '–', '—', ',', '(', ')');
            }
            if (line.Length == 0) continue;

            Row row;
            if (line.EndsWith(':') || (line.Length <= 40 && line.Any(char.IsLetter) && line == line.ToUpperInvariant() && minutes is null && startsAt is null))
            {
                row = new Row(Header, CultureInfo.InvariantCulture.TextInfo.ToTitleCase(line.TrimEnd(':').ToLowerInvariant()));
            }
            else if (SongLabel().Match(line) is { Success: true } label)
            {
                var (title, author) = TitleAndAuthor(line[label.Length..].Trim());
                row = Finished(Song, title, author, minutes, null, matcher);
            }
            else if (ScriptureReference.IsReferenceLine(line) || ReadingLabel().IsMatch(line))
            {
                var reference = ReadingLabel().Replace(line, "").Trim();
                row = Finished(Scripture, line, null, minutes, reference, matcher);
            }
            else
            {
                var (title, author) = TitleAndAuthor(line);
                var asSong = Finished(Song, title, author, minutes, null, matcher);
                row = asSong.Song is not null || asSong.Ambiguous ? asSong : new Row(Item, Clip(line), Minutes: minutes);
            }
            rows.Add((row, startsAt));
        }
        // Start times give lengths: a row lasts until the next row that has a time.
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Row.Minutes is not null || rows[i].Row.Kind == Header || rows[i].StartsAt is not { } from) continue;
            if (rows.Skip(i + 1).FirstOrDefault(r => r.StartsAt is not null).StartsAt is { } until && until > from && until - from <= 180)
                rows[i] = (rows[i].Row with { Minutes = until - from }, from);
        }
        return new Plan(Clip(name ?? "Order of service"), date, [.. rows.Select(r => r.Row)]);
    }

    private static Row Finished(string kind, string title, string? author, int? minutes, string? reference, PlanSongMatcher matcher)
    {
        switch (kind)
        {
            case Song:
                var found = matcher.Find(title, author, null);
                return new Row(Song, found.Song?.Name ?? title, author?.Trim() is { Length: > 0 } by ? by : null, minutes, Song: found.Song, Ambiguous: found.Ambiguous);
            case Scripture:
                // Our own parser's reading of the reference, so the row is named as the Bible window names it.
                var parsed = reference is null ? null : PassageReference.Parse(reference) ?? PassageReference.Parse(title);
                return new Row(Scripture, parsed is null ? title : parsed.Display, Minutes: minutes, Reference: parsed);
            case Header:
                return new Row(Header, title);
            default:
                return new Row(Item, title, Minutes: minutes);
        }
    }

    /// <summary>
    /// The plan as a service document. Songs in the library are rows that play them; a song that
    /// isn't there is a plain row that remembers its title and author (the fields a Planning
    /// Center import leaves for linking later); readings and everything else are plain rows.
    /// </summary>
    public static Service Build(Plan plan, DateOnly today) => new()
    {
        Id = LibraryStore.NewId(),
        Name = plan.Name,
        ServiceDate = plan.Date ?? today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        Items = [.. plan.Rows.Select(row => row switch
        {
            { Kind: Header } => new ServiceItem { Id = LibraryStore.NewId(), ItemKind = ServiceItemKind.Header, Name = row.Title, RefId = "" },
            { Song: { } song } => new ServiceItem
            {
                Id = LibraryStore.NewId(), ItemKind = ServiceItemKind.Presentation, Name = song.Name, RefId = song.Id, Duration = Seconds(row),
            },
            { Kind: Song } => new ServiceItem
            {
                Id = LibraryStore.NewId(), ItemKind = ServiceItemKind.Info, Name = row.Title, RefId = "", Duration = Seconds(row),
                MxuKind = "song", MxuSongTitle = row.Title, MxuSongAuthor = row.Author,
            },
            _ => new ServiceItem { Id = LibraryStore.NewId(), ItemKind = ServiceItemKind.Info, Name = row.Title, RefId = "", Duration = Seconds(row) },
        })],
    };

    /// <summary>The plan in a few words: "9 rows · 3 songs matched · 1 not in your library".</summary>
    public static string Summary(Plan plan)
    {
        var text = new StringBuilder($"{plan.Rows.Count} row{(plan.Rows.Count == 1 ? "" : "s")}");
        if (plan.SongsMatched > 0) text.Append(CultureInfo.InvariantCulture, $" · {plan.SongsMatched} song{(plan.SongsMatched == 1 ? "" : "s")} matched to your library");
        if (plan.MissingSongs.Count > 0) text.Append(CultureInfo.InvariantCulture, $" · {plan.MissingSongs.Count} not in your library");
        return text.ToString();
    }

    private static int? Seconds(Row row) => row.Minutes is { } minutes ? minutes * 60 : null;

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Clip(string text) => text.Length <= 120 ? text : text[..120];

    // "Way Maker - Sinach", "Way Maker (Sinach)", "Way Maker by Sinach".
    private static (string Title, string? Author) TitleAndAuthor(string line)
    {
        var match = TitleAuthor().Match(line);
        return match.Success ? (match.Groups[1].Value.Trim(), match.Groups[2].Value.Trim(' ', ')')) : (line, null);
    }

    private static string? DateIn(string line)
    {
        foreach (Match candidate in DateLike().Matches(line))
        {
            var plain = Ordinal().Replace(candidate.Value, ""); // "2nd November 2026" → "2 November 2026"
            if (DateOnly.TryParse(plain, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                || DateOnly.TryParse(plain, CultureInfo.CurrentCulture, DateTimeStyles.None, out date))
                return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        return null;
    }

    [GeneratedRegex(@"\b(service|order|bulletin|worship|sunday|saturday|week|gathering|mass)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ServiceTitle();

    [GeneratedRegex(@"^(\d{1,2})[:.](\d{2})\s*(?:([ap])\.?m\b\.?)?\s*[-–—:]?\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StartTime();

    [GeneratedRegex(@"^\s*(?:[-*•·]|\d{1,2}[.)])\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Bullet();

    [GeneratedRegex(@"[\s(\-–—,]*\b(\d{1,3})\s*(?:min(?:ute)?s?|')\b\.?\)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Length();

    [GeneratedRegex(@"^(song|hymn|worship song|closing song|opening song)\s*[:\-–—]\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SongLabel();

    [GeneratedRegex(@"^(scripture(\s+reading)?|reading|bible reading)\s*[:\-–—]\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReadingLabel();

    [GeneratedRegex(@"^(.+?)(?:\s+[-–—]\s+|\s+by\s+|\s*\()(.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TitleAuthor();

    [GeneratedRegex(@"(?<=\d)(st|nd|rd|th)\b", RegexOptions.CultureInvariant)]
    private static partial Regex Ordinal();

    [GeneratedRegex(@"\b(?:\d{4}-\d{2}-\d{2}|\d{1,2}(?:st|nd|rd|th)?\s+[A-Z][a-z]+\s+\d{4}|[A-Z][a-z]+\s+\d{1,2}(?:st|nd|rd|th)?,?\s+\d{4})\b", RegexOptions.CultureInvariant)]
    private static partial Regex DateLike();
}
