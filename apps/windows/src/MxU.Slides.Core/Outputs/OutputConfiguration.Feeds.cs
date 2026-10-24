using System.Collections.Immutable;
using MxU.Slides.Core.Animation;
using MxU.Slides.Core.Scene;

namespace MxU.Slides.Core.Outputs;

/// <summary>What an output shows right now, in the order the Mac checks.</summary>
public enum OutputFeedKind
{
    /// <summary>The rigging grid, while lining up projectors (beats everything).</summary>
    AlignmentGrid,

    /// <summary>The metronome picture for checking video against sound.</summary>
    SyncTest,

    /// <summary>The stage display layout (a confidence screen).</summary>
    Confidence,

    /// <summary>The program scene, reshaped and layer-routed for this output.</summary>
    Program,
}

/// <summary>
/// An output's feed, worked out from its target id (a screen or slice id as
/// text, or a display id for a direct output). <see cref="RoutingKey"/> is the
/// key output presets route by: the owning screen's id for a slice.
/// </summary>
public sealed record OutputFeed(OutputFeedKind Kind, Guid? ScreenId, string RoutingKey, SceneSize? CanvasSize, string? SlideThemeKey);

// What each output shows: the program scene shaped for its screen, the stage
// display, or a test picture. Port of the scene-provider half of
// OutputManager.swift (provider(for:), the alignment grid and the sync test).
public sealed partial class OutputConfiguration
{
    /// <summary>The media id the sync-test picture plays; the app maps it to its metronome video.</summary>
    public const string SyncTestMediaId = "test-pattern.sync";

    private ImmutableHashSet<Guid> testPatternScreens = [];
    private ImmutableHashSet<Guid> syncTestScreens = [];
    private ImmutableDictionary<string, IReadOnlySet<string>> layerRouting = ImmutableDictionary<string, IReadOnlySet<string>>.Empty;
    private ImmutableDictionary<string, string> slideThemeRouting = ImmutableDictionary<string, string>.Empty;

    /// <summary>
    /// Raised with true when the first screen starts a sync test and false when
    /// the last one stops: one player feeds every screen showing it.
    /// </summary>
    public event Action<bool>? SyncTestPlaybackChanged;

    /// <summary>The program scene (what the audience sees). Read once per frame per output.</summary>
    public Func<RenderScene>? SceneProvider { get; set; }

    /// <summary>A confidence screen's stage layout, by screen id.</summary>
    public Func<Guid, RenderScene>? ConfidenceSceneProvider { get; set; }

    /// <summary>The program re-formatted through a slide-theme look (by look key), or null when it isn't ready.</summary>
    public Func<string, RenderScene?>? VariantSceneProvider { get; set; }

    // ── Test pictures (runtime, never saved) ────────────────────────────────

    public IReadOnlySet<Guid> TestPatternScreens => testPatternScreens;

    /// <summary>Shows the alignment grid on every output of a screen while rigging.</summary>
    public void SetTestPattern(Guid screenId, bool on)
    {
        testPatternScreens = on ? testPatternScreens.Add(screenId) : testPatternScreens.Remove(screenId);
        Notify();
    }

    public IReadOnlySet<Guid> SyncTestScreens => syncTestScreens;

    /// <summary>Plays the metronome sync pattern on every output of a screen.</summary>
    public void SetSyncTest(Guid screenId, bool on)
    {
        var wasPlaying = !syncTestScreens.IsEmpty;
        syncTestScreens = on ? syncTestScreens.Add(screenId) : syncTestScreens.Remove(screenId);
        var playing = !syncTestScreens.IsEmpty;
        if (playing != wasPlaying) SyncTestPlaybackChanged?.Invoke(playing);
        Notify();
    }

    // ── Routing (set by the active output preset; runtime) ──────────────────

    /// <summary>For each routed target, the layers it composites (raw layer names). Unrouted targets show every layer.</summary>
    public IReadOnlyDictionary<string, IReadOnlySet<string>> LayerRouting => layerRouting;

