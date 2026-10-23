namespace MxU.Slides.PptxImport.Tests;

public class PptxSlideParserTests
{
    private const string SlideXml = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
               xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
               xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
          <p:cSld>
            <p:bg><p:bgPr><a:solidFill><a:srgbClr val="101010"/></a:solidFill></p:bgPr></p:bg>
            <p:spTree>
              <p:sp>
                <p:nvSpPr><p:cNvPr id="2" name="Title Box"/><p:cNvSpPr/><p:nvPr><p:ph type="title" idx="4"/></p:nvPr></p:nvSpPr>
                <p:spPr>
                  <a:xfrm rot="5400000" flipH="1"><a:off x="914400" y="457200"/><a:ext cx="1828800" cy="914400"/></a:xfrm>
                  <a:prstGeom prst="roundRect"><a:avLst><a:gd name="adj" fmla="val 25000"/></a:avLst></a:prstGeom>
                  <a:solidFill><a:srgbClr val="FF0000"/></a:solidFill>
                  <a:ln w="25400"><a:solidFill><a:srgbClr val="00FF00"/></a:solidFill></a:ln>
                </p:spPr>
                <p:txBody>
                  <a:bodyPr anchor="ctr" tIns="91440"><a:normAutofit fontScale="62500"/></a:bodyPr>
                  <a:p>
                    <a:pPr algn="ctr"/>
                    <a:r>
                      <a:rPr lang="en-US" sz="4000" b="1" u="sng" spc="200">
                        <a:solidFill><a:srgbClr val="FFFFFF"/></a:solidFill>
                        <a:latin typeface="Helvetica"/>
                      </a:rPr>
                      <a:t>Hello</a:t>
                    </a:r>
                    <a:br/>
                    <a:r><a:rPr sz="2000" strike="sngStrike"/><a:t>World</a:t></a:r>
                  </a:p>
                </p:txBody>
              </p:sp>
              <p:sp>
                <p:nvSpPr><p:cNvPr id="4" name="Floating"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>
                <p:spPr/>
                <p:txBody><a:bodyPr/><a:p><a:r><a:t>No frame</a:t></a:r></a:p></p:txBody>
              </p:sp>
              <p:pic>
                <p:nvPicPr><p:cNvPr id="3" name="Picture 1"/><p:cNvPicPr/><p:nvPr/></p:nvPicPr>
                <p:blipFill>
                  <a:blip r:embed="rId2"/>
                  <a:srcRect l="25000" r="25000"/>
                  <a:stretch><a:fillRect/></a:stretch>
                </p:blipFill>
                <p:spPr>
                  <a:xfrm><a:off x="0" y="0"/><a:ext cx="914400" cy="914400"/></a:xfrm>
                  <a:prstGeom prst="rect"/>
                </p:spPr>
              </p:pic>
              <p:graphicFrame>
                <p:nvGraphicFramePr><p:cNvPr id="7" name="Chart 1"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>
                <p:xfrm><a:off x="0" y="0"/><a:ext cx="914400" cy="914400"/></p:xfrm>
                <a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/chart">
                  <c:chart xmlns:c="http://schemas.openxmlformats.org/drawingml/2006/chart" r:id="rId9"/>
                </a:graphicData></a:graphic>
              </p:graphicFrame>
            </p:spTree>
          </p:cSld>
          <mc:AlternateContent xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006">
            <mc:Choice xmlns:p14="http://schemas.microsoft.com/office/powerpoint/2010/main" Requires="p14">
              <p:transition p14:dur="700" advTm="3000"><p:push dir="l"/></p:transition>
            </mc:Choice>
            <mc:Fallback>
              <p:transition spd="fast" advTm="3000"><p:push dir="l"/></p:transition>
            </mc:Fallback>
          </mc:AlternateContent>
          <p:timing>
            <p:tnLst><p:par><p:cTn id="1" dur="indefinite" restart="never" nodeType="tmRoot"><p:childTnLst>
              <p:seq concurrent="1" nextAc="seek"><p:cTn id="2" dur="indefinite" nodeType="mainSeq"><p:childTnLst>
                <p:par><p:cTn id="3" fill="hold"><p:stCondLst><p:cond delay="indefinite"/></p:stCondLst><p:childTnLst>
                  <p:par><p:cTn id="4" fill="hold"><p:stCondLst><p:cond delay="0"/></p:stCondLst><p:childTnLst>
                    <p:par><p:cTn id="5" presetID="2" presetClass="entr" presetSubtype="8" fill="hold" nodeType="clickEffect">
                      <p:stCondLst><p:cond delay="0"/></p:stCondLst>
                      <p:childTnLst>
                        <p:set><p:cBhvr><p:cTn id="6" dur="1" fill="hold"/><p:tgtEl><p:spTgt spid="2"><p:txEl><p:pRg st="0" end="0"/></p:txEl></p:spTgt></p:tgtEl></p:cBhvr></p:set>
                        <p:anim calcmode="lin" valueType="num"><p:cBhvr><p:cTn id="7" dur="750" fill="hold"/><p:tgtEl><p:spTgt spid="2"><p:txEl><p:pRg st="0" end="0"/></p:txEl></p:spTgt></p:tgtEl></p:cBhvr></p:anim>
                      </p:childTnLst>
                    </p:cTn></p:par>
                    <p:par><p:cTn id="8" presetID="10" presetClass="entr" fill="hold" nodeType="withEffect">
                      <p:stCondLst><p:cond delay="250"/></p:stCondLst>
                      <p:childTnLst>
                        <p:set><p:cBhvr><p:cTn id="9" dur="1" fill="hold"/><p:tgtEl><p:spTgt spid="3"/></p:tgtEl></p:cBhvr></p:set>
                        <p:animEffect transition="in" filter="fade"><p:cBhvr><p:cTn id="10" dur="500"/><p:tgtEl><p:spTgt spid="3"/></p:tgtEl></p:cBhvr></p:animEffect>
                      </p:childTnLst>
                    </p:cTn></p:par>
                  </p:childTnLst></p:cTn></p:par>
                </p:childTnLst></p:cTn></p:par>
              </p:childTnLst></p:cTn></p:seq>
            </p:childTnLst></p:cTn></p:par></p:tnLst>
          </p:timing>
        </p:sld>
        """;

    private const string RelsXml = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="../media/image1.png"/>
          <Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/notesSlide" Target="../notesSlides/notesSlide1.xml"/>
        </Relationships>
        """;

