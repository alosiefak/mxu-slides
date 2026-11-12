using System.Globalization;
using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Notes;

/// <summary>
/// Reading a note: the words of a block, its outline of headings, finding words in it, and
/// keeping the reader's place when the note changes underneath. Port of NoteReading.swift.
/// </summary>
public static class NoteReading
{
    /// <summary>
    /// Where a reader's place (a block index) lands after the note changes from
    /// <paramref name="old"/> to <paramref name="new"/>: blocks before the change keep their
    /// index, blocks after it move by however many were added or removed.
    /// </summary>
    public static int Block(int anchor, IReadOnlyList<NoteBlock> old, IReadOnlyList<NoteBlock> @new)
    {
        var prefix = 0;
        while (prefix < old.Count && prefix < @new.Count && old[prefix].Equals(@new[prefix])) prefix++;
        if (anchor < prefix) return anchor;
        var room = Math.Min(old.Count, @new.Count) - prefix;
        var same = 0;
        while (same < old.Count && same < @new.Count && old[old.Count - 1 - same].Equals(@new[@new.Count - 1 - same])) same++;
        var suffix = Math.Min(room, same);
        var moved = anchor >= old.Count - suffix ? anchor + @new.Count - old.Count : anchor;
        return Math.Max(0, Math.Min(moved, @new.Count - 1));
    }

    public sealed record OutlineEntry(int BlockIndex, int Level, string Title);

    /// <param name="Offset">Where the match starts in the block's words (UTF-16).</param>
    public sealed record Match(int BlockIndex, int Offset);

    /// <summary>The note's headings with words, as a table of contents.</summary>
    public static List<OutlineEntry> Outline(NoteBody body) =>
        body.Blocks.Select((block, index) => (block, index))
            .Where(b => b.block.Type == NoteBlockType.Heading)
            .Select(b => (b.index, b.block.Level ?? 1, Title: Text(b.block).TrimmingWhitespacesAndNewlines()))
            .Where(b => b.Title.Length > 0)
            .Select(b => new OutlineEntry(b.index, b.Item2, b.Title))
            .ToList();

    public static string Text(IEnumerable<NoteRun>? runs) => string.Concat((runs ?? []).Select(r => r.Text));

    /// <summary>A block's words, lines joined with "\n": a list's items, a quote's paragraphs, a picture's alt text.</summary>
    public static string Text(NoteBlock block) => block.Type switch
    {
        NoteBlockType.Heading or NoteBlockType.Paragraph => Text(block.Runs),
        NoteBlockType.List or NoteBlockType.TaskList => string.Join("\n", (block.Items ?? []).Select(item =>
            string.Join("\n", new[] { Text(item.Runs) }.Concat((item.Blocks ?? []).Select(Text))))),
        NoteBlockType.Quote => string.Join("\n", (block.Blocks ?? []).Select(Text)),
        NoteBlockType.Code or NoteBlockType.Unknown => block.Text ?? "",
        NoteBlockType.Image => block.Alt ?? "",
        NoteBlockType.Attachment => block.Filename ?? "",
        NoteBlockType.SlideRef => string.Join("\n", new[] { block.Slide?.PresentationName, block.Slide?.SlideText }.OfType<string>()),
        _ => "",
    };

    /// <summary>The note's searchable words: every block with words, one per line (NoteBody.plainText).</summary>
    public static string PlainText(IEnumerable<NoteBlock> blocks) =>
        string.Join("\n", blocks.Select(Text).Where(t => t.Length > 0));

    /// <summary>Every place <paramref name="query"/> appears, ignoring case and accents ("grace" finds "Gráce").</summary>
    public static List<Match> Matches(NoteBody body, string query)
    {
        var needle = query.TrimmingWhitespacesAndNewlines();
        if (needle.Length == 0) return [];
        var compare = CultureInfo.InvariantCulture.CompareInfo;
        const CompareOptions options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        var found = new List<Match>();
        for (var index = 0; index < body.Blocks.Count; index++)
        {
            var haystack = Text(body.Blocks[index]);
            var from = 0;
            while (from <= haystack.Length)
            {
                var at = compare.IndexOf(haystack.AsSpan(from), needle, options, out var length);
                if (at < 0) break;
                at += from;
                found.Add(new Match(index, at));
                from = at + Math.Max(1, length);
            }
        }
        return found;
    }
}

/// <summary>Notes made on this computer: new, or pasted in. Port of LocalNotes.swift.</summary>
public static class LocalNotes
{
    /// <summary>A note's origin when it was made here (not brought in from another notes app).</summary>
    public const string Origin = "local";

    public static NoteDocument Note(string name, NoteBody? body, string? id = null) => new()
    {
        Id = id ?? "note-" + Lyrics.SwiftString.Uuid(),
        Name = name,
        Origin = Origin,
        Body = body ?? new NoteBody { Blocks = [], PlainText = "" },
    };

    /// <summary>A pasted note's name: its first line with words, cut to 60 characters.</summary>
    public static string PastedName(string text)
    {
        var first = text.ComponentsSeparatedByNewlines().Select(l => l.TrimmingWhitespaces()).FirstOrDefault(l => l.Length > 0);
        if (first is null) return "Untitled Note";
        return first.Length > 60 ? first[..59] + "…" : first;
    }
}
