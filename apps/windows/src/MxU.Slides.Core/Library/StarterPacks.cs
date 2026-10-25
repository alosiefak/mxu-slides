using System.Globalization;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Library;

/// <summary>
/// The starter themes and their overlays. The first four have the same ids, layouts, colours and
/// animation steps as the Mac app's StarterPacks.swift; the worship looks after them (Arena,
/// Broadcast, Modern Serif, Minimal Sans) are Windows' own, built in StarterPacks.Worship.cs from
/// the fonts the app ships.
/// </summary>
public enum StarterPack { CleanGeometric, EditorialSerif, BoldBlocks, Digital, Arena, Broadcast, ModernSerif, MinimalSans }

public static partial class StarterPacks
{
    public static readonly StarterPack[] All = Enum.GetValues<StarterPack>();

    /// <summary>Bumped when the packs change, so libraries seeded by an older build get refreshed copies.</summary>
    public const int Revision = 2;

    public static readonly string[] OverlayItems =
        ["name", "nameTimed", "scriptureVerse", "point", "ctaLower", "ticker", "verseSide", "pointSide", "cta", "search", "bars"];

    public static string Raw(this StarterPack pack) => pack switch
    {
        StarterPack.CleanGeometric => "cleanGeometric",
        StarterPack.EditorialSerif => "editorialSerif",
        StarterPack.BoldBlocks => "boldBlocks",
        StarterPack.Digital => "digital",
        StarterPack.Arena => "arena",
        StarterPack.Broadcast => "broadcast",
        StarterPack.ModernSerif => "modernSerif",
        _ => "minimalSans",
    };

    public static string Title(this StarterPack pack) => pack switch
    {
        StarterPack.CleanGeometric => "Clean Geometric",
        StarterPack.EditorialSerif => "Editorial Serif",
        StarterPack.BoldBlocks => "Bold Blocks",
        StarterPack.Digital => "Digital",
        StarterPack.Arena => "Arena",
        StarterPack.Broadcast => "Broadcast",
        StarterPack.ModernSerif => "Modern Serif",
        _ => "Minimal Sans",
    };

    public static string ThemeId(this StarterPack pack) => $"pack.{pack.Raw()}.theme";

    public static string OverlayId(this StarterPack pack, string item) => $"pack.{pack.Raw()}.overlay.{item}";

    public static Theme MakeTheme(this StarterPack pack) => pack.IsWorshipLook() ? new WorshipBuilder(pack).MakeTheme() : new Builder(pack).MakeTheme();

    public static List<Overlay> MakeOverlays(this StarterPack pack) => pack.IsWorshipLook() ? new WorshipBuilder(pack).MakeOverlays() : new Builder(pack).MakeOverlays();

    /// <summary>Unit-square path data for icons (same strings as the Mac's IconCatalog).</summary>
    public static class Icons
    {
        private const double Kappa = 0.5522847498;

        private static string F(double v) => v.ToString("0.0000", CultureInfo.InvariantCulture);

        public static string Circle(double cx, double cy, double r, bool clockwise = true)
        {
            var k = Kappa * r;
            var s = clockwise ? 1.0 : -1.0;
            return $"M {F(cx + r)} {F(cy)} "
                + $"C {F(cx + r)} {F(cy + s * k)} {F(cx + k)} {F(cy + s * r)} {F(cx)} {F(cy + s * r)} "
                + $"C {F(cx - k)} {F(cy + s * r)} {F(cx - r)} {F(cy + s * k)} {F(cx - r)} {F(cy)} "
                + $"C {F(cx - r)} {F(cy - s * k)} {F(cx - k)} {F(cy - s * r)} {F(cx)} {F(cy - s * r)} "
                + $"C {F(cx + k)} {F(cy - s * r)} {F(cx + r)} {F(cy - s * k)} {F(cx + r)} {F(cy)} Z";
        }

        public static string Ring(double cx, double cy, double outer, double inner) =>
            Circle(cx, cy, outer) + " " + Circle(cx, cy, inner, clockwise: false);

        public static string Polygon(IReadOnlyList<(double X, double Y)> points) =>
            points.Count == 0 ? "" : $"M {F(points[0].X)} {F(points[0].Y)} "
                + string.Join(" ", points.Skip(1).Select(p => $"L {F(p.X)} {F(p.Y)}")) + " Z";

        public static string Bar((double X, double Y) a, (double X, double Y) b, double width)
        {
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var len = Math.Max(Math.Sqrt(dx * dx + dy * dy), 1e-6);
            var nx = -dy / len * width / 2;
            var ny = dx / len * width / 2;
            return Polygon([(a.X + nx, a.Y + ny), (b.X + nx, b.Y + ny), (b.X - nx, b.Y - ny), (a.X - nx, a.Y - ny)]);
        }

        public static readonly string Search = Ring(0.4, 0.4, 0.36, 0.26) + " " + Bar((0.66, 0.66), (0.94, 0.94), 0.14);
    }

    private sealed record Look(
        string Body, string Display, string Accent, string Accent2, string Ink, string InkOnAccent,
        string Plate, string Plate2, string Backdrop, double Radius);

    private enum CardLayout { Full, LowerThird, SideThird }

    private const string SampleLyrics = "Amazing grace, how sweet the sound\nThat saved a wretch like me\nI once was lost, but now am found\nWas blind, but now I see";

    private sealed class Builder(StarterPack pack)
    {
        private readonly Look look = pack switch
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

        private string ThemeId => pack.ThemeId();
        private bool Bold => pack == StarterPack.BoldBlocks;
        private bool Editorial => pack == StarterPack.EditorialSerif;
        private bool Clean => pack == StarterPack.CleanGeometric;
        private bool Digital => pack == StarterPack.Digital;
        private double KickerTracking => Editorial ? 8 : Digital ? 4 : 6;

        // MARK: Building blocks

        private static ObjectFill Fill(string hex) => new() { FillKind = FillKind.Solid, ColorHex = hex };

        private static ObjectFill Gradient(string a, string c, double angle = 0) => new()
        {
            FillKind = FillKind.LinearGradient,
            GradientAngleDegrees = angle,
            GradientStops = [new() { ColorHex = a, Position = 0 }, new() { ColorHex = c, Position = 1 }],
        };

        private SlideObject Block(string id, string name, double x, double y, double w, double h,
            ObjectFill? fill = null, string? color = null, double? radius = null, ObjectStroke? stroke = null,
            ObjectShadow? shadow = null, double? skew = null, bool ellipse = false, string? maskedBy = null,
            List<AnimationStep>? steps = null)
        {
            var r = radius ?? look.Radius;
            return new SlideObject
            {
                Id = id, ObjectKind = SlideObjectKind.Shape, Name = name, Text = "", X = x, Y = y, Width = w, Height = h,
                MaskObjectId = maskedBy, MaskMode = maskedBy is null ? null : MaskMode.In,
                ShapeKind = ellipse ? ShapeKind.Ellipse : r > 0 ? ShapeKind.RoundedRectangle : ShapeKind.Rectangle, CornerRadius = r,
                Fill = fill ?? Fill(color ?? look.Plate), Stroke = stroke, Shadow = shadow,
                AnimationSteps = steps is { Count: > 0 } ? steps : null, SkewX = skew,
            };
        }

        private static SlideObject Matte(string id, double x, double y, double w, double h) => new()
        {
            Id = id, ObjectKind = SlideObjectKind.Shape, Name = "Mask", Text = "", X = x, Y = y, Width = w, Height = h,
            ShapeKind = ShapeKind.Rectangle, Fill = Fill("#FFFFFFFF"),
        };

        private SlideObject Label(string id, string name, string text, double x, double y, double w, double h, double size,
            bool display = false, string? font = null, string? color = null,
            TextHorizontalAlignment align = TextHorizontalAlignment.Left, TextVerticalAlignment valign = TextVerticalAlignment.Middle,
            double tracking = 0, bool uppercase = false, double inset = 24, double lineHeight = 1,
            TextLineFill? lineFill = null, string? maskedBy = null, bool shrink = false, bool page = false,
            List<AnimationStep>? steps = null) => new()
        {
            Id = id, ObjectKind = SlideObjectKind.Text, Name = name, Text = text, X = x, Y = y, Width = w, Height = h,
            MaskObjectId = maskedBy, MaskMode = maskedBy is null ? null : MaskMode.In,
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
            AnimationSteps = steps is { Count: > 0 } ? steps : null,
        };

        private static AnimationStep Step(string id, AnimationKind kind, StepAnimation animation, AnimationTrigger trigger,
            double? delay = null, double duration = 0.5, AnimationRamp? ramp = null, bool? fade = null,
            AnimationEdge? edge = null, double? offsetX = null, double? offsetY = null, double? fromScale = null,
            double? amount = null, double? softEdge = null, bool? cursor = null, double? drawStart = null) => new()
        {
            Id = id, Kind = kind, Animation = animation, Trigger = trigger, DelaySeconds = delay, DurationSeconds = duration,
            Ramp = ramp, WithFade = fade, Edge = edge, OffsetX = offsetX, OffsetY = offsetY, FromScale = fromScale,
            Amount = amount, SoftEdge = softEdge, Cursor = cursor, DrawStart = drawStart,
        };

