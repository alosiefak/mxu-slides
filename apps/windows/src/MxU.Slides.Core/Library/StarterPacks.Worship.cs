using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Library;

// The four worship looks Windows adds to the starter packs: Arena (big, bold, in capitals),
// Broadcast (built for the livestream's lower third), Modern Serif (refined, editorial) and
// Minimal Sans (quiet and modern). Each has the same set of designs as the Mac's four
// (Lyrics, its lower and side thirds, Verse + Reference, Name + Title, Point(s), Quote, Section
// Title, Call to Action…) plus a pre-service Countdown, and the same eleven overlays.
//
// Everything here is ordinary shared-format content: fonts named by their PostScript names (the
// bundled ones, which travel with an exported deck), the step kinds the Mac already plays. The one
// Windows-only part is the kinetic build on the lyrics: the theme itself carries a whole-text rise
// the Mac plays, and KineticBuilds() lists the line-by-line builds the seeder saves beside it as
// Windows extras (see KineticExtras), so on Windows the lines rise in one after another.
public static partial class StarterPacks
{
    /// <summary>True for the looks Windows adds (built here); the first four packs are the Mac's own.</summary>
    public static bool IsWorshipLook(this StarterPack pack) => pack >= StarterPack.Arena;

    /// <summary>
    /// The kinetic builds a worship look's designs carry on Windows: each placeholder (as the theme
    /// makes it, with its shared steps) and the build to save for it. None for the Mac's packs.
    /// </summary>
    public static IReadOnlyList<(SlideObject Placeholder, KineticBuild Build)> KineticBuilds(this StarterPack pack)
    {
        if (!pack.IsWorshipLook()) return [];
        var builder = new WorshipBuilder(pack);
        builder.MakeTheme();
        return builder.Kinetic;
    }

    /// <summary>
    /// How a look's words arrive. Each is one step per object (or per line): two Ins on one object
    /// would play together, but the Mac's editor flags the second as "already shown", so they're avoided.
    /// </summary>
    private enum Motion
    {
        /// <summary>A clean rise with a fade.</summary>
        Rise,

        /// <summary>A long, weighty rise: the arena look.</summary>
        BigRise,

        /// <summary>A slow focus pull out of a blur: refined, for the serif look.</summary>
        Focus,
    }

    /// <summary>
    /// A worship look: its fonts (PostScript names of the bundled fonts), colours and motion.
    /// <paramref name="Soft"/> is the secondary ink (titles, small print); <paramref name="Plate"/> and
    /// <paramref name="Plate2"/> the panels' colours; <paramref name="Rise"/> how far words travel (canvas
    /// units); <paramref name="Stagger"/> how far apart lines start in the kinetic build.
    /// </summary>
    private sealed record WorshipLook(
        string Lyric, string Display, string Body, string Kicker, string Quote, string Title,
        string Ink, string Soft, string Accent, string InkOnAccent, string Plate, string Plate2, string Backdrop,
        bool Caps, bool Centered, double LyricSize, double LyricLine, double LyricTracking, double KickerTracking,
        double Radius, Motion Motion, double Rise, double Stagger, string Shadow);

    /// <summary>The countdown digits: Bebas Neue's figures are all the same width, so a ticking clock never shifts.</summary>
    private const string CountdownDigits = "BebasNeue-Regular";

    private static WorshipLook WorshipLookOf(StarterPack pack) => pack switch
    {
        StarterPack.Arena => new(
            Lyric: "Montserrat-Black", Display: "Montserrat-Black", Body: "Montserrat-SemiBold", Kicker: "Montserrat-Bold",
            Quote: "Montserrat-Bold", Title: "BebasNeue-Regular",
            Ink: "#FFFFFFFF", Soft: "#FFFFFFBF", Accent: "#FFB547FF", InkOnAccent: "#0A0A0AFF",
            Plate: "#050608EB", Plate2: "#0E1116EB", Backdrop: "#060709FF",
            Caps: true, Centered: true, LyricSize: 100, LyricLine: 1.02, LyricTracking: 1.5, KickerTracking: 12,
            Radius: 0, Motion: Motion.BigRise, Rise: 70, Stagger: 0.2, Shadow: "#000000B3"),
        StarterPack.Broadcast => new(
            Lyric: "Poppins-SemiBold", Display: "Poppins-Bold", Body: "Poppins-Medium", Kicker: "Poppins-SemiBold",
            Quote: "Poppins-Medium", Title: "Poppins-Bold",
            Ink: "#FFFFFFFF", Soft: "#FFFFFFC4", Accent: "#FF5A4EFF", InkOnAccent: "#FFFFFFFF",
            Plate: "#0B0E14F2", Plate2: "#161B26F2", Backdrop: "#0A0C10FF",
            Caps: false, Centered: true, LyricSize: 72, LyricLine: 1.18, LyricTracking: 0, KickerTracking: 5,
            Radius: 6, Motion: Motion.Rise, Rise: 30, Stagger: 0.16, Shadow: "#00000099"),
        StarterPack.ModernSerif => new(
            Lyric: "DMSerifDisplay-Regular", Display: "DMSerifDisplay-Regular", Body: "Montserrat-Regular", Kicker: "Montserrat-SemiBold",
            Quote: "DMSerifDisplay-Italic", Title: "DMSerifDisplay-Regular",
            Ink: "#F6F0E6FF", Soft: "#F6F0E6B8", Accent: "#D8B47AFF", InkOnAccent: "#15120EFF",
            Plate: "#110F0CE6", Plate2: "#1E1A15E6", Backdrop: "#0F0D0BFF",
            Caps: false, Centered: true, LyricSize: 86, LyricLine: 1.12, LyricTracking: 0, KickerTracking: 10,
            Radius: 0, Motion: Motion.Focus, Rise: 22, Stagger: 0.32, Shadow: "#00000080"),
        _ => new(
            Lyric: "Poppins-Medium", Display: "Poppins-SemiBold", Body: "Poppins-Regular", Kicker: "Poppins-Medium",
            Quote: "Poppins-Italic", Title: "Poppins-SemiBold",
            Ink: "#FFFFFFFF", Soft: "#FFFFFFA8", Accent: "#A9BCFFFF", InkOnAccent: "#0B0D12FF",
            Plate: "#0C0D10D9", Plate2: "#16181DD9", Backdrop: "#0B0C0EFF",
            Caps: false, Centered: false, LyricSize: 66, LyricLine: 1.3, LyricTracking: 0, KickerTracking: 6,
            Radius: 14, Motion: Motion.Rise, Rise: 26, Stagger: 0.14, Shadow: "#00000073"),
    };

    private sealed class WorshipBuilder(StarterPack pack)
    {
        private readonly WorshipLook look = WorshipLookOf(pack);

        /// <summary>The kinetic builds the theme's designs asked for while being made (KineticBuilds).</summary>
        public List<(SlideObject Placeholder, KineticBuild Build)> Kinetic { get; } = [];

        private string ThemeId => pack.ThemeId();
        private bool Arena => pack == StarterPack.Arena;
        private bool Broadcast => pack == StarterPack.Broadcast;
        private bool Serif => pack == StarterPack.ModernSerif;
        private bool Minimal => pack == StarterPack.MinimalSans;
        private TextHorizontalAlignment Align => look.Centered ? TextHorizontalAlignment.Center : TextHorizontalAlignment.Left;

        // MARK: Building blocks

        private static ObjectFill Fill(string hex) => new() { FillKind = FillKind.Solid, ColorHex = hex };

        private static ObjectFill Gradient(double angle, params (string Hex, double At)[] stops) => new()
        {
            FillKind = FillKind.LinearGradient,
            GradientAngleDegrees = angle,
            GradientStops = [.. stops.Select(s => new GradientStop { ColorHex = s.Hex, Position = s.At })],
        };

        /// <summary>A band that fades from clear to the look's plate: the soft floor lower-third words sit on.</summary>
        private ObjectFill Floor(double angle = 90) => Gradient(angle, (WithAlpha(look.Plate, 0), 0), (WithAlpha(look.Plate, 0.72), 0.45), (look.Plate, 1));

        private static string WithAlpha(string hex, double alpha) => hex[..7] + ((int)Math.Round(alpha * 255)).ToString("X2");

        private ObjectShadow TextShadow(double blur = 26, double y = 4) => new() { ColorHex = look.Shadow, BlurRadius = blur, OffsetX = 0, OffsetY = y };

        private SlideObject Block(string id, string name, double x, double y, double w, double h, ObjectFill? fill = null, string? color = null,
            double? radius = null, ObjectStroke? stroke = null, ObjectShadow? shadow = null, bool ellipse = false, List<AnimationStep>? steps = null)
        {
            var r = radius ?? look.Radius;
            return new SlideObject
            {
                Id = id, ObjectKind = SlideObjectKind.Shape, Name = name, Text = "", X = x, Y = y, Width = w, Height = h,
                ShapeKind = ellipse ? ShapeKind.Ellipse : r > 0 ? ShapeKind.RoundedRectangle : ShapeKind.Rectangle, CornerRadius = r,
                Fill = fill ?? Fill(color ?? look.Plate), Stroke = stroke, Shadow = shadow,
                AnimationSteps = steps is { Count: > 0 } ? steps : null,
            };
        }

