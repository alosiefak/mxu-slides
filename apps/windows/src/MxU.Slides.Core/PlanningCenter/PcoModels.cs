namespace MxU.Slides.Core.PlanningCenter;

// What the import reads from Planning Center Services, as plain records. Each one is made from a
// JSON:API resource (PcoDocument.cs) by its From method, which names the attributes it reads.
// The shapes follow Planning Center's documentation for Services v2
// (developer.planning.center/docs/#/apps/services).

/// <summary>A kind of service a church plans ("Sunday Morning", "Youth").</summary>
public sealed record PcoServiceType(string Id, string Name, string? Frequency)
{
    public static PcoServiceType From(PcoResource r) => new(r.Id, r.Trimmed("name") ?? "Service", r.Trimmed("frequency"));
}

/// <summary>One plan: a dated service of a service type.</summary>
public sealed record PcoPlan(
    string Id,
    string ServiceTypeId,
    string? Title,
    string? SeriesTitle,
    string? Dates,
    DateTimeOffset? SortDate,
    int ItemsCount)
{
    public static PcoPlan From(PcoResource r, string serviceTypeId) => new(
        r.Id,
        r.One("service_type")?.Id ?? serviceTypeId,
        r.Trimmed("title"),
        r.Trimmed("series_title"),
        r.Trimmed("dates"),
        r.Time("sort_date"),
        r.Integer("items_count") ?? 0);

    /// <summary>The plan's date where the church is (sort_date is in UTC), as the library stores it: yyyy-MM-dd.</summary>
    public string? LocalDate(TimeZoneInfo zone) =>
        SortDate is { } when ? TimeZoneInfo.ConvertTime(when, zone).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : null;

    /// <summary>"Week 3: Grace" or, untitled, "Rooted" (its series), or nothing.</summary>
    public string? DisplayTitle => Title ?? SeriesTitle;
}

/// <summary>A song in the church's Planning Center song library.</summary>
public sealed record PcoSong(string Id, string Title, string? Author, int? CcliNumber, string? Copyright)
{
    public static PcoSong From(PcoResource r) => new(
        r.Id, r.Trimmed("title") ?? "Untitled Song", r.Trimmed("author"),
        r.Integer("ccli_number") is { } n and > 0 ? n : null, r.Trimmed("copyright"));
}

/// <summary>
/// One way a song is played: its words (<see cref="Lyrics"/>), its chord chart, the order of
/// its sections (<see cref="Sequence"/>: "Verse 1", "Chorus", …) and its tempo and length.
/// </summary>
public sealed record PcoArrangement(
    string Id,
    string Name,
    string? Lyrics,
    string? ChordChart,
    bool HasChords,
    string? ChordChartKey,
    IReadOnlyList<string> Sequence,
    int? LengthSeconds,
    double? Bpm,
    string? Meter)
{
    public static PcoArrangement From(PcoResource r) => new(
        r.Id, r.Trimmed("name") ?? "Default Arrangement", r.Trimmed("lyrics"), r.Trimmed("chord_chart"),
        r.Flag("has_chords"), r.Trimmed("chord_chart_key"),
        r.Texts("sequence") is { Count: > 0 } full ? full : r.Texts("sequence_short"),
        r.Integer("length") is { } length and > 0 ? length : null,
        r.Number("bpm") is { } bpm and > 0 ? bpm : null,
        r.Trimmed("meter"));
}

/// <summary>The key a song is played in for this plan.</summary>
public sealed record PcoKey(string Id, string? Name, string? StartingKey)
{
    public static PcoKey From(PcoResource r) => new(r.Id, r.Trimmed("name"), r.Trimmed("starting_key"));
}

/// <summary>A note on a plan item: "Lighting: dim for the prayer".</summary>
public sealed record PcoItemNote(string? Category, string Content);

/// <summary>What kind of row a plan item is (Planning Center's item_type).</summary>
public enum PcoItemType { Song, Header, Media, Item }

/// <summary>
/// One row of a plan, with what it points at already looked up: a song item has its
/// <see cref="Song"/>, <see cref="Arrangement"/> and <see cref="Key"/>; a media item names its media.
/// </summary>
public sealed record PcoPlanItem(
    string Id,
    string Title,
    PcoItemType Type,
    int Sequence,
    int? LengthSeconds,
    string? ServicePosition,
    string? Description,
    string? KeyName,
    PcoSong? Song,
    PcoArrangement? Arrangement,
    PcoKey? Key,
    IReadOnlyList<string> MediaTitles,
    IReadOnlyList<PcoItemNote> Notes)
{
    public static PcoItemType TypeOf(string? raw) => raw switch
    {
        "song" => PcoItemType.Song,
        "header" => PcoItemType.Header,
        "media" => PcoItemType.Media,
        _ => PcoItemType.Item,
    };

    /// <summary>The item_type as Planning Center spells it, kept on the row (ServiceItem.mxuKind).</summary>
    public string RawType => Type.ToString().ToLowerInvariant();

    public static PcoPlanItem From(PcoResource r, PcoDocument document)
    {
        var song = document.Find(r.One("song")) is { } s ? PcoSong.From(s) : null;
        var arrangement = document.Find(r.One("arrangement")) is { } a ? PcoArrangement.From(a) : null;
        var key = document.Find(r.One("key")) is { } k ? PcoKey.From(k) : null;
        var media = r.Many("media").Select(document.Find).OfType<PcoResource>()
            .Select(m => m.Trimmed("title")).OfType<string>().ToList();
        var notes = r.Many("item_notes").Select(document.Find).OfType<PcoResource>()
            .Select(n => n.Trimmed("content") is { } content ? new PcoItemNote(n.Trimmed("category_name"), content) : null)
            .OfType<PcoItemNote>().ToList();
        var title = r.Trimmed("title") ?? song?.Title ?? "Untitled";
        return new PcoPlanItem(
            r.Id, title, TypeOf(r.Trimmed("item_type")), r.Integer("sequence") ?? 0,
            r.Integer("length") is { } length and > 0 ? length : null,
            r.Trimmed("service_position"), r.Trimmed("description"), r.Trimmed("key_name"),
            song, arrangement, key, media, notes);
    }
}

/// <summary>Everything the import needs from one plan: the plan, its service type's name and its items in order.</summary>
public sealed record PcoPlanContents(PcoPlan Plan, string ServiceTypeName, IReadOnlyList<PcoPlanItem> Items);