    private const string NotesXml = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <p:notes xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                 xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
          <p:cSld>
            <p:spTree>
              <p:sp>
                <p:nvSpPr><p:cNvPr id="2" name="Notes Placeholder"/><p:cNvSpPr/><p:nvPr><p:ph type="body" idx="1"/></p:nvPr></p:nvSpPr>
                <p:spPr/>
                <p:txBody><a:bodyPr/><a:p><a:r><a:t>Speaker note</a:t></a:r></a:p></p:txBody>
              </p:sp>
            </p:spTree>
          </p:cSld>
        </p:notes>
        """;

    [Fact]
    public void ParsesShapesPicturesTextAndTransition()
    {
        using var temp = new TempFolder("pptx-parser");
        TestPackages.Stage(temp.Path, new Dictionary<string, string>
        {
            ["ppt/slides/slide1.xml"] = SlideXml,
            ["ppt/slides/_rels/slide1.xml.rels"] = RelsXml,
            ["ppt/notesSlides/notesSlide1.xml"] = NotesXml,
        });
        var warnings = new List<string>();
        var slide = PptxSlideParser.Parse("ppt/slides/slide1.xml", new PptxPackage(temp.Path), warnings);

        Assert.Equal(new PptxFill.Solid("101010FF"), slide.BackgroundFill);
        Assert.Equal(2, slide.Shapes.Count);

        var shape = slide.Shapes[0];
        Assert.Equal(PptxShapeKind.Shape, shape.Kind);
        Assert.Equal("Title Box", shape.Name);
        Assert.Equal(new PptxTransform(914_400, 457_200, 1_828_800, 914_400, Rotation60k: 5_400_000, FlipH: true, FlipV: false), shape.Transform);
        Assert.Equal("roundRect", shape.PresetGeometry);
        Assert.Equal(25_000, shape.RoundRectAdjustment);
        Assert.Equal(new PptxFill.Solid("FF0000FF"), shape.Fill);
        Assert.Equal(new PptxStroke("00FF00FF", 25_400), shape.Stroke);
        Assert.Equal("title", shape.PlaceholderType);
        Assert.Equal(4, shape.PlaceholderIndex);

        var body = Assert.IsType<PptxTextBody>(shape.TextBody);
        Assert.Equal("ctr", body.Anchor);
        Assert.Equal(91_440, body.TopInsetEmu);
        Assert.Null(body.LeftInsetEmu);
        Assert.True(body.HasNormAutofit);
        Assert.Equal(62_500, body.AutofitFontScale);
        Assert.Single(body.Paragraphs);
        Assert.Equal("ctr", body.Paragraphs[0].Alignment);
        var runs = body.Paragraphs[0].Runs;
        Assert.Equal(3, runs.Count);
        Assert.Equal(new PptxRun
        {
            Text = "Hello", FontFamily = "Helvetica", SizeHundredthsPt = 4000, Bold = true,
            Underline = true, ColorHex = "FFFFFFFF", TrackingHundredthsPt = 200,
        }, runs[0]);
        Assert.True(runs[1].IsBreak);
        Assert.Equal(new PptxRun { Text = "World", SizeHundredthsPt = 2000, Strike = true }, runs[2]);
        Assert.Equal("Hello\nWorld", body.PlainText);

        var picture = slide.Shapes[1];
        Assert.Equal(PptxShapeKind.Picture, picture.Kind);
        Assert.Equal("Picture 1", picture.Name);
        Assert.Equal("rId2", picture.MediaRelationshipId);
        Assert.Equal(temp.File("ppt/media/image1.png"), picture.MediaPath);
        Assert.Equal(new PptxSourceRect(L: 25_000, T: 0, R: 25_000, B: 0), picture.SourceRect);

        var transition = Assert.IsType<PptxTransition>(slide.Transition);
        Assert.Equal("push", transition.Kind);
        Assert.Equal(0.7, transition.DurationSeconds);
        Assert.Equal(3000, transition.AdvanceAfterMs);

        Assert.Equal("Speaker note", slide.Notes);

        Assert.True(shape.ShapeId == 2 && picture.ShapeId == 3);
        Assert.Equal(2, slide.AnimationSteps.Count);
        Assert.Equal(new PptxAnimation(
            2, PresetClass: "entr", PresetId: 2, PresetSubtype: 8, NodeType: "clickEffect",
            DelayMs: 0, DurationMs: 750, ParagraphStart: 0, ParagraphEnd: 0), slide.AnimationSteps[0]);
        Assert.Equal(new PptxAnimation(
            3, PresetClass: "entr", PresetId: 10, PresetSubtype: null, NodeType: "withEffect",
            DelayMs: 250, DurationMs: 500, ParagraphStart: null, ParagraphEnd: null), slide.AnimationSteps[1]);

        Assert.Contains(warnings, w => w.Contains("Floating") && w.Contains("dropped"));
        Assert.Contains(warnings, w => w.Contains("chart \"Chart 1\" was skipped"));
    }

    [Fact]
    public void ParsesLinearGradientAndApproximatesRadial()
    {
        var warnings = new List<string>();
        var slide = ParseStagedSlide("""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                   xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
              <p:cSld>
                <p:spTree>
                  <p:sp>
                    <p:nvSpPr><p:cNvPr id="2" name="Linear"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>
                    <p:spPr>
                      <a:xfrm><a:off x="0" y="0"/><a:ext cx="914400" cy="914400"/></a:xfrm>
                      <a:gradFill>
                        <a:gsLst>
                          <a:gs pos="100000"><a:srgbClr val="0000FF"/></a:gs>
                          <a:gs pos="0"><a:srgbClr val="FF0000"/></a:gs>
                        </a:gsLst>
                        <a:lin ang="2700000" scaled="1"/>
                      </a:gradFill>
                    </p:spPr>
                  </p:sp>
                  <p:sp>
                    <p:nvSpPr><p:cNvPr id="3" name="Radial"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>
                    <p:spPr>
                      <a:xfrm><a:off x="0" y="0"/><a:ext cx="914400" cy="914400"/></a:xfrm>
                      <a:gradFill>
                        <a:gsLst>
                          <a:gs pos="0"><a:srgbClr val="FF0000"/></a:gs>
                          <a:gs pos="100000"><a:srgbClr val="0000FF"/></a:gs>
                        </a:gsLst>
                        <a:path path="circle"><a:fillToRect l="50000" t="50000" r="50000" b="50000"/></a:path>
                      </a:gradFill>
                    </p:spPr>
                  </p:sp>
                </p:spTree>
              </p:cSld>
            </p:sld>
            """, warnings);

        List<PptxGradientStop> expectedStops = [new(0, "FF0000FF"), new(100_000, "0000FFFF")];
        Assert.Equal(new PptxFill.Gradient(new PptxGradient(2_700_000, expectedStops)), slide.Shapes[0].Fill);
        Assert.Equal(new PptxFill.Gradient(new PptxGradient(null, expectedStops)), slide.Shapes[1].Fill);
        Assert.Contains("radial gradient approximated as linear", warnings);
    }

    private static PptxSlide ParseStagedSlide(string slideXml, List<string> warnings, Dictionary<string, string>? extras = null)
    {
        using var temp = new TempFolder("pptx-staged");
        var parts = new Dictionary<string, string>(extras ?? new Dictionary<string, string>()) { ["ppt/slides/slide1.xml"] = slideXml };
        TestPackages.Stage(temp.Path, parts);
        return PptxSlideParser.Parse("ppt/slides/slide1.xml", new PptxPackage(temp.Path), warnings);
    }

    private static string SlideXmlWith(string spTree) => $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
               xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
               xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
          <p:cSld><p:spTree>{spTree}</p:spTree></p:cSld>
        </p:sld>
        """;