        private SlideObject Label(string id, string name, string text, double x, double y, double w, double h, double size, string font,
            string? color = null, TextHorizontalAlignment align = TextHorizontalAlignment.Left, TextVerticalAlignment valign = TextVerticalAlignment.Middle,
            double tracking = 0, bool caps = false, double lineHeight = 1, bool shrink = false, bool page = false, ObjectShadow? shadow = null,
            TextLink? link = null, bool balanced = true, List<AnimationStep>? steps = null) => new()
        {
            Id = id, ObjectKind = SlideObjectKind.Text, Name = name, Text = text, X = x, Y = y, Width = w, Height = h,
            TextLink = link,
            TextStyle = new TextStyle
            {
                FontName = font, FontSize = size, ColorHex = color ?? look.Ink, Tracking = tracking, LineHeightMultiple = lineHeight,
                HorizontalAlignment = align, VerticalAlignment = valign, TextTransform = caps ? TextTransform.Uppercase : null,
                // Text that shrinks to fit stops at three quarters of its size; past that a third pages instead of turning tiny.
                AutoShrink = shrink ? true : null, MinFontSize = shrink ? Math.Round(size * 0.75) : null, PageOnClick = page ? true : null, Shadow = shadow,
                // Even lines: no word left alone on the last line of a lyric (the Mac's balanced wrap).
                BalancedWrap = balanced && link is null ? true : null,
                TabularFigures = link is { Source: TextSourceKind.Timer } ? true : null,
                InsetLeft = 0, InsetRight = 0,
            },
            AnimationSteps = steps is { Count: > 0 } ? steps : null,
        };

        private static AnimationStep Step(string id, AnimationKind kind, StepAnimation animation, AnimationTrigger trigger, double? delay = null,
            double duration = 0.5, AnimationRamp? ramp = null, bool? fade = null, AnimationEdge? edge = null, double? dx = null, double? dy = null,
            double? fromScale = null, double? amount = null, double? softEdge = null, bool? cursor = null, double? drawStart = null) => new()
        {
            Id = id, Kind = kind, Animation = animation, Trigger = trigger, DelaySeconds = delay, DurationSeconds = duration, Ramp = ramp,
            WithFade = fade, Edge = edge, OffsetX = dx ?? (dy is null ? null : 0), OffsetY = dy ?? (dx is null ? null : 0),
            FromScale = fromScale, Amount = amount, SoftEdge = softEdge, Cursor = cursor, DrawStart = drawStart,
        };

        // MARK: Motion

        /// <summary>
        /// Words arriving, the look's way: a rise, a long weighty rise, or a slow focus pull. Text
        /// always floats in place (an offset, never "from the screen's edge"). A list, so a design
        /// can splice it in with the steps around it.
        /// </summary>
        private List<AnimationStep> TextIn(string id, AnimationTrigger trigger = AnimationTrigger.AfterPrevious, double? delay = null, double? rise = null)
        {
            var distance = rise ?? look.Rise;
            double? lag = trigger == AnimationTrigger.AfterPrevious ? Math.Max(delay ?? 0, -0.6) : delay;
            return look.Motion switch
            {
                Motion.Focus => [Step(id, AnimationKind.In, StepAnimation.Blur, trigger, lag, 1.1, AnimationRamp.Out, true, amount: Math.Max(distance, 16))],
                Motion.BigRise => [Step(id, AnimationKind.In, StepAnimation.Move, trigger, lag, 0.85, AnimationRamp.Out, true, dy: distance)],
                _ => [Step(id, AnimationKind.In, StepAnimation.Move, trigger, lag, 0.6, AnimationRamp.Out, true, dy: distance)],
            };
        }

        /// <summary>Words leaving: a short lift and fade (a blur for the serif look).</summary>
        private List<AnimationStep> TextOut(string id, AnimationTrigger trigger = AnimationTrigger.OnClick, double? delay = null) => look.Motion switch
        {
            Motion.Focus => [Step(id, AnimationKind.Out, StepAnimation.Blur, trigger, delay, 0.6, AnimationRamp.In, true, amount: 16)],
            Motion.BigRise => [Step(id, AnimationKind.Out, StepAnimation.Move, trigger, delay, 0.45, AnimationRamp.In, true, dy: -28)],
            _ => [Step(id, AnimationKind.Out, StepAnimation.Move, trigger, delay, 0.4, AnimationRamp.In, true, dy: -look.Rise / 2)],
        };

        /// <summary>A panel arriving: a soft-edged wipe (broadcast and arena), a fade (serif), a gentle settle (minimal).</summary>
        private List<AnimationStep> PlateIn(string id, AnimationTrigger trigger = AnimationTrigger.WithPrevious, double? delay = null,
            AnimationEdge edge = AnimationEdge.Left) => pack switch
        {
            StarterPack.Arena => [Step(id, AnimationKind.In, StepAnimation.Wipe, trigger, delay, 0.7, AnimationRamp.Out, edge: edge, softEdge: 120)],
            StarterPack.Broadcast => [Step(id, AnimationKind.In, StepAnimation.Wipe, trigger, delay, 0.5, AnimationRamp.Out, edge: edge, softEdge: 40)],
            StarterPack.ModernSerif => [Step(id, AnimationKind.In, StepAnimation.Fade, trigger, delay, 0.9, AnimationRamp.Out)],
            _ => [Step(id, AnimationKind.In, StepAnimation.Scale, trigger, delay, 0.55, AnimationRamp.Out, true, fromScale: 0.97)],
        };

        private List<AnimationStep> PlateOut(string id, AnimationTrigger trigger = AnimationTrigger.AfterPrevious, double? delay = -0.1,
            AnimationEdge edge = AnimationEdge.Left) => pack switch
        {
            StarterPack.Arena => [Step(id, AnimationKind.Out, StepAnimation.Wipe, trigger, delay, 0.5, AnimationRamp.In, edge: edge, softEdge: 120)],
            StarterPack.Broadcast => [Step(id, AnimationKind.Out, StepAnimation.Wipe, trigger, delay, 0.4, AnimationRamp.In, edge: edge, softEdge: 40)],
            StarterPack.ModernSerif => [Step(id, AnimationKind.Out, StepAnimation.Fade, trigger, delay, 0.6, AnimationRamp.In)],
            _ => [Step(id, AnimationKind.Out, StepAnimation.Scale, trigger, delay, 0.4, AnimationRamp.In, true, fromScale: 0.97)],
        };

        private static List<AnimationStep> RuleIn(string id, AnimationTrigger trigger = AnimationTrigger.AfterPrevious, double? delay = -0.3,
            AnimationEdge edge = AnimationEdge.Left, double duration = 0.6) =>
            [Step(id, AnimationKind.In, StepAnimation.Wipe, trigger, delay, duration, AnimationRamp.Out, edge: edge, softEdge: 0)];

        private static List<AnimationStep> RuleOut(string id, AnimationTrigger trigger = AnimationTrigger.WithPrevious, double? delay = null,
            AnimationEdge edge = AnimationEdge.Left) =>
            [Step(id, AnimationKind.Out, StepAnimation.Wipe, trigger, delay, 0.35, AnimationRamp.In, edge: edge, softEdge: 0)];

        private static List<AnimationStep> FadeIn(string id, AnimationTrigger trigger = AnimationTrigger.WithPrevious, double? delay = null, double duration = 0.6) =>
            [Step(id, AnimationKind.In, StepAnimation.Fade, trigger, delay, duration, AnimationRamp.Out)];

        private static List<AnimationStep> FadeOut(string id, AnimationTrigger trigger = AnimationTrigger.WithPrevious, double? delay = null, double duration = 0.45) =>
            [Step(id, AnimationKind.Out, StepAnimation.Fade, trigger, delay, duration, AnimationRamp.In)];

        private static List<AnimationStep> PopIn(string id, AnimationTrigger trigger = AnimationTrigger.AfterPrevious, double? delay = -0.2, double from = 0.6) =>
            [Step(id, AnimationKind.In, StepAnimation.Scale, trigger, delay, 0.5, AnimationRamp.Out, true, fromScale: from)];

        private static List<AnimationStep> PopOut(string id, AnimationTrigger trigger = AnimationTrigger.WithPrevious, double? delay = null) =>
            [Step(id, AnimationKind.Out, StepAnimation.Scale, trigger, delay, 0.35, AnimationRamp.In, true, fromScale: 0.8)];

        private static List<string> Ids(params List<AnimationStep>[] groups) => [.. groups.SelectMany(g => g.Select(s => s.Id))];

        /// <summary>Asks for the line-by-line build on a lyric placeholder (saved as a Windows extra by the seeder).</summary>
        private void Kinetically(SlideObject placeholder, string id)
        {
            var templates = TextIn($"{id}.kinetic", AnimationTrigger.OnClick);
            Kinetic.Add((placeholder, new KineticBuild(KineticSplit.Lines, look.Stagger, templates)));
        }

        // MARK: Theme

        public Theme MakeTheme()
        {
            Slide FromOverlay(Overlay overlay, string item, string name, string folder) => new()
            {
                Id = $"{ThemeId}.{item}", Name = name, Objects = overlay.Objects, AnimationOrder = overlay.AnimationOrder, Folder = folder,
            };
            var slides = new List<Slide>
            {
                LyricsSlide(), PointsSlide(), SectionTitleSlide(), VerseSlide(), PointSlide(), QuoteSlide(),
                CallToActionSlide(), CountdownSlide(),
                FromOverlay(PointLowerThird($"{ThemeId}.lowerDefault", simple: true), "lowerDefault", "Lower Third", "Lower Thirds"),
                FromOverlay(LyricsLowerThird($"{ThemeId}.lyricsLower"), "lyricsLower", "Lyrics (Lower Third)", "Lower Thirds"),
                FromOverlay(NamePlate($"{ThemeId}.name", timed: false), "name", "Name + Title", "Lower Thirds"),
                FromOverlay(VerseLowerThird($"{ThemeId}.verseLower"), "verseLower", "Verse + Reference (Lower Third)", "Lower Thirds"),
                FromOverlay(PointLowerThird($"{ThemeId}.pointLower"), "pointLower", "Point (Lower Third)", "Lower Thirds"),
                FromOverlay(CallToActionLower($"{ThemeId}.ctaLower"), "ctaLower", "Call to Action (Lower Third)", "Lower Thirds"),
                FromOverlay(PointSideThird($"{ThemeId}.sideDefault", simple: true), "sideDefault", "Side Third", "Side Thirds"),
                FromOverlay(LyricsSideThird($"{ThemeId}.lyricsSide"), "lyricsSide", "Lyrics (Side Third)", "Side Thirds"),
                FromOverlay(PointSideThird($"{ThemeId}.pointSide"), "pointSide", "Point (Side Third)", "Side Thirds"),
                FromOverlay(VerseSideThird($"{ThemeId}.verseSide"), "verseSide", "Verse + Reference (Side Third)", "Side Thirds"),
                FromOverlay(CallToActionSide($"{ThemeId}.ctaSide"), "ctaSide", "Call to Action (Side Third)", "Side Thirds"),
            };
            return new Theme
            {
                Id = ThemeId, Name = pack.Title(), FontFamily = look.Body, FontSize = 72,
                TextColorHex = look.Ink, BackgroundColorHex = look.Backdrop, Slides = slides,
            };
        }

