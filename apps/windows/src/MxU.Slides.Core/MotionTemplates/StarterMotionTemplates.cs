using static MxU.Slides.Core.MotionTemplates.LottieBuilder;

namespace MxU.Slides.Core.MotionTemplates;

/// <summary>
/// The motion templates the app comes with, written here as Lottie animations (no downloads):
/// <list type="bullet">
/// <item><b>Name Strap</b>: a speaker's name and title on a navy plate behind a gold bar. The bar
/// grows, the plate sweeps out from it, and the words rise in; on Take Down they drop away and
/// the plate sweeps back.</item>
/// <item><b>Scripture Bar</b>: a verse and its reference on a wide bar under a gold rule that
/// draws out from the centre. Long verses shrink to fit the bar.</item>
/// <item><b>Title Card</b>: a full-screen welcome with three soft glows drifting on an
/// eight-second loop, for a background before the service.</item>
/// </list>
/// The lower thirds have In, Hold and Out markers, so they animate in, hold, and animate out on
/// Take Down (<see cref="MotionTimeline"/>). Their words, colours and the title card's glows are
/// all fields (<see cref="MotionFields"/>). They use the fonts the app ships (Montserrat, DM
/// Serif Display, Bebas Neue: Core/Library/BundledFonts.cs).
/// Windows only: the Mac has no Lottie support.
/// </summary>
public static class StarterMotionTemplates
{
    public sealed record Starter(string Id, string Name, string Description, MotionUse Use, Func<LottieDocument> Make);

    public static readonly IReadOnlyList<Starter> All =
    [
        new("starter.name-strap", "Name Strap", "A name and title on a plate, with a gold accent. Animates in, holds, and out on Take Down.", MotionUse.Overlay, NameStrap),
        new("starter.scripture-bar", "Scripture Bar", "A verse and its reference on a wide bar under a gold rule. Long verses shrink to fit.", MotionUse.Overlay, ScriptureBar),
        new("starter.title-card", "Title Card", "A full-screen welcome with soft glows drifting on a seamless loop.", MotionUse.Background, TitleCard),
    ];

    /// <summary>Puts the starters in the catalogue, or refreshes them when this version of the app draws them differently.</summary>
    public static void Seed(MotionTemplateStore store)
    {
        foreach (var starter in All)
        {
            var document = starter.Make();
            var current = store.Get(starter.Id);
            if (current is not null && current.LottieHash == HashOf(document) && current.Name == starter.Name) continue;
            store.Add(document, starter.Name, starter: true, id: starter.Id, description: starter.Description, suggestedUse: starter.Use);
        }
    }

