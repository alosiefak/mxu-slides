using MxU.Slides.Core.Model;
using static MxU.Slides.Core.Stage.ServiceTrackingTimers;

namespace MxU.Slides.Core.Stage;

/// <summary>How far through its song a timeline is (for the Timeline faces).</summary>
public sealed record TimelineProgress(double Elapsed, double Length, bool Playing);

/// <summary>
/// Keeps time for the service from this computer's own fires ("local timing"): when each row
/// was first fired, which row is current, and when the first one was. From that it makes the
/// service-tracking timers: how long the current item, its section and the whole service have
/// run and have left against their planned lengths, plus the live video and the timeline.
/// Port of the non-UI half of Sources/ServiceTimingController.swift. Not thread-safe: drive it
/// from the UI thread. Timing gives up by itself some time after the service should have
/// ended (<see cref="LocalTimingEnds"/>); the app arms that wake.
/// </summary>
public sealed class ServiceTiming
{
    private readonly Dictionary<string, DateTimeOffset> localItemStarts = new(StringComparer.Ordinal);
    private string? lastServiceId;

    /// <summary>When each row was first fired on this computer.</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> LocalItemStarts => localItemStarts;

    public string? CurrentServiceItemId { get; private set; }

    public DateTimeOffset? LocalServiceStart { get; private set; }

    /// <summary>Another service was chosen: its timing starts from nothing.</summary>
    public void ServiceChanged(string? serviceId)
    {
        if (serviceId == lastServiceId) return;
        lastServiceId = serviceId;
        Clear();
    }

    /// <summary>A row of the current service fired. True when it became the current row (firing it again changes nothing).</summary>
    public bool NoteItemFired(string serviceItemId, DateTimeOffset now)
    {
        if (serviceItemId == CurrentServiceItemId) return false;
        CurrentServiceItemId = serviceItemId;
        localItemStarts[serviceItemId] = now;
        LocalServiceStart ??= now;
        return true;
    }

    /// <summary>Stops the timers ("Clear", or the service ran its course).</summary>
    public void Clear()
    {
        localItemStarts.Clear();
        CurrentServiceItemId = null;
        LocalServiceStart = null;
    }

    /// <summary>Something has been fired and is still timed: the panel says the timers come from this computer's fires.</summary>
    public bool IsTiming(Service? service) => LocalTiming(PredictedItem(service) is not null);

    /// <summary>The current row, if it's still in the service.</summary>
    public ServiceItem? CurrentRow(Service? service) =>
        service is null || CurrentServiceItemId is not { } id ? null : service.Items.FirstOrDefault(i => i.Id == id);

    /// <summary>When local timing should give up, after firing <paramref name="firedItemId"/> now.</summary>
    public DateTimeOffset EndsAt(IReadOnlyList<ServiceItem> runOfShow, string firedItemId, DateTimeOffset now) =>
        LocalTimingEnds(LocalServiceStart ?? now, PlannedRows(runOfShow), now,
            runOfShow.FirstOrDefault(i => i.Id == firedItemId)?.Duration ?? 0);

    private static double PlannedRows(IEnumerable<ServiceItem> rows) =>
        PlannedSeconds(rows.Where(i => i.ItemKind != ServiceItemKind.Header).Select(i => i.Duration));

    private (ServiceItem Item, DateTimeOffset StartedAt)? PredictedItem(Service? service)
    {
        if (service is null || CurrentServiceItemId is not { } id || !localItemStarts.TryGetValue(id, out var startedAt)) return null;
        return service.Items.FirstOrDefault(i => i.Id == id) is { } item ? (item, startedAt) : null;
    }

    /// <summary>
    /// The timers now: Item, Section, Service, Video and Timeline, each Remaining then Elapsed.
    /// <paramref name="runOfShow"/> is the service's visible rows (hidden ones don't count).
    /// </summary>
    public List<TimerSnapshot> Snapshots(Service? service, IReadOnlyList<ServiceItem> runOfShow, DateTimeOffset now,
        VideoCountdown? video = null, TimelineProgress? timeline = null)
    {
        var result = new List<TimerSnapshot>();
        result.AddRange(LocalItem(PredictedItem(service), now));
        result.AddRange(LocalSpans(service, runOfShow, now));
        result.AddRange(VideoTimers(video, now));
        result.AddRange(TimelineTimers(timeline, now));
        return result;
    }

