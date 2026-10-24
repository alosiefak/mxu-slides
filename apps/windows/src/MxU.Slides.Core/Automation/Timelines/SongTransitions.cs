using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Automation;

/// <summary>
/// How one song hands over to the next item in the service when its timeline reaches
/// the end: stop, fade out, start the next one early (overlap), or crossfade. A
/// <see cref="Plan"/> is the handover in the song's own seconds. Port of
/// PresenterCore/Timeline/SongTransitions.swift.
/// </summary>
public static class SongTransitions
{
    public enum Kind { Stop, Fade, Overlap, Crossfade }

    public static string Raw(this Kind kind) => kind.ToString().ToLowerInvariant();

    public const double DefaultSeconds = 5.0;

    /// <summary>A service item's transition; absent or unknown is Stop (play to the end and stop).</summary>
    public static Kind KindOf(SongTransition? transition) => transition?.Kind switch
    {
        "fade" => Kind.Fade,
        "overlap" => Kind.Overlap,
        "crossfade" => Kind.Crossfade,
        _ => Kind.Stop,
    };

    /// <summary>
    /// The handover's length: the typed seconds, else five; an overlap defaults to the
    /// shorter of five, this song's last section and the next song's first.
    /// </summary>
    public static double Length(SongTransition? transition, double? lastSection = null, double? nextFirstSection = null)
    {
        if (transition?.Seconds is > 0 and var seconds) return seconds;
        if (KindOf(transition) != Kind.Overlap) return DefaultSeconds;
        return new double?[] { DefaultSeconds, lastSection, nextFirstSection }.OfType<double>().Where(s => s > 0).Min();
    }

    /// <summary>Playing another song by hand always crossfades, over the typed seconds or five.</summary>
    public static double ManualCrossfade(SongTransition? transition) => transition?.Seconds is > 0 and var seconds ? seconds : DefaultSeconds;

    /// <summary>
    /// A handover: the next item starts at <see cref="NextAt"/>; this song's music fades from
    /// <see cref="FadeFrom"/> over <see cref="FadeSeconds"/>; the next fades in over
    /// <see cref="FadeInSeconds"/> (crossfade only); <see cref="End"/> is this song's end.
    /// </summary>
    public sealed record Plan(Kind Kind, double NextAt, double? FadeFrom, double FadeSeconds, double FadeInSeconds, double End);

    public static Plan? PlanFor(Kind kind, double end, double length)
    {
        length = Math.Min(Math.Max(length, 0), Math.Max(end, 0));
        return kind switch
        {
            Kind.Fade => new Plan(kind, end, end - length, length, 0, end),
            Kind.Overlap => new Plan(kind, end - length, null, 0, 0, end),
            Kind.Crossfade => new Plan(kind, end - length, end - length, length, length, end),
            _ => null,
        };
    }

    /// <summary>The plan for a service item's transition, read from its timeline (and the next song's, for an overlap).</summary>
    public static Plan? PlanFor(
        SongTransition? transition, PresentationTimeline timeline, IReadOnlyList<TimelinePlayedBlock> blocks,
        (PresentationTimeline Timeline, IReadOnlyList<TimelinePlayedBlock> Blocks)? next = null)
    {
        var end = PresentationTimelines.Length(timeline);
        var last = PresentationTimelines.SectionSpans(timeline.Cues, blocks, end).LastOrDefault();
        var first = next is { } n
            ? PresentationTimelines.SectionSpans(n.Timeline.Cues, n.Blocks, PresentationTimelines.Length(n.Timeline)).FirstOrDefault()
            : null;
        var length = Length(transition, last is null ? null : last.End - last.Start, first is null ? null : first.End - first.Start);
        return PlanFor(KindOf(transition), end, length);
    }

    /// <summary>The two songs' music levels at a time, on the same curves the players use.</summary>
    public static (double Outgoing, double Incoming) Levels(Plan plan, double time)
    {
        var outgoing = time >= plan.End ? 0.0 : 1.0;
        if (plan.FadeFrom is { } fadeFrom && time > fadeFrom && time < plan.End)
        {
            var t = Math.Min((time - fadeFrom) / Math.Max(plan.FadeSeconds, 0.001), 1);
            outgoing = plan.FadeInSeconds > 0 ? Math.Cos(t * Math.PI / 2) : 0.5 * (1 + Math.Cos(t * Math.PI));
        }
        var incoming = time >= plan.NextAt ? 1.0 : 0.0;
        if (plan.FadeInSeconds > 0 && time >= plan.NextAt)
            incoming = Math.Sin(Math.Min((time - plan.NextAt) / plan.FadeInSeconds, 1) * Math.PI / 2);
        return (outgoing, incoming);
    }

    /// <summary>A manual jump's crossfade, starting now.</summary>
    public static Plan ManualPlan(double position, double seconds) =>
        new(Kind.Crossfade, position, position, seconds, seconds, position + seconds);

    /// <summary>A jump past the handover's start starts it at once, shortened to what is left; before it, null.</summary>
    public static Plan? Landing(Plan plan, double position)
    {
        var first = Math.Min(plan.NextAt, plan.FadeFrom ?? plan.NextAt);
        if (position < first) return null;
        var left = Math.Max(plan.End - position, 0);
        var started = plan with { NextAt = Math.Max(plan.NextAt, position) };
        if (plan.FadeFrom is not null) started = started with { FadeFrom = position, FadeSeconds = left };
        if (plan.FadeInSeconds > 0) started = started with { FadeInSeconds = Math.Max(plan.End - started.NextAt, 0) };
        return started;
    }

    /// <summary>Normal play crosses the handover once.</summary>
    public static bool Due(Plan plan, double from, double to) => from < plan.NextAt && to >= plan.NextAt;

    /// <summary>A tick of an armed go: keep playing (up to the handover), or hand over now.</summary>
    public abstract record Step
    {
        private Step() { }

        public sealed record Play(double To) : Step;

        public sealed record HandOver(double At) : Step;
    }

    /// <summary>
    /// Hands over on the tick that reaches the handover, or when the tracks player says it
    /// has; while the player is to do it, waits up to <paramref name="grace"/> seconds for it.
    /// </summary>
    public static Step StepFor(Plan plan, double position, bool inPlayer, bool playerHandedOver, double grace = 0.5)
    {
        if (playerHandedOver) return new Step.HandOver(Math.Min(position, plan.NextAt));
        if (position >= plan.NextAt && (!inPlayer || position >= plan.NextAt + grace)) return new Step.HandOver(plan.NextAt);
        return new Step.Play(Math.Min(position, plan.NextAt));
    }

    /// <summary>What the next item does when handed to: play its timeline, fire (its first slide or its media), or nothing.</summary>
    public enum NextStart { PlayTimeline, Fire, Nothing }

    public static NextStart NextStartFor(ServiceItem? item, bool hasTimeline)
    {
        if (item is null) return NextStart.Nothing;
        return item.ItemKind == ServiceItemKind.Presentation && hasTimeline ? NextStart.PlayTimeline : NextStart.Fire;
    }

    /// <summary>Only playing another song by hand, while stems sound, crossfades (a go's own handover doesn't).</summary>
    public static bool ManualJumpCrossfades(bool anotherSong, bool playing, bool stemsSounding, bool handingOver) =>
        anotherSong && playing && stemsSounding && !handingOver;

    /// <summary>A go is armed only on the song's own clock (timecode decides otherwise), not while recording or after Cancel.</summary>
    public static bool Arms(Kind kind, bool ownClock, bool recording, bool cancelled) =>
        kind != Kind.Stop && ownClock && !recording && !cancelled;
}
