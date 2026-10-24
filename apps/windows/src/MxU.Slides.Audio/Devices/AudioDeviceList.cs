using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using MxU.Slides.Core.Threading;

namespace MxU.Slides.Audio;

/// <summary>A place sound can go: a sound card, interface, HDMI screen or virtual cable.</summary>
/// <param name="Uid">The Windows endpoint id: stable across restarts, what settings store.</param>
/// <param name="ChannelCount">Channels in the device's shared-mode format.</param>
/// <param name="SampleRate">The rate Windows mixes that device at.</param>
public sealed record AudioOutputDevice(string Uid, string Name, int ChannelCount, int SampleRate = 48_000);

/// <summary>A place sound comes from: a microphone, line in, interface inputs, or an external feed.</summary>
public sealed record AudioInputDevice(string Uid, string Name, int ChannelCount, int SampleRate = 48_000);

/// <summary>
/// The machine's audio devices, read from Windows (MMDevice). Every call is safe on a
/// machine with no audio at all (it just returns nothing) and on any thread. Port of
/// AudioDeviceList.swift.
/// </summary>
public static class AudioDeviceList
{
    private static readonly Lock externalGate = new();
    private static List<AudioInputDevice> externalInputs = [];

    public static IReadOnlyList<AudioOutputDevice> OutputDevices() =>
        Mta.Run(() => Endpoints(DataFlow.Render).Select(d => new AudioOutputDevice(d.Uid, d.Name, d.Channels, d.Rate)).ToList())
        ?? [];

    /// <summary>Where Windows sends sound by default (the multimedia role); null when there is no device.</summary>
    public static AudioOutputDevice? DefaultOutputDevice() => Mta.Run(() =>
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return null;
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var (channels, rate) = Format(device);
        return new AudioOutputDevice(device.ID, device.FriendlyName, channels, rate);
    });

    /// <summary>Inputs Windows knows about, then any external feeds (DeckLink, NDI…) the app registered.</summary>
    public static IReadOnlyList<AudioInputDevice> InputDevices()
    {
        var windows = Mta.Run(() => Endpoints(DataFlow.Capture).Select(d => new AudioInputDevice(d.Uid, d.Name, d.Channels, d.Rate)).ToList()) ?? [];
        lock (externalGate) return [.. windows, .. externalInputs];
    }

    public static AudioInputDevice? InputDevice(string uid) => InputDevices().FirstOrDefault(d => d.Uid == uid);

    /// <summary>Lists capture sources that aren't Windows devices (their audio arrives through an <see cref="IExternalInputFeed"/>).</summary>
    public static void SetExternalInputDevices(IEnumerable<AudioInputDevice> devices)
    {
        lock (externalGate) externalInputs = devices.ToList();
    }

    /// <summary>
    /// Calls <paramref name="onChange"/> when devices come, go or the default changes
    /// (bursts are coalesced). It arrives on <paramref name="context"/> when given, else a
    /// thread-pool thread. Dispose the token to stop listening.
    /// </summary>
    public static IDisposable ListenForChanges(Action onChange, SynchronizationContext? context = null)
    {
        void Handler()
        {
            if (context is null) onChange();
            else context.Post(_ => onChange(), null);
        }
        AudioDeviceWatcher.Shared.Changed += Handler;
        return new Subscription(() => AudioDeviceWatcher.Shared.Changed -= Handler);
    }

    private sealed class Subscription(Action cancel) : IDisposable
    {
        private Action? cancel = cancel;

        public void Dispose() => Interlocked.Exchange(ref cancel, null)?.Invoke();
    }

    private static List<(string Uid, string Name, int Channels, int Rate)> Endpoints(DataFlow flow)
    {
        var found = new List<(string, string, int, int)>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            try
            {
                var (channels, rate) = Format(device);
                found.Add((device.ID, device.FriendlyName, channels, rate));
            }
            catch
            {
                // A device that vanished mid-enumeration or won't describe itself is skipped.
            }
            finally
            {
                device.Dispose();
            }
        }
        return found;
    }

    /// <summary>The device's shared-mode channel count and rate.</summary>
    internal static (int Channels, int Rate) Format(MMDevice device)
    {
        try
        {
            using var client = device.AudioClient;
            var mix = client.MixFormat;
            return (Math.Max(1, mix.Channels), mix.SampleRate > 0 ? mix.SampleRate : 48_000);
        }
        catch
        {
            return (2, 48_000);
        }
    }

    /// <summary>The endpoint for <paramref name="uid"/> (null = the default), if it is plugged in. Call on an MTA thread.</summary>
    internal static MMDevice? OpenEndpoint(MMDeviceEnumerator enumerator, DataFlow flow, string? uid)
    {
        if (uid is null)
            return enumerator.HasDefaultAudioEndpoint(flow, Role.Multimedia) ? enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia) : null;
        try
        {
            var device = enumerator.GetDevice(uid);
            if (device.State == DeviceState.Active) return device;
            device.Dispose();
            return null;
        }
        catch
        {
            return null;
        }
    }

    internal static string? DefaultEndpointId(DataFlow flow) => Mta.Run(() =>
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!enumerator.HasDefaultAudioEndpoint(flow, Role.Multimedia)) return null;
        using var device = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
        return device.ID;
    });
}

/// <summary>
/// One registration with Windows for device notifications, shared by every engine and
/// input. Windows calls it on its own thread, which must not be held up, so each burst
/// of notifications becomes one <see cref="Changed"/> on a thread-pool thread ~150 ms later.
/// </summary>
internal sealed class AudioDeviceWatcher : IMMNotificationClient
{
    public static readonly AudioDeviceWatcher Shared = new();

    private readonly Lock gate = new();
    private MMDeviceEnumerator? enumerator;
    private ITimer? debounce;
    private Action? changed;

    private AudioDeviceWatcher() { }

    /// <summary>Something about the devices changed (thread-pool thread).</summary>
    public event Action? Changed
    {
        add
        {
            lock (gate)
            {
                changed += value;
                Register();
            }
        }
        remove
        {
            lock (gate) changed -= value;
        }
    }

    private void Register()
    {
        if (enumerator is not null) return;
        enumerator = Mta.Run(() =>
        {
            var made = new MMDeviceEnumerator();
            made.RegisterEndpointNotificationCallback(this);
            return made;
        });
    }

    /// <summary>Raise a change now-ish, as if Windows had reported one (tests, and after a stream error).</summary>
    public void Poke()
    {
        lock (gate)
        {
            // Listeners may look the devices up again, which is slow: not on the show clock's thread.
            debounce ??= ShowTimeProvider.Shared.CreateTimer(_ => AudioThreads.Events.Async(Fire), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            debounce.Change(TimeSpan.FromMilliseconds(150), Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        Action? handlers;
        lock (gate) handlers = changed;
        handlers?.Invoke();
    }

    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState) => Poke();

    void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId) => Poke();

    void IMMNotificationClient.OnDeviceRemoved(string deviceId) => Poke();

    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (role == Role.Multimedia) Poke();
    }

    void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
    {
        // Volume and name changes don't matter; a format change breaks the stream, which reopens itself.
    }
}

/// <summary>
/// Runs Windows audio (COM) calls on a multithreaded-apartment thread. Called from the WPF
/// UI thread (single-threaded apartment) the work hops to the thread pool and waits.
/// Any failure (no audio service, no devices) comes back as the default value.
/// </summary>
internal static class Mta
{
    public static T? Run<T>(Func<T> work)
    {
        try
        {
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA) return work();
            return AudioThreads.Devices.Sync(work);
        }
        catch
        {
            return default;
        }
    }
}