        private AnimationStep PlateIn(string id, AnimationTrigger trigger = AnimationTrigger.WithPrevious, double? delay = null,
            AnimationEdge edge = AnimationEdge.Left, bool big = false, VideoPush? videoPush = null)
        {
            var stretch = big ? 1.3 : 1;
            if (big)
            {
                var s = Step(id, AnimationKind.In, StepAnimation.Move, trigger, delay, 0.7, AnimationRamp.Out, edge: edge);
                s.VideoPush = videoPush;
                return s;
            }
            return pack switch
            {
                StarterPack.CleanGeometric => Step(id, AnimationKind.In, StepAnimation.Move, trigger, delay, 0.55 * stretch, AnimationRamp.Out, true, edge),
                StarterPack.EditorialSerif => Step(id, AnimationKind.In, StepAnimation.Burn, trigger, delay, 1.1 * stretch, AnimationRamp.Out, true, amount: 0.7),
                StarterPack.BoldBlocks => Step(id, AnimationKind.In, StepAnimation.Wipe, trigger, delay, 0.45 * stretch, AnimationRamp.Out, true, edge, softEdge: big ? 60 : 0),
                _ => Step(id, AnimationKind.In, StepAnimation.Glitch, trigger, delay, 0.9 * stretch, fade: true, amount: 0.9),
            };
        }

        private AnimationStep PlateOut(string id, AnimationTrigger trigger = AnimationTrigger.OnDismiss, double? delay = null,
            AnimationEdge edge = AnimationEdge.Left, bool big = false, VideoPush? videoPush = null)
        {
            var stretch = big ? 1.2 : 1;
            if (big)
            {
                var s = Step(id, AnimationKind.Out, StepAnimation.Move, trigger, delay, 0.6, AnimationRamp.In, edge: edge);
                s.VideoPush = videoPush;
                return s;
            }
            return pack switch
            {
                StarterPack.CleanGeometric => Step(id, AnimationKind.Out, StepAnimation.Move, trigger, delay, 0.45 * stretch, AnimationRamp.In, true, edge),
                StarterPack.EditorialSerif => Step(id, AnimationKind.Out, StepAnimation.Burn, trigger, delay, 0.9 * stretch, AnimationRamp.In, true, amount: 0.7),
                StarterPack.BoldBlocks => Step(id, AnimationKind.Out, StepAnimation.Wipe, trigger, delay, 0.4 * stretch, AnimationRamp.In, true, edge, softEdge: big ? 60 : 0),
                _ => Step(id, AnimationKind.Out, StepAnimation.Glitch, trigger, delay, 0.8 * stretch, fade: true, amount: 0.9),
            };
        }

        private AnimationStep TextIn(string id, AnimationTrigger trigger = AnimationTrigger.AfterPrevious, double? delay = null,
            double rise = 28, AnimationEdge edge = AnimationEdge.Left)
        {
            double? lag = trigger == AnimationTrigger.AfterPrevious ? Math.Max(delay ?? 0, -0.05) : delay;
            return pack switch
            {
                StarterPack.CleanGeometric => Step(id, AnimationKind.In, StepAnimation.Move, trigger, lag, 0.5, AnimationRamp.Out, true, offsetX: 0, offsetY: rise),
                StarterPack.EditorialSerif => Step(id, AnimationKind.In, StepAnimation.Fade, trigger, lag, 0.9, AnimationRamp.Out),
                StarterPack.BoldBlocks => Step(id, AnimationKind.In, StepAnimation.Wipe, trigger, lag, 0.4, AnimationRamp.Out, edge: edge, softEdge: 0),
                _ => Step(id, AnimationKind.In, StepAnimation.Glitch, trigger, lag, 0.9, amount: 0.85),
            };
        }

        private AnimationStep TextOut(string id, AnimationTrigger trigger = AnimationTrigger.OnClick, double? delay = null,
            AnimationEdge toward = AnimationEdge.Bottom, double distance = 28)
        {
            var (dx, dy) = toward switch
            {
                AnimationEdge.Bottom => (0.0, distance),
                AnimationEdge.Top => (0.0, -distance),
                AnimationEdge.Left => (-distance, 0.0),
                _ => (distance, 0.0),
            };
            return pack switch
            {
                StarterPack.CleanGeometric => Step(id, AnimationKind.Out, StepAnimation.Move, trigger, delay, 0.4, AnimationRamp.In, true, offsetX: dx, offsetY: dy),
                StarterPack.EditorialSerif => Step(id, AnimationKind.Out, StepAnimation.Fade, trigger, delay, 0.7, AnimationRamp.In),
                StarterPack.BoldBlocks => Step(id, AnimationKind.Out, StepAnimation.Wipe, trigger, delay, 0.3, AnimationRamp.In, edge: toward, softEdge: 0),
                _ => Step(id, AnimationKind.Out, StepAnimation.Glitch, trigger, delay, 0.7, amount: 0.85),
            };
        }

        private static AnimationStep RuleIn(string id, AnimationTrigger trigger = AnimationTrigger.AfterPrevious, double? delay = -0.2,
            AnimationEdge edge = AnimationEdge.Left, double duration = 0.5) =>
            Step(id, AnimationKind.In, StepAnimation.Wipe, trigger, delay, duration, AnimationRamp.Out, edge: edge, softEdge: 0);

        private static AnimationStep RuleOut(string id, AnimationTrigger trigger = AnimationTrigger.WithPrevious, double? delay = null,
            AnimationEdge edge = AnimationEdge.Left) =>
            Step(id, AnimationKind.Out, StepAnimation.Wipe, trigger, delay, 0.35, AnimationRamp.In, edge: edge, softEdge: 0);

        private static AnimationStep PopIn(string id, AnimationTrigger trigger = AnimationTrigger.AfterPrevious, double? delay = -0.2,
            double duration = 0.45, double from = 0.4) =>
            Step(id, AnimationKind.In, StepAnimation.Scale, trigger, delay, duration, AnimationRamp.Out, true, fromScale: from);

        private static AnimationStep PopOut(string id, AnimationTrigger trigger = AnimationTrigger.WithPrevious, double? delay = null) =>
            Step(id, AnimationKind.Out, StepAnimation.Scale, trigger, delay, 0.35, AnimationRamp.In, true, fromScale: 0.6);

        private static AnimationStep DrawIn(string id, AnimationTrigger trigger = AnimationTrigger.WithPrevious, double? delay = null, double duration = 1.2) =>
            Step(id, AnimationKind.In, StepAnimation.Draw, trigger, delay, duration, drawStart: 0);

        private static AnimationStep TypeIn(string id, AnimationTrigger trigger = AnimationTrigger.AfterPrevious, double? delay = -0.1, double duration = 1.6) =>
            Step(id, AnimationKind.In, StepAnimation.Type, trigger, delay, duration, cursor: true);

        private static AnimationStep FadeIn(string id, AnimationTrigger trigger = AnimationTrigger.AfterPrevious, double? delay = -0.1, double duration = 0.4) =>
            Step(id, AnimationKind.In, StepAnimation.Fade, trigger, delay, duration, AnimationRamp.Out);

        private static AnimationStep FadeOut(string id, AnimationTrigger trigger = AnimationTrigger.WithPrevious, double? delay = null, double duration = 0.4) =>
            Step(id, AnimationKind.Out, StepAnimation.Fade, trigger, delay, duration, AnimationRamp.In);

        private static List<string> Ids(params AnimationStep[] steps) => steps.Select(s => s.Id).ToList();

        // MARK: Theme

