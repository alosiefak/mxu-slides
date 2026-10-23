using System.Xml.Linq;

namespace MxU.Slides.PptxImport.Tests;

public class PptxColorResolverTests
{
    private readonly PptxColorResolver resolver = new(
        new Dictionary<string, string> { ["accent1"] = "0000FF", ["lt1"] = "FFFFFF", ["dk1"] = "000000" },
        new Dictionary<string, string> { ["bg1"] = "lt1", ["tx1"] = "dk1" });

    private string? Resolve(string inner, List<string> warnings, string? phClr = null)
    {
        var fill = XElement.Parse($"<fill {TestPackages.ANs}>{inner}</fill>");
        return resolver.Color(fill, warnings, phClr);
    }

    [Fact]
    public void PlainSrgbGetsOpaqueAlpha()
    {
        var warnings = new List<string>();
        Assert.Equal("1A2B3CFF", Resolve("<a:srgbClr val=\"1a2b3c\"/>", warnings));
        Assert.Empty(warnings);
    }

    [Fact]
    public void SchemeSlotResolvesThroughClrMap()
    {
        var warnings = new List<string>();
        Assert.Equal("FFFFFFFF", Resolve("<a:schemeClr val=\"bg1\"/>", warnings));
        Assert.Equal("0000FFFF", Resolve("<a:schemeClr val=\"accent1\"/>", warnings));
    }

    [Fact]
    public void LumModDarkensViaHsl()
    {
        var warnings = new List<string>();
        Assert.Equal("000099FF", Resolve("<a:schemeClr val=\"accent1\"><a:lumMod val=\"60000\"/></a:schemeClr>", warnings));
    }

    [Fact]
    public void SatModDesaturatesViaHsl()
    {
        var warnings = new List<string>();
        Assert.Equal("4040BFFF", Resolve("<a:schemeClr val=\"accent1\"><a:satMod val=\"50000\"/></a:schemeClr>", warnings));
    }

    [Fact]
    public void TintBlendsTowardWhite()
    {
        var warnings = new List<string>();
        Assert.Equal("CDCDCDFF", Resolve("<a:srgbClr val=\"808080\"><a:tint val=\"50000\"/></a:srgbClr>", warnings));
    }

    [Fact]
    public void ShadeScalesTowardBlack()
    {
        var warnings = new List<string>();
        Assert.Equal("BC0000FF", Resolve("<a:srgbClr val=\"FF0000\"><a:shade val=\"50000\"/></a:srgbClr>", warnings));
    }

    [Fact]
    public void AlphaLandsInTheAlphaByte()
    {
        var warnings = new List<string>();
        Assert.Equal("FF000080", Resolve("<a:srgbClr val=\"FF0000\"><a:alpha val=\"50000\"/></a:srgbClr>", warnings));
    }

    [Fact]
    public void PhClrBindsThePlaceholderColor()
    {
        var warnings = new List<string>();
        Assert.Equal("112233FF", Resolve("<a:schemeClr val=\"phClr\"/>", warnings, phClr: "112233"));

        Assert.Null(Resolve("<a:schemeClr val=\"phClr\"/>", warnings));
        Assert.Contains(warnings, w => w.Contains("phClr"));
    }

    [Fact]
    public void UnknownSlotWarnsOnceAndSkips()
    {
        var warnings = new List<string>();
        Assert.Null(Resolve("<a:schemeClr val=\"accent9\"/>", warnings));
        Assert.Null(Resolve("<a:schemeClr val=\"accent9\"/>", warnings));
        Assert.Single(warnings);
        Assert.Contains("accent9", warnings[0]);
    }

    [Fact]
    public void UnhandledTransformAppliesNothingAndWarns()
    {
        var warnings = new List<string>();
        Assert.Equal("FF0000FF", Resolve("<a:srgbClr val=\"FF0000\"><a:gamma/></a:srgbClr>", warnings));
        Assert.Contains(warnings, w => w.Contains("gamma"));
    }

    [Fact]
    public void PresetColorsResolveFromTheStandardTable()
    {
        var warnings = new List<string>();
        Assert.Equal("FF0000FF", Resolve("<a:prstClr val=\"red\"/>", warnings));
        Assert.Equal("00008BFF", Resolve("<a:prstClr val=\"dkBlue\"/>", warnings));
        Assert.Equal("B0C4DEFF", Resolve("<a:prstClr val=\"ltSteelBlue\"/>", warnings));
        Assert.Equal("000000FF", Resolve("<a:prstClr val=\"black\"/>", warnings));
        Assert.Equal("FFFFFFFF", Resolve("<a:prstClr val=\"white\"/>", warnings));

        Assert.Equal("00008BFF", Resolve("<a:prstClr val=\"darkBlue\"/>", warnings));
        Assert.Equal("D3D3D3FF", Resolve("<a:prstClr val=\"lightGrey\"/>", warnings));
        Assert.Equal("808080FF", Resolve("<a:prstClr val=\"grey\"/>", warnings));
        Assert.Empty(warnings);

        Assert.Equal("BC0000FF", Resolve("<a:prstClr val=\"red\"><a:shade val=\"50000\"/></a:prstClr>", warnings));

        Assert.Null(Resolve("<a:prstClr val=\"notAColor\"/>", warnings));
        Assert.Contains(warnings, w => w.Contains("notAColor"));
    }

    [Fact]
    public void SysClrUsesLastRenderedColor()
    {
        var warnings = new List<string>();
        Assert.Equal("0A0B0CFF", Resolve("<a:sysClr val=\"windowText\" lastClr=\"0A0B0C\"/>", warnings));
    }

    [Fact]
    public void TransformsApplyInDocumentOrder()
    {
        var warnings = new List<string>();
        Assert.Equal("E1BCBCFF", Resolve("<a:srgbClr val=\"FF0000\"><a:shade val=\"50000\"/><a:tint val=\"50000\"/></a:srgbClr>", warnings));
    }
}
