using MxU.Slides.Audio.Timeline;
using MxU.Slides.Core.Automation;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Windows.ViewModels;

// The timeline editor's input: presses, drags and keys on the lanes. The lanes view
// (Views/TimelineLanesView) turns the mouse into these calls, and the self-test makes the
// same calls, so a drag there goes through exactly the code a real one does.
public sealed partial class TimelineEditorViewModel
{
    // MARK: Pointer (the lanes view and the self-test both drive these)

    private TimelineCardDrag? cardDrag;
    private bool dragToggles;
    private (ClipFadeGeometry.Handle Handle, TimelineCue Clip, double PressX, bool Moved)? fadeDrag;
    private (SongTransitions.Plan Plan, double PressX, double SavedX)? transitionDrag;
    private (double PressX, double Start, bool Engaged)? rangeDrag;
    private bool scrubbing;

    /// <summary>The card drag in progress (the view draws the dragged cards where it says).</summary>
    public TimelineCardDrag? CardDrag => cardDrag;

    /// <summary>A fade being dragged: the view draws it in place of the saved one.</summary>
    public (string CueId, bool Out, ClipFade Fade)? LiveFade { get; private set; }

    /// <summary>A transition length being dragged.</summary>
    public double? LiveTransition { get; private set; }

    /// <summary>True while any press is held on the lanes (the view keeps the mouse captured).</summary>
    public bool IsPressing => cardDrag is not null || fadeDrag is not null || transitionDrag is not null || rangeDrag is not null || scrubbing;

    /// <summary>
    /// A press on the lanes at a content point (x from the song's start, y from the top of the
    /// ruler): the ruler moves the playhead, a fade or transition handle starts that drag, a
    /// card starts a card press, empty lane starts a range. Returns false when it hit nothing.
    /// </summary>
    public bool PointerPressed(double x, double y, bool toggle = false, int clicks = 1)
    {
        if (!HasSong) return false;
        if (TimelineEditorLayout.InRuler(y))
        {
            scrubbing = true;
            if (!IsPlaying) Seek(Layout.TimeAt(x));
            return true;
        }
        if (CanEdit && TransitionHandleAt(x, y) is { } plan)
        {
            transitionDrag = (plan, x, Layout.Map.XOfTime(Math.Min(plan.NextAt, plan.FadeFrom ?? plan.NextAt)));
            return true;
        }
        if (CanEdit && FadeHandleAt(x, y) is { } handle && Cues.FirstOrDefault(c => c.Id == handle.CueId) is { } clip)
        {
            fadeDrag = (handle, clip, x, false);
            return true;
        }
        if (Layout.CardAt(x, y) is { } hit)
        {
            if (clicks >= 2)
            {
                DoubleClicked(hit.Card.Cue);
                return true;
            }
            cardDrag = new TimelineCardDrag(hit, x);
            dragToggles = toggle;
            return true;
        }
        if (Layout.RowAt(y) is not null)
        {
            rangeDrag = (x, Layout.TimeAt(x), false);
            return true;
        }
        return false;
    }

    /// <summary>The pointer moved with a press held. <paramref name="free"/> (Alt held) drags without snapping.</summary>
    public void PointerMoved(double x, double y, bool free = false)
    {
        if (scrubbing)
        {
            if (!IsPlaying) Seek(Layout.TimeAt(x));
            return;
        }
        if (cardDrag is { } drag)
        {
            if (!CanEdit) return;
            if (drag.MovedTo(x, free ? null : SnapFor(drag.Card)))
            {
                Selection = TimelineEditing.Engaged(Selection, drag.Card.Cue.Id);
                StateChanged?.Invoke();
            }
            Redraw?.Invoke();
            return;
        }
        if (fadeDrag is { } fade)
        {
            var dragged = ClipFadeGeometry.Dragged(fade.Handle, (x - fade.PressX) / Zoom, fade.Clip);
            LiveFade = (fade.Handle.CueId, fade.Handle.Out, dragged);
            fadeDrag = fade with { Moved = true };
            Redraw?.Invoke();
            return;
        }
        if (transitionDrag is { } transition)
        {
            var time = Layout.TimeAt(transition.SavedX + (x - transition.PressX));
            LiveTransition = TimelineEditing.ClampedTransition(transition.Plan.End - time, transition.Plan.End);
            Redraw?.Invoke();
            return;
        }
        if (rangeDrag is { } range && Math.Abs(x - range.PressX) > TimelineBlockEdit.EngageDistance)
        {
            Range = TimelineEditing.Range(range.Start, Layout.TimeAt(x));
            Selection.Clear();
            rangeDrag = range with { Engaged = true };
            Redraw?.Invoke();
            StateChanged?.Invoke();
        }
    }

