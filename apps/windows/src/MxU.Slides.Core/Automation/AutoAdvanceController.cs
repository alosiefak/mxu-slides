using MxU.Slides.Core.Media;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Scene;
using MxU.Slides.Core.Threading;

namespace MxU.Slides.Core.Automation;

/// <summary>What auto-advance needs from the live show. The app implements it over its show, library and video players.</summary>
public interface IAutoAdvanceHost
{
    /// <summary>The live show: its slide, the slide's builds and their clock.</summary>
    ShowState Show { get; }

    /// <summary>The show's host clock now, in seconds (<see cref="ShowState.HostClock"/> in the app).</summary>
    double HostNow { get; }

    /// <summary>The service row the live slide came from, or null.</summary>
    string? LiveContextId { get; }

    /// <summary>The live slide's position in its arranged slides (a slide can play twice in an arrangement).</summary>
    int? LiveOccurrence { get; }

    MediaItem? Media(string id);

    /// <summary>The live player for a media item, read now, in pass terms; null when no player is open.</summary>
    MediaTransportState? Transport(string mediaId);

    /// <summary>The media item is on one of the media layers.</summary>
    bool IsMediaLive(string mediaId);

    /// <summary>Takes a slide live, exactly as Next does.</summary>
    void FireSlide(Slide slide, Presentation presentation, string? arrangementId, string? contextId, int occurrence);

    /// <summary>The current service has a row with this id.</summary>
    bool ServiceContains(string itemId);

    /// <summary>Fires the row after <paramref name="itemId"/> that fires by itself (<see cref="Library.ServiceRunOrder.NextFireable"/>).</summary>
    void FireNextServiceItem(string itemId);
}

/// <summary>
/// Auto-advance: a slide that moves on by itself after N seconds (or after its video plays), a
/// "Slide Show" presentation that loops back to its first slide, and a media service row that
/// fires the next row when its video ends. Port of the auto-advance half of the Mac's
/// Sources/ServiceControls.swift (armAutoAdvance, autoAdvancePollTick, fireAutoAdvance,
/// armMediaAdvance and their friends), with the same names where they fit.
/// <para>
/// How a timer is interrupted, as on the Mac:
/// <list type="bullet">
/// <item>Any slide landing (Next, Previous, a click, a hot key, the timer itself) cancels both
/// timers and arms the new slide's own (<see cref="SlideLanded"/>).</item>
/// <item>A slide with builds waits for the operator: the timer starts only once every click (and
/// the exit press) has played (<see cref="StepsChanged"/>). Taking a build back stops it again.</item>
/// <item>Clearing the slide cancels it; firing other media or Clear All cancels a media row's timer.</item>
/// <item>Holding (<see cref="SetPaused"/>) keeps the time left; releasing restarts from there. A
/// video's end that passes while held is skipped, and the slide stays.</item>
/// <item>A looping video counts one pass from when the slide went live. A video whose player
/// never opens doesn't strand the show: after 10 seconds it counts as ended.</item>
/// </list>
/// </para>
/// Drive it from the UI thread; its timers post back to <c>ui</c>.
/// </summary>
public sealed class AutoAdvanceController(IAutoAdvanceHost host, TimeProvider? clock = null, SynchronizationContext? ui = null)
{
    /// <summary>How long a missing player is waited for before its video counts as ended.</summary>
    public const double WatchGraceSeconds = 10;

    /// <summary>How often a playing video is checked (the app also checks the moment a video ends).</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(0.25);

    private readonly TimeProvider clock = clock ?? ShowTimeProvider.Shared;

    /// <summary>What the chip shows: a time it fires at, or a video it waits for (plus the delay after it).</summary>
    public sealed record AutoAdvancePending(DateTimeOffset? FiresAt = null, string? MediaId = null, double DelaySeconds = 0);

    private abstract record TimedContext;
    private sealed record SlideContext(string SlideId) : TimedContext;
    private sealed record MediaItemContext(string ServiceItemId, string MediaId) : TimedContext;

    private sealed record Watch(string Id, string MediaId, DateTimeOffset ArmedAt);

    // The slide's timers.
    private ShowTimer? autoAdvanceTimer;
    private DateTimeOffset autoAdvanceFiresAt;
    private ShowTimer? autoAdvancePoll;
    private Watch? autoAdvanceWatch;
    private string? autoAdvanceAwaitingSteps;
    private ShowTimer? autoAdvanceArmTimer;

    // A media service row's timers.
    private ShowTimer? mediaAdvanceTimer;
    private DateTimeOffset mediaAdvanceFiresAt;
    private ShowTimer? mediaAdvancePoll;
    private Watch? mediaAdvanceWatch;

