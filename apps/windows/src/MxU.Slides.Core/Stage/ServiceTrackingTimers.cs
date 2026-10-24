using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Stage;

/// <summary>
/// The app's own read-only timers that follow the service (item, section, service, video and
/// timeline, each Remaining and Elapsed), filed in a "Service Tracking" folder on the Timers
/// board. Port of SlideScene/ServiceTrackingTimers.swift. The ids, names, descriptions and the
/// merge into the board are here; the Mac's ServiceTimingController, which feeds them the
/// service's planned lengths, isn't on Windows yet (see docs/phase5/operator.md).
/// </summary>
public static class ServiceTrackingTimers
{
    public const string IdPrefix = TimerController.PermanentIdPrefix;
    public const string FolderId = "service_tracking";
    public const string FolderName = "Service Tracking";

    public enum Subject { Item, Section, Service, Video, Timeline }

    public enum Face { Remaining, Elapsed }

    private static string Raw(Subject subject) => subject.ToString().ToLowerInvariant();

    private static string Raw(Face face) => face.ToString().ToLowerInvariant();

    public static string Title(Subject subject) => subject.ToString();

    public static IReadOnlyList<Face> Faces(Subject subject) => Enum.GetValues<Face>();

    /// <summary>Where the timing comes from: this PC's own fires (the only kind there is yet).</summary>
    public enum LocalTiming { Local }

    /// <summary>A service is timed here once something in it has been fired here.</summary>
    public static LocalTiming? LocalTimingFor(bool fired) => fired ? LocalTiming.Local : null;

    /// <summary>
    /// When local timing stops: the planned service's end, or later when the last item fired is
    /// running over (its length plus 15 minutes); with no lengths at all, an hour after the last fire.
    /// </summary>
    public static DateTimeOffset LocalTimingEnds(DateTimeOffset firstFireAt, double servicePlannedSeconds, DateTimeOffset lastFireAt, double itemPlannedSeconds)
    {
        if (servicePlannedSeconds <= 0) return lastFireAt.AddSeconds(3600);
        var planned = firstFireAt.AddSeconds(servicePlannedSeconds);
        var overrun = lastFireAt.AddSeconds(itemPlannedSeconds + 900);
        return planned > overrun ? planned : overrun;
    }

    /// <summary>The service's planned length: the items' lengths added up (an item without one counts 0).</summary>
    public static double PlannedSeconds(IEnumerable<int?> durations) => durations.Sum(d => d ?? 0);

    public static string Id(Subject subject, Face face) => $"{IdPrefix}{Raw(subject)}.{Raw(face)}";

    public static string Name(Subject subject, Face face) => $"{Title(subject)} {(face == Face.Remaining ? "Remaining" : "Elapsed")}";

    public static bool IsPermanent(string id) => TimerController.IsPermanent(id);

    public static (Subject Subject, Face Face)? SubjectAndFace(string id)
    {
        if (!id.StartsWith(IdPrefix, StringComparison.Ordinal)) return null;
        var parts = id[IdPrefix.Length..].Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return null;
        var subject = Enum.GetValues<Subject>().Cast<Subject?>().FirstOrDefault(s => Raw(s!.Value) == parts[0]);
        var face = Enum.GetValues<Face>().Cast<Face?>().FirstOrDefault(f => Raw(f!.Value) == parts[1]);
        return subject is { } s && face is { } f ? (s, f) : null;
    }

