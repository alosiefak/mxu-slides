using System.Diagnostics;
using System.Runtime.InteropServices;
using MxU.Slides.Core.Media;
using MxU.Slides.Core.Scene;
using MxU.Slides.VideoIO.Common;

namespace MxU.Slides.VideoIO.DeckLink;

/// <summary>
/// Keying on a DeckLink output. Internal: the card lays the program (by its alpha) over
/// the signal on its own input. External: the card puts the picture (fill) on one
/// connector and its alpha (key) on another, for a vision mixer to key.
/// </summary>
public enum DeckLinkKeying { Off = 0, Internal = 1, External = 2 }

/// <summary>
/// One DeckLink output in scheduled playback. Four card frames circulate: the card
/// shows one, and each time it finishes one (<see cref="IDeckLinkVideoOutputCallback"/>)
/// that frame is refilled from the newest program slot and scheduled one frame-duration
/// after the last. <see cref="Display"/> only copies a program frame into a slot under a
/// lock, so the program feed never waits for the card. <see cref="Timing"/> exposes the
/// slot bookkeeping (repeats, skips, late, dropped, schedule jumps).
/// <para>
/// Pixels go out as BGRA (fill from colour, key from alpha when keying). For the odd mode
/// a card only takes as 8-bit YUV (never with keying), frames are converted with
/// <see cref="DeckLinkFrameConverter.ConvertBgraToUyvy"/>. A program frame of another
/// size is scaled to the mode (nearest pixel).
/// </para>
/// Port of dlk_output_open / display / close and DLKScheduleCallback (decklink_shim.cpp)
/// and DeckLinkKit/DeckLinkOutput.swift.
/// </summary>
public sealed class DeckLinkOutput : IDeckLinkPlayout
{
    private const int FrameCount = 4;
    private const int SlotCount = 3;

    private readonly Lock gate = new();
    private readonly IDeckLink device;
    private readonly IDeckLinkOutput output;
    private readonly IDeckLinkKeyer? keyer;
    private readonly IDeckLinkMutableVideoFrame[] frames;
    private readonly IDeckLinkVideoBuffer[] frameBuffers;
    private readonly nint[] frameIdentity;
    private readonly nint[] frameInterface;
    private readonly byte[][] slots;
    private readonly long frameDuration;
    private readonly long timeScale;
    private readonly OutputCallback callback;
    private readonly DeckLinkOutputTiming timing = new();
    private byte[] scaled = [];
    private long streamTime;
    private volatile bool playing;
    private bool keyerLevelReasserted;
    private long lastCompletionTicks = Stopwatch.GetTimestamp();
    private int callbacksInFlight;
    private bool disposed;

    private DeckLinkOutput(IDeckLink device, IDeckLinkOutput output, IDeckLinkKeyer? keyer, int width, int height, int rowBytes,
        uint pixelFormat, long frameDuration, long timeScale, IDeckLinkMutableVideoFrame[] frames)
    {
        this.device = device;
        this.output = output;
        this.keyer = keyer;
        Width = width;
        Height = height;
        RowBytes = rowBytes;
        PixelFormat = pixelFormat;
        this.frameDuration = frameDuration;
        this.timeScale = timeScale;
        this.frames = frames;
        frameBuffers = [.. frames.Select(frame => (IDeckLinkVideoBuffer)frame)];
        frameIdentity = [.. frames.Select(Marshal.GetIUnknownForObject)];
        frameInterface = [.. frameIdentity.Select(unknown =>
            Marshal.QueryInterface(unknown, in DeckLinkIds.IDeckLinkVideoFrame, out var pointer) == 0 ? pointer : 0)];
        slots = new byte[SlotCount][];
        for (var i = 0; i < SlotCount; i++)
        {
            slots[i] = new byte[rowBytes * height];
            if (pixelFormat == Bmd.Format8BitYUV) FillYuvBlack(slots[i]);
        }
        callback = new OutputCallback(this);
    }

    public int Width { get; }
    public int Height { get; }
    public int RowBytes { get; }

    /// <summary>Bmd '2vuy' or 'BGRA': what the card is being sent.</summary>
    internal uint PixelFormat { get; }

