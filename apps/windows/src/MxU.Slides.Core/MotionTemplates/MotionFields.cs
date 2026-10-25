using System.Globalization;
using System.Text.Json.Nodes;

namespace MxU.Slides.Core.MotionTemplates;

/// <summary>What a template field changes.</summary>
public enum MotionFieldKind
{
    /// <summary>The words of a text layer.</summary>
    Text,

    /// <summary>One colour, wherever the animation uses it (fills, strokes, text, solids, effects).</summary>
    Color,

    /// <summary>A picture the animation draws (a logo, a photo).</summary>
    Image,
}

/// <summary>
/// One thing an operator can change in a template: its id (stable for a given animation, so a
/// use's saved values always find their field), what it changes, the label it's shown with, and
/// the value the animation was made with. <see cref="Where"/> says where it's used, for a tooltip.
/// </summary>
public sealed record MotionField(string Id, MotionFieldKind Kind, string Label, string Default, string Where);

/// <summary>
/// The editable parts of a Lottie animation, found and changed. A Lottie file has no list of
/// "fields" (After Effects' Essential Graphics don't survive the export), so the template's fields
/// are worked out from what's in it:
/// <list type="bullet">
/// <item><b>Text</b>: every text layer (Lottie keeps the words in its "text document"). Its label is
/// the layer's name, so a layer called "Speaker Name" becomes the field "Speaker Name".</item>
/// <item><b>Colours</b>: every distinct colour, however many shapes use it (gradient stops too). Designers build a
/// lower third from a few brand colours, and changing "the blue" should change it everywhere,
/// the way LottieFiles' own colour editor works.</item>
/// <item><b>Pictures</b>: every image the animation draws.</item>
/// </list>
/// <see cref="Apply"/> makes a changed copy; the original is never touched. Values are text:
/// the words, "#RRGGBB" for a colour, and for a picture whatever the caller's image lookup
/// understands (the app uses library media ids). A value that's missing or the same as the
/// default leaves that part as it was.
/// <para>Windows only: the Mac has no Lottie support.</para>
/// </summary>
public static class MotionFields
{
    public const string TextPrefix = "text:";
    public const string ColorPrefix = "color:";
    public const string ImagePrefix = "image:";

    /// <summary>Every field of <paramref name="document"/>: text first (in layer order), then colours (most used first), then pictures.</summary>
    public static IReadOnlyList<MotionField> Discover(LottieDocument document)
    {
        var fields = new List<MotionField>();
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (path, layer) in document.LayerNodes())
        {
            if (LottieDocument.Number(layer["ty"]) != LottieLayer.Text || TextKeyframes(layer).FirstOrDefault() is not { } first) continue;
            var name = LottieDocument.Text(layer["nm"]) is { Length: > 0 } n ? n : "Text";
            fields.Add(new MotionField(TextPrefix + path, MotionFieldKind.Text, Unique(name, labels), Readable(LottieDocument.Text(first["t"]) ?? ""), name));
        }