    private TimedContext? advanceTimedContext;
    private double? heldRemaining;

    // What the slide's own timer shows, so the chip can go back to it when a media row's timer stops.
    private AutoAdvancePending? slidePending;
    private TimedContext? slideTimedContext;
    private (AutoAdvancePending? Pending, bool Paused) published;

    /// <summary>Raised when <see cref="Pending"/> or <see cref="IsPaused"/> changes (the chip redraws).</summary>
    public event Action? Changed;

    /// <summary>The diagnostics trail: (crumb, detail).</summary>
    public event Action<string, string>? Noted;

    /// <summary>An advance is coming (the chip shows), or null.</summary>
    public AutoAdvancePending? Pending { get; private set; }

    /// <summary>The operator is holding it: the room stays where it is.</summary>
    public bool IsPaused { get; private set; }

    private DateTimeOffset Now => clock.GetUtcNow();

    private void Note(string crumb, string detail) => Noted?.Invoke(crumb, detail);

    /// <summary>
    /// Seconds until it advances, for the chip: the time held, the time left on the timer, or
    /// what's left of the video plus the delay after it. Null when it can't tell yet.
    /// </summary>
    public double? Remaining(DateTimeOffset? at = null)
    {
        if (Pending is not { } pending) return null;
        var date = at ?? Now;
        if (IsPaused && heldRemaining is { } held) return held;
        if (pending.FiresAt is { } firesAt) return Math.Max((firesAt - date).TotalSeconds, 0);
        if (pending.MediaId is { } mediaId && host.Transport(mediaId) is { Duration: > 0 } transport)
        {
            var remaining = (transport.Duration - transport.PositionAt(date)) / Math.Max(transport.Rate, 0.01);
            return Math.Max(remaining + pending.DelaySeconds, 0);
        }
        return null;
    }

    /// <summary>Holds the advance (the chip's click), or lets it go again from the time that was left.</summary>
    public void SetPaused(bool paused)
    {
        if (paused == IsPaused || Pending is null) return;
        IsPaused = paused;
        if (paused)
        {
            if (autoAdvanceTimer is not null) heldRemaining = Math.Max((autoAdvanceFiresAt - Now).TotalSeconds, 0);
            else if (mediaAdvanceTimer is not null) heldRemaining = Math.Max((mediaAdvanceFiresAt - Now).TotalSeconds, 0);
            Stop(ref autoAdvanceTimer);
            Stop(ref mediaAdvanceTimer);
            Note("autoAdvance.hold", heldRemaining is { } left ? $"{left:F1}s left" : "waiting for video");
        }
        else if (heldRemaining is { } held)
        {
            heldRemaining = null;
            switch (advanceTimedContext)
            {
                case SlideContext slide:
                    ScheduleAutoAdvanceDelay(held, slide.SlideId);
                    break;
                case MediaItemContext media:
                    ScheduleMediaAdvanceDelay(held, media.ServiceItemId, media.MediaId);
                    break;
            }
            Note("autoAdvance.resume", $"{held:F1}s left");
        }
        Publish();
    }

    // MARK: Slides

    /// <summary>
    /// A slide just landed (Next, Previous, a click, the timer itself): both timers stop and this
    /// slide's own starts, or waits for its builds. Port of armAutoAdvance.
    /// </summary>
    public void SlideLanded(Slide slide, Presentation? presentation)
    {
        CancelAutoAdvance();
        CancelMediaAdvance();
        if (SlideSceneBuilder.EffectiveAutoAdvance(slide, presentation) is null) return;
        if (host.Show.SlideAdvanceCount > 0)
        {
            autoAdvanceAwaitingSteps = slide.Id;
            return;
        }
        ArmAutoAdvanceTimers(slide, presentation);
    }