    public void SetLayerRouting(IReadOnlyDictionary<string, IReadOnlySet<string>> routing)
    {
        layerRouting = routing.ToImmutableDictionary(pair => pair.Key, pair => (IReadOnlySet<string>)pair.Value.ToImmutableHashSet(), StringComparer.Ordinal);
        Notify();
    }

    /// <summary>For each target with a slide theme, the look key whose variant scene it shows.</summary>
    public IReadOnlyDictionary<string, string> SlideThemeRouting => slideThemeRouting;

    public void SetSlideThemeRouting(IReadOnlyDictionary<string, string> routing)
    {
        slideThemeRouting = routing.ToImmutableDictionary(StringComparer.Ordinal);
        Notify();
    }

    // ── Feeds ───────────────────────────────────────────────────────────────

    /// <summary>
    /// What a target shows now. A slice id resolves to its screen, so every
    /// output of a screen shows the same picture; an id that isn't a screen or
    /// slice is used as it is (a direct display, or a confidence role set on a
    /// bare id).
    /// </summary>
    public OutputFeed Feed(string targetId)
    {
        Guid? screenId = SwiftUuid.Parse(targetId) is { } id ? SliceInfo(id)?.ScreenId ?? id : null;
        var routingKey = screenId is { } key ? SwiftUuid.Format(key) : targetId;
        var canvas = screenId is { } sid ? Screen(sid)?.Size : null;
        var kind = OutputFeedKind.Program;
        if (screenId is { } s)
        {
            if (canvas is not null && testPatternScreens.Contains(s)) kind = OutputFeedKind.AlignmentGrid;
            else if (canvas is not null && syncTestScreens.Contains(s)) kind = OutputFeedKind.SyncTest;
            else if (Role(s) == ScreenRole.Confidence) kind = OutputFeedKind.Confidence;
        }
        return new OutputFeed(kind, screenId, routingKey, canvas, slideThemeRouting.GetValueOrDefault(routingKey));
    }

    /// <summary>
    /// The program scene as this output should draw it: reshaped to its
    /// screen's aspect (unless the screen is spanned by slices), then with the
    /// layers its preset doesn't route removed.
    /// </summary>
    public RenderScene Program(OutputFeed feed, RenderScene scene)
    {
        if (feed.ScreenId is { } id && feed.CanvasSize is { } size && !IsSpanned(id))
            scene = scene.Scaled(size, ScaleMode(id));
        if (!layerRouting.TryGetValue(feed.RoutingKey, out var enabled)) return scene;
        var routed = new RenderScene(scene.CanvasSize, scene.Background);
        foreach (var layer in scene.Layers.Where(layer => enabled.Contains(layer.Kind.Raw())))
            routed[layer.Kind].Items.AddRange(layer.Items);
        return routed;
    }

    /// <summary>The animated program as this output should draw it: as <see cref="Program(OutputFeed, RenderScene)"/>, hiding unrouted layers.</summary>
    public AnimatedScene Program(OutputFeed feed, AnimatedScene scene)
    {
        if (feed.ScreenId is { } id && feed.CanvasSize is { } size && !IsSpanned(id))
            scene = scene.Scaled(size, ScaleMode(id));
        if (!layerRouting.TryGetValue(feed.RoutingKey, out var enabled)) return scene;
        var routed = scene.Copy();
        foreach (var layer in routed.Layers)
            if (!enabled.Contains(layer.Kind.Raw())) layer.IsHidden = true;
        return routed;
    }

    /// <summary>
    /// A function that returns a target's frame each time it's called, honouring
    /// every change made since (the Mac's previewProvider). Grid, then sync
    /// test, then confidence layout, then the program (through the target's
    /// slide-theme variant when it has one and it is ready).
    /// </summary>
    public Func<RenderScene> PreviewProvider(string targetId) => () =>
    {
        var feed = Feed(targetId);
        switch (feed.Kind)
        {
            case OutputFeedKind.AlignmentGrid:
                return AlignmentGrid(feed.CanvasSize!.Value);
            case OutputFeedKind.SyncTest:
                return SyncTestScene(feed.CanvasSize!.Value);
            case OutputFeedKind.Confidence when ConfidenceSceneProvider is { } confidence:
                return confidence(feed.ScreenId!.Value);
        }
        var scene = feed.SlideThemeKey is { } look && VariantSceneProvider?.Invoke(look) is { } variant
            ? variant
            : SceneProvider?.Invoke() ?? new RenderScene(SceneSize.Hd);
        return Program(feed, scene);
    };

