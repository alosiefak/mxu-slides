using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Notes;

/// <summary>An output preset or action combo a made slide fires (a "switch"), by id.</summary>
public sealed record SlidePresetChoice(SlidePresetChoice.Kinds Kind, string Id)
{
    public enum Kinds { OutputPreset, ActionCombo }

    public static SlidePresetChoice Preset(string id) => new(Kinds.OutputPreset, id);

    public static SlidePresetChoice Combo(string id) => new(Kinds.ActionCombo, id);

    /// <summary>The slide action that fires it, with a fresh id.</summary>
    public SlideAction Action() => Kind == Kinds.OutputPreset
        ? new SlideAction { Id = Lyrics.SwiftString.Uuid(), Kind = SlideActionKind.SwitchOutputPreset, PresetId = Id }
        : new SlideAction { Id = Lyrics.SwiftString.Uuid(), Kind = SlideActionKind.FireCombo, ComboId = Id };

    public bool Matches(SlideAction action) => Kind == Kinds.OutputPreset
        ? action.Kind == SlideActionKind.SwitchOutputPreset && action.PresetId == Id
        : action.Kind == SlideActionKind.FireCombo && action.ComboId == Id;

    internal JsonObject ToJson() => new() { ["id"] = Id, ["kind"] = Kind == Kinds.OutputPreset ? "outputPreset" : "actionCombo" };

    internal static SlidePresetChoice? FromJson(JsonNode? node)
    {
        if (node is not JsonObject o || o["id"]?.GetValue<string>() is not { } id) return null;
        return (string?)o["kind"] switch
        {
            "outputPreset" => Preset(id),
            "actionCombo" => Combo(id),
            _ => null,
        };
    }
}

/// <summary>Where blank slides go among the made ones.</summary>
public enum BlankPlacement { None, AfterPassage, AfterSlide }

/// <summary>How Make Slides cuts and shows each kind of passage. The Mac's SlideOptions, saved inside the design map.</summary>
public sealed record SlideOptions
{
    public enum ScriptureSplits { FitToBox, OneVerse }

    public enum VideoEnds { Next, Stop, Loop, FadeToBlack, FadeToClear }

    /// <summary>A church's own slide type ("Announcement"), with how its words are cut.</summary>
    public sealed record OwnType(string Name, OwnType.Splits Split = OwnType.Splits.FitToBox, bool Reveal = false)
    {
        public enum Splits { FitToBox, Paragraph, Line }

        public static string Title(Splits split) => split switch
        {
            Splits.FitToBox => "Fit to box",
            Splits.Paragraph => "One paragraph per slide",
            _ => "One line per slide",
        };
    }

    public bool RevealPoints { get; init; }
    public ScriptureSplits ScriptureSplit { get; init; } = ScriptureSplits.FitToBox;
    public bool VerseNumbers { get; init; } = true;
    public bool BlanksBetweenVerses { get; init; }
    public bool KeepUnderlines { get; init; } = true;
    public bool ParenthesesToNotes { get; init; } = true;
    public IReadOnlyList<OwnType> OwnTypes { get; init; } = [];
    public MediaScaleMode PictureScale { get; init; } = MediaScaleMode.ScaleAndBlur;
    public VideoEnds VideoEnd { get; init; } = VideoEnds.Next;
    public bool PicturesTogether { get; init; }

    public static string Title(VideoEnds end) => end switch
    {
        VideoEnds.Next => "Go to next slide",
        VideoEnds.Stop => "Stop",
        VideoEnds.Loop => "Loop",
        VideoEnds.FadeToBlack => "Fade to black",
        _ => "Fade to clear",
    };

