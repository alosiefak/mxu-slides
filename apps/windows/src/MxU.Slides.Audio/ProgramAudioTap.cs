using System.Diagnostics;
using System.Runtime.InteropServices;
using MxU.Slides.Core.Media;
using MxU.Slides.Core.Scene;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MxU.Slides.Audio;

/// <summary>Whether the program audio tap is capturing.</summary>
public enum ProgramAudioTapState
{
    /// <summary>No one is listening, so nothing is captured.</summary>
    Stopped,

    Starting,

    Capturing,

    /// <summary>Windows is older than 10 version 2004 (build 19041): there is no process loopback.</summary>
    Unsupported,

    /// <summary>Capture failed to start (the message says why); retrying every few seconds.</summary>
    Failed,
}

public sealed record ProgramAudioTapStatus(ProgramAudioTapState State, string Message);

/// <summary>
/// Captures everything this app plays — the audio engine's buses, the tracks, the sound
/// of videos playing in WPF — and nothing else on the machine (no system sounds, no other
/// apps), for streams and recordings. Uses Windows "process loopback" capture of our own
/// process tree (Windows 10 build 19041+). Delivered as <see cref="AudioBuffer"/>s (48 kHz
/// stereo float) to any number of <see cref="IProgramSink"/>s, stamped on
/// <see cref="ShowState.HostClock"/>. When nothing is playing Windows sends nothing, so the
/// tap fills the gap with silence: sinks always get a continuous stream.
/// Capture starts with the first sink and stops with the last. Port of ProgramAudioTap.swift
/// (a CoreAudio process tap) together with the app's ProgramAudioSource fan-out.
/// <para>
/// Threading: sinks and consumers are called on the tap's capture thread; they must copy
/// what they keep and return quickly. Add/remove from any thread.
/// </para>
/// </summary>
public sealed class ProgramAudioTap : IDisposable
{
    public const int Rate = 48_000;
    public const int Channels = 2;

    private const int RetryMilliseconds = 5000;
    private const double GapSeconds = 0.03;
    private const int SilenceBlock = 480;

    private readonly Lock gate = new();
    private readonly List<IProgramSink> sinks = [];
    private readonly Dictionary<Guid, Action<PcmBuffer, double>> consumers = [];
    private readonly int processId;
    private Thread? thread;
    private volatile bool stopping;
    private ProgramAudioTapStatus status = new(ProgramAudioTapState.Stopped, "Not capturing");

    /// <param name="processId">Whose audio to capture (and its children's); default this app.</param>
    public ProgramAudioTap(int? processId = null) => this.processId = processId ?? Environment.ProcessId;

    /// <summary>Process loopback needs Windows 10 version 2004 (build 19041) or later.</summary>
    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

    public ProgramAudioTapStatus Status
    {
        get { lock (gate) return status; }
    }

    /// <summary>Raised on a thread-pool thread when <see cref="Status"/> changes.</summary>
    public event Action<ProgramAudioTapStatus>? StatusChanged;

    public void AddSink(IProgramSink sink)
    {
        lock (gate) sinks.Add(sink);
        Reconcile();
    }

    public void RemoveSink(IProgramSink sink)
    {
        lock (gate) sinks.Remove(sink);
        Reconcile();
    }

    /// <summary>For in-app listeners (a mix feed summing program audio in): 48 kHz stereo planar.</summary>
    public Guid AddConsumer(Action<PcmBuffer, double> consumer)
    {
        var token = Guid.NewGuid();
        lock (gate) consumers[token] = consumer;
        Reconcile();
        return token;
    }

    public void RemoveConsumer(Guid token)
    {
        lock (gate) consumers.Remove(token);
        Reconcile();
    }

    private void Reconcile()
    {
        Thread? stoppingThread = null;
        lock (gate)
        {
            var wanted = sinks.Count > 0 || consumers.Count > 0;
            if (wanted && thread is null)
            {
                if (!IsSupported)
                {
                    SetStatusLocked(new ProgramAudioTapStatus(ProgramAudioTapState.Unsupported,
                        "Capturing the app's audio needs Windows 10 version 2004 or later; streams will be video-only"));
                    return;
                }
                stopping = false;
                thread = new Thread(Run) { IsBackground = true, Name = "MxU program audio tap", Priority = ThreadPriority.AboveNormal };
                thread.SetApartmentState(ApartmentState.MTA);
                thread.Start();
            }
            else if (!wanted && thread is not null)
            {
                stopping = true;
                stoppingThread = thread;
                thread = null;
            }
        }
        if (stoppingThread is not null && stoppingThread != Thread.CurrentThread) stoppingThread.Join(TimeSpan.FromSeconds(2));
    }