    private static string HashOf(LottieDocument document) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(document.ToUtf8())).ToLowerInvariant();

    private const string Navy = "#0F1F3A";
    private const string Gold = "#F5B700";
    private const string White = "#FFFFFF";

    /// <summary>1920 × 1080 at 60 fps, 4.5 s: In 0–48, Hold 48–222, Out 222–270.</summary>
    public static LottieDocument NameStrap()
    {
        const double frames = 270, outAt = 222;
        var b = new LottieBuilder("Name Strap", 1920, 1080, 60, frames)
            .Font("Montserrat-Bold", "Montserrat", "Bold")
            .Font("Montserrat-SemiBold", "Montserrat", "SemiBold")
            .Marker("In", 0, 48).Marker("Hold", 48, outAt - 48).Marker("Out", outAt, frames - outAt);

        // The words: rise in after the plate, drop away first on the way out.
        b.Layer(b.TextLayer("Name", Transform(
                Animated(At(16, Ease.Out, 214, 880, 0), At(42, Ease.Linear, 214, 856, 0), At(outAt, Ease.In, 214, 856, 0), At(outAt + 16, Ease.Linear, 214, 842, 0)),
                opacity: Animated(At(16, Ease.Out, 0), At(34, Ease.Linear, 100), At(outAt, Ease.In, 100), At(outAt + 14, Ease.Linear, 0))),
            "Pastor Grace Okafor", "Montserrat-Bold", 50, White, 760, 62));
        b.Layer(b.TextLayer("Title", Transform(
                Animated(At(24, Ease.Out, 216, 938, 0), At(48, Ease.Linear, 216, 918, 0), At(outAt + 4, Ease.In, 216, 918, 0), At(outAt + 20, Ease.Linear, 216, 906, 0)),
                opacity: Animated(At(24, Ease.Out, 0), At(42, Ease.Linear, 100), At(outAt + 4, Ease.In, 100), At(outAt + 18, Ease.Linear, 0))),
            "LEAD PASTOR", "Montserrat-SemiBold", 27, Gold, 760, 38, tracking: 160));

        // The plate sweeps out to the right from the bar (its left edge is the layer's origin), and back.
        b.Layer(b.ShapeLayer("Plate", Transform(Static(162, 900, 0),
                scale: Animated(At(6, Ease.Out, 0, 100, 100), At(36, Ease.Linear, 100, 100, 100), At(outAt + 12, Ease.In, 100, 100, 100), At(outAt + 40, Ease.Linear, 0, 100, 100))),
            Group("Plate", Rect(420, 0, 840, 132, 4), Fill(Navy, 94))));

        // The accent bar grows from its middle first, and goes last.
        b.Layer(b.ShapeLayer("Accent", Transform(Static(150, 900, 0),
                scale: Animated(At(0, Ease.Out, 100, 0, 100), At(16, Ease.Linear, 100, 100, 100), At(outAt + 30, Ease.In, 100, 100, 100), At(outAt + 46, Ease.Linear, 100, 0, 100))),
            Group("Accent", Rect(6, 0, 12, 132), Fill(Gold))));
        return b.Document();
    }

    /// <summary>1920 × 1080 at 60 fps, 5.5 s: In 0–54, Hold 54–282, Out 282–330.</summary>
    public static LottieDocument ScriptureBar()
    {
        const double frames = 330, outAt = 282;
        const string ink = "#0B1220", rule = "#D4AF37";
        var b = new LottieBuilder("Scripture Bar", 1920, 1080, 60, frames)
            .Font("DMSerifDisplay-Regular", "DM Serif Display", "Regular")
            .Font("Montserrat-SemiBold", "Montserrat", "SemiBold")
            .Marker("In", 0, 54).Marker("Hold", 54, outAt - 54).Marker("Out", outAt, frames - outAt);

        b.Layer(b.TextLayer("Verse", Transform(
                Animated(At(18, Ease.Out, 220, 858, 0), At(46, Ease.Linear, 220, 842, 0), At(outAt, Ease.In, 220, 842, 0), At(outAt + 18, Ease.Linear, 220, 832, 0)),
                opacity: Animated(At(18, Ease.Out, 0), At(40, Ease.Linear, 100), At(outAt, Ease.In, 100), At(outAt + 16, Ease.Linear, 0))),
            "For God so loved the world, that he gave his only begotten Son, that whosoever believeth in him should not perish, but have everlasting life.",
            "DMSerifDisplay-Regular", 41, White, 1480, 104, lineHeight: 51));
        b.Layer(b.TextLayer("Reference", Transform(
                Animated(At(28, Ease.Out, 220, 960, 0), At(52, Ease.Linear, 220, 952, 0), At(outAt + 2, Ease.In, 220, 952, 0), At(outAt + 18, Ease.Linear, 220, 946, 0)),
                opacity: Animated(At(28, Ease.Out, 0), At(46, Ease.Linear, 100), At(outAt + 2, Ease.In, 100), At(outAt + 16, Ease.Linear, 0))),
            "JOHN 3:16  ·  KJV", "Montserrat-SemiBold", 26, rule, 1480, 36, tracking: 140));

        b.Layer(b.ShapeLayer("Rule", Transform(Static(960, 821, 0),
                scale: Animated(At(0, Ease.Out, 0, 100, 100), At(30, Ease.Linear, 100, 100, 100), At(outAt + 10, Ease.In, 100, 100, 100), At(outAt + 34, Ease.Linear, 0, 100, 100))),
            Group("Rule", Rect(0, 0, 1600, 4), Fill(rule))));
        b.Layer(b.ShapeLayer("Bar", Transform(
                Animated(At(6, Ease.Out, 960, 932, 0), At(38, Ease.Linear, 960, 908, 0), At(outAt + 12, Ease.In, 960, 908, 0), At(outAt + 44, Ease.Linear, 960, 926, 0)),
                opacity: Animated(At(6, Ease.Out, 0), At(30, Ease.Linear, 100), At(outAt + 14, Ease.In, 100), At(outAt + 44, Ease.Linear, 0))),
            Group("Bar", Rect(0, 0, 1600, 172, 6), Fill(ink, 90))));
        return b.Document();
    }

    /// <summary>1920 × 1080 at 30 fps, an 8-second loop with no markers: every glow is back where it started on the last frame.</summary>
    public static LottieDocument TitleCard()
    {
        const double frames = 240;
        var b = new LottieBuilder("Title Card", 1920, 1080, 30, frames)
            .Font("BebasNeue-Regular", "Bebas Neue", "Regular")
            .Font("Montserrat-SemiBold", "Montserrat", "SemiBold");

        b.Layer(b.TextLayer("Title", Transform(Static(160, 336, 0)), "WELCOME", "BebasNeue-Regular", 250, White, 1600, 260, justify: 2, tracking: 60));
        b.Layer(b.TextLayer("Subtitle", Transform(Static(160, 640, 0)), "We're so glad you're here", "Montserrat-SemiBold", 44, "#E2E8F0", 1600, 64, justify: 2));
        b.Layer(b.ShapeLayer("Rule", Transform(Static(960, 612, 0), opacity: Static(70)), Group("Rule", Rect(0, 0, 180, 4, 2), Fill(White))));

        // Three glows, each drifting round its own loop, back to its start on frame 240.
        JsonGlow(b, "Glow 3", "#0EA5E9", 520, 70, (1500, 260), (1340, 360), (1560, 440));
        JsonGlow(b, "Glow 2", "#DB2777", 680, 85, (1450, 820), (1240, 660), (1420, 540));
        JsonGlow(b, "Glow 1", "#4F46E5", 760, 100, (480, 300), (720, 440), (520, 600));
        b.Layer(b.SolidLayer("Background", "#0B1024"));
        return b.Document();

        static void JsonGlow(LottieBuilder b, string name, string hex, double radius, double opacity, (double X, double Y) a, (double X, double Y) c, (double X, double Y) d) =>
            b.Layer(b.ShapeLayer(name, Transform(
                    Animated(At(0, Ease.InOut, a.X, a.Y, 0), At(80, Ease.InOut, c.X, c.Y, 0), At(160, Ease.InOut, d.X, d.Y, 0), At(240, Ease.Linear, a.X, a.Y, 0)),
                    opacity: Static(opacity)),
                Group(name, Ellipse(0, 0, radius * 2, radius * 2), Glow(hex, 0, 0, radius, 0.85))));
    }
}