    /// <summary>
    /// Next or Previous played or took back a build on the live slide. Once every build has been
    /// pressed and has played, the timer starts; taking one back stops it. Port of syncAutoAdvanceArming.
    /// </summary>
    public void StepsChanged()
    {
        var waitingId = autoAdvanceAwaitingSteps ?? (advanceTimedContext as SlideContext)?.SlideId;
        if (waitingId is null || host.Show.Live is not { } live || live.Slide.Id != waitingId || host.Show.SlideAdvanceCount <= 0
            || SlideSceneBuilder.EffectiveAutoAdvance(live.Slide, live.Presentation) is null)
            return;
        Stop(ref autoAdvanceArmTimer);
        if (host.Show.SlideStepsCompletedAt() is not { } completedAt)
        {
            // A build was taken back: nothing runs until the operator presses again.
            Stop(ref autoAdvanceTimer);
            Stop(ref autoAdvancePoll);
            autoAdvanceWatch = null;
            ClearAdvanceVisibility();
            autoAdvanceAwaitingSteps = live.Slide.Id;
            Publish();
            return;
        }
        autoAdvanceAwaitingSteps = null;
        var remaining = completedAt - host.HostNow;
        if (remaining <= 0)
        {
            ArmAutoAdvanceTimers(live.Slide, live.Presentation);
            return;
        }
        // The last build is still playing: start counting once it has finished.
        var slideId = live.Slide.Id;
        autoAdvanceArmTimer = After(TimeSpan.FromSeconds(remaining), () => autoAdvanceArmTimer, () =>
        {
            if (host.Show.Live is not { } now || now.Slide.Id != slideId || host.Show.SlideStepsCompletedAt() is null) return;
            autoAdvanceArmTimer = null;
            ArmAutoAdvanceTimers(now.Slide, now.Presentation);
        });
    }

    /// <summary>The live slide went (cleared, or its Outs finished): its timer stops.</summary>
    public void Cancel()
    {
        CancelAutoAdvance();
        Publish();
    }

    /// <summary>A slide waits for a look's Outs before landing: nothing advances meanwhile (the Mac's deferring).</summary>
    public void HandingOff()
    {
        CancelAutoAdvance();
        CancelMediaAdvance();
        Publish();
    }

    /// <summary>The live slide's presentation was edited: a slide that wasn't counting starts, with the edited timer.</summary>
    public void PresentationEdited(string presentationId)
    {
        if (autoAdvanceTimer is not null || autoAdvancePoll is not null) return;
        if (host.Show.Live is not { } live || live.Presentation?.Id != presentationId) return;
        SlideLanded(live.Slide, live.Presentation);
        Publish();
    }

    private void ArmAutoAdvanceTimers(Slide slide, Presentation? presentation)
    {
        if (SlideSceneBuilder.EffectiveAutoAdvance(slide, presentation) is not { } advance) return;
        if (advance.AfterPlayback == true && AutoAdvanceMediaId(slide) is { } mediaId)
        {
            autoAdvanceWatch = new Watch(slide.Id, mediaId, Now);
            Pending = slidePending = new AutoAdvancePending(MediaId: mediaId, DelaySeconds: advance.DelaySeconds);
            slideTimedContext = null;
            Stop(ref autoAdvancePoll);
            autoAdvancePoll = Every(PollInterval, () => autoAdvancePoll, AutoAdvancePollTick);
            Note("autoAdvance.watch", $"{slide.Name}: after {mediaId}, {advance.DelaySeconds:F1}s");
            Publish();
            return;
        }
        ScheduleAutoAdvanceDelay(advance.DelaySeconds, slide.Id);
    }

    private void ScheduleAutoAdvanceDelay(double seconds, string slideId)
    {
        // Never zero: a slide always shows for at least a moment.
        var delay = Math.Max(seconds, 0.1);
        Stop(ref autoAdvanceTimer);
        autoAdvanceFiresAt = Now.AddSeconds(delay);
        autoAdvanceTimer = After(TimeSpan.FromSeconds(delay), () => autoAdvanceTimer, () => FireAutoAdvance(slideId));
        Note("autoAdvance.arm", $"{slideId}: {delay:F1}s");
        Pending = slidePending = new AutoAdvancePending(FiresAt: autoAdvanceFiresAt);
        advanceTimedContext = slideTimedContext = new SlideContext(slideId);
        Publish();
    }

    /// <summary>The video a slide waits for: its own foreground video, else a video filling one of its objects.</summary>
    private string? AutoAdvanceMediaId(Slide slide)
    {
        if (slide.Background is { } background && background.MediaId.Length > 0 && IsForegroundVideo(background))
            return background.MediaId;
        foreach (var o in slide.Objects)
            if (SlideSceneBuilder.FillMediaId(o) is { } id && host.Media(id) is { MediaKind: MediaKind.Video })
                return id;
        return null;
    }

    private bool IsForegroundVideo(CueMedia media)
    {
        if (media.Layer is { } layer) return layer == CueMediaLayer.Videos;
        return host.Media(media.MediaId) is { MediaKind: MediaKind.Video } item && item.Classification != MediaClassification.Background;
    }

