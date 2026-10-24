namespace MxU.Slides.VideoIO.Common;

/// <summary>What to do with an input when its device comes or goes.</summary>
public enum HotplugAction { Start, Suspend, None }

/// <summary>
/// The rule for plugged and unplugged devices: a device that is back but not
/// capturing gets started; one that vanished while capturing gets suspended (held so
/// it can restart on replug); anything else is left alone, so matching states never
/// churn. Port of AudioEngine/InputHotplug.swift.
/// </summary>
public static class InputHotplug
{
    public static HotplugAction Decide(bool devicePresent, bool captureLive) => (devicePresent, captureLive) switch
    {
        (true, false) => HotplugAction.Start,
        (false, true) => HotplugAction.Suspend,
        _ => HotplugAction.None,
    };
}

/// <summary>
/// What a failed open of a direct input should do next: nothing (the failure is stale,
/// the input was stopped, or a retry is already queued), or retry, and whether this
/// failure is worth a diagnostics line (only when the message changed).
/// </summary>
public readonly record struct LivenessFailureAction(bool Retry, bool Note)
{
    public static LivenessFailureAction Ignore => new(false, false);

    public static LivenessFailureAction RetryWith(bool note) => new(true, note);
}

/// <summary>
/// Keeps a direct capture (a DeckLink card opened by this app) alive: reopens it when it
/// goes quiet for <see cref="SilentSeconds"/>, retries a failed open one attempt at a
/// time, and notes each distinct error once per episode. Pure state; the owner locks
/// around it and does the actual opening. Port of AudioEngine/DirectInputLiveness.swift.
/// </summary>
public sealed class DirectInputLiveness
{
    /// <summary>An open input that delivers nothing for this long is reopened.</summary>
    public const double SilentSeconds = 3;

    /// <summary>True from start until stop: only a running input is watched or retried.</summary>
    public bool Running { get; set; }

    public bool IsOpen { get; private set; }
    public bool RetryPending { get; private set; }
    public double LastArrivalAt { get; private set; } = double.NegativeInfinity;
    public string? LastError { get; private set; }

    /// <summary>The open succeeded at <paramref name="now"/>; that counts as an arrival.</summary>
    public void Opened(double now)
    {
        IsOpen = true;
        LastError = null;
        LastArrivalAt = now;
    }

    /// <summary>A frame or audio packet arrived.</summary>
    public void Arrived(double now) => LastArrivalAt = now;

    /// <summary>An open was started; until it answers, silence isn't a reason to reopen.</summary>
    public void Opening() => IsOpen = false;

    public void Stopped()
    {
        Running = false;
        IsOpen = false;
    }

    /// <param name="current">False when the failure belongs to an attempt that has since been replaced.</param>
    public LivenessFailureAction Failed(string message, bool current)
    {
        if (!Running || !current || RetryPending) return LivenessFailureAction.Ignore;
        IsOpen = false;
        RetryPending = true;
        var note = LastError != message;
        LastError = message;
        return LivenessFailureAction.RetryWith(note);
    }

    /// <summary>The queued retry is starting now; the next failure may queue another.</summary>
    public void Retrying() => RetryPending = false;

    public bool ShouldReopen(double now) => Running && IsOpen && !RetryPending && now - LastArrivalAt > SilentSeconds;
}
