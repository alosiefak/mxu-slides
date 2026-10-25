using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Library;

/// <summary>The four looks the gallery's lower thirds come in.</summary>
public enum LowerThirdStyle
{
    /// <summary>Clean and minimal: a quiet dark plate, one thin accent, Poppins.</summary>
    Minimal,

    /// <summary>Bold broadcast: solid plates, a coloured accent bar, Montserrat in capitals.</summary>
    Broadcast,

    /// <summary>Glass: see-through frosted panels with a bright edge.</summary>
    Glass,

    /// <summary>Editorial: words straight on the picture over a soft shadow, DM Serif Display and fine rules.</summary>
    Editorial,
}

/// <summary>
/// One design in the lower-thirds gallery: its name, what it's for (<see cref="Category"/>), its
/// look, a line about it, and how to make it. Each call to <see cref="Make"/> gives a fresh overlay.
/// </summary>
public sealed record LowerThirdTemplate(string Id, string Name, string Category, LowerThirdStyle Style, string Description, Func<Overlay> Make)
{
    public string StyleName => Style switch
    {
        LowerThirdStyle.Minimal => "Clean",
        LowerThirdStyle.Broadcast => "Broadcast",
        LowerThirdStyle.Glass => "Glass",
        _ => "Editorial",
    };
}

/// <summary>
/// The lower-thirds gallery: designed, animated overlays for the things a church puts on screen
/// (a speaker's name, a scripture reference, a sermon point, the song's credits and CCLI numbers,
/// social handles, giving, announcements, prayer, a baptism, a welcome, a LIVE bug, "starting soon"
/// and countdowns), each in one of four looks.
/// <para>
/// They're ordinary overlays (Overlay documents, built only from the shared format's shapes, text,
/// masks and step kinds, with the bundled fonts), so once added to the library they show in the
/// Overlays bin and the overlay editor like any other, and open on a Mac. Their text boxes are the
/// fields the operator fills in. Everything sits inside the title-safe area, and is laid out on the
/// 1920 × 1080 canvas every screen size scales from, so a 4K output draws it sharp at the same place.
/// </para>
/// <para>
/// The motion is staggered and quiet: plates first, then words rising out of a mask or coming into
/// focus, the small print a beat after; on the way out, the words go first and the plates follow.
/// Some headlines build word by word (ranged steps, see <see cref="KineticText"/>), and follow new
/// words typed into them.
/// </para>
/// Windows-only (the Mac has its eleven starter overlays per pack, which Windows has too).
/// </summary>
public static class LowerThirdGallery
{
    /// <summary>The categories, in the order the gallery lists them.</summary>
    public static readonly string[] Categories =
        ["Speaker", "Scripture", "Sermon Point", "Song Credits", "Social", "Giving", "Announcement", "Prayer", "Celebration", "Welcome", "Live", "Starting Soon", "Countdown"];

    /// <summary>The pack the gallery's overlays go into unless another is chosen.</summary>
    public const string DefaultPack = "Lower Thirds";

    public static IReadOnlyList<LowerThirdTemplate> All { get; } = Build();

    /// <summary>
    /// A new overlay from a template, ready for the library: its own id, filed in
    /// "<paramref name="pack"/>/&lt;category&gt;", named after the template.
    /// </summary>
    public static Overlay Instantiate(LowerThirdTemplate template, string pack = DefaultPack)
    {
        var overlay = template.Make();
        overlay.Id = LibraryStore.NewId();
        overlay.Folder = OverlayLibrary.FolderFor(OverlayLibrary.ValidPackName(pack) ?? DefaultPack, template.Category);
        return overlay;
    }

