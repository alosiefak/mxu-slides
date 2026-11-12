using System.Collections.ObjectModel;
using MxU.Slides.Core.Media;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Stage;

// The live data a stage display (confidence monitor) layout reads. Port of
// CueAlert, VideoCountdown and ConfidenceInfo in SlideScene/ConfidenceScene.swift.

public sealed record CueAlert(string Message, AlertBehavior Behavior, AlertTarget Target = AlertTarget.Confidence)
{
    public string Id { get; init; } = StageIds.New();

    /// <summary>The layer the alert renders on, as its raw name ("alerts", "overlays"…); null = the default.</summary>
    public string? Layer { get; init; }

    public Theme? Theme { get; init; }

    public bool ShowsOnConfidence => Target != AlertTarget.Audience;

    public bool ShowsOnAudience => Target != AlertTarget.Confidence;
}

/// <summary>A playing (or parked) video or audio file: <see cref="Position"/> was read at <see cref="AnchoredAt"/>.</summary>
public sealed record VideoCountdown(string Name, double Duration, double Position, DateTimeOffset AnchoredAt, bool IsPlaying)
{
    /// <summary>The video's marks in pass time (<see cref="VideoMarks.Points"/>), for counting to a mark.</summary>
    public IReadOnlyList<VideoMarks.Point> Marks { get; init; } = [];

    public double Remaining(DateTimeOffset at) => Math.Max(0, Duration - PositionAt(at));

    public double PositionAt(DateTimeOffset at)
    {
        var advance = IsPlaying ? Math.Max(0, (at - AnchoredAt).TotalSeconds) : 0;
        return Math.Min(Duration, Position + advance);
    }

    /// <summary>The mark a countdown with this rule counts to now, or null for the end.</summary>
    public VideoMarks.Point? Target(DateTimeOffset at, VideoCountdownTarget target, string? name) =>
        VideoMarks.Target(Marks, PositionAt(at), target, name);

    /// <summary>
    /// What a video countdown reads: the seconds left to its target (a mark, else the end), or,
    /// counting up, the seconds since the mark before under the same rule (the top for the end).
    /// </summary>
    public double Seconds(DateTimeOffset at, VideoCountdownTarget target, string? name, bool countsUp)
    {
        var now = PositionAt(at);
        var end = Target(at, target, name)?.At ?? Duration;
        if (!countsUp) return Math.Max(0, end - now);
        return Math.Max(0, Math.Min(now, end) - VideoMarks.CountUpStart(Marks, now, target, name));
    }

    // The marks are compared by value, so republishing an unchanged countdown changes nothing.
    public bool Equals(VideoCountdown? other) =>
        other is not null && Name == other.Name && Duration == other.Duration && Position == other.Position
        && AnchoredAt == other.AnchoredAt && IsPlaying == other.IsPlaying && Marks.SequenceEqual(other.Marks);

    public override int GetHashCode() => HashCode.Combine(Name, Duration, Position, AnchoredAt, IsPlaying, Marks.Count);
}

public sealed record ObjectText(string Name, string Text);

public readonly record struct SlidePosition(int Index, int Total);

/// <summary>One slide's words as the stage display reads them (ConfidenceInfo.SlideText in Swift).</summary>
public sealed record SlideText(string Body)
{
    public IReadOnlyList<ChordPlacement> Chords { get; init; } = [];
    public string? MusicKey { get; init; }
    public string? DisplayKey { get; init; }
    public int? StepIndex { get; init; }
    public int? StepCount { get; init; }

    /// <summary>Named, text-carrying objects in document order, for "pull one object" links.</summary>
    public IReadOnlyList<ObjectText>? ObjectTexts { get; init; }
}

public sealed record ConfidenceInfo
{
    public SlideText? Current { get; init; }
    public SlideText? Next { get; init; }

    /// <summary>The next animation step of the live slide, read by next-slide links that include steps.</summary>
    public SlideText? NextStep { get; init; }

    public SlideText? Last { get; init; }
    public CueAlert? Alert { get; init; }

    /// <summary>False on a flashing alert's off beat.</summary>
    public bool AlertVisible { get; init; } = true;

    public IReadOnlyList<TimerSnapshot> Timers { get; init; } = [];

    /// <summary>The foreground video, read when a link names no layer.</summary>
    public VideoCountdown? VideoCountdown { get; init; }

    /// <summary>Per-layer playback keyed by the layer's raw name ("videos", "loopingVideos", "slide", "overlays").</summary>
    public IReadOnlyDictionary<string, VideoCountdown> VideoCountdowns { get; init; } = ReadOnlyDictionary<string, VideoCountdown>.Empty;

    public VideoCountdown? VideoCountdownFor(string? layer) =>
        string.IsNullOrEmpty(layer) ? VideoCountdown : VideoCountdowns.GetValueOrDefault(layer);

    public SlidePosition? SlidePosition { get; init; }
    public string? CurrentItemName { get; init; }
    public string? CurrentGroupName { get; init; }
    public string? NextItemName { get; init; }
    public string? CurrentPresentationName { get; init; }
    public string? NextServiceTitle { get; init; }
    public VideoCountdown? AudioCountdown { get; init; }
    public TimecodeDisplay? Timecode { get; init; }
    public IReadOnlyList<string> ActiveLiveInputIds { get; init; } = [];
    public IReadOnlyList<string> ConnectedLiveInputIds { get; init; } = [];
    public bool CaptureActive { get; init; }
}
