using System.Globalization;

namespace MxU.Slides.Core.Scene;

// The platform-neutral description of one rendered frame: layers of items in
// canvas coordinates (1920×1080 by default). The Windows renderer draws it;
// nothing here knows about WPF. Mirrors RenderEngine/SceneModel.swift.

public readonly record struct SceneColor(double Red, double Green, double Blue, double Alpha = 1)
{
    public static readonly SceneColor White = new(1, 1, 1);
    public static readonly SceneColor Black = new(0, 0, 0);
    public static readonly SceneColor Clear = new(0, 0, 0, 0);

    public SceneColor WithAlpha(double alpha) => this with { Alpha = alpha };
}

public static class ColorHex
{
    /// <summary>Parses "#RRGGBB" or "#RRGGBBAA" (the Mac app's format); null when malformed.</summary>
    public static SceneColor? Color(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return null;
        var digits = hex.AsSpan();
        if (digits.StartsWith("#")) digits = digits[1..];
        if (digits.Length is not (6 or 8)) return null;
        if (!ulong.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)) return null;
        var hasAlpha = digits.Length == 8;
        var rgb = hasAlpha ? value >> 8 : value;
        var alpha = hasAlpha ? (value & 0xFF) / 255.0 : 1;
        return new SceneColor(((rgb >> 16) & 0xFF) / 255.0, ((rgb >> 8) & 0xFF) / 255.0, (rgb & 0xFF) / 255.0, alpha);
    }

    public static string Hex(SceneColor color)
    {
        static ulong Byte(double component) => (ulong)Math.Round(Math.Clamp(component, 0, 1) * 255);
        var value = Byte(color.Red) << 24 | Byte(color.Green) << 16 | Byte(color.Blue) << 8 | Byte(color.Alpha);
        return $"#{value:X8}";
    }
}

public readonly record struct SceneSize(double Width, double Height)
{
    public static readonly SceneSize Hd = new(1920, 1080);
}

public readonly record struct SceneRect(double X, double Y, double Width, double Height)
{
    public SceneRect(SceneSize size) : this(0, 0, size.Width, size.Height) { }

    public double Right => X + Width;
    public double Bottom => Y + Height;

    public SceneRect Inset(double dx, double dy) => new(X + dx, Y + dy, Math.Max(0, Width - 2 * dx), Math.Max(0, Height - 2 * dy));
}

/// <summary>Compositing order, bottom to top. Raw names match the Mac app's layer strings.</summary>
public enum LayerKind
{
    VideoInput,
    LoopingVideos,
    StillGraphics,
    Videos,
    Slide,
    Overlays,
    Alerts,
}

public static class LayerKinds
{
    public static readonly LayerKind[] All = Enum.GetValues<LayerKind>();

    public static string Raw(this LayerKind layer) => layer switch
    {
        LayerKind.VideoInput => "videoInput",
        LayerKind.LoopingVideos => "loopingVideos",
        LayerKind.StillGraphics => "stillGraphics",
        LayerKind.Videos => "videos",
        LayerKind.Slide => "slide",
        LayerKind.Overlays => "overlays",
        LayerKind.Alerts => "alerts",
        _ => layer.ToString(),
    };

    public static LayerKind? Parse(string? raw) =>
        All.Cast<LayerKind?>().FirstOrDefault(layer => layer!.Value.Raw() == raw);

    public static string DisplayName(this LayerKind layer) => layer switch
    {
        LayerKind.VideoInput => "Video Input",
        LayerKind.LoopingVideos => "Background Media",
        LayerKind.StillGraphics => "Still Graphics",
        LayerKind.Videos => "Foreground Videos",
        LayerKind.Slide => "Slide",
        LayerKind.Overlays => "Overlays",
        LayerKind.Alerts => "Alerts",
        _ => layer.ToString(),
    };
}

public enum SceneTextAlignment { Left, Center, Right }

public enum SceneTextVerticalAlignment { Top, Middle, Bottom }

