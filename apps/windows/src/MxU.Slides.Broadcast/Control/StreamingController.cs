using MxU.Slides.Broadcast.Library;
using MxU.Slides.Broadcast.Recording;
using MxU.Slides.Broadcast.Streaming;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Broadcast.Control;

/// <summary>The stream that is on air: which preset started it, its sessions (one per destination) and when.</summary>
public sealed class LiveStream
{
    internal LiveStream(string presetId, string presetName, IReadOnlyList<StreamSession> sessions, IReadOnlyList<IDisposable> attachments)
    {
        PresetId = presetId;
        PresetName = presetName;
        Sessions = sessions;
        Attachments = attachments;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public string PresetId { get; }

    public string PresetName { get; }

    public IReadOnlyList<StreamSession> Sessions { get; }

    internal IReadOnlyList<IDisposable> Attachments { get; }

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

    /// <summary>Set when streaming a media file (the Mac's REPLAY countdown). Never set on Windows yet.</summary>
    public double? MediaDurationSeconds { get; init; }
}

/// <summary>What a preset's picture comes from, as the app resolves it.</summary>
public abstract record ResolvedSource
{
    /// <summary>The program (the Mac lets a preset pick one of several screens; Windows has one program feed).</summary>
    public sealed record Screen : ResolvedSource;

    /// <summary>Can't start, with the sentence to show, e.g. "“Intro” isn't ready to play".</summary>
    public sealed record Unavailable(string Reason) : ResolvedSource;
}

/// <summary>
/// Goes live with a stream preset: one <see cref="StreamSession"/> per destination, each at the
/// preset's size and bitrate unless the destination overrides them (then the bitrate follows the
/// quality ladder) or caps the height (then the width and bitrate scale down with it). Refuses,
/// with <see cref="StartNotice"/>, when no destination is ready. Starting a preset ends the stream
/// already on air (and that preset's recordings). Record-only presets just start a recording.
///
/// Threading: call from the UI thread. <see cref="LiveChanged"/> and <see cref="StatusChanged"/>
/// (a session connected, dropped, reconnected…) are raised on the thread that created the
/// controller. Port of StreamingController.swift.
/// </summary>
public sealed class StreamingController : IDisposable
{
    private readonly IProgramSource program;
    private readonly RecordingController recording;
    private readonly StreamSessionOptions? sessionOptions;
    private readonly SynchronizationContext? context;
    private readonly Timer heartbeat;
    private LiveStream? live;

    public StreamingController(IProgramSource program, RecordingController recording, StreamSessionOptions? sessionOptions = null)
    {
        this.program = program;
        this.recording = recording;
        this.sessionOptions = sessionOptions;
        context = SynchronizationContext.Current;
        heartbeat = new Timer(_ => Post(Heartbeat), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
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

    /// <summary>Why the last start didn't go live, until the next start or end.</summary>
    public string? StartNotice { get; private set; }

    /// <summary>Lets the app say a preset's source isn't available (e.g. a media-file source, which Windows can't stream yet).</summary>
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
            return;
        }
        StartPresetRecording(preset, preset.VideoBitrateKbps ?? 4500);
    }

    private void StartPresetRecording(StreamRecordPreset preset, int bitrateKbps)
    {
        try
        {
            recording.Start(
                targetId: string.IsNullOrEmpty(preset.CanvasScreenId) ? "program" : preset.CanvasScreenId,
                targetName: preset.Name,
                codec: RecordingCodecs.Parse(preset.RecordCodec),
                width: preset.Width ?? 1920,
                height: preset.Height ?? 1080,
                frameRate: preset.FrameRate ?? 30,
                bitrateKbps: bitrateKbps,
                audioSource: StreamAudioSource.Of(preset),
                audioIncludesProgram: preset.AudioIncludesProgram ?? false,
                audioDelayMs: preset.AudioDelayMs ?? 0,
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
    public const string RecordingLibraryDefaultFolder = "Recordings";

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

        StartSessions(preset, destinations.Select(SessionPlanFor).ToList());
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

    private void StartSessions(StreamRecordPreset preset, IReadOnlyList<SessionPlan> plans)
    {
        var sessions = plans.Select(plan => new StreamSession(plan.Destination, ResolveVideo(preset, plan), sessionOptions)).ToList();
        if (sessions.Count == 0) return;
        var attachments = new List<IDisposable>();
        foreach (var session in sessions)
        {
            session.StateChanged += (s, _) => Post(() => StatusChanged?.Invoke(s));
            session.Start();
            attachments.Add(program.Attach(new FramePacer(session, session.Video.SafeFrameRate)));
        }
        if (StreamAudioSource.Of(preset).Kind != StreamAudioSourceKind.Program || (preset.AudioDelayMs ?? 0) != 0)
            BroadcastLog.Info("stream", $"{preset.Name}: audio mixes, inputs and delay aren't available on Windows yet — streaming program audio");

        if (preset.RecordWhileStreaming == true)
            StartPresetRecording(preset, preset.VideoBitrateKbps ?? 4500);

        Live = new LiveStream(preset.Id, preset.Name, sessions, attachments);
    }

    /// <summary>Ends the stream on air (all its destinations). Its recordings keep going; the caller stops those.</summary>
    public void EndStream()
    {
        StartNotice = null;
        if (live is not { } current) return;
        foreach (var attachment in current.Attachments) attachment.Dispose();
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
                $"{session.Destination.Name} state={status.State} submitted={status.FramesSubmitted} dropped={status.FramesDropped} encoder={session.EncoderName ?? "-"}");
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
