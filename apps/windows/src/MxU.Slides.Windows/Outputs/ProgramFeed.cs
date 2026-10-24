using MxU.Slides.Core.Media;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.Rendering;

namespace MxU.Slides.Windows.Outputs;

/// <summary>
/// The program feed: the audience picture as frames, for recording and streaming. A
/// hidden copy of the audience output is drawn with the same scenes (transitions,
/// animations and videos included) and captured by <see cref="HiddenCapture"/>, which
/// hands frames to every destination at a steady rate. Port of the Mac's OutputMirror.
/// It only runs while something is listening.
/// </summary>
public sealed class ProgramFeed(MediaHub media, int width = 1920, int height = 1080, int frameRate = 30) : IDisposable
{
    private readonly List<IProgramSink> sinks = [];
    private readonly Lock gate = new();
    private HiddenCapture? capture;
    private SceneView? view;

    public int Width { get; } = width;
    public int Height { get; } = height;
    public int FrameRate { get; } = frameRate;

    /// <summary>True while at least one destination is listening.</summary>
    public bool IsActive
    {
        get { lock (gate) return sinks.Count > 0; }
    }

    public long FramesCaptured => capture?.FramesCaptured ?? 0;
    public long FramesDelivered => capture?.FramesDelivered ?? 0;
    public long FramesLate => capture?.FramesLate ?? 0;

    /// <summary>Why the feed can't run, or null.</summary>
    public string? Problem => capture?.Problem;

    /// <summary>True when this PC can't use Windows' fast capture and frames come from the slower software copy.</summary>
    public bool UsesSoftwareCopy => capture?.UsesSoftwareCopy == true;

    /// <summary>Raised on the UI thread when the feed starts or stops (the app sends it the live scene then).</summary>
    public event Action? ActiveChanged;

    public void Add(IProgramSink sink)
    {
        lock (gate)
        {
            if (sinks.Contains(sink)) return;
            sinks.Add(sink);
            if (sinks.Count > 1) return;
        }
        // Same playback scope as the audience window, so videos share its player and stay in step.
        view = new SceneView { Media = media, PlaysVideo = true, PlaybackScope = "live" };
        capture = new HiddenCapture(view, Width, Height, FrameRate, Sinks, "MxU Slides program feed");
        ActiveChanged?.Invoke();
    }

    public void Remove(IProgramSink sink)
    {
        lock (gate)
        {
            if (!sinks.Remove(sink) || sinks.Count > 0) return;
        }
        Stop();
        ActiveChanged?.Invoke();
    }

    public void Present(Core.Animation.AnimatedScene scene, SceneTransition transition) => view?.Present(scene, transition);

    public void Update(Core.Animation.AnimatedScene scene) => view?.Update(scene);

    /// <summary>Program audio (from the audio tap) goes to the same destinations as the pictures.</summary>
    public void WriteAudio(AudioBuffer buffer)
    {
        foreach (var sink in Sinks())
        {
            try { sink.WriteAudio(buffer); }
            catch (Exception ex) { Log.Error("A program destination failed to take audio", ex); }
        }
    }

    private IProgramSink[] Sinks()
    {
        lock (gate) return [.. sinks];
    }

    private void Stop()
    {
        view?.Present((Core.Scene.RenderScene?)null, SceneTransition.Cut);
        capture?.Dispose();
        capture = null;
        view = null;
    }

    public void Dispose()
    {
        lock (gate) sinks.Clear();
        Stop();
    }
}
