using System.Globalization;
using System.Text;
using System.Text.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scripture;

namespace MxU.Slides.Assistant;

/// <summary>
/// Translation: a second-language copy of an announcement or sermon-points deck, for a
/// congregation that reads more than one language.
/// <list type="bullet">
/// <item><b>A copy, clearly marked.</b> The original is never touched. The copy's name says the
/// language and "AI translation: check before use", it is filed under "Translations", and each
/// translated slide's notes keep the original words, so a reader of both can check it.</item>
/// <item><b>Scripture is never translated by the AI.</b> A slide that holds a Bible passage is not
/// sent at all. The Bibles built into the app are English (KJV, ASV, WEB), so there is no real
/// translation to take another language's verses from: the slide stays as it is in the copy, and
/// the window says so.</item>
/// <item><b>Songs are refused.</b> Lyrics are licensed in each language; a machine translation of
/// them isn't the church's to show.</item>
/// <item><b>The answer can only fill boxes that were sent.</b> Each piece of text goes out
/// numbered, and comes back by number. A number that wasn't sent is ignored; a piece that didn't
/// come back stays in the original language and is listed.</item>
/// </list>
/// What's saved is an ordinary deck: the same objects with different words.
/// </summary>
public static class SlideTranslator
{
    /// <summary>The languages offered. (Any language Claude writes would work; these are the common ones.)</summary>
    public static readonly IReadOnlyList<string> Languages =
        ["Spanish", "French", "Portuguese", "German", "Italian", "Korean", "Chinese (Simplified)", "Tagalog", "Yoruba", "Igbo", "Swahili", "Arabic"];

    public const string Folder = "Translations";

    /// <summary>One box's words, as sent.</summary>
    public sealed record Piece(int Number, string SlideId, string ObjectId, string Text);

    /// <param name="Request">Null when there is nothing to send (a song, or nothing but scripture).</param>
    /// <param name="ScriptureSlides">Slide numbers (from 1) held back because they hold a Bible passage.</param>
    /// <param name="Refused">Why nothing can be translated; null when something can.</param>
    public sealed record Prepared(AssistantRequest? Request, IReadOnlyList<Piece> Pieces, IReadOnlyList<int> ScriptureSlides, string? PrivacyWarning, string? Refused);

    /// <param name="Notes">What to tell the operator: scripture left alone, pieces that didn't come back.</param>
    public sealed record Translated(Presentation Copy, int Pieces, IReadOnlyList<string> Notes);

