using MxU.Slides.Audio.Decoding;
using MxU.Slides.Core.Scene;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MxU.Slides.Audio;

/// <summary>Which channels of a (possibly many-channel) input a consumer listens to.</summary>
public abstract record InputChannelSelection
{
    /// <summary>A stereo pair starting at <paramref name="Offset"/> (0 = channels 1–2).</summary>
    public sealed record StereoPair(int Offset) : InputChannelSelection;

    /// <summary>One channel, played on both sides.</summary>
    public sealed record Mono(int Channel) : InputChannelSelection;

    public static InputChannelSelection Default { get; } = new StereoPair(0);
}

/// <summary>Picks a consumer's channels out of a device-wide buffer. Port of ChannelExtraction.</summary>
internal static class ChannelExtraction
{
    /// <summary>A stereo buffer of the selected channels at <paramref name="gain"/> (silence when muted).</summary>
    public static PcmBuffer? Extract(PcmBuffer source, InputChannelSelection selection, float gain = 1, bool muted = false)
    {
        if (source.FrameLength == 0) return null;
        var output = new PcmBuffer(source.SampleRate, 2, source.FrameLength) { FrameLength = source.FrameLength };
        var channels = source.ChannelCount;
        int left, right;
        switch (selection)
        {
            case InputChannelSelection.StereoPair pair when channels == 1:
                (left, right) = (0, 0);
                _ = pair;
                break;
            case InputChannelSelection.StereoPair pair:
                var clamped = pair.Offset >= 0 && pair.Offset + 1 < channels ? pair.Offset : 0;
                (left, right) = (clamped, clamped + 1);
                break;
            case InputChannelSelection.Mono mono:
                var channel = mono.Channel >= 0 && mono.Channel < channels ? mono.Channel : 0;
                (left, right) = (channel, channel);
                break;
            default:
                (left, right) = (0, Math.Min(1, channels - 1));
                break;
        }
        if (muted) return output;
        for (var frame = 0; frame < source.FrameLength; frame++)
        {
            output.Data[0][frame] = source.Data[left][frame] * gain;
            output.Data[1][frame] = source.Data[right][frame] * gain;
        }
        return output;
    }
}

/// <summary>Hands each captured buffer to every tap, cut down to the channels it asked for. Port of InputFanout.</summary>
internal sealed class InputFanout
{
    private sealed class Tap(InputChannelSelection channels, bool trimmed, Action<PcmBuffer, double> sink)
    {
        public InputChannelSelection Channels = channels;
        public readonly bool Trimmed = trimmed;
        public readonly Action<PcmBuffer, double> Sink = sink;
    }

    private readonly Lock gate = new();
    private readonly Dictionary<Guid, Tap> taps = [];

    /// <param name="trimmed">False for a tap that wants the raw input, ignoring the device's trim and mute (a meter, say).</param>
    public Guid Add(InputChannelSelection channels, Action<PcmBuffer, double> sink, bool trimmed = true)
    {
        var token = Guid.NewGuid();
        lock (gate) taps[token] = new Tap(channels, trimmed, sink);
        return token;
    }

    public void Update(Guid token, InputChannelSelection channels)
    {
        lock (gate)
            if (taps.TryGetValue(token, out var tap)) tap.Channels = channels;
    }

    public void Remove(Guid token)
    {
        lock (gate) taps.Remove(token);
    }

    public int Count
    {
        get { lock (gate) return taps.Count; }
    }

    public void Push(PcmBuffer buffer, double hostSeconds, float gain = 1, bool muted = false)
    {
        List<(InputChannelSelection Channels, bool Trimmed, Action<PcmBuffer, double> Sink)> snapshot;
        lock (gate) snapshot = taps.Values.Select(t => (t.Channels, t.Trimmed, t.Sink)).ToList();
        foreach (var tap in snapshot)
        {
            if (ChannelExtraction.Extract(buffer, tap.Channels, tap.Trimmed ? gain : 1, tap.Trimmed && muted) is not { } extracted) continue;
            try { tap.Sink(extracted, hostSeconds); }
            catch { }
        }
    }
}

/// <summary>
/// Something other than a Windows device that delivers input audio into a hub (a DeckLink
/// card's embedded audio, an NDI source…). Port of ExternalInputFeed.
/// </summary>
public interface IExternalInputFeed
{
    void Start(AudioInputHub hub);

