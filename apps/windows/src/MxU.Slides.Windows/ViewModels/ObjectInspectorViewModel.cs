using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Rendering;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// Properties of the object selected on the canvas. Getters show the effective
/// value (inheriting the theme placeholder's style); setters write an override
/// onto the object itself, as the Mac editor does.
/// </summary>
public sealed partial class ObjectInspectorViewModel : ObservableObject
{
    private readonly SlideObject o;
    private readonly SlideObject? placeholder;
    private readonly Theme? theme;
    private readonly Action changed;
    private readonly SceneSize canvas;

    public ObjectInspectorViewModel(SlideObject target, SlideObject? placeholder, Theme? theme, SceneSize canvas, Action changed)
    {
        o = target;
        this.placeholder = placeholder;
        this.theme = theme;
        this.canvas = canvas;
        this.changed = changed;
    }

    public SlideObject Object => o;

    public bool IsText => o.ObjectKind == SlideObjectKind.Text;
    public bool IsShape => o.ObjectKind == SlideObjectKind.Shape && SlideObjectNormalization.Normalized(o).Fill?.FillKind != FillKind.Media;
    public bool IsMedia => SlideObjectNormalization.Normalized(o) is { ObjectKind: SlideObjectKind.Shape, Fill.FillKind: FillKind.Media };
    public bool InheritsTheme => IsText && placeholder is not null;

    public string Title => $"{(IsText ? "Text" : IsMedia ? "Media" : "Shape")}: {(string.IsNullOrWhiteSpace(o.Name) ? "Untitled" : o.Name)}";

    public string Name
    {
        get => o.Name;
        set => Set(() => o.Name = value);
    }

    private void Set(Action apply, params string[] alsoNotify)
    {
        apply();
        foreach (var name in alsoNotify) OnPropertyChanged(name);
        changed();
    }

    private static string Num(double v) => Math.Round(v, 1).ToString(CultureInfo.CurrentCulture);

    private static double? Parse(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v)
            ? v : null;

    // MARK: Geometry

    private SceneRect Frame => SlideSceneBuilder.Frame(o, placeholder, canvas);

    private void SetFrame(SceneRect f) => Set(() =>
    {
        o.X = f.X;
        o.Y = f.Y;
        o.Width = Math.Max(1, f.Width);
        o.Height = Math.Max(1, f.Height);
    }, nameof(X), nameof(Y), nameof(W), nameof(H));

    public string X { get => Num(Frame.X); set { if (Parse(value) is { } v) SetFrame(Frame with { X = v }); } }
    public string Y { get => Num(Frame.Y); set { if (Parse(value) is { } v) SetFrame(Frame with { Y = v }); } }
    public string W { get => Num(Frame.Width); set { if (Parse(value) is { } v) SetFrame(Frame with { Width = v }); } }
    public string H { get => Num(Frame.Height); set { if (Parse(value) is { } v) SetFrame(Frame with { Height = v }); } }

    public string Rotation
    {
        get => Num(o.RotationDegrees ?? 0);
        set { if (Parse(value) is { } v) Set(() => o.RotationDegrees = Math.Abs(v % 360) < 0.01 ? null : v % 360); }
    }

    public double Opacity
    {
        get => (o.Opacity ?? 1) * 100;
        set => Set(() => o.Opacity = value >= 99.5 ? null : Math.Clamp(value / 100, 0, 1));
    }

    /// <summary>Re-reads every setting after something else (the overlay editor's fields) changed the object.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);

    /// <summary>Called after the canvas moves/resizes the object, so the numbers follow.</summary>
    public void GeometryChanged()
    {
        OnPropertyChanged(nameof(X));
        OnPropertyChanged(nameof(Y));
        OnPropertyChanged(nameof(W));
        OnPropertyChanged(nameof(H));
    }

    // MARK: Text

    private TextStyle Own => o.TextStyle ??= new TextStyle();
    private TextStyle? Inherited => placeholder?.Id == o.Id ? null : placeholder?.TextStyle;
    private StyledText Effective => SlideSceneBuilder.StyledTextFor(o, theme, placeholder);

    public string Text
    {
        get => o.Text;
        set => Set(() => o.Text = value.Replace("\r\n", "\n"));
    }

    public IReadOnlyList<string> FontFamilies { get; } = FontMap.InstalledFamilies();

    /// <summary>
    /// The fonts to pick from, with the current one in the list even when this PC hasn't got
    /// it (a Mac font such as Helvetica Neue, drawn here with a stand-in).
    /// </summary>
    public IReadOnlyList<string> FontChoices => FontFamilies.Contains(FontFamily) ? FontFamilies : [FontFamily, .. FontFamilies];