        public Theme MakeTheme()
        {
            Slide FromOverlay(Overlay overlay, string item, string name, string primary, bool fullScreen = false, string? folder = null) =>
                ThemeSlide(item, name, PromotingPrimaryText(overlay.Objects, primary), overlay.AnimationOrder ?? [], fullScreen, folder);

            var slides = new List<Slide>
            {
                LyricsSlide(), PointsSlide(), SectionTitleSlide(), VerseSlide(), PointSlide(), QuoteSlide(),
                FromOverlay(CallToAction($"{ThemeId}.ctaFull", CardLayout.Full), "ctaFull", "Call to Action", $"{ThemeId}.ctaFull.head", fullScreen: true),
                SimplifiedThird("lowerDefault", "Lower Third", PointLowerThird($"{ThemeId}.lowerDefault"),
                    $"{ThemeId}.lowerDefault.text", [$"{ThemeId}.lowerDefault.tag", $"{ThemeId}.lowerDefault.kicker"], "Lower Thirds"),
                FromOverlay(LyricsLowerThird($"{ThemeId}.lyricsLower"), "lyricsLower", "Lyrics (Lower Third)", $"{ThemeId}.lyricsLower.text", folder: "Lower Thirds"),
                FromOverlay(NamePlate(false, $"{ThemeId}.name"), "name", "Name + Title", $"{ThemeId}.name.name", folder: "Lower Thirds"),
                FromOverlay(VerseLowerThird($"{ThemeId}.verseLower"), "verseLower", "Verse + Reference (Lower Third)", $"{ThemeId}.verseLower.verse", folder: "Lower Thirds"),
                FromOverlay(PointLowerThird($"{ThemeId}.pointLower"), "pointLower", "Point (Lower Third)", $"{ThemeId}.pointLower.text", folder: "Lower Thirds"),
                FromOverlay(CallToAction($"{ThemeId}.ctaLower", CardLayout.LowerThird), "ctaLower", "Call to Action (Lower Third)", $"{ThemeId}.ctaLower.head", folder: "Lower Thirds"),
                SimplifiedThird("sideDefault", "Side Third", PointSideThird($"{ThemeId}.sideDefault"),
                    $"{ThemeId}.sideDefault.text", [$"{ThemeId}.sideDefault.kicker"], "Side Thirds"),
                FromOverlay(LyricsSideThird($"{ThemeId}.lyricsSide"), "lyricsSide", "Lyrics (Side Third)", $"{ThemeId}.lyricsSide.text", folder: "Side Thirds"),
                FromOverlay(PointSideThird($"{ThemeId}.pointSide"), "pointSide", "Point (Side Third)", $"{ThemeId}.pointSide.text", folder: "Side Thirds"),
                FromOverlay(VerseSideThird($"{ThemeId}.verseSide"), "verseSide", "Verse + Reference (Side Third)", $"{ThemeId}.verseSide.verse", folder: "Side Thirds"),
                FromOverlay(CallToAction($"{ThemeId}.ctaSide", CardLayout.SideThird), "ctaSide", "Call to Action (Side Third)", $"{ThemeId}.ctaSide.head", folder: "Side Thirds"),
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
                Group(NamePlate(false, pack.OverlayId("name")), "Lower Thirds"),
                Group(NamePlate(true, pack.OverlayId("nameTimed")), "Lower Thirds"),
                Group(VerseLowerThird(pack.OverlayId("scriptureVerse")), "Lower Thirds"),
                Group(PointLowerThird(pack.OverlayId("point")), "Lower Thirds"),
                Group(CallToAction(pack.OverlayId("ctaLower"), CardLayout.LowerThird), "Lower Thirds"),
                Group(Ticker(), "Lower Thirds"),
                Group(VerseSideThird(pack.OverlayId("verseSide")), "Side Thirds"),
                Group(PointSideThird(pack.OverlayId("pointSide")), "Side Thirds"),
                Group(CallToAction(pack.OverlayId("cta"), CardLayout.SideThird), "Side Thirds"),
                Group(SearchCard(), "Full Screen"),
                Group(CinematicBars(), "Full Screen"),
            ];
        }

        private Slide SimplifiedThird(string item, string name, Overlay overlay, string primary, HashSet<string> dropping, string folder)
        {
            var kept = overlay.Objects.Where(o => !dropping.Contains(o.Id)).ToList();
            var droppedSteps = overlay.Objects.Where(o => dropping.Contains(o.Id))
                .SelectMany(o => (o.AnimationSteps ?? []).Select(s => s.Id)).ToHashSet();
            var objects = PromotingPrimaryText(kept, primary);
            foreach (var o in objects.Where(o => o.Id == primary))
            {
                foreach (var s in (o.AnimationSteps ?? []).Where(s => s.Kind == AnimationKind.In))
                {
                    s.Trigger = AnimationTrigger.AfterPrevious;
                    s.DelaySeconds = -0.05;
                }
            }
            return new Slide
            {
                Id = $"{ThemeId}.{item}", Name = name, Objects = objects, Folder = folder,
                AnimationOrder = (overlay.AnimationOrder ?? []).Where(id => !droppedSteps.Contains(id)).ToList(),
            };
        }

        private static List<SlideObject> PromotingPrimaryText(List<SlideObject> objects, string primaryId)
        {
            var primaryIndex = objects.FindIndex(o => o.Id == primaryId);
            var firstTextIndex = objects.FindIndex(o => o.ObjectKind == SlideObjectKind.Text);
            if (primaryIndex < 0 || firstTextIndex < 0 || firstTextIndex >= primaryIndex) return objects;
            var result = objects.ToList();
            var primary = result[primaryIndex];
            result.RemoveAt(primaryIndex);
            result.Insert(firstTextIndex, primary);
            return result;
        }

        private Slide ThemeSlide(string item, string name, List<SlideObject> objects, List<string> order, bool fullScreen, string? folder = null) => new()
        {
            Id = $"{ThemeId}.{item}",
            Name = name,
            Objects = objects,
            BackgroundFill = fullScreen ? Fill(look.Backdrop) : null,
            AnimationOrder = order,
            Folder = folder ?? (fullScreen ? "Full Slide" : null),
        };

        private Slide LyricsSlide()
        {
            var b = $"{ThemeId}.lyrics";
            var textIn = TextIn($"{b}.text.in", AnimationTrigger.WithPrevious, 0, 40);
            var textOut = TextOut($"{b}.text.out");
            var ruleIn = RuleIn($"{b}.rule.in", delay: -0.2);
            var ruleOut = RuleOut($"{b}.rule.out");
            return ThemeSlide("lyrics", "Lyrics",
            [
                Label($"{b}.text", "Lyrics", SampleLyrics, Bold ? 100 : 160, 160, Bold ? 1720 : 1600, 700, Bold ? 64 : 84,
                    display: true, font: Editorial ? "Georgia" : null, align: TextHorizontalAlignment.Center,
                    tracking: Bold ? 2 : 0, uppercase: Bold, inset: 0, lineHeight: 1.22, steps: [textIn, textOut]),
                Block($"{b}.rule", "Rule", 880, 900, 160, Bold ? 10 : 4, color: look.Accent, radius: 0, skew: Bold ? -12 : null, steps: [ruleIn, ruleOut]),
            ], Ids(textIn, ruleIn, textOut, ruleOut), fullScreen: true);
        }

        private Overlay LyricsLowerThird(string b)
        {
            var bandIn = PlateIn($"{b}.band.in", edge: AnimationEdge.Bottom, big: true);
            var bandOut = PlateOut($"{b}.band.out", AnimationTrigger.AfterPrevious, -0.05, AnimationEdge.Bottom, big: true);
            var barIn = RuleIn($"{b}.bar.in", delay: -0.3, edge: AnimationEdge.Top, duration: 0.6);
            var barOut = RuleOut($"{b}.bar.out", edge: AnimationEdge.Top);
            var textIn = TextIn($"{b}.text.in", delay: -0.3, rise: 30);
            var textOut = TextOut($"{b}.text.out");
            return new Overlay
            {
                Id = b, Name = "Lyrics (Lower Third)",
                Objects =
                [
                    Block($"{b}.band", "Band", 0, 720, 1920, 280, fill: Gradient(look.Plate2, look.Plate, 90), radius: 0, steps: [bandIn, bandOut]),
                    Block($"{b}.bar", "Bar", 160, 760, Bold ? 18 : 8, 200, color: look.Accent, radius: 0, skew: Bold ? -12 : null, steps: [barIn, barOut]),
                    Label($"{b}.text", "Lyrics", SampleLyrics, 210, 750, 1550, 220, 46, display: Bold, font: Editorial ? "Georgia" : null,
                        uppercase: Bold, inset: 0, lineHeight: 1.15, shrink: true, page: true, steps: [textIn, textOut]),
                ],
                AnimationOrder = Ids(bandIn, barIn, textIn, textOut, bandOut, barOut),
            };
        }

        private Overlay LyricsSideThird(string b)
        {
            var push = new VideoPush { Zoom = 1.05, Backdrop = true };
            var panelIn = PlateIn($"{b}.panel.in", edge: AnimationEdge.Right, big: true, videoPush: push);
            var panelOut = PlateOut($"{b}.panel.out", AnimationTrigger.AfterPrevious, -0.05, AnimationEdge.Right, big: true, videoPush: push);
            var barIn = RuleIn($"{b}.bar.in", delay: -0.3, edge: AnimationEdge.Top, duration: 0.9);
            var barOut = RuleOut($"{b}.bar.out", edge: AnimationEdge.Top);
            var textIn = TextIn($"{b}.text.in", delay: -0.3, rise: 40);
            var textOut = TextOut($"{b}.text.out", toward: AnimationEdge.Right);
            return new Overlay
            {
                Id = b, Name = "Lyrics (Side Third)",
                Objects =
                [
                    Block($"{b}.panel", "Panel", 1280, 0, 640, 1080, fill: Gradient(look.Plate2, look.Plate), radius: 0, steps: [panelIn, panelOut]),
                    Block($"{b}.bar", "Bar", 1280, 0, Bold ? 18 : 6, 1080, color: look.Accent, radius: 0, steps: [barIn, barOut]),
                    Label($"{b}.text", "Lyrics", SampleLyrics, 1340, 220, 520, 640, 44, display: Bold, font: Editorial ? "Georgia" : null,
                        valign: TextVerticalAlignment.Top, uppercase: Bold, inset: 0, lineHeight: 1.2, shrink: true, page: true, steps: [textIn, textOut]),
                ],
                AnimationOrder = Ids(panelIn, barIn, textIn, textOut, panelOut, barOut),
            };
        }

