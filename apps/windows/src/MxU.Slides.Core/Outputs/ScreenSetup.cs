using System.Text.Json;
using System.Text.Json.Serialization;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Outputs;

// The saved form of the screen setup: one entry per screen with its settings
// and its extra slices. Port of PlaceholderScreenConfig / OutputSliceConfig in
// the Mac's Sources/RenderContext.swift, which keeps this array as JSON in
// UserDefaults ("outputs.placeholderScreens"). Field names and encodings are
// the Mac's, and every attribute needed for that is on the types themselves,
// so the array can sit inside the WPF app's settings.json as it is.

/// <summary>One saved screen.</summary>
public sealed class PlaceholderScreenConfig
{
    [JsonPropertyName("id"), JsonRequired, JsonConverter(typeof(SwiftUuidJsonConverter))]
    public Guid Id { get; set; }

    [JsonPropertyName("name"), JsonRequired]
    public string Name { get; set; } = "";

    [JsonPropertyName("width"), JsonRequired]
    public int Width { get; set; }

    [JsonPropertyName("height"), JsonRequired]
    public int Height { get; set; }

    [JsonPropertyName("framesPerSecond"), JsonRequired]
    public int FramesPerSecond { get; set; } = 30;

    /// <summary>The device carrying the default slice (an <see cref="OutputDevice"/> id). The Mac's name for it is deviceDisplayUUID.</summary>
    [JsonPropertyName("deviceDisplayUUID"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Device { get; set; }

    [JsonPropertyName("deviceDisplayName"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeviceName { get; set; }

    /// <summary>"confidence", or null for an audience screen.</summary>
    [JsonPropertyName("role"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Role { get; set; }

    /// <summary>"fill", or null for Fit.</summary>
    [JsonPropertyName("scaleMode"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ScaleMode { get; set; }

    [JsonPropertyName("adjustments"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OutputAdjustments? Adjustments { get; set; }

    [JsonPropertyName("videoDelayFrames"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? VideoDelayFrames { get; set; }

    [JsonPropertyName("masks"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<OutputMask>? Masks { get; set; }

    /// <summary>The extra slices; null when the screen has only its default one.</summary>
    [JsonPropertyName("slices"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<OutputSliceConfig>? Slices { get; set; }

    /// <summary>The default slice's placement in its device's frame.</summary>
    [JsonPropertyName("placement"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OutputPlacement? Placement { get; set; }

    // Windows only: the screen's own look. The names are the Mac's output-preset names for the
    // same thing (OutputAssignment.slideThemeId/Folder/SlideId); the Mac ignores them when it reads this.

    /// <summary>The theme this screen shows the live slide through, or null for the slide as designed.</summary>
    [JsonPropertyName("slideThemeId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SlideThemeId { get; set; }

    /// <summary>Only that theme's designs in this folder (a lower-thirds folder, say).</summary>
    [JsonPropertyName("slideThemeFolder"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SlideThemeFolder { get; set; }

    /// <summary>Only this one design of the theme.</summary>
    [JsonPropertyName("slideThemeSlideId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SlideThemeSlideId { get; set; }
}

/// <summary>One saved extra slice: its region of the canvas (unit coordinates) and its own settings.</summary>
public sealed class OutputSliceConfig
{
    [JsonPropertyName("id"), JsonRequired, JsonConverter(typeof(SwiftUuidJsonConverter))]
    public Guid Id { get; set; }

    [JsonPropertyName("name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("x"), JsonRequired]
    public double X { get; set; }

    [JsonPropertyName("y"), JsonRequired]
    public double Y { get; set; }

    [JsonPropertyName("width"), JsonRequired]
    public double Width { get; set; }

    [JsonPropertyName("height"), JsonRequired]
    public double Height { get; set; }

    [JsonPropertyName("deviceDisplayUUID"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Device { get; set; }

    [JsonPropertyName("deviceDisplayName"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeviceName { get; set; }

    [JsonPropertyName("adjustments"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OutputAdjustments? Adjustments { get; set; }

    [JsonPropertyName("videoDelayFrames"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? VideoDelayFrames { get; set; }

    [JsonPropertyName("placement"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OutputPlacement? Placement { get; set; }
}

/// <summary>
/// Saving and restoring the screen setup. Typical use in the app:
/// <code>
/// var outputs = ScreenSetup.Load(settings.Screens ?? ScreenSetup.FirstRun(...));
/// outputs.ConfigurationChanged += () => { settings.Screens = ScreenSetup.Capture(outputs); settings.Save(); };
/// </code>
/// Runtime state (live direct displays, preset-activated masks, test pictures,
/// routing) is not part of the setup, as on the Mac.
/// </summary>
public static class ScreenSetup
{
    /// <summary>The setup as saved data, one entry per screen in order.</summary>
    public static List<PlaceholderScreenConfig> Capture(OutputConfiguration outputs) =>
        outputs.Screens.Select(screen => new PlaceholderScreenConfig
        {
            Id = screen.Id,
            Name = screen.Name,
            Width = screen.Width,
            Height = screen.Height,
            FramesPerSecond = screen.FramesPerSecond,
            Device = outputs.Device(screen.Id),
            DeviceName = outputs.DeviceName(screen.Id),
            Role = outputs.Roles.TryGetValue(screen.Id, out var role) ? role.Raw() : null,
            ScaleMode = outputs.ScaleModes.TryGetValue(screen.Id, out var mode) ? mode.Raw() : null,
            Adjustments = outputs.StoredAdjustments.GetValueOrDefault(screen.Id),
            VideoDelayFrames = outputs.VideoDelays.TryGetValue(screen.Id, out var delay) ? delay : null,
            Masks = outputs.Masks(screen.Id) is { Count: > 0 } library ? [.. library] : null,
            Slices = outputs.ExtraSlices(screen.Id) is { Count: > 0 } extras
                ? extras.Select(slice => new OutputSliceConfig
                {
                    Id = slice.Id,
                    Name = slice.Name,
                    X = slice.SourceRect.X,
                    Y = slice.SourceRect.Y,
                    Width = slice.SourceRect.Width,
                    Height = slice.SourceRect.Height,
                    Device = outputs.Device(slice.Id),
                    DeviceName = outputs.DeviceName(slice.Id),
                    Adjustments = outputs.StoredAdjustments.GetValueOrDefault(slice.Id),
                    VideoDelayFrames = outputs.VideoDelays.TryGetValue(slice.Id, out var sliceDelay) ? sliceDelay : null,
                    Placement = outputs.Placement(slice.Id),
                }).ToList()
                : null,
            Placement = outputs.Placement(screen.Id),
            SlideThemeId = outputs.ScreenLook(screen.Id)?.ThemeId,
            SlideThemeFolder = outputs.ScreenLook(screen.Id)?.Folder,
            SlideThemeSlideId = outputs.ScreenLook(screen.Id)?.SlideId,
        }).ToList();

    /// <summary>
    /// A new configuration holding the saved setup. Nothing is subscribed yet,
    /// so restoring doesn't trigger a save.
    /// </summary>
    public static OutputConfiguration Load(IEnumerable<PlaceholderScreenConfig> configs)
    {
        var outputs = new OutputConfiguration();
        Restore(outputs, configs);
        return outputs;
    }

    /// <summary>
    /// Adds the saved screens to a configuration, in the Mac's order: the screen,
    /// its settings and device, then each slice with its own. Unknown role or
    /// scale-mode strings are ignored. A screen with an invalid size is skipped
    /// with everything under it.
    /// </summary>
    public static void Restore(OutputConfiguration outputs, IEnumerable<PlaceholderScreenConfig> configs)
    {
        foreach (var config in configs)
        {
            if (outputs.AddScreen(config.Name, config.Width, config.Height, config.FramesPerSecond, config.Id) is null) continue;
            if (ScreenRoles.Parse(config.Role) is { } role) outputs.SetRole(config.Id, role);
            if (OutputScaleModes.Parse(config.ScaleMode) is { } mode) outputs.SetScaleMode(config.Id, mode);
            if (config.Adjustments is { } adjustments) outputs.SetAdjustments(config.Id, adjustments);
            if (config.Masks is { } masks) outputs.SetMasks(config.Id, masks);
            if (config.Placement is { } placement) outputs.SetPlacement(config.Id, placement);
            if (config.VideoDelayFrames is { } delay) outputs.SetVideoDelay(config.Id, delay);
            if (config.SlideThemeId is { Length: > 0 } themeId)
                outputs.SetScreenLook(config.Id, new SlideThemeLook(themeId, config.SlideThemeFolder, config.SlideThemeSlideId));
            // Restored claims were all valid when saved, so restore them alongside each other: a frame
            // packed with several outputs comes back packed. (The Mac restores exclusively, which
            // keeps only the last output on a shared device.)
            if (config.Device is { } device) outputs.AssignDevice(config.Id, device, config.DeviceName, exclusive: false);
            foreach (var slice in config.Slices ?? [])
            {
                outputs.AddSlice(config.Id, slice.Name, new SceneRect(slice.X, slice.Y, slice.Width, slice.Height), slice.Id);
                if (slice.Adjustments is { } sliceAdjustments) outputs.SetAdjustments(slice.Id, sliceAdjustments);
                if (slice.VideoDelayFrames is { } sliceDelay) outputs.SetVideoDelay(slice.Id, sliceDelay);
                if (slice.Device is { } sliceDevice) outputs.AssignDevice(slice.Id, sliceDevice, slice.DeviceName, exclusive: false);
                if (slice.Placement is { } slicePlacement) outputs.SetPlacement(slice.Id, slicePlacement);
            }
        }
    }

    /// <summary>The setup as the Mac's JSON (an array of screens), via <see cref="MxuJson"/>.</summary>
    public static string ToJson(IReadOnlyList<PlaceholderScreenConfig> configs, bool indented = true) =>
        MxuJson.Serialize(configs, indented);

    /// <summary>Reads a saved setup; an unreadable one restores nothing, as on the Mac.</summary>
    public static List<PlaceholderScreenConfig> FromJson(string json)
    {
        try
        {
            return MxuJson.Deserialize<List<PlaceholderScreenConfig>>(json);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Windows only: the setup to start with when none is saved yet. The Mac
    /// starts with no screens; the Windows app has always had an audience output
    /// and a stage output, so it starts with one screen of each, carried by the
    /// displays it had chosen for them (if any).
    /// </summary>
    public static List<PlaceholderScreenConfig> FirstRun(
        string? audienceDevice = null, string? audienceDeviceName = null,
        string? stageDevice = null, string? stageDeviceName = null,
        int width = 1920, int height = 1080) =>
    [
        new()
        {
            Id = Guid.NewGuid(), Name = "Audience", Width = width, Height = height,
            Device = audienceDevice, DeviceName = audienceDevice is null ? null : audienceDeviceName,
        },
        new()
        {
            Id = Guid.NewGuid(), Name = "Stage", Width = width, Height = height, Role = ScreenRole.Confidence.Raw(),
            Device = stageDevice, DeviceName = stageDevice is null ? null : stageDeviceName,
        },
    ];
}
