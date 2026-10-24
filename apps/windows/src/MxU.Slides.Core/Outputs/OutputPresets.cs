using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Outputs;

/// <summary>A slide-theme look an output shows: a whole theme, one of its slide folders, or one pinned slide design.</summary>
public sealed record SlideThemeLook(string ThemeId, string? Folder, string? SlideId)
{
    /// <summary>The key the look's variant scene is built and routed under.</summary>
    public string Key => OutputPresets.LookKey(ThemeId, Folder, SlideId);
}

/// <summary>One row of a preset's routing matrix: a direct display or an audience screen.</summary>
public sealed record OutputRoutingTarget(string Id, string Name, OutputTargetKind Kind);

/// <summary>
/// What switching to a preset sets: the layers each target composites, the
/// look each target's slides go through (and the looks the app must build
/// variant scenes for), and the masks it switches on per screen.
/// </summary>
public sealed record PresetRouting(
    IReadOnlyDictionary<string, IReadOnlySet<string>> Layers,
    IReadOnlyDictionary<string, string> SlideThemes,
    IReadOnlyDictionary<string, SlideThemeLook> Looks,
    IReadOnlyDictionary<Guid, IReadOnlySet<Guid>> ActiveMasks)
{
    public static readonly PresetRouting None = new(
        new Dictionary<string, IReadOnlySet<string>>(), new Dictionary<string, string>(),
        new Dictionary<string, SlideThemeLook>(), new Dictionary<Guid, IReadOnlySet<Guid>>());
}

/// <summary>
/// Output presets: library documents (<see cref="OutputPreset"/>) that say,
/// per output, which layers it composites, which slide theme re-formats its
/// slides, and which masks it switches on. One preset is active at a time and
/// is switched globally from the Live panel; no preset means every layer
/// everywhere. Port of the Mac's OutputPresetsController.swift, as pure
/// functions: the editing ones change the preset passed in, and the app saves
/// it to the library afterwards.
/// </summary>
public static class OutputPresets
{
    /// <summary>The "Broadcast Feed" template: slide text and overlays only, for keying over a video switcher.</summary>
    public static readonly IReadOnlyList<string> BroadcastFeedLayers = [LayerKind.Slide.Raw(), LayerKind.Overlays.Raw()];

    /// <summary>
    /// A new preset. The empty one routes nothing yet (every layer everywhere);
    /// the Broadcast Feed template starts every display and screen on
    /// <see cref="BroadcastFeedLayers"/>.
    /// </summary>
    public static OutputPreset Create(OutputConfiguration outputs, bool fromBroadcastTemplate, string? id = null)
    {
        var assignments = new List<OutputAssignment>();
        if (fromBroadcastTemplate)
        {
            foreach (var display in outputs.Displays)
                assignments.Add(new OutputAssignment
                {
                    TargetKind = OutputTargetKind.Display, TargetId = display.Id, EnabledLayers = [.. BroadcastFeedLayers],
                });
            foreach (var screen in outputs.Screens)
                assignments.Add(new OutputAssignment
                {
                    TargetKind = OutputTargetKind.PlaceholderScreen, TargetId = SwiftUuid.Format(screen.Id), EnabledLayers = [.. BroadcastFeedLayers],
                });
        }
        return new OutputPreset
        {
            Id = id ?? SwiftUuid.Format(Guid.NewGuid()),
            Name = fromBroadcastTemplate ? "Broadcast Feed" : "Untitled Preset",
            Assignments = assignments,
        };
    }

    /// <summary>A copy under a new id, named "… Copy".</summary>
    public static OutputPreset Duplicate(OutputPreset preset)
    {
        var copy = MxuJson.Clone(preset);
        copy.Id = SwiftUuid.Format(Guid.NewGuid());
        copy.Name += " Copy";
        return copy;
    }