    /// <summary>The font family part of the font name (e.g. "HelveticaNeue" or "Segoe UI").</summary>
    public string FontFamily
    {
        get
        {
            var name = Effective.FontName;
            return name.Contains('-') ? name[..name.IndexOf('-')] : name;
        }
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            Set(() => Own.FontName = FontMap.WithTraits(value.Trim(), Bold, Italic), nameof(Bold), nameof(Italic));
        }
    }

    public bool Bold
    {
        get => FontMap.IsBold(Effective.FontName);
        set => Set(() => Own.FontName = FontMap.WithTraits(FontFamily, value, Italic));
    }

    public bool Italic
    {
        get => FontMap.IsItalic(Effective.FontName);
        set => Set(() => Own.FontName = FontMap.WithTraits(FontFamily, Bold, value));
    }

    public string FontSize
    {
        get => Num(Effective.FontSize);
        set { if (Parse(value) is { } v && v > 0) Set(() => Own.FontSize = Math.Clamp(v, 4, 1000)); }
    }

    public string TextColor
    {
        get => o.TextStyle?.ColorHex ?? Inherited?.ColorHex ?? theme?.TextColorHex ?? "#FFFFFFFF";
        set => Set(() => Own.ColorHex = string.IsNullOrEmpty(value) ? null : value);
    }

    public int Alignment
    {
        get => (int)Effective.Alignment;
        set => Set(() => Own.HorizontalAlignment = value switch { 0 => TextHorizontalAlignment.Left, 2 => TextHorizontalAlignment.Right, _ => TextHorizontalAlignment.Center });
    }

    public int VerticalAlignment
    {
        get => (int)Effective.VerticalAlignment;
        set => Set(() => Own.VerticalAlignment = value switch { 0 => TextVerticalAlignment.Top, 2 => TextVerticalAlignment.Bottom, _ => TextVerticalAlignment.Middle });
    }

    public string LineHeight
    {
        get => Effective.LineHeightMultiple.ToString("0.##", CultureInfo.CurrentCulture);
        set { if (Parse(value) is { } v && v > 0) Set(() => Own.LineHeightMultiple = Math.Clamp(v, 0.5, 4)); }
    }

    public string Tracking
    {
        get => Num(Effective.Tracking);
        set { if (Parse(value) is { } v) Set(() => Own.Tracking = v == 0 ? null : v); }
    }

    public bool Uppercase
    {
        get => Effective.Transform == SceneTextTransform.Uppercase;
        set => Set(() => Own.TextTransform = value ? TextTransform.Uppercase : TextTransform.None);
    }

    public bool AutoShrink
    {
        get => Effective.AutoShrink;
        set => Set(() => Own.AutoShrink = value);
    }

    public bool Shadow
    {
        get => Effective.Shadow is not null;
        set => Set(() => Own.Shadow = value ? new ObjectShadow { ColorHex = "#000000B3", BlurRadius = 12, OffsetX = 0, OffsetY = 4 } : null);
    }

    public string OutlineColor
    {
        get => o.TextStyle?.Outline?.ColorHex ?? Inherited?.Outline?.ColorHex ?? "";
        set => Set(() => Own.Outline = string.IsNullOrEmpty(value) || value == "#00000000"
            ? null
            : new ObjectStroke { ColorHex = value, Width = Own.Outline?.Width ?? Inherited?.Outline?.Width ?? 3 });
    }

    // MARK: Shapes

    public string FillColor
    {
        get => o.Fill?.FillKind switch
        {
            FillKind.Solid => o.Fill.ColorHex ?? "#FFFFFFFF",
            FillKind.LinearGradient => o.Fill.GradientStops?.FirstOrDefault()?.ColorHex ?? "#FFFFFFFF",
            FillKind.None => "#00000000",
            null => "#FFFFFFFF",
            _ => "",
        };
        set => Set(() => o.Fill = string.IsNullOrEmpty(value) || value == "#00000000"
            ? new ObjectFill { FillKind = FillKind.None }
            : new ObjectFill { FillKind = FillKind.Solid, ColorHex = value });
    }

    public string CornerRadius
    {
        get => Num(o.CornerRadius ?? 0);
        set
        {
            if (Parse(value) is not { } v) return;
            Set(() =>
            {
                o.CornerRadius = Math.Max(0, v);
                if (o.ShapeKind is null or ShapeKind.Rectangle or ShapeKind.RoundedRectangle)
                    o.ShapeKind = v > 0 ? ShapeKind.RoundedRectangle : ShapeKind.Rectangle;
            });
        }
    }

    public string StrokeColor
    {
        get => o.Stroke?.ColorHex ?? "";
        set => Set(() => o.Stroke = string.IsNullOrEmpty(value) || value == "#00000000"
            ? null
            : new ObjectStroke { ColorHex = value, Width = o.Stroke?.Width ?? 4, DashKind = o.Stroke?.DashKind });
    }

    public string StrokeWidth
    {
        get => Num(o.Stroke?.Width ?? 0);
        set
        {
            if (Parse(value) is not { } v) return;
            Set(() =>
            {
                if (v <= 0) o.Stroke = null;
                else o.Stroke = new ObjectStroke { ColorHex = o.Stroke?.ColorHex ?? "#FFFFFFFF", Width = v, DashKind = o.Stroke?.DashKind };
            }, nameof(StrokeColor));
        }
    }

    public bool IsEllipse
    {
        get => o.ShapeKind == ShapeKind.Ellipse;
        set => Set(() => o.ShapeKind = value ? ShapeKind.Ellipse : (o.CornerRadius ?? 0) > 0 ? ShapeKind.RoundedRectangle : ShapeKind.Rectangle);
    }

    // MARK: Media

    public int ScaleMode
    {
        get => (int)SlideSceneBuilder.ScaleMode(SlideObjectNormalization.Normalized(o).Fill?.MediaScaleMode);
        set => Set(() =>
        {
            var mode = value switch { 1 => MediaScaleMode.Fit, 2 => MediaScaleMode.Stretch, _ => MediaScaleMode.Fill };
            if (o.Fill is { FillKind: FillKind.Media } fill) fill.MediaScaleMode = mode;
            else o.MediaScaleMode = mode;
        });
    }

    // MARK: Mask (shown by the overlay editor's inspector)

    /// <summary>
    /// Every object of the same slide or overlay, so this one can be masked by another.
    /// Left null (as the slide editor does for now), the mask settings stay hidden.
    /// </summary>
    public IReadOnlyList<SlideObject>? Siblings { get; init; }

    /// <summary>One entry in the "Mask with" list: an object's id and name ("" = no mask).</summary>
    public sealed record MaskChoice(string Id, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>"None", then the objects this one may be masked with (no loops), as the Mac's inspector lists them.</summary>
    public IReadOnlyList<MaskChoice> MaskChoices
    {
        get
        {
            var choices = new List<MaskChoice> { new("", "None") };
            if (Siblings is null) return choices;
            choices.AddRange(ObjectMasks.Candidates(o, Siblings).Select(c => new MaskChoice(c.Id, ObjectMasks.DisplayName(c))));
            if (o.MaskObjectId is { Length: > 0 } id && choices.All(c => c.Id != id)) choices.Add(new MaskChoice(id, "Missing object"));
            return choices;
        }
    }

    public bool ShowsMask => Siblings is not null && (MaskChoices.Count > 1 || HasMask);

    public bool HasMask => !string.IsNullOrEmpty(o.MaskObjectId);

    public MaskChoice? MaskWith
    {
        get => MaskChoices.FirstOrDefault(c => c.Id == (o.MaskObjectId ?? ""));
        set => Set(() => ObjectMasks.SetMask(o, value?.Id), nameof(HasMask), nameof(MaskModeIndex));
    }

    /// <summary>0 keeps what's inside the mask object, 1 cuts it out.</summary>
    public int MaskModeIndex
    {
        get => o.MaskMode == MaskMode.Out ? 1 : 0;
        set => Set(() => o.MaskMode = value == 1 ? MaskMode.Out : MaskMode.In);
    }

    /// <summary>True when another object is masked by this one, so outputs never draw it.</summary>
    public bool IsMatte => Siblings is not null && ObjectMasks.IsMatte(o, Siblings);

    // MARK: Text source (what a text box shows)

    /// <summary>What a text box can show: what the operator types, or something the app fills in.</summary>
    public sealed record SourceChoice(TextSourceKind? Kind, string Label)
    {
        public override string ToString() => Label;
    }

    private static readonly SourceChoice[] CommonSources =
    [
        new(null, "Typed text"),
        new(TextSourceKind.Clock, "The time (clock)"),
        new(TextSourceKind.CurrentSlide, "The live slide's words"),
        new(TextSourceKind.NextSlide, "The next slide's words"),
        new(TextSourceKind.CurrentPresentation, "The song or presentation's name"),
        new(TextSourceKind.CurrentServiceItem, "The service item now"),
        new(TextSourceKind.NextServiceItem, "The next service item"),
        new(TextSourceKind.StageMessage, "The stage message"),
    ];

    public IReadOnlyList<SourceChoice> TextSources =>
        o.TextLink is { } link && CommonSources.All(c => c.Kind != link.Source)
            ? [.. CommonSources, new SourceChoice(link.Source, $"Linked: {link.Source}")]
            : CommonSources;

    public SourceChoice TextSource
    {
        get => TextSources.FirstOrDefault(c => c.Kind == o.TextLink?.Source) ?? CommonSources[0];
        set
        {
            if (value is null || value.Kind == o.TextLink?.Source) return;
            Set(() => o.TextLink = value.Kind is { } kind ? new TextLink { Source = kind } : null, nameof(IsTyped));
        }
    }

    /// <summary>True for a text box the operator types into (it appears as a field in the bin).</summary>
    public bool IsTyped => o.TextLink is null;
}
