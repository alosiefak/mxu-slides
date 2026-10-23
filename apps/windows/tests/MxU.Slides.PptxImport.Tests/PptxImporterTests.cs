using System.Windows.Media;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.PptxImport.Tests;

public class PptxImporterTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>The Mac deck uses Helvetica; Arial is the font every Windows machine has.</summary>
    private static void MakeDeck(string pptxPath)
    {
        var parts = new Dictionary<string, byte[]>
        {
            ["[Content_Types].xml"] = TestPackages.Utf8("""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Default Extension="png" ContentType="image/png"/>
                  <Override PartName="/ppt/presentation.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"/>
                  <Override PartName="/ppt/slides/slide1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slide+xml"/>
                </Types>
                """),
            ["_rels/.rels"] = TestPackages.Utf8("""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="ppt/presentation.xml"/>
                </Relationships>
                """),
            ["ppt/presentation.xml"] = TestPackages.Utf8("""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <p:presentation xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                                xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <p:sldIdLst><p:sldId id="256" r:id="rId2"/></p:sldIdLst>
                  <p:sldSz cx="12192000" cy="6858000"/>
                </p:presentation>
                """),
            ["ppt/_rels/presentation.xml.rels"] = TestPackages.Utf8("""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide" Target="slides/slide1.xml"/>
                </Relationships>
                """),
            ["ppt/slides/slide1.xml"] = TestPackages.Utf8("""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                       xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
                       xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <p:cSld>
                    <p:spTree>
                      <p:sp>
                        <p:nvSpPr><p:cNvPr id="2" name="Title"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>
                        <p:spPr>
                          <a:xfrm><a:off x="914400" y="457200"/><a:ext cx="1828800" cy="914400"/></a:xfrm>
                          <a:prstGeom prst="rect"/>
                        </p:spPr>
                        <p:txBody>
                          <a:bodyPr/>
                          <a:p><a:r><a:rPr sz="4000"><a:latin typeface="Arial"/></a:rPr><a:t>Hello glass</a:t></a:r></a:p>
                        </p:txBody>
                      </p:sp>
                      <p:pic>
                        <p:nvPicPr><p:cNvPr id="3" name="Picture 1"/><p:cNvPicPr/><p:nvPr/></p:nvPicPr>
                        <p:blipFill><a:blip r:embed="rId2"/><a:srcRect l="25000" r="25000"/></p:blipFill>
                        <p:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="914400" cy="914400"/></a:xfrm></p:spPr>
                      </p:pic>
                    </p:spTree>
                  </p:cSld>
                </p:sld>
                """),
            ["ppt/slides/_rels/slide1.xml.rels"] = TestPackages.Utf8("""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="../media/image1.png"/>
                </Relationships>
                """),
            ["ppt/media/image1.png"] = TestPackages.Png(Colors.Red),
        };
        TestPackages.Zip(pptxPath, parts);
    }

    [Fact]
    public async Task ImportsADeckAndReimportReplacesInPlace()
    {
        using var temp = new TempFolder();
        var library = TestPackages.Library(temp);
        var pptxPath = temp.File($"{Guid.NewGuid()}.pptx");
        MakeDeck(pptxPath);

        var summaries = await PptxImporter.ImportItemsAsync([pptxPath], library, cancellationToken: Cancellation);
        var summary = Assert.Single(summaries);
        Assert.True(summary.Succeeded);
        Assert.Equal(1, summary.MediaImported);
        Assert.Empty(summary.Warnings);
        Assert.Empty(summary.MissingFonts);

        var imported = Assert.IsType<Presentation>(library.Get<Presentation>(summary.PresentationId));
        Assert.Equal(Path.GetFileNameWithoutExtension(pptxPath), imported.Name);
        Assert.Equal("PowerPoint Import", imported.Folder);
        Assert.Equal(PresentationKind.Deck, imported.PresentationKind);
        Assert.Null(imported.CanvasWidth);
        Assert.Single(imported.Slides);
        var objects = imported.Slides[0].Objects;
        Assert.Equal(2, objects.Count);
        Assert.Equal(SlideObjectKind.Text, objects[0].ObjectKind);
        Assert.Equal("Hello glass", objects[0].Text);
        Assert.Equal(80, objects[0].TextStyle?.FontSize);
        Assert.Equal(SlideObjectKind.Shape, objects[1].ObjectKind);
        Assert.Equal(FillKind.Media, objects[1].Fill?.FillKind);
        Assert.False(objects[1].Fill?.MediaId?.StartsWith(PptxDocumentMapper.PlaceholderPrefix));
        Assert.True(MxuJson.StructurallyEqual(new MediaSourceRect { X = 0.25, Y = 0, Width = 0.5, Height = 1 }, objects[1].Fill?.MediaSourceRect));

        var media = Assert.IsType<MediaItem>(library.Get<MediaItem>(objects[1].Fill?.MediaId));
        Assert.Equal("image1", media.Name);
        Assert.Equal(MediaKind.Image, media.MediaKind);
        Assert.Equal(MediaClassification.Foreground, media.Classification);
        Assert.NotNull(library.BlobPath(media));

        var again = await PptxImporter.ImportItemsAsync([pptxPath], library, cancellationToken: Cancellation);
        Assert.Equal(summary.PresentationId, again[0].PresentationId);
        Assert.Equal(0, again[0].MediaImported);
        Assert.Equal(1, library.Count<Presentation>());
        Assert.Equal(1, library.Count<MediaItem>());

        var reopened = new LibraryStore(library.Root);
        reopened.Load();
        Assert.Equal("Hello glass", reopened.Get<Presentation>(summary.PresentationId)?.Slides[0].Objects[0].Text);
    }

    [Fact]
    public async Task LegacyPptIsRefusedWithGuidance()
    {
        using var temp = new TempFolder();
        var library = TestPackages.Library(temp);
        var pptPath = temp.File($"{Guid.NewGuid()}.ppt");
        File.WriteAllText(pptPath, "old binary format");

        var summary = await PptxImporter.ImportAsync(pptPath, library, cancellationToken: Cancellation);
        Assert.Null(summary.PresentationId);
        Assert.Equal(new[] { "legacy .ppt is not supported — open it in PowerPoint and save as .pptx" }, summary.Warnings);
    }

    [Fact]
    public async Task GarbageFileIsSkippedWithReason()
    {
        using var temp = new TempFolder();
        var library = TestPackages.Library(temp);
        var garbage = temp.File($"{Guid.NewGuid()}.pptx");
        File.WriteAllText(garbage, "not a zip at all");

        var summary = await PptxImporter.ImportAsync(garbage, library, cancellationToken: Cancellation);
        Assert.Null(summary.PresentationId);
        Assert.False(summary.Succeeded);
        Assert.Contains("not a readable PowerPoint document", summary.FailureReason);
    }

    [Fact]
    public async Task UnsafeArchiveIsSkippedWithReasonAndWritesNothing()
    {
        using var temp = new TempFolder();
        var library = TestPackages.Library(temp);
        var deck = temp.File("unsafe.pptx");
        TestPackages.Zip(deck, new Dictionary<string, byte[]> { ["../../escaped.xml"] = "<root/>"u8.ToArray() });

        var summary = await PptxImporter.ImportAsync(deck, library, cancellationToken: Cancellation);
        Assert.False(summary.Succeeded);
        Assert.Contains("unsafe file", summary.FailureReason);
        Assert.Equal(0, library.Count<Presentation>());
    }

    [Fact]
    public async Task TheFolderOptionPlacesTheDeck()
    {
        using var temp = new TempFolder();
        var library = TestPackages.Library(temp);
        var pptxPath = temp.File($"{Guid.NewGuid()}.pptx");
        MakeDeck(pptxPath);

        var progress = new List<PptxImportProgress>();
        var summary = await PptxImporter.ImportAsync(
            pptxPath, library, new PptxImportOptions { Folder = "Sermons" }, new SynchronousProgress(progress.Add), Cancellation);
        Assert.Equal("Sermons", library.Get<Presentation>(summary.PresentationId)?.Folder);
        Assert.Equal(PptxImportPhase.Reading, progress.First().Phase);
        Assert.Contains(progress, p => p.Phase == PptxImportPhase.Slides);
        Assert.Contains(progress, p => p.Phase == PptxImportPhase.Media);
        Assert.Equal(PptxImportPhase.Saving, progress.Last().Phase);
    }

    [Fact]
    public async Task FoldersExpandToTheirDecksInNameOrder()
    {
        using var temp = new TempFolder();
        var library = TestPackages.Library(temp);
        var folder = temp.File("Decks");
        Directory.CreateDirectory(Path.Combine(folder, "Nested"));
        MakeDeck(Path.Combine(folder, "b.pptx"));
        MakeDeck(Path.Combine(folder, "Nested", "a.pptx"));
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "ignored");

        var summaries = await PptxImporter.ImportItemsAsync([folder], library, cancellationToken: Cancellation);
        Assert.Equal(new[] { "a", "b" }, summaries.Select(s => s.Name));
        Assert.All(summaries, s => Assert.True(s.Succeeded));
        Assert.Equal(2, library.Count<Presentation>());
        Assert.Equal(1, library.Count<MediaItem>());
        Assert.Equal("Imported 2 of 2 PowerPoint decks — 1 media files", PptxImporter.Summary(summaries));
    }

    [Fact]
    public void PresentationIdIsAStableUuidShapeOfThePath()
    {
        var id = PptxImporter.PresentationId(@"C:\Decks\Sunday.pptx");
        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", id);
        Assert.Equal(id, PptxImporter.PresentationId(@"C:\Decks\Sunday.pptx"));
        Assert.NotEqual(id, PptxImporter.PresentationId(@"C:\Decks\Monday.pptx"));
    }

    private sealed class SynchronousProgress(Action<PptxImportProgress> report) : IProgress<PptxImportProgress>
    {
        public void Report(PptxImportProgress value) => report(value);
    }
}
