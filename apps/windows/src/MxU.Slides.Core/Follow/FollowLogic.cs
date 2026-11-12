using System.Globalization;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Follow;

/// <summary>
/// Backing up another computer (MxU PR #12, FollowLogic.swift): a backup follows its booth fire for
/// fire, and takes over if the booth stops answering. This is the part with no network in it: how a
/// backup's link to its booth stands, what the banners and Settings say, who is in charge, and when
/// a backup takes over or gives way on its own. Same wording as the Mac, but "computer" where the
/// Mac says "Mac" (either can be a booth or a backup).
/// </summary>
public static class FollowLogic
{
    /// <summary>
    /// Slide actions a backup holds while it follows: gear the booth already drove (MIDI out to the
    /// lighting desk, starting or stopping the stream). The picture (media, overlays, inputs) runs.
    /// </summary>
    public static bool Holds(SlideActionKind kind) => kind is SlideActionKind.MidiOut or SlideActionKind.CaptureStart or SlideActionKind.CaptureStop;

    /// <summary>How a backup's link to its booth stands.</summary>
    public abstract record State
    {
        public sealed record Connecting : State;

        public sealed record Live : State;

        /// <param name="Frozen">It still answers but its app stopped moving (a pong whose beat stands still).</param>
        public sealed record Lost(bool Frozen) : State;

        public sealed record GaveUp : State;

        public static readonly State IsConnecting = new Connecting();
        public static readonly State IsLive = new Live();
        public static readonly State HasGivenUp = new GaveUp();
    }

    /// <summary>The backup's banner: following, standing by, lost, quit.</summary>
    public static (string Title, string Detail) Banner(
        string following, State state, DateTimeOffset? lastFire, bool boothQuit = false, bool standby = false,
        CultureInfo? culture = null, TimeZoneInfo? zone = null)
    {
        if (boothQuit && state is not (State.Live or State.Connecting))
        {
            return ($"{following} quit.", standby
                ? $"Nothing shows here, and this computer stands by again when {following} opens."
                : $"This computer keeps showing the last thing it fired, and follows again when {following} opens.");
        }
        var waits = standby ? "Nothing shows here until this computer takes over." : "This computer's triggers wait until it takes over.";
        return state switch
        {
            State.Connecting => ($"Connecting to {following}…", waits),
            State.Live => (standby ? $"Ready to take over from {following}." : $"Backing up {following}.",
                lastFire is { } fire
                    ? $"Last fire {Time(fire, culture, zone, seconds: true)}. {waits}"
                    : standby ? waits : $"In step. {waits}"),
            State.Lost lost => (lost.Frozen ? $"{following} froze." : $"{following} stopped answering.", standby
                ? $"Take Over puts up what {following} last showed."
                : "This computer keeps showing the last thing it fired."),
            _ => ($"Can't reach {following}.", "Check that both computers are on the same network, then try again."),
        };
    }

    /// <summary>A backup that lost a live booth takes over on its own, unless it waits for Take Over or the booth quit.</summary>
    public static bool TakesOverOnItsOwn(bool automatic, bool backingUp, bool wasLive, State now, bool boothQuit = false) =>
        automatic && backingUp && wasLive && !boothQuit && now is State.Lost;

    public static (string Title, string Detail) InChargeBanner(
        string booth, DateTimeOffset since, bool onItsOwn = false, bool boothStreamed = false, CultureInfo? culture = null, TimeZoneInfo? zone = null)
    {
        var time = Time(since, culture, zone, seconds: false);
        var detail = boothStreamed
            ? $"{booth} was streaming. Start Stream Here to send it from this computer."
            : "It answers MIDI, Companion and the Scheduler now.";
        var title = onItsOwn
            ? $"{booth} stopped answering, so this computer took over at {time}."
            : $"This computer took over from {booth} at {time}.";
        return (title, detail);
    }

    public static string CopiedLine(int items) => items switch
    {
        0 => "",
        1 => " 1 item copied here over the network.",
        _ => $" {items} items copied here over the network.",
    };

    /// <summary>How long a booth that only dropped off the network has to come back and be in charge again.</summary>
    public static readonly TimeSpan HandBackWindow = TimeSpan.FromSeconds(60);

