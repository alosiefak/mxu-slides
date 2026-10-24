using System.Collections.Immutable;

namespace MxU.Slides.Core.Outputs;

// Looks: the theme each screen shows the live slide through. On the Mac a look is
// set per output in an output preset's routing matrix (its "Slide Theme" column);
// switching presets switches looks. Windows keeps that, and also lets a screen have
// its own look (the Screens window's Look picker), saved with the screen, for a
// church that always wants the stream in a lower third without setting up presets.
//
// What a screen shows:  the active preset's slide theme for it, if the preset gives it one,
//                       else the screen's own look, else the slide as designed.
public sealed partial class OutputConfiguration
{
    private ImmutableDictionary<Guid, SlideThemeLook> screenLooks = ImmutableDictionary<Guid, SlideThemeLook>.Empty;
    private ImmutableDictionary<string, SlideThemeLook> presetLooks = ImmutableDictionary<string, SlideThemeLook>.Empty;

    /// <summary>Each screen's own look (saved with the setup). Screens without one aren't listed.</summary>
    public IReadOnlyDictionary<Guid, SlideThemeLook> ScreenLooks => screenLooks;

    /// <summary>A screen's own look, or null (the slide as designed).</summary>
    public SlideThemeLook? ScreenLook(Guid screenId) => screenLooks.GetValueOrDefault(screenId);

    /// <summary>Sets or clears a screen's own look. A look with no theme is the same as none.</summary>
    public void SetScreenLook(Guid screenId, SlideThemeLook? look)
    {
        if (Screen(screenId) is null) return;
        var normalized = look is { ThemeId.Length: > 0 }
            ? new SlideThemeLook(look.ThemeId, Blank(look.Folder), Blank(look.SlideId))
            : null;
        if (ScreenLook(screenId) == normalized) return;
        screenLooks = normalized is null ? screenLooks.Remove(screenId) : screenLooks.SetItem(screenId, normalized);
        Persist();
    }

    /// <summary>The looks the active preset uses, by key (runtime; set by <see cref="OutputPresets.Apply"/>).</summary>
    public IReadOnlyDictionary<string, SlideThemeLook> PresetLooks => presetLooks;

    public void SetPresetLooks(IReadOnlyDictionary<string, SlideThemeLook> looks)
    {
        var next = looks.ToImmutableDictionary(StringComparer.Ordinal);
        if (next.Count == presetLooks.Count && next.All(pair => presetLooks.TryGetValue(pair.Key, out var look) && look == pair.Value)) return;
        presetLooks = next;
        Notify();
    }

    /// <summary>
    /// The look key a screen (or one of its slices) shows its slides through right now: the
    /// active preset's slide theme for it, else the screen's own look; null for the slide as designed.
    /// </summary>
    public string? LookKey(Guid screenId)
    {
        var routingKey = SwiftUuid.Format(SliceInfo(screenId)?.ScreenId ?? screenId);
        if (slideThemeRouting.TryGetValue(routingKey, out var key)) return key;
        return ScreenLook(SliceInfo(screenId)?.ScreenId ?? screenId)?.Key;
    }

    /// <summary>Every look an audience screen shows right now, by key: the variant scenes the app must build.</summary>
    public IReadOnlyDictionary<string, SlideThemeLook> ActiveLooks => LooksUnder(slideThemeRouting, presetLooks);

    /// <summary>
    /// The looks that would be showing under a preset's routing (<see cref="OutputPresets.Routing"/>):
    /// what the outputs will show once a slide that switches presets fires, so its hand-off can be worked out first.
    /// </summary>
    public IReadOnlyDictionary<string, SlideThemeLook> LooksUnder(PresetRouting routing) => LooksUnder(routing.SlideThemes, routing.Looks);

    private Dictionary<string, SlideThemeLook> LooksUnder(IReadOnlyDictionary<string, string> routing, IReadOnlyDictionary<string, SlideThemeLook> looks)
    {
        var result = new Dictionary<string, SlideThemeLook>(StringComparer.Ordinal);
        foreach (var screen in Screens.Where(s => Role(s.Id) == ScreenRole.Audience))
        {
            if (routing.TryGetValue(SwiftUuid.Format(screen.Id), out var routed))
            {
                if (looks.TryGetValue(routed, out var look)) result[routed] = look;
            }
            else if (ScreenLook(screen.Id) is { } own)
            {
                result[own.Key] = own;
            }
        }
        // A preset can also give a look to a direct display (not a screen): keep those too.
        foreach (var (target, key) in routing)
            if (SwiftUuid.Parse(target) is null && looks.TryGetValue(key, out var look)) result[key] = look;
        return result;
    }

    private static string? Blank(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