    /// <summary>Renames the preset; a blank name is ignored (returns false).</summary>
    public static bool Rename(OutputPreset preset, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) return false;
        preset.Name = trimmed;
        return true;
    }

    /// <summary>The layers a target composites under this preset; null when the preset doesn't route it (all layers).</summary>
    public static IReadOnlySet<string>? EnabledLayers(OutputPreset preset, string targetId) =>
        Assignment(preset, targetId) is { } assignment ? assignment.EnabledLayers.ToHashSet(StringComparer.Ordinal) : null;

    /// <summary>Whether a layer reaches a target: true unless the preset routes the target without it.</summary>
    public static bool IsRouted(OutputPreset? preset, string targetId, LayerKind layer) =>
        preset is null || EnabledLayers(preset, targetId) is not { } enabled || enabled.Contains(layer.Raw());

    /// <summary>Turns one layer on or off for a target, starting from "all layers" when the target wasn't routed yet.</summary>
    public static void ToggleLayer(OutputPreset preset, OutputTargetKind targetKind, string targetId, string layer)
    {
        var assignment = Assignment(preset, targetId) ?? NewAssignment(targetKind, targetId);
        var layers = assignment.EnabledLayers.ToHashSet(StringComparer.Ordinal);
        if (!layers.Remove(layer)) layers.Add(layer);
        assignment.EnabledLayers = [.. layers.Order(StringComparer.Ordinal)];
        Replace(preset, targetId, assignment);
    }

    public static string? SlideThemeId(OutputPreset preset, string targetId) =>
        Blank(Assignment(preset, targetId)?.SlideThemeId);

    public static string? SlideThemeFolder(OutputPreset preset, string targetId) =>
        Blank(Assignment(preset, targetId)?.SlideThemeFolder);

    public static string? SlideThemeSlideId(OutputPreset preset, string targetId) =>
        Blank(Assignment(preset, targetId)?.SlideThemeSlideId);

    /// <summary>
    /// Re-formats a target's slides through a theme (null = as designed), one of
    /// its slide folders, or one pinned slide. Folder and slide only count with a theme.
    /// </summary>
    public static void SetSlideTheme(
        OutputPreset preset, OutputTargetKind targetKind, string targetId,
        string? themeId, string? folder = null, string? slideId = null)
    {
        var assignment = Assignment(preset, targetId) ?? NewAssignment(targetKind, targetId);
        assignment.SlideThemeId = Blank(themeId);
        assignment.SlideThemeFolder = assignment.SlideThemeId is null ? null : Blank(folder);
        assignment.SlideThemeSlideId = assignment.SlideThemeId is null ? null : Blank(slideId);
        Replace(preset, targetId, assignment);
    }

    /// <summary>The masks this preset switches on for a target (a screen).</summary>
    public static IReadOnlySet<Guid> MaskIds(OutputPreset preset, string targetId) =>
        (Assignment(preset, targetId)?.MaskIds ?? []).Select(SwiftUuid.Parse).OfType<Guid>().ToHashSet();

    public static void ToggleMask(OutputPreset preset, OutputTargetKind targetKind, string targetId, Guid maskId)
    {
        var assignment = Assignment(preset, targetId) ?? NewAssignment(targetKind, targetId);
        var ids = (assignment.MaskIds ?? []).Select(SwiftUuid.Parse).OfType<Guid>().ToHashSet();
        if (!ids.Remove(maskId)) ids.Add(maskId);
        assignment.MaskIds = ids.Count == 0 ? null : [.. ids.Select(SwiftUuid.Format).Order(StringComparer.Ordinal)];
        Replace(preset, targetId, assignment);
    }

    /// <summary>Forgets everything the preset says about a target ("All Layers On").</summary>
    public static void ClearAssignment(OutputPreset preset, string targetId) =>
        preset.Assignments.RemoveAll(assignment => assignment.TargetId == targetId);

    /// <summary>The distinct looks the preset uses, in assignment order: the variant scenes the app must keep ready.</summary>
    public static IReadOnlyList<SlideThemeLook> SlideThemeLooks(OutputPreset preset)
    {
        var looks = new List<SlideThemeLook>();
        foreach (var assignment in preset.Assignments)
        {
            if (Blank(assignment.SlideThemeId) is not { } themeId) continue;
            var look = new SlideThemeLook(themeId, Blank(assignment.SlideThemeFolder), Blank(assignment.SlideThemeSlideId));
            if (!looks.Contains(look)) looks.Add(look);
        }
        return looks;
    }

    /// <summary>"theme", "theme#folder", "theme##slide" or "theme#folder##slide".</summary>
    public static string LookKey(string themeId, string? folder, string? slideId)
    {
        var key = themeId;
        if (!string.IsNullOrEmpty(folder)) key += $"#{folder}";
        if (!string.IsNullOrEmpty(slideId)) key += $"##{slideId}";
        return key;
    }

    /// <summary>
    /// What the preset routes (<see cref="PresetRouting.None"/> for no preset).
    /// A look whose theme isn't in the library (<paramref name="themeExists"/>
    /// says no) is left out, so that target shows the slides as designed.
    /// Screen ids are matched however they are cased.
    /// </summary>
    public static PresetRouting Routing(OutputPreset? preset, Func<string, bool>? themeExists = null)
    {
        if (preset is null) return PresetRouting.None;
        var layers = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        foreach (var assignment in preset.Assignments)
            layers[RoutingKey(assignment.TargetId)] = assignment.EnabledLayers.ToHashSet(StringComparer.Ordinal);

        var themes = new Dictionary<string, string>(StringComparer.Ordinal);
        var looks = new Dictionary<string, SlideThemeLook>(StringComparer.Ordinal);
        foreach (var assignment in preset.Assignments)
        {
            if (Blank(assignment.SlideThemeId) is not { } themeId) continue;
            var look = new SlideThemeLook(themeId, Blank(assignment.SlideThemeFolder), Blank(assignment.SlideThemeSlideId));
            if (!looks.ContainsKey(look.Key))
            {
                if (themeExists is not null && !themeExists(themeId)) continue;
                looks[look.Key] = look;
            }
            themes[RoutingKey(assignment.TargetId)] = look.Key;
        }

        var masks = new Dictionary<Guid, IReadOnlySet<Guid>>();
        foreach (var assignment in preset.Assignments)
        {
            if (assignment.MaskIds is not { Count: > 0 } ids || SwiftUuid.Parse(assignment.TargetId) is not { } screenId) continue;
            var set = masks.TryGetValue(screenId, out var existing) ? existing.ToHashSet() : [];
            set.UnionWith(ids.Select(SwiftUuid.Parse).OfType<Guid>());
            masks[screenId] = set;
        }
        return new PresetRouting(layers, themes, looks, masks);
    }

    /// <summary>
    /// Makes a preset (or none) active on the outputs: sets the layer and theme
    /// routing and each screen's preset-activated masks. Returns the routing so
    /// the app can build the looks' variant scenes.
    /// </summary>
    public static PresetRouting Apply(OutputConfiguration outputs, OutputPreset? preset, Func<string, bool>? themeExists = null)
    {
        var routing = Routing(preset, themeExists);
        outputs.SetLayerRouting(routing.Layers);
        outputs.SetSlideThemeRouting(routing.SlideThemes);
        foreach (var screen in outputs.Screens)
            outputs.SetActiveMaskIds(screen.Id, routing.ActiveMasks.GetValueOrDefault(screen.Id) ?? new HashSet<Guid>());
        return routing;
    }

    /// <summary>The routing matrix's rows: every connected display, then every audience screen (confidence screens show their own layout).</summary>
    public static IReadOnlyList<OutputRoutingTarget> Targets(OutputConfiguration outputs) =>
    [
        .. outputs.Displays.Select(display => new OutputRoutingTarget(display.Id, display.Name, OutputTargetKind.Display)),
        .. outputs.Screens
            .Where(screen => outputs.Role(screen.Id) != ScreenRole.Confidence)
            .Select(screen => new OutputRoutingTarget(SwiftUuid.Format(screen.Id), screen.Name, OutputTargetKind.PlaceholderScreen)),
    ];

    /// <summary>The masks a preset can switch on for a target: a screen's masks that aren't always on.</summary>
    public static IReadOnlyList<OutputMask> SelectableMasks(OutputConfiguration outputs, OutputRoutingTarget target) =>
        target.Kind == OutputTargetKind.PlaceholderScreen && SwiftUuid.Parse(target.Id) is { } screenId
            ? outputs.Masks(screenId).Where(mask => !mask.AlwaysOn).ToList()
            : [];

    private static OutputAssignment? Assignment(OutputPreset preset, string targetId) =>
        preset.Assignments.FirstOrDefault(assignment => assignment.TargetId == targetId);

    // A target the preset didn't mention yet starts with every layer on.
    private static OutputAssignment NewAssignment(OutputTargetKind kind, string targetId) => new()
    {
        TargetKind = kind,
        TargetId = targetId,
        EnabledLayers = [.. LayerKinds.All.Select(layer => layer.Raw()).Order(StringComparer.Ordinal)],
    };

    // An edited assignment moves to the end of the list, as on the Mac.
    private static void Replace(OutputPreset preset, string targetId, OutputAssignment assignment)
    {
        preset.Assignments.RemoveAll(existing => existing.TargetId == targetId);
        preset.Assignments.Add(assignment);
    }

    // Screen ids route under their uppercase form, which is how outputs look them up.
    private static string RoutingKey(string targetId) =>
        SwiftUuid.Parse(targetId) is { } id ? SwiftUuid.Format(id) : targetId;

    private static string? Blank(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
