using System.Globalization;
using System.Text.Json.Nodes;

namespace MxU.Slides.Core.MotionTemplates;

/// <summary>
/// A few building blocks for writing Lottie animations in code: the starter templates
/// (<see cref="StarterMotionTemplates"/>) and the tests' samples. Lottie is just JSON, and the
/// parts used here are the ones every player supports:
/// <list type="bullet">
/// <item>shape layers (<c>"ty": 4</c>) holding groups of rectangles and ellipses with fills;</item>
/// <item>text layers (<c>"ty": 5</c>) with one text document, laid out in a box that shrinks long
/// words to fit (<c>"rs": 2</c>, a Skottie extension that other players simply ignore);</item>
/// <item>a transform per layer (position, scale, opacity) whose values can be keyframed with
/// easing curves, and rectangular masks for "rising from behind an edge" reveals;</item>
/// <item>composition markers, which the app reads as In, Hold and Out (<see cref="MotionTimeline"/>).</item>
/// </list>
/// Windows only: the Mac has no Lottie support.
/// </summary>
public sealed class LottieBuilder(string name, double width, double height, double frameRate, double frames)
{
    private readonly JsonArray layers = [];
    private readonly JsonArray markers = [];
    private readonly JsonArray fonts = [];
    private int nextIndex = 1;

    /// <summary>An easing curve between two keyframes: the out-handle of the first and the in-handle of the next (CSS cubic-bezier order).</summary>
    public readonly record struct Ease(double X1, double Y1, double X2, double Y2)
    {
        /// <summary>Fast start, long gentle settle: for things arriving.</summary>
        public static readonly Ease Out = new(0.16, 1, 0.3, 1);

        /// <summary>Gentle start, quick finish: for things leaving.</summary>
        public static readonly Ease In = new(0.7, 0, 0.84, 0);

        public static readonly Ease InOut = new(0.65, 0, 0.35, 1);

        public static readonly Ease Linear = new(0, 0, 1, 1);
    }

    /// <summary>One keyframe: at <paramref name="Frame"/> the value is <paramref name="Values"/>, easing towards the next with <paramref name="Curve"/>.</summary>
    public readonly record struct Key(double Frame, double[] Values, Ease Curve);

    public static Key At(double frame, Ease curve, params double[] values) => new(frame, values, curve);

    public LottieBuilder Font(string fontName, string family, string style)
    {
        fonts.Add(new JsonObject { ["fName"] = fontName, ["fFamily"] = family, ["fStyle"] = style, ["ascent"] = 72 });
        return this;
    }

    public LottieBuilder Marker(string markerName, double frame, double duration)
    {
        markers.Add(new JsonObject { ["cm"] = markerName, ["tm"] = frame, ["dr"] = duration });
        return this;
    }

    /// <summary>Adds a layer. Lottie draws its first layer on top, so add them top first.</summary>
    public LottieBuilder Layer(JsonObject layer)
    {
        layer["ind"] = nextIndex++;
        layers.Add(layer);
        return this;
    }

    public JsonObject Build() => new()
    {
        ["v"] = "5.7.4",
        ["fr"] = frameRate,
        ["ip"] = 0,
        ["op"] = frames,
        ["w"] = width,
        ["h"] = height,
        ["nm"] = name,
        ["ddd"] = 0,
        ["assets"] = new JsonArray(),
        ["fonts"] = new JsonObject { ["list"] = fonts.DeepClone() },
        ["markers"] = markers.DeepClone(),
        ["layers"] = layers.DeepClone(),
    };

    public LottieDocument Document() => LottieDocument.Parse(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(Build()));

    // MARK: Layers

    /// <summary>A shape layer drawing <paramref name="shapes"/>, placed by <paramref name="transform"/>.</summary>
    public JsonObject ShapeLayer(string layerName, JsonObject transform, params JsonObject[] shapes) => new()
    {
        ["ddd"] = 0, ["ty"] = LottieLayer.Shape, ["nm"] = layerName, ["sr"] = 1, ["ao"] = 0, ["bm"] = 0,
        ["ip"] = 0, ["op"] = frames, ["st"] = 0, ["ks"] = transform,
        ["shapes"] = new JsonArray([.. shapes.Select(s => (JsonNode)s)]),
    };