public enum SceneTextTransform { None, Uppercase }

public enum SceneMediaScaleMode { Fill, Fit, Stretch }

public enum SceneBlendMode { Normal, Multiply, Screen, Add }

public readonly record struct SceneSourceRect(double X, double Y, double Width, double Height);

public readonly record struct SceneGradientStop(SceneColor Color, double Position);

public abstract record SceneFill
{
    private SceneFill() { }

    public sealed record NoFill : SceneFill;
    public sealed record Solid(SceneColor Color) : SceneFill;
    public sealed record LinearGradient(double AngleDegrees, IReadOnlyList<SceneGradientStop> Stops) : SceneFill
    {
        public bool Equals(LinearGradient? other) =>
            other is not null && AngleDegrees == other.AngleDegrees && Stops.SequenceEqual(other.Stops);
        public override int GetHashCode() => HashCode.Combine(AngleDegrees, Stops.Count);
    }
    public sealed record Media(string Id, SceneMediaScaleMode ScaleMode, SceneSourceRect? SourceRect = null) : SceneFill;

    public static readonly SceneFill None = new NoFill();
}

public sealed record TextShadow(SceneColor Color, double BlurRadius, double OffsetX, double OffsetY);

public sealed record TextOutline(SceneColor Color, double Width);

public enum StrokeDash { Solid, Dashed, Dotted }

public sealed record SceneStroke(SceneColor Color, double Width, StrokeDash Dash = StrokeDash.Solid);

public enum LineFillWidthMode { FullWidth, LineWidth, MaxLineWidth }

public sealed record TextLineFillStyle(
    SceneFill Fill,
    LineFillWidthMode WidthMode,
    double VerticalPadding,
    double HorizontalPadding,
    double VerticalOffset,
    double HorizontalOffset,
    double CornerRadius);

public sealed record TextLineOverride(
    int LineIndex,
    string? FontName,
    double? FontSize,
    SceneColor? Color,
    double? Tracking);

public sealed record StyleRun(
    int Line,
    int Column,
    int Length,
    bool? Underline,
    bool? Strikethrough,
    string? FontName,
    double? FontSize,
    SceneColor? Color,
    SceneColor? HighlightColor);

public sealed record ChordRun(int Line, int Column, string Symbol);

public sealed record StyledText
{
    public const double DefaultMinFontSize = 24;

    public string String { get; init; } = "";
    public string FontName { get; init; } = "HelveticaNeue-Bold";
    public double FontSize { get; init; } = 96;
    public SceneColor Color { get; init; } = SceneColor.White;
    public SceneTextAlignment Alignment { get; init; } = SceneTextAlignment.Center;
    public SceneTextVerticalAlignment VerticalAlignment { get; init; } = SceneTextVerticalAlignment.Middle;
    public TextShadow? Shadow { get; init; }
    public double Tracking { get; init; }
    public double LineHeightMultiple { get; init; } = 1;
    public SceneTextTransform Transform { get; init; }
    public bool TabularFigures { get; init; }
    public bool AutoShrink { get; init; }
    public double MinFontSize { get; init; } = DefaultMinFontSize;
    public bool KeepLinesWhole { get; init; }
    public bool BalancedWrap { get; init; }
    public TextOutline? Outline { get; init; }
    public SceneFill? Fill { get; init; }
    public TextLineFillStyle? LineFill { get; init; }
    public IReadOnlyList<TextLineOverride> LineOverrides { get; init; } = [];
    public IReadOnlyList<StyleRun> StyleRuns { get; init; } = [];
    public IReadOnlyList<ChordRun> Chords { get; init; } = [];
    public SceneColor? ChordColor { get; init; }
    public double InsetTop { get; init; }
    public double InsetLeft { get; init; }
    public double InsetBottom { get; init; }
    public double InsetRight { get; init; }
    public double FirstLineIndent { get; init; }
    public double LeftIndent { get; init; }
    public double RightIndent { get; init; }
    public double ParagraphSpacing { get; init; }
    public bool Underline { get; init; }
    public bool Strikethrough { get; init; }

