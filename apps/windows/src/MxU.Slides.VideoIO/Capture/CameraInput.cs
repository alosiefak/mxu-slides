using System.Runtime.InteropServices;
using MxU.Slides.Core.Scene;
using MxU.Slides.VideoIO.Common;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using WinRT;

namespace MxU.Slides.VideoIO.Capture;

/// <summary>
/// A camera or capture card as a live input, through Windows' own capture stack
/// (Windows.Media.Capture with a MediaFrameReader asking for BGRA8, so Windows decodes
/// MJPG and converts NV12/YUY2 for us). On start it takes the device for itself when it
/// can, and then — like the Mac — keeps the size the device starts in and picks the frame
/// rate with <see cref="CaptureFormatChoice"/>. If another app already has the device it
/// shares it read-only and leaves the format alone. The reader runs in real-time mode, so
/// late frames are dropped rather than queued.
/// <para>
/// States: Starting, Receiving, Failed (detail: why — including camera access being off
/// in Windows privacy settings), Offline (the device went away mid-service; the inventory
/// starts a new input when it's plugged back in).
/// </para>
/// Port of MediaEngine/CaptureSource.swift (AVCaptureSession there).
/// </summary>
public sealed class CameraInput : VideoInputSourceBase
{
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly double? frameRate;
    private MediaCapture? capture;
    private MediaFrameReader? reader;
    private volatile bool stopped;

    /// <param name="deviceId">A <see cref="CameraDevice.Id"/>.</param>
    /// <param name="frameRate">The operator's preferred rate, or null for automatic.</param>
    public CameraInput(string deviceId, string name, double? frameRate = null) : base(name, VideoInputState.Starting)
    {
        DeviceId = deviceId;
        this.frameRate = frameRate;
    }

    public string DeviceId { get; }

    /// <summary>The size frames arrive at (0×0 before the first).</summary>
    public (int Width, int Height) NaturalSize => LatestFrame() is { } frame ? (frame.Width, frame.Height) : (0, 0);

    /// <summary>What format was chosen, e.g. "format: HD Webcam 1920x1080 @29.97 (default @30.00)".</summary>
    public string FormatNote { get; private set; } = "";

    /// <summary>Opens the device and starts frames flowing, in the background; watch <see cref="IVideoInputSource.State"/>.</summary>
    public void Start() => _ = Task.Run(StartAsync);