    /// <summary>
    /// Fills a Song Credits overlay from a song's CCLI details: the title, the writers and the song
    /// number (the church's own licence number isn't in a song, so that field stays as typed).
    /// Fields it can't fill are left alone. Returns true when anything changed.
    /// </summary>
    public static bool FillCredits(Overlay overlay, Presentation song)
    {
        var changed = false;
        void Set(string field, string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || overlay.Objects.FirstOrDefault(o => o.ObjectKind == SlideObjectKind.Text && o.Name == field) is not { } o
                || o.Text == value) return;
            o.Text = value;
            changed = true;
        }
        var ccli = song.Ccli;
        Set("Song Title", ccli?.SongTitle is { Length: > 0 } title ? title : song.Name);
        Set("Writers", ccli?.Author is { Length: > 0 } author ? $"Words and music by {author}" : null);
        Set("CCLI Song", ccli?.SongNumber is { } number and > 0 ? $"CCLI Song # {number}" : null);
        if (changed) KineticText.Reflow(overlay);
        return changed;
    }

    /// <summary>Points a countdown overlay's digits at one timer (null: whichever is running, or the first).</summary>
    public static void UseTimer(Overlay overlay, string? timerId)
    {
        foreach (var o in overlay.Objects.Where(o => o.TextLink is { Source: TextSourceKind.Timer }))
            o.TextLink!.TimerId = string.IsNullOrEmpty(timerId) ? null : timerId;
    }

    // MARK: Building

    private static List<LowerThirdTemplate> Build()
    {
        LowerThirdTemplate T(string id, string name, string category, LowerThirdStyle style, string description, Action<Composer> design) =>
            new($"gallery.{id}", name, category, style, description, () =>
            {
                var c = new Composer($"gallery.{id}", style);
                design(c);
                return c.Build(name);
            });
        return
        [
            T("speaker.clean", "Speaker", "Speaker", LowerThirdStyle.Minimal,
                "Name and title on a quiet plate; the words rise out of it.", Designs.SpeakerClean),
            T("speaker.broadcast", "Speaker", "Speaker", LowerThirdStyle.Broadcast,
                "A solid name plate and a coloured title band behind an accent bar.", Designs.SpeakerBroadcast),
            T("speaker.glass", "Speaker", "Speaker", LowerThirdStyle.Glass,
                "A frosted glass panel; the name arrives word by word.", Designs.SpeakerGlass),
            T("speaker.editorial", "Speaker", "Speaker", LowerThirdStyle.Editorial,
                "A serif name straight on the picture, coming into focus over a fine rule.", Designs.SpeakerEditorial),
            T("scripture.editorial", "Scripture", "Scripture", LowerThirdStyle.Editorial,
                "The verse in italic serif, centred, with its reference under a hairline.", Designs.ScriptureEditorial),
            T("scripture.glass", "Scripture", "Scripture", LowerThirdStyle.Glass,
                "The verse and its reference on a wide glass card.", Designs.ScriptureGlass),
            T("point.broadcast", "Sermon Point", "Sermon Point", LowerThirdStyle.Broadcast,
                "A numbered tag and the point in capitals, word by word.", Designs.PointBroadcast),
            T("point.clean", "Sermon Point", "Sermon Point", LowerThirdStyle.Minimal,
                "A small kicker and the point, its words rising in turn.", Designs.PointClean),
            T("credits.clean", "Song Credits", "Song Credits", LowerThirdStyle.Minimal,
                "Title, writers and the CCLI song and licence numbers, small and tidy.", Designs.CreditsClean),
            T("credits.editorial", "Song Credits", "Song Credits", LowerThirdStyle.Editorial,
                "The song's credits centred in serif, with the CCLI line beneath.", Designs.CreditsEditorial),
            T("social.glass", "Social Handle", "Social", LowerThirdStyle.Glass,
                "Follow along: three icons drawing round, and your handle typed out.", Designs.SocialGlass),
            T("social.broadcast", "Social Strip", "Social", LowerThirdStyle.Broadcast,
                "A bar across the bottom with your handle and website.", Designs.SocialBroadcast),
            T("giving.clean", "Giving", "Giving", LowerThirdStyle.Minimal,
                "Text to give, and the web address, on a quiet plate.", Designs.GivingClean),
            T("giving.broadcast", "Giving", "Giving", LowerThirdStyle.Broadcast,
                "A bold giving band with a bright call to action.", Designs.GivingBroadcast),
            T("event.broadcast", "Event", "Announcement", LowerThirdStyle.Broadcast,
                "A date block beside the event's name, time and place.", Designs.EventBroadcast),
            T("announcement.glass", "Announcement", "Announcement", LowerThirdStyle.Glass,
                "A glass card with a headline, the details and a button.", Designs.AnnouncementGlass),
            T("prayer.editorial", "Prayer", "Prayer", LowerThirdStyle.Editorial,
                "\"Let us pray\" coming gently into focus, word by word.", Designs.PrayerEditorial),
            T("baptism.editorial", "Baptism", "Celebration", LowerThirdStyle.Editorial,
                "Celebrating a baptism: a gold kicker, the name in serif and the date.", Designs.BaptismEditorial),
            T("celebration.glass", "Celebration", "Celebration", LowerThirdStyle.Glass,
                "A glass card for a wedding, a dedication or a birthday; its edge draws itself round.", Designs.CelebrationGlass),
            T("welcome.clean", "Welcome", "Welcome", LowerThirdStyle.Minimal,
                "\"Welcome home\" and where to find the Welcome Desk.", Designs.WelcomeClean),
            T("welcome.editorial", "Welcome Title", "Welcome", LowerThirdStyle.Editorial,
                "A big serif welcome burning in, in the middle of the screen, over the walk-in loop.", Designs.WelcomeEditorial),
            T("live.bug", "LIVE Bug", "Live", LowerThirdStyle.Broadcast,
                "A red LIVE pill in the corner for the stream.", Designs.LiveBug),
            T("live.clock", "LIVE + Clock", "Live", LowerThirdStyle.Broadcast,
                "The LIVE pill with the time of day beside it.", Designs.LiveClock),
            T("soon.clean", "Starting Soon", "Starting Soon", LowerThirdStyle.Minimal,
                "\"We're starting soon\" with the minutes left, along the bottom.", Designs.SoonClean),
            T("soon.full", "Starting Soon Title", "Starting Soon", LowerThirdStyle.Broadcast,
                "STARTING SOON across the screen with the countdown beneath.", Designs.SoonFull),
            T("countdown.corner", "Countdown Corner", "Countdown", LowerThirdStyle.Glass,
                "A small glass card in the corner: Service starts in 04:59.", Designs.CountdownCorner),
            T("countdown.center", "Countdown", "Countdown", LowerThirdStyle.Editorial,
                "Big digits in the middle of the screen, over any background.", Designs.CountdownCenter),
        ];
    }

    // MARK: Palettes

    private static class Ink
    {
        public const string White = "#FFFFFFFF";
        public const string Soft = "#FFFFFFB8";
        public const string Faint = "#FFFFFF8C";

        public const string CleanAccent = "#9DB7FFFF";
        public const string CleanPlate = "#0D1015E6";
        public const string CleanEdge = "#FFFFFF17";

        public const string BroadcastAccent = "#FF4655FF";
        public const string BroadcastAccent2 = "#FF7A45FF";
        public const string BroadcastPlate = "#0A0C10F2";
        public const string LiveRed = "#E5172EFF";

        public const string GlassTop = "#FFFFFF38";
        public const string GlassBottom = "#0E11183D";
        public const string GlassEdge = "#FFFFFF5C";
        public const string GlassAccent = "#A8D8FFFF";

        public const string Gold = "#D8B47AFF";
        public const string Ivory = "#F6F0E6FF";
    }

    private static class Fonts
    {
        public const string PoppinsSemiBold = "Poppins-SemiBold";
        public const string PoppinsMedium = "Poppins-Medium";
        public const string PoppinsRegular = "Poppins-Regular";
        public const string PoppinsBold = "Poppins-Bold";
        public const string MontserratBlack = "Montserrat-Black";
        public const string MontserratExtraBold = "Montserrat-ExtraBold";
        public const string MontserratBold = "Montserrat-Bold";
        public const string MontserratSemiBold = "Montserrat-SemiBold";
        public const string MontserratRegular = "Montserrat-Regular";
        public const string Serif = "DMSerifDisplay-Regular";
        public const string SerifItalic = "DMSerifDisplay-Italic";
        public const string Digits = "BebasNeue-Regular";
    }

    // MARK: Motion (step templates; the composer gives them ids)

    private static class In
    {
        public static AnimationStep Step(StepAnimation animation, double duration, AnimationTrigger trigger, double? delay,
            AnimationRamp? ramp = AnimationRamp.Out, bool? fade = null, AnimationEdge? edge = null, double? dx = null, double? dy = null,
            double? fromScale = null, double? amount = null, double? softEdge = null, bool? cursor = null, double? drawStart = null,
            AnimationKind kind = AnimationKind.In, string? color = null) => new()
        {
            Id = "", Kind = kind, Animation = animation, Trigger = trigger, DelaySeconds = delay, DurationSeconds = duration, Ramp = ramp,
            WithFade = fade, Edge = edge, OffsetX = dx ?? (dy is null ? null : 0), OffsetY = dy ?? (dx is null ? null : 0),
            FromScale = fromScale, Amount = amount, SoftEdge = softEdge, Cursor = cursor, DrawStart = drawStart, ColorHex = color,
        };

        public static AnimationStep Rise(double dy, double duration = 0.55, AnimationTrigger trigger = AnimationTrigger.AfterPrevious, double? delay = -0.25, bool fade = true) =>
            Step(StepAnimation.Move, duration, trigger, delay, fade: fade ? true : null, dy: dy);

        public static AnimationStep Slide(double dx, double duration = 0.55, AnimationTrigger trigger = AnimationTrigger.AfterPrevious, double? delay = -0.25) =>
            Step(StepAnimation.Move, duration, trigger, delay, fade: true, dx: dx);

        public static AnimationStep Grow(double from = 0.96, double duration = 0.55, AnimationTrigger trigger = AnimationTrigger.WithPrevious, double? delay = null) =>
            Step(StepAnimation.Scale, duration, trigger, delay, fade: true, fromScale: from);

        public static AnimationStep Wipe(AnimationEdge edge = AnimationEdge.Left, double softEdge = 0, double duration = 0.5,
            AnimationTrigger trigger = AnimationTrigger.WithPrevious, double? delay = null) =>
            Step(StepAnimation.Wipe, duration, trigger, delay, edge: edge, softEdge: softEdge);

        public static AnimationStep Focus(double amount = 20, double duration = 0.9, AnimationTrigger trigger = AnimationTrigger.AfterPrevious, double? delay = -0.3) =>
            Step(StepAnimation.Blur, duration, trigger, delay, fade: true, amount: amount);

        public static AnimationStep Fade(double duration = 0.5, AnimationTrigger trigger = AnimationTrigger.WithPrevious, double? delay = null) =>
            Step(StepAnimation.Fade, duration, trigger, delay);

        public static AnimationStep Type(double duration = 1.0, AnimationTrigger trigger = AnimationTrigger.AfterPrevious, double? delay = -0.2) =>
            Step(StepAnimation.Type, duration, trigger, delay, ramp: null, cursor: true);

        public static AnimationStep Pulse(double amount = 1.12, double duration = 0.5, AnimationTrigger trigger = AnimationTrigger.AfterPrevious, double? delay = 0.1) =>
            Step(StepAnimation.Pulse, duration, trigger, delay, AnimationRamp.Both, amount: amount, kind: AnimationKind.Emphasis);
    }

    private static class Out
    {
        public static AnimationStep Step(StepAnimation animation, double duration, AnimationTrigger trigger, double? delay, bool? fade = null,
            AnimationEdge? edge = null, double? dx = null, double? dy = null, double? fromScale = null, double? amount = null, double? softEdge = null) => new()
        {
            Id = "", Kind = AnimationKind.Out, Animation = animation, Trigger = trigger, DelaySeconds = delay, DurationSeconds = duration, Ramp = AnimationRamp.In,
            WithFade = fade, Edge = edge, OffsetX = dx ?? (dy is null ? null : 0), OffsetY = dy ?? (dx is null ? null : 0),
            FromScale = fromScale, Amount = amount, SoftEdge = softEdge,
        };

        public static AnimationStep Sink(double dy, double duration = 0.35, AnimationTrigger trigger = AnimationTrigger.WithPrevious, double? delay = null, bool fade = false) =>
            Step(StepAnimation.Move, duration, trigger, delay, fade ? true : null, dy: dy);

        public static AnimationStep Fade(double duration = 0.35, AnimationTrigger trigger = AnimationTrigger.WithPrevious, double? delay = null) =>
            Step(StepAnimation.Fade, duration, trigger, delay);

        public static AnimationStep Wipe(AnimationEdge edge = AnimationEdge.Right, double softEdge = 0, double duration = 0.4,
            AnimationTrigger trigger = AnimationTrigger.WithPrevious, double? delay = null) =>
            Step(StepAnimation.Wipe, duration, trigger, delay, edge: edge, softEdge: softEdge);

        public static AnimationStep Shrink(double to = 0.96, double duration = 0.35, AnimationTrigger trigger = AnimationTrigger.AfterPrevious, double? delay = -0.1) =>
            Step(StepAnimation.Scale, duration, trigger, delay, true, fromScale: to);

        public static AnimationStep Unfocus(double amount = 14, double duration = 0.5, AnimationTrigger trigger = AnimationTrigger.WithPrevious, double? delay = null) =>
            Step(StepAnimation.Blur, duration, trigger, delay, true, amount: amount);
    }

    /// <summary>
    /// Builds one overlay: objects bottom to top, each with its In and Out templates. The Ins play in
    /// the order the objects are added (the first starts as the overlay goes up), then the Outs, the
    /// first of them leading the exit (when it's taken down). Ids are made from the template's id.
    /// </summary>
    private sealed class Composer(string b, LowerThirdStyle style)
    {
        private readonly List<SlideObject> objects = [];
        private readonly List<AnimationStep> ins = [];
        private readonly List<AnimationStep> outs = [];
        private int counter;

        public LowerThirdStyle Style => style;

        public SlideObject Add(SlideObject o, IEnumerable<AnimationStep>? enter = null, IEnumerable<AnimationStep>? leave = null)
        {
            var steps = new List<AnimationStep>();
            foreach (var template in enter ?? [])
            {
                var step = MxuJson.Clone(template);
                step.Id = $"{o.Id}.in{++counter}";
                // The first In starts as the overlay goes up.
                if (ins.Count == 0) step.Trigger = AnimationTrigger.WithPrevious;
                ins.Add(step);
                steps.Add(step);
            }
            foreach (var template in leave ?? [])
            {
                var step = MxuJson.Clone(template);
                step.Id = $"{o.Id}.out{++counter}";
                if (outs.Count == 0) step.Trigger = AnimationTrigger.OnDismiss;
                outs.Add(step);
                steps.Add(step);
            }
            o.AnimationSteps = steps.Count == 0 ? null : steps;
            objects.Add(o);
            return o;
        }

        /// <summary>A text whose Ins play piece by piece (word by word, or line by line), with a whole-text Out.</summary>
        public SlideObject AddKinetic(SlideObject o, KineticSplit split, double stagger, IReadOnlyList<AnimationStep> enter, IEnumerable<AnimationStep> leave)
        {
            var built = KineticText.Expand(enter, o.Text, split, stagger, () => $"{o.Id}.in{++counter}");
            if (ins.Count == 0 && built.Count > 0) built[0].Trigger = AnimationTrigger.WithPrevious;
            ins.AddRange(built);
            var steps = new List<AnimationStep>(built);
            foreach (var template in leave)
            {
                var step = MxuJson.Clone(template);
                step.Id = $"{o.Id}.out{++counter}";
                if (outs.Count == 0) step.Trigger = AnimationTrigger.OnDismiss;
                outs.Add(step);
                steps.Add(step);
            }
            o.AnimationSteps = steps;
            objects.Add(o);
            return o;
        }

        public string Id(string part) => $"{b}.{part}";

        public Overlay Build(string name) => new()
        {
            Id = b, Name = name, Objects = objects, AnimationOrder = [.. ins.Select(s => s.Id), .. outs.Select(s => s.Id)],
        };

        // MARK: Objects

        public static ObjectFill Solid(string hex) => new() { FillKind = FillKind.Solid, ColorHex = hex };

        public static ObjectFill Gradient(double angle, params (string Hex, double At)[] stops) => new()
        {
            FillKind = FillKind.LinearGradient, GradientAngleDegrees = angle,
            GradientStops = [.. stops.Select(s => new GradientStop { ColorHex = s.Hex, Position = s.At })],
        };

        public SlideObject Box(string part, double x, double y, double w, double h, ObjectFill fill, double radius = 0,
            ObjectStroke? stroke = null, ObjectShadow? shadow = null, bool ellipse = false, string? name = null, List<Effect>? effects = null) => new()
        {
            Id = Id(part), ObjectKind = SlideObjectKind.Shape, Name = name ?? Words(part), Text = "", X = x, Y = y, Width = w, Height = h,
            ShapeKind = ellipse ? ShapeKind.Ellipse : radius > 0 ? ShapeKind.RoundedRectangle : ShapeKind.Rectangle, CornerRadius = radius,
            Fill = fill, Stroke = stroke, Shadow = shadow, Effects = effects,
        };

        public SlideObject Icon(string part, double x, double y, double size, string pathData, string color) => new()
        {
            Id = Id(part), ObjectKind = SlideObjectKind.Shape, Name = Words(part), Text = "", X = x, Y = y, Width = size, Height = size,
            ShapeKind = ShapeKind.Path, PathData = pathData, Fill = Solid(color),
        };

        /// <summary>An invisible edge words rise out of (another object names it as its mask).</summary>
        public SlideObject Matte(string part, double x, double y, double w, double h) => new()
        {
            Id = Id(part), ObjectKind = SlideObjectKind.Shape, Name = "Mask", Text = "", X = x, Y = y, Width = w, Height = h,
            ShapeKind = ShapeKind.Rectangle, Fill = Solid("#FFFFFFFF"),
        };

        public SlideObject Text(string part, string field, string text, double x, double y, double w, double h, double size, string font,
            string color = Ink.White, TextHorizontalAlignment align = TextHorizontalAlignment.Left, TextVerticalAlignment valign = TextVerticalAlignment.Middle,
            double tracking = 0, bool caps = false, double lineHeight = 1.05, bool shrink = true, bool page = false, string? maskedBy = null,
            bool shadow = false, TextLink? link = null, double? minSize = null) => new()
        {
            Id = Id(part), ObjectKind = SlideObjectKind.Text, Name = field, Text = text, X = x, Y = y, Width = w, Height = h,
            MaskObjectId = maskedBy is null ? null : Id(maskedBy), MaskMode = maskedBy is null ? null : MaskMode.In, TextLink = link,
            TextStyle = new TextStyle
            {
                FontName = font, FontSize = size, ColorHex = color, Tracking = tracking, LineHeightMultiple = lineHeight,
                HorizontalAlignment = align, VerticalAlignment = valign, TextTransform = caps ? TextTransform.Uppercase : null,
                // Fields shrink a little for a long name, then stop (a lower third must stay readable).
                AutoShrink = shrink ? true : null, MinFontSize = shrink ? minSize ?? Math.Round(size * 0.7) : null, PageOnClick = page ? true : null,
                BalancedWrap = link is null ? true : null, TabularFigures = link is { Source: TextSourceKind.Timer } ? true : null,
                Shadow = shadow ? new ObjectShadow { ColorHex = "#000000A6", BlurRadius = 18, OffsetX = 0, OffsetY = 2 } : null,
                InsetLeft = 0, InsetRight = 0,
            },
        };

        /// <summary>The glass panel: a see-through frosted fill (grain, where the graphics card draws effects), a bright edge, a soft shadow.</summary>
        public SlideObject Glass(string part, double x, double y, double w, double h, double radius = 26) =>
            Box(part, x, y, w, h, Gradient(90, (Ink.GlassTop, 0), (Ink.GlassBottom, 1)), radius,
                new ObjectStroke { ColorHex = Ink.GlassEdge, Width = 1.25 },
                new ObjectShadow { ColorHex = "#00000066", BlurRadius = 34, OffsetX = 0, OffsetY = 12 }, name: "Glass",
                effects: [new Effect { EffectKind = EffectKind.Grain, Amount = 0.08, Scale = 1.5, Speed = 0 }]);

        /// <summary>The bright line across a glass panel's top edge.</summary>
        public SlideObject Sheen(string part, double x, double y, double w) =>
            Box(part, x, y, w, 1.5, Gradient(0, ("#FFFFFF00", 0), ("#FFFFFFD9", 0.5), ("#FFFFFF00", 1)), name: "Sheen");

        /// <summary>A soft shadow rising from the bottom of the screen, for words straight on the picture.</summary>
        public SlideObject Floor(string part, double top = 640, double strength = 0.78) =>
            Box(part, 0, top, 1920, 1080 - top, Gradient(90, ("#00000000", 0), (Alpha("#000000", strength * 0.55), 0.5), (Alpha("#000000", strength), 1)), name: "Shadow");

        private static string Alpha(string rgb, double alpha) => rgb + ((int)Math.Round(Math.Clamp(alpha, 0, 1) * 255)).ToString("X2");

        private static string Words(string part)
        {
            var last = part.Split('.')[^1];
            return last.Length == 0 ? part : char.ToUpperInvariant(last[0]) + last[1..];
        }
    }

    // MARK: Icons (unit-square path data)

    private static class Glyphs
    {
        public static readonly string Play = "M 0.36 0.26 L 0.76 0.5 L 0.36 0.74 Z";

        public static readonly string Camera =
            StarterPacks.Icons.Polygon([(0.2, 0.22), (0.8, 0.22), (0.8, 0.78), (0.2, 0.78)]) + " "
            + StarterPacks.Icons.Polygon([(0.28, 0.3), (0.28, 0.7), (0.72, 0.7), (0.72, 0.3)]) + " "
            + StarterPacks.Icons.Ring(0.5, 0.5, 0.13, 0.07) + " " + StarterPacks.Icons.Circle(0.66, 0.36, 0.03);

        public static readonly string Chat =
            StarterPacks.Icons.Polygon([(0.2, 0.24), (0.8, 0.24), (0.8, 0.64), (0.46, 0.64), (0.3, 0.78), (0.32, 0.64), (0.2, 0.64)]);

        public static readonly string Heart =
            "M 0.5 0.8 C 0.2 0.58 0.14 0.44 0.18 0.32 C 0.23 0.18 0.42 0.17 0.5 0.33 C 0.58 0.17 0.77 0.18 0.82 0.32 C 0.86 0.44 0.8 0.58 0.5 0.8 Z";

        /// <summary>A four-pointed sparkle.</summary>
        public static readonly string Sparkle =
            "M 0.5 0 C 0.55 0.36 0.64 0.45 1 0.5 C 0.64 0.55 0.55 0.64 0.5 1 C 0.45 0.64 0.36 0.55 0 0.5 C 0.36 0.45 0.45 0.36 0.5 0 Z";

        public static readonly string Dot = StarterPacks.Icons.Circle(0.5, 0.5, 0.5);
    }

    private static TextLink Timer => new() { Source = TextSourceKind.Timer, TimerPattern = "MM:ss", UsesWarningColor = false };

    private static TextLink Clock => new() { Source = TextSourceKind.Clock, ClockFormat = "h:mm a" };

    // MARK: The designs

    private static class Designs
    {
        public static void SpeakerClean(Composer c)
        {
            c.Add(c.Box("plate", 150, 852, 700, 118, Composer.Solid(Ink.CleanPlate), 16, new ObjectStroke { ColorHex = Ink.CleanEdge, Width = 1 }),
                [In.Grow(0.97, 0.5)], [Out.Shrink(0.98, 0.35, AnimationTrigger.AfterPrevious, -0.1)]);
            c.Add(c.Box("bar", 178, 878, 4, 66, Composer.Solid(Ink.CleanAccent), 2), [In.Wipe(AnimationEdge.Top, 0, 0.35, AnimationTrigger.AfterPrevious, -0.3)], [Out.Wipe(AnimationEdge.Top)]);
            c.Add(c.Matte("mask.name", 202, 862, 620, 58));
            c.Add(c.Text("name", "Name", "Pastor Ade Johnson", 202, 862, 620, 58, 42, Fonts.PoppinsSemiBold, maskedBy: "mask.name"),
                [In.Rise(58, 0.55, AnimationTrigger.AfterPrevious, -0.25, fade: false)], [Out.Sink(58)]);
            c.Add(c.Matte("mask.title", 202, 918, 620, 36));
            c.Add(c.Text("title", "Title", "Lead Pastor", 202, 918, 620, 36, 24, Fonts.PoppinsRegular, Ink.Soft, maskedBy: "mask.title"),
                [In.Rise(36, 0.5, AnimationTrigger.WithPrevious, 0.1, fade: false)], [Out.Sink(36)]);
        }

        public static void SpeakerBroadcast(Composer c)
        {
            c.Add(c.Box("accent", 150, 826, 12, 144, Composer.Solid(Ink.BroadcastAccent)), [In.Wipe(AnimationEdge.Bottom, 0, 0.3)],
                [Out.Wipe(AnimationEdge.Bottom, 0, 0.3, AnimationTrigger.AfterPrevious, -0.1)]);
            c.Add(c.Box("plate", 162, 826, 820, 96, Composer.Gradient(0, ("#0A0C10F5", 0), ("#151922F5", 1))),
                [In.Wipe(AnimationEdge.Left, 30, 0.45, AnimationTrigger.AfterPrevious, -0.15)], [Out.Wipe(AnimationEdge.Right, 30, 0.4, AnimationTrigger.AfterPrevious, -0.15)]);
            c.Add(c.Box("band", 162, 922, 600, 48, Composer.Gradient(0, (Ink.BroadcastAccent, 0), (Ink.BroadcastAccent2, 1))),
                [In.Wipe(AnimationEdge.Left, 20, 0.4, AnimationTrigger.WithPrevious, 0.12)], [Out.Wipe(AnimationEdge.Right, 20, 0.35)]);
            c.Add(c.Matte("mask.name", 162, 826, 820, 96));
            c.Add(c.Text("name", "Name", "Pastor Ade Johnson", 196, 826, 760, 96, 50, Fonts.MontserratExtraBold, caps: true, tracking: 1, maskedBy: "mask.name"),
                [In.Rise(96, 0.5, AnimationTrigger.AfterPrevious, -0.25, fade: false)], [Out.Sink(-96, 0.35)]);
            c.Add(c.Text("title", "Title", "Lead Pastor", 196, 922, 540, 48, 21, Fonts.MontserratBold, caps: true, tracking: 4),
                [In.Slide(-24, 0.45, AnimationTrigger.WithPrevious, 0.12)], [Out.Fade(0.25)]);
        }

        public static void SpeakerGlass(Composer c)
        {
            c.Add(c.Glass("glass", 150, 834, 660, 136), [In.Grow(0.94, 0.6)], [Out.Shrink(0.96, 0.4, AnimationTrigger.AfterPrevious, -0.15)]);
            c.Add(c.Sheen("sheen", 176, 835, 608), [In.Wipe(AnimationEdge.Left, 160, 0.9, AnimationTrigger.WithPrevious, 0.15)], [Out.Fade(0.3)]);
            c.AddKinetic(c.Text("name", "Name", "Pastor Ade Johnson", 190, 852, 580, 60, 44, Fonts.PoppinsSemiBold), KineticSplit.Words, 0.08,
                [In.Rise(26, 0.5, AnimationTrigger.WithPrevious, 0.25)], [Out.Fade(0.3)]);
            c.Add(c.Text("title", "Title", "Lead Pastor", 190, 912, 580, 40, 24, Fonts.PoppinsMedium, Ink.Soft, tracking: 0.5),
                [In.Rise(14, 0.45, AnimationTrigger.WithPrevious, 0.15)], [Out.Fade(0.3)]);
        }

        public static void SpeakerEditorial(Composer c)
        {
            c.Add(c.Floor("floor", 660), [In.Fade(0.8)], [Out.Fade(0.6, AnimationTrigger.AfterPrevious, -0.2)]);
            c.Add(c.Box("rule", 192, 848, 260, 1.5, Composer.Solid(Ink.Gold)), [In.Wipe(AnimationEdge.Left, 0, 0.7, AnimationTrigger.WithPrevious, 0.15)],
                [Out.Wipe(AnimationEdge.Left, 0, 0.35)]);
            c.Add(c.Text("name", "Name", "Pastor Ade Johnson", 192, 856, 1200, 90, 70, Fonts.Serif, Ink.Ivory, shadow: true),
                [In.Focus(18, 0.9, AnimationTrigger.AfterPrevious, -0.55)], [Out.Unfocus(12, 0.45)]);
            c.Add(c.Text("title", "Title", "Lead Pastor", 192, 946, 1200, 34, 20, Fonts.MontserratSemiBold, Ink.Gold, caps: true, tracking: 9, shadow: true),
                [In.Rise(12, 0.6, AnimationTrigger.WithPrevious, 0.25)], [Out.Fade(0.3)]);
        }

        public static void ScriptureEditorial(Composer c)
        {
            c.Add(c.Floor("floor", 560, 0.82), [In.Fade(0.8)], [Out.Fade(0.6, AnimationTrigger.AfterPrevious, -0.2)]);
            c.Add(c.Text("verse", "Verse", "Come to me, all who labor and are heavy laden, and I will give you rest.", 260, 784, 1400, 140, 46,
                    Fonts.SerifItalic, Ink.Ivory, TextHorizontalAlignment.Center, TextVerticalAlignment.Bottom, lineHeight: 1.12, page: true, shadow: true),
                [In.Focus(20, 1.0, AnimationTrigger.AfterPrevious, -0.5)], [Out.Unfocus(14, 0.5)]);
            c.Add(c.Box("rule", 930, 938, 60, 1.5, Composer.Solid(Ink.Gold)), [In.Wipe(AnimationEdge.Right, 0, 0.5, AnimationTrigger.AfterPrevious, -0.5)],
                [Out.Wipe(AnimationEdge.Left, 0, 0.3)]);
            c.Add(c.Text("ref", "Reference", "Matthew 11:28 (ESV)", 260, 950, 1400, 32, 20, Fonts.MontserratSemiBold, Ink.Gold,
                    TextHorizontalAlignment.Center, caps: true, tracking: 8, shadow: true),
                [In.Rise(12, 0.55, AnimationTrigger.WithPrevious, 0.1)], [Out.Fade(0.3)]);
        }

        public static void ScriptureGlass(Composer c)
        {
            c.Add(c.Glass("glass", 150, 766, 1160, 204), [In.Grow(0.95, 0.6)], [Out.Shrink(0.97, 0.4, AnimationTrigger.AfterPrevious, -0.15)]);
            c.Add(c.Sheen("sheen", 180, 767, 1100), [In.Wipe(AnimationEdge.Left, 200, 1.0, AnimationTrigger.WithPrevious, 0.15)], [Out.Fade(0.3)]);
            c.Add(c.Text("kicker", "Kicker", "Scripture", 192, 790, 600, 28, 17, Fonts.PoppinsMedium, Ink.Faint, caps: true, tracking: 5, shrink: false),
                [In.Fade(0.45, AnimationTrigger.AfterPrevious, -0.45)], [Out.Fade(0.25)]);
            c.Add(c.Text("verse", "Verse", "Come to me, all who labor and are heavy laden, and I will give you rest.", 192, 822, 1076, 96, 32,
                    Fonts.PoppinsMedium, lineHeight: 1.22, valign: TextVerticalAlignment.Top, page: true),
                [In.Rise(18, 0.6, AnimationTrigger.WithPrevious, 0.05)], [Out.Fade(0.3)]);
            c.Add(c.Text("ref", "Reference", "Matthew 11:28", 192, 922, 1076, 30, 21, Fonts.PoppinsSemiBold, Ink.GlassAccent, tracking: 0.5),
                [In.Rise(12, 0.5, AnimationTrigger.WithPrevious, 0.15)], [Out.Fade(0.25)]);
        }

        public static void PointBroadcast(Composer c)
        {
            c.Add(c.Box("plate", 150, 800, 1620, 170, Composer.Gradient(0, ("#0A0C10F2", 0), ("#121620E6", 0.7), ("#12162000", 1))),
                [In.Wipe(AnimationEdge.Left, 200, 0.6)], [Out.Wipe(AnimationEdge.Right, 200, 0.45, AnimationTrigger.AfterPrevious, -0.15)]);
            c.Add(c.Box("tag", 150, 760, 196, 40, Composer.Solid(Ink.BroadcastAccent)), [In.Wipe(AnimationEdge.Left, 0, 0.35, AnimationTrigger.WithPrevious, 0.2)],
                [Out.Wipe(AnimationEdge.Left, 0, 0.3)]);
            c.Add(c.Text("kicker", "Kicker", "Point 1", 150, 760, 196, 40, 18, Fonts.MontserratBold, caps: true, tracking: 4,
                    align: TextHorizontalAlignment.Center, shrink: false),
                [In.Fade(0.3, AnimationTrigger.AfterPrevious, -0.1)], [Out.Fade(0.2)]);
            c.AddKinetic(c.Text("point", "Point", "Faith is trust that outlasts fear", 192, 814, 1520, 142, 46, Fonts.MontserratExtraBold, caps: true,
                    tracking: 0.5, lineHeight: 1.04, page: true), KineticSplit.Words, 0.07,
                [In.Rise(30, 0.45, AnimationTrigger.AfterPrevious, -0.2)], [Out.Fade(0.3)]);
        }

        public static void PointClean(Composer c)
        {
            c.Add(c.Floor("floor", 680, 0.7), [In.Fade(0.7)], [Out.Fade(0.5, AnimationTrigger.AfterPrevious, -0.2)]);
            c.Add(c.Box("rule", 192, 812, 44, 3, Composer.Solid(Ink.CleanAccent), 1.5), [In.Wipe(AnimationEdge.Left, 0, 0.4, AnimationTrigger.WithPrevious, 0.1)],
                [Out.Wipe(AnimationEdge.Left, 0, 0.3)]);
            c.Add(c.Text("kicker", "Kicker", "Point 1", 250, 798, 600, 30, 20, Fonts.PoppinsMedium, Ink.CleanAccent, caps: true, tracking: 5, shrink: false),
                [In.Rise(10, 0.45, AnimationTrigger.WithPrevious, 0.15)], [Out.Fade(0.25)]);
            c.AddKinetic(c.Text("point", "Point", "Faith is trust that outlasts fear", 192, 840, 1536, 120, 50, Fonts.PoppinsSemiBold, page: true, shadow: true),
                KineticSplit.Words, 0.06, [In.Rise(24, 0.5, AnimationTrigger.AfterPrevious, -0.3)], [Out.Fade(0.3)]);
        }

        public static void CreditsClean(Composer c)
        {
            c.Add(c.Box("plate", 150, 840, 760, 140, Composer.Solid(Ink.CleanPlate), 14, new ObjectStroke { ColorHex = Ink.CleanEdge, Width = 1 }),
                [In.Grow(0.97, 0.5)], [Out.Shrink(0.98, 0.3, AnimationTrigger.AfterPrevious, -0.1)]);
            c.Add(c.Text("song", "Song Title", "Amazing Grace (My Chains Are Gone)", 180, 856, 700, 42, 30, Fonts.PoppinsSemiBold),
                [In.Rise(16, 0.5, AnimationTrigger.AfterPrevious, -0.3)], [Out.Fade(0.25)]);
            c.Add(c.Text("writers", "Writers", "Words and music by John Newton, Chris Tomlin, Louie Giglio", 180, 898, 700, 28, 19, Fonts.PoppinsRegular, Ink.Soft),
                [In.Rise(12, 0.45, AnimationTrigger.WithPrevious, 0.1)], [Out.Fade(0.25)]);
            c.Add(c.Box("divider", 180, 934, 700, 1, Composer.Solid("#FFFFFF1F")), [In.Wipe(AnimationEdge.Left, 0, 0.5, AnimationTrigger.WithPrevious, 0.05)], [Out.Fade(0.2)]);
            c.Add(c.Text("ccli", "CCLI Song", "CCLI Song # 4768151", 180, 941, 340, 28, 16, Fonts.PoppinsMedium, Ink.Faint, tracking: 1),
                [In.Fade(0.4, AnimationTrigger.WithPrevious, 0.1)], [Out.Fade(0.2)]);
            c.Add(c.Text("licence", "CCLI Licence", "CCLI License # 11223344", 520, 941, 360, 28, 16, Fonts.PoppinsMedium, Ink.Faint, TextHorizontalAlignment.Right, tracking: 1),
                [In.Fade(0.4, AnimationTrigger.WithPrevious, 0.05)], [Out.Fade(0.2)]);
        }

        public static void CreditsEditorial(Composer c)
        {
            c.Add(c.Floor("floor", 700, 0.75), [In.Fade(0.7)], [Out.Fade(0.5, AnimationTrigger.AfterPrevious, -0.2)]);
            c.Add(c.Text("song", "Song Title", "Amazing Grace (My Chains Are Gone)", 260, 852, 1400, 50, 36, Fonts.SerifItalic, Ink.Ivory,
                    TextHorizontalAlignment.Center, shadow: true),
                [In.Focus(14, 0.8, AnimationTrigger.AfterPrevious, -0.45)], [Out.Unfocus(10, 0.4)]);
            c.Add(c.Text("writers", "Writers", "John Newton · Chris Tomlin · Louie Giglio", 260, 904, 1400, 30, 18, Fonts.MontserratRegular, "#F6F0E6CC",
                    TextHorizontalAlignment.Center, tracking: 2, shadow: true),
                [In.Rise(10, 0.55, AnimationTrigger.WithPrevious, 0.15)], [Out.Fade(0.3)]);
            c.Add(c.Text("ccli", "CCLI Song", "CCLI Song # 4768151", 540, 942, 396, 24, 15, Fonts.MontserratSemiBold, Ink.Gold,
                    TextHorizontalAlignment.Right, caps: true, tracking: 3, shadow: true),
                [In.Fade(0.45, AnimationTrigger.WithPrevious, 0.15)], [Out.Fade(0.25)]);
            c.Add(c.Box("dot", 957, 951, 6, 6, Composer.Solid(Ink.Gold), ellipse: true), [In.Fade(0.4, AnimationTrigger.WithPrevious, 0.05)], [Out.Fade(0.2)]);
            c.Add(c.Text("licence", "CCLI Licence", "CCLI License # 11223344", 984, 942, 396, 24, 15, Fonts.MontserratSemiBold, Ink.Gold,
                    caps: true, tracking: 3, shadow: true),
                [In.Fade(0.45, AnimationTrigger.WithPrevious, 0.05)], [Out.Fade(0.25)]);
        }

        public static void SocialGlass(Composer c)
        {
            c.Add(c.Glass("glass", 150, 870, 700, 100, 50), [In.Grow(0.92, 0.55)], [Out.Shrink(0.95, 0.35, AnimationTrigger.AfterPrevious, -0.15)]);
            double x = 176;
            foreach (var (part, glyph) in new[] { ("camera", Glyphs.Camera), ("video", Glyphs.Play), ("chat", Glyphs.Chat) })
            {
                // Each disc's ring draws round, then fills (a Draw step on its outline).
                c.Add(c.Box($"{part}.disc", x, 890, 60, 60, Composer.Gradient(135, ("#FFFFFF4D", 0), ("#FFFFFF1F", 1)), ellipse: true,
                        stroke: new ObjectStroke { ColorHex = "#FFFFFF8C", Width = 1.5 }),
                    [In.Step(StepAnimation.Draw, 0.6, AnimationTrigger.AfterPrevious, part == "camera" ? -0.35 : -0.45, ramp: AnimationRamp.Out, drawStart: 0)], [Out.Fade(0.2)]);
                c.Add(c.Icon($"{part}.icon", x + 14, 904, 32, glyph, Ink.White), [In.Fade(0.3, AnimationTrigger.WithPrevious, 0.4)], [Out.Fade(0.2)]);
                x += 72;
            }
            c.Add(c.Text("kicker", "Kicker", "Follow along", 404, 884, 420, 26, 16, Fonts.PoppinsMedium, Ink.Faint, caps: true, tracking: 4, shrink: false),
                [In.Fade(0.4, AnimationTrigger.AfterPrevious, -0.2)], [Out.Fade(0.2)]);
            c.Add(c.Text("handle", "Handle", "@gracechurch", 404, 910, 420, 46, 32, Fonts.PoppinsSemiBold),
                [In.Type(0.9, AnimationTrigger.WithPrevious, 0.15)], [Out.Fade(0.25)]);
        }

        public static void SocialBroadcast(Composer c)
        {
            c.Add(c.Box("bar", 0, 950, 1920, 64, Composer.Gradient(0, ("#0A0C10F2", 0), ("#151922F2", 1))),
                [In.Wipe(AnimationEdge.Left, 120, 0.6)], [Out.Wipe(AnimationEdge.Right, 120, 0.45, AnimationTrigger.AfterPrevious, -0.15)]);
            c.Add(c.Box("tag", 150, 950, 220, 64, Composer.Solid(Ink.BroadcastAccent)), [In.Wipe(AnimationEdge.Left, 0, 0.35, AnimationTrigger.WithPrevious, 0.25)],
                [Out.Wipe(AnimationEdge.Left, 0, 0.3)]);
            c.Add(c.Icon("heart", 172, 966, 32, Glyphs.Heart, Ink.White), [In.Step(StepAnimation.Scale, 0.35, AnimationTrigger.AfterPrevious, -0.1, fade: true, fromScale: 0.3)], [Out.Fade(0.2)]);
            c.Add(c.Text("kicker", "Kicker", "Follow", 212, 950, 150, 64, 20, Fonts.MontserratExtraBold, caps: true, tracking: 4, shrink: false),
                [In.Fade(0.3, AnimationTrigger.WithPrevious, 0.05)], [Out.Fade(0.2)]);
            c.Add(c.Text("handle", "Handle", "@gracechurch", 400, 950, 620, 64, 28, Fonts.MontserratBold, tracking: 0.5),
                [In.Slide(-30, 0.45, AnimationTrigger.WithPrevious, 0.15)], [Out.Fade(0.25)]);
            c.Add(c.Text("site", "Website", "gracechurch.org/live", 1080, 950, 690, 64, 24, Fonts.MontserratSemiBold, Ink.Soft, TextHorizontalAlignment.Right, tracking: 1),
                [In.Slide(30, 0.45, AnimationTrigger.WithPrevious, 0.1)], [Out.Fade(0.25)]);
        }

        public static void GivingClean(Composer c)
        {
            c.Add(c.Box("plate", 150, 832, 860, 138, Composer.Solid(Ink.CleanPlate), 16, new ObjectStroke { ColorHex = Ink.CleanEdge, Width = 1 }),
                [In.Grow(0.97, 0.5)], [Out.Shrink(0.98, 0.3, AnimationTrigger.AfterPrevious, -0.1)]);
            c.Add(c.Box("badge", 176, 858, 86, 86, Composer.Gradient(135, (Ink.CleanAccent, 0), ("#C9B8FFFF", 1)), 43),
                [In.Step(StepAnimation.Scale, 0.45, AnimationTrigger.AfterPrevious, -0.3, fade: true, fromScale: 0.5)], [Out.Fade(0.2)]);
            c.Add(c.Icon("heart", 197, 881, 44, Glyphs.Heart, "#0D1015FF"),
                [In.Fade(0.25, AnimationTrigger.WithPrevious, 0.15), In.Pulse(1.15, 0.5, AnimationTrigger.AfterPrevious, 0)], [Out.Fade(0.2)]);
            c.Add(c.Text("head", "Heading", "Text GIVE to 555-0100", 288, 856, 690, 54, 38, Fonts.PoppinsSemiBold),
                [In.Rise(18, 0.5, AnimationTrigger.WithPrevious, -0.3)], [Out.Fade(0.25)]);
            c.Add(c.Text("detail", "Detail", "or give online at gracechurch.org/give", 288, 908, 690, 36, 22, Fonts.PoppinsRegular, Ink.Soft),
                [In.Rise(12, 0.45, AnimationTrigger.WithPrevious, 0.12)], [Out.Fade(0.25)]);
        }

        public static void GivingBroadcast(Composer c)
        {
            c.Add(c.Box("accent", 150, 820, 12, 150, Composer.Solid(Ink.BroadcastAccent)), [In.Wipe(AnimationEdge.Bottom, 0, 0.3)],
                [Out.Wipe(AnimationEdge.Bottom, 0, 0.3, AnimationTrigger.AfterPrevious, -0.1)]);
            c.Add(c.Box("plate", 162, 820, 1100, 150, Composer.Gradient(0, ("#0A0C10F5", 0), ("#151922F5", 1))),
                [In.Wipe(AnimationEdge.Left, 40, 0.5, AnimationTrigger.AfterPrevious, -0.15)], [Out.Wipe(AnimationEdge.Right, 40, 0.4, AnimationTrigger.AfterPrevious, -0.15)]);
            c.Add(c.Text("kicker", "Kicker", "Generosity", 196, 838, 700, 30, 18, Fonts.MontserratBold, Ink.BroadcastAccent, caps: true, tracking: 6, shrink: false),
                [In.Fade(0.35, AnimationTrigger.AfterPrevious, -0.25)], [Out.Fade(0.2)]);
            c.AddKinetic(c.Text("head", "Heading", "Give online", 196, 868, 700, 66, 52, Fonts.MontserratBlack, caps: true), KineticSplit.Words, 0.08,
                [In.Rise(30, 0.45, AnimationTrigger.WithPrevious, 0.05)], [Out.Fade(0.25)]);
            c.Add(c.Box("pill", 926, 862, 312, 64, Composer.Gradient(0, (Ink.BroadcastAccent, 0), (Ink.BroadcastAccent2, 1)), 32),
                [In.Step(StepAnimation.Scale, 0.4, AnimationTrigger.AfterPrevious, -0.2, fade: true, fromScale: 0.8)], [Out.Fade(0.25)]);
            c.Add(c.Text("site", "Website", "gracechurch.org/give", 926, 862, 312, 64, 21, Fonts.MontserratBold, align: TextHorizontalAlignment.Center, tracking: 0.5),
                [In.Fade(0.3, AnimationTrigger.WithPrevious, 0.1)], [Out.Fade(0.2)]);
        }

        public static void EventBroadcast(Composer c)
        {
            c.Add(c.Box("date", 150, 820, 150, 150, Composer.Gradient(135, (Ink.BroadcastAccent, 0), (Ink.BroadcastAccent2, 1))),
                [In.Step(StepAnimation.Scale, 0.45, AnimationTrigger.WithPrevious, null, fade: true, fromScale: 0.7)], [Out.Fade(0.3, AnimationTrigger.AfterPrevious, -0.15)]);
            c.Add(c.Text("month", "Month", "Nov", 150, 834, 150, 34, 22, Fonts.MontserratExtraBold, align: TextHorizontalAlignment.Center, caps: true, tracking: 4, shrink: false),
                [In.Fade(0.3, AnimationTrigger.AfterPrevious, -0.15)], [Out.Fade(0.2)]);
            c.Add(c.Text("day", "Day", "16", 150, 860, 150, 100, 92, Fonts.Digits, align: TextHorizontalAlignment.Center, lineHeight: 0.9, shrink: false),
                [In.Rise(16, 0.45, AnimationTrigger.WithPrevious, 0.05)], [Out.Fade(0.2)]);
            c.Add(c.Box("plate", 300, 820, 960, 150, Composer.Gradient(0, ("#0A0C10F5", 0), ("#151922F5", 1))),
                [In.Wipe(AnimationEdge.Left, 40, 0.5, AnimationTrigger.WithPrevious, -0.1)], [Out.Wipe(AnimationEdge.Left, 40, 0.4)]);
            c.AddKinetic(c.Text("title", "Event", "Family Night of Worship", 336, 836, 900, 66, 44, Fonts.MontserratExtraBold, caps: true, tracking: 0.5),
                KineticSplit.Words, 0.07, [In.Rise(24, 0.45, AnimationTrigger.AfterPrevious, -0.3)], [Out.Fade(0.25)]);
            c.Add(c.Text("detail", "Details", "Sunday · 6:30 pm · Main Auditorium", 336, 906, 900, 40, 22, Fonts.MontserratSemiBold, Ink.Soft, tracking: 1),
                [In.Rise(12, 0.45, AnimationTrigger.WithPrevious, 0.1)], [Out.Fade(0.25)]);
        }

        public static void AnnouncementGlass(Composer c)
        {
            c.Add(c.Glass("glass", 150, 770, 1040, 200), [In.Grow(0.95, 0.6)], [Out.Shrink(0.97, 0.4, AnimationTrigger.AfterPrevious, -0.15)]);
            c.Add(c.Sheen("sheen", 180, 771, 980), [In.Wipe(AnimationEdge.Left, 200, 1.0, AnimationTrigger.WithPrevious, 0.15)], [Out.Fade(0.3)]);
            c.Add(c.Text("kicker", "Kicker", "This week", 192, 794, 700, 28, 17, Fonts.PoppinsMedium, Ink.GlassAccent, caps: true, tracking: 5, shrink: false),
                [In.Fade(0.45, AnimationTrigger.AfterPrevious, -0.45)], [Out.Fade(0.25)]);
            c.AddKinetic(c.Text("head", "Headline", "Serve Day is coming", 192, 826, 720, 60, 44, Fonts.PoppinsSemiBold), KineticSplit.Words, 0.07,
                [In.Rise(22, 0.5, AnimationTrigger.WithPrevious, 0.05)], [Out.Fade(0.25)]);
            c.Add(c.Text("detail", "Details", "Saturday 9 am — sign up in the app or at the Welcome Desk", 192, 892, 720, 52, 22, Fonts.PoppinsRegular, Ink.Soft,
                    lineHeight: 1.2, valign: TextVerticalAlignment.Top),
                [In.Rise(12, 0.45, AnimationTrigger.WithPrevious, 0.1)], [Out.Fade(0.25)]);
            c.Add(c.Box("button", 942, 838, 214, 64, Composer.Solid("#FFFFFFF2"), 32), [In.Step(StepAnimation.Scale, 0.4, AnimationTrigger.AfterPrevious, -0.25, fade: true, fromScale: 0.8)],
                [Out.Fade(0.25)]);
            c.Add(c.Text("cta", "Button", "Sign up", 942, 838, 214, 64, 22, Fonts.PoppinsSemiBold, "#0D1015FF", TextHorizontalAlignment.Center, shrink: false),
                [In.Fade(0.3, AnimationTrigger.WithPrevious, 0.1)], [Out.Fade(0.2)]);
        }

        public static void PrayerEditorial(Composer c)
        {
            c.Add(c.Floor("floor", 600, 0.8), [In.Fade(0.9)], [Out.Fade(0.7, AnimationTrigger.AfterPrevious, -0.2)]);
            c.AddKinetic(c.Text("head", "Heading", "Let us pray", 260, 812, 1400, 96, 76, Fonts.SerifItalic, Ink.Ivory, TextHorizontalAlignment.Center, shadow: true),
                KineticSplit.Words, 0.28, [In.Focus(22, 1.1, AnimationTrigger.AfterPrevious, -0.6)], [Out.Unfocus(14, 0.6)]);
            c.Add(c.Box("rule", 900, 920, 120, 1.5, Composer.Solid(Ink.Gold)), [In.Wipe(AnimationEdge.Right, 0, 0.7, AnimationTrigger.AfterPrevious, -0.6)],
                [Out.Wipe(AnimationEdge.Left, 0, 0.35)]);
            c.Add(c.Text("detail", "Details", "Prayer team available at the front", 260, 936, 1400, 32, 18, Fonts.MontserratSemiBold, Ink.Gold,
                    TextHorizontalAlignment.Center, caps: true, tracking: 7, shadow: true),
                [In.Rise(10, 0.7, AnimationTrigger.WithPrevious, 0.2)], [Out.Fade(0.35)]);
        }

        public static void BaptismEditorial(Composer c)
        {
            c.Add(c.Floor("floor", 600, 0.82), [In.Fade(0.8)], [Out.Fade(0.6, AnimationTrigger.AfterPrevious, -0.2)]);
            c.Add(c.Text("kicker", "Kicker", "Celebrating the baptism of", 260, 786, 1400, 44, 30, Fonts.SerifItalic, Ink.Gold, TextHorizontalAlignment.Center, shadow: true),
                [In.Rise(12, 0.7, AnimationTrigger.WithPrevious, 0.2)], [Out.Fade(0.3)]);
            c.AddKinetic(c.Text("name", "Name", "Grace Okafor", 260, 828, 1400, 104, 84, Fonts.Serif, Ink.Ivory, TextHorizontalAlignment.Center, shadow: true),
                KineticSplit.Words, 0.22,
                // Each word comes into focus, then a warm light passes over it (a colour emphasis after its In).
                [In.Focus(20, 1.0, AnimationTrigger.AfterPrevious, -0.4),
                 In.Step(StepAnimation.Color, 0.9, AnimationTrigger.WithPrevious, 0.55, AnimationRamp.Both, kind: AnimationKind.Emphasis, color: "#F3D9A6FF")],
                [Out.Unfocus(12, 0.5)]);
            c.Add(c.Icon("spark.left", 772, 945, 22, Glyphs.Sparkle, Ink.Gold), [In.Step(StepAnimation.Scale, 0.5, AnimationTrigger.AfterPrevious, -0.4, fade: true, fromScale: 0.2)], [Out.Fade(0.25)]);
            c.Add(c.Icon("spark.right", 1126, 945, 22, Glyphs.Sparkle, Ink.Gold), [In.Step(StepAnimation.Scale, 0.5, AnimationTrigger.WithPrevious, 0.08, fade: true, fromScale: 0.2)], [Out.Fade(0.25)]);
            c.Add(c.Text("date", "Date", "25 October 2026", 800, 940, 320, 32, 18, Fonts.MontserratSemiBold, Ink.Gold, TextHorizontalAlignment.Center, caps: true, tracking: 6,
                    shadow: true, shrink: false),
                [In.Fade(0.6, AnimationTrigger.WithPrevious, 0)], [Out.Fade(0.3)]);
        }

        public static void CelebrationGlass(Composer c)
        {
            // The card's bright edge draws round, then the frost fills in behind it.
            c.Add(c.Glass("glass", 150, 800, 740, 170), [In.Step(StepAnimation.Draw, 0.9, AnimationTrigger.WithPrevious, null, ramp: AnimationRamp.Out, drawStart: 0)],
                [Out.Shrink(0.96, 0.4, AnimationTrigger.AfterPrevious, -0.15)]);
            c.Add(c.Sheen("sheen", 180, 801, 680), [In.Wipe(AnimationEdge.Left, 200, 1.0, AnimationTrigger.WithPrevious, 0.45)], [Out.Fade(0.3)]);
            c.Add(c.Icon("spark", 186, 838, 44, Glyphs.Sparkle, "#FFE7A8FF"),
                [In.Step(StepAnimation.Scale, 0.5, AnimationTrigger.AfterPrevious, -0.5, fade: true, fromScale: 0.2), In.Pulse(1.2, 0.6, AnimationTrigger.AfterPrevious, 0.05)],
                [Out.Fade(0.25)]);
            c.Add(c.Text("kicker", "Kicker", "Congratulations", 252, 822, 600, 28, 17, Fonts.PoppinsMedium, "#FFE7A8FF", caps: true, tracking: 5, shrink: false),
                [In.Fade(0.4, AnimationTrigger.WithPrevious, -0.4)], [Out.Fade(0.25)]);
            c.AddKinetic(c.Text("name", "Name", "Sarah & James", 252, 852, 600, 64, 46, Fonts.PoppinsSemiBold), KineticSplit.Words, 0.09,
                [In.Rise(22, 0.5, AnimationTrigger.WithPrevious, 0.05)], [Out.Fade(0.25)]);
            c.Add(c.Text("detail", "Details", "Married 18 October 2026", 252, 918, 600, 34, 22, Fonts.PoppinsRegular, Ink.Soft),
                [In.Rise(12, 0.45, AnimationTrigger.WithPrevious, 0.12)], [Out.Fade(0.25)]);
        }

        public static void WelcomeClean(Composer c)
        {
            c.Add(c.Floor("floor", 660, 0.72), [In.Fade(0.7)], [Out.Fade(0.5, AnimationTrigger.AfterPrevious, -0.2)]);
            c.AddKinetic(c.Text("head", "Heading", "Welcome home", 192, 830, 1536, 70, 56, Fonts.PoppinsSemiBold, shadow: true), KineticSplit.Words, 0.12,
                [In.Rise(28, 0.6, AnimationTrigger.WithPrevious, 0.15)], [Out.Fade(0.3)]);
            c.Add(c.Box("rule", 192, 908, 44, 3, Composer.Solid(Ink.CleanAccent), 1.5), [In.Wipe(AnimationEdge.Left, 0, 0.4, AnimationTrigger.AfterPrevious, -0.3)],
                [Out.Wipe(AnimationEdge.Left, 0, 0.3)]);
            c.Add(c.Text("detail", "Details", "We're so glad you're here. Say hello at the Welcome Desk after the service.", 192, 920, 1536, 40, 24, Fonts.PoppinsRegular,
                    Ink.Soft, shadow: true),
                [In.Rise(12, 0.5, AnimationTrigger.WithPrevious, 0.1)], [Out.Fade(0.3)]);
        }

        public static void WelcomeEditorial(Composer c)
        {
            c.Add(c.Box("vignette", 0, 0, 1920, 1080, Composer.Gradient(90, ("#00000059", 0), ("#00000026", 0.5), ("#00000073", 1))), [In.Fade(1.0)],
                [Out.Fade(0.8, AnimationTrigger.AfterPrevious, -0.3)]);
            c.Add(c.Text("kicker", "Kicker", "Good morning", 260, 380, 1400, 50, 26, Fonts.MontserratSemiBold, Ink.Gold, TextHorizontalAlignment.Center, caps: true,
                    tracking: 12, shadow: true, shrink: false),
                [In.Rise(14, 0.8, AnimationTrigger.WithPrevious, 0.2)], [Out.Fade(0.4)]);
            // The welcome burns in from its edges, like film catching the light.
            c.AddKinetic(c.Text("head", "Heading", "Welcome", 160, 430, 1600, 190, 168, Fonts.Serif, Ink.Ivory, TextHorizontalAlignment.Center, shadow: true),
                KineticSplit.Words, 0.2, [In.Step(StepAnimation.Burn, 1.5, AnimationTrigger.AfterPrevious, -0.5, amount: 0.6)], [Out.Unfocus(20, 0.7)]);
            c.Add(c.Box("rule", 900, 640, 120, 1.5, Composer.Solid(Ink.Gold)), [In.Wipe(AnimationEdge.Right, 0, 0.8, AnimationTrigger.AfterPrevious, -0.8)],
                [Out.Wipe(AnimationEdge.Left, 0, 0.4)]);
            c.Add(c.Text("detail", "Details", "to Sunday at Grace Church", 260, 660, 1400, 60, 40, Fonts.SerifItalic, "#F6F0E6D9", TextHorizontalAlignment.Center, shadow: true),
                [In.Rise(14, 0.8, AnimationTrigger.WithPrevious, 0.2)], [Out.Fade(0.4)]);
        }

        public static void LiveBug(Composer c)
        {
            c.Add(c.Box("pill", 120, 72, 150, 48, Composer.Gradient(0, (Ink.LiveRed, 0), ("#FF3B4BFF", 1)), 24,
                    shadow: new ObjectShadow { ColorHex = "#00000066", BlurRadius = 16, OffsetX = 0, OffsetY = 4 }),
                [In.Step(StepAnimation.Scale, 0.4, AnimationTrigger.WithPrevious, null, fade: true, fromScale: 0.6)], [Out.Shrink(0.8, 0.3, AnimationTrigger.AfterPrevious, -0.1)]);
            c.Add(c.Icon("dot", 140, 88, 16, Glyphs.Dot, Ink.White),
                [In.Fade(0.25, AnimationTrigger.AfterPrevious, -0.1), In.Pulse(1.5, 0.6, AnimationTrigger.AfterPrevious, 0.1)], [Out.Fade(0.2)]);
            c.Add(c.Text("live", "Label", "Live", 166, 72, 92, 48, 22, Fonts.MontserratExtraBold, caps: true, tracking: 4, shrink: false),
                [In.Slide(-12, 0.35, AnimationTrigger.WithPrevious, -0.5)], [Out.Fade(0.2)]);
        }

        public static void LiveClock(Composer c)
        {
            c.Add(c.Box("clockplate", 230, 72, 170, 48, Composer.Solid("#0A0C10E6"), 24),
                [In.Wipe(AnimationEdge.Left, 30, 0.45, AnimationTrigger.WithPrevious, 0.25)], [Out.Wipe(AnimationEdge.Left, 30, 0.3, AnimationTrigger.AfterPrevious, -0.2)]);
            c.Add(c.Box("pill", 120, 72, 150, 48, Composer.Gradient(0, (Ink.LiveRed, 0), ("#FF3B4BFF", 1)), 24,
                    shadow: new ObjectShadow { ColorHex = "#00000066", BlurRadius = 16, OffsetX = 0, OffsetY = 4 }),
                [In.Step(StepAnimation.Scale, 0.4, AnimationTrigger.WithPrevious, -0.25, fade: true, fromScale: 0.6)], [Out.Shrink(0.8, 0.3)]);
            c.Add(c.Icon("dot", 140, 88, 16, Glyphs.Dot, Ink.White),
                [In.Fade(0.25, AnimationTrigger.AfterPrevious, -0.1), In.Pulse(1.5, 0.6, AnimationTrigger.AfterPrevious, 0.1)], [Out.Fade(0.2)]);
            c.Add(c.Text("live", "Label", "Live", 166, 72, 92, 48, 22, Fonts.MontserratExtraBold, caps: true, tracking: 4, shrink: false),
                [In.Slide(-12, 0.35, AnimationTrigger.WithPrevious, -0.5)], [Out.Fade(0.2)]);
            // The time arrives with a short digital flicker.
            c.Add(c.Text("clock", "Clock", "10:32 AM", 286, 72, 104, 48, 19, Fonts.MontserratSemiBold, Ink.Soft, TextHorizontalAlignment.Center, link: Clock, shrink: false),
                [In.Step(StepAnimation.Glitch, 0.45, AnimationTrigger.AfterPrevious, -0.1, ramp: null, amount: 0.45)], [Out.Fade(0.2)]);
        }

        public static void SoonClean(Composer c)
        {
            c.Add(c.Box("plate", 150, 852, 1620, 118, Composer.Solid(Ink.CleanPlate), 16, new ObjectStroke { ColorHex = Ink.CleanEdge, Width = 1 }),
                [In.Grow(0.98, 0.5)], [Out.Shrink(0.99, 0.3, AnimationTrigger.AfterPrevious, -0.1)]);
            c.AddKinetic(c.Text("head", "Heading", "We're starting soon", 190, 866, 1100, 54, 40, Fonts.PoppinsSemiBold), KineticSplit.Words, 0.08,
                [In.Rise(20, 0.5, AnimationTrigger.AfterPrevious, -0.3)], [Out.Fade(0.25)]);
            c.Add(c.Text("detail", "Details", "Grab a seat — worship begins in a few minutes", 190, 918, 1100, 34, 22, Fonts.PoppinsRegular, Ink.Soft),
                [In.Rise(12, 0.45, AnimationTrigger.WithPrevious, 0.1)], [Out.Fade(0.25)]);
            c.Add(c.Box("divider", 1420, 878, 1, 66, Composer.Solid("#FFFFFF26")), [In.Fade(0.4, AnimationTrigger.WithPrevious, 0)], [Out.Fade(0.2)]);
            c.Add(c.Text("timer", "Countdown", "04:59", 1450, 856, 290, 110, 84, Fonts.Digits, align: TextHorizontalAlignment.Center, tracking: 3, link: Timer, shrink: false),
                [In.Focus(16, 0.7, AnimationTrigger.WithPrevious, 0.05)], [Out.Fade(0.25)]);
        }

        public static void SoonFull(Composer c)
        {
            c.Add(c.Box("vignette", 0, 0, 1920, 1080, Composer.Gradient(90, ("#0000004D", 0), ("#00000033", 0.5), ("#00000080", 1))), [In.Fade(1.0)],
                [Out.Fade(0.8, AnimationTrigger.AfterPrevious, -0.3)]);
            c.AddKinetic(c.Text("head", "Heading", "Starting Soon", 160, 340, 1600, 220, 230, Fonts.Digits, align: TextHorizontalAlignment.Center, tracking: 24, lineHeight: 0.9,
                    shadow: true), KineticSplit.Words, 0.18, [In.Rise(50, 0.8, AnimationTrigger.WithPrevious, 0.2)], [Out.Fade(0.4)]);
            c.Add(c.Box("rule", 860, 594, 200, 6, Composer.Gradient(0, (Ink.BroadcastAccent, 0), (Ink.BroadcastAccent2, 1))),
                [In.Wipe(AnimationEdge.Left, 0, 0.6, AnimationTrigger.AfterPrevious, -0.3)], [Out.Wipe(AnimationEdge.Right, 0, 0.35)]);
            c.Add(c.Text("timer", "Countdown", "04:59", 660, 618, 600, 140, 120, Fonts.Digits, align: TextHorizontalAlignment.Center, tracking: 6, link: Timer, shrink: false, shadow: true),
                [In.Focus(20, 0.8, AnimationTrigger.WithPrevious, 0.1)], [Out.Fade(0.35)]);
            c.Add(c.Text("detail", "Details", "Welcome — grab a seat, we'll begin in a few minutes", 260, 768, 1400, 40, 26, Fonts.MontserratSemiBold, Ink.Soft,
                    TextHorizontalAlignment.Center, tracking: 1, shadow: true),
                [In.Rise(14, 0.6, AnimationTrigger.WithPrevious, 0.2)], [Out.Fade(0.35)]);
        }

        public static void CountdownCorner(Composer c)
        {
            c.Add(c.Glass("glass", 1450, 838, 320, 132, 24), [In.Grow(0.92, 0.55)], [Out.Shrink(0.95, 0.35, AnimationTrigger.AfterPrevious, -0.15)]);
            c.Add(c.Sheen("sheen", 1474, 839, 272), [In.Wipe(AnimationEdge.Left, 120, 0.8, AnimationTrigger.WithPrevious, 0.15)], [Out.Fade(0.25)]);
            c.Add(c.Text("kicker", "Kicker", "Service starts in", 1450, 852, 320, 28, 16, Fonts.PoppinsMedium, Ink.Faint, TextHorizontalAlignment.Center,
                    caps: true, tracking: 4, shrink: false),
                [In.Fade(0.4, AnimationTrigger.AfterPrevious, -0.4)], [Out.Fade(0.2)]);
            c.Add(c.Text("timer", "Countdown", "04:59", 1450, 874, 320, 88, 80, Fonts.Digits, align: TextHorizontalAlignment.Center, tracking: 3, link: Timer, shrink: false),
                [In.Focus(16, 0.7, AnimationTrigger.WithPrevious, 0.05)], [Out.Fade(0.25)]);
        }

        public static void CountdownCenter(Composer c)
        {
            c.Add(c.Box("vignette", 0, 0, 1920, 1080, Composer.Gradient(90, ("#00000040", 0), ("#00000026", 0.5), ("#00000066", 1))), [In.Fade(0.9)],
                [Out.Fade(0.7, AnimationTrigger.AfterPrevious, -0.3)]);
            c.Add(c.Text("kicker", "Kicker", "Service starts in", 260, 250, 1400, 80, 54, Fonts.SerifItalic, Ink.Gold, TextHorizontalAlignment.Center, shadow: true, shrink: false),
                [In.Rise(16, 0.8, AnimationTrigger.WithPrevious, 0.15)], [Out.Fade(0.35)]);
            c.Add(c.Text("timer", "Countdown", "04:59", 160, 330, 1600, 430, 400, Fonts.Digits, Ink.Ivory, TextHorizontalAlignment.Center, tracking: 6, lineHeight: 0.9,
                    link: Timer, shrink: false, shadow: true),
                [In.Focus(30, 1.0, AnimationTrigger.AfterPrevious, -0.5)], [Out.Unfocus(20, 0.5)]);
            c.Add(c.Box("rule", 900, 780, 120, 1.5, Composer.Solid(Ink.Gold)), [In.Wipe(AnimationEdge.Right, 0, 0.6, AnimationTrigger.AfterPrevious, -0.5)],
                [Out.Wipe(AnimationEdge.Left, 0, 0.3)]);
            c.Add(c.Text("detail", "Message", "Take a moment. Breathe. You are welcome here.", 260, 806, 1400, 50, 30, Fonts.MontserratRegular, "#F6F0E6CC",
                    TextHorizontalAlignment.Center, tracking: 1, shadow: true),
                [In.Rise(12, 0.6, AnimationTrigger.WithPrevious, 0.15)], [Out.Fade(0.35)]);
        }
    }
}
