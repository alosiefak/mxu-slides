using MxU.Slides.Core.Outputs;

namespace MxU.Slides.Core.Stage;

/// <summary>
/// Which stage layout each confidence screen shows: the band's monitor can show chords while
/// the pastor's shows the clock and the next slide. A screen with no layout chosen shows the
/// built-in one. Port of the assignment half of Sources/ConfidenceMonitorController.swift (the
/// Mac keeps this map in UserDefaults as "confidence.screenLayouts", screen UUID → layout id;
/// Windows keeps the same map in settings.json, because screens belong to this PC).
/// </summary>
public sealed class ConfidenceScreenLayouts
{
    /// <summary>The settings key the Mac stores the map under, used as its JSON name here too.</summary>
    public const string SettingsKey = "confidence.screenLayouts";

    private readonly Dictionary<Guid, string> assignments = [];

    public IReadOnlyDictionary<Guid, string> Assignments => assignments;

    /// <summary>The layout chosen for a screen, or null for the built-in layout.</summary>
    public string? LayoutId(Guid screenId) => assignments.GetValueOrDefault(screenId);

    /// <summary>Chooses a screen's layout (null or empty: the built-in one). False when nothing changed.</summary>
    public bool SetLayout(string? layoutId, Guid screenId)
    {
        var wanted = string.IsNullOrEmpty(layoutId) ? null : layoutId;
        if (LayoutId(screenId) == wanted) return false;
        if (wanted is null) assignments.Remove(screenId);
        else assignments[screenId] = wanted;
        return true;
    }

    /// <summary>
    /// The "Set Confidence Layout" action: one screen, or (no screen) every confidence screen.
    /// A layout the library doesn't have is refused, so a stale action can't blank the monitors.
    /// False when it was refused or nothing changed.
    /// </summary>
    public bool Apply(string? layoutId, Guid? screenId, IEnumerable<Guid> confidenceScreens, Func<string, bool> layoutExists)
    {
        var resolved = string.IsNullOrEmpty(layoutId) ? null : layoutId;
        if (resolved is not null && !layoutExists(resolved)) return false;
        if (screenId is { } one) return SetLayout(resolved, one);
        var changed = false;
        foreach (var screen in confidenceScreens) changed |= SetLayout(resolved, screen);
        return changed;
    }

    /// <summary>Drops choices naming a layout the library no longer has (they fall back to the built-in one). True when any went.</summary>
    public bool Prune(Func<string, bool> layoutExists)
    {
        var gone = assignments.Where(pair => !layoutExists(pair.Value)).Select(pair => pair.Key).ToList();
        foreach (var screen in gone) assignments.Remove(screen);
        return gone.Count > 0;
    }

    /// <summary>The saved form: the Mac's map of screen ids (Swift's upper-case UUID strings) to layout ids.</summary>
    public Dictionary<string, string> ToStored() =>
        assignments.ToDictionary(pair => SwiftUuid.Format(pair.Key), pair => pair.Value, StringComparer.Ordinal);

    /// <summary>Reads a saved map; keys that aren't screen ids are skipped, as the Mac's restore does.</summary>
    public static ConfidenceScreenLayouts FromStored(IReadOnlyDictionary<string, string>? stored)
    {
        var layouts = new ConfidenceScreenLayouts();
        foreach (var (key, layoutId) in stored ?? new Dictionary<string, string>())
            if (SwiftUuid.Parse(key) is { } screen && !string.IsNullOrEmpty(layoutId)) layouts.assignments[screen] = layoutId;
        return layouts;
    }

    /// <summary>
    /// Windows only: before this, one stage layout served every stage screen. The first time the
    /// map is made, each confidence screen starts on that layout, so nothing on stage changes.
    /// </summary>
    public static ConfidenceScreenLayouts Migrated(string? sharedLayoutId, IEnumerable<Guid> confidenceScreens)
    {
        var layouts = new ConfidenceScreenLayouts();
        if (string.IsNullOrEmpty(sharedLayoutId)) return layouts;
        foreach (var screen in confidenceScreens) layouts.assignments[screen] = sharedLayoutId;
        return layouts;
    }
}
