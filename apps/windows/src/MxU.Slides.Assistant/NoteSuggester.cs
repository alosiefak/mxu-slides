using System.Globalization;
using System.Text;
using System.Text.Json;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Notes;
using MxU.Slides.Core.Scripture;

namespace MxU.Slides.Assistant;

/// <summary>
/// "Suggest with AI" in Make Slides: the assistant reads a pastor's notes and says which lines
/// are worth a slide and what kind each is. It sits beside Pick for me
/// (<see cref="NoteSlideMarks.Suggesting"/>) and hands back exactly what that does: the note with
/// passages marked "suggested". Everything after that is the existing, deterministic pipeline
/// (<see cref="DeterministicSlidePlanner"/>, <see cref="NoteSlideBuilder"/>), so Make Slides
/// treats both alike and what's saved is an ordinary shared note and deck.
/// <para>
/// <b>What the assistant can't do, by construction.</b> Its answer is block numbers, a kind from
/// a fixed list, and a Bible reference; there is no field for words. Slide words come from the
/// note. Where the note gives only a reference ("John 3:16"), the verse is read from the church's
/// own Bible (<see cref="Apply"/>), in the translation the Bible window uses. If a reply carries
/// extra words anyway, nothing reads them.
/// </para>
/// </summary>
public static class NoteSuggester
{
    /// <summary>One suggested passage, checked: blocks in range, a real kind, and a reference our parser reads (or none).</summary>
    public sealed record Passage(IReadOnlyList<int> Blocks, string Kind, PassageReference? Reference);

    /// <summary>The job, ready to send, and what was held back from it.</summary>
    public sealed record Prepared(AssistantRequest Request, string? PrivacyWarning);

    /// <summary>The note marked as suggested, and what happened on the way.</summary>
    /// <param name="VersesFilled">Bare references whose verse was filled in from the Bible.</param>
    public sealed record Applied(NoteBody Body, int Passages, int VersesFilled, IReadOnlyList<string> Notes);

    /// <summary>A passage longer than this isn't pasted into the note (a whole chapter is for the Bible window).</summary>
    public const int MostVersesFilled = 12;

    /// <summary>
    /// The only kinds the assistant may name: the text slide types a note's mark can hold
    /// (<see cref="NoteSlideMarks.SlideType"/>), less lyrics, which come from the song library.
    /// The mark's type is a field of the shared note, so nothing outside this list may be written.
    /// </summary>
    public static readonly IReadOnlyList<string> Kinds =
        [.. NoteSlideMarks.TextKinds.Where(t => t != NoteSlideMarks.SlideType.Lyrics).Select(t => t.Raw())];