        private Slide PointsSlide()
        {
            var b = $"{ThemeId}.points";
            var lineSteps = Enumerable.Range(0, 3).Select(line =>
            {
                var s = TextIn($"{b}.in{line}", AnimationTrigger.OnClick, null, 36);
                s.Ranges = [new AnimationRange { Line = line, Column = 0, Length = 12 }];
                return s;
            }).ToList();
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
                AnimationSteps = lineSteps,
            };
            var kickerIn = TextIn($"{b}.kicker.in", AnimationTrigger.WithPrevious, 0);
            var ruleIn = RuleIn($"{b}.rule.in", AnimationTrigger.AfterPrevious, -0.2);
            return ThemeSlide("points", "Points",
            [
                Label($"{b}.kicker", "Kicker", "Today's message", 200, 140, 1520, 60, 30, color: look.Accent,
                    tracking: KickerTracking, uppercase: true, inset: 0, steps: [kickerIn]),
                Block($"{b}.rule", "Rule", 224, 205, 160, 4, color: look.Accent, radius: 0, steps: [ruleIn]),
                points,
            ], [kickerIn.Id, ruleIn.Id, .. lineSteps.Select(s => s.Id)], fullScreen: true);
        }

        private Slide SectionTitleSlide()
        {
            var b = $"{ThemeId}.section";
            var kickerIn = TextIn($"{b}.kicker.in", AnimationTrigger.WithPrevious, 0);
            var kickerOut = TextOut($"{b}.kicker.out", AnimationTrigger.WithPrevious);
            var titleIn = TextIn($"{b}.in");
            var titleOut = TextOut($"{b}.out");
            var leftIn = RuleIn($"{b}.rule.in", edge: AnimationEdge.Right);
            var leftOut = RuleOut($"{b}.rule.out", edge: AnimationEdge.Right);
            var rightIn = RuleIn($"{b}.rule2.in", AnimationTrigger.WithPrevious, 0, AnimationEdge.Left);
            var rightOut = RuleOut($"{b}.rule2.out", edge: AnimationEdge.Left);
            var objects = new List<SlideObject>
            {
                Label($"{b}.kicker", "Kicker", "Part one", 160, 340, 1600, 60, 32, color: look.Accent, align: TextHorizontalAlignment.Center,
                    tracking: KickerTracking, uppercase: true, steps: [kickerIn, kickerOut]),
                Label($"{b}.text", "Section Title", "Section Title", 160, 400, 1600, 260, 140, display: true,
                    color: Bold ? look.InkOnAccent : look.Ink, align: TextHorizontalAlignment.Center, tracking: Bold ? 4 : 0, uppercase: Bold,
                    steps: [titleIn, titleOut]),
                Block($"{b}.rule", "Rule Left", 560, 690, 380, 4, color: look.Accent, radius: 0, steps: [leftIn, leftOut]),
                Block($"{b}.rule2", "Rule Right", 980, 690, 380, 4, color: look.Accent, radius: 0, steps: [rightIn, rightOut]),
            };
            var order = Ids(kickerIn, titleIn, leftIn, rightIn, titleOut, kickerOut, leftOut, rightOut);
            if (Bold)
            {
                var blockIn = PlateIn($"{b}.block.in");
                var blockOut = PlateOut($"{b}.block.out", AnimationTrigger.AfterPrevious, -0.2);
                objects.Insert(0, Block($"{b}.block", "Block", 260, 420, 1400, 220, color: look.Accent, skew: -12, steps: [blockIn, blockOut]));
                objects[1].AnimationSteps![0].Trigger = AnimationTrigger.AfterPrevious;
                order.Insert(0, blockIn.Id);
                order.Add(blockOut.Id);
            }
            return ThemeSlide("section", "Section Title", objects, order, fullScreen: true);
        }

        private Slide VerseSlide()
        {
            var b = $"{ThemeId}.verse";
            var barIn = RuleIn($"{b}.bar.in", AnimationTrigger.WithPrevious, null, AnimationEdge.Top, 0.7);
            var barOut = RuleOut($"{b}.bar.out", edge: AnimationEdge.Top);
            var verseIn = TextIn($"{b}.verse.in", delay: -0.35, rise: 40);
            var verseOut = TextOut($"{b}.verse.out");
            var ruleIn = RuleIn($"{b}.rule.in", delay: -0.15);
            var ruleOut = RuleOut($"{b}.rule.out", edge: AnimationEdge.Left);
            var refIn = TextIn($"{b}.ref.in", AnimationTrigger.WithPrevious, 0.1);
            var refOut = TextOut($"{b}.ref.out", AnimationTrigger.WithPrevious);
            return ThemeSlide("verse", "Verse + Reference",
            [
                Block($"{b}.bar", "Bar", 200, 300, Bold ? 22 : 8, 480, color: look.Accent, radius: 0, skew: Bold ? -12 : null, steps: [barIn, barOut]),
                Label($"{b}.verse", "Verse",
                    "For God so loved the world, that he gave his only Son, that whoever believes in him should not perish but have eternal life.",
                    260, 280, 1460, 440, 64, font: Editorial ? "Georgia-Italic" : look.Body, valign: TextVerticalAlignment.Top,
                    inset: 0, lineHeight: 1.15, steps: [verseIn, verseOut]),
                Block($"{b}.rule", "Rule", 260, 760, 140, 4, color: look.Accent, radius: 0, steps: [ruleIn, ruleOut]),
                Label($"{b}.ref", "Reference", "John 3:16 (ESV)", 260, 780, 1460, 60, 34, display: true, color: look.Accent,
                    tracking: KickerTracking - 2, uppercase: !Editorial, inset: 0, steps: [refIn, refOut]),
            ], Ids(barIn, verseIn, ruleIn, refIn, verseOut, refOut, ruleOut, barOut), fullScreen: true);
        }

        private Slide PointSlide()
        {
            var b = $"{ThemeId}.point";
            var kickIn = TextIn($"{b}.kicker.in", AnimationTrigger.WithPrevious, 0);
            var kickOut = TextOut($"{b}.kicker.out", AnimationTrigger.WithPrevious);
            var ruleIn = RuleIn($"{b}.rule.in", delay: -0.2, duration: 0.6);
            var ruleOut = RuleOut($"{b}.rule.out", edge: AnimationEdge.Left);
            var textIn = TextIn($"{b}.text.in", delay: -0.3, rise: 48);
            var textOut = TextOut($"{b}.text.out");
            return ThemeSlide("point", "Point",
            [
                Label($"{b}.kicker", "Kicker", "Point 1", 200, 260, 1520, 60, 34, color: look.Accent, tracking: KickerTracking, uppercase: true,
                    inset: 0, steps: [kickIn, kickOut]),
                Block($"{b}.rule", "Rule", 200, 330, 220, Bold ? 12 : 5, color: look.Accent, radius: 0, steps: [ruleIn, ruleOut]),
                Label($"{b}.text", "Point", "Faith is not the absence of fear — it is trust that outlasts it.",
                    200, 360, 1520, 460, 92, display: true, valign: TextVerticalAlignment.Top, tracking: Bold ? 2 : 0, uppercase: Bold,
                    inset: 0, lineHeight: 1.08,
                    lineFill: Bold ? new TextLineFill { Fill = Fill(look.Accent), WidthMode = TextLineFillWidthMode.LineWidth, VerticalPadding = 2, HorizontalPadding = 28 } : null,
                    steps: [textIn, textOut]),
            ], Ids(kickIn, ruleIn, textIn, textOut, kickOut, ruleOut), fullScreen: true);
        }

