using MxU.Slides.Broadcast.Library;
using MxU.Slides.Broadcast.Streaming;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;
using MxU.Slides.Core.Threading;

namespace MxU.Slides.Broadcast.Control;

/// <summary>
/// The one object the app talks to for Stream &amp; Record: start a preset by id (its library
/// destinations resolved, keys from the vault, its source worked out from the library), ask
/// what a start would replace, count what a preset has running, end everything. Owns a
/// <see cref="StreamingController"/> and a <see cref="RecordingController"/>, and (once
/// <see cref="FileRecordingsIntoLibrary"/> is called) files finished recordings into the
/// media library on its own thread. Call from the UI thread. Port of the capture half of the
/// Mac's ServiceControls.
/// </summary>
public sealed class BroadcastController : IDisposable
{
    private readonly LibraryStore library;
    private WorkQueue? adoptions;

    public BroadcastController(LibraryStore library, StreamKeyVault keys, IProgramSource program,
        string? recordingsFolder = null, StreamSessionOptions? sessionOptions = null)
        : this(library, keys, new CaptureSources(program), recordingsFolder, sessionOptions)
    {
    }

    public BroadcastController(LibraryStore library, StreamKeyVault keys, CaptureSources sources,
        string? recordingsFolder = null, StreamSessionOptions? sessionOptions = null)
    {
        this.library = library;
        Keys = keys;
        Sources = sources;
        Recording = new RecordingController(sources, recordingsFolder);
        Streaming = new StreamingController(sources, Recording, sessionOptions)
        {
            SourceResolver = preset => StreamSources.Resolve(library, preset, DateTimeOffset.Now),
        };
    }

    public StreamKeyVault Keys { get; }

    public CaptureSources Sources { get; }

    public StreamingController Streaming { get; }

    public RecordingController Recording { get; }

    /// <summary>Raised (on the filing thread) when a finished recording has been filed into the library.</summary>
    public event Action<MediaItem>? RecordingFiled;

    /// <summary>
    /// From now on finished recordings go into the media library (the Mac always does this):
    /// they're written into the library's spool folder, then filed by a background thread, so
    /// the UI never waits while a long recording is fingerprinted. Call before <see cref="Prepare"/>.
    /// </summary>
    public void FileRecordingsIntoLibrary()
    {
        if (adoptions is not null) return;
        adoptions = new WorkQueue("MxU recordings into the library", ThreadPriority.BelowNormal);
        var queue = adoptions;
        Recording.LibraryAdoption = new RecordingController.Adoption(
            RecordingLibrary.SpoolDirectory(library.Root),
            (path, folder, exportFolder, startedAt, presetId) => queue.Async(() =>
            {
                try
                {
                    var item = RecordingLibrary.Adopt(library, path, folder, startedAt, presetId, exportFolder);
                    BroadcastLog.Info("recording.adopted", $"{Path.GetFileName(path)} is in the library as “{item.Name}”");
                    RecordingFiled?.Invoke(item);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // It stays in the spool; the next launch tries again.
                    BroadcastLog.Error("recording.adopt.failed", $"{Path.GetFileName(path)}: {e.Message}");
                }
            }));
    }

    /// <summary>
    /// Run once after the library loads: moves old inline destinations into the library,
    /// recovers recordings a crash left unfinished, and files any the spool still holds.
    /// Returns the "removed a destination" sentences.
    /// </summary>
    public IReadOnlyList<string> Prepare()
    {
        var (_, dropped) = StreamDestinationLibrary.MigrateStreamDestinations(library, Keys);
        Recording.RecoverInterrupted();
        if (adoptions is { } queue && Recording.LibraryAdoption is { } adoption)
        {
            var spool = adoption.SpoolFolder;
            queue.Async(() =>
            {
                foreach (var item in RecordingLibrary.AdoptLeftovers(library, spool)) RecordingFiled?.Invoke(item);
            });
        }
        return dropped;
    }

    /// <summary>Waits (up to <paramref name="timeout"/>) for recordings being filed into the library; false if some are still going.</summary>
    public bool WaitForFiling(TimeSpan timeout)
    {
        if (adoptions is not { } queue) return true;
        try
        {
            queue.Sync(() => { }, timeout);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public List<StreamPresetDestination> ResolvedDestinations(StreamRecordPreset preset) =>
        StreamDestinationLibrary.Resolve(library, Keys, preset);

    /// <summary>The line under a preset: what it restreams, or why it won't go live (null when all is well).</summary>
    public StreamSources.Preview? CapturePreview(StreamRecordPreset preset) =>
        StreamSources.CapturePreview(library, preset, ResolvedDestinations(preset), DateTimeOffset.Now);

    public void StartCapture(string presetId)
    {
        if (library.Get<StreamRecordPreset>(presetId) is { } preset) StartCapture(preset);
    }

    public void StartCapture(StreamRecordPreset preset) => Streaming.StartCapture(preset, ResolvedDestinations(preset));

    /// <summary>The name of the stream a start would end (only stream presets replace the one on air), or null.</summary>
    public string? StartWouldReplace(string presetId)
    {
        if (Streaming.Live is not { } live) return null;
        if (library.Get<StreamRecordPreset>(presetId) is not { } target || target.IsRecordOnly()) return null;
        return live.PresetName;
    }

    /// <summary>How many things a preset has going: its stream (if on air) plus its recordings.</summary>
    public int RunningCount(string presetId)
    {
        var count = Streaming.Live?.PresetId == presetId ? 1 : 0;
        return count + Recording.Sessions.Count(s => s.PresetId == presetId && s.IsRecording);
    }

    public void EndAllCapture()
    {
        Streaming.EndStream();
        foreach (var session in Recording.Sessions.Where(s => s.IsRecording).ToList()) Recording.Stop(session);
    }

    public void EndAll(string presetId)
    {
        if (Streaming.Live?.PresetId == presetId) Streaming.EndStream();
        foreach (var session in Recording.Sessions.Where(s => s.PresetId == presetId).ToList()) Recording.Stop(session);
    }

    public void Dispose()
    {
        Streaming.Dispose();
        Recording.Dispose();
        // Filing still queued carries on (its thread is a background one); what doesn't finish stays in the spool for next time.
        adoptions?.Dispose();
    }
}
