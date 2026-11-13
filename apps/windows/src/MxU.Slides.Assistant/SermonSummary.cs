using System.Globalization;
using System.Text;
using System.Text.Json;
using MxU.Slides.Core.Library;

namespace MxU.Slides.Assistant;

/// <summary>
/// The sermon summary, for the media team after the service.
/// <list type="number">
/// <item><b>The recording is transcribed on this PC</b> by Windows' offline speech recognizer (the
/// spotter's worker, reading a sound file). The sound is never sent anywhere.</item>
/// <item><b>Only the text goes to the assistant</b>, with prayer-request lines held back first,
/// and it writes a summary, key quotes and drafts for social posts.</item>
/// </list>
/// <b>Quotes are checked.</b> The assistant gives each quote with the transcript line it starts
/// on. The app looks for the quote's words, in order, in the transcript around that line; a quote
/// that isn't there word for word is thrown away, and the timestamp shown is the transcript's own,
/// never the assistant's. So a quote can be found in the recording to cut a clip.
/// <para>
/// A caution that belongs here: Windows' recognizer makes mistakes on real preaching, so the
/// transcript, and anything written from it, needs a person's read before it is posted.
/// </para>
/// </summary>
public static class SermonSummary
{
    /// <summary>One thing heard, and when in the recording.</summary>
    public sealed record Heard(TimeSpan At, string Text);

    public sealed record Quote(TimeSpan At, string Text);

    public sealed record Summary(string Text, IReadOnlyList<Quote> Quotes, IReadOnlyList<string> Posts, int QuotesDropped);

    public sealed record Prepared(AssistantRequest Request, string? PrivacyWarning);

    public static string Schema => JsonSerializer.Serialize(new
    {
        type = "object",
        properties = new
        {
            summary = new { type = "string" },
            quotes = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new { line = new { type = "integer" }, text = new { type = "string" } },
                    required = new[] { "line", "text" },
                    additionalProperties = false,
                },
            },
            posts = new { type = "array", items = new { type = "string" } },
        },
        required = new[] { "summary", "quotes", "posts" },
        additionalProperties = false,
    });

    private const string Instructions =
        """
        You help a church's media team after a service. You are given a transcript of the sermon made by an offline speech recognizer, as numbered lines with the time each was said. It has recognition mistakes and no punctuation; read through them for the sense.

        Write:
        - summary: the sermon in one or two short paragraphs, for the church's website: its main point, how the preacher built it, and what he or she asked people to do. Plain words. Say only what the transcript supports; if a part is too garbled to follow, leave it out rather than guess.
        - quotes: up to six sentences worth sharing as short video clips. For each, give the number of the line it starts on, and its words EXACTLY as the transcript has them, in order (you may add punctuation and capitals, but not change, add or drop a word): the app finds the quote in the recording by those words, and throws away any quote that doesn't match.
        - posts: three short drafts for social media (under 280 characters each), in the church's own voice, each built on a point from the sermon. No hashtags unless the sermon gave one. Do not quote the Bible from memory: name the reference instead.

        Leave out lines shown as "(private: not sent)". Do not name anyone the preacher asked prayer for.
        """;

    /// <summary>The transcript as the assistant is given it: "[12] (14:05) and that is why grace…".</summary>
    public static Prepared Prepare(IReadOnlyList<Heard> transcript)
    {
        var safe = PrayerPrivacy.Strip([.. transcript.Select(h => h.Text)]);
        var ask = new StringBuilder("The transcript:\n");
        for (var i = 0; i < transcript.Count; i++)
            ask.Append(CultureInfo.InvariantCulture, $"[{i + 1}] ({ServiceReport.Clock(transcript[i].At)}) {safe.Lines[i]}\n");
        return new Prepared(new AssistantRequest
        {
            Feature = "Sermon summary", Instructions = Instructions, Ask = ask.ToString(), Schema = Schema, Limit = TimeSpan.FromMinutes(4),
        }, safe.Warning);
    }

    public static Summary Read(JsonElement reply, IReadOnlyList<Heard> transcript)
    {
        if (reply.ValueKind != JsonValueKind.Object) return new Summary("", [], [], 0);
        var text = reply.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.String ? summary.GetString()!.Trim() : "";
        var quotes = new List<Quote>();
        var dropped = 0;
        if (reply.TryGetProperty("quotes", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray().Take(12))
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("line", out var line) && line.ValueKind == JsonValueKind.Number && line.TryGetInt32(out var number)
                    && item.TryGetProperty("text", out var words) && words.ValueKind == JsonValueKind.String
                    && Locate(words.GetString()!, number - 1, transcript) is { } at)
                    quotes.Add(new Quote(at, words.GetString()!.Trim()));
                else dropped++;
            }
        }
        List<string> posts = reply.TryGetProperty("posts", out var drafts) && drafts.ValueKind == JsonValueKind.Array
            ? [.. drafts.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()!.Trim()).Where(p => p.Length > 0).Take(5)]
            : [];
        return new Summary(text, quotes, posts, dropped);
    }

    /// <summary>
    /// When a quote was said: its words (letters and digits only, any case) must run, in order and
    /// unbroken, through the transcript starting at the named line or one either side. The time is
    /// that line's. Null when the words aren't there.
    /// </summary>
    internal static TimeSpan? Locate(string quote, int line, IReadOnlyList<Heard> transcript)
    {
        var wanted = ServiceLinkLogic.Normalize(quote);
        if (wanted.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 3) return null;
        foreach (var start in new[] { line, line - 1, line + 1 })
        {
            if (start < 0 || start >= transcript.Count) continue;
            var window = ServiceLinkLogic.Normalize(string.Join(' ', transcript.Skip(start).Take(4).Select(h => h.Text)));
            var first = ServiceLinkLogic.Normalize(transcript[start].Text);
            var at = $" {window} ".IndexOf($" {wanted} ", StringComparison.Ordinal);
            // It must begin inside the starting line, or the time would be the wrong line's.
            if (at >= 0 && at <= first.Length) return transcript[start].At;
        }
        return null;
    }

    /// <summary>The transcript as text to read or save: "14:05  and that is why grace…".</summary>
    public static string TranscriptText(IReadOnlyList<Heard> transcript) =>
        string.Join("\n", transcript.Select(h => $"{ServiceReport.Clock(h.At),7}  {h.Text}"));

    /// <summary>Everything, as text for the media team.</summary>
    public static string Text(Summary summary)
    {
        var text = new StringBuilder("SUMMARY\n" + summary.Text + "\n");
        if (summary.Quotes.Count > 0)
        {
            text.Append("\nKEY QUOTES (times are in the recording)\n");
            foreach (var quote in summary.Quotes) text.Append(CultureInfo.InvariantCulture, $"  {ServiceReport.Clock(quote.At)}  “{quote.Text}”\n");
        }
        if (summary.Posts.Count > 0)
        {
            text.Append("\nSOCIAL POST DRAFTS\n");
            foreach (var post in summary.Posts) text.Append(CultureInfo.InvariantCulture, $"  • {post}\n");
        }
        return text.ToString();
    }
}
