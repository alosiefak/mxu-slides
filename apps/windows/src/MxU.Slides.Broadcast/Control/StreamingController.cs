using MxU.Slides.Broadcast.Library;
using MxU.Slides.Broadcast.Media;
using MxU.Slides.Broadcast.Recording;
using MxU.Slides.Broadcast.Streaming;
using MxU.Slides.Core.Media;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Threading;

namespace MxU.Slides.Broadcast.Control;

/// <summary>The stream that is on air: which preset started it, its sessions (one per destination) and when.</summary>
public sealed class LiveStream
{
    internal LiveStream(string presetId, string presetName, IReadOnlyList<StreamSession> sessions, IReadOnlyList<IDisposable> attachments,
        MediaFileSource? fileSource, IDisposable? sound)
    {
        PresetId = presetId;
        PresetName = presetName;
        Sessions = sessions;
        Attachments = attachments;
        FileSource = fileSource;
        Sound = sound;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public string PresetId { get; }

    public string PresetName { get; }

    public IReadOnlyList<StreamSession> Sessions { get; }

    /// <summary>Each session's hold on its screen's pictures.</summary>
    internal IReadOnlyList<IDisposable> Attachments { get; }

    /// <summary>The file being replayed, when the preset streams a media file instead of a screen.</summary>
    public MediaFileSource? FileSource { get; }

    /// <summary>The stream's sound (shared by every destination); null while replaying a file, which brings its own.</summary>
    internal IDisposable? Sound { get; }

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

    /// <summary>Set when streaming a media file: how long it runs (the toolbar counts down "REPLAY 12:40").</summary>
    public double? MediaDurationSeconds { get; init; }
}

/// <summary>What a preset's picture comes from, as the app resolves it.</summary>
public abstract record ResolvedSource
{
    /// <summary>A screen: the preset's (or destination's) canvas screen, or the audience program when it names none.</summary>
    public sealed record Screen : ResolvedSource;

    /// <summary>A video file from the library, played out in real time (in and out points in seconds).</summary>
    public sealed record MediaFile(string Path, string Name, double? InPoint, double? OutPoint, double? DurationSeconds) : ResolvedSource;

    /// <summary>Can't start, with the sentence to show, e.g. "“Intro” isn't ready to play".</summary>
    public sealed record Unavailable(string Reason) : ResolvedSource;
}

/// <summary>
/// Goes live with a stream preset: one <see cref="StreamSession"/> per destination, each at the
/// preset's size and bitrate unless the destination overrides them (then the bitrate follows the
/// quality ladder) or caps the height (then the width and bitrate scale down with it). Refuses,
/// with <see cref="StartNotice"/>, when no destination is ready. Starting a preset ends the stream
/// already on air (and that preset's recordings). Record-only presets just start a recording.
/// <para>
/// Pictures: each destination shows its own canvas screen if it names one, else the preset's,
/// else the audience program; destinations showing the same screen share one picture feed (the
/// biggest size and fastest rate any of them wants). A media-file preset replays the file to
/// every destination instead, and ends the stream when the file ends. Sound: one feed for the
/// whole stream (program, a mix or a sound input, maybe delayed), or the file's own.
/// </para>
/// Threading: call from the UI thread. <see cref="LiveChanged"/> and <see cref="StatusChanged"/>
/// (a session connected, dropped, reconnected…) are raised on the thread that created the
/// controller. Port of StreamingController.swift.
/// </summary>
public sealed class StreamingController : IDisposable
{
    private readonly CaptureSources sources;
    private readonly RecordingController recording;
    private readonly StreamSessionOptions? sessionOptions;
    private readonly SynchronizationContext? context;
    private readonly ITimer heartbeat;
    private LiveStream? live;

    public StreamingController(IProgramSource program, RecordingController recording, StreamSessionOptions? sessionOptions = null)
        : this(new CaptureSources(program), recording, sessionOptions)
    {
    }

