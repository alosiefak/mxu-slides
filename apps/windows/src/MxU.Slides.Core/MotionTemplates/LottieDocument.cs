using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MxU.Slides.Core.MotionTemplates;

/// <summary>A Lottie file that can't be used: not JSON, or JSON that isn't an animation. The message says why.</summary>
public sealed class LottieFormatException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A named moment in the animation (an After Effects composition marker): where it starts and how long it lasts, in frames.</summary>
public sealed record LottieMarker(string Name, double Frame, double DurationFrames);

/// <summary>One font the animation's text asks for: its PostScript-style name, family and style ("Montserrat-Bold", "Montserrat", "Bold").</summary>
public sealed record LottieFont(string Name, string Family, string Style, string? Path);

/// <summary>
/// One layer, wherever it is: <see cref="Path"/> says where ("L3" is the fourth layer of the main
/// composition; "A:comp_0/L1" the second layer inside the precomposition asset "comp_0").
/// <see cref="Type"/> is Lottie's number: 0 precomp, 1 solid, 2 image, 3 null, 4 shape, 5 text.
/// </summary>
public sealed record LottieLayer(string Path, string Name, int Type, double InPoint, double OutPoint, string? RefId)
{
    public const int Precomp = 0, Solid = 1, Image = 2, Null = 3, Shape = 4, Text = 5;
}

/// <summary>
/// A Lottie animation (the JSON that After Effects' Bodymovin plug-in, or LottieFiles, exports),
/// read into a tree we can look into and change. It knows the few things the app needs:
/// <list type="bullet">
/// <item>the picture size, frame rate and length (<c>w</c>, <c>h</c>, <c>fr</c>, <c>ip</c>, <c>op</c>);</item>
/// <item>the markers (After Effects composition markers, which templates use for In, Hold and Out);</item>
/// <item>every layer, including those inside precompositions, and the fonts its text asks for.</item>
/// </list>
/// Everything else in the file is kept exactly as it was, so the drawing (Skia's Skottie, in the
/// MotionTemplates project) sees the animation as its author made it. <see cref="MotionFields"/>
/// finds what can be edited and makes changed copies.
/// <para>Windows only: the Mac has no Lottie support.</para>
/// </summary>
public sealed class LottieDocument
{
    /// <summary>Lottie files bigger than this (with their pictures inside) aren't templates; they're refused rather than slowing the show.</summary>
    public const int MaxBytes = 64 << 20;

    /// <summary>The largest picture side accepted (8K): bigger is almost certainly a broken file.</summary>
    public const int MaxSide = 8192;