    void Stop();
}

/// <summary>
/// One capture device shared by everything that listens to it: the mixer strip, a
/// recording, a stream. It captures every channel (WASAPI shared mode), converts to
/// 48 kHz, applies the device's trim and mute, and hands each tap the channels it asked
/// for. If the device is unplugged it waits and starts again when it returns.
/// Port of AudioInputHub.swift.
/// <para>
/// Threading: taps are called on the capture thread (or the external feed's thread) and
/// must return quickly. Everything else is thread-safe.
/// </para>
/// </summary>
public sealed class AudioInputHub : IDisposable
{
    public const int Rate = 48_000;

    /// <summary>Why a capture couldn't start.</summary>
    public sealed class CaptureException(CaptureException.Kind reason, string message) : Exception(message)
    {
        public enum Kind
        {
            DeviceNotFound,
            EngineStartFailed,
            FormatUnavailable,
        }

        public Kind Reason { get; } = reason;
    }

    private readonly InputFanout fanout = new();
    private readonly Func<string, (float Gain, bool Muted)> trim;
    private readonly bool external;
    private readonly IDisposable? deviceSubscription;
    private WasapiCapture? capture;
    private SincResampler? resampler;
    private int captureRate;
    private int captureChannels;
    private bool running;
    private bool disposed;

    /// <summary>Opens and starts capturing a Windows input device.</summary>
    /// <exception cref="CaptureException">The device isn't there or won't start.</exception>
    public AudioInputHub(string deviceUid, Func<string, (float Gain, bool Muted)> trim)
    {
        DeviceUid = deviceUid;
        this.trim = trim;
        Lock.WithLock(StartCapture);
        running = true;
        deviceSubscription = AudioDeviceList.ListenForChanges(OnDeviceConfigurationChanged);
    }

    /// <summary>A hub with no capture of its own (tests).</summary>
    internal static AudioInputHub ForTests(string deviceUid, Func<string, (float Gain, bool Muted)>? trim = null) =>
        new(deviceUid, trim ?? (_ => (1, false)), external: false);

    /// <summary>A hub whose audio comes from an <see cref="IExternalInputFeed"/> calling <see cref="Push"/>.</summary>
    public static AudioInputHub External(string deviceUid, Func<string, (float Gain, bool Muted)> trim) => new(deviceUid, trim, external: true);

    private AudioInputHub(string deviceUid, Func<string, (float Gain, bool Muted)> trim, bool external)
    {
        DeviceUid = deviceUid;
        this.trim = trim;
        this.external = external;
    }

    public string DeviceUid { get; }

    internal EngineStateLock Lock { get; } = new();

    /// <summary>True while the device is delivering (false while it's unplugged).</summary>
    public bool IsCapturing => Lock.WithLock(() => capture is not null);

    /// <summary>Feeds a buffer in (an external source, or tests): applies the trim and fans out.</summary>
    public void Push(PcmBuffer buffer, double hostSeconds)
    {
        var level = trim(DeviceUid);
        fanout.Push(buffer, hostSeconds, level.Gain, level.Muted);
    }

    /// <summary>Listens to <paramref name="channels"/>; <paramref name="sink"/> gets 48 kHz stereo and the host-clock time of its first sample.</summary>
    public Guid AddTap(InputChannelSelection channels, Action<PcmBuffer, double> sink, bool trimmed = true) =>
        fanout.Add(channels, sink, trimmed);

    public void UpdateTap(Guid token, InputChannelSelection channels) => fanout.Update(token, channels);

    public void RemoveTap(Guid token) => fanout.Remove(token);

    public int TapCount => fanout.Count;

    /// <summary>
    /// A DeckLink-style packet (signed 32-bit, interleaved, 48 kHz) as a 48 kHz device-wide
    /// buffer; null when the packet is shorter than it claims.
    /// </summary>
    public static PcmBuffer? DeviceBuffer(ReadOnlySpan<int> int32Interleaved, int frames, int channels)
    {
        if (frames <= 0 || channels <= 0 || int32Interleaved.Length < frames * channels) return null;
        var buffer = new PcmBuffer(Rate, channels, frames) { FrameLength = frames };
        const float scale = 1f / (1u << 31);
        for (var channel = 0; channel < channels; channel++)
        {
            var destination = buffer.Data[channel];
            for (var frame = 0; frame < frames; frame++) destination[frame] = int32Interleaved[frame * channels + channel] * scale;
        }
        return buffer;
    }