    public bool SendsBgra => PixelFormat == Bmd.Format8BitBGRA;

    /// <summary>A copy of the slot bookkeeping and the card's counters.</summary>
    public DeckLinkOutputTiming Timing
    {
        get { lock (gate) return timing.Copy(); }
    }

    /// <summary>
    /// Opens a card output in <paramref name="modeId"/> and starts it showing black. Returns
    /// null with a reason in <paramref name="error"/> instead of throwing: device missing,
    /// mode not offered, keying not possible, output claimed by another app, …
    /// </summary>
    public static DeckLinkOutput? TryOpen(long persistentId, uint modeId, DeckLinkKeying keying, out string error)
    {
        var (opened, message) = Mta.Run(() => Open(persistentId, modeId, keying));
        error = message;
        return opened;
    }

    /// <summary>
    /// Latches a program frame for the card's next free frame. False when the output isn't
    /// playing or the card has stopped completing frames for over a second (unplugged,
    /// stalled): the controller counts refusals to notice a dead device.
    /// </summary>
    public bool Display(VideoFrame frame)
    {
        lock (gate)
        {
            if (!playing || Stopwatch.GetElapsedTime(lastCompletionTicks) > TimeSpan.FromSeconds(1)) return false;
            var slot = slots[timing.Latch()];
            var source = frame.Pixels.Span;
            var stride = frame.Stride;
            if (frame.Width != Width || frame.Height != Height)
            {
                if (scaled.Length < Width * 4 * Height) scaled = new byte[Width * 4 * Height];
                PixelCopy.ScaleNearest(source, frame.Width, frame.Height, frame.Stride, scaled, Width, Height, Width * 4);
                source = scaled;
                stride = Width * 4;
            }
            if (PixelFormat == Bmd.Format8BitBGRA)
                PixelCopy.CopyRows(source, stride, slot, RowBytes, Math.Min(stride, Width * 4), Height);
            else
                DeckLinkFrameConverter.ConvertBgraToUyvy(source, stride, Width, Height, slot, RowBytes);
            return true;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            playing = false;
        }
        // A completion that got past the "playing" check may still be scheduling; let it finish.
        var wait = Stopwatch.StartNew();
        while (Volatile.Read(ref callbacksInFlight) > 0 && wait.ElapsedMilliseconds < 1000) Thread.Sleep(1);
        Mta.Run(() =>
        {
            output.StopScheduledPlayback(0, out _, timeScale);
            output.SetScheduledFrameCompletionCallback(null);
            output.DisableVideoOutput();
            keyer?.Disable();
            foreach (var pointer in frameInterface.Concat(frameIdentity)) if (pointer != 0) Marshal.Release(pointer);
            foreach (var frame in frames) DeckLinkCom.Release(frame);
            DeckLinkCom.Release(device);
        });
    }