    public OwnType? OwnTypeNamed(string? name) =>
        name is null ? null : OwnTypes.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    public bool Equals(SlideOptions? other) =>
        other is not null && RevealPoints == other.RevealPoints && ScriptureSplit == other.ScriptureSplit
        && VerseNumbers == other.VerseNumbers && BlanksBetweenVerses == other.BlanksBetweenVerses
        && KeepUnderlines == other.KeepUnderlines && ParenthesesToNotes == other.ParenthesesToNotes
        && OwnTypes.SequenceEqual(other.OwnTypes) && PictureScale == other.PictureScale && VideoEnd == other.VideoEnd
        && PicturesTogether == other.PicturesTogether;

    public override int GetHashCode() => HashCode.Combine(RevealPoints, ScriptureSplit, VerseNumbers, OwnTypes.Count, PictureScale, VideoEnd);

    // The Mac's raw values, as its Codable enums write them.
    internal static string Raw(ScriptureSplits value) => value == ScriptureSplits.OneVerse ? "oneVerse" : "fitToBox";

    internal static string Raw(VideoEnds value) => value switch
    {
        VideoEnds.Stop => "stop", VideoEnds.Loop => "loop", VideoEnds.FadeToBlack => "fadeToBlack",
        VideoEnds.FadeToClear => "fadeToClear", _ => "next",
    };

    internal static string Raw(OwnType.Splits value) => value switch
    {
        OwnType.Splits.Paragraph => "paragraph", OwnType.Splits.Line => "line", _ => "fitToBox",
    };

    internal JsonObject ToJson() => new()
    {
        ["blanksBetweenVerses"] = BlanksBetweenVerses,
        ["keepUnderlines"] = KeepUnderlines,
        ["ownTypes"] = new JsonArray(OwnTypes.Select(t => (JsonNode)new JsonObject
        {
            ["name"] = t.Name, ["reveal"] = t.Reveal, ["split"] = Raw(t.Split),
        }).ToArray()),
        ["parenthesesToNotes"] = ParenthesesToNotes,
        ["pictureScale"] = MediaScaleModeJsonConverter.ToRaw(PictureScale),
        ["picturesTogether"] = PicturesTogether,
        ["revealPoints"] = RevealPoints,
        ["scriptureSplit"] = Raw(ScriptureSplit),
        ["verseNumbers"] = VerseNumbers,
        ["videoEnd"] = Raw(VideoEnd),
    };

