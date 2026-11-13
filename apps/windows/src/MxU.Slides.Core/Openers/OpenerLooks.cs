namespace MxU.Slides.Core.Openers;

/// <summary>A font the app ships (Core/Library/BundledFonts.cs), by the names its file carries.</summary>
public sealed record OpenerFont(string PostScriptName, string Family, string Style);

/// <summary>How a look's words arrive (they leave the same way, onward).</summary>
public enum OpenerEntrance
{
    /// <summary>Each line fades in while rising a little.</summary>
    Rise,

    /// <summary>Each line fades in while settling from slightly too big.</summary>
    Zoom,

    /// <summary>A plain, slow fade.</summary>
    Fade,

    /// <summary>Each line slides in from the left behind an accent bar.</summary>
    Slide,
}

/// <summary>
/// One look: the fonts, the layout and the way the words move. A look is typography only, so
/// every look works over every motion background; each just suggests one
/// (<see cref="Background"/>, <see cref="Palette"/>) to start from. Sizes are fractions of the
/// picture's height, so a look is the same at any size.
/// </summary>
/// <param name="Left">Words are set left, beside an accent bar (otherwise centred).</param>
/// <param name="Caps">Titles are set in capitals.</param>
/// <param name="TitleSize">The title's letter height, as a fraction of the picture's height.</param>
/// <param name="TitleTracking">Extra space between a title's letters, in ems.</param>
/// <param name="Rule">A short thin rule sits under the title (centred looks).</param>
public sealed record OpenerLook(string Id, string Name, string Description, OpenerFont TitleFont, OpenerFont TextFont,
    bool Left, bool Caps, double TitleSize, double TitleTracking, bool Rule, OpenerEntrance Entrance, string Background, string Palette);

/// <summary>The looks "Make an opener…" offers: a few good ones rather than a free-form editor.</summary>
public static class OpenerLooks
{
    public static readonly IReadOnlyList<OpenerLook> All =
    [
        new("clean", "Clean", "Montserrat, centred, with a thin rule. Words rise into place.",
            new("Montserrat-SemiBold", "Montserrat", "SemiBold"), new("Montserrat-Regular", "Montserrat", "Regular"),
            Left: false, Caps: false, TitleSize: 0.105, TitleTracking: -0.01, Rule: true, OpenerEntrance.Rise, "gradient-flow", "Midnight"),
        new("bold", "Bold", "Tall Bebas Neue capitals that settle in from large. Good for countdowns.",
            new("BebasNeue-Regular", "Bebas Neue", "Regular"), new("Montserrat-Bold", "Montserrat", "Bold"),
            Left: false, Caps: true, TitleSize: 0.17, TitleTracking: 0.03, Rule: false, OpenerEntrance.Zoom, "light-rays", "Golden Hour"),
        new("serif", "Serif", "DM Serif Display with an italic second line. A slow, quiet fade.",
            new("DMSerifDisplay-Regular", "DM Serif Display", "Regular"), new("DMSerifDisplay-Italic", "DM Serif Display", "Italic"),
            Left: false, Caps: false, TitleSize: 0.12, TitleTracking: 0, Rule: false, OpenerEntrance.Fade, "bokeh", "Rose Gold"),
        new("broadcast", "Broadcast", "Heavy Poppins set left beside an accent bar. Words slide in.",
            new("Poppins-ExtraBold", "Poppins", "ExtraBold"), new("Poppins-Medium", "Poppins", "Medium"),
            Left: true, Caps: false, TitleSize: 0.11, TitleTracking: -0.015, Rule: false, OpenerEntrance.Slide, "waves", "Ocean"),
        new("minimal", "Minimal", "Small, widely spaced Poppins capitals. Understated.",
            new("Poppins-Medium", "Poppins", "Medium"), new("Poppins-Regular", "Poppins", "Regular"),
            Left: false, Caps: true, TitleSize: 0.066, TitleTracking: 0.32, Rule: true, OpenerEntrance.Fade, "smoke", "Silver"),
    ];

    /// <summary>The look with this id, or null (a recipe from a newer version, say).</summary>
    public static OpenerLook? Find(string? id) => All.FirstOrDefault(l => l.Id == id);
}