        private Slide QuoteSlide()
        {
            var b = $"{ThemeId}.quote";
            var chipIn = PopIn($"{b}.chip.in", AnimationTrigger.WithPrevious, null, 0.5, 0.4);
            var chipOut = PopOut($"{b}.chip.out");
            var markIn = PopIn($"{b}.mark.in", AnimationTrigger.AfterPrevious, -0.05, 0.6, 0.4);
            var markOut = PopOut($"{b}.mark.out", AnimationTrigger.AfterPrevious, -0.15);
            var frameIn = DrawIn($"{b}.frame.in", AnimationTrigger.WithPrevious, 0.1, 1.3);
            var frameOut = FadeOut($"{b}.frame.out", duration: 0.5);
            var bodyIn = TextIn($"{b}.body.in", delay: -0.7, rise: 40);
            var bodyOut = TextOut($"{b}.body.out");
            var ruleIn = RuleIn($"{b}.rule.in", delay: -0.2);
            var ruleOut = RuleOut($"{b}.rule.out", edge: AnimationEdge.Left);
            var sourceIn = TextIn($"{b}.source.in", AnimationTrigger.WithPrevious, 0.15);
            var sourceOut = TextOut($"{b}.source.out", AnimationTrigger.WithPrevious);
            var markFont = Editorial ? "Georgia-Bold" : Bold ? "AvenirNext-Heavy" : look.Display;
            var objects = new List<SlideObject>
            {
                Block($"{b}.frame", "Frame", 300, 250, 1320, 580, fill: Bold ? Fill(look.Plate) : Fill("#00000000"), radius: Clean ? 14 : 0,
                    stroke: new ObjectStroke { ColorHex = look.Accent, Width = Bold ? 10 : 3 }, skew: Bold ? -6 : null, steps: [frameIn, frameOut]),
                Block($"{b}.chip", "Mark Chip", 250, 150, 220, 220, color: Bold ? look.Accent2 : look.Backdrop, radius: Clean ? 20 : 0,
                    skew: Bold ? -6 : null, steps: [chipIn, chipOut]),
                Label($"{b}.mark", "Quote Mark", "“", 250, 130, 220, 280, 300, font: markFont, color: look.Accent,
                    align: TextHorizontalAlignment.Center, inset: 0, steps: [markIn, markOut]),
                Label($"{b}.body", "Quote", "The quote goes here — long enough to wrap onto a second line, and it should still breathe.",
                    400, 330, 1120, 340, 56, font: Editorial ? "Georgia-Italic" : look.Body,
                    align: Bold ? TextHorizontalAlignment.Left : TextHorizontalAlignment.Center, inset: 0, lineHeight: 1.15, steps: [bodyIn, bodyOut]),
                Block($"{b}.rule", "Rule", Bold ? 400 : 880, 700, 160, 4, color: look.Accent, radius: 0, steps: [ruleIn, ruleOut]),
                Label($"{b}.source", "Source", "Attribution", 400, 716, 1120, 60, 30, color: look.Accent,
                    align: Bold ? TextHorizontalAlignment.Left : TextHorizontalAlignment.Center, tracking: KickerTracking,
                    uppercase: !Editorial, inset: 0, steps: [sourceIn, sourceOut]),
            };
            var order = Ids(chipIn, markIn, frameIn, bodyIn, ruleIn, sourceIn, bodyOut, sourceOut, ruleOut, frameOut, markOut, chipOut);
            if (Digital)
            {
                var shadowIn = PopIn($"{b}.mark2.in", AnimationTrigger.WithPrevious, 0.05, 0.6, 0.4);
                var shadowOut = PopOut($"{b}.mark2.out");
                objects.Insert(2, Label($"{b}.mark2", "Quote Mark Shadow", "“", 262, 138, 220, 280, 300, font: markFont, color: look.Accent2,
                    align: TextHorizontalAlignment.Center, inset: 0, steps: [shadowIn, shadowOut]));
                order.Insert(2, shadowIn.Id);
                order.Add(shadowOut.Id);
            }
            return ThemeSlide("quote", "Quote", objects, order, fullScreen: true);
        }

        // MARK: Overlays

        private Overlay NamePlate(bool timed, string b)
        {
            var objects = new List<SlideObject>();
            List<string> order;
            var outTrigger = timed ? AnimationTrigger.AfterPrevious : AnimationTrigger.OnClick;
            double? outDelay = timed ? 6 : null;

            switch (pack)
            {
                case StarterPack.CleanGeometric:
                {
                    var accentIn = PlateIn($"{b}.accent.in");
                    var accentOut = PlateOut($"{b}.accent.out", AnimationTrigger.AfterPrevious, -0.2);
                    var plateIn = Step($"{b}.plate.in", AnimationKind.In, StepAnimation.Wipe, AnimationTrigger.AfterPrevious, -0.3, 0.5, AnimationRamp.Out, edge: AnimationEdge.Left, softEdge: 40);
                    var plateOut = Step($"{b}.plate.out", AnimationKind.Out, StepAnimation.Wipe, AnimationTrigger.WithPrevious, 0.05, 0.4, AnimationRamp.In, edge: AnimationEdge.Left, softEdge: 40);
                    var nameIn = TextIn($"{b}.name.in", delay: -0.15, rise: 90);
                    var nameOut = TextOut($"{b}.name.out", outTrigger, outDelay, distance: 90);
                    var ruleIn = RuleIn($"{b}.rule.in", delay: -0.35, duration: 0.45);
                    var ruleOut = RuleOut($"{b}.rule.out", edge: AnimationEdge.Left);
                    var titleIn = TextIn($"{b}.title.in", AnimationTrigger.WithPrevious, 0.1, -60);
                    var titleOut = TextOut($"{b}.title.out", AnimationTrigger.WithPrevious, distance: 60);
                    objects =
                    [
                        Block($"{b}.plate", "Plate", 160, 820, 960, 170, fill: Gradient(look.Plate, look.Plate2), radius: 0, steps: [plateIn, plateOut]),
                        Block($"{b}.accent", "Accent Bar", 120, 820, 40, 170, color: look.Accent, radius: 0, steps: [accentIn, accentOut]),
                        Matte($"{b}.mask.name", 160, 812, 960, 98),
                        Label($"{b}.name", "Name", "Full Name", 160, 824, 960, 84, 60, display: true, inset: 40, maskedBy: $"{b}.mask.name",
                            shrink: true, page: true, steps: [nameIn, nameOut]),
                        Block($"{b}.rule", "Rule", 200, 910, 300, 4, color: look.Accent2, radius: 0, steps: [ruleIn, ruleOut]),
                        Matte($"{b}.mask.title", 160, 914, 960, 80),
                        Label($"{b}.title", "Title", "Title or role", 160, 920, 960, 60, 32, color: look.Accent2, tracking: 2, inset: 40,
                            maskedBy: $"{b}.mask.title", steps: [titleIn, titleOut]),
                    ];
                    order = Ids(accentIn, plateIn, nameIn, ruleIn, titleIn, nameOut, titleOut, ruleOut, plateOut, accentOut);
                    break;
                }
                case StarterPack.EditorialSerif:
                {
                    var bandIn = PlateIn($"{b}.band.in");
                    var bandOut = PlateOut($"{b}.band.out", AnimationTrigger.AfterPrevious, -0.3);
                    var topIn = RuleIn($"{b}.top.in", delay: -0.7, duration: 0.8);
                    var topOut = RuleOut($"{b}.top.out");
                    var nameIn = TextIn($"{b}.name.in");
                    var nameOut = TextOut($"{b}.name.out", outTrigger, outDelay);
                    var titleIn = TextIn($"{b}.title.in", AnimationTrigger.WithPrevious, 0.2);
                    var titleOut = TextOut($"{b}.title.out", AnimationTrigger.WithPrevious);
                    var bottomIn = RuleIn($"{b}.bottom.in", AnimationTrigger.WithPrevious, 0.1, duration: 0.8);
                    var bottomOut = RuleOut($"{b}.bottom.out", edge: AnimationEdge.Left);
                    objects =
                    [
                        Block($"{b}.band", "Band", 0, 800, 1300, 220, fill: Gradient(look.Plate, look.Plate2), radius: 0, steps: [bandIn, bandOut]),
                        Block($"{b}.top", "Hairline", 140, 828, 720, 2, color: look.Accent, radius: 0, steps: [topIn, topOut]),
                        Label($"{b}.name", "Name", "Full Name", 140, 840, 1000, 92, 66, display: true, inset: 0, shrink: true, page: true, steps: [nameIn, nameOut]),
                        Label($"{b}.title", "Title", "Title or role", 140, 930, 1000, 50, 28, color: look.Accent, tracking: KickerTracking,
                            uppercase: true, inset: 0, steps: [titleIn, titleOut]),
                        Block($"{b}.bottom", "Hairline", 140, 992, 220, 2, color: look.Accent, radius: 0, steps: [bottomIn, bottomOut]),
                    ];
                    order = Ids(bandIn, topIn, nameIn, titleIn, bottomIn, nameOut, titleOut, topOut, bottomOut, bandOut);
                    break;
                }
                case StarterPack.BoldBlocks:
                {
                    var yellowIn = PlateIn($"{b}.yellow.in");
                    var yellowOut = PlateOut($"{b}.yellow.out", AnimationTrigger.AfterPrevious, -0.2);
                    var nameIn = TextIn($"{b}.name.in");
                    var nameOut = TextOut($"{b}.name.out", outTrigger, outDelay);
                    var blackIn = PlateIn($"{b}.black.in", AnimationTrigger.WithPrevious, 0.1);
                    var blackOut = PlateOut($"{b}.black.out", AnimationTrigger.WithPrevious, 0.05);
                    var titleIn = TextIn($"{b}.title.in");
                    var titleOut = TextOut($"{b}.title.out", AnimationTrigger.WithPrevious);
                    var chipIn = PopIn($"{b}.chip.in", delay: -0.1, duration: 0.35, from: 0);
                    var chipOut = PopOut($"{b}.chip.out");
                    objects =
                    [
                        Block($"{b}.yellow", "Yellow Block", 120, 800, 820, 140, color: look.Accent, skew: -12, steps: [yellowIn, yellowOut]),
                        Label($"{b}.name", "Name", "Full Name", 150, 800, 790, 140, 72, display: true, color: look.InkOnAccent, tracking: 2,
                            uppercase: true, inset: 40, shrink: true, page: true, steps: [nameIn, nameOut]),
                        Block($"{b}.black", "Black Block", 200, 940, 680, 64, color: look.Accent2, skew: -12, steps: [blackIn, blackOut]),
                        Label($"{b}.title", "Title", "Title or role", 230, 940, 650, 64, 30, color: look.Ink, tracking: 5, uppercase: true,
                            inset: 40, steps: [titleIn, titleOut]),
                        Block($"{b}.chip", "Chip", 94, 940, 64, 64, color: look.Accent, skew: -12, steps: [chipIn, chipOut]),
                    ];
                    order = Ids(yellowIn, nameIn, blackIn, titleIn, chipIn, nameOut, titleOut, chipOut, blackOut, yellowOut);
                    break;
                }
                default:
                {
                    var plateIn = PlateIn($"{b}.plate.in");
                    var plateOut = PlateOut($"{b}.plate.out", AnimationTrigger.AfterPrevious, -0.3);
                    var lbIn = PopIn($"{b}.lb.in", AnimationTrigger.AfterPrevious, -0.05, 0.5, 0.2);
                    var lbOut = PopOut($"{b}.lb.out");
                    var rbIn = PopIn($"{b}.rb.in", AnimationTrigger.WithPrevious, 0, 0.5, 0.2);
                    var rbOut = PopOut($"{b}.rb.out");
                    var kickIn = TypeIn($"{b}.kicker.in", delay: -0.3, duration: 0.8);
                    var kickOut = TextOut($"{b}.kicker.out", AnimationTrigger.WithPrevious);
                    var nameIn = TextIn($"{b}.name.in", delay: -0.3);
                    var nameOut = TextOut($"{b}.name.out", outTrigger, outDelay);
                    var bandIn = RuleIn($"{b}.band.in", delay: -0.5, duration: 0.6);
                    var bandOut = RuleOut($"{b}.band.out");
                    var titleIn = TextIn($"{b}.title.in", AnimationTrigger.WithPrevious, 0.15);
                    var titleOut = TextOut($"{b}.title.out", AnimationTrigger.WithPrevious);
                    objects =
                    [
                        Block($"{b}.plate", "Plate", 120, 810, 960, 190, color: look.Plate, stroke: new ObjectStroke { ColorHex = look.Accent, Width = 1.5 }, steps: [plateIn, plateOut]),
                        Label($"{b}.lb", "Bracket", "[", 96, 800, 90, 210, 150, display: true, color: look.Accent, align: TextHorizontalAlignment.Center, inset: 0, steps: [lbIn, lbOut]),
                        Label($"{b}.rb", "Bracket", "]", 1014, 800, 90, 210, 150, display: true, color: look.Accent, align: TextHorizontalAlignment.Center, inset: 0, steps: [rbIn, rbOut]),
                        Label($"{b}.kicker", "Kicker", "// speaker", 160, 822, 800, 40, 22, color: look.Accent2, tracking: 4, inset: 0, steps: [kickIn, kickOut]),
                        Label($"{b}.name", "Name", "Full Name", 160, 858, 880, 80, 58, display: true, inset: 0, shrink: true, page: true, steps: [nameIn, nameOut]),
                        Block($"{b}.band", "Band", 160, 942, 760, 36, fill: Gradient(look.Accent, look.Accent2), radius: 0, steps: [bandIn, bandOut]),
                        Label($"{b}.title", "Title", "Title or role", 160, 942, 760, 36, 22, color: look.InkOnAccent, tracking: 3, uppercase: true,
                            inset: 16, steps: [titleIn, titleOut]),
                    ];
                    order = Ids(plateIn, lbIn, rbIn, kickIn, nameIn, bandIn, titleIn, nameOut, titleOut, kickOut, bandOut, lbOut, rbOut, plateOut);
                    break;
                }
            }
            return new Overlay { Id = b, Name = timed ? "Name + Title (timed)" : "Name + Title", Objects = objects, AnimationOrder = order };
        }