    /// <summary>What the timer counts, for the read-only note the Timers panel shows when one is opened.</summary>
    public static string Description(Subject subject, Face face) => (subject, face) switch
    {
        (Subject.Item, Face.Remaining) => "Counts down the current service item from its length. Runs from the moment this PC fires the item.",
        (Subject.Item, Face.Elapsed) => "Counts up from the moment this PC fired the current service item.",
        (Subject.Section, Face.Remaining) => "Counts down the items under the current header from their combined length, starting at the first one fired.",
        (Subject.Section, Face.Elapsed) => "Counts up from the first item fired under the current header.",
        (Subject.Service, Face.Remaining) => "Counts down the service's combined item lengths, starting at the first item fired on this PC.",
        (Subject.Service, Face.Elapsed) => "Counts up from the moment the first item was fired.",
        (Subject.Video, Face.Remaining) => "Counts down the video playing for the current item.",
        (Subject.Video, Face.Elapsed) => "Counts up from the start of the video playing for the current item.",
        (Subject.Timeline, Face.Remaining) => "Counts down to the end of the presentation timeline playing, on its own clock or from timecode.",
        _ => "Counts up from the start of the presentation timeline playing, on its own clock or from timecode.",
    };

    public static IReadOnlyList<string> AllIds =>
        [.. Enum.GetValues<Subject>().SelectMany(subject => Faces(subject).Select(face => Id(subject, face)))];

    /// <summary>A Remaining face: counts down from the planned length; with no plan it is idle.</summary>
    public static TimerSnapshot Remaining(Subject subject, double plannedSeconds, double? remainingSeconds, bool running, DateTimeOffset now,
        IReadOnlyList<TimerWarning>? warnings = null)
    {
        if (remainingSeconds is not { } remaining) return Idle(subject, Face.Remaining);
        var planned = Math.Max(plannedSeconds, 0);
        return new TimerSnapshot(Name(subject, Face.Remaining), TimerMode.Countdown, id: Id(subject, Face.Remaining),
            isRunning: running, runningSince: running ? now : null, banked: planned - remaining, durationSeconds: planned, warnings: warnings);
    }

    public static TimerSnapshot Elapsed(Subject subject, double elapsedSeconds, bool running, DateTimeOffset now, double limitSeconds = 0,
        IReadOnlyList<TimerWarning>? warnings = null) =>
        new(Name(subject, Face.Elapsed), TimerMode.CountUp, id: Id(subject, Face.Elapsed),
            isRunning: running, runningSince: running ? now : null, banked: Math.Max(elapsedSeconds, 0),
            durationSeconds: Math.Max(limitSeconds, 0), warnings: warnings);

    public static TimerSnapshot Idle(Subject subject, Face face) =>
        new(Name(subject, face), face == Face.Remaining ? TimerMode.Countdown : TimerMode.CountUp, id: Id(subject, face));

    /// <param name="TimersChanged">Something to publish (the list or an anchor moved).</param>
    /// <param name="UserTimersChanged">The operator's own timers moved: save them.</param>
    /// <param name="BoardChanged">The board changed: save it.</param>
    public sealed record Merge(List<TimerSnapshot> Timers, TimerBoard Board, bool TimersChanged, bool UserTimersChanged, bool BoardChanged);

    /// <summary>
    /// Puts a fresh set of service-tracking timers in place of the old ones: they are filed in
    /// the Service Tracking folder (made if missing), every timer is ordered as the board shows
    /// it, and permanent ids no longer sent (retired faces) drop out.
    /// </summary>
    public static Merge MergeInto(IReadOnlyList<TimerSnapshot> system, IReadOnlyList<TimerSnapshot> timers, TimerBoard board)
    {
        var user = timers.Where(t => !IsPermanent(t.Id)).ToList();
        var merged = user.Concat(system).ToList();
        var next = board.Copy();
        next.EnsureFolder(FolderId, FolderName);
        next.Reconcile([.. merged.Select(t => t.Id)]);
        foreach (var timer in system.Where(t => next.FolderContaining(t.Id)?.Id != FolderId))
            next.MoveTimerIntoFolder(timer.Id, FolderId);
        var order = next.AllTimerIds;
        merged = [.. merged.OrderBy(t => order.IndexOf(t.Id) is var at && at >= 0 ? at : int.MaxValue)];
        return new Merge(
            merged, next,
            TimersChanged: !merged.SequenceEqual(timers),
            UserTimersChanged: !merged.Where(t => !IsPermanent(t.Id)).SequenceEqual(user),
            BoardChanged: !next.Equals(board));
    }
}
