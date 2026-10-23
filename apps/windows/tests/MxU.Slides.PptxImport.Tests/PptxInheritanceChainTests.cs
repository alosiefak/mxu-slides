using static MxU.Slides.PptxImport.Tests.TestPackages;

namespace MxU.Slides.PptxImport.Tests;

public sealed class PptxInheritanceChainTests : IDisposable
{
    private readonly TempFolder root = new("pptx-chain");

    public PptxInheritanceChainTests()
    {
        var theme = $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <a:theme {ANs} name="Test">
              <a:themeElements>
                <a:clrScheme name="Test">
                  <a:dk1><a:sysClr val="windowText" lastClr="000000"/></a:dk1>
                  <a:lt1><a:sysClr val="window" lastClr="FFFFFF"/></a:lt1>
                  <a:dk2><a:srgbClr val="1F3864"/></a:dk2>
                  <a:lt2><a:srgbClr val="E7E6E6"/></a:lt2>
                  <a:accent1><a:srgbClr val="0000FF"/></a:accent1>
                  <a:accent2><a:srgbClr val="ED7D31"/></a:accent2>
                  <a:accent3><a:srgbClr val="A5A5A5"/></a:accent3>
                  <a:accent4><a:srgbClr val="FFC000"/></a:accent4>
                  <a:accent5><a:srgbClr val="5B9BD5"/></a:accent5>
                  <a:accent6><a:srgbClr val="70AD47"/></a:accent6>
                  <a:hlink><a:srgbClr val="0563C1"/></a:hlink>
                  <a:folHlink><a:srgbClr val="954F72"/></a:folHlink>
                </a:clrScheme>
                <a:fontScheme name="Test">
                  <a:majorFont><a:latin typeface="Georgia"/></a:majorFont>
                  <a:minorFont><a:latin typeface="Verdana"/></a:minorFont>
                </a:fontScheme>
                <a:fmtScheme name="Test">
                  <a:fillStyleLst>
                    <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
                    <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
                    <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
                  </a:fillStyleLst>
                  <a:lnStyleLst/>
                  <a:effectStyleLst/>
                  <a:bgFillStyleLst>
                    <a:solidFill><a:schemeClr val="phClr"><a:shade val="50000"/></a:schemeClr></a:solidFill>
                    <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
                    <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
                  </a:bgFillStyleLst>
                </a:fmtScheme>
              </a:themeElements>
            </a:theme>
            """;

        var master = $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <p:sldMaster {PNs} {ANs} {RNs}>
              <p:cSld>
                <p:bg><p:bgPr><a:solidFill><a:schemeClr val="bg1"/></a:solidFill></p:bgPr></p:bg>
                <p:spTree>
                  <p:sp>
                    <p:nvSpPr><p:cNvPr id="2" name="Master Title"/><p:cNvSpPr/><p:nvPr><p:ph type="title"/></p:nvPr></p:nvSpPr>
                    <p:spPr><a:xfrm><a:off x="100000" y="200000"/><a:ext cx="3000000" cy="600000"/></a:xfrm></p:spPr>
                  </p:sp>
                  <p:sp>
                    <p:nvSpPr><p:cNvPr id="3" name="Master Body"/><p:cNvSpPr/><p:nvPr><p:ph type="body" idx="1"/></p:nvPr></p:nvSpPr>
                    <p:spPr><a:xfrm><a:off x="500000" y="2000000"/><a:ext cx="3000000" cy="1000000"/></a:xfrm></p:spPr>
                  </p:sp>
                  <p:pic>
                    <p:nvPicPr><p:cNvPr id="4" name="Logo"/><p:cNvPicPr/><p:nvPr/></p:nvPicPr>
                    <p:blipFill><a:blip r:embed="rId10"/></p:blipFill>
                    <p:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="457200" cy="457200"/></a:xfrm></p:spPr>
                  </p:pic>
                </p:spTree>
              </p:cSld>
              <p:clrMap bg1="lt1" tx1="dk1" bg2="lt2" tx2="dk2" accent1="accent1" accent2="accent2" accent3="accent3" accent4="accent4" accent5="accent5" accent6="accent6" hlink="hlink" folHlink="folHlink"/>
              <p:txStyles>
                <p:titleStyle>
                  <a:lvl1pPr algn="ctr"><a:defRPr sz="4400" b="1"><a:solidFill><a:schemeClr val="tx1"/></a:solidFill><a:latin typeface="+mj-lt"/></a:defRPr></a:lvl1pPr>
                </p:titleStyle>
                <p:bodyStyle>
                  <a:lvl1pPr><a:defRPr sz="2800"><a:latin typeface="+mn-lt"/></a:defRPr></a:lvl1pPr>
                  <a:lvl2pPr><a:defRPr sz="2400"><a:latin typeface="+mn-lt"/></a:defRPr></a:lvl2pPr>
                </p:bodyStyle>
                <p:otherStyle/>
              </p:txStyles>
            </p:sldMaster>
            """;

        var masterRels = $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships {RelsNs}>
              <Relationship Id="rId1" Type="{RelType}/theme" Target="../theme/theme1.xml"/>
              <Relationship Id="rId10" Type="{RelType}/image" Target="../media/logo.png"/>
            </Relationships>
            """;

        var layout1 = $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <p:sldLayout {PNs} {ANs}>
              <p:cSld>
                <p:spTree>
                  <p:sp>
                    <p:nvSpPr><p:cNvPr id="2" name="Layout Title"/><p:cNvSpPr/><p:nvPr><p:ph type="title"/></p:nvPr></p:nvSpPr>
                    <p:spPr><a:xfrm><a:off x="914400" y="304800"/><a:ext cx="10363200" cy="1325563"/></a:xfrm></p:spPr>
                    <p:txBody>
                      <a:bodyPr/>
                      <a:lstStyle><a:lvl1pPr><a:defRPr sz="3600"/></a:lvl1pPr></a:lstStyle>
                      <a:p/>
                    </p:txBody>
                  </p:sp>
                  <p:sp>
                    <p:nvSpPr><p:cNvPr id="3" name="Layout Body"/><p:cNvSpPr/><p:nvPr><p:ph type="body" idx="1"/></p:nvPr></p:nvSpPr>
                    <p:spPr><a:xfrm><a:off x="457200" y="1600200"/><a:ext cx="5486400" cy="4525963"/></a:xfrm></p:spPr>
                  </p:sp>
                  <p:sp>
                    <p:nvSpPr><p:cNvPr id="4" name="Accent Bar"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>
                    <p:spPr>
                      <a:xfrm><a:off x="0" y="6400800"/><a:ext cx="12192000" cy="457200"/></a:xfrm>
                      <a:solidFill><a:schemeClr val="accent1"/></a:solidFill>
                    </p:spPr>
                  </p:sp>
                </p:spTree>
              </p:cSld>
            </p:sldLayout>
            """;

        var layout2 = $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <p:sldLayout {PNs} {ANs} showMasterSp="0">
              <p:cSld>
                <p:spTree>
                  <p:sp>
                    <p:nvSpPr><p:cNvPr id="2" name="Layout2 Deco"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>
                    <p:spPr>
                      <a:xfrm><a:off x="0" y="0"/><a:ext cx="914400" cy="914400"/></a:xfrm>
                      <a:solidFill><a:srgbClr val="123456"/></a:solidFill>
                    </p:spPr>
                  </p:sp>
                </p:spTree>
              </p:cSld>
            </p:sldLayout>
            """;

        var layoutRels = $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships {RelsNs}>
              <Relationship Id="rId1" Type="{RelType}/slideMaster" Target="../slideMasters/slideMaster1.xml"/>
            </Relationships>
            """;

        var presentation = $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <p:presentation {PNs} {ANs}>
              <p:sldSz cx="12192000" cy="6858000"/>
              <p:defaultTextStyle>
                <a:lvl1pPr><a:defRPr sz="1400"/></a:lvl1pPr>
              </p:defaultTextStyle>
            </p:presentation>
            """;

        var slide1 = $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <p:sld {PNs} {ANs}>
              <p:cSld>
                <p:spTree>
                  <p:sp>
                    <p:nvSpPr><p:cNvPr id="2" name="Title"/><p:cNvSpPr/><p:nvPr><p:ph type="ctrTitle"/></p:nvPr></p:nvSpPr>
                    <p:spPr/>
                    <p:txBody><a:bodyPr/><a:p><a:r><a:t>Inherited Title</a:t></a:r></a:p></p:txBody>
                  </p:sp>
                  <p:sp>
                    <p:nvSpPr><p:cNvPr id="3" name="Body"/><p:cNvSpPr/><p:nvPr><p:ph type="body" idx="1"/></p:nvPr></p:nvSpPr>
                    <p:spPr/>
                    <p:txBody>
                      <a:bodyPr/>
                      <a:p><a:r><a:t>Level one</a:t></a:r></a:p>
                      <a:p><a:pPr lvl="1"/><a:r><a:t>Level two</a:t></a:r></a:p>
                    </p:txBody>
                  </p:sp>
                  <p:sp>
                    <p:nvSpPr><p:cNvPr id="4" name="Sub"/><p:cNvSpPr/><p:nvPr><p:ph type="subTitle"/></p:nvPr></p:nvSpPr>
                    <p:spPr/>
                    <p:txBody><a:bodyPr/><a:p><a:r><a:t>Sub</a:t></a:r></a:p></p:txBody>
                  </p:sp>
                  <p:sp>
                    <p:nvSpPr><p:cNvPr id="5" name="Plain Box"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>
                    <p:spPr><a:xfrm><a:off x="914400" y="914400"/><a:ext cx="1828800" cy="914400"/></a:xfrm></p:spPr>
                    <p:txBody><a:bodyPr/><a:p><a:r><a:t>Plain</a:t></a:r></a:p></p:txBody>
                  </p:sp>
                </p:spTree>
              </p:cSld>
            </p:sld>
            """;

        var slide2 = $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <p:sld {PNs} {ANs} showMasterSp="0">
              <p:cSld>
                <p:bg><p:bgRef idx="1001"><a:srgbClr val="FF0000"/></p:bgRef></p:bg>
                <p:spTree>
                  <p:sp>
                    <p:nvSpPr><p:cNvPr id="2" name="Own"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>
                    <p:spPr>
                      <a:xfrm><a:off x="0" y="0"/><a:ext cx="914400" cy="914400"/></a:xfrm>
                      <a:solidFill><a:srgbClr val="00FF00"/></a:solidFill>
                    </p:spPr>
                  </p:sp>
                </p:spTree>
              </p:cSld>
            </p:sld>
            """;

        var slide3 = $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <p:sld {PNs} {ANs}>
              <p:cSld>
                <p:spTree>
                  <p:sp>
                    <p:nvSpPr><p:cNvPr id="2" name="Own3"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>
                    <p:spPr>
                      <a:xfrm><a:off x="0" y="0"/><a:ext cx="914400" cy="914400"/></a:xfrm>
                      <a:solidFill><a:srgbClr val="00FF00"/></a:solidFill>
                    </p:spPr>
                  </p:sp>
                </p:spTree>
              </p:cSld>
            </p:sld>
            """;

        string SlideRels(string layout) => $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships {RelsNs}>
              <Relationship Id="rId1" Type="{RelType}/slideLayout" Target="../slideLayouts/{layout}"/>
            </Relationships>
            """;

        Stage(root.Path, new Dictionary<string, string>
        {
            ["ppt/presentation.xml"] = presentation,
            ["ppt/theme/theme1.xml"] = theme,
            ["ppt/slideMasters/slideMaster1.xml"] = master,
            ["ppt/slideMasters/_rels/slideMaster1.xml.rels"] = masterRels,
            ["ppt/slideLayouts/slideLayout1.xml"] = layout1,
            ["ppt/slideLayouts/slideLayout2.xml"] = layout2,
            ["ppt/slideLayouts/_rels/slideLayout1.xml.rels"] = layoutRels,
            ["ppt/slideLayouts/_rels/slideLayout2.xml.rels"] = layoutRels,
            ["ppt/slides/slide1.xml"] = slide1,
            ["ppt/slides/slide2.xml"] = slide2,
            ["ppt/slides/slide3.xml"] = slide3,
            ["ppt/slides/_rels/slide1.xml.rels"] = SlideRels("slideLayout1.xml"),
            ["ppt/slides/_rels/slide2.xml.rels"] = SlideRels("slideLayout1.xml"),
            ["ppt/slides/_rels/slide3.xml.rels"] = SlideRels("slideLayout2.xml"),
            ["ppt/media/logo.png"] = "png-bytes",
        });
    }

    public void Dispose() => root.Dispose();

    private PptxSlide Parse(string slidePart, List<string> warnings) =>
        PptxSlideParser.Parse(slidePart, new PptxPackage(root.Path), warnings, presentationPart: "ppt/presentation.xml");

    [Fact]
    public void PlaceholderGeometryAndTextCascadeResolve()
    {
        var warnings = new List<string>();
        var slide = Parse("ppt/slides/slide1.xml", warnings);

        Assert.Equal(6, slide.Shapes.Count);
        Assert.Equal("Logo", slide.Shapes[0].Name);
        Assert.Equal(PptxShapeKind.Picture, slide.Shapes[0].Kind);
        Assert.Equal(root.File("ppt/media/logo.png"), slide.Shapes[0].MediaPath);
        Assert.Equal("Accent Bar", slide.Shapes[1].Name);
        Assert.Equal(new PptxFill.Solid("0000FFFF"), slide.Shapes[1].Fill);

        var title = slide.Shapes[2];
        Assert.Equal(new PptxTransform(914_400, 304_800, 10_363_200, 1_325_563), title.Transform);
        var titleRun = Assert.IsType<PptxRun>(title.TextBody?.Paragraphs.FirstOrDefault()?.Runs.FirstOrDefault());
        Assert.Equal("Georgia", titleRun.FontFamily);
        Assert.Equal(3600, titleRun.SizeHundredthsPt);
        Assert.True(titleRun.Bold);
        Assert.Equal("000000FF", titleRun.ColorHex);
        Assert.Equal("ctr", title.TextBody?.Paragraphs.FirstOrDefault()?.Alignment);

        var body = slide.Shapes[3];
        Assert.Equal(new PptxTransform(457_200, 1_600_200, 5_486_400, 4_525_963), body.Transform);
        var levelOne = Assert.IsType<PptxRun>(body.TextBody?.Paragraphs[0].Runs.FirstOrDefault());
        Assert.Equal(2800, levelOne.SizeHundredthsPt);
        Assert.Equal("Verdana", levelOne.FontFamily);
        var levelTwo = Assert.IsType<PptxRun>(body.TextBody?.Paragraphs[1].Runs.FirstOrDefault());
        Assert.Equal(2400, levelTwo.SizeHundredthsPt);

        var sub = slide.Shapes[4];
        Assert.Equal(new PptxTransform(500_000, 2_000_000, 3_000_000, 1_000_000), sub.Transform);
        Assert.Equal(2800, sub.TextBody?.Paragraphs.FirstOrDefault()?.Runs.FirstOrDefault()?.SizeHundredthsPt);

        var plain = slide.Shapes[5];
        var plainRun = Assert.IsType<PptxRun>(plain.TextBody?.Paragraphs.FirstOrDefault()?.Runs.FirstOrDefault());
        Assert.Equal(1400, plainRun.SizeHundredthsPt);
        Assert.Equal("Verdana", plainRun.FontFamily);

        Assert.Equal(new PptxFill.Solid("FFFFFFFF"), slide.BackgroundFill);
        Assert.Empty(warnings);
    }

    [Fact]
    public void BgRefResolvesThemeStyleWithBoundPhClr()
    {
        var slide = Parse("ppt/slides/slide2.xml", []);
        Assert.Equal(new PptxFill.Solid("BC0000FF"), slide.BackgroundFill);
    }

    [Fact]
    public void SlideLevelShowMasterSpZeroHidesAllDecorations()
    {
        var slide = Parse("ppt/slides/slide2.xml", []);
        Assert.Equal("Own", Assert.Single(slide.Shapes).Name);
    }

    [Fact]
    public void LayoutLevelShowMasterSpZeroHidesOnlyMasterDecorations()
    {
        var slide = Parse("ppt/slides/slide3.xml", []);
        Assert.Equal(new[] { "Layout2 Deco", "Own3" }, slide.Shapes.Select(s => s.Name));
        Assert.Equal(new PptxFill.Solid("123456FF"), slide.Shapes[0].Fill);
    }
}
