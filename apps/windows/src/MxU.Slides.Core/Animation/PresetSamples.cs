using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Animation;

/// <summary>
/// What each tile of the overlay editor's preset gallery plays: a small lower third (a plate with
/// two lines of words on it) with the preset put on both, so you can see what it does before you
/// apply it. Its steps are filed the way the editor files a preset (an overlay gets no clicks, so
/// they play "when it goes up" and "when it's taken down"), which makes the tile play exactly what
/// the preset would do to your own overlay.
/// <para>
/// A new sample is made for every tile, so a preview never shares steps with the catalogs or with
/// the overlay being edited. Windows-only (the Mac's presets are a menu without previews).
/// </para>
/// </summary>
public static class PresetSamples
{
    public const string PlateId = "sample.plate";
    public const string TextId = "sample.text";

    /// <summary>The sample with a preset applied by <paramref name="apply"/> (to the plate, then the words).</summary>
    public static Overlay Make(Func<IAnimatedComposition, List<string>, List<string>> apply)
    {
        var overlay = new Overlay { Id = "preset-sample", Name = "Preset sample", Objects = [Plate(), Words()] };
        var added = apply(overlay, [PlateId, TextId]);
        OverlaySteps.File(overlay, added);
        return overlay;
    }

    // Big enough to read in a tile a fourteenth of the canvas wide (the words are what move), and
    // each line short enough to stay one line, so "line by line" shows two lines.
    private static SlideObject Plate() => new()
    {
        Id = PlateId, ObjectKind = SlideObjectKind.Shape, Name = "Plate", Text = "",
        X = 180, Y = 300, Width = 1560, Height = 480,
        ShapeKind = ShapeKind.RoundedRectangle, CornerRadius = 40,
        Fill = new ObjectFill { FillKind = FillKind.Solid, ColorHex = "#0D1015E6" },
        Stroke = new ObjectStroke { ColorHex = "#FFFFFF33", Width = 4 },
    };

    private static SlideObject Words() => new()
    {
        Id = TextId, ObjectKind = SlideObjectKind.Text, Name = "Words", Text = "Amazing grace\nhow sweet the sound",
        X = 260, Y = 330, Width = 1400, Height = 420,
        TextStyle = new TextStyle
        {
            FontName = "Poppins-SemiBold", FontSize = 104, ColorHex = "#FFFFFFFF", LineHeightMultiple = 1.1,
            HorizontalAlignment = TextHorizontalAlignment.Center, VerticalAlignment = TextVerticalAlignment.Middle,
        },
    };
}
