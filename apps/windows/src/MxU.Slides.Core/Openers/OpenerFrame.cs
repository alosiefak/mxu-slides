namespace MxU.Slides.Core.Openers;

/// <summary>What a line of words is, which decides its font and size in a look.</summary>
public enum OpenerRole
{
    /// <summary>A small line above the title ("Service starts in").</summary>
    Kicker,
    Title,
    Subtitle,

    /// <summary>A paragraph (an announcement's details).</summary>
    Body,

    /// <summary>The countdown's clock: big figures, each in a cell of the same width so they don't jiggle.</summary>
    Digits,
}

/// <summary>
/// One line of words at one moment: what it says and how far through its move it is.
/// <paramref name="Rise"/> and <paramref name="Slide"/> are offsets down and right, as fractions
/// of the picture's height; <paramref name="Scale"/> is about the line's own centre.
/// </summary>
public sealed record OpenerLine(OpenerRole Role, string Text, double Opacity = 1, double Rise = 0, double Slide = 0, double Scale = 1);

/// <summary>
/// A block of words (and perhaps a picture) at one moment. <paramref name="Accent"/> is how far
/// the look's rule or bar has drawn in (0–1). <paramref name="PictureBeside"/> puts the picture in
/// a panel on the right with the words on the left (an announcement's photo); otherwise it sits
/// above the words (a bumper's logo).
/// </summary>
public sealed record OpenerCard(IReadOnlyList<OpenerLine> Lines, string? Picture = null, double PictureOpacity = 0, double Accent = 0, bool PictureBeside = false);

/// <summary>
/// A montage picture or clip at one moment. <paramref name="Zoom"/> is 1 when it just fills the
/// screen; <paramref name="PanX"/> and <paramref name="PanY"/> (−1 to 1) say where in the spare
/// room the zoom leaves the view sits. <paramref name="ClipSeconds"/> is how far into a video
/// clip this moment is.
/// </summary>
public sealed record OpenerShot(string Path, double Opacity, double Zoom, double PanX, double PanY, double ClipSeconds);

/// <summary>
/// Everything in one frame of an opener, worked out from the plan and the time alone
/// (<see cref="OpenerPlan.FrameAt"/>). It's a description, not pixels: the painter
/// (MxU.Slides.MotionTemplates/Openers/OpenerPainter.cs) draws it, and tests can check it
/// without drawing anything. Drawn bottom to top: the motion background at
/// <paramref name="BackgroundPhase"/> (when <paramref name="ShowBackground"/>), the shots, the
/// cards, then black at <paramref name="Black"/> opacity (the fades at each end).
/// </summary>
public sealed record OpenerFrame(double BackgroundPhase, bool ShowBackground, IReadOnlyList<OpenerShot> Shots, IReadOnlyList<OpenerCard> Cards, double Black);
