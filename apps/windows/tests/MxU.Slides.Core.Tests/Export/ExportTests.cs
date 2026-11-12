using MxU.Slides.Core.Export;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Tests.Export;

/// <summary>
/// The export formats. The names and the slide sheet are ports of the Mac's PresentationExportTests;
/// the PDF, font, lyric sheet and text tests read back what was written (<see cref="MiniPdf"/>).
/// </summary>
public sealed class ExportTests
{
    private static readonly byte[] Jpeg = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDABALDA4MChAODQ4SERATGCgaGBYWGDEjJR0oOjM9PDkzODdASFxOQERXRTc4UG1RV19iZ2hnPk1xeXBkeFxlZ2P/2wBDARESEhgVGC8aGi9jQjhCY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2P/wAARCAADAAQDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDpKKKKAP/Z");

    private static string FontPath(string name) => Path.Combine(AppContext.BaseDirectory, "Fonts", name);

    private static TrueTypeFont Poppins(string style = "Regular") => TrueTypeFont.TryLoad(FontPath($"Poppins-{style}.ttf"))!;

    private static TrueTypeFont Montserrat() => TrueTypeFont.TryLoad(FontPath("Montserrat-Regular.ttf"))!;

    private static Presentation Song(string lyrics, string name = "Amazing Grace")
    {
        var song = LyricTextImporter.MakePresentation(lyrics, fallbackTitle: name, linesPerSlide: 4);
        song.Name = name;
        return song;
    }

    private static Presentation Deck(string name) => new() { Id = "d", Name = name, PresentationKind = PresentationKind.Deck, ThemeId = "", Slides = [] };

    // MARK: Names and formats (PresentationExportTests.swift)

    [Fact]
    public void SongsAddTextAndChordProOtherDecksDoNot()
    {
        var song = LyricTextImporter.MakePresentation("Verse 1\nAmazing grace", linesPerSlide: 2, lyricLines: []);
        var deck = new Presentation { Id = "d", Name = "Announcements", PresentationKind = PresentationKind.Deck, ThemeId = "", Slides = [new Slide { Id = "s", Name = "Welcome", Objects = [] }] };
        Assert.Equal(PresentationExport.AllFormats, PresentationExport.Formats(song));
        Assert.Equal([PresentationExport.Format.SlidesFile, PresentationExport.Format.Pdf, PresentationExport.Format.Images], PresentationExport.Formats(deck));
        Assert.Equal([PresentationExport.PdfKind.SlideSheet, PresentationExport.PdfKind.SlidePages], PresentationExport.PdfKinds(deck));
        Assert.Contains(PresentationExport.PdfKind.LyricSheet, PresentationExport.PdfKinds(song));
        Assert.DoesNotContain(PresentationExport.PdfKind.ChordChart, PresentationExport.PdfKinds(song)); // no chords, no chord chart
    }

    [Fact]
    public void FileNamesFollowTheFormat()
    {
        var deck = Deck("Sunday: 9/14");
        Assert.Equal("Sunday- 9-14.mxuslides", PresentationExport.FileName(deck, PresentationExport.Format.SlidesFile));
        Assert.Equal("Sunday- 9-14.pdf", PresentationExport.FileName(deck, PresentationExport.Format.Pdf));
        Assert.Equal("Sunday- 9-14", PresentationExport.FileName(deck, PresentationExport.Format.Images));
        Assert.Equal("Sunday- 9-14.cho", PresentationExport.FileName(deck, PresentationExport.Format.ChordPro));
        Assert.Equal("Presentation.pdf", PresentationExport.FileName(Deck("  "), PresentationExport.Format.Pdf));
    }

    [Fact]
    public void ThePanelOpensOnTheDecksNameAndKeepsATypedOne()
    {
        var deck = Deck("Amazing Grace");
        Assert.Equal("Amazing Grace.pdf", PresentationExport.FileName(deck, PresentationExport.Format.Pdf, typed: null));
        Assert.Equal("Amazing Grace.pdf", PresentationExport.FileName(deck, PresentationExport.Format.Pdf, typed: ""));
        Assert.Equal("Sunday.mxuslides", PresentationExport.FileName(deck, PresentationExport.Format.SlidesFile, typed: "Sunday.pdf"));
        Assert.Equal("Sunday", PresentationExport.FileName(deck, PresentationExport.Format.Images, typed: "Sunday.cho"));
        Assert.Equal("Sunday.txt", PresentationExport.FileName(deck, PresentationExport.Format.PlainText, typed: "Sunday"));
    }