    /// <summary>The string as displayed (after the uppercase transform).</summary>
    public string DisplayString => Transform == SceneTextTransform.Uppercase
        ? String.ToUpper(CultureInfo.CurrentCulture)
        : String;
}

public abstract record SceneShapeKind
{
    private SceneShapeKind() { }

    public sealed record Rectangle : SceneShapeKind;
    public sealed record RoundedRectangle(double CornerRadius) : SceneShapeKind;
    public sealed record Ellipse : SceneShapeKind;
    public sealed record Path(string Data) : SceneShapeKind;

    public static readonly SceneShapeKind Rect = new Rectangle();
}

public sealed record ShapeStyle(SceneShapeKind Kind, SceneFill Fill, SceneStroke? Stroke = null, TextShadow? Shadow = null);

public abstract record ItemContent
{
    private ItemContent() { }

    public sealed record Text(StyledText Styled) : ItemContent;
    public sealed record Shape(ShapeStyle Style) : ItemContent;
    public sealed record Media(string Id, SceneMediaScaleMode ScaleMode, SceneSourceRect? SourceRect = null) : ItemContent;
}

public sealed record RenderItem
{
    public required string Id { get; init; }
    public required SceneRect Frame { get; init; }
    public required ItemContent Content { get; init; }
    public double RotationDegrees { get; init; }

    /// <summary>Shear in degrees about the centre: x' = x − y·tan(skewX), y' = y + x·tan(skewY) (Compositor.swift).</summary>
    public double SkewX { get; init; }
    public double SkewY { get; init; }
    public bool FlipHorizontal { get; init; }
    public bool FlipVertical { get; init; }
    public double Opacity { get; init; } = 1;
    public SceneBlendMode BlendMode { get; init; }
    public double BlurRadius { get; init; }

    /// <summary>The matte (an object id on the same layer) this item is clipped to; <see cref="MaskOut"/> inverts it.</summary>
    public string? MaskedBy { get; init; }
    public bool MaskOut { get; init; }

    /// <summary>Set on a matte: it is never drawn, only used to clip the items masked by it.</summary>
    public string? MatteGroup { get; init; }

    /// <summary>The media this item should play when it is a video, so playback can be shared across views.</summary>
    public bool Loops { get; init; } = true;
}

public sealed class RenderLayer(LayerKind kind)
{
    public LayerKind Kind { get; } = kind;
    public List<RenderItem> Items { get; } = [];
}

public sealed class RenderScene
{
    public RenderScene(SceneSize canvasSize, SceneColor? background = null)
    {
        CanvasSize = canvasSize;
        Background = background ?? SceneColor.Black;
        Layers = LayerKinds.All.Select(kind => new RenderLayer(kind)).ToArray();
    }

    public SceneSize CanvasSize { get; }
    public SceneColor Background { get; set; }

    /// <summary>All layers in compositing order (bottom first), always one per <see cref="LayerKind"/>.</summary>
    public IReadOnlyList<RenderLayer> Layers { get; }

    public RenderLayer this[LayerKind kind] => Layers[(int)kind];

    public void AddItem(RenderItem item, LayerKind layer) => this[layer].Items.Add(item);

    public IEnumerable<RenderItem> AllItems => Layers.SelectMany(layer => layer.Items);

    public bool IsEmpty => Layers.All(layer => layer.Items.Count == 0);

    /// <summary>Every media id the scene shows, so views can preload and share playback.</summary>
    public IEnumerable<string> MediaIds => AllItems
        .Select(item => item.Content switch
        {
            ItemContent.Media media => media.Id,
            ItemContent.Shape { Style.Fill: SceneFill.Media fill } => fill.Id,
            _ => null,
        })
        .OfType<string>()
        .Distinct();
}
