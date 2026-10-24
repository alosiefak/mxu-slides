namespace MxU.Slides.Broadcast.Streaming;

/// <summary>
/// Turns a platform's own health polls (e.g. YouTube's "good", "bad", "noData") into a
/// verdict that doesn't flap. "noData" in the first <see cref="GraceSeconds"/> of a stream is
/// ignored (platforms take a while to see the first frames), and only escalates after
/// <see cref="EscalationPolls"/> polls in a row. "bad" is "degraded": shown, but never tints
/// the chip. Not thread-safe; poll from one place. Port of PlatformHealthTracker.
/// </summary>
public sealed class PlatformHealthTracker(
    double graceSeconds = PlatformHealthTracker.DefaultGraceSeconds,
    int escalationPolls = PlatformHealthTracker.DefaultEscalationPolls)
{
    public const double DefaultGraceSeconds = 90;
    public const int DefaultEscalationPolls = 3;

    private readonly Dictionary<string, int> consecutiveNoData = [];
    private readonly HashSet<string> settled = [];
    private readonly Dictionary<string, PlatformVerdict> verdicts = [];

    public double GraceSeconds { get; } = graceSeconds;

    public int EscalationPolls { get; } = escalationPolls;

    /// <summary>True until the destination has once reported healthy: poll it often while true.</summary>
    public bool PollsHot(string destinationId) => !settled.Contains(destinationId);

    public PlatformVerdict? Verdict(string destinationId) =>
        verdicts.TryGetValue(destinationId, out var verdict) ? verdict : null;

    /// <param name="status">The platform's word: "good"/"ok" (healthy), "bad", "noData" or "unknown".</param>
    /// <param name="elapsed">Seconds since the stream went live.</param>
    public void Record(string destinationId, string status, IReadOnlyList<string> issues, double elapsed)
    {
        if (status == "unknown") return;
        switch (status)
        {
            case "noData":
                if (!settled.Contains(destinationId) && elapsed < GraceSeconds) return;
                settled.Remove(destinationId);
                var count = consecutiveNoData.GetValueOrDefault(destinationId) + 1;
                consecutiveNoData[destinationId] = count;
                if (count >= EscalationPolls) verdicts[destinationId] = PlatformVerdict.NoData;
                break;
            case "bad":
                settled.Remove(destinationId);
                consecutiveNoData[destinationId] = 0;
                verdicts[destinationId] = PlatformVerdict.Degraded(issues);
                break;
            default:
                settled.Add(destinationId);
                consecutiveNoData[destinationId] = 0;
                verdicts[destinationId] = PlatformVerdict.Healthy;
                break;
        }
    }

    public void Reset()
    {
        consecutiveNoData.Clear();
        settled.Clear();
        verdicts.Clear();
    }
}

public enum PlatformVerdictKind { Healthy, Degraded, NoData }

/// <summary>What a platform says about one stream. Degraded carries the platform's issue lines.</summary>
public sealed record PlatformVerdict(PlatformVerdictKind Kind, IReadOnlyList<string> Issues)
{
    public static PlatformVerdict Healthy { get; } = new(PlatformVerdictKind.Healthy, []);

    public static PlatformVerdict NoData { get; } = new(PlatformVerdictKind.NoData, []);

    public static PlatformVerdict Degraded(IReadOnlyList<string> issues) => new(PlatformVerdictKind.Degraded, [.. issues]);

    public bool Equals(PlatformVerdict? other) =>
        other is not null && Kind == other.Kind && Issues.SequenceEqual(other.Issues);

    public override int GetHashCode() => HashCode.Combine(Kind, Issues.Count);
}

/// <summary>
/// The one colour and word the broadcast chip shows for every stream at once: any failure is
/// red "STREAM FAILED"; reconnecting or connecting is amber; a live stream the platform says
/// gets no data is amber "NO DATA"; otherwise green "LIVE". Port of StreamHealthRollup.
/// </summary>
public static class StreamHealthRollup
{
    public enum Tint { None, Green, Amber, Red }

    public sealed record Input(StreamSessionState State, PlatformVerdict? Verdict = null);

    public static Tint TintOf(IReadOnlyList<Input> inputs)
    {
        if (inputs.Count == 0) return Tint.None;
        var tint = Tint.Green;
        foreach (var input in inputs)
        {
            switch (input.State.Kind)
            {
                case StreamStateKind.Failed:
                    return Tint.Red;
                case StreamStateKind.Reconnecting or StreamStateKind.Connecting:
                    tint = Tint.Amber;
                    break;
                case StreamStateKind.Publishing when input.Verdict == PlatformVerdict.NoData:
                    tint = Tint.Amber;
                    break;
            }
        }
        return tint;
    }

    public static string Word(IReadOnlyList<Input> inputs)
    {
        if (inputs.Any(i => i.State.Kind == StreamStateKind.Failed)) return "STREAM FAILED";
        if (inputs.Any(i => i.State.Kind == StreamStateKind.Reconnecting)) return "RECONNECTING";
        if (inputs.Any(i => i.State.Kind == StreamStateKind.Connecting)) return "CONNECTING";
        if (inputs.Any(i => i.State.Kind == StreamStateKind.Publishing && i.Verdict == PlatformVerdict.NoData)) return "NO DATA";
        return "LIVE";
    }
}