    public void Dispose()
    {
        lock (gate)
        {
            sinks.Clear();
            consumers.Clear();
        }
        Reconcile();
    }

    private void Run()
    {
        var mmcss = Mmcss.Enter();
        try
        {
            while (!stopping)
            {
                SetStatus(new ProgramAudioTapStatus(ProgramAudioTapState.Starting, "Starting program audio capture"));
                string? failure;
                try
                {
                    failure = Capture();
                }
                catch (Exception error)
                {
                    failure = error.Message;
                }
                if (stopping) break;
                SetStatus(new ProgramAudioTapStatus(ProgramAudioTapState.Failed,
                    $"Couldn't capture the app's audio ({failure ?? "it stopped"}); retrying"));
                // Keep sinks fed with silence while we wait to retry.
                var clock = Stopwatch.StartNew();
                var started = ShowState.HostClock();
                long blocks = 0;
                while (!stopping && clock.ElapsedMilliseconds < RetryMilliseconds)
                {
                    var due = blocks * 10;
                    if (clock.ElapsedMilliseconds < due)
                    {
                        Thread.Sleep(5);
                        continue;
                    }
                    DeliverSilence(SilenceBlock, started + blocks * 0.01);
                    blocks++;
                }
            }
        }
        finally
        {
            Mmcss.Leave(mmcss);
            SetStatus(new ProgramAudioTapStatus(ProgramAudioTapState.Stopped, "Not capturing"));
        }
    }

    // Captures until stopped (null) or something fails (the reason).
    private string? Capture()
    {
        using var client = ProcessLoopback.Activate(processId);
        var format = WaveFormat.CreateIeeeFloatWaveFormat(Rate, Channels);
        const AudioClientStreamFlags flags = AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback
            | (AudioClientStreamFlags)0x80000000 // AUTOCONVERTPCM
            | (AudioClientStreamFlags)0x08000000; // SRC_DEFAULT_QUALITY
        client.Initialize(AudioClientShareMode.Shared, flags, 200 * 10_000, 0, format, Guid.Empty);
        using var ready = new EventWaitHandle(false, EventResetMode.AutoReset);
        client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle());
        var captureClient = client.AudioCaptureClient;
        client.Start();
        SetStatus(new ProgramAudioTapStatus(ProgramAudioTapState.Capturing, "Capturing the app's audio"));
        var deliveredUntil = ShowState.HostClock();
        var samples = new float[48_000 * Channels];
        try
        {
            while (!stopping)
            {
                ready.WaitOne(10);
                if (stopping) break;
                var packet = captureClient.GetNextPacketSize();
                if (packet == 0)
                {
                    // Nothing is playing: Windows goes quiet, so we send silence up to (nearly) now.
                    var now = ShowState.HostClock();
                    while (now - deliveredUntil > GapSeconds)
                    {
                        DeliverSilence(SilenceBlock, deliveredUntil);
                        deliveredUntil += SilenceBlock / (double)Rate;
                    }
                    continue;
                }
                while (packet > 0 && !stopping)
                {
                    var data = captureClient.GetBuffer(out var frames, out var bufferFlags, out _, out var qpc100ns);
                    var count = frames * Channels;
                    if (samples.Length < count) samples = new float[count];
                    if ((bufferFlags & AudioClientBufferFlags.Silent) != 0 || data == IntPtr.Zero) Array.Clear(samples, 0, count);
                    else Marshal.Copy(data, samples, 0, count);
                    captureClient.ReleaseBuffer(frames);
                    // The packet's first sample was captured at qpc; map it onto the host clock.
                    var age = Stopwatch.GetTimestamp() * (1e7 / Stopwatch.Frequency) - qpc100ns;
                    var hostTime = qpc100ns > 0 ? ShowState.HostClock() - Math.Max(0, age) / 1e7 : ShowState.HostClock();
                    Deliver(samples, frames, hostTime);
                    deliveredUntil = Math.Max(deliveredUntil, hostTime + frames / (double)Rate);
                    packet = captureClient.GetNextPacketSize();
                }
            }
            return null;
        }
        finally
        {
            try { client.Stop(); } catch { }
        }
    }

    private float[] silence = new float[SilenceBlock * Channels];

    private void DeliverSilence(int frames, double hostTime)
    {
        if (silence.Length < frames * Channels) silence = new float[frames * Channels];
        Array.Clear(silence);
        Deliver(silence, frames, hostTime);
    }

    private void Deliver(float[] interleaved, int frames, double hostTime)
    {
        IProgramSink[] sinkList;
        Action<PcmBuffer, double>[] consumerList;
        lock (gate)
        {
            sinkList = sinks.ToArray();
            consumerList = consumers.Values.ToArray();
        }
        if (sinkList.Length > 0)
        {
            var buffer = new AudioBuffer(Rate, Channels, interleaved.AsMemory(0, frames * Channels), hostTime);
            foreach (var sink in sinkList)
            {
                try { sink.WriteAudio(buffer); }
                catch { }
            }
        }
        if (consumerList.Length > 0)
        {
            var planar = PcmBuffer.FromInterleaved(interleaved.AsSpan(0, frames * Channels), Channels, Rate);
            foreach (var consumer in consumerList)
            {
                try { consumer(planar, hostTime); }
                catch { }
            }
        }
    }

    private void SetStatus(ProgramAudioTapStatus next)
    {
        lock (gate) SetStatusLocked(next);
    }

    private void SetStatusLocked(ProgramAudioTapStatus next)
    {
        if (status == next) return;
        status = next;
        var handlers = StatusChanged;
        if (handlers is not null) ThreadPool.QueueUserWorkItem(_ => handlers(next));
    }
}

