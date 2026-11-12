using System.Diagnostics;
using System.Text.Json;

namespace MxU.Slides.LocalApi;

/// <summary>Who or what fired: the operator, a key, MIDI, the Scheduler, a timeline… or a backup following its booth.</summary>
public sealed record ApiFireSource(ApiFireSource.Kinds Kind, string? Name = null)
{
    public enum Kinds { Ui, Keyboard, Midi, Scheduler, Auto, Api, Timecode, Timeline, Follow }

    private static readonly AsyncLocal<ApiFireSource?> current = new();

    /// <summary>The source of fires made inside <see cref="With{T}"/> (the Mac's @TaskLocal APIFireSource.current).</summary>
    public static ApiFireSource? Current => current.Value;

    /// <summary>Runs <paramref name="work"/> with every fire it makes credited to <paramref name="source"/>.</summary>
    public static T With<T>(ApiFireSource source, Func<T> work)
    {
        var before = current.Value;
        current.Value = source;
        try
        {
            return work();
        }
        finally
        {
            current.Value = before;
        }
    }

    public static void With(ApiFireSource source, Action work) => With(source, () =>
    {
        work();
        return 0;
    });
}

/// <summary>
/// One fire on the fires topic (MxU PR #12): what the booth just did, for a backup to do too.
/// Its fields and names are the Mac's APIFireEvent, so a Mac follows a Windows booth and the other
/// way round. <see cref="Cause"/> is the seq of the fire this one is an effect of (a slide's
/// actions, a combo's steps); a backup replays only fires with none, and their effects follow.
/// </summary>
public sealed record ApiFireEvent
{
    public enum Kinds
    {
        Slide, Step, Clear, ClearAll, Media, Overlay, DismissOverlay, Alert, Timer, Combo, OutputPreset, DismissAlert,
        AudioItem, AudioPlaylist, StopAudio, AudioTransport, MediaTransport,

        /// <summary>Who is in charge changed (a booth took back): never replayed.</summary>
        Role,

        /// <summary>The booth started or stopped streaming (id = its preset): never replayed.</summary>
        Capture,

        /// <summary>The booth is quitting, which is no crash: a backup doesn't take over for it.</summary>
        Quit,
    }

    public int Seq { get; init; }
    public double At { get; init; }
    public ApiFireSource Source { get; init; } = new(ApiFireSource.Kinds.Ui);
    public int? Cause { get; init; }
    public required Kinds Kind { get; init; }
    public string? Id { get; init; }
    public ApiFireSlideCommand? Slide { get; init; }
    public bool? Settled { get; init; }
    public bool? SlideActions { get; init; }
    public int? Steps { get; init; }
    public ApiClearCommand? Clear { get; init; }
    public ApiFireAlertCommand? Alert { get; init; }
    public string? Layer { get; init; }
    public string? ServiceItemId { get; init; }
    public bool? Loops { get; init; }
    public ApiTimerAction? Timer { get; init; }
    public string? EntryId { get; init; }
    public ApiAudioStopCommand? AudioStop { get; init; }
    public ApiAudioTransportCommand? AudioTransport { get; init; }
    public ApiMediaTransportCommand? MediaTransport { get; init; }
    public int? Term { get; init; }
    public string? InCharge { get; init; }

    public bool Equals(ApiFireEvent? other) =>
        other is not null && Seq == other.Seq && At.Equals(other.At) && Source == other.Source && Cause == other.Cause && Kind == other.Kind
        && Id == other.Id && Slide == other.Slide && Settled == other.Settled && SlideActions == other.SlideActions && Steps == other.Steps
        && Clear == other.Clear && Equals(Alert, other.Alert) && Layer == other.Layer && ServiceItemId == other.ServiceItemId && Loops == other.Loops
        && Timer == other.Timer && EntryId == other.EntryId && AudioStop == other.AudioStop && AudioTransport == other.AudioTransport
        && MediaTransport == other.MediaTransport && Term == other.Term && InCharge == other.InCharge;

    public override int GetHashCode() => HashCode.Combine(Seq, Kind, Id);
}

public static class ApiFireKinds
{
    public static IReadOnlyList<ApiFireEvent.Kinds> All { get; } = Enum.GetValues<ApiFireEvent.Kinds>();

    /// <summary>The wire name ("clearAll"), as the Mac's raw values.</summary>
    public static string Raw(this ApiFireEvent.Kinds kind)
    {
        var name = kind.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}

/// <summary>
/// The fires topic's publisher: each fire this computer runs, stamped with a count, the time,
/// its source and its cause. With the Local API off nobody listens, and it only counts. Port of
/// APIFireFeed.swift. Publish from the UI thread (as the Mac's is @MainActor).
/// </summary>
public sealed class ApiFireFeed(Func<double>? clock = null)
{
    public static ApiFireFeed Shared { get; } = new();

    private static readonly AsyncLocal<int?> cause = new();

    /// <summary>The fire whose effects are running now (the Mac's @TaskLocal APIFireFeed.cause).</summary>
    public static int? CurrentCause => cause.Value;

    /// <summary>Runs <paramref name="work"/> with every fire it makes recorded as an effect of <paramref name="seq"/>.</summary>
    public static void WithCause(int seq, Action work)
    {
        var before = cause.Value;
        cause.Value = seq;
        try
        {
            work();
        }
        finally
        {
            cause.Value = before;
        }
    }

    /// <summary>
    /// Until disposed, fires made on this thread (and the async work it starts) are effects of
    /// <paramref name="seq"/>: for code that can't wrap its effects in a lambda.
    /// </summary>
    public static IDisposable Caused(int seq)
    {
        var before = cause.Value;
        cause.Value = seq;
        return new Restore(() => cause.Value = before);
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    private readonly Func<double> clock = clock ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
    private int seq;

    /// <summary>Where stamped fires go (the server's fires topic); null while the Local API is off.</summary>
    public Action<ApiFireEvent>? Sink { get; set; }

    /// <summary>Stamps and sends a fire; returns its seq, which its effects carry as their cause.</summary>
    public int Publish(ApiFireEvent fire)
    {
        var number = Interlocked.Increment(ref seq);
        if (Sink is not { } sink) return number;
        sink(fire with
        {
            Seq = number, At = clock(), Source = ApiFireSource.Current ?? new ApiFireSource(ApiFireSource.Kinds.Ui), Cause = CurrentCause,
        });
        return number;
    }

    /// <summary>The fire as it goes on the wire: sorted keys, absent fields left off (as the Mac's encoder writes it).</summary>
    public static byte[] Encode(ApiFireEvent fire) => ApiJson.Encode(fire);

    public static ApiFireEvent? Decode(ReadOnlySpan<byte> data)
    {
        try
        {
            return JsonSerializer.Deserialize<ApiFireEvent>(data, ApiJson.Options);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }
}
