using System.Text.Json;
using System.Text.Json.Nodes;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Undo;

/// <summary>
/// Names a step from what it changed, so a plain save gets a sensible menu entry ("Undo Move",
/// "Undo Typing", "Undo Delete Slide") and a key that lets a burst of the same edit coalesce.
/// An editor can always name a step itself (<see cref="UndoScope.BeginGroup"/>); this is for
/// everything that just saves.
/// <para>
/// It compares each document's JSON before and after: which properties changed, and whether a
/// list of things with ids (slides, objects, service items) gained, lost or reordered some.
/// The words follow the Mac's undo labels where it has them ("Move Slide", "Remove Service Item").
/// </para>
/// </summary>
public static class UndoDescription
{
    /// <summary>A step's name and coalescing key (null: a structural change, which never coalesces).</summary>
    public readonly record struct Description(string Name, string? Key);

    public static Description Of(UndoStep step)
    {
        Description? first = null;
        var touched = new List<string>();
        foreach (var change in step.Changes)
        {
            var described = Describe(change, touched);
            first ??= described;
            if (described.Key is null) return new Description(first.Value.Name, null);
        }
        if (first is not { } found) return new Description("Edit", null);
        // Coalesce only the same kind of edit to the same things: typing in one box, then typing in
        // another, is two steps.
        return new Description(found.Name, $"{found.Name}|{string.Join(";", touched.Distinct().Order(StringComparer.Ordinal))}");
    }

    private static Description Describe(UndoChange change, List<string> touched)
    {
        var noun = Noun(change.Target);
        if (change.Before is null) return new Description($"Add {noun}", null);
        if (change.After is null) return new Description($"Delete {noun}", null);
        var before = Parse(change.Before);
        var after = Parse(change.After);
        var diff = new Diff();
        diff.Walk(before, after, change.Target.Key, owner: change.Target.Key);
        if (diff.Structure is { } structure) return new Description(structure, null);
        touched.AddRange(diff.Owners);
        return new Description(LeafName(diff, noun, change.Target), "");
    }

    private static string LeafName(Diff diff, string noun, UndoTarget target)
    {
        var changed = diff.Leaves.Select(l => l.Property).ToHashSet(StringComparer.Ordinal);
        if (changed.Count == 0) return target is UndoTarget.Extra ? $"Change {noun}" : $"Edit {noun}";
        var resized = diff.Leaves.Any(l => l.Property is "width" or "height" && l.Existed);
        if (changed.IsSubsetOf(Geometry)) return resized ? "Resize" : "Move";
        if (changed.IsSubsetOf(["rotationDegrees"])) return "Rotate";
        if (changed.Contains("text") && changed.IsSubsetOf(TextContent)) return "Typing";
        if (changed.IsSubsetOf(["chords"])) return "Chords";
        if (changed.IsSubsetOf(["styleRuns"])) return "Text Style";
        if (changed.IsSubsetOf(Naming)) return "Rename";
        if (changed.IsSubsetOf(["hidden"])) return "Show or Hide";
        if (target is UndoTarget.Extra) return $"Change {noun}";
        var words = changed.Select(Words).Distinct().ToList();
        return words.Count == 1 ? $"Change {words[0]}" : $"Edit {noun}";
    }

    private static readonly HashSet<string> Geometry = ["x", "y", "width", "height"];
    private static readonly HashSet<string> TextContent = ["text", "styleRuns", "chords"];

    // A song's title is kept in its CCLI details too.
    private static readonly HashSet<string> Naming = ["name", "songTitle"];

    // MARK: Words

    private static string Noun(UndoTarget target) => target switch
    {
        UndoTarget.Document d => d.Type.Name switch
        {
            nameof(Presentation) => "Presentation",
            nameof(MediaItem) => "Media",
            nameof(ConfidenceLayout) => "Stage Layout",
            nameof(AlertPreset) => "Alert",
            nameof(AudioItem) => "Song File",
            nameof(ActionCombo) => "Combo",
            nameof(ScheduleTrigger) => "Trigger",
            nameof(MIDIDevice) => "MIDI Device",
            nameof(ControlBoard) or nameof(SchedulerBoard) => "Board",
            var other => Split(other),
        },
        UndoTarget.Extra e => Title(e.Kind.TrimEnd('s')),
        _ => "Item",
    };

    // The lists whose elements have names on the Mac's undo menu.
    private static string ElementNoun(string listKey) => listKey switch
    {
        "slides" => "Slide",
        "items" => "Service Item",
        "objects" => "Object",
        "sections" => "Group",
        "arrangements" => "Arrangement",
        "steps" or "animationSteps" or "builds" => "Animation",
        "actions" => "Action",
        "cues" => "Cue",
        "entries" => "Entry",
        _ => Words(listKey).TrimEnd('s'),
    };