    private static readonly JsonDocumentOptions ReadOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 256 };

    private LottieDocument(JsonObject root)
    {
        Root = root;
        Width = Number(root["w"]) ?? 0;
        Height = Number(root["h"]) ?? 0;
        FrameRate = Number(root["fr"]) ?? 0;
        InPoint = Number(root["ip"]) ?? 0;
        OutPoint = Number(root["op"]) ?? 0;
        Name = Text(root["nm"]) ?? "";
        Version = Text(root["v"]) ?? "";
        Markers = ReadMarkers(root["markers"] as JsonArray);
        Layers = ReadLayers(root);
        Fonts = ReadFonts(root["fonts"]?["list"] as JsonArray);
    }

    /// <summary>The whole animation as a JSON tree. Change it only through <see cref="MotionFields.Apply"/> (which works on a copy).</summary>
    public JsonObject Root { get; }

    public double Width { get; }

    public double Height { get; }

    /// <summary>Frames a second, as authored (Lottie's <c>fr</c>).</summary>
    public double FrameRate { get; }

    /// <summary>The first frame (<c>ip</c>).</summary>
    public double InPoint { get; }

    /// <summary>The frame after the last (<c>op</c>): the animation shows frames <see cref="InPoint"/> up to, but not including, this.</summary>
    public double OutPoint { get; }

    /// <summary>The composition's name, as After Effects called it.</summary>
    public string Name { get; }

    /// <summary>The Bodymovin version that wrote it ("5.7.4").</summary>
    public string Version { get; }

    public double Frames => OutPoint - InPoint;

    public double Seconds => Frames / FrameRate;

    public IReadOnlyList<LottieMarker> Markers { get; }

    public IReadOnlyList<LottieLayer> Layers { get; }

    public IReadOnlyList<LottieFont> Fonts { get; }

    /// <summary>Reads a Lottie animation. Throws <see cref="LottieFormatException"/> (saying why) if it isn't one.</summary>
    public static LottieDocument Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length > MaxBytes) throw new LottieFormatException($"The file is {utf8.Length / (1 << 20)} MB; a motion template can be at most {MaxBytes >> 20} MB.");
        // Some editors start the file with a byte-order mark, which isn't JSON.
        if (utf8.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) utf8 = utf8[3..];
        JsonNode? node;
        try
        {
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 256 });
            node = JsonNode.Parse(ref reader);
        }
        catch (JsonException e)
        {
            throw new LottieFormatException($"It isn't readable JSON: {e.Message}", e);
        }
        return From(node);
    }

    public static LottieDocument Parse(string json) => Parse(System.Text.Encoding.UTF8.GetBytes(json));

    private static LottieDocument From(JsonNode? node)
    {
        if (node is not JsonObject root) throw new LottieFormatException("It isn't a Lottie animation (the file isn't a JSON object).");
        if (root["layers"] is not JsonArray) throw new LottieFormatException("It isn't a Lottie animation (it has no layers).");
        var document = new LottieDocument(root);
        if (document.Width is <= 0 or > MaxSide || document.Height is <= 0 or > MaxSide)
            throw new LottieFormatException($"Its picture size ({document.Width} × {document.Height}) isn't usable.");
        if (document.FrameRate is <= 0 or > 240) throw new LottieFormatException($"Its frame rate ({document.FrameRate}) isn't usable.");
        if (document.OutPoint <= document.InPoint) throw new LottieFormatException("It has no frames (its out point isn't after its in point).");
        return document;
    }

    /// <summary>A deep copy, to change without touching this one.</summary>
    public LottieDocument Clone() => From(Root.DeepClone());

    /// <summary>The animation as compact UTF-8 JSON.</summary>
    public byte[] ToUtf8() => JsonSerializer.SerializeToUtf8Bytes(Root, CompactOptions);

    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        WriteIndented = false,
        // Text in the animation (names, scripture) stays readable in the file rather than \u-escaped.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The layer objects, with their paths: the main composition's, then each precomposition asset's.</summary>
    public IEnumerable<(string Path, JsonObject Layer)> LayerNodes()
    {
        if (Root["layers"] is JsonArray main)
            for (var i = 0; i < main.Count; i++)
                if (main[i] is JsonObject layer) yield return ($"L{i}", layer);
        if (Root["assets"] is not JsonArray assets) yield break;
        foreach (var asset in assets.OfType<JsonObject>())
        {
            if (asset["layers"] is not JsonArray layers) continue;
            var id = Text(asset["id"]) ?? "";
            for (var i = 0; i < layers.Count; i++)
                if (layers[i] is JsonObject layer) yield return ($"A:{id}/L{i}", layer);
        }
    }

    /// <summary>The image assets (pictures the animation draws): their id, size, and where the picture is.</summary>
    public IEnumerable<JsonObject> ImageAssets() =>
        (Root["assets"] as JsonArray)?.OfType<JsonObject>().Where(a => a["layers"] is null && a["p"] is JsonValue && a["w"] is not null) ?? [];

    private IReadOnlyList<LottieLayer> ReadLayers(JsonObject root) =>
        [.. LayerNodes().Select(entry => new LottieLayer(entry.Path, Text(entry.Layer["nm"]) ?? "", (int)(Number(entry.Layer["ty"]) ?? -1),
            Number(entry.Layer["ip"]) ?? InPoint, Number(entry.Layer["op"]) ?? OutPoint, Text(entry.Layer["refId"])))];

    private static IReadOnlyList<LottieMarker> ReadMarkers(JsonArray? markers)
    {
        if (markers is null) return [];
        var list = new List<LottieMarker>();
        foreach (var marker in markers.OfType<JsonObject>())
        {
            if (Number(marker["tm"]) is not { } frame) continue;
            list.Add(new LottieMarker(MarkerName(Text(marker["cm"]) ?? ""), frame, Math.Max(0, Number(marker["dr"]) ?? 0)));
        }
        return [.. list.OrderBy(m => m.Frame)];
    }

    /// <summary>
    /// A marker's name. After Effects keeps it in the comment; LottieFiles' tools sometimes write
    /// the comment as a little JSON object (<c>{"name":"intro"}</c>), so that's read too.
    /// </summary>
    internal static string MarkerName(string comment)
    {
        var trimmed = comment.Trim();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                if (JsonNode.Parse(trimmed) is JsonObject o && (Text(o["name"]) ?? Text(o["n"])) is { } name) return name.Trim();
            }
            catch (JsonException) { }
        }
        return trimmed;
    }

    private static IReadOnlyList<LottieFont> ReadFonts(JsonArray? list) =>
        list is null ? [] : [.. list.OfType<JsonObject>().Select(f => new LottieFont(
            Text(f["fName"]) ?? "", Text(f["fFamily"]) ?? Text(f["fName"]) ?? "", Text(f["fStyle"]) ?? "Regular", Text(f["fPath"])))];

    // MARK: Reading values (Lottie writes numbers as ints or doubles, and sometimes as one-element arrays)

    internal static double? Number(JsonNode? node)
    {
        switch (node)
        {
            case JsonValue value when value.TryGetValue<double>(out var d):
                return double.IsFinite(d) ? d : null;
            case JsonValue value when value.TryGetValue<string>(out var s)
                                       && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                return parsed;
            case JsonArray { Count: > 0 } array:
                return Number(array[0]);
            default:
                return null;
        }
    }

    internal static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;
}
