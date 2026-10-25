using System.Globalization;
using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Automation;

/// <summary>
/// The rules of the Auto Advance editor: turning it on (7 seconds), counting from the slide firing
/// or from its video ending, the delay's limits, looping to the first slide, the words under it, and
/// the same setting on several slides at once. The fields themselves (<see cref="AutoAdvance"/> on a
/// slide, a media item or a whole presentation) already play (<see cref="AutoAdvanceController"/>).
/// <para>
/// Port of the Mac's Sources/AutoAdvanceControls.swift (the bindings and the caption), the auto-advance
/// half of SlideScene/SlideBulkEdit.swift (<c>setAutoAdvance</c>, <c>autoAdvanceValue</c>),
/// <c>PresentationSettingsButton.slideShowText</c> and the slide tile's <c>autoAdvanceSummary</c>
/// (PresentGridView.swift). Every change returns a new value: the editor writes it back and saves.
/// </para>
/// </summary>
public static class AutoAdvanceEditing
{
    /// <summary>The delay a newly switched-on Auto Advance starts with.</summary>
    public const double StartingDelay = 7;

    /// <summary>The delay's range: up to an hour; counting from a video's end it may be up to 30 s early.</summary>
    public const double LongestDelay = 3600;

    public const double EarliestBeforeVideoEnd = -30;

    /// <summary>Copies an advance, so an edit never changes a value someone else still holds.</summary>
    public static AutoAdvance? Copy(AutoAdvance? advance) =>
        advance is null ? null : new AutoAdvance { DelaySeconds = advance.DelaySeconds, LoopToStart = advance.LoopToStart, AfterPlayback = advance.AfterPlayback };

    /// <summary>On (7 seconds after the slide fires) or off.</summary>
    public static AutoAdvance? Enabled(AutoAdvance? current, bool on) =>
        on ? current is null ? new AutoAdvance { DelaySeconds = StartingDelay } : Copy(current) : null;

    /// <summary>
    /// Count from the video's end (the delay starts at 0: cut at the end) or from the slide firing
    /// (a zero delay goes back to 7, since "0 seconds after it fires" would skip the slide).
    /// </summary>
    public static AutoAdvance? CountingFromVideoEnd(AutoAdvance? current, bool afterPlayback)
    {
        if (Copy(current) is not { } next) return null;
        next.AfterPlayback = afterPlayback ? true : null;
        if (afterPlayback) next.DelaySeconds = 0;
        else if (next.DelaySeconds == 0) next.DelaySeconds = StartingDelay;
        return next;
    }

    /// <summary>The delay, kept in range: from 0 (or 30 s before a video's end) up to an hour.</summary>
    public static AutoAdvance? WithDelay(AutoAdvance? current, double seconds)
    {
        if (Copy(current) is not { } next || !double.IsFinite(seconds)) return Copy(current);
        var floor = next.AfterPlayback == true ? EarliestBeforeVideoEnd : 0;
        next.DelaySeconds = Math.Round(Math.Clamp(seconds, floor, LongestDelay), 1);
        return next;
    }

    /// <summary>Loop back to the first slide after the last (a walk-in loop), or carry on into the next service item.</summary>
    public static AutoAdvance? Looping(AutoAdvance? current, bool loops)
    {
        if (Copy(current) is not { } next) return null;
        next.LoopToStart = loops ? true : null;
        return next;
    }

    /// <summary>The words under the controls (the Mac's caption).</summary>
    /// <param name="mediaScope">For a media item: it fires the next run-order item, and has no Loop.</param>
    public static string Caption(AutoAdvance current, bool mediaScope)
    {
        if (mediaScope)
            return current.AfterPlayback == true
                ? "Fires the next run-order item this long after the video finishes. 0 cuts straight at the end; a negative delay starts the next item early, so its transition crossfades the ending."
                : "Fires the next run-order item this long after this media fires.";
        return current.AfterPlayback == true
            ? "Fires the next slide this long after this slide's foreground video finishes. 0 cuts straight at the end; a negative delay starts the next slide early, so its transition crossfades the ending. Background videos are ambient and don't drive the advance (time those from the fire instead). On the last slide, Loop wraps back to the first; without Loop the service keeps walking into the next item."
            : "Fires the next slide this long after this one fires. On the last slide, Loop wraps back to the first; without Loop the service keeps walking into the next item.";
    }

    /// <summary>What "Applies to" says for a whole presentation's Slide Show.</summary>
    public const string SlideShowNote = "Applies to every slide in this presentation. A slide with its own Auto Advance keeps its own timing.";