    [Fact]
    public void CustomGeometryPromotesQuadsAndNormalizes()
    {
        var warnings = new List<string>();
        var slide = ParseStagedSlide(SlideXmlWith("""
            <p:sp>
              <p:nvSpPr><p:cNvPr id="2" name="Custom"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>
              <p:spPr>
                <a:xfrm><a:off x="0" y="0"/><a:ext cx="914400" cy="914400"/></a:xfrm>
                <a:custGeom>
                  <a:avLst/><a:gdLst/><a:rect l="0" t="0" r="0" b="0"/>
                  <a:pathLst>
                    <a:path w="200" h="100">
                      <a:moveTo><a:pt x="0" y="0"/></a:moveTo>
                      <a:lnTo><a:pt x="200" y="0"/></a:lnTo>
                      <a:quadBezTo><a:pt x="200" y="100"/><a:pt x="100" y="100"/></a:quadBezTo>
                      <a:close/>
                    </a:path>
                  </a:pathLst>
                </a:custGeom>
                <a:solidFill><a:srgbClr val="FF0000"/></a:solidFill>
              </p:spPr>
            </p:sp>
            """), warnings);

        Assert.Equal(
            "M 0.0000 0.0000 C 0.0000 0.0000 1.0000 0.0000 1.0000 0.0000 C 1.0000 0.6667 0.8333 1.0000 0.5000 1.0000 C 0.5000 1.0000 0.0000 0.0000 0.0000 0.0000 Z",
            slide.Shapes[0].CustomPathData);
        Assert.Empty(warnings);
    }

