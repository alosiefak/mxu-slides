using System.Diagnostics;
using System.Runtime.InteropServices;
using MxU.Slides.Core.Scene;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MxU.Slides.Audio;

/// <summary>Where an engine's sound is going right now.</summary>
public enum AudioOutputState
{
    /// <summary>Nothing has played yet, so no device is open.</summary>
    Idle,

    /// <summary>Playing on a device.</summary>
    Playing,

    /// <summary>The machine has no output device; the engine keeps time silently.</summary>
    NoDevice,

    /// <summary>The chosen device is unplugged; the engine keeps time silently until it's back.</summary>
    DeviceMissing,

    /// <summary>The device is there but wouldn't open; retrying every few seconds.</summary>
    Failed,

    /// <summary>Rendered by hand (tests), not by a device.</summary>
    Manual,
}

/// <summary>A status line for the UI and diagnostics: what state, which device, and a sentence.</summary>
public sealed record AudioOutputStatus(AudioOutputState State, string? DeviceUid, string? DeviceName, string Message)
{
    public static readonly AudioOutputStatus Idle = new(AudioOutputState.Idle, null, null, "Not started");

    public static readonly AudioOutputStatus Manual = new(AudioOutputState.Manual, null, null, "Rendered offline");

    /// <summary>True while the sound is reaching a device.</summary>
    public bool IsAudible => State == AudioOutputState.Playing;
}

/// <summary>Something that fills audio blocks on demand (an engine's render graph).</summary>
internal interface IAudioRenderSource
{
    /// <summary>
    /// Fills <paramref name="frames"/> frames of each channel in <paramref name="buffers"/>
    /// (planar, already zeroed) with audio that will be heard at <paramref name="hostTime"/>
    /// (<see cref="ShowState.HostClock"/> seconds). Called on the audio thread: never block.
    /// </summary>
    void Render(float[][] buffers, int frames, int sampleRate, double hostTime);
}

/// <summary>
/// Runs one engine's output: a thread that pulls audio from the engine and hands it to a
/// WASAPI (shared mode) stream on the chosen device. When there is no device, or it is
/// unplugged mid-service, the same thread keeps pulling audio on a real-time clock and
/// throws it away, so the playback position, playlist advances and stream feeds carry on;
/// when a device appears (Windows tells us, and we look every few seconds anyway) the
/// stream reopens on it. Nothing here throws for a missing device: <see cref="Status"/>
/// says what is going on. The Windows stand-in for AVAudioEngine's output node and its
/// configuration-change recovery.
/// </summary>
internal sealed class AudioOutputHost : IDisposable
{
    /// <summary>What the source renders and where it lands, given the device's channel count.</summary>
    /// <param name="SourceChannels">How many channels the source renders.</param>
    /// <param name="Map">Per device channel, the source channel it plays (-1 = silence).</param>
    public sealed record Layout(int SourceChannels, int[] Map);

    private const int ClockBlockMilliseconds = 10;
    private const long BufferDuration100ns = 30 * 10_000;
    private const int RetryMilliseconds = 3000;

    // One per audio thread, so a thread told to stop stays stopped even if a new one starts.
    private sealed class Run
    {
        public volatile bool Stopping;
    }

    private readonly string name;
    private readonly IAudioRenderSource source;
    private readonly Lock gate = new();
    private readonly AutoResetEvent wake = new(false);
    private readonly ManualResetEventSlim applied = new(true);
    private readonly IDisposable deviceSubscription;
    private Thread? thread;
    private Run? run;
    private volatile bool reopenRequested;

    // Guarded by gate.
    private int configVersion;
    private string? requestedUid;
    private Func<int, Layout> layoutFor;
    private int? fixedRate;
    private AudioOutputStatus status = AudioOutputStatus.Idle;
    private string? openUid;

    /// <param name="fixedRate">The rate the source renders at; null = the device's own rate.</param>
    public AudioOutputHost(string name, IAudioRenderSource source, Func<int, Layout> layoutFor, int? fixedRate = 48_000)
    {
        this.name = name;
        this.source = source;
        this.layoutFor = layoutFor;
        this.fixedRate = fixedRate;
        deviceSubscription = AudioDeviceList.ListenForChanges(DevicesChanged);
    }

    public AudioOutputStatus Status
    {
        get { lock (gate) return status; }
    }

    /// <summary>Raised on a thread-pool thread whenever <see cref="Status"/> changes.</summary>
    public event Action<AudioOutputStatus>? StatusChanged;

    /// <summary>The open stream's channel count (2 on the clock).</summary>
    public int DeviceChannels { get; private set; } = 2;

