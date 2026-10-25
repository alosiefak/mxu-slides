using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Json;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Library;

/// <summary>
/// The presets people save for themselves: animation recipes on the AnimationPresetBoard and
/// effect chains on the EffectPresetBoard, one document each, with the Mac's ids, so presets saved
/// on either app show in both once the libraries are shared. Port of the board half of the Mac's
/// AppModel (animationPresetBoard, saveAnimationPreset, overwriteAnimationPreset,
/// deleteAnimationPreset, and the same for effect presets). Each change saves a fresh copy of the
/// board, as AutomationLibrary does, so nothing holding the old one sees a half-made change.
/// </summary>
public static class PresetBoards
{
    /// <summary>The library's animation preset board (an empty one when it has none yet).</summary>
    public static AnimationPresetBoard Animations(LibraryStore library) =>
        library.Get<AnimationPresetBoard>(AnimationPresetBoard.WellKnownId) is { } board
            ? MxuJson.Clone(board)
            : new AnimationPresetBoard { Id = AnimationPresetBoard.WellKnownId, Presets = [] };

    /// <summary>Saves a new animation preset from an object's steps (see AnimationPresetApplication.Recipe). Returns it.</summary>
    public static AnimationPreset SaveAnimation(LibraryStore library, string name, List<AnimationStep> steps, BlockScroll? scroll = null, double? tilt = null)
    {
        var preset = new AnimationPreset { Id = LibraryStore.NewId(), Name = name.Trim(), Steps = steps, Scroll = scroll, Tilt = tilt };
        var board = Animations(library);
        board.Presets.Add(preset);
        library.Save(board);
        return preset;
    }

    /// <summary>Saves an object's steps over a saved preset (its name stays). False when there's no such preset.</summary>
    public static bool OverwriteAnimation(LibraryStore library, string id, List<AnimationStep> steps, BlockScroll? scroll = null, double? tilt = null)
    {
        var board = Animations(library);
        if (board.Presets.FirstOrDefault(p => p.Id == id) is not { } preset) return false;
        preset.Steps = steps;
        preset.Scroll = scroll;
        preset.Tilt = tilt;
        library.Save(board);
        return true;
    }

    public static bool DeleteAnimation(LibraryStore library, string id)
    {
        var board = Animations(library);
        if (board.Presets.RemoveAll(p => p.Id == id) == 0) return false;
        library.Save(board);
        return true;
    }

    /// <summary>The library's effect preset board (an empty one when it has none yet).</summary>
    public static EffectPresetBoard Effects(LibraryStore library) =>
        library.Get<EffectPresetBoard>(EffectPresetBoard.WellKnownId) is { } board
            ? MxuJson.Clone(board)
            : new EffectPresetBoard { Id = EffectPresetBoard.WellKnownId, Presets = [] };

    public static EffectPreset SaveEffect(LibraryStore library, string name, IEnumerable<Effect> effects)
    {
        var preset = new EffectPreset { Id = LibraryStore.NewId(), Name = name.Trim(), Effects = [.. effects.Select(MxuJson.Clone)] };
        var board = Effects(library);
        board.Presets.Add(preset);
        library.Save(board);
        return preset;
    }

    public static bool DeleteEffect(LibraryStore library, string id)
    {
        var board = Effects(library);
        if (board.Presets.RemoveAll(p => p.Id == id) == 0) return false;
        library.Save(board);
        return true;
    }
}