    public static string Schema => JsonSerializer.Serialize(new
    {
        type = "object",
        properties = new
        {
            translations = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new { n = new { type = "integer" }, text = new { type = "string" } },
                    required = new[] { "n", "text" },
                    additionalProperties = false,
                },
            },
        },
        required = new[] { "translations" },
        additionalProperties = false,
    });

    private static string Instructions(string language) =>
        $"""
        You translate the words on a church's announcement and sermon-point slides into {language}, for members of the congregation who read {language}. A bilingual person will check your work before it is shown.

        You are given numbered pieces of text, one per text box. Return each piece's translation under the same number.
        - Translate naturally and warmly, as a church in that language would say it. Keep it as short as the original: it has to fit the same box.
        - Keep the line breaks where the original has them, and keep names, times, dates, places, web addresses and phone numbers exactly as written.
        - If a piece quotes the Bible, do not translate the quotation: return that piece unchanged. Bible text must come from a published translation, which the church adds itself.
        - Return every number you were given, and no others.
        """;

    /// <summary>True for a box that holds a Bible passage or reference (it is never sent).</summary>
    public static bool IsScripture(string text) =>
        ScriptureReference.SplitText(text) is not null || text.Split('\n').Any(line => line.Trim().Length > 0 && ScriptureReference.IsReferenceLine(line.Trim()));

    public static Prepared Prepare(Presentation deck, string language)
    {
        // A song by any of the app's signs: it says so, it has sections (verse, chorus), or it carries a CCLI licence.
        if (deck.PresentationKind == PresentationKind.Song || deck.Sections is { Count: > 0 } || deck.Ccli is not null)
            return new Prepared(null, [], [], null, "Songs aren't translated here: lyrics are licensed in each language. Add the published translation as its own song.");

        var pieces = new List<Piece>();
        var scripture = new List<int>();
        for (var s = 0; s < deck.Slides.Count; s++)
        {
            var slide = deck.Slides[s];
            var boxes = slide.Objects.Where(o => o.Text.Trim().Length > 0 && o.TextLink is null).ToList();
            if (boxes.Count == 0) continue;
            // One box of scripture holds back the whole slide: its reference and verse belong together.
            if (boxes.Any(o => IsScripture(o.Text)))
            {
                scripture.Add(s + 1);
                continue;
            }
            foreach (var box in boxes) pieces.Add(new Piece(pieces.Count + 1, slide.Id, box.Id, box.Text));
        }
        // A prayer request on an announcement slide stays here; that piece is simply not translated.
        var safe = PrayerPrivacy.Strip([.. pieces.Select(p => p.Text.Replace('\n', ' '))]);
        var sending = pieces.Where((_, i) => !safe.Withheld.Contains(i)).ToList();
        if (sending.Count == 0)
            return new Prepared(null, [], scripture, safe.Warning, scripture.Count > 0 ? "Every slide here is scripture, which is never machine-translated." : "There are no words on these slides to translate.");

        var ask = new StringBuilder();
        foreach (var piece in sending) ask.Append(CultureInfo.InvariantCulture, $"[{piece.Number}]\n{piece.Text}\n\n");
        return new Prepared(new AssistantRequest
        {
            Feature = "Translation", Instructions = Instructions(language), Ask = ask.ToString().TrimEnd(), Schema = Schema,
        }, sending, scripture, safe.Warning, null);
    }

    /// <summary>
    /// Makes the marked copy from the reply. Only numbers that were sent are used; a translation
    /// that is empty or absurdly long is dropped and the piece stays as it was.
    /// </summary>
    public static Translated Apply(Presentation deck, Prepared prepared, JsonElement reply, string language, DateTimeOffset now)
    {
        var answers = new Dictionary<int, string>();
        if (reply.ValueKind == JsonValueKind.Object && reply.TryGetProperty("translations", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("n", out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out var number)
                    && item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    answers.TryAdd(number, text.GetString()!.ReplaceLineEndings("\n").Trim());
            }
        }

        var copy = LibraryBatch.Duplicate(deck, now);
        copy.Name = $"{deck.Name} ({language} · AI translation: check before use)";
        copy.Folder = Folder;
        copy.FolderId = null;
        var done = 0;
        var missed = new List<int>();
        // The copy's slides and objects keep their order, so a piece is found by position.
        foreach (var piece in prepared.Pieces)
        {
            var slideIndex = deck.Slides.FindIndex(s => s.Id == piece.SlideId);
            var objectIndex = slideIndex < 0 ? -1 : deck.Slides[slideIndex].Objects.FindIndex(o => o.Id == piece.ObjectId);
            if (objectIndex < 0) continue;
            if (!answers.TryGetValue(piece.Number, out var words) || words.Length == 0 || words.Length > piece.Text.Length * 4 + 60)
            {
                missed.Add(slideIndex + 1);
                continue;
            }
            var slide = copy.Slides[slideIndex];
            var box = slide.Objects[objectIndex];
            box.Text = words;
            // Bold runs and chords were placed by letter position in the old words.
            box.StyleRuns = null;
            box.Chords = null;
            var original = $"AI translation ({language}): check before use. Original: {piece.Text.Replace('\n', ' ')}";
            slide.Notes = string.IsNullOrWhiteSpace(slide.Notes) ? original : $"{slide.Notes}\n{original}";
            done++;
        }

        var notes = new List<string>();
        if (prepared.ScriptureSlides.Count > 0)
            notes.Add($"Slide{(prepared.ScriptureSlides.Count == 1 ? "" : "s")} {string.Join(", ", prepared.ScriptureSlides)}: scripture, left as it is. Bible text is never machine-translated, and the built-in Bibles are English; type the verse from a published {language} Bible.");
        if (missed.Count > 0)
            notes.Add($"Not translated (left in the original): slide{(missed.Distinct().Count() == 1 ? "" : "s")} {string.Join(", ", missed.Distinct())}.");
        if (prepared.PrivacyWarning is { } warning) notes.Add(warning);
        return new Translated(copy, done, notes);
    }
}
