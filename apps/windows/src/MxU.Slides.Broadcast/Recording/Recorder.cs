using System.Runtime.InteropServices;
using System.Threading.Channels;
using MxU.Slides.Broadcast.Media;
using MxU.Slides.Broadcast.Streaming;
using MxU.Slides.Core.Media;

namespace MxU.Slides.Broadcast.Recording;

public enum RecorderStateKind { Recording, Finishing, Finished, Failed }

/// <summary>Where a recording is: recording, finishing, finished (with why), or failed (with the error).</summary>
public readonly record struct RecorderState(RecorderStateKind Kind, RecordingStopReason Reason = RecordingStopReason.Requested, string? Message = null)
{
    public static RecorderState Recording => new(RecorderStateKind.Recording);

    public static RecorderState Finishing => new(RecorderStateKind.Finishing);

    public static RecorderState Finished(RecordingStopReason reason) => new(RecorderStateKind.Finished, reason);

    public static RecorderState Failed(string message) => new(RecorderStateKind.Failed, RecordingStopReason.WriterFailed, message);

    public override string ToString() => Kind switch
    {
        RecorderStateKind.Finished => $"finished({Reason})",
        RecorderStateKind.Failed => $"failed({Message})",
        _ => Kind.ToString().ToLowerInvariant(),
    };
}

/// <summary>A snapshot for status lines. <see cref="FreeDiskBytes"/> is <see cref="long.MaxValue"/> until first measured.</summary>
public readonly record struct RecorderStatus(
    RecorderState State,
    double ElapsedSeconds,
    int AppendedFrames,
    int DroppedFrames,
    int DroppedAudioBuffers,
    long FreeDiskBytes,
    string Path);

/// <summary>
/// Records the program to an MP4 file. Frames and audio arrive on the app's threads (it is an
/// <see cref="IProgramSink"/>); they are stamped there, copied into a short queue, and written
/// by the recorder's own thread, so the app never waits on the disk. A full queue drops the
/// frame and counts it. Timestamps start at zero with the first picture; a picture stamped at
/// the same instant as the last is nudged forward rather than dropped; audio before the first
/// picture is dropped and counted (all as on the Mac).
///
/// Safety: the file is written as <c>name.partial.mp4</c> beside the final name and renamed
/// when finished, so a finished name is always a finished file. The partial file is a
/// fragmented MP4, playable up to its last fragment if the app is killed;
/// <see cref="RecoverInterrupted"/> gives such files their proper names on the next run. Free
/// disk space is checked every second; below the configured floor the recording finishes
/// itself with <see cref="RecordingStopReason.DiskFull"/>. Port of Recorder.swift.
/// </summary>
public sealed class Recorder : IProgramSink, IDisposable
{
    public const string PartialSuffix = ".partial.mp4";

    private const long Timescale = 10_000_000;

    private abstract record Work;

    private sealed record VideoWork(PendingVideoFrame Frame, long Time) : Work;

    private sealed record AudioWork(PendingAudioBuffer Buffer, long Time) : Work;

    private const int MaxQueuedFrames = 8;
    private const int MaxQueuedAudio = 256;

    private readonly Lock gate = new();
    private readonly Channel<Work> queue = Channel.CreateUnbounded<Work>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Thread worker;
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Timer diskTimer;
    private RecorderState state = RecorderState.Recording;
    private RecordingStopReason finishReason = RecordingStopReason.Requested;
    private double? sessionOrigin;
    private double lastVideoSeconds;
    private int appendedFrames;
    private int acceptedFrames;
    private int droppedFrames;
    private int droppedAudioBuffers;
    private int queuedFrames;
    private int queuedAudio;
    private long freeDiskBytes = long.MaxValue;
    private string path;