        // Colours, grouped by value: the order is how often each is used, then first appearance.
        var colours = ColourSlots(document)
            .Select((slot, index) => (Slot: slot, Index: index, Hex: slot.Read()))
            .Where(c => c.Hex is not null)
            .GroupBy(c => c.Hex!)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Min(c => c.Index))
            .ToList();
        foreach (var group in colours)
        {
            // Named after the layers that use it ("Plate colour", "Title & Accent colour"): designers name
            // their layers for what they are.
            var places = group.Select(c => c.Slot.Where).Distinct().ToList();
            var where = places.Count == 1 ? places[0] : $"{places[0]} and {places.Count - 1} more";
            var layers = group.Select(c => c.Slot.Layer).Distinct().ToList();
            var label = layers.Count switch
            {
                1 => $"{layers[0]} colour",
                2 => $"{layers[0]} & {layers[1]} colour",
                _ => $"{layers[0]} colour (+{layers.Count - 1})",
            };
            fields.Add(new MotionField(ColorPrefix + group.Key, MotionFieldKind.Color, Unique(label, labels), group.Key, where));
        }

        var picture = 0;
        foreach (var asset in document.ImageAssets())
        {
            picture++;
            var id = LottieDocument.Text(asset["id"]) ?? $"image_{picture}";
            var user = document.Layers.FirstOrDefault(l => l.Type == LottieLayer.Image && l.RefId == id)?.Name;
            var label = Unique(user is { Length: > 0 } ? user : $"Picture {picture}", labels);
            fields.Add(new MotionField(ImagePrefix + id, MotionFieldKind.Image, label, "", user ?? id));
        }
        return fields;
    }

    /// <summary>
    /// A copy of <paramref name="document"/> with <paramref name="values"/> put in. Pictures are
    /// looked up with <paramref name="imageUri"/>, which turns a value into a <c>data:</c> URI (or
    /// null to leave the picture as it was).
    /// </summary>
    public static LottieDocument Apply(LottieDocument document, IReadOnlyDictionary<string, string>? values, Func<string, string?>? imageUri = null)
    {
        if (values is null || values.Count == 0) return document;
        var copy = document.Clone();
        var defaults = Discover(document).ToDictionary(f => f.Id);
        var changed = false;

        foreach (var (path, layer) in copy.LayerNodes())
        {
            if (!values.TryGetValue(TextPrefix + path, out var text) || !defaults.TryGetValue(TextPrefix + path, out var field) || text == field.Default) continue;
            // After Effects ends lines with a carriage return; the field shows ordinary new lines.
            var stored = text.Replace("\r\n", "\n").Replace('\n', '\r');
            foreach (var keyframe in TextKeyframes(layer)) keyframe["t"] = stored;
            changed = true;
        }

        var recolour = values.Where(v => v.Key.StartsWith(ColorPrefix, StringComparison.Ordinal) && defaults.ContainsKey(v.Key))
            .Select(v => (From: v.Key[ColorPrefix.Length..], To: NormalHex(v.Value)))
            .Where(c => c.To is not null && c.To != c.From)
            .ToDictionary(c => c.From, c => c.To!, StringComparer.OrdinalIgnoreCase);
        if (recolour.Count > 0)
            foreach (var slot in ColourSlots(copy))
                if (slot.Read() is { } hex && recolour.TryGetValue(hex, out var to))
                {
                    slot.Write(to);
                    changed = true;
                }

        if (imageUri is not null)
            foreach (var asset in copy.ImageAssets())
            {
                var id = ImagePrefix + (LottieDocument.Text(asset["id"]) ?? "");
                if (!values.TryGetValue(id, out var value) || value.Length == 0 || imageUri(value) is not { } uri) continue;
                asset["u"] = "";
                asset["p"] = uri;
                asset["e"] = 1;
                changed = true;
            }
        return changed ? LottieDocument.Parse(copy.ToUtf8()) : document;
    }

    /// <summary>What each field shows in <paramref name="document"/> now (the tests read a changed copy back with it).</summary>
    public static Dictionary<string, string> CurrentValues(LottieDocument document) =>
        Discover(document).ToDictionary(f => f.Id, f => f.Default);

    /// <summary>"#RRGGBB" (upper case) from "#rgb", "#rrggbb" or "#rrggbbaa" (the alpha is dropped: a field changes the colour, not its opacity); null when unreadable.</summary>
    public static string? NormalHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var digits = hex.Trim().TrimStart('#');
        if (digits.Length == 3) digits = string.Concat(digits.Select(c => $"{c}{c}"));
        if (digits.Length == 8) digits = digits[..6];
        return digits.Length == 6 && int.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _)
            ? "#" + digits.ToUpperInvariant()
            : null;
    }

    // MARK: Text

    /// <summary>A text layer's text documents, one per keyframe (most have one). Each is the <c>s</c> object, holding the words in <c>t</c>.</summary>
    internal static IEnumerable<JsonObject> TextKeyframes(JsonObject layer) =>
        (layer["t"]?["d"]?["k"] as JsonArray)?.OfType<JsonObject>().Select(k => k["s"]).OfType<JsonObject>() ?? [];

    /// <summary>The words as an operator types them: After Effects' carriage returns (and its odd ETX line breaks) become new lines.</summary>
    private static string Readable(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\u0003', '\n');

    private static string Unique(string label, HashSet<string> taken)
    {
        var candidate = label;
        for (var i = 2; !taken.Add(candidate); i++) candidate = $"{label} {i}";
        return candidate;
    }

    // MARK: Colours

    /// <summary>One place a colour is stored (in <paramref name="Layer"/>, as its <paramref name="Part"/>: fill, stroke…), readable and writable as "#RRGGBB".</summary>
    private sealed record ColourSlot(string Layer, string Part, Func<string?> Read, Action<string> Write)
    {
        public string Where => $"{Layer} ({Part})";
    }

    /// <summary>Every place the animation stores a colour, in file order.</summary>
    private static IEnumerable<ColourSlot> ColourSlots(LottieDocument document)
    {
        foreach (var (_, layer) in document.LayerNodes())
        {
            var name = LottieDocument.Text(layer["nm"]) is { Length: > 0 } n ? n : "Layer";
            var type = LottieDocument.Number(layer["ty"]);
            if (type == LottieLayer.Solid && layer["sc"] is JsonValue)
                yield return new ColourSlot(name, "solid", () => NormalHex(LottieDocument.Text(layer["sc"])), hex => layer["sc"] = hex.ToLowerInvariant());
            if (type == LottieLayer.Text)
                foreach (var keyframe in TextKeyframes(layer))
                {
                    if (keyframe["fc"] is JsonArray fill) yield return ArraySlot(name, "text", fill);
                    if (keyframe["sc"] is JsonArray stroke) yield return ArraySlot(name, "text outline", stroke);
                }
            if (layer["shapes"] is JsonArray shapes)
                foreach (var slot in ShapeSlots(shapes, name)) yield return slot;
            if (layer["ef"] is JsonArray effects)
                foreach (var effect in effects.OfType<JsonObject>())
                    foreach (var parameter in (effect["ef"] as JsonArray)?.OfType<JsonObject>() ?? [])
                        if (LottieDocument.Number(parameter["ty"]) == 2 && parameter["v"] is JsonObject value)
                            foreach (var array in PropertyArrays(value))
                                yield return ArraySlot(name, LottieDocument.Text(effect["nm"]) ?? "effect", array);
        }
    }

    private static IEnumerable<ColourSlot> ShapeSlots(JsonArray items, string layerName)
    {
        foreach (var item in items.OfType<JsonObject>())
        {
            switch (LottieDocument.Text(item["ty"]))
            {
                case "gr" when item["it"] is JsonArray children:
                    foreach (var slot in ShapeSlots(children, layerName)) yield return slot;
                    break;
                case "fl" when item["c"] is JsonObject colour:
                    foreach (var array in PropertyArrays(colour)) yield return ArraySlot(layerName, "fill", array);
                    break;
                case "st" when item["c"] is JsonObject colour:
                    foreach (var array in PropertyArrays(colour)) yield return ArraySlot(layerName, "stroke", array);
                    break;
                case "gf" or "gs" when item["g"] is JsonObject gradient && gradient["k"] is JsonObject stops:
                    // A gradient keeps its colour stops as [position, r, g, b] × count, then its opacity stops.
                    var count = (int)(LottieDocument.Number(gradient["p"]) ?? 0);
                    foreach (var array in PropertyArrays(stops))
                        for (var stop = 0; stop < count && stop * 4 + 3 < array.Count; stop++)
                            yield return ArraySlot(layerName, "gradient", array, stop * 4 + 1);
                    break;
            }
        }
    }

    /// <summary>
    /// The value arrays of an animatable property: one when it's still (<c>"a":0, "k":[r,g,b,a]</c>),
    /// one per keyframe when it moves (each keyframe's start <c>s</c>, and the older files' end <c>e</c>).
    /// </summary>
    private static IEnumerable<JsonArray> PropertyArrays(JsonObject property)
    {
        if (property["k"] is not JsonArray k || k.Count == 0) yield break;
        if (k[0] is JsonValue)
        {
            yield return k;
            yield break;
        }
        foreach (var keyframe in k.OfType<JsonObject>())
        {
            if (keyframe["s"] is JsonArray start) yield return start;
            if (keyframe["e"] is JsonArray end) yield return end;
        }
    }

    /// <summary>
    /// A colour stored as [r, g, b] or [r, g, b, a] (starting at <paramref name="offset"/> in the array,
    /// for a gradient's stops): 0 to 1 in most files, 0 to 255 in some old ones.
    /// </summary>
    private static ColourSlot ArraySlot(string layer, string part, JsonArray array, int offset = 0)
    {
        bool Bytes() => array.Skip(offset).Take(3).Any(c => LottieDocument.Number(c) > 1.0001);
        return new ColourSlot(layer, part,
            () =>
            {
                if (array.Count < offset + 3) return null;
                var scale = Bytes() ? 1.0 : 255.0;
                var parts = array.Skip(offset).Take(3).Select(c => LottieDocument.Number(c)).ToList();
                if (parts.Any(p => p is null)) return null;
                return "#" + string.Concat(parts.Select(p => ((int)Math.Round(Math.Clamp(p!.Value * scale, 0, 255))).ToString("X2", CultureInfo.InvariantCulture)));
            },
            hex =>
            {
                var bytes = Bytes();
                var value = int.Parse(hex.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
                double[] rgb = [(value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF];
                for (var i = 0; i < 3; i++) array[offset + i] = bytes ? rgb[i] : Math.Round(rgb[i] / 255.0, 4);
            });
    }
}
