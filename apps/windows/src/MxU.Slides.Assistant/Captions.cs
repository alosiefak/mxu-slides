using System.Text.Json;

namespace MxU.Slides.Assistant;

/// <summary>
/// Live captions: what the speech recognizer hears (on this PC, by the same worker the scripture
/// spotter uses) shown as a line or two of text on the stage screens or over the audience picture.
/// <para>
/// Captions travel as a live <i>alert</i> (HOW-IT-WORKS section 9): something that is on screen
/// now and is written into no document, so nothing new is ever saved to the shared library. The
/// operator turns them on; from then each sentence appears as it is heard.
/// </para>
/// <para>
/// <b>Translation is optional.</b> Each finished sentence can be sent to the assistant alone and
/// shown in another language a second or two later. The sound never leaves the PC; only that
/// sentence's text does, and a sentence that names someone to pray for is shown as heard and not sent.
/// </para>
/// </summary>
public static class Captions
{
    /// <summary>The alert id captions go up under (so they replace themselves, and another alert is left alone).</summary>
    public const string AlertId = "live-captions";

    /// <summary>A heard sentence made presentable: trimmed, first letter capital.</summary>
    public static string Tidy(string heard)
    {
        var text = string.Join(' ', heard.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length == 0 ? "" : char.ToUpperInvariant(text[0]) + text[1..];
    }

    /// <summary>True when a sentence may be sent for translation (it doesn't name someone to pray for).</summary>
    public static bool MaySend(string sentence) => PrayerPrivacy.Strip([sentence]).Withheld.Count == 0;

    public static string Schema => JsonSerializer.Serialize(new
    {
        type = "object",
        properties = new { text = new { type = "string" } },
        required = new[] { "text" },
        additionalProperties = false,
    });

    /// <summary>One sentence to translate: quick, and with nothing else of the sermon.</summary>
    public static AssistantRequest TranslateRequest(string sentence, string language) => new()
    {
        Feature = "Captions translation",
        Instructions =
            $"You translate live captions of a church service into {language}. You are given one sentence as a speech recognizer heard it, so it may have small mistakes and no punctuation. Return its natural {language} translation as text, in one line, with punctuation. If the sentence quotes the Bible, translate the meaning plainly; do not present it as a quotation. Return only the translation.",
        Ask = sentence, Schema = Schema, Effort = AssistantEffort.Low, Limit = TimeSpan.FromSeconds(20),
    };

    /// <summary>The translation, or null when the reply has none worth showing.</summary>
    public static string? ReadTranslation(JsonElement reply, string sentence) =>
        reply.ValueKind == JsonValueKind.Object && reply.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
        && text.GetString()!.ReplaceLineEndings(" ").Trim() is { Length: > 0 } words && words.Length <= sentence.Length * 4 + 60
            ? words : null;
}

/// <summary>
/// The caption on screen: the newest words that fit in about two lines. Each new sentence is
/// added at the end and the oldest words fall off the front, a whole word at a time.
/// </summary>
public sealed class CaptionRoll(int mostLetters = 110)
{
    public string Current { get; private set; } = "";

    public string Add(string sentence)
    {
        var tidy = Captions.Tidy(sentence);
        if (tidy.Length == 0) return Current;
        var all = Current.Length == 0 ? tidy : $"{Current} {tidy}";
        while (all.Length > mostLetters && all.IndexOf(' ') is var space and >= 0) all = all[(space + 1)..];
        if (all.Length > mostLetters) all = all[^mostLetters..];
        return Current = all;
    }

    public void Clear() => Current = "";
}
