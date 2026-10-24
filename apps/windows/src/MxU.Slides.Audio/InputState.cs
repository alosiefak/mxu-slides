namespace MxU.Slides.Audio;

/// <summary>
/// Watches a direct capture input (a DeckLink card's audio, say) for going quiet: an
/// input that opened but has delivered nothing for <see cref="SilentSeconds"/> is
/// reopened, and a failure schedules one retry at a time, noting each new error message
/// once. Pure state, no timers. Port of DirectInputLiveness.swift.
/// </summary>
public struct DirectInputLiveness : IEquatable<DirectInputLiveness>
{
    public const double SilentSeconds = 3;

    /// <summary>What to do about a failure: nothing, or retry (and log it when <c>Note</c>).</summary>
    public readonly record struct FailureAction(bool Retry, bool Note)
    {
        public static readonly FailureAction Ignore = new(false, false);

        public static FailureAction RetryWith(bool note) => new(true, note);
    }

    public DirectInputLiveness()
    {
        LastArrivalAt = double.NegativeInfinity;
    }

    public bool Running { get; set; }
    public bool IsOpen { get; private set; }
    public bool RetryPending { get; private set; }
    public double LastArrivalAt { get; private set; }
    public string? LastError { get; private set; }

    public void Opened(double now)
    {
        IsOpen = true;
        LastError = null;
        LastArrivalAt = now;
    }

    public void Arrived(double now) => LastArrivalAt = now;

    public void Opening() => IsOpen = false;

    public void Stopped()
    {
        Running = false;
        IsOpen = false;
    }

    /// <param name="current">False when the failure is from an open we have since replaced.</param>
    public FailureAction Failed(string message, bool current)
    {
        if (!Running || !current || RetryPending) return FailureAction.Ignore;
        IsOpen = false;
        RetryPending = true;
        var note = LastError != message;
        LastError = message;
        return FailureAction.RetryWith(note);
    }

    public void Retrying() => RetryPending = false;

    public readonly bool ShouldReopen(double now) =>
        Running && IsOpen && !RetryPending && now - LastArrivalAt > SilentSeconds;

    public readonly bool Equals(DirectInputLiveness other) =>
        Running == other.Running && IsOpen == other.IsOpen && RetryPending == other.RetryPending
        && LastArrivalAt.Equals(other.LastArrivalAt) && LastError == other.LastError;

    public override readonly bool Equals(object? obj) => obj is DirectInputLiveness other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(Running, IsOpen, RetryPending, LastArrivalAt, LastError);
}

/// <summary>What to do with a capture input when its device comes or goes. Port of InputHotplug.swift.</summary>
public static class InputHotplug
{
    public enum Action
    {
        None,
        Start,
        Suspend,
    }

    public static Action Decide(bool devicePresent, bool captureLive) => (devicePresent, captureLive) switch
    {
        (true, false) => Action.Start,
        (false, true) => Action.Suspend,
        _ => Action.None,
    };
}

/// <summary>
/// When the operator monitors on headphones that are also some output's device, those
/// outputs go quiet so the monitor isn't doubled. Port of MonitorTakeover.
/// </summary>
public static class MonitorTakeover
{
    /// <param name="outputs">Each output and its device (null = follows the system default).</param>
    /// <param name="silentUid">The "no device" placeholder, which is never silenced.</param>
    public static HashSet<string> SilencedOutputs(
        IEnumerable<(string Id, string? DeviceUid)> outputs, string? monitorDeviceUid, string? systemDefaultUid, string silentUid)
    {
        var monitor = monitorDeviceUid ?? systemDefaultUid;
        var silenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var output in outputs)
        {
            var device = output.DeviceUid ?? systemDefaultUid;
            if (device is not null && device == monitor && output.DeviceUid != silentUid) silenced.Add(output.Id);
        }
        return silenced;
    }
}

/// <summary>The audio-input id for a DeckLink card's embedded audio ("decklink:&lt;persistent id&gt;").</summary>
public static class DeckLinkAudioUid
{
    public const string Prefix = "decklink:";

    public static string Uid(long persistentId) => Prefix + persistentId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static long? PersistentId(string uid) =>
        uid.StartsWith(Prefix, StringComparison.Ordinal)
        && long.TryParse(uid.AsSpan(Prefix.Length), System.Globalization.NumberStyles.AllowLeadingSign,
            System.Globalization.CultureInfo.InvariantCulture, out var id)
            ? id : null;
}
