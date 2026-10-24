using System.Globalization;
using MxU.Slides.Broadcast.Recording;
using MxU.Slides.Core.Threading;

namespace MxU.Slides.Broadcast.Control;

/// <summary>One recording the controller is running (or has just stopped).</summary>
public sealed class RecordingSession
{
    internal RecordingSession(string targetId, string targetName, string? presetId, Recorder recorder, IDisposable attachment, IDisposable sound,
        string? libraryFolder, string? exportFolder)
    {
        TargetId = targetId;
        TargetName = targetName;
        PresetId = presetId;
        Recorder = recorder;
        Attachment = attachment;
        Sound = sound;
        LibraryFolder = libraryFolder;
        ExportFolder = exportFolder;
    }

    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>The screen being recorded: "program" (the audience program) or a screen's id.</summary>
    public string TargetId { get; }

    public string TargetName { get; }

    public string? PresetId { get; }

    public Recorder Recorder { get; }

    /// <summary>The recording's hold on its screen's pictures.</summary>
    internal IDisposable Attachment { get; }

    /// <summary>The recording's sound feed.</summary>
    internal IDisposable Sound { get; }

    /// <summary>Lets go of the pictures and the sound (safe to call more than once).</summary>
    internal void Detach()
    {
        Attachment.Dispose();
        Sound.Dispose();
    }

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

    public string FilePath => Recorder.FilePath;

    /// <summary>The library folder the finished file is filed into (when a <see cref="RecordingController.LibraryAdoption"/> is set).</summary>
    public string? LibraryFolder { get; }

    /// <summary>A folder a copy is exported to as well.</summary>
    public string? ExportFolder { get; }

    public RecorderStatus Status => Recorder.Status;

    public bool IsRecording => Recorder.Status.State.Kind == RecorderStateKind.Recording;
}

/// <summary>
/// Starts and stops recordings. Each recording takes the pictures of one screen (the audience
/// program unless a preset names another, such as the stage display) at its own frame rate and
/// size, and the sound its preset asks for (program audio, a mix or a sound input, maybe
/// delayed), and writes an MP4 into the recordings folder (Videos\MxU Slides) or, when the app
/// files recordings into its library, a spool folder handed to <see cref="LibraryAdoption"/>
/// once the file is finished.
///
/// Threading: like the Mac's @MainActor controller, call it from the UI thread.
/// <see cref="SessionsChanged"/> and <see cref="StatusChanged"/> are raised on the thread (and
/// synchronization context) that created the controller. Port of RecordingController.swift.
/// </summary>
public sealed class RecordingController : IDisposable
{
    private readonly CaptureSources sources;
    private readonly SynchronizationContext? context;
    private readonly List<RecordingSession> sessions = [];
    private readonly HashSet<Guid> adoptedSessions = [];
    private readonly ITimer heartbeat;

    public RecordingController(IProgramSource program, string? recordingsFolder = null)
        : this(new CaptureSources(program), recordingsFolder)
    {
    }

