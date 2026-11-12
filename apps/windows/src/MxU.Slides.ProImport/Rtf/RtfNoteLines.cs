using MxU.Slides.Core.Model;
using MxU.Slides.Core.Notes;

namespace MxU.Slides.ProImport.Rtf;

/// <summary>
/// An RTF file (WordPad's, Word's, TextEdit's) as note lines: a line per paragraph, with bold,
/// italic, underline and strike, and the text's size for headings. Uses the RTF reader the
/// ProPresenter importer already has, with bold and italic switched on. Windows' stand-in for
/// the Mac's NSAttributedString(url:) on RTF. Links in RTF fields keep their words, not the link.
/// </summary>
public static class RtfNoteLines
{
    public static NoteBody? Body(ReadOnlySpan<byte> data) => Lines(data) is { } lines ? NoteFileReader.Body(lines) : null;

    public static List<NoteFileReader.Line>? Lines(ReadOnlySpan<byte> data)
    {
        if (AttributedText.FromRtf(data, weights: true) is not { } text) return null;
        var lines = new List<NoteFileReader.Line>();
        var start = 0;
        while (start <= text.Length)
        {
            var end = text.Text.IndexOf('\n', start);
            if (end < 0) end = text.Length;
            lines.Add(Line(text, start, end));
            start = end + 1;
        }
        // The text ends with its last paragraph's "\n": no empty line after it.
        if (lines.Count > 0 && lines[^1].Gap) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static NoteFileReader.Line Line(AttributedText text, int start, int end)
    {
        var runs = new List<NoteRun>();
        var sizes = new Dictionary<double, int>();
        foreach (var run in text.Runs.Where(r => r.End > start && r.Start < end))
        {
            var from = Math.Max(start, run.Start);
            var to = Math.Min(end, run.End);
            var words = text.Text[from..to];
            var a = run.Attributes;
            var font = a.FontName.ToLowerInvariant();
            var noteRun = new NoteRun { Text = words };
            if (a.Bold || font.Contains("bold") || font.Contains("black") || font.Contains("heavy")) noteRun.Bold = true;
            if (a.Italic || font.Contains("italic") || font.Contains("oblique")) noteRun.Italic = true;
            if (a.Underline != 0) noteRun.Underline = true;
            if (a.Strikethrough != 0) noteRun.Strike = true;
            runs.Add(noteRun);
            sizes[a.FontSize] = sizes.GetValueOrDefault(a.FontSize) + words.Count(c => !char.IsWhiteSpace(c));
        }
        var line = new NoteFileReader.Line(runs, sizes.Count == 0 ? 0 : sizes.MaxBy(p => p.Value).Key);
        if (runs.All(r => r.Text.Trim().Length == 0)) return line with { Gap = true };
        return NoteFileReader.ReadingList(line, null);
    }
}
