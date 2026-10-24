using MxU.Slides.Broadcast.Library;
using MxU.Slides.Broadcast.Streaming;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Broadcast.Control;

/// <summary>
/// The one object the app talks to for Stream &amp; Record: start a preset by id (its library
/// destinations resolved, keys from the vault), ask what a start would replace, count what a
/// preset has running, end everything. Owns a <see cref="StreamingController"/> and a
/// <see cref="RecordingController"/>. Call from the UI thread. Port of the capture half of the
/// Mac's ServiceControls.
/// </summary>
public sealed class BroadcastController : IDisposable
{
    private readonly LibraryStore library;

    public BroadcastController(LibraryStore library, StreamKeyVault keys, IProgramSource program,
        string? recordingsFolder = null, StreamSessionOptions? sessionOptions = null)
    {
        this.library = library;
        Keys = keys;
        Recording = new RecordingController(program, recordingsFolder);
        Streaming = new StreamingController(program, Recording, sessionOptions);
    }

    public StreamKeyVault Keys { get; }

    public StreamingController Streaming { get; }

    public RecordingController Recording { get; }

    /// <summary>
    /// Run once after the library loads: moves old inline destinations into the library and
    /// recovers recordings a crash left unfinished. Returns the "removed a destination" sentences.
    /// </summary>
    public IReadOnlyList<string> Prepare()
    {
        var (_, dropped) = StreamDestinationLibrary.MigrateStreamDestinations(library, Keys);
        Recording.RecoverInterrupted();
        return dropped;
    }

    public List<StreamPresetDestination> ResolvedDestinations(StreamRecordPreset preset) =>
        StreamDestinationLibrary.Resolve(library, Keys, preset);

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
    }
}