    [Fact]
    public void CustomGeometryArcBecomesChordWithWarning()
    {
        var warnings = new List<string>();
        var slide = ParseStagedSlide(SlideXmlWith("""
            <p:sp>
              <p:nvSpPr><p:cNvPr id="2" name="Arc"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>
              <p:spPr>
                <a:xfrm><a:off x="0" y="0"/><a:ext cx="914400" cy="914400"/></a:xfrm>
                <a:custGeom><a:pathLst>
                  <a:path w="100" h="100">
                    <a:moveTo><a:pt x="100" y="50"/></a:moveTo>
                    <a:arcTo wR="50" hR="50" stAng="0" swAng="5400000"/>
                  </a:path>
                </a:pathLst></a:custGeom>
                <a:solidFill><a:srgbClr val="FF0000"/></a:solidFill>
              </p:spPr>
            </p:sp>
            """), warnings);

        Assert.Equal("M 1.0000 0.5000 C 1.0000 0.5000 0.5000 1.0000 0.5000 1.0000", slide.Shapes[0].CustomPathData);
        Assert.Contains(warnings, w => w.Contains("arc") && w.Contains("straight"));
    }

    [Fact]
    public void FormulaDrivenCustomGeometrySkipsWithWarning()
    {
        var warnings = new List<string>();
        var slide = ParseStagedSlide(SlideXmlWith("""
            <p:sp>
              <p:nvSpPr><p:cNvPr id="2" name="Guided"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>
              <p:spPr>
                <a:xfrm><a:off x="0" y="0"/><a:ext cx="914400" cy="914400"/></a:xfrm>
                <a:custGeom><a:pathLst>
                  <a:path w="100" h="100">
                    <a:moveTo><a:pt x="adj1" y="0"/></a:moveTo>
                    <a:lnTo><a:pt x="100" y="100"/></a:lnTo>
                  </a:path>
                </a:pathLst></a:custGeom>
                <a:solidFill><a:srgbClr val="FF0000"/></a:solidFill>
              </p:spPr>
            </p:sp>
            """), warnings);
        Assert.Null(slide.Shapes[0].CustomPathData);
        Assert.Contains(warnings, w => w.Contains("formula-driven"));
    }

    [Fact]
    public void GrpFillResolvesThroughNestedGroups()
    {
        var warnings = new List<string>();
        var slide = ParseStagedSlide(SlideXmlWith("""
            <p:grpSp>
              <p:nvGrpSpPr><p:cNvPr id="2" name="Outer"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>
              <p:grpSpPr>
                <a:xfrm>
                  <a:off x="0" y="0"/><a:ext cx="1000000" cy="1000000"/>
                  <a:chOff x="0" y="0"/><a:chExt cx="1000000" cy="1000000"/>
                </a:xfrm>
                <a:solidFill><a:srgbClr val="FFFFFF"/></a:solidFill>
              </p:grpSpPr>
              <p:grpSp>
                <p:nvGrpSpPr><p:cNvPr id="3" name="Inner"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>
                <p:grpSpPr><a:xfrm>
                  <a:off x="0" y="0"/><a:ext cx="1000000" cy="1000000"/>
                  <a:chOff x="0" y="0"/><a:chExt cx="1000000" cy="1000000"/>
                </a:xfrm></p:grpSpPr>
                <p:sp>
                  <p:nvSpPr><p:cNvPr id="4" name="Paper"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>
                  <p:spPr>
                    <a:xfrm><a:off x="0" y="0"/><a:ext cx="200000" cy="200000"/></a:xfrm>
                    <a:grpFill/>
                  </p:spPr>
                </p:sp>
              </p:grpSp>
            </p:grpSp>
            """), warnings);
        Assert.Single(slide.Shapes);
        Assert.Equal(new PptxFill.Solid("FFFFFFFF"), slide.Shapes[0].Fill);
        Assert.False(slide.Shapes[0].UsesGroupFill);
    }