    /// <summary>The rate the source is being pulled at.</summary>
    public int SampleRate { get; private set; } = 48_000;

    public bool IsRunning
    {
        get { lock (gate) return thread is not null; }
    }

    /// <summary>The device open now (its endpoint id), or null when running on the clock.</summary>
    public string? OpenDeviceUid
    {
        get { lock (gate) return openUid; }
    }

    /// <summary>
    /// Points the output at a device (null = follow the Windows default) with a layout,
    /// starts the audio thread if needed, and waits (up to 3 s) until it has switched.
    /// </summary>
    public void Configure(string? deviceUid, Func<int, Layout> layout, int? rate = 48_000)
    {
        lock (gate)
        {
            requestedUid = deviceUid;
            layoutFor = layout;
            fixedRate = rate;
            configVersion++;
            applied.Reset();
        }
        Start();
        wake.Set();
        if (Thread.CurrentThread != thread) applied.Wait(TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// Changes which device is asked for (a uid, or null = follow the default) without
    /// reopening, for when the open device is already the right one.
    /// </summary>
    public void Retarget(string? deviceUid)
    {
        lock (gate) requestedUid = deviceUid;
    }

    public void Start()
    {
        lock (gate)
        {
            if (thread is not null) return;
            var mine = run = new Run();
            thread = new Thread(() => Loop(mine)) { IsBackground = true, Name = $"MxU audio out: {name}", Priority = ThreadPriority.Highest };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }
    }

    /// <summary>Stops the stream and its thread, releasing the device. <see cref="Start"/> runs it again.</summary>
    public void Stop()
    {
        Thread? running;
        lock (gate)
        {
            running = thread;
            if (running is null || run is null) return;
            run.Stopping = true;
            thread = null;
            run = null;
            openUid = null;
        }
        wake.Set();
        if (running != Thread.CurrentThread) running.Join(TimeSpan.FromSeconds(3));
        applied.Set();
        SetStatus(AudioOutputStatus.Idle);
    }

    public void Dispose()
    {
        deviceSubscription.Dispose();
        Stop();
    }

    /// <summary>A device came or went (or the default moved): look again.</summary>
    internal void DevicesChanged()
    {
        reopenRequested = true;
        try { wake.Set(); }
        catch (ObjectDisposedException) { }
    }

    private void Loop(Run mine)
    {
        var mmcss = Mmcss.Enter();
        try
        {
            while (!mine.Stopping)
            {
                var (uid, version) = Snapshot();
                reopenRequested = false;
                MMDevice? device = null;
                try
                {
                    using (var enumerator = new MMDeviceEnumerator()) device = AudioDeviceList.OpenEndpoint(enumerator, DataFlow.Render, uid);
                }
                catch
                {
                    device = null;
                }
                using (device)
                {
                    if (device is not null && RunDevice(mine, device, version)) continue;
                }
                if (mine.Stopping) break;
                RunClock(mine, uid, missing: device is null, version);
            }
        }
        finally
        {
            Mmcss.Leave(mmcss);
            applied.Set();
        }
    }

    private (string? Uid, int Version) Snapshot()
    {
        lock (gate) return (requestedUid, configVersion);
    }

    private void MarkApplied(int version)
    {
        lock (gate)
        {
            if (version == configVersion) applied.Set();
        }
    }

    private bool ConfigChanged(int version)
    {
        lock (gate) return configVersion != version;
    }

    // Plays on the device until the config changes (true: reopen), or we stop or the device fails (false).
    private bool RunDevice(Run mine, MMDevice device, int version)
    {
        AudioClient? client = null;
        EventWaitHandle? ready = null;
        var deviceName = Safe(() => device.FriendlyName) ?? "the output device";
        var deviceId = Safe(() => device.ID);
        try
        {
            client = device.AudioClient;
            var mix = client.MixFormat;
            var channels = Math.Max(1, mix.Channels);
            int rate;
            Func<int, Layout> layoutFunction;
            lock (gate)
            {
                rate = fixedRate ?? mix.SampleRate;
                layoutFunction = layoutFor;
            }
            var layout = layoutFunction(channels);
            // Shared mode: Windows mixes us with other apps and converts our rate if it differs.
            const AudioClientStreamFlags flags = AudioClientStreamFlags.EventCallback
                | (AudioClientStreamFlags)0x80000000 // AUTOCONVERTPCM
                | (AudioClientStreamFlags)0x08000000; // SRC_DEFAULT_QUALITY
            client.Initialize(AudioClientShareMode.Shared, flags, BufferDuration100ns, 0, FloatFormat(rate, channels, mix), Guid.Empty);
            ready = new EventWaitHandle(false, EventResetMode.AutoReset);
            client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle());
            var bufferFrames = client.BufferSize;
            var render = client.AudioRenderClient;
            var planar = Planar(layout.SourceChannels, bufferFrames);
            DeviceChannels = channels;
            SampleRate = rate;
            lock (gate) openUid = deviceId;
            Fill(render, planar, layout, bufferFrames, channels, rate, ShowState.HostClock());
            client.Start();
            SetStatus(new AudioOutputStatus(AudioOutputState.Playing, deviceId, deviceName, $"Playing on {deviceName}"));
            MarkApplied(version);
            WaitHandle[] handles = [ready, wake];
            var stalls = 0;
            while (!mine.Stopping)
            {
                var which = WaitHandle.WaitAny(handles, 1000);
                if (mine.Stopping) break;
                if (ConfigChanged(version)) return true;
                if (reopenRequested)
                {
                    reopenRequested = false;
                    if (deviceId is not null && FollowsDefaultAndMoved(deviceId)) return true;
                }
                if (which == WaitHandle.WaitTimeout)
                {
                    // A healthy stream asks every ~10 ms; three quiet seconds means it died silently.
                    if (++stalls >= 3) throw new IOException("the device stopped asking for audio");
                    continue;
                }
                if (which != 0) continue;
                stalls = 0;
                var padding = client.CurrentPadding;
                var available = bufferFrames - padding;
                if (available <= 0) continue;
                // What we write now is heard after the `padding` frames already queued.
                Fill(render, planar, layout, available, channels, rate, ShowState.HostClock() + padding / (double)rate);
            }
            return false;
        }
        catch (Exception error)
        {
            lock (gate) openUid = null;
            var unplugged = error is COMException { HResult: unchecked((int)0x88890004) } || !StillActive(device);
            SetStatus(unplugged
                ? new AudioOutputStatus(AudioOutputState.DeviceMissing, deviceId, deviceName,
                    $"{deviceName} is disconnected: audio keeps time silently until it's back")
                : new AudioOutputStatus(AudioOutputState.Failed, deviceId, deviceName,
                    $"Couldn't play on {deviceName} ({error.Message}); retrying"));
            return false;
        }
        finally
        {
            try { client?.Stop(); } catch { }
            try { client?.Dispose(); } catch { }
            ready?.Dispose();
        }
    }

    // No device (or it failed): keep the source running in real time and throw the sound
    // away, until Windows reports a device change or a few seconds pass; then look again.
    private void RunClock(Run mine, string? uid, bool missing, int version)
    {
        int rate;
        Func<int, Layout> layoutFunction;
        AudioOutputStatus previous;
        lock (gate)
        {
            rate = fixedRate ?? 48_000;
            layoutFunction = layoutFor;
            openUid = null;
            previous = status;
        }
        if (missing)
        {
            var knownName = previous.DeviceUid == uid ? previous.DeviceName : null;
            SetStatus(uid is null
                ? new AudioOutputStatus(AudioOutputState.NoDevice, null, null,
                    "No output device: audio keeps time silently until one is connected")
                : new AudioOutputStatus(AudioOutputState.DeviceMissing, uid, knownName,
                    $"{knownName ?? "The chosen output device"} is disconnected: audio keeps time silently until it's back"));
        }
        var layout = layoutFunction(2);
        DeviceChannels = 2;
        SampleRate = rate;
        MarkApplied(version);
        var block = Math.Max(1, rate * ClockBlockMilliseconds / 1000);
        var planar = Planar(layout.SourceChannels, block);
        var clock = Stopwatch.StartNew();
        var started = ShowState.HostClock();
        long blocks = 0;
        while (!mine.Stopping)
        {
            if (ConfigChanged(version) || reopenRequested || clock.ElapsedMilliseconds >= RetryMilliseconds) return;
            var due = blocks * ClockBlockMilliseconds;
            var now = clock.ElapsedMilliseconds;
            if (now < due)
            {
                wake.WaitOne((int)Math.Min(due - now, 50));
                continue;
            }
            // After a long stall (sleep, debugger) carry on from now rather than racing to catch up.
            if (now - due > 250)
            {
                blocks = now / ClockBlockMilliseconds;
                continue;
            }
            Clear(planar, block);
            try
            {
                source.Render(planar, block, rate, started + blocks * (ClockBlockMilliseconds / 1000.0));
            }
            catch
            {
                // The source must never take the clock down.
            }
            blocks++;
        }
    }

    private bool FollowsDefaultAndMoved(string openId)
    {
        string? uid;
        lock (gate) uid = requestedUid;
        if (uid is not null) return false;
        var now = AudioDeviceList.DefaultEndpointId(DataFlow.Render);
        return now is not null && now != openId;
    }

    private static bool StillActive(MMDevice device) => Safe(() => device.State == DeviceState.Active);

    private static T? Safe<T>(Func<T> read)
    {
        try { return read(); }
        catch { return default; }
    }

    /// <summary>
    /// 32-bit float in the device's own speaker layout (its channel mask), at our rate, so a
    /// 16-channel interface gets its channels one-to-one instead of being re-matrixed.
    /// </summary>
    internal static WaveFormat FloatFormat(int rate, int channels, WaveFormat mix)
    {
        var mask = channels <= 2 ? (1 << channels) - 1 : 0;
        if (mix.Encoding == WaveFormatEncoding.Extensible)
        {
            var mixPointer = WaveFormat.MarshalToPtr(mix);
            try { mask = Marshal.ReadInt32(mixPointer, 20); }
            finally { Marshal.FreeHGlobal(mixPointer); }
        }
        // WAVEFORMATEXTENSIBLE, laid out by hand (NAudio has no setter for the mask).
        var bytes = new byte[40];
        BitConverter.TryWriteBytes(bytes.AsSpan(0), (ushort)0xFFFE);
        BitConverter.TryWriteBytes(bytes.AsSpan(2), (ushort)channels);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), rate);
        BitConverter.TryWriteBytes(bytes.AsSpan(8), rate * channels * 4);
        BitConverter.TryWriteBytes(bytes.AsSpan(12), (ushort)(channels * 4));
        BitConverter.TryWriteBytes(bytes.AsSpan(14), (ushort)32);
        BitConverter.TryWriteBytes(bytes.AsSpan(16), (ushort)22);
        BitConverter.TryWriteBytes(bytes.AsSpan(18), (ushort)32);
        BitConverter.TryWriteBytes(bytes.AsSpan(20), mask);
        new Guid("00000003-0000-0010-8000-00aa00389b71").TryWriteBytes(bytes.AsSpan(24));
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            return WaveFormat.MarshalFromPtr(pointer);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private void Fill(AudioRenderClient render, float[][] planar, Layout layout, int frames, int channels, int rate, double hostTime)
    {
        Clear(planar, frames);
        try
        {
            source.Render(planar, frames, rate, hostTime);
        }
        catch
        {
            Clear(planar, frames);
        }
        var pointer = render.GetBuffer(frames);
        unsafe
        {
            var output = new Span<float>((void*)pointer, frames * channels);
            var map = layout.Map;
            for (var channel = 0; channel < channels; channel++)
            {
                var from = channel < map.Length ? map[channel] : -1;
                if (from < 0 || from >= planar.Length)
                {
                    for (var frame = 0; frame < frames; frame++) output[frame * channels + channel] = 0;
                    continue;
                }
                var samples = planar[from];
                for (var frame = 0; frame < frames; frame++) output[frame * channels + channel] = samples[frame];
            }
        }
        render.ReleaseBuffer(frames, AudioClientBufferFlags.None);
    }