    /// <summary>
    /// Read like the Mac's decoder: a missing key keeps its default, and so does an enum value
    /// this version doesn't know (a newer Mac's), rather than losing the whole map.
    /// </summary>
    internal static SlideOptions FromJson(JsonNode? node)
    {
        var defaults = new SlideOptions();
        if (node is not JsonObject o) return defaults;
        bool Bool(string key, bool fallback) => o[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;
        string? Text(string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        var ownTypes = (o["ownTypes"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(t => t["name"] is JsonValue n && n.TryGetValue<string>(out _))
            .Select(t => new OwnType(
                t["name"]!.GetValue<string>(),
                ((string?)(t["split"] as JsonValue)) switch { "paragraph" => OwnType.Splits.Paragraph, "line" => OwnType.Splits.Line, _ => OwnType.Splits.FitToBox },
                t["reveal"] is JsonValue r && r.TryGetValue<bool>(out var reveal) && reveal))
            .ToList();
        return new SlideOptions
        {
            RevealPoints = Bool("revealPoints", defaults.RevealPoints),
            ScriptureSplit = Text("scriptureSplit") == "oneVerse" ? ScriptureSplits.OneVerse : defaults.ScriptureSplit,
            VerseNumbers = Bool("verseNumbers", defaults.VerseNumbers),
            BlanksBetweenVerses = Bool("blanksBetweenVerses", defaults.BlanksBetweenVerses),
            KeepUnderlines = Bool("keepUnderlines", defaults.KeepUnderlines),
            ParenthesesToNotes = Bool("parenthesesToNotes", defaults.ParenthesesToNotes),
            OwnTypes = ownTypes,
            PictureScale = MediaScaleModeJsonConverter.TryParse(Text("pictureScale"), out var scale) ? scale : defaults.PictureScale,
            VideoEnd = Text("videoEnd") switch
            {
                "next" => VideoEnds.Next, "stop" => VideoEnds.Stop, "loop" => VideoEnds.Loop,
                "fadeToBlack" => VideoEnds.FadeToBlack, "fadeToClear" => VideoEnds.FadeToClear, _ => defaults.VideoEnd,
            },
            PicturesTogether = Bool("picturesTogether", defaults.PicturesTogether),
        };
    }
}

/// <summary>
/// Which theme design each kind of slide gets, which switch it fires, where blanks go, and the
/// options. Saved as a JSON string in the shared SlideBuildingSettings document, so it is read and
/// written exactly as the Mac's SlideDesignMap Codable does. Port of SlideDesignMap.swift.
/// </summary>
public sealed class SlideDesignMap : IEquatable<SlideDesignMap>
{
    public sealed record Entry(string ThemeId, string ThemeSlideName);

    /// <summary>By slide type: a kind's raw value ("points") or an own type's name.</summary>
    public Dictionary<string, Entry> Entries { get; set; } = [];

    public Dictionary<string, SlidePresetChoice> Presets { get; set; } = [];

    /// <summary>The switch for every type that has none of its own.</summary>
    public SlidePresetChoice? OtherPreset { get; set; }

    public BlankPlacement Blanks { get; set; } = BlankPlacement.None;

    public SlideOptions Options { get; set; } = new();

    public SlideDesignMap Copy() => new()
    {
        Entries = new(Entries), Presets = new(Presets), OtherPreset = OtherPreset, Blanks = Blanks, Options = Options,
    };

    public Entry? EntryFor(SlideKind kind) => EntryForType(kind.Raw());

    public void Set(Entry? entry, SlideKind kind) => SetForType(entry, kind.Raw());

    public SlidePresetChoice? PresetFor(SlideKind kind) => PresetForType(kind.Raw());

    public SlidePresetChoice? OwnPresetFor(SlideKind kind) => OwnPresetForType(kind.Raw());

    public void SetPreset(SlidePresetChoice? preset, SlideKind kind) => SetPresetForType(preset, kind.Raw());

    public Entry? EntryForType(string type) => Entries.GetValueOrDefault(type);

    public void SetForType(Entry? entry, string type)
    {
        if (entry is null) Entries.Remove(type);
        else Entries[type] = entry;
    }

    public SlidePresetChoice? PresetForType(string type) => Presets.GetValueOrDefault(type) ?? OtherPreset;

    public SlidePresetChoice? OwnPresetForType(string type) => Presets.GetValueOrDefault(type);

    public void SetPresetForType(SlidePresetChoice? preset, string type)
    {
        if (preset is null) Presets.Remove(type);
        else Presets[type] = preset;
    }

    public bool CanAddOwnType(string name)
    {
        var trimmed = Lyrics.SwiftString.TrimmingWhitespaces(name);
        return trimmed.Length > 0 && SlideKinds.Parse(trimmed.ToLowerInvariant()) is null && Options.OwnTypeNamed(trimmed) is null;
    }

    public void RenameOwnType(string old, string @new)
    {
        var types = Options.OwnTypes.ToList();
        var index = types.FindIndex(t => t.Name == old);
        if (index < 0) return;
        types[index] = types[index] with { Name = @new };
        Options = Options with { OwnTypes = types };
        if (Entries.Remove(old, out var entry)) Entries[@new] = entry;
        else Entries.Remove(@new);
        if (Presets.Remove(old, out var preset)) Presets[@new] = preset;
        else Presets.Remove(@new);
    }

    public void RemoveOwnType(string name)
    {
        Options = Options with { OwnTypes = Options.OwnTypes.Where(t => t.Name != name).ToList() };
        Entries.Remove(name);
        Presets.Remove(name);
    }

    /// <summary>The kinds Settings lists, in its order.</summary>
    public static readonly IReadOnlyList<SlideKind> Kinds =
        [SlideKind.Title, SlideKind.Points, SlideKind.Scripture, SlideKind.Quote, SlideKind.Lyrics, SlideKind.Picture, SlideKind.Video, SlideKind.Blank];

    /// <summary>The map saved in a document, or an empty one when there is none or it can't be read.</summary>
    public static SlideDesignMap Decode(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new SlideDesignMap();
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return new SlideDesignMap();
        }
        if (root is null) return new SlideDesignMap();
        var map = new SlideDesignMap();
        try
        {
            foreach (var (type, value) in root["entries"] as JsonObject ?? [])
            {
                if (value is JsonObject e && (string?)e["themeId"] is { } themeId && (string?)e["themeSlideName"] is { } name)
                    map.Entries[type] = new Entry(themeId, name);
            }
            foreach (var (type, value) in root["presets"] as JsonObject ?? [])
            {
                if (SlidePresetChoice.FromJson(value) is { } choice) map.Presets[type] = choice;
            }
            map.OtherPreset = SlidePresetChoice.FromJson(root["otherPreset"]);
            map.Blanks = (string?)(root["blanks"] as JsonValue) switch
            {
                "afterPassage" => BlankPlacement.AfterPassage,
                "afterSlide" => BlankPlacement.AfterSlide,
                _ => BlankPlacement.None,
            };
            map.Options = SlideOptions.FromJson(root["options"]);
            // An older Mac kept the blank slides' switch on its own ("blankPreset").
            if (!map.Presets.ContainsKey(SlideKind.Blank.Raw()) && SlidePresetChoice.FromJson(root["blankPreset"]) is { } old)
                map.Presets[SlideKind.Blank.Raw()] = old;
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            return new SlideDesignMap();
        }
        return map;
    }

    /// <summary>The map as the Mac saves it into the document: compact JSON with sorted keys.</summary>
    public string Encoded()
    {
        var root = new JsonObject
        {
            ["blanks"] = Blanks switch { BlankPlacement.AfterPassage => "afterPassage", BlankPlacement.AfterSlide => "afterSlide", _ => "none" },
            ["entries"] = Sorted(Entries.ToDictionary(e => e.Key, e => (JsonNode)new JsonObject
            {
                ["themeId"] = e.Value.ThemeId, ["themeSlideName"] = e.Value.ThemeSlideName,
            })),
            ["options"] = Options.ToJson(),
        };
        if (OtherPreset is { } other) root["otherPreset"] = other.ToJson();
        root["presets"] = Sorted(Presets.ToDictionary(p => p.Key, p => (JsonNode)p.Value.ToJson()));
        return SortedText(root);
    }

    private static JsonObject Sorted(Dictionary<string, JsonNode> values)
    {
        var o = new JsonObject();
        foreach (var key in values.Keys.Order(StringComparer.Ordinal)) o[key] = values[key];
        return o;
    }

    // Swift's .sortedKeys sorts every object's keys by their UTF-8 bytes; so does this.
    private static string SortedText(JsonNode node)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            Write(writer, node);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void Write(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                writer.WriteStartObject();
                foreach (var (key, value) in o.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(key);
                    Write(writer, value);
                }
                writer.WriteEndObject();
                break;
            case JsonArray a:
                writer.WriteStartArray();
                foreach (var item in a) Write(writer, item);
                writer.WriteEndArray();
                break;
            case null:
                writer.WriteNullValue();
                break;
            default:
                node.WriteTo(writer);
                break;
        }
    }

    public bool Equals(SlideDesignMap? other) => other is not null && Encoded() == other.Encoded();

    public override bool Equals(object? obj) => Equals(obj as SlideDesignMap);

    public override int GetHashCode() => Encoded().GetHashCode(StringComparison.Ordinal);
}
