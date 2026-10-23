using System.Text;
using MxU.Slides.ProImport.Rtf;

namespace MxU.Slides.ProImport.Tests;

/// <summary>The Windows stand-in for NSAttributedString(rtf:) on the RTF ProPresenter actually writes.</summary>
public class RtfReaderTests
{
    private static AttributedText Read(string rtf) => AttributedText.FromRtf(Encoding.UTF8.GetBytes(rtf))!;

    private const string ProPresenterRtf =
        "{\\rtf1\\ansi\\ansicpg1252\\cocoartf2639\n"
        + "\\cocoatextscaling0\\cocoaplatform0{\\fonttbl\\f0\\fnil\\fcharset0 HelveticaNeue;}\n"
        + "{\\colortbl;\\red255\\green255\\blue255;\\red255\\green255\\blue255;}\n"
        + "{\\*\\expandedcolortbl;;\\csgenericrgb\\c100000\\c100000\\c100000;}\n"
        + "\\deftab1680\n"
        + "\\pard\\pardeftab1680\\pardirnatural\\qc\\partightenfactor0\n\n"
        + "\\f0\\fs120 \\cf2 \\kerning1\\expnd0\\expndtw0 Amazing grace\\\n"
        + "how sweet the sound}";

    [Fact]
    public void ReadsProPresentersCocoaRtf()
    {
        var text = Read(ProPresenterRtf);
        Assert.Equal("Amazing grace\nhow sweet the sound", text.Text);
        var attributes = text.AttributesAt(0);
        Assert.Equal("HelveticaNeue", attributes.FontName);
        Assert.Equal(60, attributes.FontSize);
        Assert.Equal("#FFFFFFFF", attributes.Foreground?.Hex);
        Assert.Equal(0, attributes.Kern);
        Assert.Single(text.Runs);
    }

    [Fact]
    public void ColourTablesConvertLikeCocoa()
    {
        var text = Read("{\\rtf1\\ansi{\\colortbl;\\red255\\green0\\blue0;\\red0\\green0\\blue255;}{\\*\\expandedcolortbl;;\\cssrgb\\c0\\c0\\c100000;}"
            + "\\cf1 a\\cf2 b\\cf0 c}");
        Assert.Equal("#FF2600FF", text.AttributesAt(0).Foreground?.Hex);
        Assert.Equal("#0000FFFF", text.AttributesAt(1).Foreground?.Hex);
        Assert.Null(text.AttributesAt(2).Foreground);
    }

    [Fact]
    public void DecodesEscapesUnicodeAndCodePages()
    {
        var text = Read("{\\rtf1\\ansi\\ansicpg1252 caf\\'e9 \\u8364 ? \\{x\\} a\\tab b\\line c\\~d\\uc0\\u8212 e}");
        Assert.Equal("caf\u00E9 \u20AC {x} a\tb" + (char)0x2028 + "c\u00A0d\u2014e", text.Text);
    }

    [Fact]
    public void ParagraphIndentsAndRunAttributesCarry()
    {
        var text = Read("{\\rtf1\\ansi{\\fonttbl\\f0 Helvetica;}\\f0\\fs48 \\li400\\fi-200\\ri100 one {\\ul\\strike two}\\expndtw40  three\\par\\pard four}");
        Assert.Equal("one two three\nfour", text.Text);
        var first = text.AttributesAt(0).Paragraph;
        Assert.Equal(new RtfParagraphStyle(10, 20, -5), first);
        Assert.Equal(RtfParagraphStyle.Default, text.AttributesAt(text.Text.Length - 1).Paragraph);
        Assert.Equal(1, text.AttributesAt(4).Underline);
        Assert.Equal(1, text.AttributesAt(4).Strikethrough);
        Assert.Equal(0, text.AttributesAt(0).Underline);
        Assert.Equal(2, text.AttributesAt(9).Kern);
    }

    [Fact]
    public void RejectsWhatIsNotRtf()
    {
        Assert.Null(AttributedText.FromRtf([]));
        Assert.Null(AttributedText.FromRtf(Encoding.UTF8.GetBytes("plain text")));
        Assert.Equal("", Read("{\\rtf1\\ansi }").Text);
    }
}
