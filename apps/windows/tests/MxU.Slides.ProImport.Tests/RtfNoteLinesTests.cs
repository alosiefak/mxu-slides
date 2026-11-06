using System.Text;
using MxU.Slides.Core.Model;
using MxU.Slides.ProImport.Rtf;

namespace MxU.Slides.ProImport.Tests;

/// <summary>Notes read from RTF (the rtf case of the Mac's NoteFileReaderTests.richTextFilesKeepBoldAndUnderline, and more).</summary>
public class RtfNoteLinesTests
{
    private static NoteBody Read(string rtf) => RtfNoteLines.Body(Encoding.UTF8.GetBytes(rtf))!;

    [Fact]
    public void RichTextKeepsBoldAndUnderline()
    {
        // As TextEdit writes it: the bold comes from \b and from the font's own name.
        var body = Read(@"{\rtf1\ansi{\fonttbl\f0\fswiss Helvetica;\f1\fswiss Helvetica-Bold;}\pard\f1\b\fs24 Big idea \f0\b0\ul trust\ulnone\par}");
        Assert.Equal([new NoteRun { Text = "Big idea ", Bold = true }, new NoteRun { Text = "trust", Underline = true }], body.Blocks[0].Runs!);
    }

    [Fact]
    public void WordsRtfHeadingsListsAndItalicsAreRead()
    {
        var body = Read(@"{\rtf1\ansi{\fonttbl{\f0 Calibri;}}"
            + @"\pard\fs48\b Unshakable\b0\par"
            + @"\pard\fs24 When {\i everything} shakes\par"
            + @"{\listtext\'95\tab}God is our refuge\par"
            + @"{\listtext\'95\tab}God is our strength\par}");
        Assert.Equal([NoteBlockType.Heading, NoteBlockType.Paragraph, NoteBlockType.List], body.Blocks.Select(b => b.Type));
        Assert.Contains(body.Blocks[1].Runs!, r => r.Text.Trim() == "everything" && r.Italic == true);
        Assert.Equal(["God is our refuge", "God is our strength"], body.Blocks[2].Items!.Select(i => string.Concat(i.Runs.Select(r => r.Text))));
    }

    [Fact]
    public void ProPresentersTextStillReadsWithoutWeights()
    {
        var text = AttributedText.FromRtf(Encoding.UTF8.GetBytes(@"{\rtf1\ansi{\fonttbl\f0 Helvetica;}\pard\f0\b Bold\par}"))!;
        Assert.False(text.Runs[0].Attributes.Bold); // ProPresenter's importer reads weight from font names, as the Mac does
    }
}