    private static (DeckLinkOutput?, string) Open(long persistentId, uint modeId, DeckLinkKeying keying)
    {
        var device = DeckLinkRuntime.FindDevice(persistentId);
        if (device is null) return (null, "DeckLink device not found (unplugged, or Desktop Video not installed)");
        var keep = false;
        try
        {
            if (device is not IDeckLinkOutput output) return (null, "device does not support playback");
            if (output.GetDisplayModeIterator(out var modes) != 0 || modes is null) return (null, "display modes unavailable");
            int width = 0, height = 0;
            long duration = 0, scale = 0;
            var found = false;
            try
            {
                while (!found && modes.Next(out var candidate) == 0 && candidate is not null)
                {
                    try
                    {
                        if (candidate.GetDisplayMode() != modeId) continue;
                        found = true;
                        width = candidate.GetWidth();
                        height = candidate.GetHeight();
                        candidate.GetFrameRate(out duration, out scale);
                    }
                    finally { DeckLinkCom.Release(candidate); }
                }
            }
            finally { DeckLinkCom.Release(modes); }
            if (!found) return (null, "display mode is not offered by this device");

            uint pixelFormat;
            if (keying != DeckLinkKeying.Off)
            {
                if (!DeckLinkRuntime.Supports(output, modeId, Bmd.Format8BitBGRA, Bmd.SupportedVideoModeKeying))
                    return (null, "this display mode cannot carry a keyed output on this device (check link grouping in Desktop Video Setup)");
                pixelFormat = Bmd.Format8BitBGRA;
            }
            else if (DeckLinkRuntime.Supports(output, modeId, Bmd.Format8BitBGRA, Bmd.SupportedVideoModeDefault))
                pixelFormat = Bmd.Format8BitBGRA;
            else if (DeckLinkRuntime.Supports(output, modeId, Bmd.Format8BitYUV, Bmd.SupportedVideoModeDefault))
                pixelFormat = Bmd.Format8BitYUV;
            else
                return (null, "device cannot output BGRA or YUV at the requested mode");

            if (output.EnableVideoOutput(modeId, Bmd.VideoOutputFlagDefault) != 0)
                return (null, "video output enable failed (output may be claimed by another app)");

            IDeckLinkKeyer? keyer = null;
            if (keying != DeckLinkKeying.Off)
            {
                keyer = device as IDeckLinkKeyer;
                if (keyer is null)
                {
                    output.DisableVideoOutput();
                    return (null, "device does not support keying");
                }
                keyer.SetLevel(255);
                if (keyer.Enable(keying == DeckLinkKeying.External ? 1 : 0) != 0)
                {
                    output.DisableVideoOutput();
                    return (null, keying == DeckLinkKeying.External
                        ? "external key/fill not supported by this device"
                        : "internal keying not supported by this device");
                }
                keyer.SetLevel(255);
                keyer.RampUp(0);
            }

            void Undo()
            {
                output.DisableVideoOutput();
                keyer?.Disable();
            }

            if (duration == 0 || scale == 0)
            {
                Undo();
                return (null, "display mode timing unavailable");
            }

            var bytesPerPixel = pixelFormat == Bmd.Format8BitBGRA ? 4 : 2;
            if (output.RowBytesForPixelFormat(pixelFormat, width, out var rowBytes) != 0 || rowBytes < width * bytesPerPixel)
                rowBytes = width * bytesPerPixel;
            var frames = new List<IDeckLinkMutableVideoFrame>();
            for (var i = 0; i < FrameCount; i++)
            {
                if (output.CreateVideoFrame(width, height, rowBytes, pixelFormat, Bmd.FrameFlagDefault, out var frame) != 0 || frame is null) break;
                if (frame is not IDeckLinkVideoBuffer)
                {
                    DeckLinkCom.Release(frame);
                    break;
                }
                frames.Add(frame);
            }
            if (frames.Count < FrameCount)
            {
                Undo();
                foreach (var frame in frames) DeckLinkCom.Release(frame);
                return (null, "video frame allocation failed");
            }

            var opened = new DeckLinkOutput(device, output, keyer, width, height, rowBytes, pixelFormat, duration, scale, [.. frames]);
            if (!opened.StartPlayback())
            {
                opened.playing = false;
                output.SetScheduledFrameCompletionCallback(null);
                Undo();
                opened.ReleaseFrames();
                return (null, "scheduled playback failed to start");
            }
            keep = true;
            return (opened, "");
        }
        finally
        {
            if (!keep) DeckLinkCom.Release(device);
        }
    }

    /// <summary>Prerolls all four frames (black) one duration apart, then starts the card's clock.</summary>
    private bool StartPlayback()
    {
        output.SetScheduledFrameCompletionCallback(callback);
        playing = true;
        for (var i = 0; i < FrameCount; i++)
        {
            CopyIntoFrame(i, slots[0]);
            output.ScheduleVideoFrame(frameInterface[i], streamTime, frameDuration, timeScale);
            streamTime += frameDuration;
        }
        return output.StartScheduledPlayback(0, timeScale, 1.0) == 0;
    }

    private void ReleaseFrames()
    {
        foreach (var pointer in frameInterface.Concat(frameIdentity)) if (pointer != 0) Marshal.Release(pointer);
        foreach (var frame in frames) DeckLinkCom.Release(frame);
    }

