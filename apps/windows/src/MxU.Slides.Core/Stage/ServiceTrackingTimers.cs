using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Stage;

/// <summary>
/// The service-tracking timers: Item, Section, Service, Video and Timeline, each as Remaining
/// and Elapsed. The app keeps them (they're never edited), so a stage layout can show "Item
/// Remaining" like any timer, by its id (<c>service_tracking.item.remaining</c>).
/// Port of SlideScene/ServiceTrackingTimers.swift. The Mac files them into a "Service Tracking"
/// folder on its timer board; Windows has no timer board, so <see cref="TimerController.SetSystemTimers"/>
/// keeps them after the user's timers instead.
/// </summary>
public static class ServiceTrackingTimers
{
    public const string IdPrefix = TimerController.PermanentIdPrefix;
    public const string FolderName = "Service Tracking";

    public enum Subject { Item, Section, Service, Video, Timeline }

    public enum Face { Remaining, Elapsed }

    public static string Raw(this Subject subject) => subject switch
    {
        Subject.Item => "item",
        Subject.Section => "section",
        Subject.Service => "service",
        Subject.Video => "video",
        _ => "timeline",
    };

    public static string Title(this Subject subject) => subject switch
    {
        Subject.Item => "Item",
        Subject.Section => "Section",
        Subject.Service => "Service",
        Subject.Video => "Video",
        _ => "Timeline",
    };

    public static string Raw(this Face face) => face == Face.Remaining ? "remaining" : "elapsed";

    public static IReadOnlyList<Face> Faces(Subject subject) => [Face.Remaining, Face.Elapsed];

    /// <summary>Timing comes from this computer's own fires ("local timing"), once anything has been fired.</summary>
    public static bool LocalTiming(bool fired) => fired;

    /// <summary>
    /// When local timing gives up: when the planned service would end, or 15 minutes after the
    /// planned end of the item fired last if that's later (a message running over). With no
    /// lengths planned, an hour after the last fire.
    /// </summary>
    public static DateTimeOffset LocalTimingEnds(DateTimeOffset firstFireAt, double servicePlannedSeconds, DateTimeOffset lastFireAt, double itemPlannedSeconds)
    {
        if (servicePlannedSeconds <= 0) return lastFireAt.AddSeconds(3600);
        var serviceEnd = firstFireAt.AddSeconds(servicePlannedSeconds);
        var itemEnd = lastFireAt.AddSeconds(itemPlannedSeconds + 900);
        return serviceEnd > itemEnd ? serviceEnd : itemEnd;
    }

    /// <summary>The planned length of some rows: their durations added up (rows without one count nothing).</summary>
    public static double PlannedSeconds(IEnumerable<int?> durations) => durations.Sum(d => d ?? 0);

    public static string Id(Subject subject, Face face) => $"{IdPrefix}{subject.Raw()}.{face.Raw()}";

    public static string Name(Subject subject, Face face) => $"{subject.Title()} {(face == Face.Remaining ? "Remaining" : "Elapsed")}";

    public static bool IsPermanent(string id) => TimerController.IsPermanent(id);

    /// <summary>The subject and face an id names, or null (a user timer, or a retired id like "pre_service").</summary>
    public static (Subject Subject, Face Face)? SubjectAndFace(string id)
    {
        if (!id.StartsWith(IdPrefix, StringComparison.Ordinal)) return null;
        var parts = id[IdPrefix.Length..].Split('.');
        if (parts.Length != 2) return null;
        var subject = Enum.GetValues<Subject>().Cast<Subject?>().FirstOrDefault(s => s!.Value.Raw() == parts[0]);
        var face = Enum.GetValues<Face>().Cast<Face?>().FirstOrDefault(f => f!.Value.Raw() == parts[1]);
        return subject is { } s && face is { } f ? (s, f) : null;
    }