    [Fact]
    public void ImageNamesSortInExplorerAndCarryTheLabel()
    {
        Assert.Equal(["01 Verse 1.png", "02.png", "03 A-B.png", "04.png"], PresentationExport.ImageFileNames(["Verse 1", null, "A/B", " "]));
        var many = PresentationExport.ImageFileNames(Enumerable.Repeat<string?>(null, 120).ToList(), PresentationExport.ImageType.Jpeg);
        Assert.Equal("001.jpg", many[0]);
        Assert.Equal("120.jpg", many[^1]);
    }

    [Fact]
    public void TheSheetFillsPagesInRowsWithinTheMargins()
    {
        var sheet = new PresentationExport.Sheet(612, 792, 3, 16.0 / 9.0);
        var pages = sheet.Pages(40);
        Assert.Equal(40, pages.Sum(p => p.Count));
        Assert.True(pages[0].Count <= pages[1].Count); // the title takes room on the first page
        foreach (var rect in pages.SelectMany(p => p))
        {
            Assert.True(rect.X >= PresentationExport.Sheet.Margin - 0.001);
            Assert.True(rect.MaxX <= 612 - PresentationExport.Sheet.Margin + 0.001);
            Assert.True(rect.Y - PresentationExport.Sheet.CaptionHeight >= PresentationExport.Sheet.Margin - 0.001);
            Assert.True(Math.Abs(rect.Width / rect.Height - 16.0 / 9.0) < 0.001);
        }
        Assert.Equal(12, pages[0][1].X - pages[0][0].MaxX, 6);
        Assert.True(pages[0][3].MaxY < pages[0][0].Y); // row two is below row one
    }

    [Fact]
    public void TheSheetClampsColumnsAndScalesCorners()
    {
        Assert.Equal(1, new PresentationExport.Sheet(612, 792, 0, 1).Columns);
        Assert.Equal(6, new PresentationExport.Sheet(612, 792, 9, 1).Columns);
        Assert.Equal(8, new PresentationExport.Sheet(612, 792, 1, 16.0 / 9.0).CornerRadius);
        Assert.True(new PresentationExport.Sheet(612, 792, 6, 16.0 / 9.0).CornerRadius < 8);
        Assert.Empty(new PresentationExport.Sheet(612, 792, 3, 16.0 / 9.0).Pages(0));
    }

    [Fact]
    public void PaperIsA4WhereTheRegionIsMetric()
    {
        Assert.Equal((612, 792), PresentationExport.PaperSize(new System.Globalization.RegionInfo("US")));
        Assert.Equal(595.28, PresentationExport.PaperSize(new System.Globalization.RegionInfo("NG")).Width);
    }

    // MARK: Fonts

    [Fact]
    public void AFontsGlyphsAndWidthsAreRead()
    {
        var font = Poppins();
        Assert.Equal("Poppins-Regular", font.PostScriptName);
        Assert.True(font.CanEmbed);
        Assert.NotEqual(0, font.GlyphFor('A'));
        Assert.Equal(0, font.GlyphFor(0x10FFFF)); // nothing there: the missing-glyph box
        Assert.True(font.Measure("WWW", 12) > font.Measure("iii", 12));
        Assert.Equal(font.Measure("ab", 10) * 2, font.Measure("ab", 20), 6);
    }

    [Fact]
    public void ASubsetKeepsTheGlyphsUsedAndTheirNumbersAndIsMuchSmaller()
    {
        var font = Poppins();
        var used = "Amazing grace é".EnumerateRunes().Select(r => font.GlyphFor(r.Value)).ToList();
        var subset = font.Subset(used);
        var original = new FileInfo(FontPath("Poppins-Regular.ttf")).Length;
        Assert.True(subset.Length < original / 4, $"{subset.Length} of {original}");
        var again = TrueTypeFont.TryParse(subset)!;
        Assert.NotNull(again);
        foreach (var rune in "Amazing grace é".EnumerateRunes())
        {
            Assert.Equal(font.GlyphFor(rune.Value), again.GlyphFor(rune.Value));
            Assert.Equal(font.Advance(font.GlyphFor(rune.Value)), again.Advance(again.GlyphFor(rune.Value)));
        }
        // A well-formed font file: its checksum adjustment makes the whole file sum to the magic number.
        Assert.Equal(0xB1B0AFBAu, TrueTypeFont.Checksum(subset));
    }