        public List<Overlay> MakeOverlays()
        {
            Overlay Group(Overlay overlay, string folder)
            {
                overlay.Folder = $"{pack.Title()}/{folder}";
                return overlay;
            }
            return
            [
                Group(NamePlate(pack.OverlayId("name"), timed: false), "Lower Thirds"),
                Group(NamePlate(pack.OverlayId("nameTimed"), timed: true), "Lower Thirds"),
                Group(VerseLowerThird(pack.OverlayId("scriptureVerse")), "Lower Thirds"),
                Group(PointLowerThird(pack.OverlayId("point")), "Lower Thirds"),
                Group(CallToActionLower(pack.OverlayId("ctaLower")), "Lower Thirds"),
                Group(Ticker(), "Lower Thirds"),
                Group(VerseSideThird(pack.OverlayId("verseSide")), "Side Thirds"),
                Group(PointSideThird(pack.OverlayId("pointSide")), "Side Thirds"),
                Group(CallToActionSide(pack.OverlayId("cta")), "Side Thirds"),
                Group(SearchCard(), "Full Screen"),
                Group(CinematicBars(), "Full Screen"),
            ];
        }

        private Slide FullSlide(string item, string name, List<SlideObject> objects, List<string> order) => new()
        {
            Id = $"{ThemeId}.{item}", Name = name, Objects = objects, BackgroundFill = Fill(look.Backdrop), AnimationOrder = order, Folder = "Full Slide",
        };

        // MARK: Full-slide designs

        private const string SampleLyrics = "Amazing grace, how sweet the sound\nThat saved a wretch like me\nI once was lost, but now am found\nWas blind, but now I see";

        private Slide LyricsSlide()
        {
            var b = $"{ThemeId}.lyrics";
            var textIn = TextIn($"{b}.text.in", AnimationTrigger.WithPrevious, 0);
            var textOut = TextOut($"{b}.text.out");
            var (x, y, w, h) = pack switch
            {
                StarterPack.Arena => (140.0, 140.0, 1640.0, 800.0),
                StarterPack.MinimalSans => (192.0, 190.0, 1400.0, 700.0),
                _ => (180.0, 160.0, 1560.0, 760.0),
            };
            var text = Label($"{b}.text", "Lyrics", SampleLyrics, x, y, w, h, look.LyricSize, look.Lyric, align: Align,
                tracking: look.LyricTracking, caps: look.Caps, lineHeight: look.LyricLine, shrink: true, shadow: TextShadow(), steps: [.. textIn, .. textOut]);
            Kinetically(text, b);
            var objects = new List<SlideObject> { text };
            var order = Ids(textIn, textOut);
            if (Broadcast)
            {
                // The broadcast look's small brand mark under the words.
                var ruleIn = RuleIn($"{b}.rule.in", delay: -0.4, edge: AnimationEdge.Left, duration: 0.5);
                var ruleOut = RuleOut($"{b}.rule.out");
                objects.Add(Block($"{b}.rule", "Rule", 912, 940, 96, 6, color: look.Accent, radius: 3, steps: [.. ruleIn, .. ruleOut]));
                order = Ids(textIn, ruleIn, textOut, ruleOut);
            }
            return FullSlide("lyrics", "Lyrics", objects, order);
        }

        private Slide PointsSlide()
        {
            var b = $"{ThemeId}.points";
            var kickIn = TextIn($"{b}.kicker.in", AnimationTrigger.WithPrevious, 0, rise: 16);
            var kickOut = FadeOut($"{b}.kicker.out");
            var ruleIn = RuleIn($"{b}.rule.in");
            var ruleOut = RuleOut($"{b}.rule.out");
            var textIn = TextIn($"{b}.text.in", AnimationTrigger.AfterPrevious, -0.3);
            var textOut = TextOut($"{b}.text.out");
            var size = Arena ? 70 : Serif ? 74 : Broadcast ? 64 : 60;
            var points = Label($"{b}.text", "Points", "First point\nSecond point\nThird point", 192, 300, 1536, 600, size,
                Arena ? "Montserrat-ExtraBold" : look.Lyric, caps: Arena, tracking: Arena ? 1 : 0,
                lineHeight: Arena ? 1.3 : 1.4, valign: TextVerticalAlignment.Top, shadow: TextShadow(18, 3), steps: [.. textIn, .. textOut]);
            Kinetically(points, b);
            return FullSlide("points", "Points",
            [
                points,
                Label($"{b}.kicker", "Kicker", "Today's message", 192, 180, 1536, 60, Arena ? 30 : 28, look.Kicker, look.Accent,
                    tracking: look.KickerTracking, caps: true, steps: [.. kickIn, .. kickOut]),
                Block($"{b}.rule", "Rule", 192, 250, 120, Arena ? 6 : 3, color: look.Accent, radius: 0, steps: [.. ruleIn, .. ruleOut]),
            ], Ids(kickIn, ruleIn, textIn, textOut, kickOut, ruleOut));
        }

        private Slide SectionTitleSlide()
        {
            var b = $"{ThemeId}.section";
            var kickIn = TextIn($"{b}.kicker.in", AnimationTrigger.WithPrevious, 0, rise: 14);
            var kickOut = FadeOut($"{b}.kicker.out");
            var titleIn = TextIn($"{b}.in", AnimationTrigger.AfterPrevious, -0.35);
            var titleOut = TextOut($"{b}.out");
            var ruleIn = RuleIn($"{b}.rule.in", delay: -0.35, edge: Minimal ? AnimationEdge.Left : AnimationEdge.Right);
            var ruleOut = RuleOut($"{b}.rule.out");
            var center = !Minimal;
            var align = center ? TextHorizontalAlignment.Center : TextHorizontalAlignment.Left;
            var (titleFont, titleSize, titleTracking) = pack switch
            {
                StarterPack.Arena => ("BebasNeue-Regular", 250.0, 6.0),
                StarterPack.Broadcast => ("Poppins-Bold", 132.0, 0.0),
                StarterPack.ModernSerif => ("DMSerifDisplay-Regular", 150.0, 0.0),
                _ => ("Poppins-SemiBold", 124.0, -1.0),
            };
            var title = Label($"{b}.text", "Section Title", "Section Title", center ? 160 : 192, 370, center ? 1600 : 1536, 300, titleSize, titleFont,
                align: align, tracking: titleTracking, caps: Arena, lineHeight: 0.95, shadow: TextShadow(30, 5), steps: [.. titleIn, .. titleOut]);
            var kicker = Serif
                ? Label($"{b}.kicker", "Kicker", "Part one", 160, 300, 1600, 70, 46, "DMSerifDisplay-Italic", look.Accent, align, steps: [.. kickIn, .. kickOut])
                : Label($"{b}.kicker", "Kicker", "Part one", center ? 160 : 192, 320, center ? 1600 : 1536, 50, 28, look.Kicker, look.Accent, align,
                    tracking: look.KickerTracking, caps: true, steps: [.. kickIn, .. kickOut]);
            var rule = center
                ? Block($"{b}.rule", "Rule", Serif ? 900 : 880, 700, Serif ? 120 : 160, Serif ? 1.5 : Arena ? 6 : 4, color: look.Accent, radius: 0, steps: [.. ruleIn, .. ruleOut])
                : Block($"{b}.rule", "Rule", 192, 690, 96, 4, color: look.Accent, radius: 2, steps: [.. ruleIn, .. ruleOut]);
            return FullSlide("section", "Section Title", [title, kicker, rule], Ids(kickIn, titleIn, ruleIn, titleOut, kickOut, ruleOut));
        }

