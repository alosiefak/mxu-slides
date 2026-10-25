using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using Mode = MxU.Slides.Core.Animation.AnimationPresetApplication.Mode;

namespace MxU.Slides.Windows.ViewModels;

// The overlay editor's Animation tab (ViewModels/AnimationTabViewModel.cs, shared with the theme and
// stage layout editors): this half answers it for an overlay. An overlay never gets a click, so
// presets and generated steps are made as the Mac makes them and then filed into "when it goes up"
// and "when it's taken down" (OverlaySteps).
public sealed partial class OverlayEditorViewModel
{
    /// <summary>The Animation tab: gallery, presets, steps, timeline, morphs.</summary>
    public AnimationTabViewModel Animation { get; }

    IAnimatedComposition? IAnimationHost.Composition => Current;

    IReadOnlyList<SlideObject> IAnimationHost.SelectedObjects => SelectedObjects();

    void IAnimationHost.AnimationChanged(bool objects) => Changed(objects ? Parts.Objects | Parts.Fields : Parts.None);

    bool IAnimationHost.IsOverlay => true;

    void IAnimationHost.Notify(string message) => Notice = message;

    /// <summary>Plays the preview from <paramref name="seconds"/> into the play-through (the step list's ▶).</summary>
    public void PlayPreviewFrom(double seconds)
    {
        if (Current is not { } overlay || canvas is null) return;
        previewStartedAt = ShowState.HostClock() - Math.Max(seconds, 0);
        canvas.ShowPreview(OverlayPreview.PlayThrough(overlay, SceneSize.Hd, previewStartedAt, null, BackdropScene()));
        IsPlaying = true;
        previewClock.Start();
    }

    void IAnimationHost.ScrubPreview(double seconds) => Playhead = Math.Clamp(seconds, 0, PreviewDuration);

    // MARK: Posing a step on the canvas

    private string? posedStepId;

    /// <summary>
    /// The canvas edits a step's pose (a morph's end state, an In's custom start) instead of the
    /// overlay's objects: it's given the objects with the pose in place of its object (the same
    /// instance, so dragging it moves the pose), drawn over a faint copy of where it rests.
    /// </summary>
    public void Pose(string? stepId)
    {
        if (Current is not { } overlay) return;
        StopPreview();
        var posed = stepId is null ? null : AnimationTimelineEditing.PosedObjects(overlay, stepId);
        posedStepId = posed is null ? null : stepId;
        Mirror = new Slide { Id = overlay.Id, Name = overlay.Name, Objects = posed ?? overlay.Objects, AnimationOrder = overlay.AnimationOrder };
        var keep = canvas?.Selection.ToList() ?? [];
        canvas?.Load(Mirror, null, null);
        canvas?.Select(posedStepId is not null && AnimationEditing.Find(overlay, posedStepId) is { } found ? [found.ObjectId] : keep);
        OnPropertyChanged(nameof(IsPosing));
    }

    /// <summary>True while a step's pose is on the canvas.</summary>
    public bool IsPosing => posedStepId is not null;

    List<string> IAnimationHost.ApplyPreset(PresetChoice choice, List<string> objectIds, Mode mode)
    {
        if (Current is not { } overlay) return [];
        var added = choice.Apply(overlay, objectIds, mode);
        OverlaySteps.File(overlay, added);
        return added;
    }

    /// <summary>
    /// One step per line or word: a fade (a pulse for emphasis) per piece, which follow one another
    /// (an overlay never gets a click), overlapping a little, in "when it goes up" ("taken down" for Outs).
    /// </summary>
    List<string> IAnimationHost.GenerateSteps(SlideObject text, AnimationRanges.Generator by, AnimationKind kind)
    {
        if (Current is not { } overlay) return [];
        var animation = kind == AnimationKind.Emphasis ? StepAnimation.Pulse : StepAnimation.Fade;
        var ids = AnimationRanges.Generate(overlay, text.Id, AnimationRanges.WholeText(text.Text), by, kind, animation, AnimationTrigger.OnClick);
        if (ids.Count == 0)
        {
            Notice = "That text has nothing to split yet.";
            return [];
        }
        for (var i = 1; i < ids.Count; i++)
            AnimationEditing.Update(overlay, ids[i], s =>
            {
                s.Trigger = AnimationTrigger.AfterPrevious;
                s.DelaySeconds = by == AnimationRanges.Generator.Word ? -0.38 : -0.25;
            });
        OverlaySteps.File(overlay, ids);
        Notice = $"{ids.Count} steps, one per {(by == AnimationRanges.Generator.Word ? "word" : "line")}.";
        return ids;
    }
}
