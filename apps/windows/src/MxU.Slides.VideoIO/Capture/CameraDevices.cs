using Windows.Devices.Enumeration;

namespace MxU.Slides.VideoIO.Capture;

/// <summary>
/// A camera or capture card Windows knows about. <see cref="Id"/> is the device
/// interface path (\\?\USB#VID_…), stable across launches, which is what an input
/// stores as its source id.
/// </summary>
public sealed record CameraDevice(string Id, string Name);

/// <summary>Lists video capture devices (webcams, USB/PCIe capture cards with a Windows driver).</summary>
public static class CameraDevices
{
    /// <summary>Every enabled capture device, by name. Empty (never throws) when enumeration fails.</summary>
    public static async Task<IReadOnlyList<CameraDevice>> ListAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture).AsTask(cancellationToken).ConfigureAwait(false);
            return [.. devices.Where(d => d.IsEnabled).Select(d => new CameraDevice(d.Id, d.Name)).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }
    }

    /// <summary>
    /// The frame rates the operator can pick for a camera at the size it starts in, slowest
    /// first (for the input's rate menu; see <see cref="CaptureFormatChoice.Rates"/>). Opens
    /// the device read-only for a moment, so it works while the camera is in use. Empty when
    /// the device can't be opened.
    /// </summary>
    public static async Task<IReadOnlyList<double>> FrameRatesAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        using var capture = new Windows.Media.Capture.MediaCapture();
        try
        {
            await capture.InitializeAsync(new Windows.Media.Capture.MediaCaptureInitializationSettings
            {
                VideoDeviceId = deviceId,
                StreamingCaptureMode = Windows.Media.Capture.StreamingCaptureMode.Video,
                MemoryPreference = Windows.Media.Capture.MediaCaptureMemoryPreference.Cpu,
                SharingMode = Windows.Media.Capture.MediaCaptureSharingMode.SharedReadOnly,
            }).AsTask(cancellationToken).ConfigureAwait(false);
            var source = capture.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == Windows.Media.Capture.Frames.MediaFrameSourceKind.Color);
            if (source is null) return [];
            var (candidates, active) = CameraInput.Candidates([.. source.SupportedFormats.Where(f => f.VideoFormat is not null)], source.CurrentFormat);
            return CaptureFormatChoice.Rates(candidates, active);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }
    }
}

/// <summary>
/// Which capture devices are plugged in right now, kept current by a Windows device
/// watcher, so the inventory can suspend an input whose camera was unplugged and start
/// it again on replug. <see cref="Changed"/> is raised on a Windows thread pool thread.
/// Plays the part of AVCaptureDevice's connect/disconnect notifications on the Mac.
/// </summary>
public sealed class CameraPresence : IDisposable
{
    private const string InterfaceEnabled = "System.Devices.InterfaceEnabled";

    private readonly Lock gate = new();
    private readonly HashSet<string> present = new(StringComparer.OrdinalIgnoreCase);
    private readonly DeviceWatcher? watcher;

    public CameraPresence()
    {
        try
        {
            watcher = DeviceInformation.CreateWatcher(DeviceClass.VideoCapture);
            watcher.Added += (_, device) => Update(device.Id, device.IsEnabled);
            watcher.Removed += (_, update) => Update(update.Id, false);
            watcher.Updated += (_, update) =>
            {
                if (update.Properties.TryGetValue(InterfaceEnabled, out var enabled) && enabled is bool isEnabled) Update(update.Id, isEnabled);
            };
            watcher.EnumerationCompleted += (_, _) =>
            {
                IsReady = true;
                Changed?.Invoke();
            };
            watcher.Start();
        }
        catch (Exception)
        {
            watcher = null;
        }
    }

    /// <summary>True once the first full list has come in; before that, <see cref="IsPresent"/> can't be trusted.</summary>
    public bool IsReady { get; private set; }

    public event Action? Changed;

    public bool IsPresent(string deviceId)
    {
        lock (gate) return present.Contains(deviceId);
    }

    public IReadOnlyList<string> PresentIds
    {
        get { lock (gate) return [.. present]; }
    }

    public void Dispose()
    {
        try
        {
            if (watcher is { Status: DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted }) watcher.Stop();
        }
        catch (Exception)
        {
            // The watcher may already be stopping.
        }
    }

    private void Update(string id, bool isPresent)
    {
        bool changed;
        lock (gate) changed = isPresent ? present.Add(id) : present.Remove(id);
        if (changed && IsReady) Changed?.Invoke();
    }
}