    private static readonly Dictionary<string, string> PropertyWords = new(StringComparer.Ordinal)
    {
        ["themeSlideName"] = "Design",
        ["themeId"] = "Theme",
        ["backgroundFill"] = "Background",
        ["background"] = "Background",
        ["fontName"] = "Font",
        ["tracking"] = "Letter Spacing",
        ["lineHeightMultiple"] = "Line Spacing",
        ["horizontalAlignment"] = "Alignment",
        ["verticalAlignment"] = "Alignment",
        ["autoShrink"] = "Text Fit",
        ["pageOnClick"] = "Text Fit",
        ["minFontSize"] = "Text Fit",
        ["sectionId"] = "Group",
        ["arrangementId"] = "Arrangement",
        ["rotationDegrees"] = "Rotation",
    };

    /// <summary>"colorHex" → "Color", "fontSize" → "Font Size".</summary>
    private static string Words(string property)
    {
        if (PropertyWords.TryGetValue(property, out var known)) return known;
        var trimmed = property.EndsWith("Hex", StringComparison.Ordinal) ? property[..^3]
            : property.EndsWith("Id", StringComparison.Ordinal) && property.Length > 2 ? property[..^2]
            : property;
        return Split(trimmed);
    }

    private static string Split(string camel)
    {
        var text = new System.Text.StringBuilder();
        foreach (var ch in camel)
        {
            if (char.IsUpper(ch) && text.Length > 0) text.Append(' ');
            text.Append(text.Length == 0 ? char.ToUpperInvariant(ch) : ch);
        }
        return text.ToString();
    }

    private static string Title(string words) =>
        string.Join(' ', words.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));

    private static JsonNode? Parse(byte[] json)
    {
        try { return JsonNode.Parse(json); }
        catch (JsonException) { return null; }
    }

    // MARK: Comparing

    private sealed record Leaf(string Property, bool Existed);

    /// <summary>Walks two JSON trees side by side, noting what differs.</summary>
    private sealed class Diff
    {
        public List<Leaf> Leaves { get; } = [];

        /// <summary>The things with ids whose own properties changed (for the coalescing key).</summary>
        public HashSet<string> Owners { get; } = [];

        /// <summary>A list gained, lost or reordered elements: "Add Slide", "Move Service Item".</summary>
        public string? Structure { get; private set; }

        public void Walk(JsonNode? before, JsonNode? after, string path, string owner)
        {
            if (Structure is not null || JsonNode.DeepEquals(before, after)) return;
            if (before is JsonObject b && after is JsonObject a)
            {
                foreach (var key in b.Select(p => p.Key).Union(a.Select(p => p.Key)))
                {
                    var child = $"{path}/{key}";
                    if (b[key] is JsonObject or JsonArray || a[key] is JsonObject or JsonArray)
                    {
                        if (b[key] is null || a[key] is null) Leaves.Add(new Leaf(key, b[key] is not null));
                        else Walk(b[key], a[key], child, owner);
                        if (b[key] is null || a[key] is null) Owners.Add(owner);
                    }
                    else if (!JsonNode.DeepEquals(b[key], a[key]))
                    {
                        Leaves.Add(new Leaf(key, b[key] is not null));
                        Owners.Add(owner);
                    }
                }
                return;
            }
            if (before is JsonArray bl && after is JsonArray al && Ids(bl) is { } beforeIds && Ids(al) is { } afterIds)
            {
                var listKey = path[(path.LastIndexOf('/') + 1)..];
                var noun = ElementNoun(listKey);
                var added = afterIds.Except(beforeIds).Count();
                var removed = beforeIds.Except(afterIds).Count();
                if (added > 0 && removed == 0) Structure = Plural($"Add {noun}", added);
                else if (removed > 0 && added == 0) Structure = Plural(listKey == "items" ? $"Remove {noun}" : $"Delete {noun}", removed);
                else if (added > 0) Structure = $"Edit {noun}s";
                else if (!beforeIds.SequenceEqual(afterIds)) Structure = listKey == "objects" ? "Arrange" : $"Move {noun}";
                if (Structure is not null) return;
                var byId = bl.ToDictionary(n => (string)n!["id"]!, StringComparer.Ordinal);
                foreach (var element in al)
                {
                    var id = (string)element!["id"]!;
                    Walk(byId[id], element, $"{path}/{id}", $"{path}/{id}");
                }
                return;
            }
            // Any other list, or a value that changed type, is one property.
            var property = path[(path.LastIndexOf('/') + 1)..];
            Leaves.Add(new Leaf(property, before is not null));
            Owners.Add(owner);
        }

        private static string Plural(string name, int count) => count == 1 ? name : name + "s";

        private static List<string>? Ids(JsonArray list)
        {
            var ids = new List<string>();
            foreach (var n in list)
            {
                if (n is not JsonObject o || o["id"] is not JsonValue v || !v.TryGetValue<string>(out var id)) return null;
                ids.Add(id);
            }
            return ids.Distinct().Count() == ids.Count ? ids : null;
        }
    }
}
