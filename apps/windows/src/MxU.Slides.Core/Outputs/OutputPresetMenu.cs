using MxU.Slides.Core.Lyrics;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Outputs;

/// <summary>
/// The output preset ("look") switcher: the header chip and the Screens panel's menu. The first
/// choice is no preset at all (every layer reaches every screen). Port of OutputPresetMenu in
/// Sources/Modules/OutputsModule.swift and the name shortening in Sources/OutputPresetChip.swift.
/// </summary>
public static class OutputPresetMenu
{
    public const string DefaultName = "Default — All Layers";

    /// <summary>One menu entry; the default's id is "".</summary>
    public sealed record Choice(string Id, string Name);

    public static List<Choice> Choices(IEnumerable<OutputPreset> presets) =>
        [new Choice("", DefaultName), .. presets.Select(preset => new Choice(preset.Id, preset.Name))];

    public static bool IsActive(Choice choice, string? activePresetId) => (activePresetId ?? "") == choice.Id;

    /// <summary>The active preset's name; a missing or unknown one reads as the default.</summary>
    public static string ActiveName(IEnumerable<OutputPreset> presets, string? activePresetId) =>
        activePresetId is not null && presets.FirstOrDefault(p => p.Id == activePresetId) is { } active ? active.Name : DefaultName;

    /// <summary>The chip's label: 13 characters at most, then "…" (spaces before it trimmed).</summary>
    public static string ChipName(string name)
    {
        var characters = name.Characters();
        return characters.Count <= 13 ? name : string.Concat(characters.Take(13)).TrimmingWhitespaces() + "…";
    }

    public static string ChipHelp(string activeName) => $"Output Preset: {activeName}. Which layers reach each screen";
}
