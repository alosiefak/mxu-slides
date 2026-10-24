using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Stage;
using MxU.Slides.Core.Threading;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.Rendering;

namespace MxU.Slides.Windows.ViewModels;

/// <summary>
/// Service tracking: how long the current item, its section and the whole service have run and
/// have left against the lengths planned for them, timed from this computer's own fires, plus the
/// live video and the timeline. They're published as the permanent "service_tracking." timers, so
/// a stage layout shows them like any timer, and the Service Tracking panel shows them here.
/// Core's <see cref="ServiceTiming"/> keeps the clock; this is the app's half of the Mac's
/// ServiceTimingController and its ServiceTrackingModule panel.
/// </summary>
public sealed partial class MainViewModel
{
    public ServiceTiming ServiceTiming { get; } = new();

    private IReadOnlyList<TimerSnapshot> publishedTracking = [];
    private ITimer? localTimingWake;

    /// <summary>A service row fired (a slide of it, its media or its music): its timers start, and local timing's end moves on.</summary>
    private void NoteServiceItemFired(string? itemId)
    {
        if (itemId is null || SelectedService is not { } service || !service.Items.Any(i => i.Id == itemId)) return;
        var now = DateTimeOffset.Now;
        ArmLocalTimingEnd(ServiceRunOrder.Visible(service.Items), itemId, now);
        if (ServiceTiming.NoteItemFired(itemId, now)) RefreshServiceTracking(force: true);
    }

    /// <summary>
    /// Local timing gives up by itself once the service should be over (later if an item runs over),
    /// so yesterday's timers aren't counting when the PC is next used. Timed on the show clock.
    /// </summary>
    private void ArmLocalTimingEnd(IReadOnlyList<ServiceItem> rows, string firedItemId, DateTimeOffset now)
    {
        var endsAt = ServiceTiming.EndsAt(rows, firedItemId, now);
        var dispatcher = Dispatcher.CurrentDispatcher;
        localTimingWake?.Dispose();
        localTimingWake = ShowTimeProvider.Shared.CreateTimer(_ => dispatcher.BeginInvoke(() =>
        {
            if (!ServiceTiming.IsTiming(SelectedService)) return;
            Log.Info($"Service tracking: local timing ended ({endsAt:t})");
            ClearServiceTracking();
        }), null, TimeSpan.FromSeconds(Math.Max(0, (endsAt - now).TotalSeconds)), Timeout.InfiniteTimeSpan);
    }

    /// <summary>The panel's Clear: stops these timers on this computer.</summary>
    [RelayCommand]
    private void ClearServiceTracking()
    {
        localTimingWake?.Dispose();
        localTimingWake = null;
        ServiceTiming.Clear();
        RefreshServiceTracking(force: true);
    }

    /// <summary>The timeline faces' source: the song timeline that's playing (or paused part-way).</summary>
    private TimelineProgress? TimelineTracking() =>
        timeline is { ItemId: not null } player && TimelineLength > 0 && (player.Playing || player.PlayheadTime > 0)
            ? new TimelineProgress(player.PlayheadTime, TimelineLength, player.Playing)
            : null;

    /// <summary>
    /// Publishes the timers when they're news: a fire, a service change, a video paused or moved.
    /// Running faces count on by themselves, so a refresh that reads the same isn't published
    /// (each publish redraws the stage and the timer list).
    /// </summary>
    private void RefreshServiceTracking(bool force = false)
    {
        var now = DateTimeOffset.Now;
        var service = SelectedService;
        var rows = service is null ? [] : ServiceRunOrder.Visible(service.Items);
        var timers = ServiceTiming.Snapshots(service, rows, now, TransportCountdown(), TimelineTracking());
        if (force || !ServiceTrackingTimers.Tracks(publishedTracking, timers, now))
        {
            publishedTracking = timers;
            Timers.SetSystemTimers(timers);
        }
        UpdateTrackingPanel(service, rows, now);
    }

    // MARK: The Service Tracking panel

    [ObservableProperty]
    public partial string TrackingItemName { get; set; } = "No item";

