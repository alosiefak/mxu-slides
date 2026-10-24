using System.Windows.Threading;
using MxU.Slides.Broadcast.Control;
using MxU.Slides.Core.Media;
using MxU.Slides.Core.Outputs;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.Rendering;
using AnimatedScene = MxU.Slides.Core.Animation.AnimatedScene;

namespace MxU.Slides.Windows.Outputs;

/// <summary>
/// Pictures of one screen at a time, for recording or streaming something other than the
/// audience program: the stage display, a lower-thirds screen, an LED wall... or the program
/// itself at more than the program feed's 30 frames a second. Each is built like
/// <see cref="ProgramFeed"/>: a hidden <see cref="SceneView"/> shown inside a
/// <see cref="HiddenCapture"/> window, given the same scenes that screen's outputs get.
/// <list type="bullet">
/// <item>What a screen shows is worked out the way <see cref="OutputManager"/> does it, from its
/// public <see cref="OutputManager.Config"/>: the program reshaped to the screen's size and
/// layer routing, or the stage display, or the alignment grid while lining up.</item>
/// <item>The app hands every new audience and stage scene to <see cref="Show"/> (next to where it
/// sends them to the outputs), so transitions, builds and videos match the real screen. Videos
/// share the outputs' players ("live" or "stage"), so they stay in step.</item>
/// <item>A feed is made when its first destination is added and closed when its last one goes.
/// Destinations wanting the same screen at the same size and rate share one feed.</item>
/// </list>
/// Like the Mac's OutputMirror with a screen's previewProvider. Use from the UI thread.
/// </summary>
public sealed class ScreenFeeds : IDisposable
{
    private readonly OutputManager outputs;
    private readonly MediaHub media;
    private readonly Dispatcher dispatcher;
    private readonly Dictionary<(string Target, int Width, int Height, int Rate), Feed> feeds = [];
    private AnimatedScene? audience;
    private AnimatedScene? stage;
    private bool disposed;

    public ScreenFeeds(OutputManager outputs, MediaHub media)
    {
        this.outputs = outputs;
        this.media = media;
        dispatcher = Dispatcher.CurrentDispatcher;
        outputs.Config.StateChanged += OnConfigChanged;
        outputs.Config.ConfigurationChanged += OnConfigChanged;
    }

    /// <summary>How many screen feeds are running (each is a hidden window being captured).</summary>
    public int RunningCount => feeds.Values.Count(f => f.IsRunning);

    /// <summary>Why the last feed to start can't capture (no graphics card that can), or null.</summary>
    public string? Problem => feeds.Values.Select(f => f.Problem).FirstOrDefault(p => p is not null);

    /// <summary>
    /// The pictures of <paramref name="target"/> (a screen's id, or "program") at a size and rate,
    /// for <see cref="CaptureSources.Screens"/>. Null for a screen that isn't in the setup.
    /// Nothing is drawn until a destination is added to the source.
    /// </summary>
    public IProgramSource? Source(string target, int width, int height, int frameRate)
    {
        if (disposed) return null;
        if (target != CaptureSources.ProgramTarget && (SwiftUuid.Parse(target) is not { } id || outputs.Config.Screen(id) is null)) return null;
        var key = (Target: target, Width: Math.Max(2, width & ~1), Height: Math.Max(2, height & ~1), Rate: Math.Clamp(frameRate, 1, 120));
        if (!feeds.TryGetValue(key, out var feed)) feeds[key] = feed = new Feed(this, key.Target, key.Width, key.Height, key.Rate);
        return feed;
    }

    /// <summary>
    /// A new scene for every screen with that job: <paramref name="transition"/> null means the
    /// same cue refreshed (a clock ticking), as <see cref="OutputManager.Update(OutputRole, AnimatedScene)"/>.
    /// </summary>
    public void Show(OutputRole role, AnimatedScene? scene, SceneTransition? transition)
    {
        if (role == OutputRole.Audience) audience = scene;
        else stage = scene;
        foreach (var feed in feeds.Values.Where(f => f.IsRunning && RoleOf(f.Target) == role).ToList())
        {
            var shaped = SceneFor(feed.Target);
            if (transition is { } change) feed.View?.Present(shaped, change);
            else if (shaped is not null) feed.View?.Update(shaped);
        }
    }

    public void Show(OutputRole role, RenderScene? scene, SceneTransition? transition) =>
        Show(role, scene is null ? null : AnimatedScene.From(scene), transition);