    /// <summary>The metronome picture: the sync-test video fitted to the canvas over near-black.</summary>
    public static RenderScene SyncTestScene(SceneSize canvasSize)
    {
        var scene = new RenderScene(canvasSize, new SceneColor(0.06, 0.06, 0.06));
        scene.AddItem(new RenderItem
        {
            Id = SyncTestMediaId,
            Frame = new SceneRect(canvasSize),
            Content = new ItemContent.Media(SyncTestMediaId, SceneMediaScaleMode.Fit),
        }, LayerKind.Videos);
        return scene;
    }

    /// <summary>
    /// The rigging grid: lines every tenth, a border, both diagonals, a circle
    /// (round only when nothing stretches the picture) and a green centre cross.
    /// </summary>
    public static RenderScene AlignmentGrid(SceneSize canvasSize)
    {
        var scene = new RenderScene(canvasSize, new SceneColor(0.12, 0.12, 0.12));
        var line = new SceneColor(0.85, 0.85, 0.85);
        var accent = new SceneColor(0.2, 0.85, 0.4);
        var index = 0;
        void Add(SceneRect frame, SceneColor color)
        {
            scene.AddItem(new RenderItem
            {
                Id = $"grid-{index}",
                Frame = frame,
                Content = new ItemContent.Shape(new ShapeStyle(SceneShapeKind.Rect, new SceneFill.Solid(color))),
            }, LayerKind.Slide);
            index++;
        }
        var w = canvasSize.Width;
        var h = canvasSize.Height;
        var thin = Math.Max(1, Math.Round(w / 960, MidpointRounding.AwayFromZero));
        for (var step = 1; step < 10; step++)
        {
            var fraction = step / 10.0;
            Add(new SceneRect(w * fraction - thin / 2, 0, thin, h), line);
            Add(new SceneRect(0, h * fraction - thin / 2, w, thin), line);
        }

        Add(new SceneRect(0, 0, w, thin * 2), line);
        Add(new SceneRect(0, h - thin * 2, w, thin * 2), line);
        Add(new SceneRect(0, 0, thin * 2, h), line);
        Add(new SceneRect(w - thin * 2, 0, thin * 2, h), line);

        var diagonalLength = Math.Sqrt(w * w + h * h);
        var diagonalAngle = Math.Atan2(h, w) * 180 / Math.PI;
        foreach (var angle in new[] { diagonalAngle, -diagonalAngle })
        {
            scene.AddItem(new RenderItem
            {
                Id = $"grid-diag-{index}",
                Frame = new SceneRect((w - diagonalLength) / 2, h / 2 - thin / 2, diagonalLength, thin),
                Content = new ItemContent.Shape(new ShapeStyle(SceneShapeKind.Rect, new SceneFill.Solid(line))),
                RotationDegrees = angle,
            }, LayerKind.Slide);
            index++;
        }

        var diameter = h * 0.6;
        scene.AddItem(new RenderItem
        {
            Id = "grid-circle",
            Frame = new SceneRect(w / 2 - diameter / 2, h / 2 - diameter / 2, diameter, diameter),
            Content = new ItemContent.Shape(new ShapeStyle(new SceneShapeKind.Ellipse(), SceneFill.None, new SceneStroke(line, thin * 2))),
        }, LayerKind.Slide);

        Add(new SceneRect(w / 2 - thin * 2, 0, thin * 4, h), accent);
        Add(new SceneRect(0, h / 2 - thin * 2, w, thin * 4), accent);
        return scene;
    }
}