        private Overlay VerseLowerThird(string b)
        {
            var bandIn = PlateIn($"{b}.band.in", edge: AnimationEdge.Bottom, big: true);
            var bandOut = PlateOut($"{b}.band.out", AnimationTrigger.AfterPrevious, -0.25, AnimationEdge.Bottom, big: true);
            var barIn = RuleIn($"{b}.bar.in", delay: -0.3, edge: AnimationEdge.Top, duration: 0.6);
            var barOut = RuleOut($"{b}.bar.out", edge: AnimationEdge.Top);
            var verseIn = TextIn($"{b}.verse.in", delay: -0.3, rise: 30);
            var verseOut = TextOut($"{b}.verse.out");
            var refPlateIn = Editorial
                ? RuleIn($"{b}.refplate.in", delay: 0.05, duration: 0.5)
                : PlateIn($"{b}.refplate.in", AnimationTrigger.AfterPrevious, -0.1, AnimationEdge.Right);
            var refPlateOut = Editorial
                ? RuleOut($"{b}.refplate.out", AnimationTrigger.AfterPrevious, -0.05)
                : PlateOut($"{b}.refplate.out", AnimationTrigger.AfterPrevious, -0.05, AnimationEdge.Right);
            var refIn = TextIn($"{b}.ref.in");
            var refOut = TextOut($"{b}.ref.out", AnimationTrigger.WithPrevious);
            return new Overlay
            {
                Id = b, Name = "Verse + Reference",
                Objects =
                [
                    Block($"{b}.band", "Band", 0, 720, 1920, 280, fill: Gradient(look.Plate2, look.Plate, 90), radius: 0, steps: [bandIn, bandOut]),
                    Block($"{b}.bar", "Bar", 160, 760, Bold ? 18 : 8, 130, color: look.Accent, radius: 0, skew: Bold ? -12 : null, steps: [barIn, barOut]),
                    Label($"{b}.verse", "Verse", "For God so loved the world, that he gave his only Son, that whoever believes in him should not perish but have eternal life.",
                        210, 750, 1500, 150, 42, font: Editorial ? "Georgia-Italic" : look.Body, inset: 0, lineHeight: 1.12, shrink: true, page: true, steps: [verseIn, verseOut]),
                    Block($"{b}.refplate", Editorial ? "Hairline" : "Reference Plate", 1380, Editorial ? 972 : 914, 380, Editorial ? 2 : 56,
                        color: look.Accent, radius: Clean ? 28 : 0, skew: Bold ? -12 : null, steps: [refPlateIn, refPlateOut]),
                    Label($"{b}.ref", "Reference", "John 3:16 (ESV)", 1380, 914, 380, 56, 26, display: true,
                        color: Editorial ? look.Accent : look.InkOnAccent, align: Editorial ? TextHorizontalAlignment.Right : TextHorizontalAlignment.Center,
                        tracking: KickerTracking - 2, uppercase: !Editorial, inset: Editorial ? 0 : 24, steps: [refIn, refOut]),
                ],
                AnimationOrder = Ids(bandIn, barIn, verseIn, refPlateIn, refIn, verseOut, refOut, refPlateOut, barOut, bandOut),
            };
        }

        private Overlay PointLowerThird(string b)
        {
            var bandIn = PlateIn($"{b}.band.in", edge: AnimationEdge.Bottom, big: true);
            var bandOut = PlateOut($"{b}.band.out", AnimationTrigger.AfterPrevious, -0.05, AnimationEdge.Bottom, big: true);
            var tagIn = PopIn($"{b}.tag.in", delay: 0, from: 0.6);
            var tagOut = PopOut($"{b}.tag.out");
            var kickIn = TextIn($"{b}.kicker.in", AnimationTrigger.WithPrevious, 0.05);
            var kickOut = TextOut($"{b}.kicker.out", AnimationTrigger.WithPrevious);
            var textIn = TextIn($"{b}.text.in", delay: -0.2, rise: 36);
            var textOut = TextOut($"{b}.text.out");
            return new Overlay
            {
                Id = b, Name = "Point",
                Objects =
                [
                    Block($"{b}.band", "Band", 0, 760, 1920, 240, fill: Gradient(look.Plate2, look.Plate, 90), radius: 0, steps: [bandIn, bandOut]),
                    Block($"{b}.tag", "Tag", 160, 792, 200, 48, color: look.Accent, radius: Clean ? 24 : 0, skew: Bold ? -12 : null, steps: [tagIn, tagOut]),
                    Label($"{b}.kicker", "Kicker", "Point 1", 160, 792, 200, 48, 22, display: true, color: look.InkOnAccent, align: TextHorizontalAlignment.Center,
                        tracking: 3, uppercase: true, inset: 0, steps: [kickIn, kickOut]),
                    Label($"{b}.text", "Point", "Faith is not the absence of fear — it is trust that outlasts it.",
                        160, 850, 1600, 130, 50, display: true, tracking: Bold ? 2 : 0, uppercase: Bold, inset: 0, lineHeight: 1.08, shrink: true, page: true,
                        steps: [textIn, textOut]),
                ],
                AnimationOrder = Ids(bandIn, tagIn, kickIn, textIn, textOut, kickOut, bandOut, tagOut),
            };
        }