    private static float[][] Planar(int channels, int frames)
    {
        var planar = new float[Math.Max(1, channels)][];
        for (var channel = 0; channel < planar.Length; channel++) planar[channel] = new float[Math.Max(1, frames)];
        return planar;
    }

    private static void Clear(float[][] planar, int frames)
    {
        foreach (var channel in planar) Array.Clear(channel, 0, Math.Min(frames, channel.Length));
    }

    private void SetStatus(AudioOutputStatus next)
    {
        lock (gate)
        {
            if (status == next) return;
            status = next;
        }
        var handlers = StatusChanged;
        if (handlers is not null) AudioThreads.Events.Async(() => handlers(next));
    }
}

/// <summary>Asks Windows to schedule the audio thread as "Pro Audio" (MMCSS), so a busy machine doesn't starve it.</summary>
internal static partial class Mmcss
{
    public static IntPtr Enter()
    {
        try
        {
            var index = 0u;
            return AvSetMmThreadCharacteristics("Pro Audio", ref index);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    public static void Leave(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        try { AvRevertMmThreadCharacteristics(handle); }
        catch { }
    }

    [LibraryImport("avrt.dll", EntryPoint = "AvSetMmThreadCharacteristicsW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

    [LibraryImport("avrt.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AvRevertMmThreadCharacteristics(IntPtr handle);
}
