using System.Windows;
using System.Windows.Media;
using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Windows.Rendering;

// Two ways a layer is drawn as one picture rather than item by item (MxU's latest releases):
//
// - A dissolve on a layer of words, overlays or alerts fades each stack as one picture (the Mac's
//   crossfade pass, Compositor.crossfadeStacks). The leaving stack and the arriving one each go
//   into their own container, and the two containers fade (WPF fades a container as a whole), so
//   an opaque backdrop leaving never lets what it covered show through its fading words.
// - A layer the Screen Map places (Compositor.drawPlaced) is drawn once as the room's picture, in
//   a container no one sees directly, and a brush of it lands in each frame the placement covers
//   on this screen: fitted (letterboxed), or filling and cropped to the frame.
public sealed partial class SceneView
{
    /// <summary>One layer drawn as a picture: its items go into <see cref="Container"/>.</summary>
    private sealed class LayerPicture
    {
        public ContainerVisual Container { get; } = new();
        public List<Visual> Items { get; } = [];

        /// <summary>For a placed layer: the picture drawn into its frames.</summary>
        public DrawingVisual Face { get; } = new();

        public (PlacedLayer Placed, Rect Canvas)? DrawnFace;
        public bool Used;
    }

    /// <summary>The picture for a layer (made the first time), emptied for this frame.</summary>
    private static LayerPicture BeginPicture(Root root, string key)
    {
        if (!root.Pictures.TryGetValue(key, out var picture)) root.Pictures[key] = picture = new LayerPicture();
        picture.Items.Clear();
        picture.Used = true;
        return picture;
    }

    /// <summary>
    /// Draws a placed layer's picture into its frames. The brush shows the room's canvas, so the
    /// picture's shape is the room's; Fit letterboxes it inside the frame, Fill covers the frame
    /// and the frame's edges crop it. Only redrawn when the placement or the canvas changes (the
    /// brush itself follows a playing video).
    /// </summary>
    private static void DrawPlaced(LayerPicture picture, PlacedLayer placed, Rect canvas)
    {
        if (picture.DrawnFace is { } drawn && drawn.Placed == placed && drawn.Canvas == canvas) return;
        var room = new Rect(0, 0, placed.CanvasSize.Width, placed.CanvasSize.Height);
        using (var dc = picture.Face.RenderOpen())
        {
            if (room.Width > 0 && room.Height > 0)
            {
                var brush = new VisualBrush(picture.Container) { Viewbox = room, ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
                RenderOptions.SetCachingHint(brush, CachingHint.Cache);
                foreach (var frame in placed.Frames)
                {
                    var rect = new Rect(frame.Rect.X, frame.Rect.Y, frame.Rect.Width, frame.Rect.Height);
                    if (rect.Width <= 0 || rect.Height <= 0) continue;
                    var quad = Fitted(room.Size, rect, frame.Fill);
                    dc.PushClip(new RectangleGeometry(rect));
                    dc.DrawRectangle(brush, null, quad);
                    dc.Pop();
                }
            }
        }
        picture.DrawnFace = (placed, canvas);
    }

    /// <summary>The picture's rectangle in a frame: inside it whole (fit), or covering it (fill), centred.</summary>
    internal static Rect Fitted(Size content, Rect frame, bool fill)
    {
        if (content.Width <= 0 || content.Height <= 0) return frame;
        var scaleX = frame.Width / content.Width;
        var scaleY = frame.Height / content.Height;
        var scale = fill ? Math.Max(scaleX, scaleY) : Math.Min(scaleX, scaleY);
        var width = content.Width * scale;
        var height = content.Height * scale;
        return new Rect(frame.X + (frame.Width - width) / 2, frame.Y + (frame.Height - height) / 2, width, height);
    }

    /// <summary>Lets go of the pictures no layer used this frame.</summary>
    private static void SweepPictures(Root root)
    {
        foreach (var key in root.Pictures.Where(p => !p.Value.Used).Select(p => p.Key).ToList())
        {
            // Its items may be drawn elsewhere from now on, and a visual has one parent.
            root.Pictures[key].Container.Children.Clear();
            root.Pictures.Remove(key);
        }
        foreach (var picture in root.Pictures.Values) picture.Used = false;
    }

    private static bool HasMediaLooks(AnimatedScene scene) => scene.Layers.Any(layer => layer.Items.Any(item =>
        item.Render.Content is ItemContent.Media { ScaleMode: SceneMediaScaleMode.ScaleAndBlur }
        || item.Render.Content is ItemContent.Shape { Style.Fill: SceneFill.Media { ScaleMode: SceneMediaScaleMode.ScaleAndBlur } }));

    /// <summary>
    /// Seconds left in a video this view plays (its pass), for an end fade; null when it has no
    /// player here, it loops (it never ends) or its length isn't known yet.
    /// </summary>
    private double? PlaybackRemaining(string mediaId)
    {
        if (media is null) return null;
        foreach (var (key, id) in media.ActiveVideos)
        {
            if (id != mediaId || !key.StartsWith(PlaybackScope + ":", StringComparison.Ordinal)) continue;
            if (media.PlaybackRemaining(key) is { } left) return left;
        }
        return null;
    }
}
