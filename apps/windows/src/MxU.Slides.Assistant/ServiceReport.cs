using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MxU.Slides.Assistant;

/// <summary>
/// One line of the service's running record: a row of the service became the current one (its
/// first slide, media or music was fired), or the service's timers were cleared
/// (<see cref="ItemId"/> null: the end).
/// </summary>
/// <param name="Run">One sitting of a service: a new id each time tracking starts from nothing.</param>
public sealed record ServiceRunEntry(string Run, string ServiceId, string ServiceName, DateTimeOffset At, string? ItemId, string? Name, string? Section, int? PlannedSeconds);

/// <summary>
/// The running record of services on this PC: a small file of lines in the app's local data
/// folder (never the shared library, so nothing new reaches a Mac). Service tracking
/// (HOW-IT-WORKS section 36) already knows when each row is fired but forgets when the service
/// ends; this keeps it, so a report can be made afterwards. Written on the UI thread, one short
/// line at a time; a failure to write is ignored (a report is not worth interrupting a service).
/// </summary>
public sealed class ServiceRunLog(string path)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private readonly Lock gate = new();

    public string FilePath => path;

    public void Add(ServiceRunEntry entry)
    {
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, JsonSerializer.Serialize(entry, Options) + "\n");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public List<ServiceRunEntry> Entries()
    {
        var entries = new List<ServiceRunEntry>();
        try
        {
            lock (gate)
            {
                if (!File.Exists(path)) return entries;
                foreach (var line in File.ReadLines(path))
                {
                    try
                    {
                        if (JsonSerializer.Deserialize<ServiceRunEntry>(line, Options) is { } entry) entries.Add(entry);
                    }
                    catch (JsonException) { } // a half-written last line after a power cut
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return entries;
    }

    /// <summary>The sittings on record, newest first, each with its lines in order.</summary>
    public List<IReadOnlyList<ServiceRunEntry>> Runs() =>
        [.. Entries().GroupBy(e => e.Run).Select(g => (IReadOnlyList<ServiceRunEntry>)[.. g.OrderBy(e => e.At)]).OrderByDescending(r => r[0].At)];
}

/// <summary>
/// The service report: what ran when, and what went over its planned length. It is worked out
/// here from the running record with no AI at all. The assistant can add "notes for next week"
/// (<see cref="NotesRequest"/>), which are shown under the report as its suggestions.
/// </summary>
public static class ServiceReport
{
    /// <param name="Ran">How long the row was current: until the next row was fired, or the end. Null for the last row of a service still running with no end recorded.</param>
    /// <param name="Over">Ran minus planned when there was a plan; negative when it finished early.</param>
    public sealed record Line(string Name, string? Section, DateTimeOffset Started, TimeSpan? Ran, TimeSpan? Planned, TimeSpan? Over);

    public sealed record Report(string ServiceName, DateTimeOffset Started, DateTimeOffset? Ended, IReadOnlyList<Line> Lines, IReadOnlyList<string> NeverFired)
    {
        public TimeSpan? Ran => Ended - Started;

        /// <summary>The planned length of the rows that ran (rows with no length planned count nothing).</summary>
        public TimeSpan Planned => TimeSpan.FromSeconds(Lines.Sum(l => l.Planned?.TotalSeconds ?? 0));

        /// <summary>Rows that ran more than <see cref="Tolerance"/> past their plan, longest overrun first.</summary>
        public IReadOnlyList<Line> WentOver => [.. Lines.Where(l => l.Over > Tolerance).OrderByDescending(l => l.Over)];
    }

    /// <summary>A row is "over" once it runs this far past its plan (nobody minds fifteen seconds).</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Builds the report for one sitting. A row lasts from its fire to the next one; the last row
    /// lasts until the end marker, or until <paramref name="now"/> when the service is still running.
    /// </summary>
    /// <param name="planned">The service's fireable rows (name), to list what was never fired.</param>
    public static Report? Build(IReadOnlyList<ServiceRunEntry> run, IReadOnlyList<string> planned, DateTimeOffset? now = null)
    {
        var fires = run.Where(e => e.ItemId is not null).OrderBy(e => e.At).ToList();
        if (fires.Count == 0) return null;
        var end = run.Where(e => e.ItemId is null && e.At >= fires[^1].At).Select(e => (DateTimeOffset?)e.At).FirstOrDefault() ?? now;
        var lines = new List<Line>();
        for (var i = 0; i < fires.Count; i++)
        {
            var until = i + 1 < fires.Count ? fires[i + 1].At : end;
            TimeSpan? ran = until is { } stop ? stop - fires[i].At : null;
            TimeSpan? plan = fires[i].PlannedSeconds is > 0 and var seconds ? TimeSpan.FromSeconds(seconds) : null;
            lines.Add(new Line(fires[i].Name ?? "Untitled", fires[i].Section, fires[i].At, ran, plan, ran - plan));
        }
        var fired = fires.Select(f => f.Name).ToHashSet();
        return new Report(fires[0].ServiceName, fires[0].At, end, lines, [.. planned.Where(name => !fired.Contains(name)).Distinct()]);
    }

    /// <summary>The report as plain text: to read, copy or save. No AI wrote any of it.</summary>
    public static string Text(Report report)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.CurrentCulture, $"{report.ServiceName}: service report\n");
        text.Append(CultureInfo.CurrentCulture, $"{report.Started:dddd d MMMM yyyy}, started {report.Started:t}");
        if (report.Ended is { } ended) text.Append(CultureInfo.CurrentCulture, $", ended {ended:t}");
        text.Append('\n');
        if (report.Ran is { } ran)
        {
            text.Append(CultureInfo.InvariantCulture, $"Ran {Clock(ran)}");
            if (report.Planned > TimeSpan.Zero) text.Append(CultureInfo.InvariantCulture, $" against {Clock(report.Planned)} planned for the rows that ran");
            text.Append(".\n");
        }
        text.Append('\n');
        string? section = null;
        foreach (var line in report.Lines)
        {
            // A header is printed when the service moves into a different named part.
            if (line.Section is { Length: > 0 } part && part != section)
            {
                text.Append(CultureInfo.InvariantCulture, $"{part.ToUpperInvariant()}\n");
                section = part;
            }
            text.Append(CultureInfo.CurrentCulture, $"  {line.Started:t}  {line.Name}");
            text.Append(CultureInfo.InvariantCulture, $"  ·  ran {(line.Ran is { } r ? Clock(r) : "(still running)")}");
            if (line.Planned is { } plan) text.Append(CultureInfo.InvariantCulture, $", planned {Clock(plan)}");
            if (line.Over is { } over && over > Tolerance) text.Append(CultureInfo.InvariantCulture, $"  ▲ over by {Clock(over)}");
            else if (line.Over is { } under && under < -Tolerance) text.Append(CultureInfo.InvariantCulture, $"  ▼ under by {Clock(-under)}");
            text.Append('\n');
        }
        text.Append('\n');
        var went = report.WentOver;
        text.Append(went.Count == 0
            ? "Nothing ran over its planned length.\n"
            : "Went over: " + string.Join("; ", went.Select(l => $"{l.Name} by {Clock(l.Over!.Value)}")) + ".\n");
        if (report.NeverFired.Count > 0) text.Append("Never shown: " + string.Join("; ", report.NeverFired) + ".\n");
        return text.ToString();
    }

    /// <summary>"4:05", "1:02:30".</summary>
    public static string Clock(TimeSpan time)
    {
        var seconds = (long)Math.Round(Math.Abs(time.TotalSeconds));
        return seconds >= 3600
            ? string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}");
    }

    // MARK: Notes for next week (the only AI part, and optional)

    public static string NotesSchema => JsonSerializer.Serialize(new
    {
        type = "object",
        properties = new { notes = new { type = "array", items = new { type = "string" } } },
        required = new[] { "notes" },
        additionalProperties = false,
    });

    /// <summary>Asks for up to five short notes for next week's planning. Only the report's text is sent (row names and times).</summary>
    public static AssistantRequest NotesRequest(Report report) => new()
    {
        Feature = "Service report notes",
        Instructions =
            """
            You help a church's production team plan next week. You are given a report of how this week's service ran: each row, when it started, how long it ran and how long was planned. Write up to five short, practical notes for next week's planning, most useful first: where the plan and the reality differ enough to change the plan (give the numbers), rows that had no planned length, rows that were planned but never shown. One sentence each. Say only what the report supports; if it ran close to plan, say so in one note. Do not comment on the content of the service.
            """,
        Ask = Text(report), Schema = NotesSchema, Effort = AssistantEffort.Low,
    };

    public static List<string> ReadNotes(JsonElement reply) =>
        reply.ValueKind == JsonValueKind.Object && reply.TryGetProperty("notes", out var notes) && notes.ValueKind == JsonValueKind.Array
            ? [.. notes.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.String).Select(n => n.GetString()!.Trim()).Where(n => n.Length > 0).Take(5)]
            : [];
}