        private Slide VerseSlide()
        {
            var b = $"{ThemeId}.verse";
            var verseIn = TextIn($"{b}.verse.in", AnimationTrigger.WithPrevious, 0);
            var verseOut = TextOut($"{b}.verse.out");
            var ruleIn = RuleIn($"{b}.rule.in", delay: -0.4, edge: look.Centered ? AnimationEdge.Right : AnimationEdge.Left, duration: 0.5);
            var ruleOut = RuleOut($"{b}.rule.out");
            var refIn = TextIn($"{b}.ref.in", AnimationTrigger.WithPrevious, 0.15, rise: 14);
            var refOut = FadeOut($"{b}.ref.out");
            const string verse = "For God so loved the world, that he gave his only Son, that whoever believes in him should not perish but have eternal life.";
            var objects = new List<SlideObject>();
            List<string> order;
            if (Broadcast)
            {
                var barIn = RuleIn($"{b}.bar.in", AnimationTrigger.WithPrevious, null, AnimationEdge.Top, 0.6);
                var barOut = RuleOut($"{b}.bar.out", edge: AnimationEdge.Top);
                verseIn[0].Trigger = AnimationTrigger.AfterPrevious;
                verseIn[0].DelaySeconds = -0.35;
                objects.Add(Label($"{b}.verse", "Verse", verse, 240, 250, 1450, 470, 58, "Poppins-Medium", lineHeight: 1.22,
                    valign: TextVerticalAlignment.Bottom, shadow: TextShadow(18, 3), steps: [.. verseIn, .. verseOut]));
                objects.Add(Block($"{b}.bar", "Bar", 192, 260, 8, 560, color: look.Accent, radius: 4, steps: [.. barIn, .. barOut]));
                objects.Add(Block($"{b}.rule", "Rule", 240, 752, 64, 4, color: look.Accent, radius: 2, steps: [.. ruleIn, .. ruleOut]));
                objects.Add(Label($"{b}.ref", "Reference", "John 3:16 (ESV)", 240, 772, 1450, 50, 30, "Poppins-SemiBold", look.Accent,
                    tracking: 3, caps: true, steps: [.. refIn, .. refOut]));
                order = Ids(barIn, verseIn, ruleIn, refIn, verseOut, refOut, ruleOut, barOut);
                return FullSlide("verse", "Verse + Reference", objects, order);
            }
            var (font, size, line) = pack switch
            {
                StarterPack.Arena => ("Montserrat-SemiBold", 60.0, 1.2),
                StarterPack.ModernSerif => ("DMSerifDisplay-Italic", 68.0, 1.14),
                _ => ("Poppins-Regular", 56.0, 1.32),
            };
            var center = look.Centered;
            objects.Add(Label($"{b}.verse", "Verse", verse, center ? 220 : 192, 220, center ? 1480 : 1400, 500, size, font, align: Align,
                lineHeight: line, valign: TextVerticalAlignment.Bottom, shadow: TextShadow(20, 3), steps: [.. verseIn, .. verseOut]));
            objects.Add(Block($"{b}.rule", "Rule", center ? 920 : 192, 768, center ? 80 : 64, Serif ? 1.5 : 4, color: look.Accent, radius: 0, steps: [.. ruleIn, .. ruleOut]));
            objects.Add(Label($"{b}.ref", "Reference", "John 3:16 (ESV)", center ? 220 : 192, 790, center ? 1480 : 1400, 50, Serif ? 26 : 30, look.Kicker, look.Accent,
                align: Align, tracking: look.KickerTracking - 2, caps: true, steps: [.. refIn, .. refOut]));
            order = Ids(verseIn, ruleIn, refIn, verseOut, refOut, ruleOut);
            return FullSlide("verse", "Verse + Reference", objects, order);
        }

        private Slide PointSlide()
        {
            var b = $"{ThemeId}.point";
            var kickIn = TextIn($"{b}.kicker.in", AnimationTrigger.WithPrevious, 0, rise: 14);
            var kickOut = FadeOut($"{b}.kicker.out");
            var textIn = TextIn($"{b}.text.in", AnimationTrigger.AfterPrevious, -0.3);
            var textOut = TextOut($"{b}.text.out");
            var ruleIn = RuleIn($"{b}.rule.in", delay: -0.5);
            var ruleOut = RuleOut($"{b}.rule.out");
            var center = look.Centered;
            var (font, size, line) = pack switch
            {
                StarterPack.Arena => ("Montserrat-Black", 96.0, 1.02),
                StarterPack.Broadcast => ("Poppins-Bold", 84.0, 1.1),
                StarterPack.ModernSerif => ("DMSerifDisplay-Regular", 96.0, 1.06),
                _ => ("Poppins-SemiBold", 80.0, 1.15),
            };
            var text = Label($"{b}.text", "Point", "Faith is not the absence of fear — it is trust that outlasts it.",
                center ? 200 : 192, 330, center ? 1520 : 1450, 470, size, font, align: Align, caps: Arena, tracking: Arena ? 1 : 0,
                lineHeight: line, valign: TextVerticalAlignment.Top, shadow: TextShadow(), steps: [.. textIn, .. textOut]);
            return FullSlide("point", "Point",
            [
                text,
                Label($"{b}.kicker", "Kicker", "Point 1", center ? 200 : 192, 240, center ? 1520 : 1450, 50, 28, look.Kicker, look.Accent,
                    align: Align, tracking: look.KickerTracking, caps: true, steps: [.. kickIn, .. kickOut]),
                Block($"{b}.rule", "Rule", center ? 900 : 192, 300, center ? 120 : 96, Serif ? 1.5 : 4, color: look.Accent, radius: 0, steps: [.. ruleIn, .. ruleOut]),
            ], Ids(kickIn, textIn, ruleIn, textOut, kickOut, ruleOut));
        }

        private Slide QuoteSlide()
        {
            var b = $"{ThemeId}.quote";
            var markIn = PopIn($"{b}.mark.in", AnimationTrigger.WithPrevious, null, 0.7);
            var markOut = FadeOut($"{b}.mark.out", AnimationTrigger.AfterPrevious, -0.2);
            var bodyIn = TextIn($"{b}.body.in", AnimationTrigger.AfterPrevious, -0.25);
            var bodyOut = TextOut($"{b}.body.out");
            var ruleIn = RuleIn($"{b}.rule.in", delay: -0.5);
            var ruleOut = RuleOut($"{b}.rule.out");
            var sourceIn = TextIn($"{b}.source.in", AnimationTrigger.WithPrevious, 0.15, rise: 12);
            var sourceOut = FadeOut($"{b}.source.out");
            var center = look.Centered;
            var markFont = Serif ? "DMSerifDisplay-Regular" : Arena ? "Montserrat-Black" : "Poppins-Bold";
            var (font, size) = pack switch
            {
                StarterPack.Arena => ("Montserrat-Bold", 62.0),
                StarterPack.ModernSerif => ("DMSerifDisplay-Italic", 68.0),
                StarterPack.Broadcast => ("Poppins-Medium", 58.0),
                _ => ("Poppins-Regular", 56.0),
            };
            var body = Label($"{b}.body", "Quote", "The quote goes here — long enough to wrap onto a second line, and it should still breathe.",
                center ? 260 : 192, 360, center ? 1400 : 1400, 340, size, font, align: Align, caps: false, lineHeight: 1.18,
                valign: TextVerticalAlignment.Top, shadow: TextShadow(20, 3), steps: [.. bodyIn, .. bodyOut]);
            return FullSlide("quote", "Quote",
            [
                body,
                Label($"{b}.mark", "Quote Mark", "“", center ? 860 : 180, 120, 200, 260, Serif ? 300 : 260, markFont, look.Accent,
                    align: center ? TextHorizontalAlignment.Center : TextHorizontalAlignment.Left, steps: [.. markIn, .. markOut]),
                Block($"{b}.rule", "Rule", center ? 920 : 192, 742, center ? 80 : 64, Serif ? 1.5 : 4, color: look.Accent, radius: 0, steps: [.. ruleIn, .. ruleOut]),
                Label($"{b}.source", "Source", "Attribution", center ? 260 : 192, 764, 1400, 50, 26, look.Kicker, look.Accent, align: Align,
                    tracking: look.KickerTracking - 2, caps: true, steps: [.. sourceIn, .. sourceOut]),
            ], Ids(markIn, bodyIn, ruleIn, sourceIn, bodyOut, sourceOut, ruleOut, markOut));
        }

        private Slide CallToActionSlide()
        {
            var b = $"{ThemeId}.ctaFull";
            var cardIn = PlateIn($"{b}.card.in");
            var cardOut = PlateOut($"{b}.card.out");
            var headIn = TextIn($"{b}.head.in", AnimationTrigger.AfterPrevious, -0.35);
            var headOut = TextOut($"{b}.head.out");
            var bodyIn = TextIn($"{b}.body.in", AnimationTrigger.WithPrevious, 0.12, rise: 16);
            var bodyOut = FadeOut($"{b}.body.out");
            var pillIn = PopIn($"{b}.pill.in", delay: -0.3, from: 0.85);
            var pillOut = PopOut($"{b}.pill.out");
            var pillTextIn = FadeIn($"{b}.pilltext.in", delay: 0.1, duration: 0.4);
            var pillTextOut = FadeOut($"{b}.pilltext.out");
            const double cx = 360, cy = 250, cw = 1200, ch = 580;
            var pillW = 340.0;
            var pillX = look.Centered ? cx + (cw - pillW) / 2 : cx + 80;
            var align = Align;
            // The card goes first so it draws behind the words; the heading is still the first text (the slide's text lands there).
            return FullSlide("ctaFull", "Call to Action",
            [
                Block($"{b}.card", "Card", cx, cy, cw, ch, fill: Gradient(135, (look.Plate2, 0), (look.Plate, 1)), radius: Minimal ? 28 : look.Radius,
                    stroke: Serif ? new ObjectStroke { ColorHex = WithAlpha(look.Accent, 0.6), Width = 1.5 } : null,
                    shadow: new ObjectShadow { ColorHex = "#00000080", BlurRadius = 40, OffsetX = 0, OffsetY = 16 }, steps: [.. cardIn, .. cardOut]),
                Label($"{b}.head", "Heading", "Next Steps", cx + 80, cy + 80, cw - 160, 120, Arena ? 96 : 84, Arena ? "Montserrat-Black" : look.Display,
                    align: align, caps: Arena, steps: [.. headIn, .. headOut]),
                Label($"{b}.body", "Body", "Text NEXT to 555-0100 or stop by the Welcome Desk after the service.", cx + 80, cy + 220, cw - 160, 150,
                    40, look.Body, look.Soft, align, lineHeight: 1.25, valign: TextVerticalAlignment.Top, steps: [.. bodyIn, .. bodyOut]),
                Block($"{b}.pill", "Button", pillX, cy + ch - 150, pillW, 76, color: look.Accent, radius: Arena || Serif ? 0 : 38, steps: [.. pillIn, .. pillOut]),
                Label($"{b}.pilltext", "Button Label", "Text NEXT", pillX, cy + ch - 150, pillW, 76, 28, look.Kicker, look.InkOnAccent,
                    TextHorizontalAlignment.Center, tracking: 3, caps: true, steps: [.. pillTextIn, .. pillTextOut]),
            ], Ids(cardIn, headIn, bodyIn, pillIn, pillTextIn, headOut, bodyOut, pillTextOut, pillOut, cardOut));
        }

