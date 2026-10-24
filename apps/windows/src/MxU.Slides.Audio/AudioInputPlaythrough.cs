using MxU.Slides.Audio.Multitrack;

namespace MxU.Slides.Audio;

/// <summary>
/// Plays live audio out of a device: a microphone or line input "on air" to the room,
/// or a mix sent on to a second output. Buffers are pushed in as they are captured and
/// played back a moment later (30 ms of lead when the queue runs dry, at most 0.5 s
/// queued), with gain, mute (it starts muted until routed), pan, a lip-sync delay and
/// its own output device and channels. Port of AudioInputPlaythrough.swift.
/// <para>
/// Threading: <see cref="Push"/> may be called on a capture thread and never waits on the
/// lock (a contended push is dropped). Everything else is thread-safe.
/// </para>
/// </summary>
public sealed class AudioInputPlaythrough : IDisposable, IAudioRenderSource
{
    private const int Rate = 48_000;
    private const int MaxPendingFrames = 24_000;
    internal const int RestartLeadFrames = 1_440;

    private readonly AudioDelayLine delay = new();
    private readonly string name;
    private volatile StemRing ring = new(MaxPendingFrames * 2);
    private AudioOutputHost? host;
    private bool running;
    private float storedGain = 1;
    private bool muted = true;
    private float storedPan;
    private string? deviceUid;
    private int channelOffset;
    private bool monoOut;
    private float appliedGain;
    private float[] left = new float[4096];
    private float[] right = new float[4096];

    public AudioInputPlaythrough(string name = "Playthrough") => this.name = name;

    internal EngineStateLock Lock { get; } = new();

    public bool IsRunning => Lock.WithLock(() => running);

    public AudioOutputStatus OutputStatus => host?.Status ?? AudioOutputStatus.Idle;

    /// <summary>Starts playing out (on the clock if there is no device: never throws for that).</summary>
    public void Start()
    {
        Lock.WithLock(() =>
        {
            if (running) return;
            host ??= new AudioOutputHost(name, this, Layout);
            ring = new StemRing(MaxPendingFrames * 2);
            host.Configure(deviceUid, Layout);
            running = true;
        });
    }

    public void Stop()
    {
        Lock.WithLock(() =>
        {
            if (!running) return;
            running = false;
            host?.Stop();
            ring = new StemRing(MaxPendingFrames * 2);
        });
    }

    /// <summary>
    /// Queues a captured buffer (48 kHz; mono plays on both sides). False when it was
    /// dropped: stopped, the queue is full (0.5 s), or the lock was busy.
    /// </summary>
    public bool Push(PcmBuffer buffer)
    {
        if (!Lock.TryWithLock(() => PushLocked(buffer), out var accepted)) return false;
        return accepted;
    }

    private bool PushLocked(PcmBuffer buffer)
    {
        if (!running || buffer.SampleRate != Rate || buffer.FrameLength == 0) return false;
        var target = ring;
        var pending = target.Buffered;
        if (pending >= MaxPendingFrames) return false;
        var lead = LeadFrames(pending);
        if (lead > 0)
        {
            var silence = new float[lead];
            target.Write(silence, silence, lead);
        }
        var scheduled = Panned(buffer, storedPan) ?? buffer;
        var rightChannel = scheduled.ChannelCount > 1 ? 1 : 0;
        target.Write(scheduled.Data[0].AsSpan(0, scheduled.FrameLength), scheduled.Data[rightChannel].AsSpan(0, scheduled.FrameLength), scheduled.FrameLength);
        return true;
    }

    /// <summary>When the queue has run dry, start this far behind the source so jitter doesn't starve it again.</summary>
    internal static int LeadFrames(int pending) => pending == 0 ? RestartLeadFrames : 0;

    internal static PcmBuffer Silence(int frames, int channels = 2, int sampleRate = Rate) =>
        new(sampleRate, channels, frames) { FrameLength = frames };

    public float Gain
    {
        get => Lock.WithLock(() => storedGain);
        set => Lock.WithLock(() => storedGain = Math.Clamp(value, 0, 1));
    }