    public static string Schema => JsonSerializer.Serialize(new
    {
        type = "object",
        properties = new
        {
            passages = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        first_block = new { type = "integer" },
                        last_block = new { type = "integer" },
                        kind = new { type = "string", @enum = Kinds },
                        reference = new { type = new[] { "string", "null" } },
                    },
                    required = new[] { "first_block", "last_block", "kind", "reference" },
                    additionalProperties = false,
                },
            },
        },
        required = new[] { "passages" },
        additionalProperties = false,
    });

    private const string Instructions =
        """
        You help a church's slide operator turn a pastor's sermon notes into slides. The note is given as numbered blocks. Choose the blocks worth a slide and say what kind of slide each is. A person reviews every suggestion before anything is made.

        Kinds:
        - title: the sermon's title (at most one, with its subtitle line if it has one).
        - scripture: a Bible passage or a Bible reference.
        - points: the sermon's points. Consecutive point lines, or one list block, are ONE passage.
        - quote: a quotation with its author.
        - body: a statement, application or announcement the congregation should read.

        Leave out: run-sheet lines and stage directions ("Intro:", "Video:", "Transition", timings), the pastor's private asides and reminders, greetings, illustrations told aloud, blocks shown as "(private: not sent)", and blocks shown as "[marked by hand]" (the operator has already decided those).

        A passage is the consecutive blocks first_block to last_block (the same number twice for one block). Passages must not overlap, and must be in order.

        For a scripture passage give its reference in the usual form ("John 3:16-18", "Psalm 23", "1 Corinthians 13:4-7"); for every other kind give null. If the note only alludes to a passage, give the reference you are sure of, or null. You can check a reference with the look_up_passage tool.

        Never write verse text, and never rewrite the note's words: the app takes every slide's words from the note itself, and all Bible text from the church's own Bible.
        """;

    /// <summary>
    /// Builds the job for a note. Prayer-request lines are held back first
    /// (<see cref="PrayerPrivacy"/>), and blocks the operator marked by hand are shown as taken.
    /// </summary>
    /// <param name="read">Reads a passage from the church's Bible; null when the reference isn't in it.</param>
    public static Prepared Prepare(NoteBody body, Func<PassageReference, PassageText?> read)
    {
        var taken = Taken(body);
        var texts = body.Blocks.Select(b => NoteReading.Text(b).Trim()).ToList();
        var safe = PrayerPrivacy.Strip(texts, i => body.Blocks[i].Type == NoteBlockType.Heading);
        var ask = new StringBuilder("The note:\n");
        for (var i = 0; i < body.Blocks.Count; i++)
        {
            var shape = NoteSlideMarks.ShapeOf(body.Blocks[i]).ToString().ToLowerInvariant();
            var words = taken.Contains(i) ? "[marked by hand]" : safe.Lines[i].Replace("\n", " / ", StringComparison.Ordinal);
            if (words.Length > 0) ask.Append(CultureInfo.InvariantCulture, $"[{i}] ({shape}) {words}\n");
        }
        var tool = new AssistantTool(
            "look_up_passage", "Checks a Bible reference against the church's own Bible. Says whether it exists and how many verses it has; it does not return the text.",
            new Dictionary<string, JsonElement> { ["reference"] = JsonSerializer.SerializeToElement(new { type = "string", description = "For example: John 3:16-18" }) },
            ["reference"],
            input => LookUp(input["reference"].GetString() ?? "", read));
        var request = new AssistantRequest
        {
            Feature = "Make Slides", Instructions = Instructions, Ask = ask.ToString(), Schema = Schema, Tools = [tool],
        };
        return new Prepared(request, safe.Warning);
    }

    internal static string LookUp(string reference, Func<PassageReference, PassageText?> read)
    {
        if (PassageReference.Parse(reference) is not { } parsed) return $"“{reference}” doesn't read as a Bible reference.";
        var passage = read(parsed);
        if (passage is null || passage.IsEmpty) return $"{parsed.Display} isn't in the church's Bible. {passage?.Problem}".Trim();
        return $"{passage.Reference.Display} is in the church's Bible ({passage.Translation.Abbreviation}): {passage.Verses.Count} verse{(passage.Verses.Count == 1 ? "" : "s")}.";
    }

    /// <summary>
    /// Reads the reply, keeping only what is safe to use: block numbers inside the note, a kind
    /// from <see cref="Kinds"/>, no overlaps. Anything else in the reply (a "text" field a
    /// misbehaving model added) is never looked at.
    /// </summary>
    public static List<Passage> Read(JsonElement reply, int blockCount)
    {
        var passages = new List<Passage>();
        if (reply.ValueKind != JsonValueKind.Object || !reply.TryGetProperty("passages", out var list) || list.ValueKind != JsonValueKind.Array) return passages;
        var used = new HashSet<int>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("first_block", out var firstValue) || firstValue.ValueKind != JsonValueKind.Number || !firstValue.TryGetInt32(out var first)
                || !item.TryGetProperty("last_block", out var lastValue) || lastValue.ValueKind != JsonValueKind.Number || !lastValue.TryGetInt32(out var last)
                || !item.TryGetProperty("kind", out var kindValue) || kindValue.ValueKind != JsonValueKind.String) continue;
            var kind = kindValue.GetString()!;
            if (!Kinds.Contains(kind) || first < 0 || last < first || last >= blockCount || last - first > 40) continue;
            var blocks = Enumerable.Range(first, last - first + 1).ToList();
            if (blocks.Any(used.Contains)) continue;
            used.UnionWith(blocks);
            var reference = kind == "scripture" && item.TryGetProperty("reference", out var named) && named.ValueKind == JsonValueKind.String
                ? PassageReference.Parse(named.GetString()!) : null;
            passages.Add(new Passage(blocks, kind, reference));
        }
        return passages;
    }

    /// <summary>
    /// Marks the passages in the note as suggestions. A scripture passage that is only a
    /// reference gets its verse from <paramref name="read"/> (the church's Bible); a passage that
    /// already has words keeps the pastor's words. Blocks marked by hand are left alone.
    /// </summary>
    public static Applied Apply(NoteBody basis, IReadOnlyList<Passage> passages, Func<PassageReference, PassageText?> read)
    {
        var taken = Taken(basis);
        var marked = basis.Copy();
        var notes = new List<string>();
        var (count, filled) = (0, 0);
        foreach (var passage in passages)
        {
            var blocks = passage.Blocks.Where(i => i < marked.Blocks.Count && !taken.Contains(i) && NoteReading.Text(marked.Blocks[i]).Trim().Length > 0).ToList();
            if (blocks.Count == 0) continue;
            var group = NoteSlideMarks.MintGroupId();
            foreach (var index in blocks)
                marked.Blocks[index] = NoteSlideMarks.Marking(marked.Blocks[index], group, passage.Kind, NoteSlideMarks.Suggested);
            count++;
            if (passage.Kind != "scripture") continue;

            var words = string.Join("\n", blocks.Select(i => NoteReading.Text(basis.Blocks[i]).Trim()));
            if (!ScriptureReference.IsReferenceLine(words)) continue; // the pastor's own words stay as written
            // The note's own reference when our parser reads it; the assistant's only when it doesn't.
            var reference = PassageReference.Parse(words) ?? passage.Reference;
            if (reference is null) continue;
            var found = read(reference);
            if (found is null || found.IsEmpty)
            {
                notes.Add($"{reference.Display} isn't in the Bible on this PC, so its slide has only the reference.");
                continue;
            }
            if (found.Verses.Count > MostVersesFilled)
            {
                notes.Add($"{found.Reference.Display} is {found.Verses.Count} verses: too long to fill in. Use the Bible window for it.");
                continue;
            }
            marked = NoteSlideMarks.ReplacingText(group, VerseWords(found), marked);
            filled++;
        }
        return new Applied(marked, count, filled, notes);
    }

    /// <summary>
    /// A passage as Make Slides reads scripture: the verses (numbered when there are several, so
    /// "one verse a slide" can cut them), then the reference with its translation on the last line.
    /// </summary>
    public static string VerseWords(PassageText passage)
    {
        var verses = passage.Verses.Count == 1
            ? passage.Verses[0].Text
            : string.Join(" ", passage.Verses.Select(v => string.Create(CultureInfo.InvariantCulture, $"{v.Ref.Verse} {v.Text}")));
        return $"{verses}\n{passage.Title}";
    }

    // The blocks already in a passage someone marked by hand (a suggestion isn't "taken").
    private static HashSet<int> Taken(NoteBody body) =>
        [.. NoteSlideMarks.Groups(body).Where(g => g.State != NoteSlideMarks.Suggested).SelectMany(g => g.BlockIndexes)];
}