/// <summary>
/// Activates an audio client that captures one process tree's output: the
/// AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK flavour of ActivateAudioInterfaceAsync.
/// (NAudio 2.2.1 has the structures but keeps them internal, so they're declared here.)
/// </summary>
internal static class ProcessLoopback
{
    private const string VirtualDevice = "VAD\\Process_Loopback";
    private static readonly Guid AudioClientIid = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

    public static AudioClient Activate(int processId, int timeoutMilliseconds = 5000)
    {
        // AUDIOCLIENT_ACTIVATION_PARAMS { ActivationType = PROCESS_LOOPBACK, TargetProcessId, INCLUDE_TARGET_PROCESS_TREE }
        var parameters = Marshal.AllocHGlobal(12);
        // PROPVARIANT holding a VT_BLOB that points at the parameters.
        var variant = Marshal.AllocHGlobal(IntPtr.Size == 8 ? 24 : 16);
        try
        {
            Marshal.WriteInt32(parameters, 0, 1);
            Marshal.WriteInt32(parameters, 4, processId);
            Marshal.WriteInt32(parameters, 8, 0);
            for (var offset = 0; offset < (IntPtr.Size == 8 ? 24 : 16); offset += 4) Marshal.WriteInt32(variant, offset, 0);
            Marshal.WriteInt16(variant, 0, 65); // VT_BLOB
            Marshal.WriteInt32(variant, 8, 12);
            Marshal.WriteIntPtr(variant, IntPtr.Size == 8 ? 16 : 12, parameters);
            var handler = new CompletionHandler();
            ActivateAudioInterfaceAsync(VirtualDevice, AudioClientIid, variant, handler, out _);
            if (!handler.Done.Wait(timeoutMilliseconds)) throw new TimeoutException("Windows didn't answer the capture request");
            if (handler.Error is { } error) throw error;
            return new AudioClient((NAudio.CoreAudioApi.Interfaces.IAudioClient)handler.Result!);
        }
        finally
        {
            Marshal.FreeHGlobal(variant);
            Marshal.FreeHGlobal(parameters);
        }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    // Windows calls the handler on its own thread, so it must be agile (callable from any apartment).
    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject;

    [ComVisible(true)]
    private sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Done = new(false);
        public object? Result;
        public Exception? Error;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            try
            {
                activateOperation.GetActivateResult(out var hr, out var activated);
                if (hr < 0) Error = Marshal.GetExceptionForHR(hr) ?? new COMException("Activation failed", hr);
                else Result = activated;
            }
            catch (Exception error)
            {
                Error = error;
            }
            finally
            {
                Done.Set();
            }
        }
    }
}