    [Fact]
    public void FontsThatArentTrueTypeAreRefused()
    {
        Assert.Null(TrueTypeFont.TryParse([0x4F, 0x54, 0x54, 0x4F, 0, 0, 0, 0, 0, 0, 0, 0])); // "OTTO": CFF outlines
        Assert.Null(TrueTypeFont.TryParse([1, 2, 3]));
        Assert.Null(TrueTypeFont.TryLoad(FontPath("missing.ttf")));
    }

    // MARK: PDF

    [Fact]
    public void APdfReadsBackItsPagesPicturesAndText()
    {
        var pdf = new PdfDocument { Title = "Sunday 25 October", Created = new DateTimeOffset(2026, 10, 25, 9, 30, 0, TimeSpan.FromHours(1)) };
        // Poppins hasn't got Yoruba's dotted letters: they're borrowed from Montserrat, which has.
        var font = pdf.AddFont(Poppins(), Montserrat());
        var bold = pdf.AddFont(Poppins("SemiBold"));
        var page = pdf.AddPage(612, 792);
        page.Text(bold, 16, 36, 740, "Amazing Grace");
        page.Text(font, 12, 36, 700, "Ẹ ṣeun, Olúwa — “thank you”");
        page.DrawImage(pdf.AddJpeg(Jpeg), new PdfRect(36, 400, 320, 180), cornerRadius: 8);
        var second = pdf.AddPage(720, 405);
        second.DrawImage(pdf.AddJpeg(Jpeg), new PdfRect(0, 0, 720, 405));

        var read = new MiniPdf(pdf.ToBytes());
        var pages = read.Pages();
        Assert.Equal(2, pages.Count);
        Assert.Equal((612, 792), read.PageSize(pages[0]));
        Assert.Equal((720, 405), read.PageSize(pages[1]));
        Assert.Equal("Amazing Grace", read.Text(pages[0])[0]);
        Assert.Equal("Ẹ ṣeun, Olúwa — “thank you”", string.Concat(read.Text(pages[0]).Skip(1)));
        Assert.Equal(["Im1"], read.ImagesDrawn(pages[0]));
        Assert.Equal(["Im2"], read.ImagesDrawn(pages[1]));
        Assert.Contains("W n", read.Content(pages[0])); // the rounded clip
        // The embedded fonts are real (subset) TrueType files with the glyphs drawn.
        var borrowed = TrueTypeFont.TryParse(read.FontFile(pages[0], "F2"))!;
        Assert.Equal("Montserrat-Regular", borrowed.PostScriptName);
        Assert.NotEqual(0, borrowed.GlyphFor('Ẹ'));
        Assert.Equal(Montserrat().GlyphFor('ṣ'), borrowed.GlyphFor('ṣ'));
        Assert.Equal(Poppins().GlyphFor('O'), TrueTypeFont.TryParse(read.FontFile(pages[0], "F1"))!.GlyphFor('O'));
    }

    [Fact]
    public void ADecomposedLetterIsDrawnAsTheOneLetter()
    {
        var pdf = new PdfDocument();
        var font = pdf.AddFont(Montserrat());
        var page = pdf.AddPage(200, 200);
        page.Text(font, 12, 10, 10, "ẹ"); // e + combining dot below
        var read = new MiniPdf(pdf.ToBytes());
        Assert.Equal(["ẹ"], read.Text(read.Pages()[0]));
    }

    [Fact]
    public void LongCaptionsAreCutToFit()
    {
        var pdf = new PdfDocument();
        var font = pdf.AddFont(Poppins());
        var fitted = PdfPage.Fit(font, 8, "This caption is far too long for a small slide tile", 60);
        Assert.EndsWith("…", fitted);
        Assert.True(font.Measure(fitted, 8) <= 60);
        Assert.Equal("Short", PdfPage.Fit(font, 8, "Short", 60));
    }

    [Fact]
    public void JpegSizesAreReadFromTheFrameHeader()
    {
        Assert.Equal((4, 3, 3), PdfDocument.JpegSize(Jpeg));
        Assert.Null(PdfDocument.JpegSize([0x89, 0x50, 0x4E, 0x47]));
        Assert.Throws<InvalidDataException>(() => new PdfDocument().AddJpeg([1, 2, 3]));
    }