    [Fact]
    public void GroupsFlattenWithScaleTranslateAndFlip()
    {
        var warnings = new List<string>();
        var slide = ParseStagedSlide(SlideXmlWith("""
            <p:grpSp>
              <p:nvGrpSpPr><p:cNvPr id="2" name="Group"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>
              <p:grpSpPr><a:xfrm>
                <a:off x="1000000" y="1000000"/><a:ext cx="2000000" cy="2000000"/>
                <a:chOff x="0" y="0"/><a:chExt cx="1000000" cy="1000000"/>
              </a:xfrm></p:grpSpPr>
              <p:sp>
                <p:nvSpPr><p:cNvPr id="3" name="Scaled"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>
                <p:spPr>
                  <a:xfrm><a:off x="100000" y="100000"/><a:ext cx="200000" cy="200000"/></a:xfrm>
                  <a:solidFill><a:srgbClr val="FF0000"/></a:solidFill>
                </p:spPr>
              </p:sp>
            </p:grpSp>
            <p:grpSp>
              <p:nvGrpSpPr><p:cNvPr id="4" name="Flipped"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>
              <p:grpSpPr><a:xfrm flipH="1">
                <a:off x="0" y="0"/><a:ext cx="1000000" cy="1000000"/>
                <a:chOff x="0" y="0"/><a:chExt cx="1000000" cy="1000000"/>
              </a:xfrm></p:grpSpPr>
              <p:sp>
                <p:nvSpPr><p:cNvPr id="5" name="Mirrored"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>
                <p:spPr>
                  <a:xfrm><a:off x="0" y="0"/><a:ext cx="200000" cy="200000"/></a:xfrm>
                  <a:solidFill><a:srgbClr val="00FF00"/></a:solidFill>
                </p:spPr>
              </p:sp>
            </p:grpSp>
            """), warnings);

        Assert.Equal(new PptxTransform(1_200_000, 1_200_000, 400_000, 400_000), slide.Shapes[0].Transform);
        Assert.Equal(new PptxTransform(800_000, 0, 200_000, 200_000, FlipH: true), slide.Shapes[1].Transform);
        Assert.Empty(warnings);
    }

    [Fact]
    public void GroupRotationSpinsChildCentersAndAddsAngles()
    {
        var warnings = new List<string>();
        var slide = ParseStagedSlide(SlideXmlWith("""
            <p:grpSp>
              <p:nvGrpSpPr><p:cNvPr id="2" name="Turned"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>
              <p:grpSpPr><a:xfrm rot="5400000">
                <a:off x="1000000" y="1000000"/><a:ext cx="2000000" cy="2000000"/>
                <a:chOff x="0" y="0"/><a:chExt cx="2000000" cy="2000000"/>
              </a:xfrm></p:grpSpPr>
              <p:sp>
                <p:nvSpPr><p:cNvPr id="3" name="Child"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>
                <p:spPr>
                  <a:xfrm rot="600000"><a:off x="0" y="0"/><a:ext cx="400000" cy="400000"/></a:xfrm>
                  <a:solidFill><a:srgbClr val="FF0000"/></a:solidFill>
                </p:spPr>
              </p:sp>
            </p:grpSp>
            """), warnings);

        var child = slide.Shapes[0].Transform;
        Assert.True(Math.Abs(child.OffXEmu - 2_600_000) < 1);
        Assert.True(Math.Abs(child.OffYEmu - 1_000_000) < 1);
        Assert.Equal(6_000_000, child.Rotation60k);
    }

    [Fact]
    public void ConnectorParsesAsLineShape()
    {
        var warnings = new List<string>();
        var slide = ParseStagedSlide(SlideXmlWith("""
            <p:cxnSp>
              <p:nvCxnSpPr><p:cNvPr id="2" name="Straight Arrow"/><p:cNvCxnSpPr/><p:nvPr/></p:nvCxnSpPr>
              <p:spPr>
                <a:xfrm flipV="1"><a:off x="0" y="0"/><a:ext cx="914400" cy="457200"/></a:xfrm>
                <a:prstGeom prst="line"><a:avLst/></a:prstGeom>
                <a:ln w="19050"><a:solidFill><a:srgbClr val="000000"/></a:solidFill></a:ln>
              </p:spPr>
            </p:cxnSp>
            """), warnings);
        var connector = slide.Shapes[0];
        Assert.Equal("Straight Arrow", connector.Name);
        Assert.Equal("line", connector.PresetGeometry);
        Assert.True(connector.Transform.FlipV);
        Assert.Equal(new PptxStroke("000000FF", 19_050), connector.Stroke);
    }