    public StreamingController(CaptureSources sources, RecordingController recording, StreamSessionOptions? sessionOptions = null)
    {
        this.sources = sources;
        this.recording = recording;
        this.sessionOptions = sessionOptions;
        context = SynchronizationContext.Current;
        heartbeat = ShowTimeProvider.Shared.CreateTimer(_ => Post(Heartbeat), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    public LiveStream? Live
    {
        get => live;
        private set
        {
            live = value;
            LiveChanged?.Invoke();
        }
    }

    public event Action? LiveChanged;

    /// <summary>A session's state changed; re-read <see cref="WorstTint"/>, <see cref="CaptionWord"/> and the health lines.</summary>
    public event Action<StreamSession>? StatusChanged;

    /// <summary>Why the last start didn't go live (or why a replay ended early), until the next start or end.</summary>
    public string? StartNotice { get; private set; }

    /// <summary>What a preset streams: a screen, a media file, or why it can't (the broadcast controller sets this from the library).</summary>
    public Func<StreamRecordPreset, ResolvedSource>? SourceResolver { get; set; }

    public bool IsLive => live is not null;

    public StreamHealthRollup.Tint WorstTint => StreamHealthRollup.TintOf(RollupInputs);

    public string CaptionWord => StreamHealthRollup.Word(RollupInputs);

    private List<StreamHealthRollup.Input> RollupInputs =>
        live?.Sessions.Select(s => new StreamHealthRollup.Input(s.Status.State)).ToList() ?? [];

    public void StartCapture(StreamRecordPreset preset, IReadOnlyList<StreamPresetDestination> destinations)
    {
        StartNotice = null;
        if (preset.IsRecordOnly()) StartRecordOnly(preset);
        else GoLive(preset, destinations);
    }

    private void StartRecordOnly(StreamRecordPreset preset)
    {
        if ((preset.SourceKind ?? StreamSourceKind.Screen) != StreamSourceKind.Screen)
        {
            BroadcastLog.Warning("stream.source", $"{preset.Name}: record-only preset needs a screen source");
            StartNotice = $"{preset.Name}: a record-only preset records a screen, not a media file";
            return;
        }
        StartPresetRecording(preset, preset.VideoBitrateKbps ?? 4500);
    }

    private void StartPresetRecording(StreamRecordPreset preset, int bitrateKbps)
    {
        try
        {
            recording.Start(
                targetId: CaptureSources.TargetFor(preset.CanvasScreenId),
                targetName: preset.Name,
                codec: RecordingCodecs.Parse(preset.RecordCodec),
                width: preset.Width ?? 1920,
                height: preset.Height ?? 1080,
                frameRate: preset.FrameRate ?? 30,
                bitrateKbps: bitrateKbps,
                audio: SessionAudioRequest.Of(preset),
                presetId: preset.Id,
                libraryFolder: preset.RecordLibraryFolder ?? RecordingLibraryDefaultFolder,
                exportFolder: preset.RecordExportFolder);
        }
        catch (IOException e)
        {
            BroadcastLog.Error("recording", $"{preset.Name}: couldn't start recording — {e.Message}");
            StartNotice = $"{preset.Name}: couldn't start recording — {e.Message}";
        }
    }

    /// <summary>The library folder recordings go to by default (the Mac's RecordingLibrary.defaultFolder).</summary>
    public const string RecordingLibraryDefaultFolder = RecordingLibrary.DefaultFolder;

    public void GoLive(StreamRecordPreset preset, IReadOnlyList<StreamPresetDestination> destinations)
    {
        var source = SourceResolver?.Invoke(preset) ?? new ResolvedSource.Screen();
        if (source is ResolvedSource.Unavailable unavailable)
        {
            StartNotice = $"{preset.Name}: {unavailable.Reason}";
            BroadcastLog.Warning("stream.source.unavailable", StartNotice);
            return;
        }
        if (StreamDestinationReadiness.StartRefusal(StreamPresetKind.Stream, destinations) is { } refusal)
        {
            var sentence = $"{preset.Name} — {refusal}";
            StartNotice = sentence;
            BroadcastLog.Warning("stream.destination.refused", sentence);
            return;
        }

        var outgoing = live?.PresetId;
        EndStream();
        if (outgoing is not null) recording.StopAll(outgoing);

        StartSessions(preset, source, destinations.Select(SessionPlanFor).ToList());
    }

    /// <summary>One destination's stream before the preset's defaults are applied.</summary>
    internal sealed record SessionPlan(
        StreamEndpoint Destination, StreamCodec Codec, int? MaxHeight, int? MaxBitrateKbps, bool Hdr,
        string? CanvasScreenId, int? Width, int? Height, int? FrameRate);

    internal static SessionPlan SessionPlanFor(StreamPresetDestination destination) => new(
        new StreamEndpoint(destination.Name, KindFor(destination.Transport), destination.Url, destination.StreamKey ?? "", id: destination.Id),
        CodecFor(destination),
        destination.MaxHeight,
        null,
        HdrFor(destination),
        destination.CanvasScreenId,
        destination.Width,
        destination.Height,
        destination.FrameRate);

    /// <summary>HDR only for HEVC over HLS, as on the Mac (Windows then streams it as SDR).</summary>
    private static bool HdrFor(StreamPresetDestination d) =>
        d.Hdr == true && d.Transport == StreamTransport.Hls && d.VideoCodec == StreamVideoCodec.Hevc;

    private static StreamCodec CodecFor(StreamPresetDestination d) =>
        d.VideoCodec == StreamVideoCodec.Hevc && d.Transport == StreamTransport.Hls ? StreamCodec.Hevc : StreamCodec.H264;

    private static StreamEndpointKind KindFor(StreamTransport transport) => transport switch
    {
        StreamTransport.Rtmp => StreamEndpointKind.Rtmp,
        StreamTransport.Rtmps => StreamEndpointKind.Rtmps,
        StreamTransport.Srt => StreamEndpointKind.Srt,
        _ => StreamEndpointKind.Hls,
    };

    /// <summary>
    /// The picture each destination gets. Port of the sizing half of the Mac's startSessions:
    /// overrides take the ladder's bitrate (or scale the preset's by area); a height cap scales
    /// the width to match (kept even) and the bitrate by area, never below 2 Mbps.
    /// </summary>
    internal static StreamVideoConfiguration ResolveVideo(StreamRecordPreset preset, SessionPlan plan)
    {
        var presetWidth = preset.Width ?? 1920;
        var presetHeight = preset.Height ?? 1080;
        var presetFrameRate = preset.FrameRate ?? 30;
        var presetBitrate = preset.VideoBitrateKbps ?? 4500;
        var overridden = plan.Width is not null || plan.Height is not null;
        var width = plan.Width ?? presetWidth;
        var height = plan.Height ?? presetHeight;
        var rate = plan.FrameRate ?? presetFrameRate;
        int bitrate;
        if (overridden)
        {
            bitrate = StreamQualityRung.RecommendedKbps(width, height, rate, plan.Hdr)
                ?? Math.Max(2000, (int)((long)presetBitrate * (width * height) / (presetWidth * presetHeight)));
        }
        else
        {
            bitrate = presetBitrate;
        }
        if (plan.MaxHeight is { } cap && cap < height)
        {
            var scale = (double)cap / height;
            width = Math.Max(2, (int)Math.Round(width * scale)) & ~1;
            var unscaledPixels = (long)(plan.Width ?? presetWidth) * (plan.Height ?? presetHeight);
            bitrate = Math.Max(2000, (int)((long)bitrate * width * cap / unscaledPixels));
            height = cap;
        }
        if (plan.MaxBitrateKbps is { } bitrateCap) bitrate = Math.Min(bitrate, bitrateCap);
        return new StreamVideoConfiguration(width, height, rate, bitrate, plan.Codec, plan.Hdr);
    }

    /// <summary>
    /// Which screen each destination shows: its own canvas, else the preset's, else the
    /// program. (The Mac falls back to its first screen; on Windows that is the audience program.)
    /// </summary>
    internal static string TargetFor(StreamRecordPreset preset, SessionPlan plan) =>
        !string.IsNullOrEmpty(plan.CanvasScreenId) ? plan.CanvasScreenId : CaptureSources.TargetFor(preset.CanvasScreenId);

    private void StartSessions(StreamRecordPreset preset, ResolvedSource source, IReadOnlyList<SessionPlan> rawPlans)
    {
        var file = source as ResolvedSource.MediaFile;
        // A file is SDR; it can't be streamed as HDR.
        var plans = file is null ? rawPlans : rawPlans.Select(plan => plan with { Hdr = false }).ToList();
        var resolved = plans.Select(plan => (Session: new StreamSession(plan.Destination, ResolveVideo(preset, plan), sessionOptions), Target: TargetFor(preset, plan))).ToList();
        if (resolved.Count == 0) return;
        var sessions = resolved.Select(r => r.Session).ToList();
        foreach (var session in sessions)
        {
            session.StateChanged += (s, _) => Post(() => StatusChanged?.Invoke(s));
            session.Start();
        }

        var attachments = new List<IDisposable>();
        MediaFileSource? fileSource = null;
        IDisposable? sound = null;
        if (file is not null)
        {
            var pacers = sessions.Select(s => new FramePacer(s, s.Video.SafeFrameRate)).ToList();
            var presetName = preset.Name;
            MediaFileSource? created = null;
            created = new MediaFileSource(file.Path, file.InPoint, file.OutPoint,
                frame =>
                {
                    foreach (var pacer in pacers) pacer.WriteVideo(frame);
                },
                buffer =>
                {
                    foreach (var session in sessions) session.WriteAudio(buffer);
                },
                reason => Post(() => MediaEnded(created!, file.Name, presetName, reason)));
            fileSource = created;
            BroadcastLog.Info("stream.replay", $"{preset.Name}: replaying “{file.Name}”");
            fileSource.Start();
        }
        else
        {
            // Destinations on the same screen share its feed, at the biggest size and fastest rate any of them wants.
            foreach (var group in resolved.GroupBy(r => r.Target))
            {
                var width = group.Max(r => r.Session.Video.Width);
                var height = group.Max(r => r.Session.Video.Height);
                var rate = group.Max(r => r.Session.Video.SafeFrameRate);
                foreach (var (session, _) in group)
                    attachments.Add(sources.AttachPictures(group.Key, width, height, rate, new FramePacer(session, session.Video.SafeFrameRate)));
            }
            sound = sources.OpenAudio(SessionAudioRequest.Of(preset), new AudioFanout(sessions));
            if (preset.RecordWhileStreaming == true)
                StartPresetRecording(preset, preset.VideoBitrateKbps ?? 4500);
        }

        Live = new LiveStream(preset.Id, preset.Name, sessions, attachments, fileSource, sound)
        {
            MediaDurationSeconds = file?.DurationSeconds,
        };
    }

    /// <summary>The replayed file ended (or couldn't be read): the stream ends with it, and says why if it wasn't the natural end.</summary>
    private void MediaEnded(MediaFileSource ended, string name, string presetName, string? reason)
    {
        if (live?.FileSource != ended) return;
        BroadcastLog.Info("stream.mediaEnded", $"{name}: {reason ?? "end of media"} — ending stream");
        EndStream();
        // (The Mac sets this before ending the stream, which clears it again; it's kept here so the operator sees why.)
        if (reason is not null) StartNotice = $"{presetName}: {reason}";
    }

    /// <summary>Ends the stream on air (all its destinations). Its recordings keep going; the caller stops those.</summary>
    public void EndStream()
    {
        StartNotice = null;
        if (live is not { } current) return;
        foreach (var attachment in current.Attachments) attachment.Dispose();
        current.FileSource?.Stop();
        current.Sound?.Dispose();
        foreach (var session in current.Sessions) session.Stop();
        Live = null;
    }

    private void Heartbeat()
    {
        if (live is not { } current) return;
        foreach (var session in current.Sessions)
        {
            var status = session.Status;
            BroadcastLog.Info("stream.heartbeat",
                $"{session.Destination.Name} state={status.State} submitted={status.FramesSubmitted} dropped={status.FramesDropped} encoder={session.EncoderName ?? "-"}"
                + (current.FileSource is { } file ? $" replayDelivered={file.FramesDelivered} replayDropped={file.FramesDropped}" : ""));
        }
    }

    private void Post(Action action)
    {
        if (context is null) action();
        else context.Post(_ => action(), null);
    }

    public void Dispose()
    {
        heartbeat.Dispose();
        EndStream();
    }
}