    /// <summary>Which job's scenes a target follows; null for a test picture, which only changes with the setup.</summary>
    private OutputRole? RoleOf(string target)
    {
        if (target == CaptureSources.ProgramTarget) return OutputRole.Audience;
        return outputs.Config.Feed(target).Kind switch
        {
            OutputFeedKind.Confidence => OutputRole.Stage,
            OutputFeedKind.Program => OutputRole.Audience,
            _ => null,
        };
    }

    /// <summary>What a target shows now, as <see cref="OutputManager"/> works it out for its outputs.</summary>
    private AnimatedScene? SceneFor(string target)
    {
        if (target == CaptureSources.ProgramTarget) return audience;
        // A screen gets exactly what OutputManager gives that screen's outputs: its look, its layers, the grid.
        if (SwiftUuid.Parse(target) is { } id && outputs.Picture(id) is { } shown) return shown;
        var feed = outputs.Config.Feed(target);
        return feed.Kind switch
        {
            OutputFeedKind.AlignmentGrid => AnimatedScene.From(OutputConfiguration.AlignmentGrid(feed.CanvasSize!.Value)),
            OutputFeedKind.SyncTest => AnimatedScene.From(OutputConfiguration.SyncTestScene(feed.CanvasSize!.Value)),
            OutputFeedKind.Confidence => stage,
            _ => audience is null ? null : outputs.Config.Program(feed, audience),
        };
    }

    /// <summary>Stage screens share the "stage" video players, everything else "live" (as OutputManager does).</summary>
    private string ScopeFor(string target) =>
        target != CaptureSources.ProgramTarget && SwiftUuid.Parse(target) is { } id && outputs.Config.Role(id) == ScreenRole.Confidence ? "stage" : "live";

    // The grid went on, a screen changed job or size...: every feed shows its screen afresh.
    private void OnConfigChanged()
    {
        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(OnConfigChanged);
            return;
        }
        foreach (var feed in feeds.Values.Where(f => f.IsRunning)) feed.View?.Present(SceneFor(feed.Target), SceneTransition.Cut);
    }

    private void Forget(Feed feed)
    {
        foreach (var pair in feeds.Where(p => p.Value == feed).ToList()) feeds.Remove(pair.Key);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        outputs.Config.StateChanged -= OnConfigChanged;
        outputs.Config.ConfigurationChanged -= OnConfigChanged;
        foreach (var feed in feeds.Values.ToList()) feed.Stop();
        feeds.Clear();
    }

    /// <summary>One screen's pictures at one size and rate, running while anyone listens.</summary>
    private sealed class Feed(ScreenFeeds owner, string target, int width, int height, int frameRate) : IProgramSource
    {
        private readonly Lock gate = new();
        private readonly List<IProgramSink> sinks = [];
        private HiddenCapture? capture;

        public string Target { get; } = target;

        public SceneView? View { get; private set; }

        public bool IsRunning => capture is not null;

        public string? Problem => capture?.Problem;

        public void Add(IProgramSink sink)
        {
            if (!owner.dispatcher.CheckAccess())
            {
                owner.dispatcher.Invoke(() => Add(sink));
                return;
            }
            lock (gate)
            {
                if (sinks.Contains(sink)) return;
                sinks.Add(sink);
                if (sinks.Count > 1) return;
            }
            View = new SceneView { Media = owner.media, PlaysVideo = true, PlaybackScope = owner.ScopeFor(Target) };
            capture = new HiddenCapture(View, width, height, frameRate, Sinks, $"MxU Slides screen feed {Target}");
            View.Present(owner.SceneFor(Target), SceneTransition.Cut);
            Log.Info($"Screen feed started: {Target} at {width}x{height}@{frameRate}" + (capture.Problem is { } problem ? $" ({problem})" : ""));
        }

        public void Remove(IProgramSink sink)
        {
            if (!owner.dispatcher.CheckAccess())
            {
                owner.dispatcher.Invoke(() => Remove(sink));
                return;
            }
            lock (gate)
            {
                if (!sinks.Remove(sink) || sinks.Count > 0) return;
            }
            Stop();
            owner.Forget(this);
        }

        private IProgramSink[] Sinks()
        {
            lock (gate) return [.. sinks];
        }

        public void Stop()
        {
            lock (gate) sinks.Clear();
            if (capture is null) return;
            View?.Present((RenderScene?)null, SceneTransition.Cut);
            capture.Dispose();
            capture = null;
            View = null;
            Log.Info($"Screen feed stopped: {Target}");
        }
    }
}