    /// <summary>A full-frame solid colour layer.</summary>
    public JsonObject SolidLayer(string layerName, string hex) => new()
    {
        ["ddd"] = 0, ["ty"] = LottieLayer.Solid, ["nm"] = layerName, ["sr"] = 1, ["ao"] = 0, ["bm"] = 0,
        ["ip"] = 0, ["op"] = frames, ["st"] = 0, ["sc"] = hex.ToLowerInvariant(), ["sw"] = width, ["sh"] = height,
        ["ks"] = Transform(Static(0, 0, 0), opacity: Static(100)),
    };

    /// <summary>
    /// A text layer: <paramref name="text"/> in a box of <paramref name="boxWidth"/> × <paramref name="boxHeight"/>
    /// whose top-left is at the layer's position. Long text shrinks to fit the box. <paramref name="justify"/>:
    /// 0 left, 1 right, 2 centre.
    /// </summary>
    public JsonObject TextLayer(string layerName, JsonObject transform, string text, string fontName, double size, string hex,
        double boxWidth, double boxHeight, int justify = 0, double tracking = 0, double? lineHeight = null) => new()
    {
        ["ddd"] = 0, ["ty"] = LottieLayer.Text, ["nm"] = layerName, ["sr"] = 1, ["ao"] = 0, ["bm"] = 0,
        ["ip"] = 0, ["op"] = frames, ["st"] = 0, ["ks"] = transform,
        ["t"] = new JsonObject
        {
            ["d"] = new JsonObject
            {
                ["k"] = new JsonArray(new JsonObject
                {
                    ["s"] = new JsonObject
                    {
                        ["s"] = size, ["f"] = fontName, ["t"] = text, ["j"] = justify, ["tr"] = tracking,
                        ["lh"] = lineHeight ?? Math.Round(size * 1.2, 2), ["ls"] = 0, ["fc"] = Rgb(hex),
                        ["sz"] = new JsonArray(boxWidth, boxHeight), ["ps"] = new JsonArray(0, 0), ["rs"] = 2,
                    },
                    ["t"] = 0,
                }),
            },
            ["p"] = new JsonObject(),
            ["m"] = new JsonObject { ["g"] = 1, ["a"] = Static(0, 0) },
            ["a"] = new JsonArray(),
        },
    };

    /// <summary>Clips a layer to a rectangle (in the layer's own coordinates), so it can rise from behind an invisible edge.</summary>
    public static JsonObject Masked(JsonObject layer, double x, double y, double w, double h)
    {
        layer["hasMask"] = true;
        layer["masksProperties"] = new JsonArray(new JsonObject
        {
            ["inv"] = false, ["mode"] = "a", ["nm"] = "Edge",
            ["pt"] = new JsonObject
            {
                ["a"] = 0,
                ["k"] = new JsonObject
                {
                    ["i"] = new JsonArray(Pair(0, 0), Pair(0, 0), Pair(0, 0), Pair(0, 0)),
                    ["o"] = new JsonArray(Pair(0, 0), Pair(0, 0), Pair(0, 0), Pair(0, 0)),
                    ["v"] = new JsonArray(Pair(x, y), Pair(x + w, y), Pair(x + w, y + h), Pair(x, y + h)),
                    ["c"] = true,
                },
            },
            ["o"] = Static(100),
            ["x"] = Static(0),
        });
        return layer;
    }

    // MARK: Shapes

    public static JsonObject Group(string groupName, params JsonObject[] items)
    {
        var all = new JsonArray([.. items.Select(i => (JsonNode)i)]);
        all.Add(new JsonObject
        {
            ["ty"] = "tr", ["p"] = Static(0, 0), ["a"] = Static(0, 0), ["s"] = Static(100, 100), ["r"] = Static(0), ["o"] = Static(100),
            ["sk"] = Static(0), ["sa"] = Static(0),
        });
        return new JsonObject { ["ty"] = "gr", ["nm"] = groupName, ["it"] = all };
    }