    /// <summary>Opens the device and starts frames flowing. Never throws: failures end up in the state.</summary>
    public async Task StartAsync()
    {
        await serial.WaitAsync().ConfigureAwait(false);
        try
        {
            if (stopped || capture is not null) return;
            var (opened, exclusive, error) = await OpenAsync().ConfigureAwait(false);
            if (opened is null)
            {
                Fail(error);
                return;
            }
            capture = opened;
            opened.Failed += (_, failure) => DeviceFailed(failure.Message);

            var source = opened.FrameSources.Values
                .Where(s => s.Info.SourceKind == MediaFrameSourceKind.Color)
                .OrderBy(s => s.Info.MediaStreamType switch { MediaStreamType.VideoRecord => 0, MediaStreamType.VideoPreview => 1, _ => 2 })
                .FirstOrDefault();
            if (source is null)
            {
                Fail("this device has no colour video stream");
                return;
            }
            if (exclusive) await ApplyBestFormatAsync(source).ConfigureAwait(false);
            else Note($"format: {Name} busy, left at default");

            var frames = await opened.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8).AsTask().ConfigureAwait(false);
            frames.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            frames.FrameArrived += OnFrameArrived;
            var status = await frames.StartAsync().AsTask().ConfigureAwait(false);
            if (status != MediaFrameReaderStartStatus.Success)
            {
                frames.Dispose();
                Fail(status == MediaFrameReaderStartStatus.ExclusiveControlNotAvailable
                    ? "the device is in use by another app"
                    : $"capture didn't start ({status})");
                return;
            }
            reader = frames;
            if (stopped) await CloseAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Fail($"capture setup failed: {ex.Message}");
        }
        finally
        {
            serial.Release();
        }
    }

    public override void Stop()
    {
        if (stopped) return;
        stopped = true;
        SetState(VideoInputState.Stopped);
        Frames.Clear();
        _ = Task.Run(async () =>
        {
            await serial.WaitAsync().ConfigureAwait(false);
            try { await CloseAsync().ConfigureAwait(false); }
            finally { serial.Release(); }
        });
    }

    /// <summary>The device's formats as <see cref="CaptureFormatChoice.Candidate"/>s, and the one it's in now.</summary>
    internal static (List<CaptureFormatChoice.Candidate> All, CaptureFormatChoice.Candidate Active) Candidates(IReadOnlyList<MediaFrameFormat> formats, MediaFrameFormat? current)
    {
        static CaptureFormatChoice.Candidate Describe(MediaFrameFormat format, int index) => CaptureFormatChoice.CandidateFor(
            index, (int)(format.VideoFormat?.Width ?? 0), (int)(format.VideoFormat?.Height ?? 0), format.Subtype ?? "",
            format.FrameRate?.Numerator ?? 0, format.FrameRate?.Denominator ?? 0);

        var all = formats.Select(Describe).ToList();
        var activeIndex = current is null ? -1 : formats.ToList().FindIndex(format => Same(format, current));
        var active = current is null ? (all.Count > 0 ? all[0] : new CaptureFormatChoice.Candidate(0, 0, 0, 0, [])) : Describe(current, Math.Max(0, activeIndex));
        return (all, active);
    }

    /// <summary>
    /// Copies a frame Windows handed over into the ring: BGRA8 (converting if Windows sent
    /// something else), alpha forced to 255. Exposed for the tests, which feed it bitmaps
    /// made in memory because CI has no camera.
    /// </summary>
    internal static unsafe VideoFrameInfo? CopyBitmap(SoftwareBitmap bitmap, LatestFrameStore store, double hostTime)
    {
        var converted = bitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8 ? null : SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8);
        var source = converted ?? bitmap;
        try
        {
            using var buffer = source.LockBuffer(BitmapBufferAccessMode.Read);
            var plane = buffer.GetPlaneDescription(0);
            using var reference = buffer.CreateReference();
            reference.As<IMemoryBufferByteAccess>().GetBuffer(out var data, out var capacity);
            if (data == null || plane.Width <= 0 || plane.Height <= 0 || capacity < plane.StartIndex + plane.Stride * (plane.Height - 1) + plane.Width * 4)
                return null;
            var pixels = new ReadOnlySpan<byte>(data + plane.StartIndex, (int)capacity - plane.StartIndex);
            var frame = store.PublishCopy(pixels, plane.Stride, plane.Width, plane.Height, forceOpaque: true, hostTime);
            return new VideoFrameInfo(frame.Width, frame.Height);
        }
        finally
        {
            converted?.Dispose();
        }
    }

    internal readonly record struct VideoFrameInfo(int Width, int Height);

    private async Task<(MediaCapture?, bool Exclusive, string Error)> OpenAsync()
    {
        foreach (var sharing in new[] { MediaCaptureSharingMode.ExclusiveControl, MediaCaptureSharingMode.SharedReadOnly })
        {
            var attempt = new MediaCapture();
            try
            {
                await attempt.InitializeAsync(new MediaCaptureInitializationSettings
                {
                    VideoDeviceId = DeviceId,
                    StreamingCaptureMode = StreamingCaptureMode.Video,
                    MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                    SharingMode = sharing,
                }).AsTask().ConfigureAwait(false);
                return (attempt, sharing == MediaCaptureSharingMode.ExclusiveControl, "");
            }
            catch (UnauthorizedAccessException)
            {
                attempt.Dispose();
                return (null, false,
                    "camera access is off — turn on Settings › Privacy & security › Camera › Let desktop apps access your camera");
            }
            catch (Exception ex) when (sharing == MediaCaptureSharingMode.ExclusiveControl)
            {
                attempt.Dispose();
                Note($"format: {Name} exclusive open failed ({ex.Message}), sharing instead");
            }
            catch (Exception ex)
            {
                attempt.Dispose();
                return (null, false, $"the device would not open: {ex.Message}");
            }
        }
        return (null, false, "the device would not open");
    }

    /// <summary>Port of CaptureSource.applyBestFormat: same size, best rate (or the operator's).</summary>
    private async Task ApplyBestFormatAsync(MediaFrameSource source)
    {
        var formats = source.SupportedFormats.Where(f => f.VideoFormat is not null).ToList();
        var (candidates, active) = Candidates(formats, source.CurrentFormat);
        var defaultRate = active.FrameRates.FirstOrDefault();
        var choice = CaptureFormatChoice.Best(candidates, active, frameRate);
        if (choice is not null && choice.Index < formats.Count && !Same(formats[choice.Index], source.CurrentFormat))
        {
            try
            {
                await source.SetFormatAsync(formats[choice.Index]).AsTask().ConfigureAwait(false);
                Note($"format: {Name} {active.Width}x{active.Height} @{choice.FrameRate:0.00} (default @{defaultRate:0.00})");
                return;
            }
            catch (Exception ex)
            {
                Note($"format: {Name} could not switch ({ex.Message})");
            }
        }
        Note($"format: {Name} {active.Width}x{active.Height} left at default @{defaultRate:0.00}");
    }

    private static bool Same(MediaFrameFormat? left, MediaFrameFormat? right) =>
        left is not null && right is not null
        && left.Subtype == right.Subtype
        && left.VideoFormat?.Width == right.VideoFormat?.Width && left.VideoFormat?.Height == right.VideoFormat?.Height
        && left.FrameRate?.Numerator == right.FrameRate?.Numerator && left.FrameRate?.Denominator == right.FrameRate?.Denominator;

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (stopped) return;
        try
        {
            using var reference = sender.TryAcquireLatestFrame();
            using var bitmap = reference?.VideoMediaFrame?.SoftwareBitmap;
            if (bitmap is null) return;
            if (CopyBitmap(bitmap, Frames, ShowState.HostClock()) is null) return;
            SetStateIf(state => state.Status != VideoInputStatus.Stopped, VideoInputState.ReceivingFrom(Name));
            if (Frames.Latest is { } frame) Deliver(frame);
        }
        catch (Exception ex)
        {
            VideoIOLog.Note("input.camera", $"{Name}: frame dropped: {ex.Message}");
        }
    }

    private void DeviceFailed(string message)
    {
        VideoIOLog.Note("input.camera", $"{Name}: camera stopped: {message}");
        SetStateIf(state => state.Status != VideoInputStatus.Stopped, VideoInputState.OfflineBecause(message));
        _ = Task.Run(async () =>
        {
            await serial.WaitAsync().ConfigureAwait(false);
            try { await CloseAsync().ConfigureAwait(false); }
            finally { serial.Release(); }
        });
    }

    private void Fail(string message)
    {
        VideoIOLog.Note("input.camera", $"{Name}: {message}");
        SetStateIf(state => state.Status != VideoInputStatus.Stopped, VideoInputState.FailedWith(message));
    }

    private void Note(string detail)
    {
        FormatNote = detail;
        VideoIOLog.Note("input.camera", detail);
    }

    private async Task CloseAsync()
    {
        var closingReader = reader;
        var closingCapture = capture;
        reader = null;
        capture = null;
        if (closingReader is not null)
        {
            closingReader.FrameArrived -= OnFrameArrived;
            try { await closingReader.StopAsync().AsTask().ConfigureAwait(false); }
            catch (Exception)
            {
                // A device that vanished can't be stopped; disposing still lets it go.
            }
            closingReader.Dispose();
        }
        closingCapture?.Dispose();
    }
}

/// <summary>Raw access to a WinRT memory buffer's bytes (a COM interface Windows documents but doesn't project).</summary>
[ComImport, Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal unsafe interface IMemoryBufferByteAccess
{
    void GetBuffer(out byte* buffer, out uint capacity);
}