    private void AutoAdvancePollTick()
    {
        if (autoAdvanceWatch is not { } watch || host.Show.Live is not { } live || live.Slide.Id != watch.Id
            || SlideSceneBuilder.EffectiveAutoAdvance(live.Slide, live.Presentation) is not { } advance)
        {
            CancelAutoAdvance();
            Publish();
            return;
        }
        if (!Ended(watch, advance.DelaySeconds)) return;
        if (IsPaused)
        {
            // Held through the video's end: the end is skipped and the slide stays.
            CancelAutoAdvance();
            Publish();
            return;
        }
        Stop(ref autoAdvancePoll);
        autoAdvanceWatch = null;
        if (advance.DelaySeconds <= 0) FireAutoAdvance(watch.Id);
        else ScheduleAutoAdvanceDelay(advance.DelaySeconds, watch.Id);
    }

    /// <summary>
    /// The watched video has ended, or is within the lead of its end (a negative delay fires the
    /// next cue that early). A loop counts one pass from when it was armed; a player that never
    /// opened counts as ended after the grace.
    /// </summary>
    private bool Ended(Watch watch, double delaySeconds)
    {
        var now = Now;
        var lead = MediaAutoAdvance.LeadSeconds(delaySeconds);
        if (host.Transport(watch.MediaId) is { } transport)
        {
            if (lead > 0 && !transport.IsLooping && transport.Duration > 0)
            {
                var remaining = (transport.Duration - transport.PositionAt(now)) / Math.Max(transport.Rate, 0.01);
                return MediaAutoAdvance.ShouldPreFire(remaining, delaySeconds);
            }
            return transport.IsFinished(now)
                || transport.IsLooping && transport.Duration > 0 && (now - watch.ArmedAt).TotalSeconds >= transport.Duration - lead;
        }
        return (now - watch.ArmedAt).TotalSeconds > WatchGraceSeconds;
    }

    private void FireAutoAdvance(string slideId)
    {
        autoAdvanceTimer = null;
        ClearAdvanceVisibility();
        Publish();
        if (host.Show.Live is not { } live || live.Slide.Id != slideId || live.Presentation is not { } presentation) return;
        if (host.Show.SlideAdvanceCount > 0 && host.Show.SlideStepsCompletedAt() is null)
        {
            autoAdvanceAwaitingSteps = slideId;
            return;
        }
        var slides = SlideSceneBuilder.ArrangedSlides(presentation, live.ArrangementId);
        var current = host.LiveOccurrence ?? slides.FindIndex(s => s.Id == slideId);
        if (current < 0) return;
        var loops = SlideSceneBuilder.EffectiveAutoAdvance(live.Slide, presentation)?.LoopToStart ?? false;
        if (SlideSceneBuilder.AutoAdvanceTarget(current, slides.Count, loops) is { } next && next < slides.Count)
        {
            Note("autoAdvance.fire", $"{presentation.Name} › {next + 1} of {slides.Count}");
            host.FireSlide(slides[next], presentation, live.ArrangementId, host.LiveContextId, next);
            return;
        }
        // The last slide: the service moves on to its next row.
        if (host.LiveContextId is not { } contextId || !host.ServiceContains(contextId)) return;
        Note("autoAdvance.nextItem", $"after {presentation.Name}");
        host.FireNextServiceItem(contextId);
    }

    // MARK: Media service rows

    /// <summary>A media service row just fired: its video's auto-advance (after N seconds, or after playback) starts. Port of armMediaAdvance.</summary>
    public void MediaFired(MediaItem item, string serviceItemId)
    {
        CancelMediaAdvance();
        if (item.AutoAdvance is not { } advance)
        {
            Publish();
            return;
        }
        if (advance.AfterPlayback == true && item.MediaKind == MediaKind.Video)
        {
            mediaAdvanceWatch = new Watch(serviceItemId, item.Id, Now);
            Pending = new AutoAdvancePending(MediaId: item.Id, DelaySeconds: advance.DelaySeconds);
            advanceTimedContext = new MediaItemContext(serviceItemId, item.Id);
            Stop(ref mediaAdvancePoll);
            mediaAdvancePoll = Every(PollInterval, () => mediaAdvancePoll, MediaAdvancePollTick);
            Note("mediaAdvance.watch", $"{item.Name}, {advance.DelaySeconds:F1}s");
            Publish();
            return;
        }
        ScheduleMediaAdvanceDelay(advance.DelaySeconds, serviceItemId, item.Id);
    }

    /// <summary>Media fired some other way (the bin, an action) or Clear All: a media row's timer stops.</summary>
    public void CancelMedia()
    {
        CancelMediaAdvance();
        Publish();
    }

