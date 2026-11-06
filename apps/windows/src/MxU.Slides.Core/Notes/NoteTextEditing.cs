using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Notes;

/// <summary>
/// Editing a note as plain text on Windows. The Mac edits a note in a rich text view (bold,
/// lists, marks over any words); Windows edits it as lines, a line a block, and keeps every
/// block whose lines didn't change exactly as it was. A changed passage loses its mark, and the
/// note's live updates find it again by its words (NoteSlideMatch), as the Mac does for a note
/// pasted over itself.
/// </summary>
public static class NoteTextEditing
{
    /// <summary>The note's text for the plain editor: a line a block ("# " headings, "- " and "1. " list items, "> " quotes).</summary>
    public static string EditableText(NoteBody body) => string.Join("\n", body.Blocks.Select(EditableLines));

    private static string EditableLines(NoteBlock block) => block.Type switch
    {
        NoteBlockType.Heading => new string('#', Math.Clamp(block.Level ?? 1, 1, 3)) + " " + NoteReading.Text(block.Runs),
        NoteBlockType.List or NoteBlockType.TaskList => string.Join("\n", (block.Items ?? []).Select((item, i) =>
            (block.Ordered == true ? $"{i + 1}. " : "- ") + NoteReading.Text(item.Runs))),
        NoteBlockType.Quote => string.Join("\n", (block.Blocks ?? []).Select(b => "> " + NoteReading.Text(b))),
        NoteBlockType.Image => $"![{block.Alt}]({block.Url})",
        NoteBlockType.Attachment => $"[{block.Filename}]({block.Url ?? "hex:" + block.HexId})",
        NoteBlockType.SlideRef => "",
        NoteBlockType.Rule => "---",
        _ => NoteReading.Text(block),
    };

    /// <summary>
    /// The edited text back as the note's blocks. Every block whose lines are unchanged is kept
    /// exactly as it was (its bold, links and slide marks); only changed lines become plain
    /// paragraphs, headings and list items. The note's live updates then find a changed
    /// passage again by its words, as the Mac finds a pasted note's (NoteSlideMatch).
    /// </summary>
    public static NoteBody Edited(NoteBody original, string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var blocks = new List<NoteBlock>();
        var kept = original.Blocks.Where(b => b.Type != NoteBlockType.SlideRef)
            .Select(b => (Block: b, Lines: EditableLines(b).Split('\n'))).ToList();
        var at = 0;
        var next = 0;
        while (at < lines.Count)
        {
            // An original block whose lines come next, unchanged, stays itself.
            var match = -1;
            for (var k = next; k < kept.Count; k++)
            {
                var candidate = kept[k].Lines;
                if (at + candidate.Length <= lines.Count && candidate.SequenceEqual(lines.Skip(at).Take(candidate.Length)) && candidate.Any(l => l.Length > 0))
                {
                    match = k;
                    break;
                }
            }
            if (match >= 0)
            {
                blocks.Add(kept[match].Block.Copy());
                at += kept[match].Lines.Length;
                next = match + 1;
                continue;
            }
            blocks.Add(Typed(lines[at]));
            at++;
        }
        // Adjacent list items typed fresh join one list, as a note's lists are.
        var joined = new List<NoteBlock>();
        foreach (var block in blocks)
        {
            if (block.Type == NoteBlockType.List && joined.Count > 0 && joined[^1].Type == NoteBlockType.List && joined[^1].Ordered == block.Ordered
                && block.Items is { Count: 1 } && block.Items[0].Runs.All(r => r.SlideGroup is null)
                && joined[^1].Items!.All(i => i.Runs.All(r => r.SlideGroup is null)))
            {
                joined[^1].Items!.AddRange(block.Items);
                continue;
            }
            joined.Add(block);
        }
        // Hand-placed slide cards stay at the end of the note.
        joined.AddRange(original.Blocks.Where(b => b.Type == NoteBlockType.SlideRef && b.Slide?.PlacedByHand == true).Select(b => b.Copy()));
        return new NoteBody { Blocks = joined, PlainText = NoteReading.PlainText(joined) };
    }

    private static NoteBlock Typed(string line)
    {
        static List<NoteRun> Runs(string text) => text.Length == 0 ? [] : [new NoteRun { Text = text }];
        var hashes = line.TakeWhile(c => c == '#').Count();
        if (hashes is >= 1 and <= 3 && line.Length > hashes && line[hashes] == ' ')
            return new NoteBlock { Type = NoteBlockType.Heading, Level = hashes, Runs = Runs(line[(hashes + 1)..]) };
        if (line.StartsWith("> ", StringComparison.Ordinal))
            return new NoteBlock { Type = NoteBlockType.Quote, Blocks = [new NoteBlock { Type = NoteBlockType.Paragraph, Runs = Runs(line[2..]) }] };
        if (line == "---") return new NoteBlock { Type = NoteBlockType.Rule };
        if (NoteFileReader.Body(line).Blocks is [{ Type: NoteBlockType.List } list]) return list;
        return new NoteBlock { Type = NoteBlockType.Paragraph, Runs = Runs(line) };
    }
}
