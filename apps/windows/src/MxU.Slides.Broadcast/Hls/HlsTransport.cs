using System.Threading.Channels;
using MxU.Slides.Broadcast.Hls.Vendored;
using MxU.Slides.Broadcast.Media;
using MxU.Slides.Broadcast.Streaming;

namespace MxU.Slides.Broadcast.Hls;

/// <summary>
/// One HLS publishing attempt: encode the program (a keyframe at least every
/// <see cref="SegmentDuration"/> seconds), mux it into MPEG-TS with <see cref="TsWriter"/>,
/// cut a segment at each keyframe past the segment length, and hand segments to the
/// <see cref="HlsUploader"/>. The muxer waits until it has seen audio (or 15 pictures without
/// any) before writing, so the first segment's PMT lists both tracks. When the frame queue
/// ends (Stop), the encoder is drained, the last short segment is uploaded and the run
/// finishes. Port of HLSTransport.swift.
/// </summary>
internal sealed class HlsTransport(StreamEndpoint destination, HlsContinuity continuity, HlsUploader uploader)
{
    public const double SegmentDuration = 2.0;

    private abstract record MuxEvent;

    private sealed record VideoEvent(EncodedVideoFrame Frame) : MuxEvent;

    private sealed record AudioEvent(EncodedAudioFrame Frame) : MuxEvent;