    // MARK: Words for menus and tiles

    /// <summary>"7 s", "After Video", "After Video + 2 s", "Off" or "Mixed" for the slides chosen (SlideBulkEdit.autoAdvanceValue).</summary>
    public static string Value(IReadOnlyList<AutoAdvance?> advances)
    {
        if (advances.Count == 0) return "Off";
        var first = advances[0];
        if (!advances.All(a => Same(a, first))) return "Mixed";
        if (first is null) return "Off";
        if (first.AfterPlayback == true)
            return first.DelaySeconds > 0 ? $"After Video + {TransitionEdit.Label(first.DelaySeconds)}" : "After Video";
        return TransitionEdit.Label(first.DelaySeconds);
    }

    /// <summary>Two advances that time the same way.</summary>
    public static bool Same(AutoAdvance? a, AutoAdvance? b) =>
        a is null ? b is null : b is not null && a.DelaySeconds == b.DelaySeconds && (a.LoopToStart ?? false) == (b.LoopToStart ?? false)
            && (a.AfterPlayback ?? false) == (b.AfterPlayback ?? false);

    /// <summary>"8s" or "8s · Loop": the Slide Show button's words (PresentationSettingsButton.slideShowText).</summary>
    public static string SlideShowText(AutoAdvance slideShow) => $"{Interval(slideShow.DelaySeconds)}{(slideShow.LoopToStart == true ? " · Loop" : "")}";

    /// <summary>"Auto Advance: after 8s, loops to the first slide", for a slide tile's tooltip (the Mac's autoAdvanceSummary).</summary>
    public static string Summary(AutoAdvance advance)
    {
        var seconds = advance.DelaySeconds;
        var interval = Interval(Math.Abs(seconds));
        var text = advance.AfterPlayback == true
            ? seconds > 0 ? $"Auto Advance: {interval} after the video ends"
                : seconds < 0 ? $"Auto Advance: {interval} before the video ends"
                : "Auto Advance: when the video ends"
            : $"Auto Advance: after {interval}";
        if (advance.LoopToStart == true) text += ", loops to the first slide";
        return text;
    }

    private static string Interval(double seconds) =>
        seconds == Math.Round(seconds) ? $"{(int)seconds}s" : seconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";

    // MARK: Several slides at once

    /// <summary>Gives every slide in <paramref name="slideIds"/> the same Auto Advance (or none), each its own copy (SlideBulkEdit.setAutoAdvance).</summary>
    public static int SetOnSlides(Presentation presentation, IEnumerable<string> slideIds, AutoAdvance? advance)
    {
        var targets = slideIds.ToHashSet(StringComparer.Ordinal);
        var changed = 0;
        foreach (var slide in presentation.Slides.Where(s => targets.Contains(s.Id)))
        {
            slide.AutoAdvance = Copy(advance);
            changed++;
        }
        return changed;
    }

    /// <summary>The Auto Advance of each chosen slide, in the presentation's order (what <see cref="Value"/> describes).</summary>
    public static List<AutoAdvance?> OnSlides(Presentation presentation, IEnumerable<string> slideIds)
    {
        var targets = slideIds.ToHashSet(StringComparer.Ordinal);
        return [.. presentation.Slides.Where(s => targets.Contains(s.Id)).Select(s => s.AutoAdvance)];
    }

    /// <summary>Which slides one edit reaches (Windows: the main grid picks one slide at a time, so the editor offers these too).</summary>
    public enum Reach
    {
        /// <summary>The slides chosen (one, or a selection).</summary>
        Chosen,

        /// <summary>Every slide of the chosen slides' groups (Verse 1, Chorus…).</summary>
        Groups,

        /// <summary>Every slide in the presentation.</summary>
        Every,
    }

    /// <summary>The ids an edit reaches: the chosen slides, every slide in their groups, or every slide.</summary>
    public static List<string> Reached(Presentation presentation, IReadOnlyCollection<string> chosen, Reach reach)
    {
        switch (reach)
        {
            case Reach.Every:
                return [.. presentation.Slides.Select(s => s.Id)];
            case Reach.Groups:
                var sections = presentation.Slides.Where(s => chosen.Contains(s.Id)).Select(s => s.SectionId).ToHashSet();
                return [.. presentation.Slides.Where(s => chosen.Contains(s.Id) || s.SectionId is not null && sections.Contains(s.SectionId)).Select(s => s.Id)];
            default:
                return [.. presentation.Slides.Where(s => chosen.Contains(s.Id)).Select(s => s.Id)];
        }
    }
}
