using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using Mode = MxU.Slides.Core.Animation.AnimationPresetApplication.Mode;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// One entry in the presets gallery: the shelf it's on (<see cref="Group"/>), what it's called, a
/// line about it, and how to put it on objects. A class, not a record: two entries are the same
/// only if they're the same entry (the pickers find the selected one by reference).
/// </summary>
public sealed class PresetChoice(string group, string name, string description, string? savedId,
    Func<IAnimatedComposition, List<string>, Mode, List<string>> apply, Action<SlideObject, Mode>? applyEffects = null)
{
    public const string Kinetic = "Kinetic text";
    public const string Recommended = "Recommended";
    public const string Mine = "My presets";

    public string Group { get; } = group;

    public string Name { get; } = name;

    /// <summary>"Shelf › Name" (or just the name, for look effects).</summary>
    public string Title => Group.Length == 0 ? Name : $"{Group} › {Name}";

    public string Description { get; } = description;

    /// <summary>The id of a preset you saved (only those can be deleted).</summary>
    public string? SavedId { get; } = savedId;

    public Func<IAnimatedComposition, List<string>, Mode, List<string>> Apply { get; } = apply;

    public Action<SlideObject, Mode>? ApplyEffects { get; } = applyEffects;

    public override string ToString() => Title;
}

// The overlay editor's Animation tab, part two: presets (the Mac's recommended set, Windows'
// kinetic text, and your own saved ones), "one step per line / per word", and look effects.
// Presets and generated steps are made as the Mac makes them (an In waits for a click), then filed
// into "when it goes up" and "when it's taken down" (OverlaySteps), since an overlay gets no clicks.
public sealed partial class OverlayEditorViewModel
{
    // MARK: Animation presets

    private List<PresetChoice>? presetChoices;

    /// <summary>
    /// Every preset to choose from: Windows' kinetic text first, then the Mac's recommended set, then
    /// yours. Made once and kept (the gallery's tiles and the selection point at these entries), and
    /// made again when you save or delete one of your own.
    /// </summary>
    public IReadOnlyList<PresetChoice> PresetChoices => presetChoices ??= BuildPresetChoices();

    private List<PresetChoice> BuildPresetChoices()
    {
        var list = new List<PresetChoice>();
        foreach (var kinetic in KineticPresets.All)
            list.Add(new(PresetChoice.Kinetic, kinetic.Name, kinetic.Description, null, (c, ids, mode) => KineticText.ApplyTo(c, ids, kinetic, mode)));
        foreach (var group in AnimationPresetCatalog.RecommendedGroups)
            foreach (var preset in group.Presets)
            {
                var name = group.Presets.Count == 1 ? group.Name : $"{group.Name} · {preset.Name}";
                list.Add(new(PresetChoice.Recommended, name, PresetLine(preset), null, (c, ids, mode) => AnimationPresetApplication.ApplyTo(c, ids, preset, mode)));
            }
        foreach (var preset in PresetBoards.Animations(Library).Presets)
            list.Add(new(PresetChoice.Mine, preset.Name, PresetLine(preset), preset.Id, (c, ids, mode) => AnimationPresetApplication.ApplyTo(c, ids, preset, mode)));
        return list;
    }

    /// <summary>Your presets changed: the list is made again, keeping the selection where it can.</summary>
    private void PresetsChanged(string? select = null)
    {
        var keep = select ?? SelectedPreset?.Title;
        presetChoices = null;
        OnPropertyChanged(nameof(PresetChoices));
        SelectedPreset = PresetChoices.FirstOrDefault(p => p.Title == keep);
    }

    private static string PresetLine(AnimationPreset preset) =>
        preset.Steps.Count == 0 && preset.Scroll is not null ? "Scrolls the text (credits roll)."
            : string.Join(", then ", preset.Steps.Select(s => AnimationEditing.Sentence(s).ToLowerInvariant())) is { Length: > 0 } words
                ? char.ToUpperInvariant(words[0]) + words[1..] + "."
                : "";

    /// <summary>What a gallery tile plays: a small lower third with this preset on it (Core/Animation/PresetSamples.cs).</summary>
    public static Overlay Sample(PresetChoice choice) => PresetSamples.Make((c, ids) => choice.Apply(c, ids, Mode.Replace));

    [ObservableProperty]
    public partial PresetChoice? SelectedPreset { get; set; }

    partial void OnSelectedPresetChanged(PresetChoice? value) => OnPropertyChanged(nameof(CanDeletePreset));

    public bool CanDeletePreset => SelectedPreset?.SavedId is not null;

    /// <summary>Applies the chosen preset to the selected objects: "replace" swaps their steps, "add" keeps them.</summary>
    [RelayCommand]
    private void ApplyPreset(string? how)
    {
        if (Current is not { } overlay || SelectedPreset is not { } choice) return;
        var targets = SelectedObjects().Select(o => o.Id).ToList();
        if (targets.Count == 0)
        {
            Notice = "Select an object first, then apply a preset to it.";
            return;
        }
        var added = choice.Apply(overlay, targets, how == "add" ? Mode.Add : Mode.Replace);
        OverlaySteps.File(overlay, added);
        Changed(Parts.Steps | Parts.Objects);
        if (added.LastOrDefault() is { } last) SelectedStepRow = StepRows.FirstOrDefault(r => r.StepId == last);
        Notice = $"{choice.Title.Split(" › ").Last()} on {targets.Count} object{(targets.Count == 1 ? "" : "s")}: {added.Count} step{(added.Count == 1 ? "" : "s")}.";
    }