    /// <summary>The press let go: a card click selects, a card drag moves or trims, a fade or transition drag is saved.</summary>
    public void PointerReleased(double x, double y, bool free = false)
    {
        if (scrubbing)
        {
            scrubbing = false;
            if (IsPlaying) Seek(Layout.TimeAt(x));
            return;
        }
        if (cardDrag is { } drag)
        {
            cardDrag = null;
            var cue = drag.Card.Cue;
            var (mode, dx) = drag.Released(x, free || !CanEdit ? null : SnapFor(drag.Card));
            Selection = TimelineEditing.Released(Selection, cue.Id, mode, dragToggles);
            Range = null;
            if (mode is { } moved && dx != 0 && CanEdit)
            {
                Move(cue, moved, dx / Zoom, "Move Cue", Layout.Map.Locate(drag.Card.X + drag.Card.Width)?.Time);
                return;
            }
            Redraw?.Invoke();
            StateChanged?.Invoke();
            return;
        }
        if (fadeDrag is { } fade)
        {
            fadeDrag = null;
            var live = LiveFade;
            LiveFade = null;
            if (fade.Moved && live is { } set) Edit("Change Fade", cues => TimelineEditing.WithFadeSet(cues, set.CueId, set.Out, set.Fade));
            else Redraw?.Invoke();
            return;
        }
        if (transitionDrag is not null)
        {
            transitionDrag = null;
            var seconds = LiveTransition;
            LiveTransition = null;
            if (seconds is { } length) SetTransitionSeconds(length);
            Redraw?.Invoke();
            return;
        }
        if (rangeDrag is { } range)
        {
            rangeDrag = null;
            if (!range.Engaged)
            {
                Range = null;
                Selection.Clear();
            }
            Redraw?.Invoke();
            StateChanged?.Invoke();
        }
    }

    /// <summary>The press was lost (the window lost the mouse): drop it without changing anything.</summary>
    public void PointerCancelled()
    {
        cardDrag = null;
        fadeDrag = null;
        LiveFade = null;
        transitionDrag = null;
        LiveTransition = null;
        rangeDrag = null;
        scrubbing = false;
        Redraw?.Invoke();
    }

    /// <summary>A card drag's snapping: onto the other cues' edges and the playhead (the Mac's snap closure).</summary>
    private Func<TimelineBlockEdit.Mode, double, double> SnapFor(TimelineCard card) => (mode, dx) =>
        TimelineEditing.SnappedDelta(dx, card.Cue, Cues, mode, Selection, Layout.Map, card.X, card.Width, IsCurrent ? InsertionTime : null);

    /// <summary>Whether a card is drawn moved by the drag in progress (the dragged card and, for a move, the others selected with it).</summary>
    public bool MovesWithDrag(TimelineCard card)
    {
        if (cardDrag is not { Mode: TimelineBlockEdit.Mode.Move } drag) return false;
        if (!Selection.Contains(drag.Card.Cue.Id)) return false;
        if (Selection.Contains(card.Cue.Id)) return true;
        // Stems that start with a dragged stem move with it.
        return TimelineEditing.Together(drag.Card.Cue, Cues, Selection).Contains(card.Cue.Id);
    }

    private void DoubleClicked(TimelineCue cue)
    {
        if (cue.Kind() == TimelineLane.Actions && CanEdit) EditActionsRequested?.Invoke(cue);
        else Seek(cue.At);
    }

    /// <summary>The window opens the actions editor for an actions cue (a double-click, or Edit Actions…).</summary>
    public event Action<TimelineCue>? EditActionsRequested;

    // MARK: Keys

    /// <summary>
    /// A key on the lanes (from the window when no text box has the focus). Returns true when
    /// it did something. Ctrl+Z and Ctrl+Y undo and redo; Space plays.
    /// </summary>
    public bool Key(string key, bool control, bool shift, bool alt)
    {
        if (!HasSong) return false;
        if (control && !alt && key == "Z")
        {
            if (shift) Redo();
            else Undo();
            return true;
        }
        if (control && !alt && !shift && key == "Y")
        {
            Redo();
            return true;
        }
        if (!control && !alt && !shift && key == "Space")
        {
            PlayOrPause();
            return true;
        }
        if (!control && !alt && !shift && key == "Home")
        {
            Seek(0);
            return true;
        }
        if (TimelineEditing.VerbFor(key, control, shift, alt) is not { } verb) return false;
        if (verb is TimelineEditing.KeyVerb.Discard)
        {
            if (!IsRecording) return false;
            DiscardTake();
            return true;
        }
        if (!CanEdit) return false;
        switch (verb)
        {
            case TimelineEditing.KeyVerb.Split:
                SplitAtPlayhead();
                break;
            case TimelineEditing.KeyVerb.Copy:
                Copy();
                break;
            case TimelineEditing.KeyVerb.Paste:
                Paste();
                break;
            case TimelineEditing.KeyVerb.Delete:
                if (Range is not null || Picked is null) return false;
                DeleteSelected();
                break;
            case TimelineEditing.KeyVerb.RippleDelete:
                if (Range is null) return false;
                RippleDelete();
                break;
            case TimelineEditing.KeyVerb.Nudge nudge:
                if (Picked is null) return false;
                Nudge(nudge.Seconds);
                break;
        }
        return true;
    }
}