    /// <summary>A rectangle centred on (<paramref name="cx"/>, <paramref name="cy"/>).</summary>
    public static JsonObject Rect(double cx, double cy, double w, double h, double radius = 0) => new()
    {
        ["ty"] = "rc", ["nm"] = "Rectangle", ["d"] = 1, ["s"] = Static(w, h), ["p"] = Static(cx, cy), ["r"] = Static(radius),
    };

    public static JsonObject Ellipse(double cx, double cy, double w, double h) => new()
    {
        ["ty"] = "el", ["nm"] = "Ellipse", ["d"] = 1, ["s"] = Static(w, h), ["p"] = Static(cx, cy),
    };

    public static JsonObject Fill(string hex, double opacity = 100) => new()
    {
        ["ty"] = "fl", ["nm"] = "Fill", ["c"] = Static([.. Rgb(hex).Select(n => n!.GetValue<double>()), 1]), ["o"] = Static(opacity), ["r"] = 1,
    };

    /// <summary>A radial glow: <paramref name="hex"/> at the centre fading to nothing at <paramref name="radius"/>.</summary>
    public static JsonObject Glow(string hex, double cx, double cy, double radius, double centreOpacity = 1)
    {
        var rgb = Rgb(hex).Select(n => n!.GetValue<double>()).ToArray();
        return new JsonObject
        {
            ["ty"] = "gf", ["nm"] = "Glow", ["o"] = Static(100), ["r"] = 1, ["t"] = 2,
            ["s"] = Static(cx, cy), ["e"] = Static(cx + radius, cy), ["h"] = Static(0), ["a"] = Static(0),
            ["g"] = new JsonObject
            {
                ["p"] = 2,
                ["k"] = Static(0, rgb[0], rgb[1], rgb[2], 1, rgb[0], rgb[1], rgb[2], 0, centreOpacity, 1, 0),
            },
        };
    }

    // MARK: Transforms and values

    public static JsonObject Transform(JsonObject position, JsonObject? scale = null, JsonObject? opacity = null, JsonObject? anchor = null,
        JsonObject? rotation = null) => new()
    {
        ["o"] = opacity ?? Static(100),
        ["r"] = rotation ?? Static(0),
        ["p"] = position,
        ["a"] = anchor ?? Static(0, 0, 0),
        ["s"] = scale ?? Static(100, 100, 100),
    };

    public static JsonObject Static(params double[] values) => new() { ["a"] = 0, ["k"] = values.Length == 1 ? values[0] : Numbers(values) };

    /// <summary>A keyframed value. The last key's curve is unused (nothing follows it).</summary>
    public static JsonObject Animated(params Key[] keys)
    {
        var list = new JsonArray();
        for (var i = 0; i < keys.Length; i++)
        {
            var key = new JsonObject { ["t"] = keys[i].Frame, ["s"] = Numbers(keys[i].Values) };
            if (i < keys.Length - 1)
            {
                var curve = keys[i].Curve;
                key["o"] = new JsonObject { ["x"] = new JsonArray(curve.X1), ["y"] = new JsonArray(curve.Y1) };
                key["i"] = new JsonObject { ["x"] = new JsonArray(curve.X2), ["y"] = new JsonArray(curve.Y2) };
            }
            list.Add(key);
        }
        return new JsonObject { ["a"] = 1, ["k"] = list };
    }

    private static JsonArray Numbers(double[] values) => new([.. values.Select(v => (JsonNode)JsonValue.Create(Math.Round(v, 4)))]);

    private static JsonArray Pair(double x, double y) => new(x, y);

    /// <summary>"#RRGGBB" as Lottie's [r, g, b] from 0 to 1.</summary>
    public static JsonArray Rgb(string hex)
    {
        var value = int.Parse((MotionFields.NormalHex(hex) ?? "#FFFFFF").AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        return Numbers([((value >> 16) & 0xFF) / 255.0, ((value >> 8) & 0xFF) / 255.0, (value & 0xFF) / 255.0]);
    }
}
