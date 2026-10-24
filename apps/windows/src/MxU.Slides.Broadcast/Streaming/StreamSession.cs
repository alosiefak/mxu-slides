using System.Threading.Channels;
using MxU.Slides.Broadcast.Hls;
using MxU.Slides.Broadcast.Media;
using MxU.Slides.Broadcast.Rtmp;
using MxU.Slides.Core.Media;

namespace MxU.Slides.Broadcast.Streaming;

/// <summary>Knobs for a <see cref="StreamSession"/> that most callers leave alone.</summary>
public sealed class StreamSessionOptions
{
    public EncoderPreference Encoder { get; init; } = EncoderPreference.Automatic;

    /// <summary>Audio bitrate; the Windows AAC encoder rounds to 96/128/160/192 kbps.</summary>
    public int AudioBitrateKbps { get; init; } = 128;

    /// <summary>Tests: how the session waits between reconnect attempts (normally 1, 2, 4, 8, then 15 s).</summary>
    internal Func<TimeSpan, CancellationToken, Task> ReconnectDelay { get; init; } = Task.Delay;

    internal RtmpPublishOptions? Rtmp { get; init; }

    /// <summary>Tests: an HTTP handler standing in for the HLS ingest.</summary>
    internal HttpMessageHandler? HlsHandler { get; init; }

    /// <summary>Tests: how long Stop gives an HLS upload to finish (3 s, as on the Mac).</summary>
    internal TimeSpan HlsStopGrace { get; init; } = TimeSpan.FromSeconds(3);
}

/// <summary>
/// One live stream to one destination. Give it program frames and audio (it is an
/// <see cref="IProgramSink"/>) and it encodes and publishes them, reconnecting by itself when
/// the connection drops: attempts back off 1, 2, 4, 8 and then every 15 seconds, and a drop
/// after it was live starts the count again at 1. Failures that retrying can't fix (a
/// malformed URL, a rejected HLS credential, no encoder) end in <see cref="StreamStateKind.Failed"/>.
///
/// Threading: <see cref="WriteVideo"/> and <see cref="WriteAudio"/> are called from the app's
/// frame and audio threads; they copy the data into small queues (2 frames, 16 audio buffers)
/// and return at once. When the encoder falls behind, the oldest queued frame is dropped and
/// counted in <see cref="StreamSessionStatus.FramesDropped"/>, as on the Mac. Frames given
/// before the session is publishing are ignored and not counted. Everything else (encoding,
/// sending) runs on the session's own tasks. <see cref="Status"/> may be read from any thread;
/// <see cref="StateChanged"/> is raised on an engine thread. Port of StreamSession.swift.
/// </summary>
public sealed class StreamSession : IProgramSink, IDisposable
{
    private readonly Lock gate = new();
    private readonly StreamSessionOptions options;
    private readonly HlsContinuity hlsContinuity = new();
    private StreamSessionState state = StreamSessionState.Idle;
    private int framesSubmitted;
    private int framesDropped;
    private (int SampleRate, int Channels)? audioFormat;
    private bool audioFormatMismatchLogged;
    private Channel<PendingVideoFrame>? frames;
    private Channel<PendingAudioBuffer>? audio;
    private CancellationTokenSource? supervisorCancellation;
    private Task? supervisor;

    public StreamSession(StreamEndpoint destination, StreamVideoConfiguration video, StreamSessionOptions? options = null)
    {
        Destination = destination;
        Video = video;
        this.options = options ?? new StreamSessionOptions();
    }

    public StreamEndpoint Destination { get; }

    public StreamVideoConfiguration Video { get; }

    /// <summary>The encoder in use (e.g. "Intel® Quick Sync Video H.264 Encoder MFT"), once connected.</summary>
    public string? EncoderName { get; private set; }

    /// <summary>Raised (on an engine thread) whenever <see cref="Status"/>'s state changes.</summary>
    public event Action<StreamSession, StreamSessionState>? StateChanged;

    public StreamSessionStatus Status
    {
        get
        {
            lock (gate) return new StreamSessionStatus(state, framesSubmitted, framesDropped);
        }
    }

    /// <summary>The supervisor task, for tests that wait for a session to wind down.</summary>
    internal Task? Supervisor => supervisor;

    private void SetState(StreamSessionState next)
    {
        bool changed;
        lock (gate)
        {
            // Once stopped, only a new Start may change the state.
            if (state.Kind == StreamStateKind.Stopped && next.Kind != StreamStateKind.Stopped) return;
            changed = state != next;
            state = next;
        }
        if (changed) Raise(next);
    }

    private void Raise(StreamSessionState next)
    {
        try { StateChanged?.Invoke(this, next); }
        catch (Exception e) { BroadcastLog.Error("stream", $"StateChanged handler threw: {e.Message}"); }
    }

