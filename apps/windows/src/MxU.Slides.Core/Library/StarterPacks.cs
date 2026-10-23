using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Library;

/// <summary>
/// The four starter themes, with the same ids, layouts and colours as the
/// Mac app's StarterPacks.swift (animation steps are not carried yet).
/// </summary>
public enum StarterPack { CleanGeometric, EditorialSerif, BoldBlocks, Digital }

public static class StarterPacks
{
    public static readonly StarterPack[] All = Enum.GetValues<StarterPack>();

    public static string Raw(this StarterPack pack) => pack switch
    {
        StarterPack.CleanGeometric => "cleanGeometric",
        StarterPack.EditorialSerif => "editorialSerif",
        StarterPack.BoldBlocks => "boldBlocks",
        _ => "digital",
    };

    public static string Title(this StarterPack pack) => pack switch
    {
        StarterPack.CleanGeometric => "Clean Geometric",
        StarterPack.EditorialSerif => "Editorial Serif",
        StarterPack.BoldBlocks => "Bold Blocks",
        _ => "Digital",
    };

    public static string ThemeId(this StarterPack pack) => $"pack.{pack.Raw()}.theme";

    private sealed record Look(
        string Body, string Display, string Accent, string Accent2, string Ink, string InkOnAccent,
        string Plate, string Plate2, string Backdrop, double Radius);

    private static Look LookOf(StarterPack pack) => pack switch
    {
        StarterPack.CleanGeometric => new("HelveticaNeue", "HelveticaNeue-Bold", "#3B82F6FF", "#60A5FAFF",
            "#FFFFFFFF", "#FFFFFFFF", "#0F172AF2", "#1E293BF2", "#0B1220FF", 10),
        StarterPack.EditorialSerif => new("Georgia", "Georgia-Bold", "#C8A96AFF", "#E6D3A3FF",
            "#F7F3EAFF", "#1C1A17FF", "#1C1A17CC", "#1C1A1700", "#14120FFF", 0),
        StarterPack.BoldBlocks => new("AvenirNext-DemiBold", "AvenirNext-Heavy", "#FACC15FF", "#111111FF",
            "#FFFFFFFF", "#111111FF", "#111111FF", "#111111FF", "#111111FF", 0),
        _ => new("Menlo-Regular", "Menlo-Bold", "#22D3EEFF", "#F472B6FF",
            "#E6FBFFFF", "#05080CFF", "#0A0F14E6", "#0A0F14E6", "#05080CFF", 4),
    };

    private const string SampleLyrics = "Amazing grace, how sweet the sound\nThat saved a wretch like me\nI once was lost, but now am found\nWas blind, but now I see";

    public static Theme MakeTheme(this StarterPack pack)
    {
        var b = new Builder(pack);
        var look = LookOf(pack);
        return new Theme
        {
            Id = pack.ThemeId(),
            Name = pack.Title(),
            FontFamily = look.Body,
            FontSize = 72,
            TextColorHex = look.Ink,
            BackgroundColorHex = look.Backdrop,
            Slides =
            [
                b.LyricsSlide(), b.PointsSlide(), b.SectionTitleSlide(), b.VerseSlide(), b.PointSlide(), b.QuoteSlide(),
                b.LyricsLowerThird(), b.LyricsSideThird(),
            ],
        };
    }

    private sealed class Builder(StarterPack pack)
    {
        private readonly Look look = LookOf(pack);
        private string ThemeId => pack.ThemeId();
        private bool Bold => pack == StarterPack.BoldBlocks;
        private bool Editorial => pack == StarterPack.EditorialSerif;
        private double KickerTracking => Editorial ? 8 : pack == StarterPack.Digital ? 4 : 6;

        private static ObjectFill Fill(string hex) => new() { FillKind = FillKind.Solid, ColorHex = hex };

        private static ObjectFill Gradient(string a, string c, double angle = 0) => new()
        {
            FillKind = FillKind.LinearGradient,
            GradientAngleDegrees = angle,
            GradientStops = [new() { ColorHex = a, Position = 0 }, new() { ColorHex = c, Position = 1 }],
        };