    /// <summary>A device-wide buffer at another rate, converted to 48 kHz (keeping every channel).</summary>
    internal static PcmBuffer? ConvertDeviceBuffer(PcmBuffer buffer, SincResampler resampler)
    {
        if (buffer.FrameLength == 0) return null;
        var output = new PcmBuffer(resampler.ToRate, buffer.ChannelCount, resampler.MaxOutput(buffer.FrameLength));
        output.FrameLength = resampler.Process(buffer.Data, 0, buffer.FrameLength, output.Data, 0);
        return output.FrameLength > 0 ? output : null;
    }

    // Lock held.
    private void StartCapture()
    {
        var started = Mta.Run<(WasapiCapture? Capture, CaptureException? Error)>(() =>
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = AudioDeviceList.OpenEndpoint(enumerator, DataFlow.Capture, DeviceUid);
            if (device is null) return (null, new CaptureException(CaptureException.Kind.DeviceNotFound, $"No input device {DeviceUid}"));
            try
            {
                var made = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 20);
                var format = made.WaveFormat;
                if (format.SampleRate <= 0 || format.Channels <= 0)
                    return (null, new CaptureException(CaptureException.Kind.FormatUnavailable, "The input reported no format"));
                captureRate = format.SampleRate;
                captureChannels = format.Channels;
                resampler = captureRate == Rate ? null : new SincResampler(captureRate, Rate, captureChannels);
                made.DataAvailable += (_, e) => Captured(made, e);
                made.RecordingStopped += (_, e) => CaptureStopped(made, e.Exception);
                made.StartRecording();
                return (made, null);
            }
            catch (Exception error)
            {
                return (null, new CaptureException(CaptureException.Kind.EngineStartFailed, error.Message));
            }
        });
        if (started.Error is { } failure) throw failure;
        capture = started.Capture ?? throw new CaptureException(CaptureException.Kind.EngineStartFailed, "Windows audio isn't available");
    }

    private void Captured(WasapiCapture source, WaveInEventArgs e)
    {
        RealtimeAudioCallback.Scope(() =>
        {
            var buffer = Decode(source.WaveFormat, e.Buffer, e.BytesRecorded);
            if (buffer is null) return;
            var device = resampler is { } converter ? ConvertDeviceBuffer(buffer, converter) : buffer;
            if (device is null) return;
            // Roughly when the first sample was heard: now, less the block's length.
            var hostSeconds = ShowState.HostClock() - buffer.FrameLength / (double)Math.Max(1, captureRate);
            var level = trim(DeviceUid);
            fanout.Push(device, hostSeconds, level.Gain, level.Muted);
        });
    }

    private static PcmBuffer? Decode(WaveFormat format, byte[] bytes, int count)
    {
        var channels = format.Channels;
        var bytesPerSample = format.BitsPerSample / 8;
        if (channels <= 0 || bytesPerSample <= 0) return null;
        var frames = count / (bytesPerSample * channels);
        if (frames <= 0) return null;
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
            || format is WaveFormatExtensible { SubFormat: var sub } && sub == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT;
        var buffer = new PcmBuffer(format.SampleRate, channels, frames) { FrameLength = frames };
        for (var frame = 0; frame < frames; frame++)
            for (var channel = 0; channel < channels; channel++)
            {
                var at = (frame * channels + channel) * bytesPerSample;
                buffer.Data[channel][frame] = (isFloat, bytesPerSample) switch
                {
                    (true, 4) => BitConverter.ToSingle(bytes, at),
                    (false, 2) => BitConverter.ToInt16(bytes, at) / 32768f,
                    (false, 3) => (bytes[at] | bytes[at + 1] << 8 | (sbyte)bytes[at + 2] << 16) / 8388608f,
                    (false, 4) => BitConverter.ToInt32(bytes, at) / 2147483648f,
                    _ => 0,
                };
            }
        return buffer;
    }

    private void CaptureStopped(WasapiCapture source, Exception? error)
    {
        // A device pulled out stops the capture with an error; wait for it to come back.
        AudioThreads.Chores.Async(() => Lock.WithLock(() =>
        {
            if (capture != source) return;
            capture = null;
            try { source.Dispose(); } catch { }
            _ = error;
        }));
    }

    /// <summary>Windows reported a device change: restart a capture whose device is back. Never blocks the caller.</summary>
    internal void OnDeviceConfigurationChanged()
    {
        AudioThreads.Chores.Async(() => Lock.WithLock(() =>
        {
            if (!running || disposed || external || capture is not null) return;
            if (InputHotplug.Decide(AudioDeviceList.InputDevice(DeviceUid) is not null, captureLive: false) != InputHotplug.Action.Start) return;
            try { StartCapture(); }
            catch (CaptureException) { }
        }));
    }

    public void Dispose()
    {
        Lock.WithLock(() =>
        {
            disposed = true;
            running = false;
            var stopping = capture;
            capture = null;
            if (stopping is not null)
            {
                try { stopping.StopRecording(); } catch { }
                try { stopping.Dispose(); } catch { }
            }
        });
        deviceSubscription?.Dispose();
    }
}

