using System.Text;
using System.Windows.Media;
using MxU.Slides.Core.Export;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.Rendering;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>How to export: the format, and the choices inside it.</summary>
public sealed record ExportChoice(
    PresentationExport.Format Format, PresentationExport.PdfKind PdfKind = PresentationExport.PdfKind.SlideSheet, int Columns = 3,
    PresentationExport.ImageType ImageType = PresentationExport.ImageType.Png, bool IncludeMedia = true);

/// <summary>
/// Exporting presentations: an .mxuslides file (MainViewModel.LibraryExchange.cs), a PDF (a
/// sheet of slides, a page per slide, a lyric sheet, a chord chart), slide pictures (PNG or JPEG),
/// plain text or ChordPro. Port of the Mac's PresentationExportPanel.swift and SlideImageExport.swift.
/// Slides are drawn on the UI thread one at a time, letting the window (and any live output) draw
/// between them; files are written off it.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Exports one presentation to <paramref name="destination"/> (a file; for pictures, the folder they go in). Returns a line for the notice.</summary>
    public async Task<string> ExportPresentationAsync(Presentation presentation, ExportChoice choice, string destination)
    {
        switch (choice.Format)
        {
            case PresentationExport.Format.SlidesFile:
                var summary = await ExportPresentationAsync(presentation, destination);
                return summary is null ? $"“{presentation.Name}” couldn't be exported." : $"Saved “{Path.GetFileName(destination)}”.";
            case PresentationExport.Format.PlainText or PresentationExport.Format.ChordPro:
                var text = TextExport.For(presentation, choice.Format);
                await OffUiThread(() =>
                {
                    File.WriteAllText(destination, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    return true;
                });
                return $"Saved “{Path.GetFileName(destination)}”.";
            case PresentationExport.Format.Images:
                var written = await WriteSlideImagesAsync(presentation, choice, destination);
                return $"Saved {written} slide picture{(written == 1 ? "" : "s")} in “{Path.GetFileName(destination)}”.";
            default:
                await WritePdfAsync(presentation, choice, destination);
                return $"Saved “{Path.GetFileName(destination)}”.";
        }
    }

    /// <summary>
    /// Exports several into one folder, each named after itself (numbered when two would clash, and
    /// never over a file already there). Text and ChordPro are for songs: other presentations are
    /// left out of those, and said so.
    /// </summary>
    public async Task<string> ExportPresentationsAsync(IReadOnlyList<Presentation> presentations, ExportChoice choice, string folder)
    {
        var textOnly = choice.Format is PresentationExport.Format.PlainText or PresentationExport.Format.ChordPro;
        var lyricOnly = choice.Format == PresentationExport.Format.Pdf && choice.PdfKind is PresentationExport.PdfKind.LyricSheet or PresentationExport.PdfKind.ChordChart;
        var wanted = presentations.Where(p => !(textOnly || lyricOnly) || Core.Lyrics.ChordProExport.IsSong(p)).ToList();
        var skipped = presentations.Count - wanted.Count;
        var extension = choice.Format.FileExtension() is { } ext ? "." + ext : "";
        var taken = Directory.Exists(folder) ? Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).OfType<string>() : [];
        var names = LibraryBatch.UniqueFileNames(wanted.Select(PresentationExport.BaseName).ToList(), extension, taken);
        var done = 0;
        for (var i = 0; i < wanted.Count; i++)
        {
            Notice = $"Exporting “{wanted[i].Name}” ({i + 1} of {wanted.Count})…";
            try
            {
                await ExportPresentationAsync(wanted[i], choice, Path.Combine(folder, names[i]));
                done++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Log.Warn($"Export of {wanted[i].Name} failed: {ex.Message}");
            }
        }
        var line = $"Exported {done} of {presentations.Count} to {folder}.";
        if (skipped > 0) line += $" {skipped} {(skipped == 1 ? "isn't a song" : "aren't songs")}, so had no words to export.";
        Notice = line;
        return line;
    }

    // MARK: Slides as pictures

    /// <summary>The slides an export draws: the default arrangement's, each with its label (its name, else its section's), as the Mac lists them.</summary>
    private static IReadOnlyList<(Slide Slide, string? Label)> ExportedSlides(Presentation presentation)
    {
        var sections = (presentation.Sections ?? []).GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First().Name);
        return [.. SlideSceneBuilder.ArrangedSlides(presentation, presentation.DefaultArrangementId).Select(slide =>
            (slide, (string?)(slide.Name.Length > 0 ? slide.Name : slide.SectionId is { } id && sections.TryGetValue(id, out var name) ? name : null)))];
    }

    /// <summary>One slide's scene for export: with its backgrounds and media, or (left out) only the slide's own layer.</summary>
    private RenderScene ExportScene(Presentation presentation, Slide slide, bool includeMedia)
    {
        var scene = SlideSceneBuilder.Scene(slide, ThemeFor(slide, presentation), presentation, presentation.DefaultArrangementId, pager: TextLayout.Pager);
        if (!includeMedia)
            foreach (var layer in scene.Layers.Where(l => l.Kind != LayerKind.Slide)) layer.Items.Clear();
        return scene;
    }

    private static Color Backdrop(RenderScene scene) =>
        Color.FromArgb(255, (byte)(scene.Background.Red * 255), (byte)(scene.Background.Green * 255), (byte)(scene.Background.Blue * 255));

    private async Task<int> WriteSlideImagesAsync(Presentation presentation, ExportChoice choice, string folder)
    {
        Directory.CreateDirectory(folder);
        var slides = ExportedSlides(presentation);
        var names = PresentationExport.ImageFileNames(slides.Select(s => s.Label).ToList(), choice.ImageType);
        var width = SlideSceneBuilder.CanvasSizeFor(presentation).Width;
        var written = 0;
        for (var i = 0; i < slides.Count; i++)
        {
            Notice = $"Drawing slide {i + 1} of {slides.Count}…";
            var scene = ExportScene(presentation, slides[i].Slide, choice.IncludeMedia);
            var backdrop = Backdrop(scene);
            // Without the media, a PNG is see-through where there are no words (as the Mac's are).
            var picture = await SlidePictures.RenderAsync(scene, (int)width, Media, transparent: !choice.IncludeMedia && choice.ImageType == PresentationExport.ImageType.Png);
            var bytes = choice.ImageType == PresentationExport.ImageType.Jpeg ? SlidePictures.Jpeg(picture, backdrop) : SlidePictures.Png(picture);
            var path = Path.Combine(folder, names[i]);
            await OffUiThread(() =>
            {
                File.WriteAllBytes(path, bytes);
                return true;
            });
            written++;
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        }
        return written;
    }

    private async Task WritePdfAsync(Presentation presentation, ExportChoice choice, string path)
    {
        var fonts = await OffUiThread(() => PdfFonts.Find()) ?? throw new InvalidOperationException("No font could be found to write the PDF with.");
        var (pageWidth, pageHeight) = PresentationExport.PaperSize();
        var pdf = new PdfDocument { Title = presentation.Name };
        if (choice.PdfKind is PresentationExport.PdfKind.LyricSheet or PresentationExport.PdfKind.ChordChart)
        {
            await OffUiThread(() =>
            {
                var (regular, bold) = PdfFonts.AddTo(pdf, fonts);
                var sheet = new LyricSheet(pageWidth, pageHeight, withChords: choice.PdfKind == PresentationExport.PdfKind.ChordChart);
                LyricSheet.Draw(pdf, sheet.Layout(presentation, (text, size, isBold) => (isBold ? bold : regular).Measure(text, size)), pageWidth, pageHeight, regular, bold);
                WriteFile(path, pdf.ToBytes());
                return true;
            });
            return;
        }

        var slides = ExportedSlides(presentation);
        var canvas = SlideSceneBuilder.CanvasSizeFor(presentation);
        var aspect = canvas.Width / Math.Max(1, canvas.Height);
        var sheetLayout = new PresentationExport.Sheet(pageWidth, pageHeight, choice.Columns, aspect);
        // A tile on a sheet needn't be full size: three times its width on the page is plenty for print (the Mac's rule).
        var width = choice.PdfKind == PresentationExport.PdfKind.SlidePages
            ? (int)canvas.Width
            : (int)Math.Min(canvas.Width, Math.Round(sheetLayout.TileSize.Width * 3));
        var tiles = new List<SlidesPdf.Tile>();
        for (var i = 0; i < slides.Count; i++)
        {
            Notice = $"Drawing slide {i + 1} of {slides.Count}…";
            var scene = ExportScene(presentation, slides[i].Slide, choice.IncludeMedia);
            var picture = await SlidePictures.RenderAsync(scene, width, Media, transparent: false);
            tiles.Add(new SlidesPdf.Tile(SlidePictures.Jpeg(picture, Backdrop(scene), quality: 85), slides[i].Label is { } label ? $"{i + 1}  {label}" : $"{i + 1}"));
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        }
        await OffUiThread(() =>
        {
            var (regular, bold) = PdfFonts.AddTo(pdf, fonts);
            if (choice.PdfKind == PresentationExport.PdfKind.SlidePages) SlidesPdf.OnePerPage(pdf, tiles, aspect);
            else SlidesPdf.Sheet(pdf, tiles, sheetLayout, presentation.Name, regular, bold);
            WriteFile(path, pdf.ToBytes());
            return true;
        });
    }

    /// <summary>Written beside the destination and moved into place, so a failed export never leaves half a file under the real name.</summary>
    private static void WriteFile(string path, byte[] bytes)
    {
        var temp = path + ".partial";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }
}