        /// <summary>
        /// The pre-service countdown: "Service starts in" over big digits linked to a timer (the one
        /// running, or the first, unless the box names one). The digits come from the design itself,
        /// so a slide only needs its two lines of words.
        /// </summary>
        private Slide CountdownSlide()
        {
            var b = $"{ThemeId}.countdown";
            var kickIn = TextIn($"{b}.kicker.in", AnimationTrigger.WithPrevious, 0, rise: 18);
            var kickOut = FadeOut($"{b}.kicker.out");
            // The digits come into focus (one In: a second on the same object reads as "already shown" on the Mac).
            var digitsIn = new List<AnimationStep>
            {
                Step($"{b}.digits.in", AnimationKind.In, StepAnimation.Blur, AnimationTrigger.AfterPrevious, -0.4, 1.0, AnimationRamp.Out, true, amount: 30),
            };
            var digitsOut = TextOut($"{b}.digits.out");
            var ruleIn = RuleIn($"{b}.rule.in", delay: -0.5, edge: AnimationEdge.Right);
            var ruleOut = RuleOut($"{b}.rule.out");
            var messageIn = TextIn($"{b}.message.in", AnimationTrigger.WithPrevious, 0.1, rise: 14);
            var messageOut = FadeOut($"{b}.message.out");
            var digits = Label($"{b}.digits", "Countdown", "5:00", 160, 330, 1600, 430, Serif ? 400 : Arena ? 440 : 400, CountdownDigits,
                align: TextHorizontalAlignment.Center, tracking: Arena ? 8 : 4, lineHeight: 0.9, shadow: TextShadow(40, 8),
                link: new TextLink { Source = TextSourceKind.Timer, TimerPattern = "MM:ss", UsesWarningColor = false },
                steps: [.. digitsIn, .. digitsOut]);
            // The small words sit on a busy background, so they get a soft shadow too.
            var kicker = Serif
                ? Label($"{b}.kicker", "Kicker", "Service starts in", 160, 238, 1600, 90, 58, "DMSerifDisplay-Italic", look.Accent,
                    TextHorizontalAlignment.Center, shadow: TextShadow(16, 2), steps: [.. kickIn, .. kickOut])
                : Label($"{b}.kicker", "Kicker", "Service starts in", 160, 252, 1600, 60, Arena ? 34 : 30, look.Kicker, look.Accent,
                    TextHorizontalAlignment.Center, tracking: Arena ? 16 : look.KickerTracking + 4, caps: true, shadow: TextShadow(16, 2), steps: [.. kickIn, .. kickOut]);
            return FullSlide("countdown", "Countdown",
            [
                kicker,
                digits,
                Block($"{b}.rule", "Rule", 900, 780, 120, Serif ? 1.5 : 4, color: look.Accent, radius: 0, steps: [.. ruleIn, .. ruleOut]),
                Label($"{b}.message", "Message", "Grab a coffee and find a seat. We're so glad you're here.", 260, 810, 1400, 60, 32, look.Body, look.Soft,
                    TextHorizontalAlignment.Center, tracking: 0.5, shadow: TextShadow(16, 2), steps: [.. messageIn, .. messageOut]),
            ], Ids(kickIn, digitsIn, ruleIn, messageIn, digitsOut, kickOut, messageOut, ruleOut));
        }

        // MARK: Lower thirds

        private Overlay LyricsLowerThird(string b)
        {
            var bandIn = Broadcast || Minimal ? PlateIn($"{b}.band.in") : FadeIn($"{b}.band.in", duration: 0.7);
            var bandOut = Broadcast || Minimal ? PlateOut($"{b}.band.out") : FadeOut($"{b}.band.out", AnimationTrigger.AfterPrevious, -0.1, 0.6);
            var textIn = TextIn($"{b}.text.in", AnimationTrigger.AfterPrevious, -0.45);
            var textOut = TextOut($"{b}.text.out");
            var objects = new List<SlideObject>();
            List<string> order;
            switch (pack)
            {
                case StarterPack.Arena:
                    objects.Add(Label($"{b}.text", "Lyrics", SampleLyrics, 192, 806, 1536, 196, 54, "Montserrat-ExtraBold", align: TextHorizontalAlignment.Center,
                        tracking: 1, caps: true, lineHeight: 1.06, shrink: true, page: true, shadow: TextShadow(22, 3), steps: [.. textIn, .. textOut]));
                    objects.Insert(0, Block($"{b}.band", "Band", 0, 560, 1920, 520, fill: Floor(), radius: 0, steps: [.. bandIn, .. bandOut]));
                    order = Ids(bandIn, textIn, textOut, bandOut);
                    break;
                case StarterPack.Broadcast:
                {
                    var barIn = RuleIn($"{b}.bar.in", AnimationTrigger.WithPrevious, 0.15, AnimationEdge.Top, 0.45);
                    var barOut = RuleOut($"{b}.bar.out", edge: AnimationEdge.Top);
                    objects.Add(Label($"{b}.text", "Lyrics", SampleLyrics, 206, 826, 1520, 164, 46, "Poppins-SemiBold", lineHeight: 1.16,
                        shrink: true, page: true, steps: [.. textIn, .. textOut]));
                    objects.Insert(0, Block($"{b}.band", "Plate", 150, 812, 1620, 192, fill: Gradient(0, (look.Plate, 0), (look.Plate2, 1)),
                        shadow: new ObjectShadow { ColorHex = "#00000066", BlurRadius = 24, OffsetX = 0, OffsetY = 8 }, steps: [.. bandIn, .. bandOut]));
                    objects.Insert(1, Block($"{b}.bar", "Accent Bar", 150, 812, 10, 192, color: look.Accent, radius: 0, steps: [.. barIn, .. barOut]));
                    order = Ids(bandIn, barIn, textIn, textOut, bandOut, barOut);
                    break;
                }
                case StarterPack.ModernSerif:
                {
                    var ruleIn = RuleIn($"{b}.rule.in", AnimationTrigger.WithPrevious, 0.3, AnimationEdge.Right, 0.8);
                    var ruleOut = RuleOut($"{b}.rule.out");
                    objects.Add(Label($"{b}.text", "Lyrics", SampleLyrics, 240, 836, 1440, 160, 52, "DMSerifDisplay-Regular", align: TextHorizontalAlignment.Center,
                        lineHeight: 1.08, shrink: true, page: true, shadow: TextShadow(20, 2), steps: [.. textIn, .. textOut]));
                    objects.Insert(0, Block($"{b}.band", "Band", 0, 600, 1920, 480, fill: Floor(), radius: 0, steps: [.. bandIn, .. bandOut]));
                    objects.Insert(1, Block($"{b}.rule", "Hairline", 890, 816, 140, 1.5, color: look.Accent, radius: 0, steps: [.. ruleIn, .. ruleOut]));
                    order = Ids(bandIn, ruleIn, textIn, textOut, bandOut, ruleOut);
                    break;
                }
                default:
                    objects.Add(Label($"{b}.text", "Lyrics", SampleLyrics, 236, 842, 1448, 136, 42, "Poppins-Medium", lineHeight: 1.2,
                        shrink: true, page: true, steps: [.. textIn, .. textOut]));
                    objects.Insert(0, Block($"{b}.band", "Plate", 192, 826, 1536, 168, color: look.Plate,
                        stroke: new ObjectStroke { ColorHex = "#FFFFFF1F", Width = 1 }, steps: [.. bandIn, .. bandOut]));
                    order = Ids(bandIn, textIn, textOut, bandOut);
                    break;
            }
            return new Overlay { Id = b, Name = "Lyrics (Lower Third)", Objects = objects, AnimationOrder = order };
        }