    /// <summary>Starts connecting (from idle or stopped; does nothing otherwise).</summary>
    public void Start()
    {
        CancellationTokenSource cancellation;
        lock (gate)
        {
            if (state.Kind is not (StreamStateKind.Idle or StreamStateKind.Stopped)) return;
            state = StreamSessionState.Connecting;
            cancellation = new CancellationTokenSource();
            supervisorCancellation = cancellation;
        }
        Raise(StreamSessionState.Connecting);
        supervisor = Task.Run(() => SuperviseAsync(cancellation.Token));
    }

    /// <summary>
    /// Ends the stream. Queued frames are let go; RTMP sends its unpublish and closes; HLS gets
    /// three more seconds to upload the last segment (as the Mac does). Safe to call repeatedly.
    /// </summary>
    public void Stop()
    {
        Channel<PendingVideoFrame>? takenFrames;
        Channel<PendingAudioBuffer>? takenAudio;
        CancellationTokenSource? cancellation;
        bool changed;
        lock (gate)
        {
            changed = state.Kind != StreamStateKind.Stopped;
            state = StreamSessionState.Stopped;
            takenFrames = frames;
            takenAudio = audio;
            frames = null;
            audio = null;
            cancellation = supervisorCancellation;
            supervisorCancellation = null;
        }
        if (changed) Raise(StreamSessionState.Stopped);
        takenFrames?.Writer.TryComplete();
        takenAudio?.Writer.TryComplete();
        if (cancellation is null) return;
        if (Destination.Kind == StreamEndpointKind.Hls) cancellation.CancelAfter(options.HlsStopGrace);
        else cancellation.Cancel();
    }

    public void Dispose() => Stop();

    /// <summary>Queues a program frame (copied; returns at once). Ignored unless publishing.</summary>
    public void WriteVideo(VideoFrame frame)
    {
        Channel<PendingVideoFrame>? pipe;
        lock (gate) pipe = state.Kind == StreamStateKind.Publishing ? frames : null;
        if (pipe is null || frame.Width <= 0 || frame.Height <= 0) return;
        PendingVideoFrame copy;
        try
        {
            copy = PendingVideoFrame.Copy(frame);
        }
        catch (ArgumentException)
        {
            Interlocked.Increment(ref framesDropped);
            return;
        }
        var droppedBefore = Volatile.Read(ref framesDropped);
        if (!pipe.Writer.TryWrite(copy))
        {
            copy.Return();
            return;
        }
        // A full queue drops its oldest frame (counted in the queue's callback) instead.
        if (Volatile.Read(ref framesDropped) == droppedBefore) Interlocked.Increment(ref framesSubmitted);
    }

    /// <summary>
    /// Queues program audio (copied). The first buffer after connecting pins the format; a
    /// buffer with a different rate or channel count is dropped (and logged once) until the
    /// next reconnect, as on the Mac.
    /// </summary>
    public void WriteAudio(AudioBuffer buffer)
    {
        if (buffer.FrameCount == 0) return;
        Channel<PendingAudioBuffer>? pipe = null;
        (int, int)? mismatch = null;
        lock (gate)
        {
            if (state.Kind != StreamStateKind.Publishing) return;
            if (audioFormat is { } pinned)
            {
                if (pinned != (buffer.SampleRate, buffer.Channels))
                {
                    if (!audioFormatMismatchLogged)
                    {
                        audioFormatMismatchLogged = true;
                        mismatch = pinned;
                    }
                }
                else pipe = audio;
            }
            else
            {
                audioFormat = (buffer.SampleRate, buffer.Channels);
                pipe = audio;
            }
        }
        if (mismatch is { } was)
            BroadcastLog.Warning("stream", $"audio format changed mid-stream: {Destination.Name} pinned={was.Item1} Hz×{was.Item2} got={buffer.SampleRate} Hz×{buffer.Channels} — dropping until reconnect");
        if (pipe is null) return;
        var copy = PendingAudioBuffer.Copy(buffer);
        if (!pipe.Writer.TryWrite(copy)) copy.Return();
    }

