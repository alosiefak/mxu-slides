namespace MxU.Slides.Core.Export;

/// <summary>
/// The two PDFs made of slide pictures. <see cref="Sheet"/> is the Mac's (SlideImageExport.swift,
/// drawPDF): the slides in rows on the page, the presentation's name above the first row, each
/// slide's number and label under it, the pictures clipped to rounded corners with a faint
/// outline. <see cref="OnePerPage"/> is Windows': each slide filling a page the slide's shape,
/// for showing the PDF full screen or printing one slide to a sheet.
/// </summary>
public static class SlidesPdf
{
    /// <summary>A slide's picture (a JPEG) and the words under it ("3  Chorus").</summary>
    public sealed record Tile(byte[] Jpeg, string Caption);

    /// <summary>How big a slide's page is: 10 inches across (720 points), the slide's shape.</summary>
    public const double PageWidth = 720;

    public static void Sheet(PdfDocument pdf, IReadOnlyList<Tile> tiles, PresentationExport.Sheet sheet, string title, PdfFont font, PdfFont bold, bool roundedCorners = true)
    {
        var radius = roundedCorners ? sheet.CornerRadius : 0;
        var next = 0;
        foreach (var (rects, index) in sheet.Pages(tiles.Count).Select((rects, index) => (rects, index)))
        {
            var page = pdf.AddPage(sheet.PageWidth, sheet.PageHeight);
            if (index == 0)
                page.TextFitting(bold, 16, PresentationExport.Sheet.Margin, sheet.PageHeight - PresentationExport.Sheet.Margin - 16, title,
                    sheet.PageWidth - 2 * PresentationExport.Sheet.Margin);
            foreach (var rect in rects)
            {
                var tile = tiles[next++];
                page.DrawImage(pdf.AddJpeg(tile.Jpeg), rect, radius);
                // The Mac strokes black at 18% opacity over white paper: the same grey, without the transparency.
                page.StrokeRect(rect, radius, gray: 0.82);
                page.TextFitting(font, 8, rect.X, rect.Y - PresentationExport.Sheet.CaptionHeight + 4, tile.Caption, rect.Width, gray: 0.35);
            }
        }
    }

    public static void OnePerPage(PdfDocument pdf, IReadOnlyList<Tile> tiles, double aspect)
    {
        var height = Math.Round(PageWidth / Math.Max(aspect, 0.01), 2);
        foreach (var tile in tiles)
            pdf.AddPage(PageWidth, height).DrawImage(pdf.AddJpeg(tile.Jpeg), new PdfRect(0, 0, PageWidth, height));
    }
}