    [Fact]
    public void TheSlideSheetHasTheTitleAndACaptionPerSlide()
    {
        var pdf = new PdfDocument();
        var font = pdf.AddFont(Poppins());
        var bold = pdf.AddFont(Poppins("SemiBold"));
        var tiles = Enumerable.Range(1, 14).Select(n => new SlidesPdf.Tile(Jpeg, $"{n}  Verse")).ToList();
        SlidesPdf.Sheet(pdf, tiles, new PresentationExport.Sheet(612, 792, 2, 16.0 / 9.0), "Amazing Grace", font, bold);
        var read = new MiniPdf(pdf.ToBytes());
        var pages = read.Pages();
        Assert.True(pages.Count >= 2);
        Assert.Equal("Amazing Grace", read.Text(pages[0])[0]);
        Assert.Equal(14, pages.Sum(p => read.ImagesDrawn(p).Count));
        Assert.Equal("14  Verse", read.Text(pages[^1])[^1]);
        Assert.DoesNotContain("Amazing Grace", read.Text(pages[1])); // the title is on the first page only

        var onePer = new PdfDocument();
        SlidesPdf.OnePerPage(onePer, tiles.Take(3).ToList(), 16.0 / 9.0);
        var each = new MiniPdf(onePer.ToBytes());
        Assert.Equal(3, each.Pages().Count);
        Assert.Equal((720, 405), each.PageSize(each.Pages()[0]));
    }

    // MARK: Lyric sheets and chord charts

    /// <summary>Every character 6 points wide at 12 points (bold the same): easy sums for where things land.</summary>
    private static double FakeMeasure(string text, double size, bool bold) => text.Length * size / 2;

    private static Presentation ChordSong()
    {
        var song = Song("Verse 1\n[G]Amazing [C]grace how [G]sweet the sound\nThat [D]saved a wretch like me\n\nChorus\nMy [G]chains are gone");
        song.Ccli = new CCLIInfo { Author = "John Newton", SongNumber = 22025, Copyright = "Public Domain" };
        song.MusicKey = "G";
        return song;
    }

    [Fact]
    public void ALyricSheetHasTheTitleCreditsSectionsAndWords()
    {
        var song = ChordSong();
        var pages = new LyricSheet(612, 792, withChords: false).Layout(song, FakeMeasure);
        var runs = pages.Single().Runs;
        Assert.Equal("Amazing Grace", runs.Single(r => r.Kind == LyricSheet.RunKind.Title).Text);
        Assert.Equal("John Newton  ·  CCLI 22025  ·  Key G", runs.Single(r => r.Kind == LyricSheet.RunKind.Credits).Text);
        Assert.Equal(["VERSE 1", "CHORUS"], runs.Where(r => r.Kind == LyricSheet.RunKind.Section).Select(r => r.Text));
        Assert.Equal("Amazing grace how sweet the sound", runs.First(r => r.Kind == LyricSheet.RunKind.Lyric).Text);
        Assert.DoesNotContain(runs, r => r.Kind == LyricSheet.RunKind.Chord);
        Assert.Equal(["© Public Domain", "CCLI Song # 22025"], runs.Where(r => r.Kind == LyricSheet.RunKind.Footer).Select(r => r.Text));
        // Top to bottom.
        var ys = runs.Where(r => r.Kind != LyricSheet.RunKind.Footer).Select(r => r.Y).ToList();
        Assert.Equal(ys.OrderByDescending(y => y), ys);
    }

    [Fact]
    public void AChordChartPutsEachChordOverItsLetter()
    {
        var song = ChordSong();
        var runs = new LyricSheet(612, 792, withChords: true).Layout(song, FakeMeasure).Single().Runs;
        var line = runs.First(r => r.Kind == LyricSheet.RunKind.Lyric);
        var chords = runs.Where(r => r.Kind == LyricSheet.RunKind.Chord && r.Y > line.Y && r.Y < line.Y + 20).ToList();
        Assert.Equal(["G", "C", "G"], chords.Select(c => c.Text));
        // "Amazing " is 8 characters, at 6.25 points each (12.5-point lyrics, the fake measure) = 50.
        Assert.Equal(LyricSheet.Margin + 0, chords[0].X, 3);
        Assert.Equal(LyricSheet.Margin + 8 * LyricSheet.LyricSize / 2, chords[1].X, 3);
        Assert.True(chords[0].Y > line.Y);
    }

    [Fact]
    public void ChordsCloseTogetherDontOverlap()
    {
        var song = Song("Verse 1\n[Gsus4][G]Amen");
        var runs = new LyricSheet(612, 792, withChords: true).Layout(song, FakeMeasure).Single().Runs;
        var chords = runs.Where(r => r.Kind == LyricSheet.RunKind.Chord).ToList();
        Assert.Equal(2, chords.Count);
        Assert.True(chords[1].X >= chords[0].X + FakeMeasure("Gsus4 ", LyricSheet.ChordSize, true) - 0.001);
    }