    [Fact]
    public void TableFlattensToTabSeparatedText()
    {
        var warnings = new List<string>();
        var slide = ParseStagedSlide(SlideXmlWith("""
            <p:graphicFrame>
              <p:nvGraphicFramePr><p:cNvPr id="2" name="Table 1"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>
              <p:xfrm><a:off x="914400" y="914400"/><a:ext cx="1828800" cy="914400"/></p:xfrm>
              <a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/table">
                <a:tbl>
                  <a:tblGrid><a:gridCol w="914400"/><a:gridCol w="914400"/></a:tblGrid>
                  <a:tr h="457200">
                    <a:tc><a:txBody><a:bodyPr/><a:p><a:r><a:rPr sz="1800"/><a:t>A</a:t></a:r></a:p></a:txBody></a:tc>
                    <a:tc><a:txBody><a:bodyPr/><a:p><a:r><a:t>B</a:t></a:r></a:p></a:txBody></a:tc>
                  </a:tr>
                  <a:tr h="457200">
                    <a:tc><a:txBody><a:bodyPr/><a:p><a:r><a:t>C</a:t></a:r></a:p></a:txBody></a:tc>
                    <a:tc><a:txBody><a:bodyPr/><a:p><a:r><a:t>D</a:t></a:r></a:p></a:txBody></a:tc>
                  </a:tr>
                </a:tbl>
              </a:graphicData></a:graphic>
            </p:graphicFrame>
            """), warnings);
        var table = slide.Shapes[0];
        Assert.Equal("Table 1", table.Name);
        Assert.Equal("A\tB\nC\tD", table.TextBody?.PlainText);

        Assert.All(table.TextBody!.Paragraphs, p => Assert.Equal(1800d, p.Runs.FirstOrDefault()?.SizeHundredthsPt));
        Assert.Contains(warnings, w => w.Contains("tabs"));
    }