        private SlideObject Block(string id, string name, double x, double y, double w, double h,
            ObjectFill? fill = null, string? color = null, double? radius = null, ObjectStroke? stroke = null, double? skew = null)
        {
            var r = radius ?? look.Radius;
            return new SlideObject
            {
                Id = id, ObjectKind = SlideObjectKind.Shape, Name = name, Text = "", X = x, Y = y, Width = w, Height = h,
                ShapeKind = r > 0 ? ShapeKind.RoundedRectangle : ShapeKind.Rectangle, CornerRadius = r,
                Fill = fill ?? Fill(color ?? look.Plate), Stroke = stroke, SkewX = skew,
            };
        }

        private SlideObject Label(string id, string name, string text, double x, double y, double w, double h, double size,
            bool display = false, string? font = null, string? color = null,
            TextHorizontalAlignment align = TextHorizontalAlignment.Left, TextVerticalAlignment valign = TextVerticalAlignment.Middle,
            double tracking = 0, bool uppercase = false, double inset = 24, double lineHeight = 1,
            TextLineFill? lineFill = null, bool shrink = false, bool page = false) => new()
        {
            Id = id, ObjectKind = SlideObjectKind.Text, Name = name, Text = text, X = x, Y = y, Width = w, Height = h,
            TextStyle = new TextStyle
            {
                FontName = font ?? (display ? look.Display : look.Body),
                FontSize = size,
                ColorHex = color ?? look.Ink,
                Tracking = tracking,
                LineHeightMultiple = lineHeight,
                HorizontalAlignment = align,
                VerticalAlignment = valign,
                TextTransform = uppercase ? TextTransform.Uppercase : null,
                AutoShrink = shrink ? true : null,
                PageOnClick = page ? true : null,
                LineFill = lineFill,
                InsetLeft = inset,
                InsetRight = inset,
            },
        };

        private Slide ThemeSlide(string item, string name, List<SlideObject> objects, bool fullScreen, string? folder = null) => new()
        {
            Id = $"{ThemeId}.{item}",
            Name = name,
            Objects = objects,
            BackgroundFill = fullScreen ? Fill(look.Backdrop) : null,
            Folder = folder ?? (fullScreen ? "Full Slide" : null),
        };

        public Slide LyricsSlide()
        {
            var b = $"{ThemeId}.lyrics";
            return ThemeSlide("lyrics", "Lyrics",
            [
                Label($"{b}.text", "Lyrics", SampleLyrics, Bold ? 100 : 160, 160, Bold ? 1720 : 1600, 700,
                    Bold ? 64 : 84, display: true, font: Editorial ? "Georgia" : null, align: TextHorizontalAlignment.Center,
                    tracking: Bold ? 2 : 0, uppercase: Bold, inset: 0, lineHeight: 1.22),
                Block($"{b}.rule", "Rule", 880, 900, 160, Bold ? 10 : 4, color: look.Accent, radius: 0, skew: Bold ? -12 : null),
            ], fullScreen: true);
        }

        public Slide LyricsLowerThird()
        {
            var b = $"{ThemeId}.lyricsLower";
            return ThemeSlide("lyricsLower", "Lyrics (Lower Third)",
            [
                Label($"{b}.text", "Lyrics", SampleLyrics, 210, 750, 1550, 220, 46,
                    display: Bold, font: Editorial ? "Georgia" : null, uppercase: Bold, inset: 0, lineHeight: 1.15, shrink: true, page: true),
                Block($"{b}.band", "Band", 0, 720, 1920, 280, fill: Gradient(look.Plate2, look.Plate, 90), radius: 0),
                Block($"{b}.bar", "Bar", 160, 760, Bold ? 18 : 8, 200, color: look.Accent, radius: 0, skew: Bold ? -12 : null),
            ], fullScreen: false, folder: "Lower Thirds").WithTextOnTop();
        }