    private async Task SuperviseAsync(CancellationToken cancellation)
    {
        var attempt = 0;
        while (!cancellation.IsCancellationRequested)
        {
            var frameQueue = PendingQueues.Newest<PendingVideoFrame>(2, dropped =>
            {
                dropped.Return();
                Interlocked.Increment(ref framesDropped);
            });
            var audioQueue = PendingQueues.Newest<PendingAudioBuffer>(16, dropped => dropped.Return());
            lock (gate)
            {
                if (state.Kind == StreamStateKind.Stopped) return;
                frames = frameQueue;
                audio = audioQueue;
                audioFormat = null;
                audioFormatMismatchLogged = false;
            }
            try
            {
                await RunOnceAsync(frameQueue, audioQueue, cancellation);
                return;
            }
            catch (StreamSessionException error) when (error.IsFatal || error.Kind == StreamFailureKind.BadUrl)
            {
                BroadcastLog.Error("stream", $"transport failed permanently: {Destination.Name} — {error.Message}");
                SetState(StreamSessionState.Failed(error.FailureDescription));
                return;
            }
            catch (Exception) when (cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                bool wasPublishing;
                lock (gate)
                {
                    if (state.Kind == StreamStateKind.Stopped) return;
                    wasPublishing = state.Kind == StreamStateKind.Publishing;
                }
                attempt = wasPublishing ? 1 : attempt + 1;
                BroadcastLog.Warning("stream", $"transport lost: {Destination.Name} attempt {attempt} — {error.Message}");
                SetState(StreamSessionState.Reconnecting(attempt));
                var delay = Math.Min(15.0, Math.Pow(2.0, attempt - 1));
                try
                {
                    await options.ReconnectDelay(TimeSpan.FromSeconds(delay), cancellation);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
            finally
            {
                lock (gate)
                {
                    if (frames == frameQueue) frames = null;
                    if (audio == audioQueue) audio = null;
                }
                PendingQueues.Drain(frameQueue, audioQueue);
            }
        }
    }

    private Task RunOnceAsync(Channel<PendingVideoFrame> frameQueue, Channel<PendingAudioBuffer> audioQueue, CancellationToken cancellation) =>
        Destination.Kind switch
        {
            StreamEndpointKind.Rtmp or StreamEndpointKind.Rtmps => RunRtmpAsync(frameQueue, audioQueue, cancellation),
            StreamEndpointKind.Hls => RunHlsAsync(frameQueue, audioQueue, cancellation),
            _ => throw StreamSessionException.Fatal("SRT isn't available on Windows yet"),
        };

    private bool IsPublishing
    {
        get { lock (gate) return state.Kind == StreamStateKind.Publishing; }
    }

    internal H264Settings EncoderSettings(double keyframeInterval = 2) => new(
        Video.EncodedWidth, Video.EncodedHeight, Video.SafeFrameRate, Math.Max(100, Video.BitrateKbps), keyframeInterval);

    /// <summary>Makes the video encoder; not having one is permanent, so it is fatal.</summary>
    internal H264Encoder CreateVideoEncoder(double keyframeInterval)
    {
        if (Video.Codec == StreamCodec.Hevc)
            BroadcastLog.Info("stream", $"{Destination.Name}: HEVC isn't available for streams on Windows; sending H.264");
        try
        {
            var encoder = H264Encoder.Create(EncoderSettings(keyframeInterval), options.Encoder);
            EncoderName = encoder.Name;
            return encoder;
        }
        catch (MediaFoundationException e)
        {
            throw StreamSessionException.Fatal($"no H.264 encoder: {e.Message}", e);
        }
    }

    private async Task RunRtmpAsync(Channel<PendingVideoFrame> frameQueue, Channel<PendingAudioBuffer> audioQueue, CancellationToken cancellation)
    {
        var url = RtmpUrl.Parse(Destination.Url);
        using var encoder = CreateVideoEncoder(keyframeInterval: 2);
        var publisher = await RtmpPublisher.ConnectAsync(url, Destination.StreamKey, options.Rtmp, cancellation);
        using var pumps = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task? videoPump = null, audioPump = null;
        try
        {
            publisher.SendMetadata(FlvTags.OnMetaData(
                Video.EncodedWidth, Video.EncodedHeight, Video.SafeFrameRate, Video.BitrateKbps, options.AudioBitrateKbps, 48_000, 2));
            SetState(StreamSessionState.Publishing);
            // Host and app only: a query string on the URL may carry a token.
            BroadcastLog.Info("stream", $"publishing: {Destination.Name} -> {url.Host}/{url.App.Split('?')[0]} via {encoder.Name}");

            var timeline = new MediaTimeline();
            videoPump = Task.Run(() => PumpRtmpVideoAsync(frameQueue.Reader, encoder, publisher, timeline, pumps.Token), CancellationToken.None);
            audioPump = Task.Run(() => PumpRtmpAudioAsync(audioQueue.Reader, publisher, timeline, pumps.Token), CancellationToken.None);

            var first = await Task.WhenAny(videoPump, audioPump, publisher.Completion);
            if (publisher.Completion.IsFaulted) await publisher.Completion;
            if (first.IsFaulted) await first;
            if (!cancellation.IsCancellationRequested && !IsStopped)
                throw StreamSessionException.TransportClosed("stream ended unexpectedly");
        }
        finally
        {
            pumps.Cancel();
            foreach (var pump in new[] { videoPump, audioPump })
            {
                if (pump is null) continue;
                try { await pump; } catch { }
            }
            await publisher.CloseAsync();
        }
    }

    private bool IsStopped
    {
        get { lock (gate) return state.Kind == StreamStateKind.Stopped; }
    }

    private async Task PumpRtmpVideoAsync(
        ChannelReader<PendingVideoFrame> reader, H264Encoder encoder, RtmpPublisher publisher, MediaTimeline timeline, CancellationToken cancellation)
    {
        var converter = new Nv12Converter(encoder.Settings.Width, encoder.Settings.Height);
        var nv12 = new byte[converter.FrameSize];
        var encoderDrops = 0;
        await foreach (var frame in reader.ReadAllAsync(cancellation))
        {
            try
            {
                converter.Convert(frame.Span, frame.Width, frame.Height, frame.Stride, nv12);
            }
            catch (ArgumentException)
            {
                Interlocked.Increment(ref framesDropped);
                continue;
            }
            finally
            {
                frame.Return();
            }
            timeline.Begin(frame.HostTime);
            List<EncodedVideoFrame> encoded;
            try
            {
                encoded = encoder.Encode(nv12, frame.HostTime);
            }
            catch (MediaFoundationException e)
            {
                throw StreamSessionException.TransportClosed($"video encoder failed: {e.Message}", e);
            }
            foreach (var picture in encoded)
                if (!publisher.SendVideo(picture, timeline.Milliseconds(picture.PresentationTime)))
                    Interlocked.Increment(ref framesDropped);
            if (encoder.DroppedInputs != encoderDrops)
            {
                Interlocked.Add(ref framesDropped, encoder.DroppedInputs - encoderDrops);
                encoderDrops = encoder.DroppedInputs;
            }
            if (!IsPublishing) break;
        }
    }

    private async Task PumpRtmpAudioAsync(
        ChannelReader<PendingAudioBuffer> reader, RtmpPublisher publisher, MediaTimeline timeline, CancellationToken cancellation)
    {
        AacEncoder? encoder = null;
        var unavailable = false;
        try
        {
            await foreach (var buffer in reader.ReadAllAsync(cancellation))
            {
                try
                {
                    if (unavailable || !timeline.HasBegun) continue;
                    if (encoder is null)
                    {
                        try
                        {
                            encoder = AacEncoder.Create(buffer.SampleRate, buffer.Channels, options.AudioBitrateKbps);
                        }
                        catch (MediaFoundationException e)
                        {
                            unavailable = true;
                            BroadcastLog.Error("stream", $"audio encoder failed for {buffer.SampleRate} Hz×{buffer.Channels} — streaming without audio ({e.Message})");
                            continue;
                        }
                    }
                    foreach (var packet in encoder.Encode(buffer.Span, buffer.HostTime))
                    {
                        if (packet.PresentationTime < timeline.Origin) continue;
                        publisher.SendAudio(packet, timeline.Milliseconds(packet.PresentationTime));
                    }
                }
                finally
                {
                    buffer.Return();
                }
                if (!IsPublishing) break;
            }
        }
        finally
        {
            encoder?.Dispose();
        }
    }

    private async Task RunHlsAsync(Channel<PendingVideoFrame> frameQueue, Channel<PendingAudioBuffer> audioQueue, CancellationToken cancellation)
    {
        if (HlsUploader.UploadUrl(Destination.Url, "playlist.m3u8") is null)
            throw StreamSessionException.BadUrl(Destination.Url);
        using var encoder = CreateVideoEncoder(HlsTransport.SegmentDuration);
        var transport = new HlsTransport(Destination, hlsContinuity, new HlsUploader(Destination.Url, Destination.BackupUrl, hlsContinuity, options.HlsHandler));
        SetState(StreamSessionState.Publishing);
        BroadcastLog.Info("stream", $"publishing: {Destination.Name} -> hls ingest via {encoder.Name}");
        await transport.RunAsync(frameQueue.Reader, audioQueue.Reader, encoder, options.AudioBitrateKbps, () => Interlocked.Increment(ref framesDropped), cancellation);
    }
}

/// <summary>
/// The shared zero point of one RTMP attempt: the first picture's host time. Audio and video
/// are both timed from it (in milliseconds), so they stay in step on the server. (HaishinKit
/// starts each track at zero separately; one origin is the closer match to what was heard.)
/// </summary>
internal sealed class MediaTimeline
{
    private double origin = double.NaN;

    public bool HasBegun => !double.IsNaN(Volatile.Read(ref origin));

    public double Origin => Volatile.Read(ref origin);

    public void Begin(double hostTime)
    {
        if (!HasBegun) Volatile.Write(ref origin, hostTime);
    }

    public uint Milliseconds(double hostTime) =>
        (uint)Math.Max(0, Math.Round((hostTime - Origin) * 1000));
}