    private static IEnumerable<TimerSnapshot> LocalItem((ServiceItem Item, DateTimeOffset StartedAt)? predicted, DateTimeOffset now)
    {
        if (predicted is not { } current) return [Idle(Subject.Item, Face.Remaining), Idle(Subject.Item, Face.Elapsed)];
        var planned = (double)(current.Item.Duration ?? 0);
        var since = (now - current.StartedAt).TotalSeconds;
        return
        [
            Remaining(Subject.Item, planned, planned > 0 ? planned - since : null, running: true, now),
            Elapsed(Subject.Item, since, planned, running: true, now),
        ];
    }

    private IEnumerable<TimerSnapshot> LocalSpans(Service? service, IReadOnlyList<ServiceItem> rows, DateTimeOffset now)
    {
        List<TimerSnapshot> idle =
        [
            Idle(Subject.Section, Face.Remaining), Idle(Subject.Section, Face.Elapsed),
            Idle(Subject.Service, Face.Remaining), Idle(Subject.Service, Face.Elapsed),
        ];
        if (service is null || CurrentServiceItemId is not { } currentId || LocalServiceStart is not { } serviceStart) return idle;
        var index = rows.ToList().FindIndex(i => i.Id == currentId);
        if (index < 0) return idle;
        // The section: the rows between the headers either side of the current row.
        var sectionStart = 0;
        for (var i = index - 1; i >= 0; i--)
            if (rows[i].ItemKind == ServiceItemKind.Header)
            {
                sectionStart = i + 1;
                break;
            }
        var sectionEnd = rows.Count;
        for (var i = index + 1; i < rows.Count; i++)
            if (rows[i].ItemKind == ServiceItemKind.Header)
            {
                sectionEnd = i;
                break;
            }
        var section = rows.Skip(sectionStart).Take(sectionEnd - sectionStart).Where(i => i.ItemKind != ServiceItemKind.Header).ToList();
        var sectionPlanned = PlannedSeconds(section.Select(i => i.Duration));
        var firstInSection = section.Where(i => localItemStarts.ContainsKey(i.Id)).Select(i => localItemStarts[i.Id]).DefaultIfEmpty().Min();
        var sectionElapsed = firstInSection == default ? 0 : (now - firstInSection).TotalSeconds;
        var servicePlanned = PlannedRows(rows);
        var serviceElapsed = (now - serviceStart).TotalSeconds;
        return
        [
            Remaining(Subject.Section, sectionPlanned, sectionPlanned > 0 ? sectionPlanned - sectionElapsed : null, running: true, now),
            Elapsed(Subject.Section, sectionElapsed, sectionPlanned, running: true, now),
            Remaining(Subject.Service, servicePlanned, servicePlanned > 0 ? servicePlanned - serviceElapsed : null, running: true, now),
            Elapsed(Subject.Service, serviceElapsed, servicePlanned, running: true, now),
        ];
    }

    private static IEnumerable<TimerSnapshot> VideoTimers(VideoCountdown? countdown, DateTimeOffset now)
    {
        if (countdown is null) return [Idle(Subject.Video, Face.Remaining), Idle(Subject.Video, Face.Elapsed)];
        var position = countdown.Position + (countdown.IsPlaying ? (now - countdown.AnchoredAt).TotalSeconds : 0);
        return
        [
            Remaining(Subject.Video, countdown.Duration, countdown.Duration - position, countdown.IsPlaying, now),
            Elapsed(Subject.Video, position, countdown.Duration, countdown.IsPlaying, now),
        ];
    }

    /// <summary>The Timeline faces (Windows fills these in from its timeline player; the Mac defines them and leaves them idle).</summary>
    private static IEnumerable<TimerSnapshot> TimelineTimers(TimelineProgress? timeline, DateTimeOffset now)
    {
        if (timeline is not { Length: > 0 } progress) return [Idle(Subject.Timeline, Face.Remaining), Idle(Subject.Timeline, Face.Elapsed)];
        return
        [
            Remaining(Subject.Timeline, progress.Length, progress.Length - progress.Elapsed, progress.Playing, now),
            Elapsed(Subject.Timeline, progress.Elapsed, progress.Length, progress.Playing, now),
        ];
    }

    /// <summary>The next row that fires by itself after the current one ("Next · Sermon 30:00").</summary>
    public ServiceItem? NextRow(IReadOnlyList<ServiceItem> runOfShow) =>
        CurrentServiceItemId is { } id ? Library.ServiceRunOrder.NextFireable(runOfShow, id) : null;
}