        private Overlay NamePlate(string b, bool timed)
        {
            var outTrigger = timed ? AnimationTrigger.AfterPrevious : AnimationTrigger.OnClick;
            double? outDelay = timed ? 6 : null;
            var nameIn = TextIn($"{b}.name.in", AnimationTrigger.AfterPrevious, -0.3);
            var nameOut = TextOut($"{b}.name.out", outTrigger, outDelay);
            var titleIn = TextIn($"{b}.title.in", AnimationTrigger.WithPrevious, 0.12, rise: 14);
            var titleOut = FadeOut($"{b}.title.out");
            var objects = new List<SlideObject>();
            List<string> order;
            switch (pack)
            {
                case StarterPack.Broadcast:
                {
                    var plateIn = PlateIn($"{b}.plate.in");
                    var plateOut = PlateOut($"{b}.plate.out", delay: -0.15);
                    var bandIn = PlateIn($"{b}.band.in", AnimationTrigger.WithPrevious, 0.1);
                    var bandOut = PlateOut($"{b}.band.out", AnimationTrigger.WithPrevious, 0.05);
                    objects =
                    [
                        Block($"{b}.plate", "Plate", 150, 826, 820, 100, fill: Gradient(0, (look.Plate, 0), (look.Plate2, 1)), radius: 0, steps: [.. plateIn, .. plateOut]),
                        Block($"{b}.band", "Title Band", 150, 926, 820, 52, color: look.Accent, radius: 0, steps: [.. bandIn, .. bandOut]),
                        Label($"{b}.name", "Name", "Full Name", 190, 826, 760, 100, 54, "Poppins-Bold", shrink: true, page: true, steps: [.. nameIn, .. nameOut]),
                        Label($"{b}.title", "Title", "Title or role", 190, 926, 760, 52, 24, "Poppins-SemiBold", look.InkOnAccent, tracking: 3, caps: true,
                            steps: [.. titleIn, .. titleOut]),
                    ];
                    order = Ids(plateIn, bandIn, nameIn, titleIn, nameOut, titleOut, bandOut, plateOut);
                    break;
                }
                case StarterPack.MinimalSans:
                {
                    var plateIn = PlateIn($"{b}.plate.in");
                    var plateOut = PlateOut($"{b}.plate.out", delay: -0.15);
                    var dotIn = PopIn($"{b}.dot.in", AnimationTrigger.WithPrevious, 0.2, 0.2);
                    var dotOut = PopOut($"{b}.dot.out");
                    objects =
                    [
                        Block($"{b}.plate", "Plate", 192, 832, 780, 138, color: look.Plate, stroke: new ObjectStroke { ColorHex = "#FFFFFF1F", Width = 1 },
                            steps: [.. plateIn, .. plateOut]),
                        Block($"{b}.dot", "Dot", 232, 868, 14, 14, color: look.Accent, ellipse: true, steps: [.. dotIn, .. dotOut]),
                        Label($"{b}.name", "Name", "Full Name", 262, 844, 680, 64, 46, "Poppins-SemiBold", shrink: true, page: true, steps: [.. nameIn, .. nameOut]),
                        Label($"{b}.title", "Title", "Title or role", 262, 906, 680, 44, 26, "Poppins-Regular", look.Soft, steps: [.. titleIn, .. titleOut]),
                    ];
                    order = Ids(plateIn, dotIn, nameIn, titleIn, nameOut, titleOut, dotOut, plateOut);
                    break;
                }
                default:
                {
                    // Arena and Modern Serif: words straight on the picture, over a soft floor, with an accent line.
                    var floorIn = FadeIn($"{b}.floor.in", duration: 0.7);
                    var floorOut = FadeOut($"{b}.floor.out", AnimationTrigger.AfterPrevious, -0.2, 0.6);
                    var ruleIn = RuleIn($"{b}.rule.in", AnimationTrigger.WithPrevious, 0.1, duration: 0.6);
                    var ruleOut = RuleOut($"{b}.rule.out");
                    objects =
                    [
                        Block($"{b}.floor", "Floor", 0, 640, 1920, 440, fill: Floor(), radius: 0, steps: [.. floorIn, .. floorOut]),
                        Block($"{b}.rule", "Accent", 192, Arena ? 832 : 968, Arena ? 72 : 240, Arena ? 6 : 1.5, color: look.Accent, radius: 0, steps: [.. ruleIn, .. ruleOut]),
                        Label($"{b}.name", "Name", "Full Name", 192, 846, 1200, 86, Arena ? 68 : 70, Arena ? "Montserrat-Black" : "DMSerifDisplay-Regular",
                            caps: Arena, tracking: Arena ? 1 : 0, shrink: true, page: true, shadow: TextShadow(20, 3), steps: [.. nameIn, .. nameOut]),
                        Label($"{b}.title", "Title", "Title or role", 192, Arena ? 926 : 918, 1200, 44, Arena ? 26 : 24, look.Kicker, look.Accent,
                            tracking: look.KickerTracking - 2, caps: true, shadow: TextShadow(14, 2), steps: [.. titleIn, .. titleOut]),
                    ];
                    order = Ids(floorIn, ruleIn, nameIn, titleIn, nameOut, titleOut, ruleOut, floorOut);
                    break;
                }
            }
            return new Overlay { Id = b, Name = timed ? "Name + Title (timed)" : "Name + Title", Objects = objects, AnimationOrder = order };
        }

        private Overlay VerseLowerThird(string b)
        {
            var bandIn = Broadcast || Minimal ? PlateIn($"{b}.band.in") : FadeIn($"{b}.band.in", duration: 0.7);
            var bandOut = Broadcast || Minimal ? PlateOut($"{b}.band.out") : FadeOut($"{b}.band.out", AnimationTrigger.AfterPrevious, -0.1, 0.6);
            var verseIn = TextIn($"{b}.verse.in", AnimationTrigger.AfterPrevious, -0.45);
            var verseOut = TextOut($"{b}.verse.out");
            var refIn = TextIn($"{b}.ref.in", AnimationTrigger.WithPrevious, 0.2, rise: 12);
            var refOut = FadeOut($"{b}.ref.out");
            const string verse = "For God so loved the world, that he gave his only Son, that whoever believes in him should not perish but have eternal life.";
            List<SlideObject> objects;
            List<string> order;
            switch (pack)
            {
                case StarterPack.Broadcast:
                {
                    var barIn = RuleIn($"{b}.bar.in", AnimationTrigger.WithPrevious, 0.15, AnimationEdge.Top, 0.45);
                    var barOut = RuleOut($"{b}.bar.out", edge: AnimationEdge.Top);
                    var tagIn = PlateIn($"{b}.tag.in", AnimationTrigger.AfterPrevious, -0.3, AnimationEdge.Right);
                    var tagOut = PlateOut($"{b}.tag.out", AnimationTrigger.WithPrevious, null, AnimationEdge.Right);
                    objects =
                    [
                        Block($"{b}.band", "Plate", 150, 800, 1620, 204, fill: Gradient(0, (look.Plate, 0), (look.Plate2, 1)),
                            shadow: new ObjectShadow { ColorHex = "#00000066", BlurRadius = 24, OffsetX = 0, OffsetY = 8 }, steps: [.. bandIn, .. bandOut]),
                        Block($"{b}.bar", "Accent Bar", 150, 800, 10, 204, color: look.Accent, radius: 0, steps: [.. barIn, .. barOut]),
                        Label($"{b}.verse", "Verse", verse, 206, 818, 1500, 120, 38, "Poppins-Medium", lineHeight: 1.16, valign: TextVerticalAlignment.Top,
                            shrink: true, page: true, steps: [.. verseIn, .. verseOut]),
                        Block($"{b}.tag", "Reference Plate", 1410, 946, 360, 58, color: look.Accent, radius: 0, steps: [.. tagIn, .. tagOut]),
                        Label($"{b}.ref", "Reference", "John 3:16 (ESV)", 1410, 946, 360, 58, 24, "Poppins-SemiBold", look.InkOnAccent,
                            TextHorizontalAlignment.Center, tracking: 2, caps: true, steps: [.. refIn, .. refOut]),
                    ];
                    order = Ids(bandIn, barIn, verseIn, tagIn, refIn, verseOut, refOut, tagOut, bandOut, barOut);
                    break;
                }
                case StarterPack.MinimalSans:
                    objects =
                    [
                        Block($"{b}.band", "Plate", 192, 800, 1536, 196, color: look.Plate, stroke: new ObjectStroke { ColorHex = "#FFFFFF1F", Width = 1 },
                            steps: [.. bandIn, .. bandOut]),
                        Label($"{b}.verse", "Verse", verse, 236, 822, 1448, 110, 36, "Poppins-Regular", lineHeight: 1.25, valign: TextVerticalAlignment.Top,
                            shrink: true, page: true, steps: [.. verseIn, .. verseOut]),
                        Label($"{b}.ref", "Reference", "John 3:16 (ESV)", 236, 938, 1448, 40, 24, "Poppins-Medium", look.Accent, tracking: 4, caps: true,
                            steps: [.. refIn, .. refOut]),
                    ];
                    order = Ids(bandIn, verseIn, refIn, verseOut, refOut, bandOut);
                    break;
                default:
                {
                    var ruleIn = RuleIn($"{b}.rule.in", AnimationTrigger.WithPrevious, 0.25, AnimationEdge.Right, 0.6);
                    var ruleOut = RuleOut($"{b}.rule.out");
                    objects =
                    [
                        Block($"{b}.band", "Band", 0, 560, 1920, 520, fill: Floor(), radius: 0, steps: [.. bandIn, .. bandOut]),
                        Label($"{b}.verse", "Verse", verse, 260, 790, 1400, 132, Serif ? 44 : 40, Serif ? "DMSerifDisplay-Italic" : "Montserrat-SemiBold",
                            align: TextHorizontalAlignment.Center, lineHeight: 1.14, valign: TextVerticalAlignment.Bottom,
                            shrink: true, page: true, shadow: TextShadow(20, 2), steps: [.. verseIn, .. verseOut]),
                        Block($"{b}.rule", "Hairline", 920, 938, 80, Serif ? 1.5 : 4, color: look.Accent, radius: 0, steps: [.. ruleIn, .. ruleOut]),
                        Label($"{b}.ref", "Reference", "John 3:16 (ESV)", 260, 952, 1400, 40, Serif ? 22 : 24, look.Kicker, look.Accent,
                            TextHorizontalAlignment.Center, tracking: look.KickerTracking - 2, caps: true, shadow: TextShadow(12, 2), steps: [.. refIn, .. refOut]),
                    ];
                    order = Ids(bandIn, verseIn, ruleIn, refIn, verseOut, refOut, ruleOut, bandOut);
                    break;
                }
            }
            return new Overlay { Id = b, Name = "Verse + Reference", Objects = objects, AnimationOrder = order };
        }