        public Slide LyricsSideThird()
        {
            var b = $"{ThemeId}.lyricsSide";
            return ThemeSlide("lyricsSide", "Lyrics (Side Third)",
            [
                Label($"{b}.text", "Lyrics", SampleLyrics, 1340, 220, 520, 640, 44,
                    display: Bold, font: Editorial ? "Georgia" : null, valign: TextVerticalAlignment.Top, uppercase: Bold, inset: 0,
                    lineHeight: 1.2, shrink: true, page: true),
                Block($"{b}.panel", "Panel", 1280, 0, 640, 1080, fill: Gradient(look.Plate2, look.Plate), radius: 0),
                Block($"{b}.bar", "Bar", 1280, 0, Bold ? 18 : 6, 1080, color: look.Accent, radius: 0),
            ], fullScreen: false, folder: "Side Thirds").WithTextOnTop();
        }

        public Slide PointsSlide()
        {
            var b = $"{ThemeId}.points";
            var points = new SlideObject
            {
                Id = $"{b}.text", ObjectKind = SlideObjectKind.Text, Name = "Points",
                Text = "First point\nSecond point\nThird point",
                X = 200, Y = 220, Width = 1520, Height = 640,
                TextStyle = new TextStyle
                {
                    FontName = look.Body, FontSize = 80, ColorHex = look.Ink, LineHeightMultiple = 1.25,
                    HorizontalAlignment = TextHorizontalAlignment.Left, VerticalAlignment = TextVerticalAlignment.Middle,
                    LineFill = Bold ? new TextLineFill { Fill = Fill(look.Accent), WidthMode = TextLineFillWidthMode.LineWidth, VerticalPadding = 4, HorizontalPadding = 24 } : null,
                },
            };
            return ThemeSlide("points", "Points",
            [
                Label($"{b}.kicker", "Kicker", "Today's message", 200, 140, 1520, 60, 30, color: look.Accent,
                    tracking: KickerTracking, uppercase: true, inset: 0),
                Block($"{b}.rule", "Rule", 224, 205, 160, 4, color: look.Accent, radius: 0),
                points,
            ], fullScreen: true);
        }

        public Slide SectionTitleSlide()
        {
            var b = $"{ThemeId}.section";
            var objects = new List<SlideObject>
            {
                Label($"{b}.kicker", "Kicker", "Part one", 160, 340, 1600, 60, 32, color: look.Accent,
                    align: TextHorizontalAlignment.Center, tracking: KickerTracking, uppercase: true),
                Label($"{b}.text", "Section Title", "Section Title", 160, 400, 1600, 260, 140, display: true,
                    color: Bold ? look.InkOnAccent : look.Ink, align: TextHorizontalAlignment.Center,
                    tracking: Bold ? 4 : 0, uppercase: Bold),
                Block($"{b}.rule", "Rule Left", 560, 690, 380, 4, color: look.Accent, radius: 0),
                Block($"{b}.rule2", "Rule Right", 980, 690, 380, 4, color: look.Accent, radius: 0),
            };
            if (Bold) objects.Insert(0, Block($"{b}.block", "Block", 260, 420, 1400, 220, color: look.Accent, skew: -12));
            return ThemeSlide("section", "Section Title", objects, fullScreen: true);
        }

        public Slide VerseSlide()
        {
            var b = $"{ThemeId}.verse";
            return ThemeSlide("verse", "Verse + Reference",
            [
                Block($"{b}.bar", "Bar", 200, 300, Bold ? 22 : 8, 480, color: look.Accent, radius: 0, skew: Bold ? -12 : null),
                Label($"{b}.verse", "Verse",
                    "For God so loved the world, that he gave his only Son, that whoever believes in him should not perish but have eternal life.",
                    260, 280, 1460, 440, 64, font: Editorial ? "Georgia-Italic" : look.Body, valign: TextVerticalAlignment.Top,
                    inset: 0, lineHeight: 1.15),
                Block($"{b}.rule", "Rule", 260, 760, 140, 4, color: look.Accent, radius: 0),
                Label($"{b}.ref", "Reference", "John 3:16 (ESV)", 260, 780, 1460, 60, 34, display: true,
                    color: look.Accent, tracking: KickerTracking - 2, uppercase: !Editorial, inset: 0),
            ], fullScreen: true);
        }

