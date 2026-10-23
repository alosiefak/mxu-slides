using System.Globalization;
using System.Text.Json.Serialization;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Stage;

// Timer values as the stage display reads them. Port of TimerWarning and
// TimerSnapshot in SlideScene/ConfidenceScene.swift.

/// <summary>A colour the timer turns once its remaining time drops to <see cref="RemainingSeconds"/>.</summary>
public readonly record struct TimerWarning(
    [property: JsonPropertyName("remainingSeconds")] double RemainingSeconds,
    [property: JsonPropertyName("colorHex")] string ColorHex)
{
    /// <summary>Spelled like Swift's "\(remainingSeconds)-\(colorHex)", e.g. "30.0-#FFB826FF".</summary>
    [JsonIgnore]
    public string Id
    {
        get
        {
            var seconds = RemainingSeconds.ToString(CultureInfo.InvariantCulture);
            if (double.IsFinite(RemainingSeconds) && !seconds.Contains('.') && !seconds.Contains('E')) seconds += ".0";
            return $"{seconds}-{ColorHex}";
        }
    }

    public const string AmberHex = "#FFB826FF";
    public const string RedHex = "#FF4739FF";

    public static readonly IReadOnlyList<TimerWarning> Defaults = [new(30, AmberHex), new(0, RedHex)];
}

public enum TimerUrgency { Normal, Warning, Overrun }

/// <summary>
/// One timer at rest: running time is <see cref="Banked"/> plus the stretch since
/// <see cref="RunningSince"/>, so every read is a pure function of the date.
/// </summary>
public sealed record TimerSnapshot
{
    public TimerSnapshot(
        string name,
        TimerMode mode,
        string? id = null,
        bool isRunning = false,
        DateTimeOffset? runningSince = null,
        double banked = 0,
        double durationSeconds = 0,
        DateTimeOffset? targetTime = null,
        DateTimeOffset? armedAt = null,
        IReadOnlyList<TimerWarning>? warnings = null)
    {
        Id = id ?? StageIds.New();
        Name = name;
        Mode = mode;
        IsRunning = isRunning;
        RunningSince = runningSince;
        Banked = banked;
        DurationSeconds = durationSeconds;
        TargetTime = targetTime;
        ArmedAt = armedAt;
        Warnings = warnings ?? TimerWarning.Defaults;
    }

    public const double WarningThreshold = 30;

    public string Id { get; init; }
    public string Name { get; init; }
    public TimerMode Mode { get; init; }
    public bool IsRunning { get; init; }
    public DateTimeOffset? RunningSince { get; init; }
    public double Banked { get; init; }

    /// <summary>Countdown length, or the Count Up limit (0 = unlimited).</summary>
    public double DurationSeconds { get; init; }

    public DateTimeOffset? TargetTime { get; init; }

    /// <summary>When a countdown-to-time was set, the baseline of its progress bar.</summary>
    public DateTimeOffset? ArmedAt { get; init; }

    public IReadOnlyList<TimerWarning> Warnings { get; init; }

    public double? Remaining(DateTimeOffset at) => Mode switch
    {
        TimerMode.CountUp => DurationSeconds > 0 ? DurationSeconds - Elapsed(at) : null,
        _ => Value(at),
    };

    public bool IsIdle =>
        !IsRunning && Banked == 0 && DurationSeconds == 0 && (Mode != TimerMode.CountdownToTime || TargetTime is null);

    public TimerWarning? ActiveWarning(DateTimeOffset at)
    {
        if (IsIdle || Remaining(at) is not { } remaining) return null;
        TimerWarning? best = null;
        foreach (var warning in Warnings)
            if (warning.RemainingSeconds >= remaining && (best is null || warning.RemainingSeconds < best.Value.RemainingSeconds))
                best = warning;
        return best;
    }

    public TimerUrgency Urgency(DateTimeOffset at) => ActiveWarning(at) switch
    {
        null => TimerUrgency.Normal,
        { RemainingSeconds: <= 0 } => TimerUrgency.Overrun,
        _ => TimerUrgency.Warning,
    };

    public bool IsLive => IsRunning || Mode == TimerMode.CountdownToTime;