        /// <summary>A point on the lower third: its tag ("POINT 1") and the words. <paramref name="simple"/> drops the tag (the default "Lower Third" design).</summary>
        private Overlay PointLowerThird(string b, bool simple = false)
        {
            var bandIn = Broadcast || Minimal ? PlateIn($"{b}.band.in") : FadeIn($"{b}.band.in", duration: 0.7);
            var bandOut = Broadcast || Minimal ? PlateOut($"{b}.band.out") : FadeOut($"{b}.band.out", AnimationTrigger.AfterPrevious, -0.1, 0.6);
            var textIn = TextIn($"{b}.text.in", AnimationTrigger.AfterPrevious, -0.45);
            var textOut = TextOut($"{b}.text.out");
            var tagIn = PopIn($"{b}.tag.in", AnimationTrigger.WithPrevious, 0, 0.7);
            var tagOut = PopOut($"{b}.tag.out");
            var kickIn = FadeIn($"{b}.kicker.in", delay: 0.1, duration: 0.4);
            var kickOut = FadeOut($"{b}.kicker.out");
            var objects = new List<SlideObject>();
            var textY = simple ? 846.0 : 880.0;
            var (font, size) = pack switch
            {
                StarterPack.Arena => ("Montserrat-ExtraBold", 50.0),
                StarterPack.Broadcast => ("Poppins-SemiBold", 44.0),
                StarterPack.ModernSerif => ("DMSerifDisplay-Regular", 52.0),
                _ => ("Poppins-Medium", 42.0),
            };
            var x = Broadcast ? 206.0 : Minimal ? 236 : 192;
            var w = Broadcast ? 1520.0 : Minimal ? 1448 : 1536;
            var text = Label($"{b}.text", "Point", "Faith is not the absence of fear — it is trust that outlasts it.", x, textY, w, simple ? 140 : 108,
                size, font, caps: Arena, tracking: Arena ? 1 : 0, lineHeight: 1.08, shrink: true, page: true,
                shadow: Broadcast || Minimal ? null : TextShadow(20, 2), steps: [.. textIn, .. textOut]);
            objects.Add(Broadcast
                ? Block($"{b}.band", "Plate", 150, 812, 1620, 192, fill: Gradient(0, (look.Plate, 0), (look.Plate2, 1)), steps: [.. bandIn, .. bandOut])
                : Minimal
                    ? Block($"{b}.band", "Plate", 192, 826, 1536, 168, color: look.Plate, stroke: new ObjectStroke { ColorHex = "#FFFFFF1F", Width = 1 }, steps: [.. bandIn, .. bandOut])
                    : Block($"{b}.band", "Band", 0, 600, 1920, 480, fill: Floor(), radius: 0, steps: [.. bandIn, .. bandOut]));
            List<string> order;
            if (simple)
            {
                objects.Add(text);
                order = Ids(bandIn, textIn, textOut, bandOut);
            }
            else
            {
                var tagX = Broadcast ? 206.0 : Minimal ? 236 : 192;
                objects.Add(text);
                objects.Add(Block($"{b}.tag", "Tag", tagX, 832, 168, 40, color: look.Accent, radius: Minimal ? 20 : Broadcast ? 4 : 0, steps: [.. tagIn, .. tagOut]));
                objects.Add(Label($"{b}.kicker", "Kicker", "Point 1", tagX, 832, 168, 40, 20, look.Kicker, look.InkOnAccent, TextHorizontalAlignment.Center,
                    tracking: 3, caps: true, steps: [.. kickIn, .. kickOut]));
                order = Ids(bandIn, tagIn, kickIn, textIn, textOut, kickOut, tagOut, bandOut);
            }
            return new Overlay { Id = b, Name = "Point", Objects = objects, AnimationOrder = order };
        }

        private Overlay CallToActionLower(string b)
        {
            var cardIn = PlateIn($"{b}.card.in");
            var cardOut = PlateOut($"{b}.card.out");
            var headIn = TextIn($"{b}.head.in", AnimationTrigger.AfterPrevious, -0.35);
            var headOut = TextOut($"{b}.head.out");
            var bodyIn = TextIn($"{b}.body.in", AnimationTrigger.WithPrevious, 0.12, rise: 12);
            var bodyOut = FadeOut($"{b}.body.out");
            var pillIn = PopIn($"{b}.pill.in", delay: -0.3, from: 0.85);
            var pillOut = PopOut($"{b}.pill.out");
            var pillTextIn = FadeIn($"{b}.pilltext.in", delay: 0.1, duration: 0.4);
            var pillTextOut = FadeOut($"{b}.pilltext.out");
            const double cx = 192, cy = 812, cw = 1536, ch = 180;
            return new Overlay
            {
                Id = b, Name = "Call to Action",
                Objects =
                [
                    Block($"{b}.card", "Card", cx, cy, cw, ch, fill: Gradient(0, (look.Plate, 0), (look.Plate2, 1)), radius: Minimal ? 18 : look.Radius,
                        stroke: Serif ? new ObjectStroke { ColorHex = WithAlpha(look.Accent, 0.6), Width = 1.5 } : Minimal ? new ObjectStroke { ColorHex = "#FFFFFF1F", Width = 1 } : null,
                        steps: [.. cardIn, .. cardOut]),
                    Label($"{b}.head", "Heading", "Next Steps", cx + 48, cy + 26, cw - 460, 70, Arena ? 50 : 46, Arena ? "Montserrat-Black" : look.Display,
                        caps: Arena, shrink: true, page: true, steps: [.. headIn, .. headOut]),
                    Label($"{b}.body", "Body", "Text NEXT to 555-0100 or stop by the Welcome Desk after the service.", cx + 48, cy + 98, cw - 460, 56, 26,
                        look.Body, look.Soft, shrink: true, page: true, steps: [.. bodyIn, .. bodyOut]),
                    Block($"{b}.pill", "Button", cx + cw - 360, cy + (ch - 72) / 2, 312, 72, color: look.Accent, radius: Arena || Serif ? 0 : 36, steps: [.. pillIn, .. pillOut]),
                    Label($"{b}.pilltext", "Button Label", "Text NEXT", cx + cw - 360, cy + (ch - 72) / 2, 312, 72, 26, look.Kicker, look.InkOnAccent,
                        TextHorizontalAlignment.Center, tracking: 3, caps: true, steps: [.. pillTextIn, .. pillTextOut]),
                ],
                AnimationOrder = Ids(cardIn, headIn, bodyIn, pillIn, pillTextIn, headOut, bodyOut, pillTextOut, pillOut, cardOut),
            };
        }

        private Overlay Ticker()
        {
            var b = pack.OverlayId("ticker");
            var plateIn = PlateIn($"{b}.plate.in");
            var plateOut = PlateOut($"{b}.plate.out");
            var tagIn = PopIn($"{b}.tag.in", AnimationTrigger.WithPrevious, 0.1, 0.8);
            var tagOut = PopOut($"{b}.tag.out");
            var tagTextIn = FadeIn($"{b}.tagtext.in", delay: 0.1, duration: 0.4);
            var tagTextOut = FadeOut($"{b}.tagtext.out");
            var lineIn = FadeIn($"{b}.text.in", AnimationTrigger.AfterPrevious, -0.1, 0.4);
            var lineOut = FadeOut($"{b}.text.out", AnimationTrigger.OnDismiss);
            var line = Label($"{b}.text", "Ticker Text",
                "Welcome — service times, upcoming events and announcements scroll here.   •   Next Steps class Sunday 9am   •   Text NEXT to 555-0100",
                320, 992, 1600, 76, 34, look.Body, steps: [.. lineIn, .. lineOut]);
            line.TextStyle!.Scroll = new BlockScroll { Axis = BlockScrollAxis.Left, Speed = 150 };
            return new Overlay
            {
                Id = b, Name = "Ticker",
                Objects =
                [
                    Block($"{b}.plate", "Plate", 0, 992, 1920, 76, fill: Gradient(0, (look.Plate, 0), (look.Plate2, 1)), radius: 0, steps: [.. plateIn, .. plateOut]),
                    line,
                    Block($"{b}.tag", "Tag", 0, 992, 300, 76, color: look.Accent, radius: 0, steps: [.. tagIn, .. tagOut]),
                    Label($"{b}.tagtext", "Tag Text", "Announcements", 0, 992, 300, 76, 22, look.Kicker, look.InkOnAccent, TextHorizontalAlignment.Center,
                        tracking: 3, caps: true, steps: [.. tagTextIn, .. tagTextOut]),
                ],
                AnimationOrder = Ids(plateIn, tagIn, tagTextIn, lineIn, lineOut, tagTextOut, tagOut, plateOut),
            };
        }

        // MARK: Side thirds

        private SlideObject Panel(string b, List<AnimationStep> steps) =>
            Block($"{b}.panel", "Panel", 1280, 0, 640, 1080, fill: Serif || Arena
                ? Gradient(0, (WithAlpha(look.Plate, 0), 0), (WithAlpha(look.Plate, 0.85), 0.35), (look.Plate, 1))
                : Gradient(90, (look.Plate2, 0), (look.Plate, 1)), radius: 0, steps: steps);

        private static VideoPush Push => new() { Zoom = 1.05, Backdrop = true };

        private (List<AnimationStep> In, List<AnimationStep> Out) PanelSteps(string b)
        {
            var panelIn = Step($"{b}.panel.in", AnimationKind.In, StepAnimation.Move, AnimationTrigger.WithPrevious, null, 0.7, AnimationRamp.Out, edge: AnimationEdge.Right);
            var panelOut = Step($"{b}.panel.out", AnimationKind.Out, StepAnimation.Move, AnimationTrigger.AfterPrevious, -0.05, 0.6, AnimationRamp.In, edge: AnimationEdge.Right);
            panelIn.VideoPush = Push;
            panelOut.VideoPush = Push;
            return ([panelIn], [panelOut]);
        }

        private Overlay LyricsSideThird(string b)
        {
            var (panelIn, panelOut) = PanelSteps(b);
            var ruleIn = RuleIn($"{b}.rule.in", delay: -0.35, duration: 0.6);
            var ruleOut = RuleOut($"{b}.rule.out");
            var textIn = TextIn($"{b}.text.in", AnimationTrigger.AfterPrevious, -0.3);
            var textOut = TextOut($"{b}.text.out");
            return new Overlay
            {
                Id = b, Name = "Lyrics (Side Third)",
                Objects =
                [
                    Panel(b, [.. panelIn, .. panelOut]),
                    Block($"{b}.rule", "Rule", 1360, 240, 64, Serif ? 1.5 : 4, color: look.Accent, radius: 0, steps: [.. ruleIn, .. ruleOut]),
                    Label($"{b}.text", "Lyrics", SampleLyrics, 1360, 270, 480, 600, Arena ? 46 : 44, Arena ? "Montserrat-ExtraBold" : look.Lyric,
                        caps: Arena, lineHeight: 1.2, valign: TextVerticalAlignment.Top, shrink: true, page: true, steps: [.. textIn, .. textOut]),
                ],
                AnimationOrder = Ids(panelIn, ruleIn, textIn, textOut, ruleOut, panelOut),
            };
        }