    [ObservableProperty]
    public partial string TrackingItemRemaining { get; set; } = "0:00";

    [ObservableProperty]
    public partial string TrackingItemElapsed { get; set; } = "+0:00";

    [ObservableProperty]
    public partial Brush TrackingItemBrush { get; set; } = Brushes.White;

    /// <summary>"late by 2:10" once the item has run over.</summary>
    [ObservableProperty]
    public partial string TrackingLate { get; set; } = "";

    [ObservableProperty]
    public partial string TrackingSection { get; set; } = "";

    [ObservableProperty]
    public partial Brush TrackingSectionBrush { get; set; } = Brushes.White;

    [ObservableProperty]
    public partial string TrackingService { get; set; } = "";

    [ObservableProperty]
    public partial Brush TrackingServiceBrush { get; set; } = Brushes.White;

    [ObservableProperty]
    public partial string TrackingNext { get; set; } = "";

    /// <summary>What the panel says under the timers: how they're timed, or how to start them.</summary>
    [ObservableProperty]
    public partial string TrackingNote { get; set; } = "";

    [ObservableProperty]
    public partial bool IsTrackingService { get; set; }

    private void UpdateTrackingPanel(Service? service, IReadOnlyList<ServiceItem> rows, DateTimeOffset now)
    {
        TimerSnapshot? Face(ServiceTrackingTimers.Subject subject, ServiceTrackingTimers.Face face) =>
            Timers.Snapshot(ServiceTrackingTimers.Id(subject, face));

        TrackingItemName = ServiceTiming.CurrentRow(service)?.Name ?? "No item";
        var remaining = Face(ServiceTrackingTimers.Subject.Item, ServiceTrackingTimers.Face.Remaining);
        var elapsed = Face(ServiceTrackingTimers.Subject.Item, ServiceTrackingTimers.Face.Elapsed);
        TrackingItemRemaining = remaining?.DisplayString(now) ?? "0:00";
        TrackingItemElapsed = "+" + (elapsed?.DisplayString(now) ?? "0:00");
        TrackingItemBrush = UrgencyBrush(remaining, now);
        TrackingLate = remaining is { IsRunning: true } r && r.Value(now) < 0 ? $"late by {TimerSnapshot.Timecode(-r.Value(now))}" : "";

        string Span(ServiceTrackingTimers.Subject subject) =>
            $"+{Face(subject, ServiceTrackingTimers.Face.Elapsed)?.DisplayString(now) ?? "0:00"}   {Face(subject, ServiceTrackingTimers.Face.Remaining)?.DisplayString(now) ?? "0:00"}";
        TrackingSection = Span(ServiceTrackingTimers.Subject.Section);
        TrackingSectionBrush = UrgencyBrush(Face(ServiceTrackingTimers.Subject.Section, ServiceTrackingTimers.Face.Remaining), now);
        TrackingService = Span(ServiceTrackingTimers.Subject.Service);
        TrackingServiceBrush = UrgencyBrush(Face(ServiceTrackingTimers.Subject.Service, ServiceTrackingTimers.Face.Remaining), now);

        var next = ServiceTiming.NextRow(rows);
        TrackingNext = next is null ? "" : $"Next · {next.Name}" + (next.Duration is { } length and > 0 ? $"   {TimerSnapshot.Timecode(length)}" : "");
        IsTrackingService = ServiceTiming.IsTiming(service);
        TrackingNote = IsTrackingService
            ? "Timed on this computer from your fires. The timers stop when the planned service would end (later if an item runs over)."
            : service is null ? "Select a service to track it."
            : "Fire the first item to start tracking.";
    }

    private static Brush UrgencyBrush(TimerSnapshot? timer, DateTimeOffset now) => timer?.Urgency(now) switch
    {
        TimerUrgency.Overrun => Painting.Brush(TimerWarning.RedHex, Core.Scene.SceneColor.White),
        TimerUrgency.Warning => Painting.Brush(TimerWarning.AmberHex, Core.Scene.SceneColor.White),
        _ => Brushes.White,
    };
}
