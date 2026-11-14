using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Scene;
using LayerTransition = MxU.Slides.Core.Animation.SceneTransition;

namespace MxU.Slides.Core.Outputs;

/// <summary>
/// Whether a screen is a key feed for a video switcher, and over which colour.
/// A switcher's keyer makes one colour see-through and lays the rest over the
/// camera: black for a luma key, green for a chroma key.
/// </summary>
public enum KeyFeedMode
{
    /// <summary>An ordinary screen: every layer, over the slide's own background.</summary>
    Off,

    /// <summary>Words and overlays over black, for a luma key.</summary>
    Black,

    /// <summary>Words and overlays over pure green, for a chroma key.</summary>
    Green,
}

/// <summary>
/// The rules of a key feed, as pure functions: which layers it keeps, the colour
/// behind them, and which pieces of the Slide layer are backgrounds to drop.
/// Windows only. The Mac gets a similar picture from an output preset made with
/// its "Broadcast Feed" template (<see cref="OutputPresets.BroadcastFeedLayers"/>),
/// but that keeps a slide's backdrop and is always over black.
/// </summary>
public static class KeyFeeds
{
    /// <summary>
    /// The layers a key feed keeps: the slide's words and objects, overlays (lower thirds,
    /// tickers) and alerts. Only an alert aimed at the audience is ever in the program scene,
    /// and one that matters enough to interrupt the room should reach the stream too.
    /// Everything else is a media layer: backgrounds, videos and live inputs, which are the
    /// camera's job on a key feed.
    /// </summary>
    public static readonly IReadOnlyList<LayerKind> Layers = [LayerKind.Slide, LayerKind.Overlays, LayerKind.Alerts];

    /// <summary>Pure green: red 0, green 255, blue 0.</summary>
    public static readonly SceneColor Green = new(0, 1, 0);

    public static string? Raw(this KeyFeedMode mode) => mode switch
    {
        KeyFeedMode.Black => "black",
        KeyFeedMode.Green => "green",
        _ => null,
    };

    /// <summary>The saved word back to a mode; anything unknown (or nothing) is Off.</summary>
    public static KeyFeedMode Parse(string? raw) => raw switch
    {
        "black" => KeyFeedMode.Black,
        "green" => KeyFeedMode.Green,
        _ => KeyFeedMode.Off,
    };

    public static string DisplayName(this KeyFeedMode mode) => mode switch
    {
        KeyFeedMode.Black => "Over black (for a luma key)",
        KeyFeedMode.Green => "Over green (for a chroma key)",
        _ => "Off",
    };

    /// <summary>The colour everywhere nothing is drawn; null when the screen isn't a key feed.</summary>
    public static SceneColor? Fill(KeyFeedMode mode) => mode switch
    {
        KeyFeedMode.Black => SceneColor.Black,
        KeyFeedMode.Green => Green,
        _ => null,
    };

    /// <summary>Whether a key feed draws a layer. A custom layer (the Screen Map's) holds fired media, so it doesn't.</summary>
    public static bool Shows(LayerKind? layer) => layer is { } kind && Layers.Contains(kind);

    /// <summary>
    /// Whether an item on the Slide layer is a background rather than words. Two kinds are:
    /// the slide's or song's own background fill (the builder names it "…-backdrop"), and any
    /// shape or picture that covers the whole canvas, which is how a theme design or an
    /// imported PowerPoint slide carries its background. Text never is; nor is a matte (it is
    /// never drawn), a piece clipped by one, or a shape with no fill (a border).
    /// </summary>
    public static bool IsBackground(RenderItem item, SceneSize canvas)
    {
        if (item.Content is ItemContent.Text) return false;
        if (item.Id.EndsWith("-backdrop", StringComparison.Ordinal)) return true;
        if (item.MatteGroup is not null || item.MaskedBy is not null) return false;
        if (item.Content is ItemContent.Shape { Style.Fill: SceneFill.NoFill }) return false;
        // Within 1% of each edge counts: designs are often drawn a pixel or two short.
        var dx = canvas.Width * 0.01;
        var dy = canvas.Height * 0.01;
        var frame = item.Frame;
        return frame.X <= dx && frame.Y <= dy
            && frame.X + frame.Width >= canvas.Width - dx && frame.Y + frame.Height >= canvas.Height - dy;
    }

    /// <summary>
    /// The scene as a key feed shows it: a copy over the key colour, with the media layers
    /// hidden and the Slide layer's backgrounds taken out. Off returns the scene untouched.
    /// Safe to apply twice.
    /// </summary>
    public static AnimatedScene Apply(AnimatedScene scene, KeyFeedMode mode)
    {
        if (Fill(mode) is not { } fill) return scene;
        var keyed = scene.Copy();
        keyed.Background = fill;
        foreach (var layer in keyed.Layers)
        {
            if (!Shows(layer.Kind)) layer.IsHidden = true;
            else if (layer.Kind == LayerKind.Slide) layer.Items.RemoveAll(item => IsBackground(item.Render, scene.CanvasSize));
        }
        return keyed;
    }

    /// <summary>As <see cref="Apply(AnimatedScene, KeyFeedMode)"/>, for a still scene (the previews): layers it doesn't show are left empty.</summary>
    public static RenderScene Apply(RenderScene scene, KeyFeedMode mode)
    {
        if (Fill(mode) is not { } fill) return scene;
        var keyed = new RenderScene(scene.CanvasSize, fill);
        string? below = null;
        foreach (var layer in scene.Layers)
        {
            var into = keyed.Matching(layer.Id, layer.Kind, layer.Name, below);
            below = layer.Id;
            if (!Shows(layer.Kind)) continue;
            into.Placed = layer.Placed;
            into.Items.AddRange(layer.Kind == LayerKind.Slide ? layer.Items.Where(item => !IsBackground(item, scene.CanvasSize)) : layer.Items);
        }
        return keyed;
    }

    /// <summary>
    /// The transition a key feed plays in place of <paramref name="transition"/>. A cut or a
    /// dissolve only fades the words, so they stay. The others put a colour or an effect across
    /// the whole picture (a fade through white, a film burn, a wipe), which the switcher would
    /// lay over the camera as a solid flash: they become a dissolve of the same length.
    /// </summary>
    public static LayerTransition? Transition(LayerTransition? transition) =>
        transition is null or { Kind: SceneTransitionKind.Cut or SceneTransitionKind.Dissolve }
            ? transition
            : new LayerTransition(SceneTransitionKind.Dissolve, transition.Duration);
}