    [Fact]
    public void VideoAndAudioPicturesResolveTheirMedia()
    {
        var warnings = new List<string>();
        const string rels = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/video" Target="../media/media1.mp4"/>
              <Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="../media/poster1.png"/>
              <Relationship Id="rId4" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/audio" Target="file:///Volumes/SFX/bell%20tone.wav" TargetMode="External"/>
              <Relationship Id="rId5" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/audio" Target="C:\SFX\organ.wav" TargetMode="External"/>
            </Relationships>
            """;
        var slide = ParseStagedSlide(SlideXmlWith("""
            <p:pic>
              <p:nvPicPr><p:cNvPr id="2" name="Clip"/><p:cNvPicPr/><p:nvPr><a:videoFile r:link="rId2"/></p:nvPr></p:nvPicPr>
              <p:blipFill><a:blip r:embed="rId3"/></p:blipFill>
              <p:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="914400" cy="914400"/></a:xfrm></p:spPr>
            </p:pic>
            <p:pic>
              <p:nvPicPr><p:cNvPr id="3" name="Bell"/><p:cNvPicPr/><p:nvPr><a:audioFile r:link="rId4"/></p:nvPr></p:nvPicPr>
              <p:blipFill><a:blip r:embed="rId3"/></p:blipFill>
              <p:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="457200" cy="457200"/></a:xfrm></p:spPr>
            </p:pic>
            <p:pic>
              <p:nvPicPr><p:cNvPr id="4" name="Organ"/><p:cNvPicPr/><p:nvPr><a:audioFile r:link="rId5"/></p:nvPr></p:nvPicPr>
              <p:blipFill><a:blip r:embed="rId3"/></p:blipFill>
              <p:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="457200" cy="457200"/></a:xfrm></p:spPr>
            </p:pic>
            """), warnings, new() { ["ppt/slides/_rels/slide1.xml.rels"] = rels });
        var video = slide.Shapes[0];
        Assert.Equal(PptxMediaKind.Video, video.MediaKind);
        Assert.EndsWith(@"ppt\media\media1.mp4", video.MediaPath);
        var audio = slide.Shapes[1];
        Assert.Equal(PptxMediaKind.Audio, audio.MediaKind);

        Assert.Equal("/Volumes/SFX/bell tone.wav", audio.MediaPath);
        Assert.Equal(@"C:\SFX\organ.wav", slide.Shapes[2].MediaPath);
        Assert.Empty(warnings);
    }

    [Fact]
    public void PicturesParseTheirClippingGeometry()
    {
        var warnings = new List<string>();
        var slide = ParseStagedSlide(SlideXmlWith("""
            <p:pic>
              <p:nvPicPr><p:cNvPr id="2" name="Torn"/><p:cNvPicPr/><p:nvPr/></p:nvPicPr>
              <p:blipFill><a:blip r:embed="rId9"/></p:blipFill>
              <p:spPr>
                <a:xfrm><a:off x="0" y="0"/><a:ext cx="914400" cy="914400"/></a:xfrm>
                <a:custGeom><a:pathLst>
                  <a:path w="100" h="100">
                    <a:moveTo><a:pt x="0" y="0"/></a:moveTo>
                    <a:lnTo><a:pt x="100" y="0"/></a:lnTo>
                    <a:lnTo><a:pt x="50" y="100"/></a:lnTo>
                    <a:close/>
                  </a:path>
                </a:pathLst></a:custGeom>
              </p:spPr>
            </p:pic>
            <p:pic>
              <p:nvPicPr><p:cNvPr id="3" name="Round"/><p:cNvPicPr/><p:nvPr/></p:nvPicPr>
              <p:blipFill><a:blip r:embed="rId9"/></p:blipFill>
              <p:spPr>
                <a:xfrm><a:off x="0" y="0"/><a:ext cx="914400" cy="914400"/></a:xfrm>
                <a:prstGeom prst="ellipse"><a:avLst/></a:prstGeom>
              </p:spPr>
            </p:pic>
            """), warnings);
        Assert.StartsWith("M 0.0000 0.0000", slide.Shapes[0].CustomPathData);
        Assert.EndsWith("Z", slide.Shapes[0].CustomPathData);
        Assert.Equal("ellipse", slide.Shapes[1].PresetGeometry);
    }

    [Fact]
    public void DynamicFieldsImportAsStaticTextWithWarning()
    {
        var warnings = new List<string>();
        var slide = ParseStagedSlide(SlideXmlWith("""
            <p:sp>
              <p:nvSpPr><p:cNvPr id="2" name="Slide Number"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>
              <p:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="914400" cy="457200"/></a:xfrm></p:spPr>
              <p:txBody>
                <a:bodyPr/>
                <a:p>
                  <a:r><a:t>Page </a:t></a:r>
                  <a:fld id="{11111111-2222-3333-4444-555555555555}" type="slidenum"><a:rPr b="1"/><a:t>7</a:t></a:fld>
                </a:p>
              </p:txBody>
            </p:sp>
            """), warnings);
        Assert.Equal("Page 7", slide.Shapes[0].TextBody?.PlainText);
        Assert.True(slide.Shapes[0].TextBody?.Paragraphs[0].Runs[1].Bold);
        Assert.Contains(warnings, w => w.Contains("static text"));
    }

    [Fact]
    public void WhitespaceOnlyRunsKeepTheirSpace()
    {
        var warnings = new List<string>();
        var slide = ParseStagedSlide(SlideXmlWith("""
            <p:sp>
              <p:nvSpPr><p:cNvPr id="2" name="Spaced"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>
              <p:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="914400" cy="457200"/></a:xfrm></p:spPr>
              <p:txBody><a:bodyPr/><a:p><a:r><a:rPr b="1"/><a:t>Grace</a:t></a:r><a:r><a:t> </a:t></a:r><a:r><a:t>alone</a:t></a:r></a:p></p:txBody>
            </p:sp>
            """), warnings);
        Assert.Equal("Grace alone", slide.Shapes[0].TextBody?.PlainText);
    }

    [Fact]
    public void SmartArtImportsThePreLaidOutDrawing()
    {
        const string dgmNs = "xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\"";
        const string dspNs = "xmlns:dsp=\"http://schemas.microsoft.com/office/drawing/2008/diagram\"";
        var extras = new Dictionary<string, string>
        {
            ["ppt/slides/_rels/slide1.xml.rels"] = """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId5" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/diagramData" Target="../diagrams/data1.xml"/>
                </Relationships>
                """,
            ["ppt/diagrams/data1.xml"] = $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <dgm:dataModel {dgmNs} {TestPackages.ANs} {TestPackages.RNs}>
                  <dgm:ptLst/>
                  <dgm:extLst><a:ext uri="http://schemas.microsoft.com/office/drawing/2008/diagram">
                    <dsp:dataModelExt {dspNs} relId="rId1" minVer="http://schemas.openxmlformats.org/drawingml/2006/diagram"/>
                  </a:ext></dgm:extLst>
                </dgm:dataModel>
                """,
            ["ppt/diagrams/_rels/data1.xml.rels"] = """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.microsoft.com/office/2007/relationships/diagramDrawing" Target="drawing1.xml"/>
                </Relationships>
                """,
            ["ppt/diagrams/drawing1.xml"] = $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <dsp:drawing {dspNs} {TestPackages.ANs}>
                  <dsp:spTree>
                    <dsp:sp modelId="{"{1}"}">
                      <dsp:spPr>
                        <a:xfrm><a:off x="100000" y="200000"/><a:ext cx="500000" cy="300000"/></a:xfrm>
                        <a:prstGeom prst="roundRect"><a:avLst/></a:prstGeom>
                        <a:solidFill><a:srgbClr val="336699"/></a:solidFill>
                      </dsp:spPr>
                      <dsp:txBody><a:bodyPr/><a:p><a:r><a:t>Step 1</a:t></a:r></a:p></dsp:txBody>
                    </dsp:sp>
                  </dsp:spTree>
                </dsp:drawing>
                """,
        };
        var warnings = new List<string>();
        var slide = ParseStagedSlide(SlideXmlWith($"""
            <p:graphicFrame>
              <p:nvGraphicFramePr><p:cNvPr id="2" name="Diagram 1"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>
              <p:xfrm><a:off x="1000000" y="500000"/><a:ext cx="4000000" cy="3000000"/></p:xfrm>
              <a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/diagram">
                <dgm:relIds {dgmNs} r:dm="rId5" r:lo="rId5" r:qs="rId5" r:cs="rId5"/>
              </a:graphicData></a:graphic>
            </p:graphicFrame>
            """), warnings, extras);
        var step = Assert.Single(slide.Shapes);

        Assert.Equal(1_100_000, step.Transform.OffXEmu);
        Assert.Equal(700_000, step.Transform.OffYEmu);
        Assert.Equal("roundRect", step.PresetGeometry);
        Assert.Equal(new PptxFill.Solid("336699FF"), step.Fill);
        Assert.Equal("Step 1", step.TextBody?.PlainText);
        Assert.Empty(warnings);
    }

    [Fact]
    public void SectionListResolvesSlideMembership()
    {
        using var temp = new TempFolder("pptx-sections");
        TestPackages.Stage(temp.Path, new Dictionary<string, string>
        {
            ["_rels/.rels"] = """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="ppt/presentation.xml"/>
                </Relationships>
                """,
            ["ppt/presentation.xml"] = """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <p:presentation xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                                xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <p:sldIdLst>
                    <p:sldId id="256" r:id="rId2"/>
                    <p:sldId id="257" r:id="rId3"/>
                  </p:sldIdLst>
                  <p:sldSz cx="12192000" cy="6858000"/>
                  <p:extLst>
                    <p:ext uri="{521415D9-36F7-43E2-AB2F-B90AF26B5E84}">
                      <p14:sectionLst xmlns:p14="http://schemas.microsoft.com/office/powerpoint/2010/main">
                        <p14:section name="Opening" id="{AB12CD34-0000-1111-2222-333344445555}">
                          <p14:sldIdLst><p14:sldId id="256"/></p14:sldIdLst>
                        </p14:section>
                        <p14:section name="Message" id="{FF12CD34-0000-1111-2222-333344445555}">
                          <p14:sldIdLst><p14:sldId id="257"/></p14:sldIdLst>
                        </p14:section>
                      </p14:sectionLst>
                    </p:ext>
                  </p:extLst>
                </p:presentation>
                """,
            ["ppt/_rels/presentation.xml.rels"] = """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide" Target="slides/slide1.xml"/>
                  <Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide" Target="slides/slide2.xml"/>
                </Relationships>
                """,
        });
        var warnings = new List<string>();
        var result = PptxPresentationParser.Parse(new PptxPackage(temp.Path), warnings);
        Assert.Equal(
            new[]
            {
                new PptxSection("ab12cd34-0000-1111-2222-333344445555", "Opening") { SlideIndexes = [0] },
                new PptxSection("ff12cd34-0000-1111-2222-333344445555", "Message") { SlideIndexes = [1] },
            },
            result.Sections);
        Assert.Equal(new[] { "ppt/slides/slide1.xml", "ppt/slides/slide2.xml" }, result.SlidePartPaths);
    }

    [Fact]
    public void SmartArtWithoutDrawingPartSkipsWithWarning()
    {
        const string dgmNs = "xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\"";
        var warnings = new List<string>();
        var slide = ParseStagedSlide(SlideXmlWith($"""
            <p:graphicFrame>
              <p:nvGraphicFramePr><p:cNvPr id="2" name="Diagram 2"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>
              <p:xfrm><a:off x="0" y="0"/><a:ext cx="914400" cy="914400"/></p:xfrm>
              <a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/diagram">
                <dgm:relIds {dgmNs} r:dm="rId9" r:lo="rId9" r:qs="rId9" r:cs="rId9"/>
              </a:graphicData></a:graphic>
            </p:graphicFrame>
            """), warnings);
        Assert.Empty(slide.Shapes);
        Assert.Contains(warnings, w => w.Contains("Diagram 2") && w.Contains("skipped"));
    }
}
