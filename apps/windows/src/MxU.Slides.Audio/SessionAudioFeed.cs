namespace MxU.Slides.Audio;

/// <summary>
/// What one recording or stream wants to hear. A mix wins over an input, an input entry over a
/// bare device id; with none of them it's the program audio. <see cref="IncludeProgram"/> adds
/// the program on top of a mix or an input; <see cref="DelayMilliseconds"/> (0–2000) holds the
/// sound back so it lines up with a late picture.
/// </summary>
public sealed record SessionAudioChoice(
    string? MixId = null, string? InputId = null, string? InputUid = null, bool IncludeProgram = false, int DelayMilliseconds = 0);

/// <summary>Where session sound can come from. The app supplies these (tests supply fakes).</summary>
public interface ISessionAudioBackends
{
    /// <summary>Everything this app plays (the program audio tap), 48 kHz stereo. Disposing the result stops it.</summary>
    IDisposable OpenProgram(Action<PcmBuffer, double> consumer);

    /// <summary>A mix's sound, or null when this PC has no such mix.</summary>
    IDisposable? OpenMix(string mixId, Action<PcmBuffer, double> consumer);

    /// <summary>An audio input entry's sound, or null (with why) when it isn't available.</summary>
    IDisposable? OpenInput(string inputId, Action<PcmBuffer, double> consumer, out string? problem);

    /// <summary>A sound device named by its id (how older presets named an input), or null (with why).</summary>
    IDisposable? OpenInputDevice(string uid, Action<PcmBuffer, double> consumer, out string? problem);

    /// <summary>An input entry's name, for messages.</summary>
    string? InputName(string inputId);
}

/// <summary>
/// The sound one recording or stream hears, opened from a <see cref="SessionAudioChoice"/>:
/// <list type="bullet">
/// <item>a mix (the Music tab's buses), or</item>
/// <item>an audio input (say, the mixing desk on a USB sound interface), or</item>
/// <item>the program audio (everything this app plays) — also what it falls back to, with
/// <see cref="FallbackReason"/> saying why, when the mix or input isn't there.</item>
/// </list>
/// With "include program", the program is summed into the mix or input. The delay is applied
/// last. Buffers reach the consumer on whichever audio thread produced them (the mix clock,
/// the capture thread, the program tap); it must copy what it keeps and return quickly.
/// Port of SessionAudioFeed in the Mac's ProgramAudioSource.swift.
/// </summary>
public sealed class SessionAudioFeed : IDisposable
{
    private readonly Lock gate = new();
    private List<IDisposable>? handles;

    private SessionAudioFeed(string description, string? fallbackReason, List<IDisposable> handles)
    {
        Description = description;
        FallbackReason = fallbackReason;
        this.handles = handles;
    }

    /// <summary>What it ended up hearing: "program", "mix:main", "input:pulpit+program" (plus " +120ms" when delayed).</summary>
    public string Description { get; }

    /// <summary>Why it's on program audio although something else was asked for, or null.</summary>
    public string? FallbackReason { get; }

    public static SessionAudioFeed Open(SessionAudioChoice choice, ISessionAudioBackends backends, Action<PcmBuffer, double> consumer)
    {
        var delay = Math.Clamp(choice.DelayMilliseconds, 0, 2000);
        var output = Delayed(consumer, delay);
        var suffix = delay > 0 ? $" +{delay}ms" : "";
        string? fallback = null;

        var plus = choice.IncludeProgram ? "+program" : "";
        if (choice.MixId is { Length: > 0 } mixId)
        {
            if (Attach(choice.IncludeProgram, backends, output, sink => backends.OpenMix(mixId, sink)) is { } mix)
                return new SessionAudioFeed($"mix:{mixId}{plus}{suffix}", null, mix);
            fallback = $"mix {mixId} not available — fell back to Program Audio";
        }
        else if (choice.InputId is { Length: > 0 } inputId)
        {
            string? problem = null;
            if (Attach(choice.IncludeProgram, backends, output, sink => backends.OpenInput(inputId, sink, out problem)) is { } input)
                return new SessionAudioFeed($"input:{inputId}{plus}{suffix}", null, input);
            fallback = $"input {backends.InputName(inputId) ?? inputId} not available{(problem is null ? "" : $" ({problem})")} — fell back to Program Audio";
        }
        else if (choice.InputUid is { Length: > 0 } uid)
        {
            string? problem = null;
            if (Attach(choice.IncludeProgram, backends, output, sink => backends.OpenInputDevice(uid, sink, out problem)) is { } device)
                return new SessionAudioFeed($"input-device{plus}{suffix}", null, device);
            fallback = $"input {uid} not available{(problem is null ? "" : $" ({problem})")} — fell back to Program Audio";
        }
        return new SessionAudioFeed($"program{suffix}", fallback, [backends.OpenProgram(output)]);
    }