    [Fact]
    public void AChordChartFollowsTheKeyTheSongIsShownIn()
    {
        var song = ChordSong();
        song.DisplayKey = "A";
        var runs = new LyricSheet(612, 792, withChords: true).Layout(song, FakeMeasure).Single().Runs;
        Assert.Equal(["A", "D", "A"], runs.Where(r => r.Kind == LyricSheet.RunKind.Chord).Take(3).Select(c => c.Text));
        Assert.Contains("Key A", runs.Single(r => r.Kind == LyricSheet.RunKind.Credits).Text);
    }

    [Fact]
    public void ALongSongRunsOntoMorePagesWithoutLeavingASectionsNameAlone()
    {
        var stanzas = string.Join("\n\n", Enumerable.Range(1, 30).Select(n => $"Verse {n}\nLine one of verse {n}\nLine two of verse {n}\nLine three\nLine four"));
        var song = Song(stanzas, "Long Song");
        var sheet = new LyricSheet(612, 792, withChords: false);
        var pages = sheet.Layout(song, FakeMeasure);
        Assert.True(pages.Count >= 3);
        foreach (var page in pages)
        {
            Assert.All(page.Runs, r => Assert.True(r.Y >= LyricSheet.Margin - 0.001));
            // The last thing on a page is never a section's name.
            Assert.NotEqual(LyricSheet.RunKind.Section, page.Runs.Where(r => r.Kind != LyricSheet.RunKind.Footer).Last().Kind);
        }
        Assert.Equal(30, pages.Sum(p => p.Runs.Count(r => r.Kind == LyricSheet.RunKind.Section)));
    }

    [Fact]
    public void ALineTooWideForThePageWraps()
    {
        var song = Song("Verse 1\n" + string.Join(' ', Enumerable.Repeat("[C]word", 30)));
        var runs = new LyricSheet(612, 792, withChords: true).Layout(song, FakeMeasure).Single().Runs;
        var lyrics = runs.Where(r => r.Kind == LyricSheet.RunKind.Lyric).ToList();
        Assert.True(lyrics.Count >= 2);
        Assert.All(lyrics, r => Assert.True(FakeMeasure(r.Text, LyricSheet.LyricSize, false) <= 612 - 2 * LyricSheet.Margin));
        Assert.Equal(30, runs.Count(r => r.Kind == LyricSheet.RunKind.Chord));
    }

    [Fact]
    public void ALyricSheetDrawnWithARealFontReadsBack()
    {
        var song = ChordSong();
        var pdf = new PdfDocument { Title = song.Name };
        var regular = pdf.AddFont(Poppins());
        var bold = pdf.AddFont(Poppins("SemiBold"));
        var sheet = new LyricSheet(612, 792, withChords: true);
        LyricSheet.Draw(pdf, sheet.Layout(song, (t, s, b) => (b ? bold : regular).Measure(t, s)), 612, 792, regular, bold);
        var read = new MiniPdf(pdf.ToBytes());
        var text = read.Text(read.Pages()[0]);
        Assert.Contains("Amazing grace how sweet the sound", text);
        Assert.Contains("D", text);
        Assert.Contains("CCLI Song # 22025", text);
    }

    // MARK: Text

    [Fact]
    public void PlainTextIsTheWordsWithoutChordsOrTags()
    {
        var text = TextExport.PlainText(ChordSong());
        Assert.StartsWith("Amazing Grace\r\nJohn Newton  ·  CCLI 22025  ·  Key G\r\n\r\nVERSE 1\r\nAmazing grace how sweet the sound\r\n", text);
        Assert.DoesNotContain("[", text);
        Assert.DoesNotContain("{", text);
        Assert.EndsWith("© Public Domain\r\nCCLI Song # 22025\r\n", text);
        Assert.Contains("{title: Amazing Grace}", TextExport.For(ChordSong(), PresentationExport.Format.ChordPro));
    }

    [Fact]
    public void TheFontsArePickedFromWindowsThenTheApp()
    {
        var fonts = PdfFonts.Find(windowsFonts: Path.Combine(Path.GetTempPath(), "no-fonts-here"), shippedFonts: Path.Combine(AppContext.BaseDirectory, "Fonts"));
        Assert.Equal("Montserrat-Regular", fonts!.Regular.PostScriptName);
        Assert.Null(PdfFonts.Find(windowsFonts: Path.GetTempPath(), shippedFonts: Path.GetTempPath()));
    }
}