    public async Task RunAsync(
        ChannelReader<PendingVideoFrame> frames,
        ChannelReader<PendingAudioBuffer> audio,
        H264Encoder encoder,
        int audioBitrateKbps,
        Action frameDropped,
        CancellationToken cancellation)
    {
        var muxEvents = PendingQueues.Newest<MuxEvent>(240, _ => { });
        BroadcastLog.Info("hls", $"hls publishing: {destination.Name} codec=h264 {encoder.Settings.Width}x{encoder.Settings.Height}");

        using var failing = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var tasks = new List<Task>
        {
            Task.Run(() => EncodeVideoAsync(frames, encoder, muxEvents.Writer, frameDropped, failing.Token), CancellationToken.None),
            Task.Run(() => EncodeAudioAsync(audio, audioBitrateKbps, muxEvents.Writer, failing.Token), CancellationToken.None),
            Task.Run(() => MuxAsync(muxEvents.Reader, failing.Token), CancellationToken.None),
            Task.Run(() => uploader.RunAsync(failing.Token), CancellationToken.None),
        };
        // Like the Mac's task group: wait for all, but the first failure cancels the rest.
        var pending = new List<Task>(tasks);
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending);
            pending.Remove(done);
            if (done.IsFaulted)
            {
                failing.Cancel();
                foreach (var other in pending)
                {
                    try { await other; } catch { }
                }
                await done;
            }
        }
        cancellation.ThrowIfCancellationRequested();
    }

    private async Task EncodeVideoAsync(
        ChannelReader<PendingVideoFrame> frames, H264Encoder encoder, ChannelWriter<MuxEvent> output, Action frameDropped, CancellationToken cancellation)
    {
        var converter = new Nv12Converter(encoder.Settings.Width, encoder.Settings.Height);
        var nv12 = new byte[converter.FrameSize];
        var cadence = new KeyframeCadence(SegmentDuration);
        var encoderDrops = 0;
        try
        {
            await foreach (var frame in frames.ReadAllAsync(cancellation))
            {
                try
                {
                    converter.Convert(frame.Span, frame.Width, frame.Height, frame.Stride, nv12);
                }
                catch (ArgumentException)
                {
                    frameDropped();
                    continue;
                }
                finally
                {
                    frame.Return();
                }
                var force = cadence.ShouldForce(frame.HostTime);
                foreach (var encoded in Encode(() => encoder.Encode(nv12, frame.HostTime, force)))
                    output.TryWrite(new VideoEvent(encoded));
                for (; encoderDrops < encoder.DroppedInputs; encoderDrops++) frameDropped();
            }
            foreach (var encoded in Encode(encoder.Drain)) output.TryWrite(new VideoEvent(encoded));
        }
        finally
        {
            output.TryComplete();
        }
    }

    private static List<EncodedVideoFrame> Encode(Func<List<EncodedVideoFrame>> work)
    {
        try
        {
            return work();
        }
        catch (MediaFoundationException e)
        {
            throw StreamSessionException.TransportClosed($"video encoder failed: {e.Message}", e);
        }
    }

    private static async Task EncodeAudioAsync(
        ChannelReader<PendingAudioBuffer> audio, int bitrateKbps, ChannelWriter<MuxEvent> output, CancellationToken cancellation)
    {
        AacEncoder? encoder = null;
        var unavailable = false;
        try
        {
            await foreach (var buffer in audio.ReadAllAsync(cancellation))
            {
                try
                {
                    if (unavailable) continue;
                    if (encoder is null)
                    {
                        try
                        {
                            encoder = AacEncoder.Create(buffer.SampleRate, buffer.Channels, bitrateKbps);
                        }
                        catch (MediaFoundationException e)
                        {
                            unavailable = true;
                            BroadcastLog.Error("hls", $"hls audio converter init failed for {buffer.SampleRate} Hz×{buffer.Channels} — streaming without audio ({e.Message})");
                            continue;
                        }
                    }
                    foreach (var packet in encoder.Encode(buffer.Span, buffer.HostTime))
                        output.TryWrite(new AudioEvent(packet));
                }
                finally
                {
                    buffer.Return();
                }
            }
        }
        finally
        {
            encoder?.Dispose();
        }
    }

    private async Task MuxAsync(ChannelReader<MuxEvent> events, CancellationToken cancellation)
    {
        var writer = new TsWriter(SegmentDuration);
        var segmentData = new MemoryStream();
        var currentName = continuity.ClaimSegmentName();
        var finished = new List<HlsUploader.Item>();
        writer.Sink = bytes => segmentData.Write(bytes.Span);
        writer.OnSegmentCut = duration =>
        {
            finished.Add(new HlsUploader.Item(currentName, segmentData.ToArray(), duration));
            segmentData = new MemoryStream();
            currentName = continuity.ClaimSegmentName();
        };

        void Apply(MuxEvent e)
        {
            switch (e)
            {
                case VideoEvent video:
                    writer.VideoFormat = video.Frame.ParameterSets;
                    writer.Append(video.Frame);
                    break;
                case AudioEvent sound:
                    writer.AudioFormat = sound.Frame.Config;
                    writer.Append(sound.Frame);
                    break;
            }
        }

        void DeclareFormats(IEnumerable<MuxEvent> buffered)
        {
            foreach (var e in buffered)
            {
                if (e is VideoEvent video) writer.VideoFormat = video.Frame.ParameterSets;
                else if (e is AudioEvent sound) writer.AudioFormat = sound.Frame.Config;
            }
        }

        void Flush()
        {
            foreach (var item in finished) uploader.Enqueue(item);
            finished.Clear();
        }

        var buffered = new List<MuxEvent>();
        var writerStarted = false;
        var bufferedVideo = 0;
        var sawAudio = false;
        try
        {
            await foreach (var e in events.ReadAllAsync(cancellation))
            {
                if (!writerStarted)
                {
                    buffered.Add(e);
                    if (e is VideoEvent) bufferedVideo++;
                    if (e is AudioEvent) sawAudio = true;
                    if (!sawAudio && bufferedVideo < 15) continue;
                    writer.ExpectedMedias = sawAudio ? TsMedia.Video | TsMedia.Audio : TsMedia.Video;
                    DeclareFormats(buffered);
                    writerStarted = true;
                    buffered.ForEach(Apply);
                    buffered.Clear();
                }
                else
                {
                    Apply(e);
                }
                Flush();
            }
            if (!writerStarted && buffered.Count > 0)
            {
                writer.ExpectedMedias = sawAudio ? TsMedia.Video | TsMedia.Audio : TsMedia.Video;
                DeclareFormats(buffered);
                buffered.ForEach(Apply);
                Flush();
            }
            if (segmentData.Length > 0)
                uploader.Enqueue(new HlsUploader.Item(currentName, segmentData.ToArray(), Math.Max(writer.OpenSegmentSeconds, 1.0 / 30.0)));
        }
        finally
        {
            uploader.FinishEnqueuing();
        }
    }
}

/// <summary>
/// Forces a keyframe whenever a segment's worth of time has passed since the last forced one.
/// Encoders stretch the gap between keyframes on still pictures (a slide that doesn't move), and
/// a segment can only end on a keyframe, so without this a static slide would make one long
/// segment — and YouTube drops segments over 5 s. Port of HLSVideoEncoder.encode's rule.
/// </summary>
internal sealed class KeyframeCadence(double interval)
{
    private double? lastKeyframe;

    public bool ShouldForce(double presentationTime)
    {
        if (lastKeyframe is not null && presentationTime - lastKeyframe.Value < interval) return false;
        lastKeyframe = presentationTime;
        return true;
    }
}