    /// <summary>Saves the first selected object's steps as a preset of your own (on the shared AnimationPresetBoard).</summary>
    public bool SavePreset(string name)
    {
        if (SelectedObjects().FirstOrDefault() is not { } o || string.IsNullOrWhiteSpace(name)) return false;
        var steps = AnimationPresetApplication.Recipe(o.AnimationSteps);
        var scroll = o.TextStyle?.Scroll;
        if (steps.Count == 0 && scroll is null)
        {
            Notice = "That object has no steps to save yet.";
            return false;
        }
        var preset = PresetBoards.SaveAnimation(Library, name, steps, scroll, scroll is null ? null : o.Tilt);
        PresetsChanged();
        SelectedPreset = PresetChoices.FirstOrDefault(p => p.SavedId == preset.Id);
        Notice = $"Saved “{preset.Name}” to your presets.";
        return true;
    }

    [RelayCommand]
    private void SavePresetAs()
    {
        var owner = System.Windows.Application.Current?.Windows.OfType<Views.OverlayEditorWindow>().FirstOrDefault();
        if (owner is null) return;
        if (Views.InputDialog.Ask(owner, "Save as preset", "A name for the animation preset:", "My preset") is { } name) SavePreset(name);
    }

    [RelayCommand]
    private void DeletePreset()
    {
        if (SelectedPreset?.SavedId is not { } id) return;
        PresetBoards.DeleteAnimation(Library, id);
        SelectedPreset = null;
        PresetsChanged();
    }

    // MARK: One step per line / per word

    /// <summary>
    /// The Mac's Generate › One Step per Line / per Word, on the selected text object's whole text
    /// (Windows' editor has no text selection): a fade (a pulse for emphasis) per piece. Each piece
    /// starts a moment after the one before, in "when it goes up" (or "when it's taken down" for Outs).
    /// </summary>
    [RelayCommand]
    private void GenerateSteps(string? by)
    {
        if (Current is not { } overlay) return;
        if (SelectedObjects() is not [{ ObjectKind: SlideObjectKind.Text } text])
        {
            Notice = "Select one text object to give each of its lines or words a step.";
            return;
        }
        var generator = by == "word" ? AnimationRanges.Generator.Word : AnimationRanges.Generator.Line;
        var animation = GalleryKind == AnimationKind.Emphasis ? StepAnimation.Pulse : StepAnimation.Fade;
        var ids = AnimationRanges.Generate(overlay, text.Id, AnimationRanges.WholeText(text.Text), generator, GalleryKind, animation, AnimationTrigger.OnClick);
        if (ids.Count == 0)
        {
            Notice = "That text has nothing to split yet.";
            return;
        }
        // An overlay never gets a click: the pieces follow one another instead, overlapping a little.
        for (var i = 1; i < ids.Count; i++)
            AnimationEditing.Update(overlay, ids[i], s =>
            {
                s.Trigger = AnimationTrigger.AfterPrevious;
                s.DelaySeconds = generator == AnimationRanges.Generator.Word ? -0.38 : -0.25;
            });
        OverlaySteps.File(overlay, ids);
        Changed(Parts.Steps);
        SelectedStepRow = StepRows.FirstOrDefault(r => r.StepId == ids[^1]);
        Notice = $"{ids.Count} steps, one per {(generator == AnimationRanges.Generator.Word ? "word" : "line")}.";
    }

    // MARK: Look effects

    private List<PresetChoice>? effectChoices;

    /// <summary>The Mac's recommended picture effects (Frost, Static, Heat Haze…) and your saved ones (made once and kept).</summary>
    public IReadOnlyList<PresetChoice> EffectChoices => effectChoices ??= BuildEffectChoices();

    private List<PresetChoice> BuildEffectChoices()
    {
        var list = new List<PresetChoice>();
        foreach (var group in EffectPresetCatalog.RecommendedGroups)
            foreach (var preset in group.Presets)
                list.Add(new("", group.Presets.Count == 1 ? group.Name : $"{group.Name} · {preset.Name}", "", null, (_, _, _) => [],
                    (o, mode) => EffectPresetCatalog.ApplyTo(o, preset, mode)));
        foreach (var preset in PresetBoards.Effects(Library).Presets)
            list.Add(new("My effects", preset.Name, "", preset.Id, (_, _, _) => [], (o, mode) => EffectPresetCatalog.ApplyTo(o, preset, mode)));
        return list;
    }

    [ObservableProperty]
    public partial PresetChoice? SelectedEffect { get; set; }

    /// <summary>Puts the chosen effects on the selected objects ("add" keeps theirs), or with "clear" takes all effects off.</summary>
    [RelayCommand]
    private void ApplyEffect(string? how)
    {
        if (Current is null) return;
        var selected = SelectedObjects();
        if (selected.Count == 0)
        {
            Notice = "Select an object first.";
            return;
        }
        foreach (var o in selected)
        {
            if (how == "clear") EffectPresetCatalog.ApplyTo(o, new EffectPreset { Id = "none", Name = "None", Effects = [] }, Mode.Replace);
            else SelectedEffect?.ApplyEffects?.Invoke(o, how == "add" ? Mode.Add : Mode.Replace);
        }
        Changed(Parts.Objects);
        Notice = how == "clear" ? "Effects taken off." : $"{SelectedEffect?.Title} on {selected.Count} object{(selected.Count == 1 ? "" : "s")} (drawn by the graphics card).";
    }
}