    public static string Description(Subject subject, Face face) => (subject, face) switch
    {
        (Subject.Item, Face.Remaining) => "Counts down the current service item from its length. Runs from the moment this computer fires the item.",
        (Subject.Item, Face.Elapsed) => "Counts up from the moment this computer fired the current service item.",
        (Subject.Section, Face.Remaining) => "Counts down the items under the current header from their combined length, starting at the first one fired.",
        (Subject.Section, Face.Elapsed) => "Counts up from the first item fired under the current header.",
        (Subject.Service, Face.Remaining) => "Counts down the service's combined item lengths, starting at the first item fired on this computer.",
        (Subject.Service, Face.Elapsed) => "Counts up from the moment the first item was fired.",
        (Subject.Video, Face.Remaining) => "Counts down the video playing for the current item.",
        (Subject.Video, Face.Elapsed) => "Counts up from the start of the video playing for the current item.",
        (Subject.Timeline, Face.Remaining) => "Counts down to the end of the presentation timeline playing, on its own clock or from timecode.",
        _ => "Counts up from the start of the presentation timeline playing, on its own clock or from timecode.",
    };

    /// <summary>Every service-tracking timer id.</summary>
    public static IReadOnlyList<string> AllIds =>
        [.. Enum.GetValues<Subject>().SelectMany(subject => Faces(subject).Select(face => Id(subject, face)))];

    /// <summary>
    /// A Remaining face: a countdown from the planned length, anchored now with
    /// <paramref name="remainingSeconds"/> left (it counts on by itself while running). No time
    /// left to count (nothing planned) is an idle face.
    /// </summary>
    public static TimerSnapshot Remaining(
        Subject subject, double plannedSeconds, double? remainingSeconds, bool running, DateTimeOffset now, IReadOnlyList<TimerWarning>? warnings = null)
    {
        if (remainingSeconds is not { } remaining) return Idle(subject, Face.Remaining);
        var planned = Math.Max(plannedSeconds, 0);
        return new TimerSnapshot(Name(subject, Face.Remaining), TimerMode.Countdown, id: Id(subject, Face.Remaining),
            isRunning: running, runningSince: running ? now : null, banked: planned - remaining, durationSeconds: planned,
            warnings: warnings ?? TimerWarning.Defaults);
    }

    /// <summary>An Elapsed face: a count up from <paramref name="elapsedSeconds"/>, its limit the planned length (0 = none).</summary>
    public static TimerSnapshot Elapsed(
        Subject subject, double elapsedSeconds, double limitSeconds, bool running, DateTimeOffset now, IReadOnlyList<TimerWarning>? warnings = null) =>
        new(Name(subject, Face.Elapsed), TimerMode.CountUp, id: Id(subject, Face.Elapsed),
            isRunning: running, runningSince: running ? now : null, banked: Math.Max(elapsedSeconds, 0), durationSeconds: Math.Max(limitSeconds, 0),
            warnings: warnings ?? TimerWarning.Defaults);

    /// <summary>A face with nothing to show: stopped at zero.</summary>
    public static TimerSnapshot Idle(Subject subject, Face face) =>
        new(Name(subject, face), face == Face.Remaining ? TimerMode.Countdown : TimerMode.CountUp, id: Id(subject, face));

    /// <summary>
    /// The same faces as last published, give or take: a running face re-anchored each refresh
    /// reads the same value, so republishing would only churn the timer list (Windows-only; the
    /// Mac publishes every refresh). Used to decide whether a refresh is news.
    /// </summary>
    public static bool Tracks(IReadOnlyList<TimerSnapshot> published, IReadOnlyList<TimerSnapshot> fresh, DateTimeOffset at, double tolerance = 0.3)
    {
        if (published.Count != fresh.Count) return false;
        for (var i = 0; i < fresh.Count; i++)
        {
            var (a, b) = (published[i], fresh[i]);
            if (a.Id != b.Id || a.Name != b.Name || a.Mode != b.Mode || a.IsRunning != b.IsRunning || a.DurationSeconds != b.DurationSeconds)
                return false;
            if (Math.Abs(a.Value(at) - b.Value(at)) >= tolerance) return false;
        }
        return true;
    }
}
