using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using MxU.Slides.Core.Outputs;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Rendering;

namespace MxU.Slides.Windows.Outputs;

/// <summary>
/// One slice of a screen on an output: the part of the screen's picture it shows
/// (<see cref="SourceRect"/>), drawn at full resolution, then corrected on the graphics
/// card: keystone (corner pin), colour, edge-blend ramps and masks
/// (<see cref="OutputAdjustEffect"/>).
/// <para>The scene is drawn by a <see cref="SceneView"/> sized so that only the source
/// rect lands inside this element (the rest is clipped away). The correction shader then
/// works in the slice's own coordinates, which is what the Mac's edge blends use.</para>
/// </summary>
public sealed class SliceView : Canvas
{
    private OutputAdjustEffect? effect;

    public SliceView(Guid sliceId, Guid screenId, MediaHub media, string playbackScope)
    {
        SliceId = sliceId;
        ScreenId = screenId;
        ClipToBounds = true;
        Background = Brushes.Black;
        View = new SceneView { Media = media, PlaysVideo = true, PlaybackScope = playbackScope };
        Children.Add(View);
    }

    public Guid SliceId { get; }
    public Guid ScreenId { get; }
    public SceneView View { get; }

    /// <summary>The part of the screen's canvas this slice shows, in 0–1.</summary>
    public SceneRect SourceRect
    {
        get => sourceRect;
        set
        {
            if (sourceRect == value) return;
            sourceRect = value;
            InvalidateArrange();
        }
    }

    private SceneRect sourceRect = OutputPass.FullSourceRect;

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var w = sourceRect.Width > 0 ? arrangeSize.Width / sourceRect.Width : arrangeSize.Width;
        var h = sourceRect.Height > 0 ? arrangeSize.Height / sourceRect.Height : arrangeSize.Height;
        View.Arrange(new Rect(-sourceRect.X * w, -sourceRect.Y * h, w, h));
        return arrangeSize;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        View.Measure(constraint);
        return new Size(double.IsInfinity(constraint.Width) ? 0 : constraint.Width, double.IsInfinity(constraint.Height) ? 0 : constraint.Height);
    }

    /// <summary>
    /// Sets the correction pass, or removes it when there's nothing to correct. <paramref name="pixelWidth"/>
    /// and <paramref name="pixelHeight"/> are this slice's size on its output; <paramref name="mask"/> is the
    /// screen's mask coverage (one byte per canvas pixel, white = shown) or null.
    /// </summary>
    public void SetCorrections(OutputAdjustments? adjustments, byte[]? mask, SceneSize canvas, int pixelWidth, int pixelHeight)
    {
        if (adjustments is null && mask is null || OutputAdjustEffect.Shader is null)
        {
            Effect = null;
            effect = null;
            View.Visibility = Visibility.Visible;
            return;
        }
        var a = adjustments ?? new OutputAdjustments();
        var geometry = OutputPass.Geometry(a, OutputPass.FullSourceRect, null, Math.Max(1, pixelWidth), Math.Max(1, pixelHeight));
        // A corner pin folded into a line or a bow tie has no picture to show.
        View.Visibility = geometry.Homography is null ? Visibility.Hidden : Visibility.Visible;
        if (geometry.Homography is null) return;

        effect ??= new OutputAdjustEffect();
        effect.SetWarp(geometry.Homography);
        var u = OutputPass.Uniforms(a, OutputPass.FullSourceRect, mask is not null);
        effect.Color = new Point4D(u.Brightness, u.Contrast, u.Gamma, u.BlackLevel);
        effect.RgbLevels = new Point4D(u.RedLevel, u.GreenLevel, u.BlueLevel, u.HasMask ? 1 : 0);
        effect.BlendLeft = Edge(u.BlendLeft);
        effect.BlendRight = Edge(u.BlendRight);
        effect.BlendTop = Edge(u.BlendTop);
        effect.BlendBottom = Edge(u.BlendBottom);
        effect.SourceRect = new Point4D(0, 0, 1, 1);
        effect.Mask = mask is null ? null : MaskBrush(mask, canvas);
        Effect = effect;
    }

    private static Point4D Edge(OutputEdgeUniform e) => new(e.Width, e.Curve, e.Intensity, e.BlackLift);

    /// <summary>The coverage as a brush, cropped to this slice's source rect so it lines up with the slice.</summary>
    private Brush? MaskBrush(byte[] coverage, SceneSize canvas)
    {
        int w = (int)canvas.Width, h = (int)canvas.Height;
        if (w <= 0 || h <= 0 || coverage.Length < w * h) return null;
        var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Gray8, null, coverage, w);
        bitmap.Freeze();
        var brush = new ImageBrush(bitmap)
        {
            Viewbox = new Rect(sourceRect.X, sourceRect.Y, sourceRect.Width, sourceRect.Height),
            ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
            Stretch = Stretch.Fill,
        };
        brush.Freeze();
        return brush;
    }
}
