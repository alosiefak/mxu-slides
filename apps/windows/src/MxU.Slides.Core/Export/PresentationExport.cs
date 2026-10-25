using System.Globalization;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Export;

/// <summary>
/// The Export panel's choices and file names, and the PDF slide sheet's layout. Port of the Mac's
/// PresentationExport.swift: the same five formats (an .mxuslides file, a PDF, slide images,
/// plain text, ChordPro), the same names ("Sunday- 9-14.pdf", "01 Verse 1.png") and the same sheet
/// maths, in PDF points with the origin at the bottom left, as Core Graphics draws.
/// <para>
/// Windows adds choices inside two of the formats (<see cref="PdfKind"/>: one slide per page, a lyric
/// sheet or a chord chart, as well as the Mac's sheet of slides; and JPEG as well as PNG pictures),
/// and its plain text is words only (the Mac writes the ChordPro text into its .txt).
/// </para>
/// </summary>
public static class PresentationExport
{
    public enum Format
    {
        SlidesFile,
        Pdf,
        Images,
        PlainText,
        ChordPro,
    }

    /// <summary>What a PDF holds. The Mac's PDF is <see cref="SlideSheet"/>; the others are Windows'.</summary>
    public enum PdfKind
    {
        /// <summary>The slides in rows and columns on a page, with the title and each slide's number (the Mac's).</summary>
        SlideSheet,

        /// <summary>One slide per page, the page the slide's shape.</summary>
        SlidePages,

        /// <summary>The song's words, section by section, for a music stand or a newsletter.</summary>
        LyricSheet,

        /// <summary>The words with each chord above the syllable it falls on.</summary>
        ChordChart,
    }

    public enum ImageType
    {
        Png,
        Jpeg,
    }

    public static IReadOnlyList<Format> AllFormats { get; } = [Format.SlidesFile, Format.Pdf, Format.Images, Format.PlainText, Format.ChordPro];

    public static string Label(this Format format) => format switch
    {
        Format.SlidesFile => $"MxU Slides Presentation (.{SlidesPresentationFile.FileExtension})",
        Format.Pdf => "PDF (.pdf)",
        Format.Images => "Slide Images (PNG or JPEG)",
        Format.PlainText => ChordProExport.FileFormat.PlainText.Label(),
        _ => ChordProExport.FileFormat.ChordPro.Label(),
    };

    public static string Label(this PdfKind kind) => kind switch
    {
        PdfKind.SlidePages => "Slides, one per page",
        PdfKind.LyricSheet => "Lyric sheet (words only)",
        PdfKind.ChordChart => "Chord chart (words and chords)",
        _ => "Slides, several to a page",
    };

    /// <summary>The file's extension without the dot, or null for images (they go into a folder).</summary>
    public static string? FileExtension(this Format format) => format switch
    {
        Format.SlidesFile => SlidesPresentationFile.FileExtension,
        Format.Pdf => "pdf",
        Format.Images => null,
        Format.PlainText => ChordProExport.FileFormat.PlainText.RawValue(),
        _ => ChordProExport.FileFormat.ChordPro.RawValue(),
    };

    public static string Extension(this ImageType type) => type == ImageType.Jpeg ? ".jpg" : ".png";

    public static ChordProExport.FileFormat? ChordProFormat(this Format format) => format switch
    {
        Format.PlainText => ChordProExport.FileFormat.PlainText,
        Format.ChordPro => ChordProExport.FileFormat.ChordPro,
        _ => null,
    };

    /// <summary>True for the formats that draw the slides (a PDF of slides, slide pictures).</summary>
    public static bool RendersSlides(this Format format) => format is Format.Pdf or Format.Images;

    /// <summary>Songs can also be exported as text and ChordPro; other decks as a file, a PDF or pictures. As on the Mac.</summary>
    public static IReadOnlyList<Format> Formats(Presentation presentation) =>
        ChordProExport.IsSong(presentation) ? AllFormats : [Format.SlidesFile, Format.Pdf, Format.Images];

    /// <summary>A lyric sheet and a chord chart need a song's words; a chord chart its chords too.</summary>
    public static IReadOnlyList<PdfKind> PdfKinds(Presentation presentation)
    {
        if (!ChordProExport.IsSong(presentation)) return [PdfKind.SlideSheet, PdfKind.SlidePages];
        return ChordMath.HasChords(presentation)
            ? [PdfKind.SlideSheet, PdfKind.SlidePages, PdfKind.LyricSheet, PdfKind.ChordChart]
            : [PdfKind.SlideSheet, PdfKind.SlidePages, PdfKind.LyricSheet];
    }

    public static string BaseName(Presentation presentation)
    {
        var name = Sanitized(presentation.Name);
        return name.Length == 0 ? "Presentation" : name;
    }

    public static string FileName(Presentation presentation, Format format) =>
        format.ChordProFormat() is { } chordPro
            ? ChordProExport.FileName(presentation, chordPro)
            : BaseName(presentation) + (format.FileExtension() is { } ext ? "." + ext : "");

