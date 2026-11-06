using MxU.Slides.Core.Model;

namespace MxU.Slides.PptxImport.Tests;

/// <summary>Port of the Mac's PPTXNoteTextTests.swift.</summary>
public class PptxNoteTextTests
{
    [Fact]
    public void ADeckReadsAsNotesTitleFirstWithBoldAndUnderlineKept()
    {
        var path = Path.Combine(Path.GetTempPath(), $"notes-{Guid.NewGuid()}.pptx");
        try
        {
            TestPackages.Zip(path, Parts().Select(p => new KeyValuePair<string, byte[]>(p.Key, TestPackages.Utf8(p.Value))));
            var body = PptxNoteText.Body(path, TestContext.Current.CancellationToken);
            Assert.Equal([NoteBlockType.Heading, NoteBlockType.Paragraph, NoteBlockType.Paragraph], body.Blocks.Select(b => b.Type));
            Assert.Equal(2, body.Blocks[0].Level);
            Assert.Equal("Unshakable\nGod is our refuge\nTrust him this week", body.PlainText);
            Assert.Equal([new NoteRun { Text = "God is our refuge", Bold = true }], body.Blocks[1].Runs!);
            Assert.Equal([new NoteRun { Text = "Trust ", Underline = true }, new NoteRun { Text = "him this week" }], body.Blocks[2].Runs!);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static Dictionary<string, string> Parts() => new()
    {
        ["[Content_Types].xml"] = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/ppt/presentation.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"/>
              <Override PartName="/ppt/slides/slide1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slide+xml"/>
            </Types>
            """,
        ["_rels/.rels"] = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="ppt/presentation.xml"/>
            </Relationships>
            """,
        ["ppt/presentation.xml"] = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <p:presentation xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <p:sldIdLst><p:sldId id="256" r:id="rId2"/></p:sldIdLst>
              <p:sldSz cx="12192000" cy="6858000"/>
            </p:presentation>
            """,
        ["ppt/_rels/presentation.xml.rels"] = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide" Target="slides/slide1.xml"/>
            </Relationships>
            """,
        ["ppt/slides/slide1.xml"] = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <p:cSld><p:spTree>
                <p:sp>
                  <p:nvSpPr><p:cNvPr id="3" name="Body"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>
                  <p:spPr><a:xfrm><a:off x="914400" y="2000000"/><a:ext cx="6000000" cy="3000000"/></a:xfrm></p:spPr>
                  <p:txBody><a:bodyPr/>
                    <a:p><a:r><a:rPr b="1"/><a:t>God is our refuge</a:t></a:r></a:p>
                    <a:p><a:r><a:rPr u="sng"/><a:t>Trust</a:t></a:r><a:r><a:rPr/><a:t> him this week</a:t></a:r></a:p>
                  </p:txBody>
                </p:sp>
                <p:sp>
                  <p:nvSpPr><p:cNvPr id="2" name="Title 1"/><p:cNvSpPr/><p:nvPr><p:ph type="title"/></p:nvPr></p:nvSpPr>
                  <p:spPr><a:xfrm><a:off x="914400" y="457200"/><a:ext cx="6000000" cy="914400"/></a:xfrm></p:spPr>
                  <p:txBody><a:bodyPr/><a:p><a:r><a:t>Unshakable</a:t></a:r></a:p></p:txBody>
                </p:sp>
              </p:spTree></p:cSld>
            </p:sld>
            """,
    };
}