        private Overlay PointSideThird(string b)
        {
            var push = new VideoPush { Zoom = 1.05, Backdrop = true };
            var panelIn = PlateIn($"{b}.panel.in", edge: AnimationEdge.Right, big: true, videoPush: push);
            var panelOut = PlateOut($"{b}.panel.out", AnimationTrigger.AfterPrevious, -0.05, AnimationEdge.Right, big: true, videoPush: push);
            var barIn = RuleIn($"{b}.bar.in", delay: -0.3, edge: AnimationEdge.Top, duration: 0.9);
            var barOut = RuleOut($"{b}.bar.out", edge: AnimationEdge.Top);
            var kickIn = TextIn($"{b}.kicker.in", delay: -0.3);
            var kickOut = TextOut($"{b}.kicker.out", AnimationTrigger.WithPrevious, toward: AnimationEdge.Right);
            var textIn = TextIn($"{b}.text.in", AnimationTrigger.WithPrevious, 0.15, 40);
            var textOut = TextOut($"{b}.text.out", toward: AnimationEdge.Right);
            return new Overlay
            {
                Id = b, Name = "Point (Side Third)",
                Objects =
                [
                    Block($"{b}.panel", "Panel", 1280, 0, 640, 1080, fill: Gradient(look.Plate2, look.Plate), radius: 0, steps: [panelIn, panelOut]),
                    Block($"{b}.bar", "Bar", 1280, 0, Bold ? 18 : 6, 1080, color: look.Accent, radius: 0, steps: [barIn, barOut]),
                    Label($"{b}.kicker", "Kicker", "Point 1", 1340, 300, 520, 50, 28, color: look.Accent, tracking: KickerTracking, uppercase: true,
                        inset: 0, steps: [kickIn, kickOut]),
                    Label($"{b}.text", "Point", "Faith is not the absence of fear — it is trust that outlasts it.",
                        1340, 360, 520, 440, 50, display: true, valign: TextVerticalAlignment.Top, uppercase: Bold, inset: 0, lineHeight: 1.1,
                        shrink: true, page: true, steps: [textIn, textOut]),
                ],
                AnimationOrder = Ids(panelIn, barIn, kickIn, textIn, textOut, kickOut, panelOut, barOut),
            };
        }

        private Overlay VerseSideThird(string b)
        {
            var push = new VideoPush { Zoom = 1.05, Backdrop = true };
            var panelIn = PlateIn($"{b}.panel.in", edge: AnimationEdge.Right, big: true, videoPush: push);
            var panelOut = PlateOut($"{b}.panel.out", AnimationTrigger.AfterPrevious, -0.05, AnimationEdge.Right, big: true, videoPush: push);
            var barIn = RuleIn($"{b}.bar.in", delay: -0.3, edge: AnimationEdge.Top, duration: 0.9);
            var barOut = RuleOut($"{b}.bar.out", edge: AnimationEdge.Top);
            var verseIn = TextIn($"{b}.verse.in", delay: -0.3, rise: 40);
            var verseOut = TextOut($"{b}.verse.out", toward: AnimationEdge.Right);
            var ruleIn = RuleIn($"{b}.rule.in", delay: -0.15);
            var ruleOut = RuleOut($"{b}.rule.out", edge: AnimationEdge.Right);
            var refIn = TextIn($"{b}.ref.in", AnimationTrigger.WithPrevious, 0.1);
            var refOut = TextOut($"{b}.ref.out", AnimationTrigger.WithPrevious, toward: AnimationEdge.Right);
            return new Overlay
            {
                Id = b, Name = "Verse + Reference (Side Third)",
                Objects =
                [
                    Block($"{b}.panel", "Panel", 1280, 0, 640, 1080, fill: Gradient(look.Plate2, look.Plate), radius: 0, steps: [panelIn, panelOut]),
                    Block($"{b}.bar", "Bar", 1280, 0, Bold ? 18 : 6, 1080, color: look.Accent, radius: 0, steps: [barIn, barOut]),
                    Label($"{b}.verse", "Verse", "For God so loved the world, that he gave his only Son, that whoever believes in him should not perish but have eternal life.",
                        1340, 240, 520, 520, 40, font: Editorial ? "Georgia-Italic" : look.Body, valign: TextVerticalAlignment.Top, inset: 0,
                        lineHeight: 1.15, shrink: true, page: true, steps: [verseIn, verseOut]),
                    Block($"{b}.rule", "Rule", 1340, 780, 120, 4, color: look.Accent, radius: 0, steps: [ruleIn, ruleOut]),
                    Label($"{b}.ref", "Reference", "John 3:16 (ESV)", 1340, 796, 520, 50, 26, display: true, color: look.Accent,
                        tracking: KickerTracking - 2, uppercase: !Editorial, inset: 0, steps: [refIn, refOut]),
                ],
                AnimationOrder = Ids(panelIn, barIn, verseIn, ruleIn, refIn, verseOut, refOut, panelOut, ruleOut, barOut),
            };
        }

        private Overlay CallToAction(string b, CardLayout layout)
        {
            var (cx, cy, cw, ch) = layout switch
            {
                CardLayout.SideThird => (1220.0, 110.0, 600.0, 340.0),
                CardLayout.LowerThird => (120.0, 780.0, 1680.0, 220.0),
                _ => (360.0, 240.0, 1200.0, 600.0),
            };
            var full = layout == CardLayout.Full;
            var pad = full ? 72.0 : 40;
            var headSize = full ? 80.0 : 44;
            var bodySize = full ? 40.0 : 28;
            var cardEdge = layout == CardLayout.LowerThird ? AnimationEdge.Bottom : AnimationEdge.Right;
            var away = layout == CardLayout.SideThird ? AnimationEdge.Top : AnimationEdge.Bottom;
            var stroked = Editorial || Digital;
            var cardIn = stroked
                ? DrawIn($"{b}.card.in", AnimationTrigger.WithPrevious, duration: 1.2)
                : Clean || full
                    ? Step($"{b}.card.in", AnimationKind.In, StepAnimation.Scale, AnimationTrigger.WithPrevious, null, 0.55, AnimationRamp.Out, true, fromScale: 0.9)
                    : PlateIn($"{b}.card.in", edge: cardEdge);
            var cardOut = Clean || full
                ? Step($"{b}.card.out", AnimationKind.Out, StepAnimation.Scale, AnimationTrigger.AfterPrevious, -0.05, 0.45, AnimationRamp.In, true, fromScale: 0.9)
                : PlateOut($"{b}.card.out", AnimationTrigger.AfterPrevious, -0.05, cardEdge);
            if (layout == CardLayout.SideThird)
            {
                cardIn.VideoPush = new VideoPush { Zoom = 1.05, Backdrop = true };
                cardOut.VideoPush = new VideoPush { Zoom = 1.05, Backdrop = true };
            }
            var dotIn = PopIn($"{b}.dot.in");
            var dotOut = PopOut($"{b}.dot.out");
            var headIn = TextIn($"{b}.head.in", delay: -0.2);
            var headOut = TextOut($"{b}.head.out", toward: away);
            var bodyIn = TextIn($"{b}.body.in", AnimationTrigger.WithPrevious, 0.12);
            var bodyOut = TextOut($"{b}.body.out", AnimationTrigger.WithPrevious, toward: away);
            var pillIn = PopIn($"{b}.pill.in", AnimationTrigger.AfterPrevious, -0.15, 0.45, 0.7);
            var pillOut = PopOut($"{b}.pill.out");
            var pillTextIn = TextIn($"{b}.pilltext.in", AnimationTrigger.WithPrevious, 0.1);
            var pillTextOut = TextOut($"{b}.pilltext.out", AnimationTrigger.WithPrevious, toward: away);
            var dotSize = full ? 72.0 : 56;
            var headX = cx + pad + dotSize + 20;
            var bodyY = cy + pad + (full ? 110 : 76);
            var pillY = cy + ch - pad - 60;
            var bodyW = layout == CardLayout.LowerThird ? cw - pad * 2 - 360 : cw - pad * 2;
            var pillX = layout == CardLayout.LowerThird ? cx + cw - pad - 260 : cx + pad;
            var pillTop = layout == CardLayout.LowerThird ? cy + (ch - 60) / 2 : pillY;
            return new Overlay
            {
                Id = b, Name = "Call to Action",
                Objects =
                [
                    Block($"{b}.card", "Card", cx, cy, cw, ch, fill: Bold ? Fill(look.Plate) : Gradient(look.Plate, look.Plate2, 135),
                        radius: Clean ? 18 : look.Radius,
                        stroke: stroked ? new ObjectStroke { ColorHex = look.Accent, Width = Digital ? 1.5 : 2 } : null,
                        shadow: Clean ? new ObjectShadow { ColorHex = "#00000066", BlurRadius = 30, OffsetX = 0, OffsetY = 12 } : null,
                        skew: Bold && layout != CardLayout.LowerThird ? -6 : null, steps: [cardIn, cardOut]),
                    Block($"{b}.dot", "Icon", cx + pad, cy + pad, dotSize, dotSize, color: look.Accent, radius: dotSize / 2, ellipse: !Bold, steps: [dotIn, dotOut]),
                    Label($"{b}.head", "Heading", "Next Steps", headX, cy + pad - 4, cx + cw - pad - headX, dotSize + 8, headSize, display: true,
                        tracking: Bold ? 2 : 0, uppercase: Bold, inset: 0, shrink: !full, page: !full, steps: [headIn, headOut]),
                    Label($"{b}.body", "Body", "Text NEXT to 555-0100 or stop by the Welcome Desk after the service.", cx + pad, bodyY, bodyW,
                        layout == CardLayout.LowerThird ? 70 : pillY - bodyY - 16, bodySize, valign: TextVerticalAlignment.Top, inset: 0, lineHeight: 1.1,
                        shrink: !full, page: !full, steps: [bodyIn, bodyOut]),
                    Block($"{b}.pill", "Button", pillX, pillTop, 260, 60, color: look.Accent, radius: Clean ? 30 : look.Radius, skew: Bold ? -12 : null,
                        steps: [pillIn, pillOut]),
                    Label($"{b}.pilltext", "Button Label", "Text NEXT", pillX, pillTop, 260, 60, 24, display: true, color: look.InkOnAccent,
                        align: TextHorizontalAlignment.Center, tracking: 2, uppercase: true, inset: 0, steps: [pillTextIn, pillTextOut]),
                ],
                AnimationOrder = Ids(cardIn, dotIn, headIn, bodyIn, pillIn, pillTextIn, headOut, bodyOut, pillTextOut, cardOut, pillOut, dotOut),
            };
        }