    /// <summary>Opens a mix or input (and the program on top, when asked); null if the mix or input wouldn't open.</summary>
    private static List<IDisposable>? Attach(bool includeProgram, ISessionAudioBackends backends, Action<PcmBuffer, double> output,
        Func<Action<PcmBuffer, double>, IDisposable?> open)
    {
        if (!includeProgram) return open(output) is { } plain ? [plain] : null;
        var summer = new ProgramAudioSummer();
        // Each buffer is copied before the program is added: the original may be shared with other listeners.
        var main = open((buffer, host) =>
        {
            var copy = buffer.Copy();
            summer.SumInto(copy);
            output(copy, host);
        });
        if (main is null) return null;
        var program = backends.OpenProgram((buffer, _) => summer.PushProgram(buffer));
        return [main, program];
    }

    private static Action<PcmBuffer, double> Delayed(Action<PcmBuffer, double> consumer, int milliseconds)
    {
        if (milliseconds <= 0) return consumer;
        var line = new AudioDelayLine { DelayMilliseconds = milliseconds };
        return (buffer, host) => consumer(line.DelayedCopy(buffer), host);
    }

    /// <summary>Stops listening (safe to call more than once).</summary>
    public void Dispose()
    {
        List<IDisposable>? closing;
        lock (gate)
        {
            closing = handles;
            handles = null;
        }
        foreach (var handle in closing ?? [])
        {
            try { handle.Dispose(); }
            catch { }
        }
    }
}

/// <summary>
/// Keeps the program audio waiting and adds it into another feed's buffers as they come
/// (a mix or an input running on its own clock), at most half a second behind. Both sides are
/// 48 kHz; a mono buffer counts as both channels. Port of ProgramAudioSummer.swift.
/// </summary>
public sealed class ProgramAudioSummer
{
    internal const int MaxQueuedFrames = 24_000;

    private readonly Lock gate = new();
    private readonly Queue<(float Left, float Right)> queue = new();

    public int QueuedFrames
    {
        get { lock (gate) return queue.Count; }
    }

    public void PushProgram(PcmBuffer buffer)
    {
        if (buffer.FrameLength == 0 || buffer.SampleRate != 48_000) return;
        var right = buffer.ChannelCount > 1 ? 1 : 0;
        lock (gate)
        {
            for (var frame = 0; frame < buffer.FrameLength; frame++)
                queue.Enqueue((buffer.Data[0][frame], buffer.Data[right][frame]));
            while (queue.Count > MaxQueuedFrames) queue.Dequeue();
        }
    }

    /// <summary>Adds as much queued program audio as there is (up to the buffer's length) into <paramref name="buffer"/>, clamped to ±1.</summary>
    public void SumInto(PcmBuffer buffer)
    {
        if (buffer.FrameLength == 0) return;
        var stereo = buffer.ChannelCount > 1;
        lock (gate)
        {
            var take = Math.Min(buffer.FrameLength, queue.Count);
            for (var frame = 0; frame < take; frame++)
            {
                var (left, right) = queue.Dequeue();
                buffer.Data[0][frame] = Math.Clamp(buffer.Data[0][frame] + left, -1, 1);
                if (stereo) buffer.Data[1][frame] = Math.Clamp(buffer.Data[1][frame] + right, -1, 1);
            }
        }
    }
}
