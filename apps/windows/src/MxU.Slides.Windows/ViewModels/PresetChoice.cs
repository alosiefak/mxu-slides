using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Model;
using Mode = MxU.Slides.Core.Animation.AnimationPresetApplication.Mode;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// One entry in the presets gallery: the shelf it's on (<see cref="Group"/>), what it's called, a
/// line about it, and how to put it on objects. A class, not a record: two entries are the same
/// only if they're the same entry (the pickers find the selected one by reference).
/// </summary>
public sealed class PresetChoice(string group, string name, string description, string? savedId,
    Func<IAnimatedComposition, List<string>, Mode, List<string>> apply)
{
    public const string Kinetic = "Kinetic text";
    public const string Recommended = "Recommended";
    public const string Mine = "My presets";

    public string Group { get; } = group;

    public string Name { get; } = name;

    /// <summary>"Shelf › Name".</summary>
    public string Title => Group.Length == 0 ? Name : $"{Group} › {Name}";

    public string Description { get; } = description;

    /// <summary>The id of a preset you saved (only those can be deleted).</summary>
    public string? SavedId { get; } = savedId;

    public Func<IAnimatedComposition, List<string>, Mode, List<string>> Apply { get; } = apply;

    /// <summary>For a kinetic text preset, the recipe itself (a theme design turns it into a kinetic build instead of ranged steps).</summary>
    public KineticPreset? KineticRecipe { get; init; }

    public override string ToString() => Title;
}