    private void CopyIntoFrame(int index, byte[] slot)
    {
        var buffer = frameBuffers[index];
        if (buffer.StartAccess(Bmd.BufferAccessWrite) != 0) return;
        try
        {
            if (buffer.GetBytes(out var bytes) == 0 && bytes != 0) Marshal.Copy(slot, 0, bytes, slot.Length);
        }
        finally { buffer.EndAccess(Bmd.BufferAccessWrite); }
    }

    private int FrameIndex(IntPtr completed)
    {
        if (completed == 0) return -1;
        var identity = ComCall.QueryInterface(completed, DeckLinkIds.IUnknown);
        if (identity == 0) return -1;
        ComCall.Release(identity);
        return Array.IndexOf(frameIdentity, identity);
    }

    /// <summary>
    /// The card finished showing a frame: tally it, refill that frame from the slot now
    /// due, and schedule it one duration after the last. If the schedule fell behind the
    /// card's clock (the PC stalled), jump it two frames ahead of the hardware so frames
    /// aren't scheduled into the past. Runs on a driver thread.
    /// </summary>
    private void Completed(IntPtr completedFrame, uint result)
    {
        Interlocked.Increment(ref callbacksInFlight);
        try
        {
            if (!playing) return;
            var haveHardwareTime = output.GetScheduledStreamTime(timeScale, out var hardwareTime, out _) == 0;
            var reassertKeyerLevel = false;
            lock (gate)
            {
                // Some cards drop the keyer level on the first completion; set it once more then.
                if (!keyerLevelReasserted)
                {
                    keyerLevelReasserted = true;
                    reassertKeyerLevel = keyer is not null;
                }
            }
            if (reassertKeyerLevel)
            {
                keyer!.SetLevel(255);
                keyer.RampUp(0);
            }
            var index = FrameIndex(completedFrame);
            long scheduleAt;
            lock (gate)
            {
                if (!playing) return;
                lastCompletionTicks = Stopwatch.GetTimestamp();
                var jumped = haveHardwareTime && streamTime < hardwareTime + frameDuration;
                if (jumped) streamTime = hardwareTime + 2 * frameDuration;
                var tallied = result switch
                {
                    Bmd.OutputFrameDisplayedLate => DeckLinkCompletion.Late,
                    Bmd.OutputFrameDropped => DeckLinkCompletion.Dropped,
                    Bmd.OutputFrameFlushed => DeckLinkCompletion.Flushed,
                    _ => DeckLinkCompletion.OnTime,
                };
                var slot = timing.TallyCompletion(ShowState.HostClock(), tallied, jumped);
                if (index >= 0) CopyIntoFrame(index, slots[slot]);
                scheduleAt = streamTime;
                streamTime += frameDuration;
            }
            if (index < 0) return;
            if (output.ScheduleVideoFrame(frameInterface[index], scheduleAt, frameDuration, timeScale) != 0)
            {
                lock (gate)
                {
                    streamTime += frameDuration;
                    output.ScheduleVideoFrame(frameInterface[index], streamTime, frameDuration, timeScale);
                    streamTime += frameDuration;
                }
            }
        }
        catch (Exception ex)
        {
            VideoIOLog.Note("decklink.frames", $"completion failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Decrement(ref callbacksInFlight);
        }
    }

    /// <summary>UYVY black: U=128, Y=16, V=128, Y=16. (All zero bytes would be an illegal green.)</summary>
    private static void FillYuvBlack(byte[] slot)
    {
        for (var i = 0; i + 3 < slot.Length; i += 4)
        {
            slot[i] = 128;
            slot[i + 1] = 16;
            slot[i + 2] = 128;
            slot[i + 3] = 16;
        }
    }

    /// <summary>The object the driver calls back. Exposed to COM as IDeckLinkVideoOutputCallback.</summary>
    [ClassInterface(ClassInterfaceType.None)]
    internal sealed class OutputCallback(DeckLinkOutput? owner) : IDeckLinkVideoOutputCallback
    {
        public int ScheduledFrameCompleted(IntPtr completedFrame, uint result)
        {
            owner?.Completed(completedFrame, result);
            return 0;
        }

        public int ScheduledPlaybackHasStopped() => 0;
    }
}