    /// <summary>
    /// The name the save dialog offers: a name the operator already typed keeps its stem, with the
    /// new format's extension ("Sunday.pdf" switched to ChordPro is "Sunday.cho"), as the Mac's panel does.
    /// </summary>
    public static string FileName(Presentation presentation, Format format, string? typed)
    {
        if (string.IsNullOrEmpty(typed)) return FileName(presentation, format);
        var known = AllFormats.Select(f => f.FileExtension()).OfType<string>().Select(e => "." + e);
        var stem = known.FirstOrDefault(e => typed.EndsWith(e, StringComparison.OrdinalIgnoreCase)) is { } found ? typed[..^found.Length] : typed;
        return stem + (format.FileExtension() is { } ext ? "." + ext : "");
    }

    /// <summary>
    /// Each slide picture's file name: its number, padded so they list in order ("01", or "001" past
    /// 99), then its label when it has one ("01 Verse 1.png"). Port of <c>imageFileNames</c>.
    /// </summary>
    public static IReadOnlyList<string> ImageFileNames(IReadOnlyList<string?> labels, ImageType type = ImageType.Png)
    {
        var digits = Math.Max(2, labels.Count.ToString(CultureInfo.InvariantCulture).Length);
        return labels.Select((label, index) =>
        {
            var number = (index + 1).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
            var name = label is null ? "" : Sanitized(label);
            return (name.Length == 0 ? number : $"{number} {name}") + type.Extension();
        }).ToList();
    }

    /// <summary>"/ : \" become "-" (the Mac's rule), and so do the other characters Windows forbids in a file name.</summary>
    private static string Sanitized(string name) => LibraryBatch.SanitizedName(name);

    /// <summary>
    /// The size of the paper this PC uses, in points (1/72 inch): A4 where the region is metric,
    /// US Letter otherwise. The Mac asks its print settings (NSPrintInfo.shared.paperSize).
    /// </summary>
    public static (double Width, double Height) PaperSize(RegionInfo? region = null) =>
        (region ?? RegionInfo.CurrentRegion).IsMetric ? (595.28, 841.89) : (612, 792);

    /// <summary>
    /// A sheet of slides: the slides in rows of <see cref="Columns"/> across a page, inside a margin,
    /// with a caption under each and the title above the first row of the first page. Port of
    /// <c>PresentationExport.Sheet</c>; rectangles are in PDF points, y up from the page's bottom.
    /// </summary>
    public sealed record Sheet
    {
        public const double Margin = 36;
        public const double TitleHeight = 28;
        public const double CaptionHeight = 14;
        public const int MinColumns = 1;
        public const int MaxColumns = 6;

        public Sheet(double pageWidth, double pageHeight, int columns, double aspect, double spacing = 12)
        {
            PageWidth = pageWidth;
            PageHeight = pageHeight;
            Columns = Math.Clamp(columns, MinColumns, MaxColumns);
            Spacing = spacing;
            Aspect = Math.Max(aspect, 0.01);
        }

        public double PageWidth { get; }
        public double PageHeight { get; }
        public int Columns { get; }
        public double Spacing { get; }
        public double Aspect { get; }

        public (double Width, double Height) TileSize
        {
            get
            {
                var usable = PageWidth - 2 * Margin - (Columns - 1) * Spacing;
                var width = Math.Max(1, usable / Columns);
                return (width, width / Aspect);
            }
        }

        public double CornerRadius => Math.Min(8, Math.Max(3, 8 * TileSize.Width / 200));

        private double RowHeight => TileSize.Height + CaptionHeight;

        private int Rows(bool firstPage)
        {
            var top = firstPage ? TitleHeight : 0;
            var usable = PageHeight - 2 * Margin - top;
            return Math.Max(1, (int)((usable + Spacing) / (RowHeight + Spacing)));
        }

        /// <summary>Where each of <paramref name="count"/> slides goes, page by page.</summary>
        public IReadOnlyList<IReadOnlyList<PdfRect>> Pages(int count)
        {
            var pages = new List<IReadOnlyList<PdfRect>>();
            var placed = 0;
            var (tileWidth, tileHeight) = TileSize;
            while (placed < count)
            {
                var first = pages.Count == 0;
                var perPage = Rows(first) * Columns;
                var top = PageHeight - Margin - (first ? TitleHeight : 0);
                var rects = Enumerable.Range(0, Math.Min(perPage, count - placed)).Select(slot =>
                {
                    var row = slot / Columns;
                    var column = slot % Columns;
                    return new PdfRect(Margin + column * (tileWidth + Spacing), top - row * (RowHeight + Spacing) - tileHeight, tileWidth, tileHeight);
                }).ToList();
                pages.Add(rects);
                placed += rects.Count;
            }
            return pages;
        }
    }
}

/// <summary>A rectangle in PDF points: x from the left, y up from the bottom (the corner nearest the origin).</summary>
public readonly record struct PdfRect(double X, double Y, double Width, double Height)
{
    public double MaxX => X + Width;
    public double MaxY => Y + Height;
}
