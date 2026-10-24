using System.Diagnostics;
using System.Runtime.InteropServices;
using MxU.Slides.Core.Scene;
using MxU.Slides.VideoIO.Common;

namespace MxU.Slides.VideoIO.DeckLink;

/// <summary>The signal a DeckLink input is locked to, e.g. "1080i59.94".</summary>
public sealed record DeckLinkInputFormat(string Name, int Width, int Height, long FrameDuration, long TimeScale, bool Interlaced)
{
    public double FramesPerSecond => FrameDuration == 0 ? 0 : (double)TimeScale / FrameDuration;
}

/// <summary>
/// One captured frame plus the audio that came with it, lent for the duration of the
/// callback: <see cref="Video"/> is 8-bit UYVY straight from the card's buffer (empty when
/// there is no signal), <see cref="Audio"/> interleaved 32-bit integer samples.
/// <see cref="CaptureHostTime"/> is when the card actually captured it, worked out from
/// the card's own clock (it can be a frame or two before <see cref="HostTime"/>, when the
/// callback ran); both are <c>ShowState.HostClock</c> seconds.
/// </summary>
public readonly ref struct DeckLinkInputArrival
{
    public ReadOnlySpan<byte> Video { get; init; }
    public int RowBytes { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public bool Interlaced { get; init; }
    public bool HasSignal { get; init; }
    public double HostTime { get; init; }
    public double CaptureHostTime { get; init; }
    public ReadOnlySpan<int> Audio { get; init; }
    public int AudioFrames { get; init; }
    public int AudioChannels { get; init; }
}

public delegate void DeckLinkFrameHandler(in DeckLinkInputArrival arrival);

/// <summary>
/// Captures from a DeckLink input: 8-bit YUV video and embedded 48 kHz audio, following
/// the signal's format. If the card can detect formats it starts in 1080p29.97 (or the
/// first mode it supports) and, when told the signal is something else, re-enables the
/// streams in the new mode. Frames arrive on a driver thread through the handlers given
/// to <see cref="TryOpen"/>. Port of dlk_input_open / close and DLKInputCallback
/// (decklink_shim.cpp) and DeckLinkKit/DeckLinkInput.swift.
/// </summary>
public sealed class DeckLinkInput : IDisposable
{
    private readonly IDeckLink device;
    private readonly IDeckLinkInput input;
    private readonly InputCallback callback;
    private readonly DeckLinkFrameHandler onFrame;
    private readonly Action<DeckLinkInputFormat> onFormat;
    private readonly int audioChannels;
    private uint currentMode;
    private volatile bool interlaced;
    private volatile bool closing;
    private int callbacksInFlight;

    private DeckLinkInput(IDeckLink device, IDeckLinkInput input, int audioChannels, uint startMode,
        DeckLinkFrameHandler onFrame, Action<DeckLinkInputFormat> onFormat)
    {
        this.device = device;
        this.input = input;
        this.audioChannels = audioChannels;
        currentMode = startMode;
        this.onFrame = onFrame;
        this.onFormat = onFormat;
        callback = new InputCallback(this);
    }

    /// <summary>Opens capture on a card. Null with a reason in <paramref name="error"/> instead of throwing.</summary>
    public static DeckLinkInput? TryOpen(long persistentId, int audioChannels, DeckLinkFrameHandler onFrame,
        Action<DeckLinkInputFormat> onFormat, out string error)
    {
        var (opened, message) = Mta.Run(() => Open(persistentId, audioChannels, onFrame, onFormat));
        error = message;
        return opened;
    }

    /// <summary>Capture channels to ask for: 16 or 8 when the card has them, else stereo.</summary>
    public static int AudioChannels(int forMaximum) => forMaximum >= 16 ? 16 : forMaximum >= 8 ? 8 : 2;

    /// <summary>Only a real, different mode restarts the streams; an event with no mode (0) changes nothing.</summary>
    public static bool FormatNeedsRestart(uint currentMode, uint detectedMode) => detectedMode != 0 && detectedMode != currentMode;

    public void Dispose()
    {
        if (closing) return;
        closing = true;
        var wait = Stopwatch.StartNew();
        while (Volatile.Read(ref callbacksInFlight) > 0 && wait.ElapsedMilliseconds < 1000) Thread.Sleep(1);
        Mta.Run(() =>
        {
            input.StopStreams();
            input.SetCallback(null);
            input.DisableAudioInput();
            input.DisableVideoInput();
            DeckLinkCom.Release(device);
        });
    }

    private static (DeckLinkInput?, string) Open(long persistentId, int audioChannels, DeckLinkFrameHandler onFrame, Action<DeckLinkInputFormat> onFormat)
    {
        var device = DeckLinkRuntime.FindDevice(persistentId);
        if (device is null) return (null, "DeckLink device not found (unplugged, or Desktop Video not installed)");
        var keep = false;
        try
        {
            if (device is not IDeckLinkInput input)
                return (null, "device cannot capture (this connector may be mapped as an output in Desktop Video Setup)");

            // Format detection lets the card follow the source; embedded audio is the audio
            // carried inside the SDI/HDMI signal (rather than the card's analogue inputs).
            var formatDetection = false;
            if (device is IDeckLinkProfileAttributes attributes)
            {
                formatDetection = attributes.GetFlag(Bmd.SupportsInputFormatDetection, out var detect) == 0 && detect != 0;
                if (attributes.GetInt(Bmd.AudioInputConnections, out var connections) == 0
                    && (connections & Bmd.AudioConnectionEmbedded) != 0 && device is IDeckLinkConfiguration configuration)
                    configuration.SetInt(Bmd.ConfigAudioInputConnection, Bmd.AudioConnectionEmbedded);
            }

            var startMode = StartMode(input);
            if (startMode == 0) return (null, "no capture modes on this device");

            if (input.EnableVideoInput(startMode, Bmd.Format8BitYUV,
                    formatDetection ? Bmd.VideoInputEnableFormatDetection : Bmd.VideoInputFlagDefault) != 0)
                return (null, "video input enable failed (input may be in use by another app)");
            if (input.EnableAudioInput(Bmd.AudioSampleRate48kHz, Bmd.AudioSampleType32bitInteger, (uint)audioChannels) != 0)
            {
                input.DisableVideoInput();
                return (null, "audio input enable failed");
            }

            var opened = new DeckLinkInput(device, input, audioChannels, startMode, onFrame, onFormat);
            if (input.GetDisplayMode(startMode, out var mode) == 0 && mode is not null)
            {
                try
                {
                    mode.GetName(out var name);
                    mode.GetFrameRate(out var duration, out var scale);
                    opened.ReportMode(name ?? "", mode.GetWidth(), mode.GetHeight(), duration, scale, mode.GetFieldDominance());
                }
                finally { DeckLinkCom.Release(mode); }
            }
            input.SetCallback(opened.callback);
            if (input.StartStreams() != 0)
            {
                input.SetCallback(null);
                input.DisableAudioInput();
                input.DisableVideoInput();
                return (null, "capture failed to start (input may be in use by another app)");
            }
            keep = true;
            return (opened, "");
        }
        finally
        {
            if (!keep) DeckLinkCom.Release(device);
        }
    }

    /// <summary>1080p29.97 if the input takes it, else the first mode it supports, else 0.</summary>
    private static uint StartMode(IDeckLinkInput input)
    {
        bool Supported(uint mode) =>
            input.DoesSupportVideoMode(Bmd.VideoConnectionUnspecified, mode, Bmd.Format8BitYUV, Bmd.NoVideoConversion,
                Bmd.SupportedVideoModeDefault, out _, out var supported) == 0 && supported != 0;

        if (Supported(Bmd.ModeHD1080p2997)) return Bmd.ModeHD1080p2997;
        if (input.GetDisplayModeIterator(out var modes) != 0 || modes is null) return 0;
        try
        {
            while (modes.Next(out var candidate) == 0 && candidate is not null)
            {
                try
                {
                    var id = candidate.GetDisplayMode();
                    if (Supported(id)) return id;
                }
                finally { DeckLinkCom.Release(candidate); }
            }
            return 0;
        }
        finally { DeckLinkCom.Release(modes); }
    }

    private void ReportMode(string name, int width, int height, long duration, long scale, uint fieldDominance)
    {
        var fields = fieldDominance is Bmd.LowerFieldFirst or Bmd.UpperFieldFirst;
        interlaced = fields;
        try { onFormat(new DeckLinkInputFormat(name, width, height, duration, scale, fields)); }
        catch (Exception ex) { VideoIOLog.Note("input.decklink", $"format handler failed: {ex.Message}"); }
    }

    /// <summary>The signal changed (the source switched to 1080i, say): re-enable the streams in its mode.</summary>
    private void FormatChanged(IntPtr mode)
    {
        if (mode == 0 || closing) return;
        var detected = ComCall.ModeId(mode);
        if (!FormatNeedsRestart(currentMode, detected)) return;
        currentMode = detected;
        input.PauseStreams();
        input.EnableVideoInput(detected, Bmd.Format8BitYUV, Bmd.VideoInputEnableFormatDetection);
        input.FlushStreams();
        input.StartStreams();
        ComCall.ModeFrameRate(mode, out var duration, out var scale);
        ReportMode(ComCall.ModeName(mode), ComCall.ModeWidth(mode), ComCall.ModeHeight(mode), duration, scale, ComCall.ModeFieldDominance(mode));
    }

    private unsafe void FrameArrived(IntPtr video, IntPtr audio)
    {
        if (closing) return;
        var hostTime = ShowState.HostClock();
        var captureHostTime = hostTime;
        int width = 0, height = 0, rowBytes = 0;
        var hasSignal = false;
        IntPtr buffer = 0;
        IntPtr bytes = 0;
        var accessing = false;
        try
        {
            if (video != 0)
            {
                // The card stamps each frame on its own microsecond clock; how far behind
                // "now" on that clock the frame is tells us when it was really captured.
                if (ComCall.HardwareReferenceTimestamp(video, 1_000_000, out var frameTime, out _)
                    && input.GetHardwareReferenceClock(1_000_000, out var hardwareNow, out _, out _) == 0
                    && hardwareNow >= frameTime && hardwareNow - frameTime < 1_000_000)
                    captureHostTime = hostTime - (hardwareNow - frameTime) / 1e6;
                hasSignal = (ComCall.FrameFlags(video) & Bmd.FrameHasNoInputSource) == 0;
                width = ComCall.FrameWidth(video);
                height = ComCall.FrameHeight(video);
                rowBytes = ComCall.FrameRowBytes(video);
                if (hasSignal && ComCall.FramePixelFormat(video) == Bmd.Format8BitYUV)
                {
                    buffer = ComCall.QueryInterface(video, DeckLinkIds.IDeckLinkVideoBuffer);
                    if (buffer != 0 && ComCall.StartAccess(buffer, Bmd.BufferAccessRead))
                    {
                        accessing = true;
                        bytes = ComCall.BufferBytes(buffer);
                    }
                }
            }
            IntPtr samples = 0;
            var audioFrames = 0;
            if (audio != 0 && (samples = ComCall.AudioBytes(audio)) != 0) audioFrames = ComCall.AudioFrameCount(audio);
            var arrival = new DeckLinkInputArrival
            {
                Video = bytes == 0 ? default : new ReadOnlySpan<byte>((void*)bytes, rowBytes * height),
                RowBytes = rowBytes,
                Width = width,
                Height = height,
                Interlaced = interlaced,
                HasSignal = hasSignal,
                HostTime = hostTime,
                CaptureHostTime = captureHostTime,
                Audio = samples == 0 ? default : new ReadOnlySpan<int>((void*)samples, audioFrames * audioChannels),
                AudioFrames = samples == 0 ? 0 : audioFrames,
                AudioChannels = samples == 0 ? 0 : audioChannels,
            };
            onFrame(in arrival);
        }
        catch (Exception ex)
        {
            VideoIOLog.Note("input.decklink", $"frame handler failed: {ex.Message}");
        }
        finally
        {
            if (buffer != 0)
            {
                if (accessing) ComCall.EndAccess(buffer, Bmd.BufferAccessRead);
                ComCall.Release(buffer);
            }
        }
    }

    /// <summary>The object the driver calls back. Exposed to COM as IDeckLinkInputCallback.</summary>
    [ClassInterface(ClassInterfaceType.None)]
    internal sealed class InputCallback(DeckLinkInput? owner) : IDeckLinkInputCallback
    {
        public int VideoInputFormatChanged(uint notificationEvents, IntPtr newDisplayMode, uint detectedSignalFlags)
        {
            if (owner is null) return 0;
            Interlocked.Increment(ref owner.callbacksInFlight);
            try { owner.FormatChanged(newDisplayMode); }
            catch (Exception ex) { VideoIOLog.Note("input.decklink", $"format change failed: {ex.Message}"); }
            finally { Interlocked.Decrement(ref owner.callbacksInFlight); }
            return 0;
        }

        public int VideoInputFrameArrived(IntPtr videoFrame, IntPtr audioPacket)
        {
            if (owner is null) return 0;
            Interlocked.Increment(ref owner.callbacksInFlight);
            try { owner.FrameArrived(videoFrame, audioPacket); }
            finally { Interlocked.Decrement(ref owner.callbacksInFlight); }
            return 0;
        }
    }
}