    public RecordingController(CaptureSources sources, string? recordingsFolder = null)
    {
        this.sources = sources;
        context = SynchronizationContext.Current;
        RecordingsFolder = recordingsFolder ?? DefaultRecordingsFolder;
        heartbeat = ShowTimeProvider.Shared.CreateTimer(_ => Post(Heartbeat), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    /// <summary>Videos\MxU Slides: where recordings go when the library doesn't take them.</summary>
    public static string DefaultRecordingsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "MxU Slides");

    public string RecordingsFolder { get; }

    /// <summary>Which H.264 encoder new recordings use (hardware when present, by default).</summary>
    public Media.EncoderPreference Encoder { get; set; } = Media.EncoderPreference.Automatic;

    /// <summary>How finished recordings are filed into the library (the app sets this); null keeps them in the recordings folder.</summary>
    public sealed record Adoption(string SpoolFolder, Action<string, string, string?, DateTimeOffset, string?> Adopt);

    /// <summary>(path, libraryFolder, exportFolder, startedAt, presetId) for each finished library recording.</summary>
    public Adoption? LibraryAdoption { get; set; }

    public IReadOnlyList<RecordingSession> Sessions => sessions;

    public event Action? SessionsChanged;

    /// <summary>A recording's state changed (raised on the UI context).</summary>
    public event Action<RecordingSession>? StatusChanged;

    public bool AnyRecording => sessions.Any(s => s.IsRecording);

    /// <summary>A recording stopped on its own: disk full, or the writer failed.</summary>
    public bool AnyStoppedAbnormally => sessions.Any(s => s.Status.State switch
    {
        { Kind: RecorderStateKind.Finished, Reason: RecordingStopReason.DiskFull or RecordingStopReason.WriterFailed } => true,
        { Kind: RecorderStateKind.Failed } => true,
        _ => false,
    });

    public bool IsRecording(string targetId) => sessions.Any(s => s.TargetId == targetId && s.IsRecording);

    /// <summary>
    /// Recordings a crash left unfinished (named *.partial.mp4) get their final names. Call once
    /// at start-up; returns the recovered files.
    /// </summary>
    public IReadOnlyList<string> RecoverInterrupted()
    {
        var recovered = new List<string>(Recorder.RecoverInterrupted(RecordingsFolder));
        if (LibraryAdoption is { } adoption) recovered.AddRange(Recorder.RecoverInterrupted(adoption.SpoolFolder));
        return recovered;
    }

    /// <summary>
    /// Starts recording <paramref name="targetId"/> ("program" or a screen id). Throws
    /// <see cref="IOException"/> when the file can't be started (no space, no encoder, folder
    /// not writable). <paramref name="libraryFolder"/> null keeps the file out of the library.
    /// </summary>
    public RecordingSession Start(
        string targetId,
        string targetName,
        RecordingCodec codec,
        int width,
        int height,
        int frameRate,
        int? bitrateKbps = null,
        string? folder = null,
        SessionAudioRequest? audio = null,
        string? presetId = null,
        string? libraryFolder = null,
        string? exportFolder = null)
    {
        var stamp = DateTime.Now.ToString("yyyy-MM-dd 'at' h.mm.ss tt", CultureInfo.InvariantCulture);
        var adopting = libraryFolder is not null && LibraryAdoption is not null;
        var destination = adopting ? LibraryAdoption!.SpoolFolder : folder ?? RecordingsFolder;
        Directory.CreateDirectory(destination);
        var path = Path.Combine(destination, $"{SafeFileName(targetName)} {stamp}.mp4");
        var configuration = new RecordingConfiguration(
            codec, width, height, frameRate,
            AverageBitrate: bitrateKbps * 1000,
            IncludesAudio: true)
        { Encoder = Encoder };
        var recorder = new Recorder(path, configuration);
        var sound = audio ?? SessionAudioRequest.Program;
        IDisposable attachment;
        try
        {
            attachment = sources.AttachPictures(targetId, width, height, frameRate, new FramePacer(recorder, frameRate));
        }
        catch (Exception e) when (e is not IOException)
        {
            // Nothing to record from: finish the (empty) file so its thread stops, and say why.
            _ = recorder.FinishAsync();
            throw new IOException($"couldn't picture {targetId}: {e.Message}", e);
        }
        var soundFeed = sources.OpenAudio(sound, recorder);
        var session = new RecordingSession(targetId, targetName, presetId, recorder, attachment, soundFeed,
            adopting ? libraryFolder : null, adopting ? exportFolder : null);
        recorder.StateChanged += (_, _) => Post(() =>
        {
            FinalizeIfNeeded(session);
            StatusChanged?.Invoke(session);
        });
        sessions.Add(session);
        BroadcastLog.Info("recording.start", $"{targetName} target={targetId} codec={codec.Raw()} {width}x{height}@{frameRate} audio={sound}");
        SessionsChanged?.Invoke();
        return session;
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "Recording" : cleaned;
    }

    public void StopAll(string presetId)
    {
        foreach (var session in sessions.Where(s => s.PresetId == presetId && s.IsRecording).ToList()) Stop(session);
    }

    /// <summary>Stops a recording; it leaves <see cref="Sessions"/> once its file is final.</summary>
    public void Stop(RecordingSession session) => _ = StopAsync(session);

    public async Task StopAsync(RecordingSession session)
    {
        session.Detach();
        await session.Recorder.FinishAsync();
        FinalizeIfNeeded(session);
        if (sessions.Remove(session)) SessionsChanged?.Invoke();
    }

    /// <summary>Removes a recording that stopped by itself (disk full, failure) from the list.</summary>
    public void Dismiss(RecordingSession session)
    {
        session.Detach();
        FinalizeIfNeeded(session);
        if (sessions.Remove(session)) SessionsChanged?.Invoke();
    }

    /// <summary>Detaches recordings that have already stopped from their pictures and sound.</summary>
    public void ReapFinished()
    {
        foreach (var session in sessions.Where(s => !s.IsRecording)) session.Detach();
    }

    private void FinalizeIfNeeded(RecordingSession session)
    {
        if (LibraryAdoption is not { } adoption || session.LibraryFolder is not { } libraryFolder) return;
        if (session.Status.State.Kind != RecorderStateKind.Finished || !adoptedSessions.Add(session.Id)) return;
        BroadcastLog.Info("recording.adopt", $"{Path.GetFileName(session.FilePath)} → {libraryFolder}"
            + (session.ExportFolder is { } export ? $" (+ copy → {export})" : ""));
        adoption.Adopt(session.FilePath, libraryFolder, session.ExportFolder, session.StartedAt, session.PresetId);
    }

    private void Heartbeat()
    {
        foreach (var session in sessions.ToList())
        {
            var status = session.Status;
            FinalizeIfNeeded(session);
            BroadcastLog.Info("recording.heartbeat",
                $"{session.TargetName} state={status.State} frames={status.AppendedFrames} dropped={status.DroppedFrames} "
                + $"audioDropped={status.DroppedAudioBuffers} freeDiskMB={(status.FreeDiskBytes == long.MaxValue ? -1 : status.FreeDiskBytes / 1_000_000)}");
        }
    }

    private void Post(Action action)
    {
        if (context is null) action();
        else context.Post(_ => action(), null);
    }

    /// <summary>Stops the diagnostics heartbeat. (Recordings still running are not stopped; stop them first.)</summary>
    public void Dispose() => heartbeat.Dispose();
}