    public bool IsMuted
    {
        get => Lock.WithLock(() => muted);
        set => Lock.WithLock(() => muted = value);
    }

    /// <summary>-1 (left) … 1 (right), by the balance law: the far side dips, the near side stays.</summary>
    public float Pan
    {
        get => Lock.WithLock(() => storedPan);
        set => Lock.WithLock(() => storedPan = Math.Clamp(value, -1, 1));
    }

    /// <summary>The level the output stage is at (the Mac test reads mainMixerNode.outputVolume).</summary>
    internal float OutputVolume => Lock.WithLock(() => muted ? 0 : storedGain * (monoOut ? AudioEngine.MonoFold : 1));

    /// <summary>A panned copy (the original may be shared); null when there's no pan to apply.</summary>
    internal static PcmBuffer? Panned(PcmBuffer buffer, float pan)
    {
        if (pan == 0 || buffer.FrameLength == 0 || buffer.ChannelCount < 2) return null;
        var copy = new PcmBuffer(buffer.SampleRate, buffer.ChannelCount, buffer.FrameLength) { FrameLength = buffer.FrameLength };
        var leftGain = MathF.Min(1, 1 - pan);
        var rightGain = MathF.Min(1, 1 + pan);
        for (var frame = 0; frame < buffer.FrameLength; frame++)
        {
            copy.Data[0][frame] = buffer.Data[0][frame] * leftGain;
            copy.Data[1][frame] = buffer.Data[1][frame] * rightGain;
        }
        return copy;
    }

    public double OutputDelayMilliseconds
    {
        get => delay.DelayMilliseconds;
        set => delay.DelayMilliseconds = value;
    }

    /// <summary>Plays on <paramref name="device"/> (null = the Windows default), pair or mono channel. Never throws for a missing device.</summary>
    public void SetOutputDevice(AudioOutputDevice? device, int channelOffset = 0, bool mono = false)
    {
        Lock.WithLock(() =>
        {
            if (device?.Uid == deviceUid && channelOffset == this.channelOffset && mono == monoOut && (host?.IsRunning ?? false) == running) return;
            deviceUid = device?.Uid;
            this.channelOffset = channelOffset;
            monoOut = mono;
            if (running) host?.Configure(deviceUid, Layout);
        });
    }

    private AudioOutputHost.Layout Layout(int deviceChannels) =>
        new(monoOut ? 1 : 2, ChannelMap.Make(deviceChannels, channelOffset, monoOut));

    /// <summary>Windows reported a device change. Never blocks the caller.</summary>
    internal void OnDeviceConfigurationChanged() => host?.DevicesChanged();

    void IAudioRenderSource.Render(float[][] buffers, int frames, int sampleRate, double hostTime)
    {
        if (left.Length < frames)
        {
            left = new float[frames];
            right = new float[frames];
        }
        var got = ring.ReadAvailable(left.AsSpan(0, frames), right.AsSpan(0, frames), frames);
        Array.Clear(left, got, frames - got);
        Array.Clear(right, got, frames - got);
        float target;
        bool mono;
        // Never wait on the control lock here: read the settings racily (they're single words).
        target = muted ? 0 : storedGain;
        mono = buffers.Length == 1;
        if (mono) target *= AudioEngine.MonoFold;
        var block = new PcmBuffer(sampleRate, [left, right]) { FrameLength = frames };
        delay.Process(block);
        var from = appliedGain;
        var step = (target - from) / frames;
        for (var frame = 0; frame < frames; frame++)
        {
            var gain = from + step * (frame + 1);
            if (mono) buffers[0][frame] = (left[frame] + right[frame]) * AudioEngine.MonoFold * gain;
            else
            {
                buffers[0][frame] = left[frame] * gain;
                buffers[1][frame] = right[frame] * gain;
            }
        }
        appliedGain = target;
    }

    public void Dispose()
    {
        Stop();
        host?.Dispose();
    }
}