    /// <summary>Starts recording to <paramref name="path"/> (an .mp4). Throws <see cref="IOException"/> if the file can't be started.</summary>
    public Recorder(string path, RecordingConfiguration configuration)
    {
        Configuration = configuration;
        this.path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(this.path)!);
        TemporaryPath = PartialPathFor(this.path);
        worker = new Thread(Run) { IsBackground = true, Name = "Recorder", Priority = ThreadPriority.AboveNormal };
        worker.Start();
        try
        {
            started.Task.GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            throw new IOException($"Couldn't start recording to {Path.GetFileName(this.path)}: {e.Message}", e);
        }
        CheckDiskHeadroom(autoStop: false);
        diskTimer = new Timer(_ => CheckDiskHeadroom(autoStop: true), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public RecordingConfiguration Configuration { get; }

    /// <summary>Where the finished file is (or will be).</summary>
    public string FilePath
    {
        get { lock (gate) return path; }
    }

    /// <summary>The file being written until the recording finishes.</summary>
    public string TemporaryPath { get; }

    /// <summary>The codec actually recorded (H.264 unless HEVC was asked for and this PC has an HEVC encoder).</summary>
    public RecordingCodec RecordedCodec { get; private set; }

    /// <summary>False when Windows could only write a plain (not crash-safe) MP4.</summary>
    public bool IsCrashSafe => pipeline?.Fragmented ?? true;

    private volatile IRecordingPipeline? pipeline;

    /// <summary>Raised (on a recorder thread) when <see cref="Status"/>'s state changes.</summary>
    public event Action<Recorder, RecorderState>? StateChanged;

    public RecorderStatus Status
    {
        get
        {
            lock (gate)
                return new RecorderStatus(state, lastVideoSeconds, appendedFrames, droppedFrames, droppedAudioBuffers, freeDiskBytes, path);
        }
    }

    public static string PartialPathFor(string finalPath) =>
        Path.Combine(Path.GetDirectoryName(finalPath)!, Path.GetFileNameWithoutExtension(finalPath) + PartialSuffix);

    void IProgramSink.WriteVideo(VideoFrame frame) => Append(frame);

    void IProgramSink.WriteAudio(AudioBuffer buffer) => AppendAudio(buffer);

    /// <summary>Queues one picture. False when it wasn't taken (not recording, or the writer is behind).</summary>
    public bool Append(VideoFrame frame)
    {
        long time;
        lock (gate)
        {
            if (state.Kind != RecorderStateKind.Recording) return false;
            var origin = sessionOrigin ??= frame.HostTime;
            var relative = Math.Max(0, frame.HostTime - origin);
            // A picture at the same instant as the last is nudged one tick later, never dropped.
            var floor = lastVideoSeconds + 1.0 / Timescale;
            var stamped = acceptedFrames == 0 ? relative : Math.Max(relative, floor);
            if (queuedFrames >= MaxQueuedFrames)
            {
                droppedFrames++;
                return false;
            }
            lastVideoSeconds = stamped;
            acceptedFrames++;
            queuedFrames++;
            time = (long)Math.Round(stamped * Timescale);
        }
        PendingVideoFrame copy;
        try
        {
            copy = PendingVideoFrame.Copy(frame);
        }
        catch (ArgumentException)
        {
            lock (gate)
            {
                queuedFrames--;
                droppedFrames++;
            }
            return false;
        }
        if (!queue.Writer.TryWrite(new VideoWork(copy, time)))
        {
            copy.Return();
            lock (gate)
            {
                queuedFrames--;
                droppedFrames++;
            }
            return false;
        }
        return true;
    }

    /// <summary>Queues program audio, timed from the first picture. Audio before it is dropped (and counted).</summary>
    public void AppendAudio(AudioBuffer buffer)
    {
        if (!Configuration.IncludesAudio || buffer.FrameCount == 0) return;
        long time;
        lock (gate)
        {
            if (state.Kind != RecorderStateKind.Recording || sessionOrigin is not { } origin || buffer.HostTime < origin || queuedAudio >= MaxQueuedAudio)
            {
                droppedAudioBuffers++;
                return;
            }
            queuedAudio++;
            time = (long)Math.Round((buffer.HostTime - origin) * Timescale);
        }
        var copy = PendingAudioBuffer.Copy(buffer);
        if (!queue.Writer.TryWrite(new AudioWork(copy, time)))
        {
            copy.Return();
            lock (gate)
            {
                queuedAudio--;
                droppedAudioBuffers++;
            }
        }
    }

    /// <summary>
    /// Stops taking frames, writes what is queued, finishes and renames the file. Completes when
    /// the file is final. Calling it again (or after a failure) just waits for the same end.
    /// </summary>
    public Task FinishAsync(RecordingStopReason reason = RecordingStopReason.Requested)
    {
        bool begin;
        lock (gate)
        {
            begin = state.Kind == RecorderStateKind.Recording;
            if (begin)
            {
                state = RecorderState.Finishing;
                finishReason = reason;
            }
        }
        if (begin)
        {
            diskTimer.Dispose();
            Raise(RecorderState.Finishing);
            queue.Writer.TryComplete();
        }
        return finished.Task;
    }

    public void Dispose() => FinishAsync().GetAwaiter().GetResult();

    private void Raise(RecorderState next)
    {
        try { StateChanged?.Invoke(this, next); }
        catch (Exception e) { BroadcastLog.Error("recording", $"StateChanged handler threw: {e.Message}"); }
    }

    private void SetFailed(string message)
    {
        bool changed;
        lock (gate)
        {
            changed = state.Kind is RecorderStateKind.Recording or RecorderStateKind.Finishing;
            if (changed) state = RecorderState.Failed(message);
        }
        if (!changed) return;
        diskTimer?.Dispose();
        queue.Writer.TryComplete();
        BroadcastLog.Error("recording", $"{Path.GetFileName(FilePath)}: {message}");
        Raise(RecorderState.Failed(message));
    }

    /// <summary>The recorder thread: owns the encoders and the MP4 writer from start to finish.</summary>
    private void Run()
    {
        IRecordingPipeline pipeline;
        Mp4Settings settings;
        try
        {
            var hevc = Configuration.Codec is RecordingCodec.Hevc or RecordingCodec.Hevc10 && HevcAvailable.Value;
            RecordedCodec = Configuration.EffectiveCodec(hevc);
            settings = new Mp4Settings(
                Configuration.EncodedWidth, Configuration.EncodedHeight, Math.Max(1, Configuration.FrameRate),
                Configuration.EffectiveBitrate, Configuration.GopFrames,
                Configuration.IncludesAudio ? Configuration.AudioSampleRate : null, Math.Clamp(Configuration.AudioChannels, 1, 2),
                Configuration.AudioBitrateKbps);
            if (File.Exists(TemporaryPath)) File.Delete(TemporaryPath);
            pipeline = RecordedCodec == RecordingCodec.Hevc
                ? new EncodingRecordingPipeline(TemporaryPath, settings, allowHardware: true)
                : new EncodedRecordingPipeline(TemporaryPath, settings, Math.Max(0.5, Configuration.FragmentInterval), Configuration.Encoder);
            this.pipeline = pipeline;
            BroadcastLog.Info("recording", $"{Path.GetFileName(FilePath)}: {RecordedCodec.Raw()} {settings.Width}x{settings.Height}@{settings.FrameRate} {settings.Bitrate / 1000} kbps via {pipeline.Description}");
            started.TrySetResult();
        }
        catch (Exception e)
        {
            lock (gate) state = RecorderState.Failed(e.Message);
            started.TrySetException(e);
            finished.TrySetResult();
            return;
        }

        var converter = new Nv12Converter(settings.Width, settings.Height);
        var nv12 = new byte[converter.FrameSize];
        var frameDuration = Timescale / Math.Max(1, Configuration.FrameRate);
        (int, int)? audioFormat = null;
        var failed = false;
        try
        {
            var reader = queue.Reader;
            while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (reader.TryRead(out var work))
                {
                    switch (work)
                    {
                        case VideoWork video:
                            try
                            {
                                converter.Convert(video.Frame.Span, video.Frame.Width, video.Frame.Height, video.Frame.Stride, nv12);
                                var droppedBefore = pipeline.DroppedPictures;
                                pipeline.WriteVideo(nv12, video.Time, frameDuration);
                                var dropped = pipeline.DroppedPictures - droppedBefore;
                                lock (gate)
                                {
                                    // A busy hardware encoder may leave out an older picture instead of this one.
                                    appendedFrames += 1 - dropped;
                                    droppedFrames += dropped;
                                }
                            }
                            catch (ArgumentException)
                            {
                                lock (gate) droppedFrames++;
                            }
                            finally
                            {
                                video.Frame.Return();
                                lock (gate) queuedFrames--;
                            }
                            break;
                        case AudioWork sound:
                            try
                            {
                                // The first buffer pins the format; a different one is dropped (as the Mac's writer would refuse it).
                                audioFormat ??= (sound.Buffer.SampleRate, sound.Buffer.Channels);
                                if (audioFormat != (sound.Buffer.SampleRate, sound.Buffer.Channels))
                                    lock (gate) droppedAudioBuffers++;
                                else
                                    pipeline.WriteAudio(sound.Buffer, sound.Time);
                            }
                            finally
                            {
                                sound.Buffer.Return();
                                lock (gate) queuedAudio--;
                            }
                            break;
                    }
                }
            }
        }
        catch (Exception e) when (e is MediaFoundationException or COMException)
        {
            failed = true;
            SetFailed(e.Message);
            while (queue.Reader.TryRead(out var left))
            {
                if (left is VideoWork v) v.Frame.Return();
                else if (left is AudioWork a) a.Buffer.Return();
            }
        }

        // Finish the file (even after a failure, so what was written stays playable) and give it its name.
        string? finishError = null;
        try
        {
            pipeline.Finish();
        }
        catch (Exception e) when (e is MediaFoundationException or COMException)
        {
            finishError = e.Message;
        }
        finally
        {
            pipeline.Dispose();
        }
        // A file with anything in it gets its name (even after a failure it plays up to its last
        // fragment); an empty one (nothing was ever recorded) is removed.
        string? finalPath = null;
        if (File.Exists(TemporaryPath) && new FileInfo(TemporaryPath).Length > 0) finalPath = Promote(TemporaryPath, FilePath);
        else TryDelete(TemporaryPath);
        RecorderState end;
        lock (gate)
        {
            path = finalPath ?? path;
            if (state.Kind == RecorderStateKind.Failed) end = state;
            else if (finishError is not null || finalPath is null) end = state = RecorderState.Failed(finishError ?? "couldn't rename the finished recording");
            else end = state = RecorderState.Finished(finishReason);
        }
        if (end.Kind == RecorderStateKind.Finished) Raise(end);
        else if (!failed)
        {
            BroadcastLog.Error("recording", $"{Path.GetFileName(FilePath)}: {end.Message}");
            Raise(end);
        }
        finished.TrySetResult();
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static readonly Lazy<bool> HevcAvailable = new(() =>
    {
        try
        {
            var candidates = MediaFoundation.FindEncoders(
                Media.Interop.MF.MFT_CATEGORY_VIDEO_ENCODER, Media.Interop.MF.MFMediaType_Video,
                Media.Interop.MF.MFVideoFormat_NV12, Media.Interop.MF.MFVideoFormat_HEVC, includeHardware: true);
            MediaFoundation.ReleaseAll(candidates);
            return candidates.Count > 0;
        }
        catch (Exception)
        {
            return false;
        }
    });

    /// <summary>Renames the partial file to its final name (adding " 2", " 3"… if taken). Null if it can't.</summary>
    private static string? Promote(string partial, string final)
    {
        if (!File.Exists(partial)) return null;
        var target = Available(final);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                File.Move(partial, target, overwrite: false);
                return target;
            }
            catch (IOException) when (File.Exists(target))
            {
                target = Available(final);
            }
            catch (IOException)
            {
                // The writer may still be letting go of the file for a moment.
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
        return null;
    }

    private static string Available(string path)
    {
        if (!File.Exists(path)) return path;
        var folder = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(folder, $"{name} {n}{extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    /// <summary>
    /// Gives recordings that a crash left as <c>*.partial.mp4</c> their final names (they play up
    /// to their last fragment). Call at start-up, before recording into <paramref name="folder"/>.
    /// Returns the recovered files' paths.
    /// </summary>
    public static IReadOnlyList<string> RecoverInterrupted(string folder)
    {
        var recovered = new List<string>();
        if (!Directory.Exists(folder)) return recovered;
        foreach (var partial in Directory.EnumerateFiles(folder, "*" + PartialSuffix))
        {
            var info = new FileInfo(partial);
            if (info.Length == 0)
            {
                try { info.Delete(); } catch (IOException) { }
                continue;
            }
            var final = partial[..^PartialSuffix.Length] + ".mp4";
            try
            {
                var target = Available(final);
                File.Move(partial, target);
                recovered.Add(target);
                BroadcastLog.Warning("recording", $"recovered an interrupted recording: {Path.GetFileName(target)}");
            }
            catch (IOException)
            {
                // Still open (another copy of the app is recording into it): leave it.
            }
        }
        return recovered;
    }

    private void CheckDiskHeadroom(bool autoStop)
    {
        long free;
        try
        {
            if (!GetDiskFreeSpaceEx(Path.GetDirectoryName(FilePath)!, out var available, out _, out _)) return;
            free = (long)Math.Min(available, long.MaxValue);
        }
        catch (Exception)
        {
            return;
        }
        lock (gate) freeDiskBytes = free;
        if (autoStop && free < Configuration.MinimumFreeDiskBytes)
        {
            BroadcastLog.Warning("recording", $"{Path.GetFileName(FilePath)}: disk nearly full ({free / 1_000_000} MB free) — stopping");
            _ = FinishAsync(RecordingStopReason.DiskFull);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directory, out ulong freeBytesAvailable, out ulong totalBytes, out ulong totalFreeBytes);
}