        private Overlay PointSideThird(string b, bool simple = false)
        {
            var (panelIn, panelOut) = PanelSteps(b);
            var kickIn = FadeIn($"{b}.kicker.in", AnimationTrigger.AfterPrevious, -0.3, 0.5);
            var kickOut = FadeOut($"{b}.kicker.out");
            var textIn = TextIn($"{b}.text.in", AnimationTrigger.WithPrevious, 0.12);
            var textOut = TextOut($"{b}.text.out");
            var text = Label($"{b}.text", "Point", "Faith is not the absence of fear — it is trust that outlasts it.", 1360, 360, 480, 480,
                Arena ? 50 : 48, Arena ? "Montserrat-ExtraBold" : look.Display, caps: Arena, lineHeight: 1.12, valign: TextVerticalAlignment.Top,
                shrink: true, page: true, steps: [.. textIn, .. textOut]);
            if (simple)
            {
                textIn[0].Trigger = AnimationTrigger.AfterPrevious;
                textIn[0].DelaySeconds = -0.3;
                return new Overlay { Id = b, Name = "Point (Side Third)", Objects = [Panel(b, [.. panelIn, .. panelOut]), text], AnimationOrder = Ids(panelIn, textIn, textOut, panelOut) };
            }
            return new Overlay
            {
                Id = b, Name = "Point (Side Third)",
                Objects =
                [
                    Panel(b, [.. panelIn, .. panelOut]),
                    text,
                    Label($"{b}.kicker", "Kicker", "Point 1", 1360, 300, 480, 44, 24, look.Kicker, look.Accent, tracking: look.KickerTracking, caps: true,
                        steps: [.. kickIn, .. kickOut]),
                ],
                AnimationOrder = Ids(panelIn, kickIn, textIn, textOut, kickOut, panelOut),
            };
        }

        private Overlay VerseSideThird(string b)
        {
            var (panelIn, panelOut) = PanelSteps(b);
            var verseIn = TextIn($"{b}.verse.in", AnimationTrigger.AfterPrevious, -0.3);
            var verseOut = TextOut($"{b}.verse.out");
            var ruleIn = RuleIn($"{b}.rule.in", delay: -0.3);
            var ruleOut = RuleOut($"{b}.rule.out");
            var refIn = TextIn($"{b}.ref.in", AnimationTrigger.WithPrevious, 0.1, rise: 12);
            var refOut = FadeOut($"{b}.ref.out");
            return new Overlay
            {
                Id = b, Name = "Verse + Reference (Side Third)",
                Objects =
                [
                    Panel(b, [.. panelIn, .. panelOut]),
                    Label($"{b}.verse", "Verse", "For God so loved the world, that he gave his only Son, that whoever believes in him should not perish but have eternal life.",
                        1360, 250, 480, 500, Serif ? 44 : 38, Serif ? "DMSerifDisplay-Italic" : look.Body, lineHeight: 1.2, valign: TextVerticalAlignment.Top,
                        shrink: true, page: true, steps: [.. verseIn, .. verseOut]),
                    Block($"{b}.rule", "Rule", 1360, 776, 64, Serif ? 1.5 : 4, color: look.Accent, radius: 0, steps: [.. ruleIn, .. ruleOut]),
                    Label($"{b}.ref", "Reference", "John 3:16 (ESV)", 1360, 796, 480, 44, 22, look.Kicker, look.Accent, tracking: look.KickerTracking - 4,
                        caps: true, steps: [.. refIn, .. refOut]),
                ],
                AnimationOrder = Ids(panelIn, verseIn, ruleIn, refIn, verseOut, refOut, ruleOut, panelOut),
            };
        }

        private Overlay CallToActionSide(string b)
        {
            var (panelIn, panelOut) = PanelSteps(b);
            var headIn = TextIn($"{b}.head.in", AnimationTrigger.AfterPrevious, -0.3);
            var headOut = TextOut($"{b}.head.out");
            var bodyIn = TextIn($"{b}.body.in", AnimationTrigger.WithPrevious, 0.12, rise: 12);
            var bodyOut = FadeOut($"{b}.body.out");
            var pillIn = PopIn($"{b}.pill.in", delay: -0.3, from: 0.85);
            var pillOut = PopOut($"{b}.pill.out");
            var pillTextIn = FadeIn($"{b}.pilltext.in", delay: 0.1, duration: 0.4);
            var pillTextOut = FadeOut($"{b}.pilltext.out");
            return new Overlay
            {
                Id = b, Name = "Call to Action",
                Objects =
                [
                    Panel(b, [.. panelIn, .. panelOut]),
                    Label($"{b}.head", "Heading", "Next Steps", 1360, 330, 480, 80, Arena ? 58 : 54, Arena ? "Montserrat-Black" : look.Display, caps: Arena,
                        shrink: true, page: true, steps: [.. headIn, .. headOut]),
                    Label($"{b}.body", "Body", "Text NEXT to 555-0100 or stop by the Welcome Desk after the service.", 1360, 430, 480, 180, 30, look.Body, look.Soft,
                        lineHeight: 1.25, valign: TextVerticalAlignment.Top, shrink: true, page: true, steps: [.. bodyIn, .. bodyOut]),
                    Block($"{b}.pill", "Button", 1360, 650, 300, 70, color: look.Accent, radius: Arena || Serif ? 0 : 35, steps: [.. pillIn, .. pillOut]),
                    Label($"{b}.pilltext", "Button Label", "Text NEXT", 1360, 650, 300, 70, 24, look.Kicker, look.InkOnAccent, TextHorizontalAlignment.Center,
                        tracking: 3, caps: true, steps: [.. pillTextIn, .. pillTextOut]),
                ],
                AnimationOrder = Ids(panelIn, headIn, bodyIn, pillIn, pillTextIn, headOut, bodyOut, pillTextOut, pillOut, panelOut),
            };
        }

        // MARK: Full screen

        private Overlay SearchCard()
        {
            var b = pack.OverlayId("search");
            var fieldIn = Step($"{b}.field.in", AnimationKind.In, StepAnimation.Draw, AnimationTrigger.WithPrevious, null, 1.0, drawStart: 0);
            var fieldOut = Step($"{b}.field.out", AnimationKind.Out, StepAnimation.Scale, AnimationTrigger.AfterPrevious, -0.15, 0.35, AnimationRamp.In, true, fromScale: 0.9);
            fieldIn.VideoPush = new VideoPush { Mode = VideoPushMode.BlurBackground, Zoom = 1.12 };
            fieldOut.VideoPush = new VideoPush { Mode = VideoPushMode.BlurBackground, Zoom = 1.12 };
            var glyphIn = PopIn($"{b}.glyph.in", delay: -0.2);
            var glyphOut = PopOut($"{b}.glyph.out");
            var queryIn = new List<AnimationStep> { Step($"{b}.query.in", AnimationKind.In, StepAnimation.Type, AnimationTrigger.AfterPrevious, -0.1, 1.6, cursor: true) };
            var queryOut = FadeOut($"{b}.query.out", AnimationTrigger.OnClick);
            return new Overlay
            {
                Id = b, Name = "Search",
                Objects =
                [
                    Block($"{b}.field", "Field", 460, 470, 1000, 120, color: WithAlpha(look.Plate, 0.85), radius: Minimal || Broadcast ? 60 : 0,
                        stroke: new ObjectStroke { ColorHex = look.Accent, Width = Serif ? 1.5 : 2.5 }, steps: [fieldIn, fieldOut]),
                    new SlideObject
                    {
                        Id = $"{b}.glyph", ObjectKind = SlideObjectKind.Shape, Name = "Search Icon", Text = "", X = 506, Y = 502, Width = 56, Height = 56,
                        ShapeKind = ShapeKind.Path, PathData = Icons.Search, Fill = Fill(look.Accent), AnimationSteps = [.. glyphIn, .. glyphOut],
                    },
                    Label($"{b}.query", "Query", "how do I find peace?", 590, 470, 830, 120, 44, look.Body, steps: [.. queryIn, .. queryOut]),
                ],
                AnimationOrder = [fieldIn.Id, .. Ids(glyphIn, queryIn, queryOut, glyphOut), fieldOut.Id],
            };
        }

        private Overlay CinematicBars()
        {
            var b = pack.OverlayId("bars");
            var topIn = Step($"{b}.top.in", AnimationKind.In, StepAnimation.Move, AnimationTrigger.WithPrevious, duration: 3.0, ramp: AnimationRamp.Both, edge: AnimationEdge.Top);
            var bottomIn = Step($"{b}.bottom.in", AnimationKind.In, StepAnimation.Move, AnimationTrigger.WithPrevious, duration: 3.0, ramp: AnimationRamp.Both, edge: AnimationEdge.Bottom);
            var topOut = Step($"{b}.top.out", AnimationKind.Out, StepAnimation.Move, AnimationTrigger.OnDismiss, duration: 2.5, ramp: AnimationRamp.Both, edge: AnimationEdge.Top);
            var bottomOut = Step($"{b}.bottom.out", AnimationKind.Out, StepAnimation.Move, AnimationTrigger.WithPrevious, duration: 2.5, ramp: AnimationRamp.Both, edge: AnimationEdge.Bottom);
            return new Overlay
            {
                Id = b, Name = "Cinematic Bars",
                Objects =
                [
                    Block($"{b}.top", "Top Bar", 0, 0, 1920, 132, color: "#000000FF", radius: 0, steps: [topIn, topOut]),
                    Block($"{b}.bottom", "Bottom Bar", 0, 948, 1920, 132, color: "#000000FF", radius: 0, steps: [bottomIn, bottomOut]),
                ],
                AnimationOrder = [topIn.Id, bottomIn.Id, topOut.Id, bottomOut.Id],
            };
        }
    }
}