    private void ScheduleMediaAdvanceDelay(double seconds, string serviceItemId, string mediaId)
    {
        var delay = Math.Max(seconds, 0.1);
        Stop(ref mediaAdvanceTimer);
        mediaAdvanceFiresAt = Now.AddSeconds(delay);
        mediaAdvanceTimer = After(TimeSpan.FromSeconds(delay), () => mediaAdvanceTimer, () => FireMediaAdvance(serviceItemId, mediaId));
        Pending = new AutoAdvancePending(FiresAt: mediaAdvanceFiresAt);
        advanceTimedContext = new MediaItemContext(serviceItemId, mediaId);
        Publish();
    }

    private void MediaAdvancePollTick()
    {
        if (mediaAdvanceWatch is not { } watch || !host.IsMediaLive(watch.MediaId) || host.Media(watch.MediaId)?.AutoAdvance is not { } advance)
        {
            CancelMediaAdvance();
            Publish();
            return;
        }
        if (!Ended(watch, advance.DelaySeconds)) return;
        if (IsPaused)
        {
            CancelMediaAdvance();
            Publish();
            return;
        }
        Stop(ref mediaAdvancePoll);
        mediaAdvanceWatch = null;
        if (advance.DelaySeconds <= 0) FireMediaAdvance(watch.Id, watch.MediaId);
        else ScheduleMediaAdvanceDelay(advance.DelaySeconds, watch.Id, watch.MediaId);
    }

    private void FireMediaAdvance(string serviceItemId, string mediaId)
    {
        mediaAdvanceTimer = null;
        ClearAdvanceVisibility();
        Publish();
        if (!host.IsMediaLive(mediaId) || !host.ServiceContains(serviceItemId)) return;
        Note("mediaAdvance.fire", $"after {mediaId}");
        host.FireNextServiceItem(serviceItemId);
    }

    /// <summary>Checks the watched videos now (the app calls this the moment a video ends, rather than wait for the next poll).</summary>
    public void PollNow()
    {
        if (autoAdvancePoll is not null) AutoAdvancePollTick();
        if (mediaAdvancePoll is not null) MediaAdvancePollTick();
    }

    // MARK: Bookkeeping

    private void CancelAutoAdvance()
    {
        Stop(ref autoAdvanceTimer);
        Stop(ref autoAdvancePoll);
        autoAdvanceWatch = null;
        Stop(ref autoAdvanceArmTimer);
        autoAdvanceAwaitingSteps = null;
        slidePending = null;
        slideTimedContext = null;
        // A media row's timer keeps its chip (the Mac clears it here, though that timer runs on).
        if (advanceTimedContext is not MediaItemContext) ClearAdvanceVisibility();
    }

    private void CancelMediaAdvance()
    {
        Stop(ref mediaAdvanceTimer);
        Stop(ref mediaAdvancePoll);
        mediaAdvanceWatch = null;
        // The Mac clears the chip here even while a slide's timer still runs; Windows shows the
        // running slide timer again instead (the timer itself is untouched on both).
        if (autoAdvanceTimer is null && autoAdvancePoll is null)
        {
            ClearAdvanceVisibility();
        }
        else if (advanceTimedContext is MediaItemContext)
        {
            Pending = slidePending;
            advanceTimedContext = slideTimedContext;
            IsPaused = false;
            heldRemaining = null;
        }
    }

    private void ClearAdvanceVisibility()
    {
        Pending = null;
        IsPaused = false;
        heldRemaining = null;
        advanceTimedContext = null;
        if (autoAdvanceTimer is null && autoAdvancePoll is null)
        {
            slidePending = null;
            slideTimedContext = null;
        }
    }

    private void Publish()
    {
        var now = (Pending, IsPaused);
        if (now == published) return;
        published = now;
        Changed?.Invoke();
    }

    private static void Stop(ref ShowTimer? timer)
    {
        timer?.Dispose();
        timer = null;
    }

    /// <summary>
    /// A one-shot that only acts while it's still the timer in its slot (<paramref name="current"/>).
    /// A timer stopped just as its time came already has its callback on the way to the UI thread;
    /// without this, a hold or the next slide could be overtaken by the old slide's timer.
    /// </summary>
    private ShowTimer After(TimeSpan delay, Func<ShowTimer?> current, Action callback)
    {
        ShowTimer? made = null;
        made = ShowTimer.After(clock, ui, delay, () =>
        {
            if (ReferenceEquals(current(), made)) callback();
        });
        return made;
    }

    /// <summary>A repeating poll that likewise stops acting the moment it's replaced or stopped.</summary>
    private ShowTimer Every(TimeSpan period, Func<ShowTimer?> current, Action callback)
    {
        ShowTimer? made = null;
        made = ShowTimer.Every(clock, ui, period, () =>
        {
            if (ReferenceEquals(current(), made)) callback();
        });
        return made;
    }
}
