using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MxU.Slides.Assistant;

/// <summary>
/// "Clean up with AI" in the song editor: lyrics pasted from a website (run together, unlabelled,
/// with the site's own clutter) come back sorted into Verse 1, Chorus, Bridge… with sensible line
/// breaks and the order the song is sung in. The result is plain labelled text, which the
/// existing lyric pipeline (<c>Core/Lyrics/Reflow</c>) turns into slides as it always has.
/// <para>
/// <b>The words never change.</b> The assistant may label, re-break lines and leave clutter out,
/// but it may not add, alter or reorder a single word: for accuracy, and because lyrics are
/// licensed (CCLI). That isn't left to trust. <see cref="Read"/> cuts both texts into words and
/// checks that the answer's words are the pasted words, in the pasted order, with nothing new.
/// A reply that fails is thrown away whole and the editor is left as it was.
/// </para>
/// </summary>
public static class LyricsCleanup
{
    /// <summary>The cleaned lyrics, ready for the song editor's box.</summary>
    /// <param name="Text">Labelled sections in sung order; a repeat is just its label again.</param>
    /// <param name="LeftOut">Pasted lines that aren't in the result (the site's clutter), for the operator to check.</param>
    public sealed record Cleaned(string Text, IReadOnlyList<string> LeftOut, int Sections);

    /// <summary>
    /// The section names the assistant may use: all ones the lyric pipeline already reads as
    /// labels (a test checks each against <c>Reflow.LabelName</c>).
    /// </summary>
    public static readonly IReadOnlyList<string> Labels =
        ["Verse", "Chorus", "Pre-Chorus", "Bridge", "Tag", "Intro", "Outro", "Ending", "Interlude", "Refrain"];

    public static string Schema => JsonSerializer.Serialize(new
    {
        type = "object",
        properties = new
        {
            sections = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        label = new { type = "string", @enum = Labels },
                        number = new { type = new[] { "integer", "null" } },
                        lines = new { type = "array", items = new { type = "string" } },
                    },
                    required = new[] { "label", "number", "lines" },
                    additionalProperties = false,
                },
            },
            order = new { type = "array", items = new { type = "integer" } },
        },
        required = new[] { "sections", "order" },
        additionalProperties = false,
    });

    private const string Instructions =
        """
        You tidy song lyrics that a church's slide operator pasted from a website, so they can be made into slides. A person checks your result before it is used.

        Give each DIFFERENT section once, in the order it first appears in the pasted text: its label, its number (Verse 1, Verse 2; null for sections that aren't numbered), and its lines. Then give "order": the sections as sung, by their position in your list (0 for the first). A chorus sung three times appears once in "sections" and three times in "order".

        Line breaks: one sung phrase per line, about 20 to 40 letters. You may join or split the pasted lines to get there.

        Leave out everything that is not sung words: the song's title and credits, website clutter ("Lyrics provided by", adverts, "Embed", "Share"), bare section labels, and marks like "x2" or "(repeat)" (show repeats in "order" instead). Keep chords written in square brackets, like [G], exactly where they are.

        The one rule that matters most: COPY every word exactly as pasted, character for character, with its capitals, punctuation and spelling, and keep the words in the pasted order. Never correct a spelling, never complete a line from memory, never add a word. Your answer is checked word by word against the pasted text, and thrown away if a single word differs.
        """;

    public static AssistantRequest Prepare(string pasted) => new()
    {
        Feature = "Lyrics clean-up", Instructions = Instructions, Ask = "The pasted text:\n" + pasted.ReplaceLineEndings("\n"), Schema = Schema,
    };

    /// <summary>
    /// Builds the labelled text from a reply, after checking its words against what was pasted.
    /// </summary>
    /// <exception cref="AssistantException">The reply added, changed or moved a word, or holds no lyrics.</exception>
    public static Cleaned Read(JsonElement reply, string pasted)
    {
        var sections = new List<(string Name, List<string> Lines)>();
        if (reply.ValueKind == JsonValueKind.Object && reply.TryGetProperty("sections", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("label", out var labelValue) || labelValue.ValueKind != JsonValueKind.String || labelValue.GetString() is not { } label || !Labels.Contains(label)
                    || !item.TryGetProperty("lines", out var lineValues) || lineValues.ValueKind != JsonValueKind.Array)
                    throw Rejected("its sections weren't in the shape asked for");
                var number = item.TryGetProperty("number", out var numberValue) && numberValue.ValueKind == JsonValueKind.Number && numberValue.TryGetInt32(out var n) && n is > 0 and < 100 ? n : (int?)null;
                var lines = lineValues.EnumerateArray().Select(l => l.ValueKind == JsonValueKind.String ? l.GetString()!.Trim() : "").Where(l => l.Length > 0).ToList();
                if (lines.Count == 0) continue;
                sections.Add((number is { } value ? string.Create(CultureInfo.InvariantCulture, $"{label} {value}") : label, lines));
            }
        }
        if (sections.Count == 0) throw Rejected("it held no lyrics");

        // The check: every word of the answer, in order, must be found in the pasted text, in order.
        var pastedWords = Words(pasted);
        var at = 0;
        foreach (var word in sections.SelectMany(s => s.Lines).SelectMany(Words))
        {
            while (at < pastedWords.Count && pastedWords[at] != word) at++;
            if (at == pastedWords.Count) throw Rejected($"it changed, added or moved the word “{word}”");
            at++;
        }

        var order = reply.TryGetProperty("order", out var orderValue) && orderValue.ValueKind == JsonValueKind.Array
            ? orderValue.EnumerateArray().Where(o => o.ValueKind == JsonValueKind.Number && o.TryGetInt32(out var i) && i >= 0 && i < sections.Count).Select(o => o.GetInt32()).ToList()
            : [];
        // An order that misses a section would lose its words: sing them in written order instead.
        if (order.Count == 0 || Enumerable.Range(0, sections.Count).Any(i => !order.Contains(i))) order = [.. Enumerable.Range(0, sections.Count)];

        var text = new StringBuilder();
        var written = new HashSet<int>();
        foreach (var index in order)
        {
            if (text.Length > 0) text.Append("\n\n");
            text.Append(sections[index].Name);
            // The first time a section is sung its words follow; after that its label alone repeats it.
            if (written.Add(index)) text.Append('\n').AppendJoin('\n', sections[index].Lines);
        }

        var kept = sections.SelectMany(s => s.Lines).Select(Squeezed).ToHashSet(StringComparer.Ordinal);
        var keptWords = sections.SelectMany(s => s.Lines).SelectMany(Words).ToHashSet(StringComparer.Ordinal);
        var leftOut = pasted.ReplaceLineEndings("\n").Split('\n').Select(l => l.Trim())
            .Where(l => l.Length > 0 && !kept.Contains(Squeezed(l)) && !Words(l).All(keptWords.Contains))
            .Distinct().ToList();
        return new Cleaned(text.ToString(), leftOut, sections.Count);
    }

    /// <summary>A text as its words: cut at spaces and line ends, nothing else changed (capitals and punctuation count).</summary>
    public static List<string> Words(string text) => [.. text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)];

    private static string Squeezed(string line) => string.Join(' ', Words(line));

    private static AssistantException Rejected(string why) =>
        new(AssistantProblem.BadReply, $"The assistant's answer was thrown away because {why}. Your lyrics are unchanged.");
}