        public Slide PointSlide()
        {
            var b = $"{ThemeId}.point";
            return ThemeSlide("point", "Point",
            [
                Label($"{b}.kicker", "Kicker", "Point 1", 200, 260, 1520, 60, 34, color: look.Accent, tracking: KickerTracking, uppercase: true, inset: 0),
                Block($"{b}.rule", "Rule", 200, 330, 220, Bold ? 12 : 5, color: look.Accent, radius: 0),
                Label($"{b}.text", "Point", "Faith is not the absence of fear — it is trust that outlasts it.",
                    200, 360, 1520, 460, 92, display: true, valign: TextVerticalAlignment.Top, tracking: Bold ? 2 : 0, uppercase: Bold,
                    inset: 0, lineHeight: 1.08,
                    lineFill: Bold ? new TextLineFill { Fill = Fill(look.Accent), WidthMode = TextLineFillWidthMode.LineWidth, VerticalPadding = 2, HorizontalPadding = 28 } : null),
            ], fullScreen: true);
        }

        public Slide QuoteSlide()
        {
            var b = $"{ThemeId}.quote";
            var markFont = Editorial ? "Georgia-Bold" : Bold ? "AvenirNext-Heavy" : look.Display;
            var objects = new List<SlideObject>
            {
                Block($"{b}.frame", "Frame", 300, 250, 1320, 580, fill: Bold ? Fill(look.Plate) : Fill("#00000000"),
                    radius: pack == StarterPack.CleanGeometric ? 14 : 0,
                    stroke: new ObjectStroke { ColorHex = look.Accent, Width = Bold ? 10 : 3 }, skew: Bold ? -6 : null),
                Block($"{b}.chip", "Mark Chip", 250, 150, 220, 220, color: Bold ? look.Accent2 : look.Backdrop,
                    radius: pack == StarterPack.CleanGeometric ? 20 : 0, skew: Bold ? -6 : null),
                Label($"{b}.mark", "Quote Mark", "“", 250, 130, 220, 280, 300, font: markFont, color: look.Accent,
                    align: TextHorizontalAlignment.Center, inset: 0),
                Label($"{b}.body", "Quote", "The quote goes here — long enough to wrap onto a second line, and it should still breathe.",
                    400, 330, 1120, 340, 56, font: Editorial ? "Georgia-Italic" : look.Body,
                    align: Bold ? TextHorizontalAlignment.Left : TextHorizontalAlignment.Center, inset: 0, lineHeight: 1.15),
                Block($"{b}.rule", "Rule", Bold ? 400 : 880, 700, 160, 4, color: look.Accent, radius: 0),
                Label($"{b}.source", "Source", "Attribution", 400, 716, 1120, 60, 30, color: look.Accent,
                    align: Bold ? TextHorizontalAlignment.Left : TextHorizontalAlignment.Center, tracking: KickerTracking,
                    uppercase: !Editorial, inset: 0),
            };
            if (pack == StarterPack.Digital)
                objects.Insert(2, Label($"{b}.mark2", "Quote Mark Shadow", "“", 262, 138, 220, 280, 300, font: markFont,
                    color: look.Accent2, align: TextHorizontalAlignment.Center, inset: 0));
            return ThemeSlide("quote", "Quote", objects, fullScreen: true);
        }
    }

    /// <summary>
    /// The Mac builds lower/side thirds with the text first (so it is the
    /// placeholder) but draws it above the band; reorder so the band sits under it.
    /// </summary>
    private static Slide WithTextOnTop(this Slide slide)
    {
        var text = slide.Objects.Where(o => o.ObjectKind == SlideObjectKind.Text).ToList();
        slide.Objects = slide.Objects.Where(o => o.ObjectKind != SlideObjectKind.Text).Concat(text).ToList();
        return slide;
    }
}