    public double Elapsed(DateTimeOffset at) =>
        Banked + (IsRunning ? Math.Max(0, (at - (RunningSince ?? at)).TotalSeconds) : 0);

    public double Value(DateTimeOffset at) => Mode switch
    {
        TimerMode.CountUp => Elapsed(at),
        TimerMode.Countdown => DurationSeconds - Elapsed(at),
        _ => ((TargetTime ?? at) - at).TotalSeconds,
    };

    public string DisplayString(DateTimeOffset at) => Timecode(DisplayValue(at));

    public double DisplayValue(DateTimeOffset at) => Math.Truncate(Value(at));

    public string DisplayString(DateTimeOffset at, TimerTextFormat? format, string? pattern = null) =>
        Text(Value(at), format, pattern);

    public static string Text(double seconds, TimerTextFormat? format, string? pattern) =>
        !string.IsNullOrEmpty(pattern)
            ? TimerPattern.Text(seconds, pattern)
            : Text(seconds, format ?? TimerTextFormat.Digits);

    public static string Text(double seconds, TimerTextFormat format) => format switch
    {
        TimerTextFormat.Abbreviated => Units(seconds, ("d", "h", "m", "s"), spaced: false),
        TimerTextFormat.Words => Units(seconds, ("day", "hour", "minute", "second"), spaced: true),
        _ => Timecode(seconds),
    };

    private static string Units(double seconds, (string, string, string, string) labels, bool spaced)
    {
        var total = (long)Math.Truncate(seconds);
        var magnitude = Math.Abs(total);
        (long Count, string Label)[] parts =
        [
            (magnitude / 86400, labels.Item1), (magnitude / 3600 % 24, labels.Item2),
            (magnitude / 60 % 60, labels.Item3), (magnitude % 60, labels.Item4),
        ];
        var started = false;
        var words = new List<string>();
        for (var index = 0; index < parts.Length; index++)
        {
            var (count, label) = parts[index];
            var last = index == parts.Length - 1;
            if (!(started || count > 0 || last)) continue;
            started = true;
            var unit = spaced ? $" {label}{(count == 1 ? "" : "s")}" : label;
            words.Add($"{count}{unit}");
        }
        return (total < 0 ? "-" : "") + string.Join(" ", words);
    }

    public double? Progress(DateTimeOffset at)
    {
        switch (Mode)
        {
            case TimerMode.CountdownToTime:
                if (ArmedAt is not { } armedAt || TargetTime is not { } targetTime) return null;
                var span = (targetTime - armedAt).TotalSeconds;
                if (span <= 0) return 1;
                return Math.Min(1, Math.Max(0, (at - armedAt).TotalSeconds / span));
            default:
                if (DurationSeconds <= 0) return null;
                return Math.Min(1, Math.Max(0, Elapsed(at) / DurationSeconds));
        }
    }

    /// <summary>Booth-style digits: m:ss under an hour, h:mm:ss above, truncated toward zero.</summary>
    public static string Timecode(double seconds)
    {
        var total = (long)Math.Truncate(seconds);
        var magnitude = Math.Abs(total);
        var sign = seconds < 0 && magnitude > 0 ? "-" : "";
        return magnitude >= 3600
            ? string.Create(CultureInfo.InvariantCulture, $"{sign}{magnitude / 3600}:{magnitude / 60 % 60:00}:{magnitude % 60:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{sign}{magnitude / 60}:{magnitude % 60:00}");
    }

    public bool Equals(TimerSnapshot? other) =>
        other is not null
        && Id == other.Id && Name == other.Name && Mode == other.Mode && IsRunning == other.IsRunning
        && RunningSince == other.RunningSince && Banked.Equals(other.Banked)
        && DurationSeconds.Equals(other.DurationSeconds) && TargetTime == other.TargetTime
        && ArmedAt == other.ArmedAt && Warnings.SequenceEqual(other.Warnings);

    public override int GetHashCode() => HashCode.Combine(Id, Name, Mode, IsRunning, RunningSince, Banked, DurationSeconds, TargetTime);
}

internal static class StageIds
{
    /// <summary>Uppercase, hyphenated, like Swift's UUID().uuidString.</summary>
    public static string New() => Guid.NewGuid().ToString("D").ToUpperInvariant();
}
