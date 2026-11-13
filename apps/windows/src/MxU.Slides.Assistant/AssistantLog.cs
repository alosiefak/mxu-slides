using System.Text.Json;
using System.Text.RegularExpressions;

namespace MxU.Slides.Assistant;

/// <summary>One job in the log: exactly the words that were sent and the words that came back.</summary>
/// <param name="Feature">What asked: "Make Slides", "Lyrics clean-up", "Scripture spotter", "Key test".</param>
/// <param name="Sent">The instructions and the request, as sent.</param>
/// <param name="Tools">Each look-up the model asked the app to do, and what the app answered.</param>
/// <param name="Received">The model's answer; empty when the job failed.</param>
/// <param name="Problem">Why it failed, in the operator's words; null when it worked.</param>
public sealed record AssistantLogEntry(
    DateTimeOffset At, string Feature, string Model, string Sent, IReadOnlyList<string> Tools, string Received,
    AssistantUsage Usage, decimal Cost, string? Problem);

/// <summary>
/// The record of everything the assistant sent to Anthropic and everything that came back, so a
/// church can see for itself what leaves the building. It's a text file on this PC
/// (<c>Assistant\log.jsonl</c> in the local data folder: one JSON object per line), never in the
/// library. The API key can't appear in it: it's never part of what's logged, and
/// <see cref="Scrub"/> blanks anything that looks like one in case a pastor pasted a key into
/// their notes. Thread-safe.
/// </summary>
public sealed partial class AssistantLog(string path)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Lock gate = new();
    private List<AssistantLogEntry>? entries;

    /// <summary>Raised after an entry is added (on the thread that added it).</summary>
    public event Action? Changed;

    public string FilePath => path;

    public IReadOnlyList<AssistantLogEntry> Entries
    {
        get { lock (gate) return [.. Loaded()]; }
    }

    public void Add(AssistantLogEntry entry)
    {
        var clean = entry with
        {
            Sent = Scrub(entry.Sent), Received = Scrub(entry.Received), Tools = [.. entry.Tools.Select(Scrub)],
            Problem = entry.Problem is null ? null : Scrub(entry.Problem),
        };
        lock (gate)
        {
            Loaded().Add(clean);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, JsonSerializer.Serialize(clean, Json) + "\n");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A log that can't be written is no reason to stop a job; it's still in memory.
            }
        }
        Changed?.Invoke();
    }

    /// <summary>The tokens and dollars of every job since <paramref name="since"/>.</summary>
    public (AssistantUsage Usage, decimal Cost, int Jobs) Total(DateTimeOffset since)
    {
        lock (gate)
        {
            var recent = Loaded().Where(e => e.At >= since).ToList();
            return (recent.Aggregate(AssistantUsage.None, (sum, e) => sum.Plus(e.Usage)), recent.Sum(e => e.Cost), recent.Count);
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            entries = [];
            try { File.Delete(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        Changed?.Invoke();
    }

    private List<AssistantLogEntry> Loaded()
    {
        if (entries is not null) return entries;
        entries = [];
        try
        {
            if (!File.Exists(path)) return entries;
            foreach (var line in File.ReadLines(path))
            {
                // A line cut short by a power cut is skipped; the rest still reads.
                try
                {
                    if (JsonSerializer.Deserialize<AssistantLogEntry>(line, Json) is { } entry) entries.Add(entry);
                }
                catch (JsonException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return entries;
    }

    /// <summary>Text with anything shaped like an Anthropic key ("sk-ant-…") blanked out.</summary>
    public static string Scrub(string text) => KeyShape().Replace(text, "[key removed]");

    [GeneratedRegex(@"sk-ant-[A-Za-z0-9_\-]{8,}")]
    private static partial Regex KeyShape();
}
