using MxU.Slides.Core.Model;
using Slot = MxU.Slides.Core.Animation.AnimationSequence.GroupSlot;

namespace MxU.Slides.Core.Animation;

/// <summary>
/// An overlay never gets a click: it plays "when it goes up" and "when it's taken down". Presets
/// and generated steps are made the Mac's way (an In waits for a click), so the overlay editor
/// files them: Ins and emphasis to the end of "when it goes up", Outs to the end of "when it's
/// taken down", one at a time in play order so a word-by-word build keeps its order and stagger.
/// Windows-only (the Mac edits overlays as one-slide compositions, where clicks exist).
/// </summary>
public static class OverlaySteps
{
    /// <summary>Files the steps (ids in play order) into the overlay's two groups.</summary>
    public static void File(IAnimatedComposition overlay, IEnumerable<string> stepIds)
    {
        foreach (var id in stepIds)
        {
            if (AnimationEditing.Find(overlay, id) is not { } found) continue;
            var slot = found.Step.Kind == AnimationKind.Out ? Slot.Exit : Slot.Auto;
            AnimationEditing.MoveIntoGroup(overlay, id, slot, int.MaxValue);
        }
    }
}
