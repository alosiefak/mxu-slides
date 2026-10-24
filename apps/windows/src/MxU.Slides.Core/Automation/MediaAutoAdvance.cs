namespace MxU.Slides.Core.Automation;

/// <summary>
/// Auto-advance after media: a negative delay fires the next slide that many seconds
/// before the video ends (so the next cue is up as it finishes). Port of
/// PresenterCore/MediaAutoAdvance.swift.
/// </summary>
public static class MediaAutoAdvance
{
    /// <summary>How long before the end to fire: the negative part of the delay.</summary>
    public static double LeadSeconds(double delay) => Math.Max(0, -delay);

    /// <summary>True once the time left (wall clock) is within the lead.</summary>
    public static bool ShouldPreFire(double remainingWallClock, double delay)
    {
        var lead = LeadSeconds(delay);
        return lead > 0 && remainingWallClock <= lead;
    }
}

/// <summary>
/// The "Automatic" timer choice for a stage layout's timer: the first running timer, else
/// the first timer. Port of PresenterCore/Library/AutomaticTimer.swift (its wording).
/// </summary>
public static class AutomaticTimer
{
    public const string Title = "Automatic (Running Timer)";

    public const string Help = "Shows the first timer that's running. If none are, shows the first timer in your list.";

    /// <summary>How sentences name it ("counts down the first running timer").</summary>
    public const string Phrase = "the first running timer";
}
