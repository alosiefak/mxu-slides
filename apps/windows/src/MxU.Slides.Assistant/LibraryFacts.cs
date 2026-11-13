using System.Globalization;
using System.Text;
using System.Text.Json;
using MxU.Slides.Core.Library;

namespace MxU.Slides.Assistant;

/// <summary>
/// What the assistant may look up about this church's library while it works: the names and ids
/// of what's in it, the service that's open, and the themes. The app takes this snapshot on the
/// UI thread just before a job; the look-ups then run on the assistant's own thread against the
/// snapshot, so they never touch the live library.
/// <para>
/// This is how the model works from real data: it can only name a song, a lower third or a theme
/// that a look-up returned, and the code that reads its answer checks every id against the same
/// snapshot (<see cref="Has"/>). Names and ids leave the PC when a look-up is answered; slide
/// words, lyrics and files do not.
/// </para>
/// </summary>
/// <param name="CurrentService">The open service as lines of text, for <c>read_current_service</c>; empty when none is open.</param>
public sealed record LibraryFacts(IReadOnlyList<LibraryFacts.Entry> Entries, string CurrentService)
{
    /// <param name="Kind">The Local API's name for the kind: "presentations", "overlays", "media", "themes"…</param>
    /// <param name="Detail">A folder, or a song's author: something to tell two of the same name apart.</param>
    public sealed record Entry(string Id, string Kind, string Name, string? Detail = null);

    public static readonly LibraryFacts Empty = new([], "");

    public bool Has(string id) => Entries.Any(e => e.Id == id);

    public Entry? Find(string id) => Entries.FirstOrDefault(e => e.Id == id);

    /// <summary>
    /// Entries whose name holds every word of <paramref name="query"/> (case and punctuation
    /// ignored), exact names first: "giving" finds the lower third "Giving" and the song
    /// "The Giving Song".
    /// </summary>
    public List<Entry> Search(string query, string? kind = null, int limit = 12)
    {
        var words = ServiceLinkLogic.Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return [];
        var wanted = string.Join(' ', words);
        return [.. Entries
            .Where(e => kind is null || e.Kind == kind)
            .Select(e => (Entry: e, Name: ServiceLinkLogic.Normalize(e.Name)))
            .Where(x => words.All(w => $" {x.Name} ".Contains($" {w}", StringComparison.Ordinal)))
            .OrderBy(x => x.Name == wanted ? 0 : x.Name.StartsWith(wanted, StringComparison.Ordinal) ? 1 : 2)
            .ThenBy(x => x.Name.Length)
            .Take(limit)
            .Select(x => x.Entry)];
    }

    /// <summary>The kinds present, for a tool's description ("presentations, overlays, media").</summary>
    public IReadOnlyList<string> Kinds => [.. Entries.Select(e => e.Kind).Distinct().Order(StringComparer.Ordinal)];

    // MARK: The look-ups

    /// <summary>
    /// The three look-ups every job that needs the library gets: search it, read the open
    /// service, list the themes.
    /// </summary>
    public IReadOnlyList<AssistantTool> Tools =>
    [
        new AssistantTool(
            "search_library",
            "Searches this church's library by name. Returns up to 12 matches as lines: kind, name, id (and a folder or author). "
            + "Use it before naming any song, slide deck, lower third, media or alert: only what it returns exists.",
            new Dictionary<string, JsonElement>
            {
                ["query"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Words from the name, for example: giving" }),
                ["kind"] = JsonSerializer.SerializeToElement(new { type = "string", description = $"Optional. One of: {string.Join(", ", Kinds)}" }),
            },
            ["query"],
            input => SearchAnswer(
                input["query"].GetString() ?? "",
                input.TryGetValue("kind", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() is { Length: > 0 } named ? named : null)),
        new AssistantTool(
            "read_current_service", "Returns the service that is open now: its rows in order, with each row's id, kind, planned length, and which one is live.",
            new Dictionary<string, JsonElement>(), [],
            _ => CurrentService.Length > 0 ? CurrentService : "No service is open."),
        new AssistantTool(
            "list_themes", "Lists this church's themes (the looks slides are made in), with their ids.",
            new Dictionary<string, JsonElement>(), [],
            _ => Listed(Entries.Where(e => e.Kind == "themes").ToList(), "There are no themes.")),
    ];

    private string SearchAnswer(string query, string? kind)
    {
        var found = Search(query, kind);
        // A kind that found nothing is tried again without it: the model often guesses the kind wrong.
        if (found.Count == 0 && kind is not null) found = Search(query);
        return Listed(found, $"Nothing in the library is named like “{query}”.");
    }

    private static string Listed(IReadOnlyList<Entry> entries, string none)
    {
        if (entries.Count == 0) return none;
        var text = new StringBuilder();
        foreach (var e in entries)
            text.Append(CultureInfo.InvariantCulture, $"{e.Kind} | {e.Name} | id {e.Id}{(e.Detail is { Length: > 0 } detail ? $" | {detail}" : "")}\n");
        return text.ToString().TrimEnd();
    }
}