/// <summary>
/// Shares one <see cref="AudioInputHub"/> per device between everything that wants it,
/// counting holders and closing the device when the last lets go. Port of AudioInputHubPool.
/// </summary>
public sealed class AudioInputHubPool
{
    public static AudioInputHubPool Shared { get; } = new();

    private readonly Lock gate = new();
    private readonly Lock providerGate = new();
    private readonly Dictionary<string, (AudioInputHub Hub, int RefCount)> hubs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IExternalInputFeed> feeds = new(StringComparer.Ordinal);
    private Func<string, (float Gain, bool Muted)>? trimProvider;
    private Func<string, IExternalInputFeed?>? externalFeedProvider;

    /// <summary>Builds hubs for tests instead of opening devices.</summary>
    internal Func<string, Func<string, (float Gain, bool Muted)>, AudioInputHub>? HubFactory { get; set; }

    /// <summary>Each device's trim and mute (the mixer's device strip).</summary>
    public Func<string, (float Gain, bool Muted)>? TrimProvider
    {
        get { lock (providerGate) return trimProvider; }
        set { lock (providerGate) trimProvider = value; }
    }

    /// <summary>A feed for ids that aren't Windows devices (null for those that are).</summary>
    public Func<string, IExternalInputFeed?>? ExternalFeedProvider
    {
        get { lock (providerGate) return externalFeedProvider; }
        set { lock (providerGate) externalFeedProvider = value; }
    }

    /// <exception cref="AudioInputHub.CaptureException">The device can't be opened.</exception>
    public AudioInputHub Acquire(string deviceUid)
    {
        var feed = ExternalFeedProvider?.Invoke(deviceUid);
        lock (gate)
        {
            if (hubs.TryGetValue(deviceUid, out var existing))
            {
                hubs[deviceUid] = (existing.Hub, existing.RefCount + 1);
                return existing.Hub;
            }
            (float Gain, bool Muted) Trim(string uid) => TrimProvider?.Invoke(uid) ?? (1, false);
            AudioInputHub hub;
            if (feed is not null)
            {
                hub = AudioInputHub.External(deviceUid, Trim);
                feed.Start(hub);
                feeds[deviceUid] = feed;
            }
            else
            {
                hub = HubFactory?.Invoke(deviceUid, Trim) ?? new AudioInputHub(deviceUid, Trim);
            }
            hubs[deviceUid] = (hub, 1);
            return hub;
        }
    }

    public void Release(string deviceUid)
    {
        AudioInputHub? dying = null;
        IExternalInputFeed? stoppedFeed = null;
        lock (gate)
        {
            if (!hubs.TryGetValue(deviceUid, out var entry)) return;
            if (entry.RefCount <= 1)
            {
                hubs.Remove(deviceUid);
                dying = entry.Hub;
                if (feeds.Remove(deviceUid, out var feed)) stoppedFeed = feed;
            }
            else
            {
                hubs[deviceUid] = (entry.Hub, entry.RefCount - 1);
            }
        }
        stoppedFeed?.Stop();
        dying?.Dispose();
    }

    public int HubCount
    {
        get { lock (gate) return hubs.Count; }
    }
}