    /// <summary>
    /// A backup that took over on its own because the link dropped (not a freeze, not a relaunch)
    /// gives way when the same run of the booth's app comes back within a minute.
    /// </summary>
    public static bool HandsBackOnReturn(bool onItsOwn, bool frozen, DateTimeOffset since, DateTimeOffset now, string? launchThen, string? launchNow) =>
        onItsOwn && !frozen && launchThen is not null && launchThen == launchNow && now - since <= HandBackWindow;

    public static string CameBackLine(string booth, TimeSpan after) =>
        $"{booth} came back after {Math.Max(1, (int)Math.Round(after.TotalSeconds, MidpointRounding.AwayFromZero))} seconds, so it answers again.";

    public static string BoothBackupLine(string backup, bool connected, bool inCharge = false)
    {
        if (inCharge) return $"{backup} took over, so it answers MIDI, Companion and the Scheduler. Take Back when this computer should again.";
        return connected
            ? $"{backup} is backing up this computer, fire for fire. If this computer stops answering, it takes over."
            : $"{backup} isn't connected. It follows again when MxU Slides opens on it, on this network.";
    }

    /// <summary>Settings › Backup on the booth: its backups and which are connected.</summary>
    public static (string Title, string Detail) BoothSettingsLine(IReadOnlyList<string> backups, IReadOnlySet<string> connected, string? tookOver)
    {
        if (tookOver is not null) return ($"{tookOver} took over", "This computer's triggers wait until it takes back.");
        if (backups.Count >= 2)
        {
            var on = backups.Where(connected.Contains).ToList();
            return ($"{backups.Count} backups follow this computer", on.Count == 0 ? "None connected now." : $"Connected now: {string.Join(", ", on)}.");
        }
        if (backups.Count == 0) return ("No backup computer", "A second computer on this network can stand in if this one stops.");
        var name = backups[0];
        return connected.Contains(name)
            ? ($"{name} is following this computer", "Connected. It can stand in if this computer stops.")
            : ($"{name} isn't connected", "It follows again when MxU Slides opens on it, on this network.");
    }

    /// <summary>Settings › Backup on the backup: how it follows.</summary>
    public static (string Title, string Detail) BackupSettingsLine(
        string following, State state, bool inCharge, bool standby, bool automatic, int copied, bool boothQuit = false)
    {
        if (inCharge) return ($"Took over from {following}", "This computer answers every trigger until it hands back.");
        var line = Banner(following, state, null, boothQuit, standby);
        var title = line.Title.EndsWith('.') ? line.Title[..^1] : line.Title;
        if (state is not State.Live || boothQuit) return (title, line.Detail);
        var shows = standby ? "Stays ready with clear screens." : "Shows the same thing live.";
        var stops = automatic ? "Takes over after 3 seconds." : "Waits for Take Over.";
        var copies = copied switch { 0 => "", 1 => " 1 item copied.", _ => $" {copied} items copied." };
        return (title, $"{shows} {stops}{copies}");
    }

    public static (string Title, string Detail) BackupDroppedBanner(string backup) =>
        ($"{backup} stopped following.", $"This computer keeps running the service. {backup} follows again when it reconnects.");

    public static (string Title, string Detail) SteppedDownBanner(string backup) =>
        ($"{backup} took over.", "This computer's triggers wait until it takes back.");

    private static string Time(DateTimeOffset at, CultureInfo? culture, TimeZoneInfo? zone, bool seconds)
    {
        var format = culture ?? CultureInfo.CurrentCulture;
        var local = TimeZoneInfo.ConvertTime(at, zone ?? TimeZoneInfo.Local);
        return local.ToString(seconds ? format.DateTimeFormat.LongTimePattern : format.DateTimeFormat.ShortTimePattern, format);
    }
}

/// <summary>
/// Who is in charge, by number: each takeover or hand-back counts one up, and the higher number
/// stands, so a booth that unfreezes after its backup took over hears it and steps down. Port of FollowRole.
/// </summary>
public sealed record FollowRole(int Term, string InCharge)
{
    public FollowRole Handed(string station) => new(Term + 1, station);

    public FollowRole? Newer(FollowRole other) => other.Term > Term ? other : null;

    public static FollowRole Start(string booth) => new(0, booth);
}