        private Overlay SearchCard()
        {
            var b = pack.OverlayId("search");
            var fieldIn = DrawIn($"{b}.field.in", AnimationTrigger.WithPrevious, duration: 1.1);
            var fieldOut = PopOut($"{b}.field.out", AnimationTrigger.AfterPrevious, -0.15);
            fieldIn.VideoPush = new VideoPush { Mode = VideoPushMode.BlurBackground, Zoom = 1.15 };
            fieldOut.VideoPush = new VideoPush { Mode = VideoPushMode.BlurBackground, Zoom = 1.15 };
            var glyphIn = PopIn($"{b}.glyph.in", delay: -0.2);
            var glyphOut = PopOut($"{b}.glyph.out");
            var queryIn = TypeIn($"{b}.query.in", duration: 1.8);
            var queryOut = TextOut($"{b}.query.out");
            var fieldFill = Editorial ? Fill("#00000000") : Bold ? Fill(look.Ink) : Fill(look.Plate);
            return new Overlay
            {
                Id = b, Name = "Search",
                Objects =
                [
                    Block($"{b}.field", "Field", 460, 470, 1000, 120, fill: fieldFill, radius: Clean ? 60 : look.Radius,
                        stroke: new ObjectStroke { ColorHex = look.Accent, Width = Bold ? 8 : Editorial ? 1.5 : 2 }, skew: Bold ? -8 : null,
                        steps: [fieldIn, fieldOut]),
                    new SlideObject
                    {
                        Id = $"{b}.glyph", ObjectKind = SlideObjectKind.Shape, Name = "Search Icon", Text = "", X = 504, Y = 502, Width = 56, Height = 56,
                        ShapeKind = ShapeKind.Path, PathData = Icons.Search, Fill = Fill(look.Accent), AnimationSteps = [glyphIn, glyphOut],
                    },
                    Label($"{b}.query", "Query", "how do I find peace?", 584, 470, 840, 120, 44, color: Bold ? look.InkOnAccent : look.Ink,
                        inset: 0, steps: [queryIn, queryOut]),
                ],
                AnimationOrder = Ids(fieldIn, glyphIn, queryIn, queryOut, glyphOut, fieldOut),
            };
        }

        private Overlay CinematicBars()
        {
            var b = pack.OverlayId("bars");
            var topIn = Step($"{b}.top.in", AnimationKind.In, StepAnimation.Move, AnimationTrigger.WithPrevious, duration: 3.5, ramp: AnimationRamp.Both, edge: AnimationEdge.Top);
            var bottomIn = Step($"{b}.bottom.in", AnimationKind.In, StepAnimation.Move, AnimationTrigger.WithPrevious, duration: 3.5, ramp: AnimationRamp.Both, edge: AnimationEdge.Bottom);
            var topOut = Step($"{b}.top.out", AnimationKind.Out, StepAnimation.Move, AnimationTrigger.OnDismiss, duration: 3.0, ramp: AnimationRamp.Both, edge: AnimationEdge.Top);
            var bottomOut = Step($"{b}.bottom.out", AnimationKind.Out, StepAnimation.Move, AnimationTrigger.WithPrevious, duration: 3.0, ramp: AnimationRamp.Both, edge: AnimationEdge.Bottom);
            var edgeIn = RuleIn($"{b}.edge.in", delay: -1.0, duration: 2.0);
            var edgeOut = RuleOut($"{b}.edge.out");
            var edge2In = RuleIn($"{b}.edge2.in", AnimationTrigger.WithPrevious, 0, AnimationEdge.Right, 2.0);
            var edge2Out = RuleOut($"{b}.edge2.out", edge: AnimationEdge.Right);
            return new Overlay
            {
                Id = b, Name = "Cinematic Bars",
                Objects =
                [
                    Block($"{b}.top", "Top Bar", 0, 0, 1920, 140, color: "#000000FF", radius: 0, steps: [topIn, topOut]),
                    Block($"{b}.bottom", "Bottom Bar", 0, 940, 1920, 140, color: "#000000FF", radius: 0, steps: [bottomIn, bottomOut]),
                    Block($"{b}.edge", "Top Edge", 0, 138, 1920, 2, color: look.Accent, radius: 0, steps: [edgeIn, edgeOut]),
                    Block($"{b}.edge2", "Bottom Edge", 0, 940, 1920, 2, color: look.Accent, radius: 0, steps: [edge2In, edge2Out]),
                ],
                AnimationOrder = Ids(topIn, bottomIn, edgeIn, edge2In, topOut, bottomOut, edgeOut, edge2Out),
            };
        }

        private Overlay Ticker()
        {
            var b = pack.OverlayId("ticker");
            var plateIn = Step($"{b}.plate.in", AnimationKind.In, StepAnimation.Wipe, AnimationTrigger.WithPrevious, duration: 0.6, ramp: AnimationRamp.Out, edge: AnimationEdge.Left, softEdge: 30);
            var plateOut = Step($"{b}.plate.out", AnimationKind.Out, StepAnimation.Wipe, AnimationTrigger.AfterPrevious, -0.1, 0.5, AnimationRamp.In, edge: AnimationEdge.Left, softEdge: 30);
            var tagIn = PopIn($"{b}.tag.in", delay: 0, from: 0.6);
            var tagOut = PopOut($"{b}.tag.out");
            var tagTextIn = TextIn($"{b}.tagtext.in", AnimationTrigger.WithPrevious, 0.05);
            var tagTextOut = TextOut($"{b}.tagtext.out", AnimationTrigger.WithPrevious);
            var lineIn = FadeIn($"{b}.text.in");
            var lineOut = FadeOut($"{b}.text.out", AnimationTrigger.OnDismiss);
            var line = new SlideObject
            {
                Id = $"{b}.text", ObjectKind = SlideObjectKind.Text, Name = "Ticker Text",
                Text = "Welcome — service times, upcoming events and announcements scroll here.   •   Next Steps class Sunday 9am   •   Text NEXT to 555-0100",
                X = 300, Y = 990, Width = 1620, Height = 80,
                TextStyle = new TextStyle
                {
                    FontName = look.Body, FontSize = 38, ColorHex = look.Ink,
                    HorizontalAlignment = TextHorizontalAlignment.Left, VerticalAlignment = TextVerticalAlignment.Middle,
                    Scroll = new BlockScroll { Axis = BlockScrollAxis.Left, Speed = 170 },
                },
                AnimationSteps = [lineIn, lineOut],
            };
            return new Overlay
            {
                Id = b, Name = "Ticker",
                Objects =
                [
                    Block($"{b}.plate", "Plate", 0, 990, 1920, 80, fill: Gradient(look.Plate, look.Plate2), radius: 0, steps: [plateIn, plateOut]),
                    line,
                    Block($"{b}.tag", "Tag", 0, 990, 280, 80, color: look.Accent, radius: 0, skew: Bold ? -12 : null, steps: [tagIn, tagOut]),
                    Label($"{b}.tagtext", "Tag Text", "Announcements", 0, 990, 280, 80, 24, display: true, color: look.InkOnAccent,
                        align: TextHorizontalAlignment.Center, tracking: 2, uppercase: true, inset: 0, steps: [tagTextIn, tagTextOut]),
                ],
                AnimationOrder = Ids(plateIn, tagIn, tagTextIn, lineIn, lineOut, tagTextOut, tagOut, plateOut),
            };
        }
    }
}
