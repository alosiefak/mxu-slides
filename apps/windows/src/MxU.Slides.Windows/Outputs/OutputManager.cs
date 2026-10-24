using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using MxU.Slides.Core.Media;
using MxU.Slides.Core.Outputs;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.Rendering;
using AnimatedScene = MxU.Slides.Core.Animation.AnimatedScene;

namespace MxU.Slides.Windows.Outputs;

/// <summary>
/// Puts every screen in the setup (<see cref="Config"/>, the Mac's placeholder screens)
/// out to whatever carries it: a window covering a display, an NDI source or a
/// DeckLink card, or a preview window on a one-screen laptop. Each screen shows the
/// audience program or the stage display, reshaped to its size, through its slices'
/// corrections. Follows displays being plugged in or out mid-service and keeps the
/// computer awake while anything is showing. Port of the Mac's OutputManager runtime.
/// </summary>
public sealed class OutputManager
{
    private readonly AppSettings settings;
    private readonly Window main;
    private readonly MediaHub media;
    private readonly Dictionary<string, OutputWindow> windows = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, PreviewWindow> previews = [];
    private readonly Dictionary<string, DeviceFeed> feeds = new(StringComparer.Ordinal);
    private readonly DispatcherTimer displayDebounce;
    private AnimatedScene? audience;
    private AnimatedScene? stage;
    private IReadOnlyDictionary<string, AnimatedScene> looks = new Dictionary<string, AnimatedScene>();
    private bool showing;

    public OutputManager(AppSettings settings, Window main, MediaHub media)
    {
        this.settings = settings;
        this.main = main;
        this.media = media;
        Config = ScreenSetup.Load(settings.Screens ?? FirstRun(settings));
        Config.MaskPathFill = MaskCoverage;
        Config.UpdateDisplays(Snapshots());
        Config.ConfigurationChanged += () =>
        {
            settings.Screens = ScreenSetup.Capture(Config);
            settings.Save();
        };
        Config.StateChanged += () => main.Dispatcher.BeginInvoke(() => Rebuild(force: false));
        displayDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(800), DispatcherPriority.Normal, (_, _) =>
        {
            displayDebounce!.Stop();
            Log.Info("Displays changed; re-placing outputs");
            Config.UpdateDisplays(Snapshots());
            if (showing) Apply();
            DisplaysChanged?.Invoke();
        }, main.Dispatcher);
        displayDebounce.Stop();
        SystemEvents.DisplaySettingsChanged += (_, _) => main.Dispatcher.BeginInvoke(() => displayDebounce.Start());
    }

    /// <summary>The screens, their slices, devices and corrections (saved in Settings).</summary>
    public OutputConfiguration Config { get; }

    public event Action? StatusChanged;
    public event Action? DisplaysChanged;

    /// <summary>Makes NDI and DeckLink feeds for those devices (set by the app once its video I/O is ready).</summary>
    public Func<string, IProgramSink?>? DeviceSinkFactory { get; set; }

    /// <summary>The library's themes, for the Screens window's Look picker (set by the app).</summary>
    public Func<IReadOnlyList<Core.Model.Theme>>? Themes { get; set; }

    /// <summary>
    /// The looks some screen shows changed (a screen's Look, or an output preset): the app should
    /// render the show again so the new look's scene exists. Raised on the UI thread.
    /// </summary>
    public event Action? LooksChanged;

    /// <summary>Upgrades an old settings file: its Audience and Stage display choices become two screens.</summary>
    private static List<PlaceholderScreenConfig> FirstRun(AppSettings s) => ScreenSetup.FirstRun(
        s.AudienceDisplay is { } a ? OutputDevice.Display(a) : null, s.AudienceDisplayName,
        s.StageDisplay is { } st ? OutputDevice.Display(st) : null, s.StageDisplayName);

    private static IEnumerable<DisplaySnapshot> Snapshots() => Displays.All().Select(d =>
        new DisplaySnapshot(OutputDevice.Display(d.DeviceName), d.FriendlyName, new SceneRect(d.Left, d.Top, d.Width, d.Height), IsMain: d.IsPrimary));

    private static ScreenRole RoleOf(OutputRole role) => role == OutputRole.Audience ? ScreenRole.Audience : ScreenRole.Confidence;

    private IEnumerable<PlaceholderScreen> ScreensFor(OutputRole role) => Config.Screens.Where(s => Config.Role(s.Id) == RoleOf(role));

    // MARK: Scenes

    /// <summary>
    /// The audience program; every screen with the audience job shows it, reshaped. A screen with a
    /// look (<see cref="OutputConfiguration.LookKey"/>) shows that look's scene from
    /// <paramref name="lookScenes"/> instead, when it is there; if it isn't, the program as designed.
    /// </summary>
    public void Present(OutputRole role, AnimatedScene? scene, SceneTransition transition, IReadOnlyDictionary<string, AnimatedScene>? lookScenes = null)
    {
        if (role == OutputRole.Audience)
        {
            audience = scene;
            looks = lookScenes ?? new Dictionary<string, AnimatedScene>();
        }
        else
        {
            stage = scene;
        }
        foreach (var target in Targets(role)) target.Present(scene is null ? null : SceneFor(target.ScreenId), transition);
    }

    /// <summary>A still scene (the stage display) to every screen with that job.</summary>
    public void Present(OutputRole role, RenderScene? scene, SceneTransition transition) =>
        Present(role, scene is null ? null : AnimatedScene.From(scene), transition);

    public void Update(OutputRole role, RenderScene scene) => Update(role, AnimatedScene.From(scene));

    public void Update(OutputRole role, AnimatedScene scene, IReadOnlyDictionary<string, AnimatedScene>? lookScenes = null)
    {
        if (role == OutputRole.Audience)
        {
            audience = scene;
            if (lookScenes is not null) looks = lookScenes;
        }
        else
        {
            stage = scene;
        }
        foreach (var target in Targets(role))
            if (SceneFor(target.ScreenId) is { } shaped) target.View.Update(shaped);
    }

    private readonly record struct Target(Guid ScreenId, SceneView View)
    {
        public void Present(AnimatedScene? scene, SceneTransition transition) => View.Present(scene, transition);
    }

    private IEnumerable<Target> Targets(OutputRole role)
    {
        var screens = ScreensFor(role).Select(s => s.Id).ToHashSet();
        foreach (var window in windows.Values)
            foreach (var slice in window.Slices.Where(s => screens.Contains(s.ScreenId)))
                yield return new Target(slice.ScreenId, slice.View);
        foreach (var feed in feeds.Values)
            foreach (var slice in feed.Slices.Where(s => screens.Contains(s.ScreenId)))
                yield return new Target(slice.ScreenId, slice.View);
        foreach (var preview in previews.Values.Where(p => screens.Contains(p.ScreenId)))
            yield return new Target(preview.ScreenId, preview.View);
    }

    /// <summary>
    /// What one screen shows now: the grid or sync test while lining up, else the stage, or the
    /// program (through the screen's look when it has one) shaped for it.
    /// </summary>
    private AnimatedScene? SceneFor(Guid screenId)
    {
        var feed = Config.Feed(SwiftUuid.Format(screenId));
        return feed.Kind switch
        {
            OutputFeedKind.AlignmentGrid => AnimatedScene.From(OutputConfiguration.AlignmentGrid(feed.CanvasSize!.Value)),
            OutputFeedKind.SyncTest => AnimatedScene.From(OutputConfiguration.SyncTestScene(feed.CanvasSize!.Value)),
            OutputFeedKind.Confidence => stage,
            _ => (feed.SlideThemeKey is { } look && looks.TryGetValue(look, out var variant) ? variant : audience) is { } program
                ? Config.Program(feed, program)
                : null,
        };
    }

    /// <summary>The scene a screen is showing now (for the self-test and diagnostics); null when it shows nothing.</summary>
    public AnimatedScene? ShownScene(Guid screenId) => SceneFor(screenId);

    /// <summary>What a screen is showing right now, shaped to its size (the mask editor draws over it); null when nothing is up.</summary>
    public AnimatedScene? Picture(Guid screenId) => SceneFor(screenId);

    // MARK: Windows and feeds

    /// <summary>Opens, moves or closes outputs to match the setup and the connected displays.</summary>
    public void Apply()
    {
        showing = true;
        var connected = Displays.All().ToDictionary(d => OutputDevice.Display(d.DeviceName), StringComparer.Ordinal);
        var carries = Config.Carries();
        foreach (var gone in windows.Keys.Where(k => !carries.ContainsKey(k)).ToList())
        {
            windows[gone].AllowClose = true;
            windows[gone].Close();
            windows.Remove(gone);
        }
        foreach (var gone in feeds.Keys.Where(k => !carries.ContainsKey(k)).ToList())
        {
            feeds[gone].Dispose();
            feeds.Remove(gone);
        }
        foreach (var (device, carried) in carries)
        {
            switch (OutputDevice.Kind(device))
            {
                case OutputDeviceKind.Display:
                    if (!connected.TryGetValue(device, out var display))
                    {
                        if (windows.TryGetValue(device, out var orphan) && orphan.IsVisible)
                        {
                            Log.Warn($"Display {Config.DeviceName(carried[0].SliceId) ?? device} is not connected; its output is hidden");
                            orphan.Hide();
                        }
                        continue;
                    }
                    if (!windows.TryGetValue(device, out var window))
                        windows[device] = window = new OutputWindow(device, display.FriendlyName, media);
                    window.SetSlices(SlicesFor(carried));
                    window.PlaceOn(display);
                    break;
                default:
                    if (!feeds.TryGetValue(device, out var feed))
                    {
                        if (DeviceSinkFactory?.Invoke(device) is not { } sink) continue;
                        feeds[device] = feed = new DeviceFeed(device, sink, media);
                    }
                    var screen = Config.SliceInfo(carried[0].SliceId) is { } first ? Config.Screen(first.ScreenId) : null;
                    feed.SetSlices(SlicesFor(carried), screen?.Width ?? 1920, screen?.Height ?? 1080);
                    break;
            }
        }
        Rebuild(force: true);
        UpdateKeepAwake();
        StatusChanged?.Invoke();
    }

    private List<(Guid SliceId, Guid ScreenId, SceneRect SourceRect, SceneRect Placement, string PlaybackScope)> SlicesFor(IReadOnlyList<OutputCarry> carried) =>
    [
        .. carried.Select(c => (c.SliceId, c.ScreenId, Config.SliceInfo(c.SliceId)?.SourceRect ?? OutputPass.FullSourceRect,
            Config.Placement(c.SliceId)?.UnitRect ?? OutputPass.FullSourceRect, ScopeFor(c.ScreenId))),
    ];

    /// <summary>Audience screens share the "live" players, so a video is in step on every one; stage screens share "stage".</summary>
    private string ScopeFor(Guid screenId) => Config.Role(screenId) == ScreenRole.Confidence ? "stage" : "live";

    private readonly Dictionary<Guid, (OutputFeedKind Kind, int Width, int Height, OutputScaleMode Mode, bool Spanned, string? Look)> shown = [];

    /// <summary>
    /// After any change to the setup: re-applies every slice's corrections (cheap, so a slider can
    /// drag smoothly) and re-sends a screen its picture only when what it shows has changed
    /// (its job, test grid, size, fit or look). A new look needs a scene the app builds, so the app is
    /// asked (<see cref="LooksChanged"/>) to render again.
    /// </summary>
    private void Rebuild(bool force)
    {
        foreach (var window in windows.Values)
        {
            foreach (var slice in window.Slices)
            {
                var (w, h) = window.PixelSize(slice);
                ApplyCorrections(slice, w, h);
            }
        }
        foreach (var feed in feeds.Values)
            foreach (var slice in feed.Slices)
                ApplyCorrections(slice, feed.Width, feed.Height);
        var changed = new HashSet<Guid>();
        var looksChanged = false;
        foreach (var screen in Config.Screens)
        {
            var feed = Config.Feed(SwiftUuid.Format(screen.Id));
            var look = feed.Kind == OutputFeedKind.Program ? feed.SlideThemeKey : null;
            var key = (feed.Kind, screen.Width, screen.Height, Config.ScaleMode(screen.Id), Config.IsSpanned(screen.Id), look);
            var known = shown.TryGetValue(screen.Id, out var was);
            if (force || !known || was != key) changed.Add(screen.Id);
            if (known && was.Look != look || !known && look is not null) looksChanged = true;
            shown[screen.Id] = key;
        }
        foreach (var role in Enum.GetValues<OutputRole>())
            foreach (var target in Targets(role).Where(t => changed.Contains(t.ScreenId)))
                target.Present(SceneFor(target.ScreenId), SceneTransition.Cut);
        if (looksChanged) LooksChanged?.Invoke();
    }

    private void ApplyCorrections(SliceView slice, int width, int height)
    {
        var canvas = Config.Screen(slice.ScreenId)?.Size ?? SceneSize.Hd;
        slice.SetCorrections(Config.EffectiveAdjustments(slice.SliceId), Config.MaskCoverage(slice.SliceId), canvas, width, height);
    }

    /// <summary>Fills a mask path (unit-square SVG data) into a white-on-black coverage map of the canvas's size.</summary>
    private static byte[]? MaskCoverage(string pathData, int width, int height)
    {
        try
        {
            var geometry = Geometry.Parse("F1 " + pathData).Clone();
            geometry.Transform = new ScaleTransform(width, height);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));
                dc.DrawGeometry(Brushes.White, null, geometry);
            }
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var bgra = new byte[width * height * 4];
            bitmap.CopyPixels(bgra, width * 4, 0);
            var coverage = new byte[width * height];
            for (var i = 0; i < coverage.Length; i++) coverage[i] = bgra[i * 4 + 2];
            return coverage;
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't draw an output mask: {ex.Message}");
            return null;
        }
    }

    // MARK: Status and controls (by job)

    /// <summary>A window or preview of that job's screens, for the toolbar and the self-test.</summary>
    public Window? this[OutputRole role]
    {
        get
        {
            var screens = ScreensFor(role).Select(s => s.Id).ToHashSet();
            return previews.Values.FirstOrDefault(p => screens.Contains(p.ScreenId) && p.IsVisible)
                ?? (Window?)windows.Values.FirstOrDefault(w => w.IsVisible && w.Slices.Any(s => screens.Contains(s.ScreenId)));
        }
    }

    public bool IsShowing(OutputRole role) => IsOnDisplay(role) || HasFeed(role)
        || previews.Values.Any(p => p.IsVisible && ScreensFor(role).Any(s => s.Id == p.ScreenId));

    public bool IsOnDisplay(OutputRole role)
    {
        var screens = ScreensFor(role).Select(s => s.Id).ToHashSet();
        return windows.Values.Any(w => w.IsVisible && w.Slices.Any(s => screens.Contains(s.ScreenId)));
    }

    private bool HasFeed(OutputRole role)
    {
        var screens = ScreensFor(role).Select(s => s.Id).ToHashSet();
        return feeds.Values.Any(f => f.Slices.Any(s => screens.Contains(s.ScreenId)));
    }

    /// <summary>"EPSON PJ", "NDI: Stream feed", "Preview window", "2 outputs" or "Off".</summary>
    public string StatusText(OutputRole role)
    {
        var screens = ScreensFor(role).Select(s => s.Id).ToHashSet();
        var names = windows.Values.Where(w => w.IsVisible && w.Slices.Any(s => screens.Contains(s.ScreenId)))
            .Select(w => w.Display?.FriendlyName ?? "On")
            .Concat(feeds.Values.Where(f => f.Slices.Any(s => screens.Contains(s.ScreenId))).Select(f => f.Label))
            .ToList();
        if (names.Count > 1) return $"{names.Count} outputs";
        if (names.Count == 1) return names[0];
        return previews.Values.Any(p => p.IsVisible && screens.Contains(p.ScreenId)) ? "Preview window" : "Off";
    }

    /// <summary>Shows the first screen with that job as an ordinary window, for a laptop with one display.</summary>
    public void ShowPreviewWindow(OutputRole role)
    {
        if (ScreensFor(role).FirstOrDefault() is not { } screen) return;
        if (!previews.TryGetValue(screen.Id, out var preview))
        {
            previews[screen.Id] = preview = new PreviewWindow(screen.Id, screen.Name, media, ScopeFor(screen.Id), main, previews.Count);
            preview.IsVisibleChanged += (_, _) => StatusChanged?.Invoke();
        }
        preview.Show();
        preview.Activate();
        preview.View.Present(SceneFor(screen.Id), SceneTransition.Cut);
        StatusChanged?.Invoke();
    }

    /// <summary>Takes that job's outputs off the displays and closes its preview windows.</summary>
    public void Hide(OutputRole role)
    {
        var screens = ScreensFor(role).Select(s => s.Id).ToHashSet();
        foreach (var window in windows.Values.Where(w => w.Slices.Any(s => screens.Contains(s.ScreenId)))) window.Hide();
        foreach (var preview in previews.Values.Where(p => screens.Contains(p.ScreenId))) preview.Hide();
        UpdateKeepAwake();
        StatusChanged?.Invoke();
    }

    /// <summary>The toolbar button: off if showing; else onto its display if one is connected; else a preview window.</summary>
    public void Toggle(OutputRole role)
    {
        if (IsShowing(role))
        {
            Hide(role);
            return;
        }
        var connected = Displays.All().Select(d => OutputDevice.Display(d.DeviceName)).ToHashSet(StringComparer.Ordinal);
        var screens = ScreensFor(role).Select(s => s.Id).ToHashSet();
        var hasDisplay = Config.Carries().Any(pair => connected.Contains(pair.Key) && pair.Value.Any(c => screens.Contains(c.ScreenId)));
        if (hasDisplay) Apply();
        else ShowPreviewWindow(role);
    }

    /// <summary>Puts the first screen with that job on a display (or nowhere), keeping its other slices as they are.</summary>
    public void AssignDisplay(OutputRole role, string? deviceName, string? displayName)
    {
        if (ScreensFor(role).FirstOrDefault() is not { } screen) return;
        Config.AssignDevice(screen.Id, deviceName is null ? null : OutputDevice.Display(deviceName), displayName);
    }

    /// <summary>The display the first screen with that job is on (GDI device name), or null.</summary>
    public string? AssignedDisplay(OutputRole role) =>
        ScreensFor(role).FirstOrDefault() is { } screen && Config.Device(screen.Id) is { } device && OutputDevice.Kind(device) == OutputDeviceKind.Display
            ? OutputDevice.Value(device)
            : null;

    public void CloseAll()
    {
        foreach (var window in windows.Values)
        {
            window.AllowClose = true;
            window.Close();
        }
        foreach (var preview in previews.Values)
        {
            preview.AllowClose = true;
            preview.Close();
        }
        foreach (var feed in feeds.Values) feed.Dispose();
        feeds.Clear();
        KeepAwake.Set(false);
    }

    private void UpdateKeepAwake() =>
        KeepAwake.Set(settings.PreventSleep && (windows.Values.Any(w => w.IsVisible) || feeds.Count > 0));

    /// <summary>Flashes each display's number and name on it for a few seconds.</summary>
    public static void Identify()
    {
        var shown = new List<Window>();
        var number = 1;
        foreach (var display in Displays.All())
        {
            var label = new TextBlock
            {
                Text = $"{number}\n{display.FriendlyName}\n{display.Width}×{display.Height}",
                Foreground = Brushes.White,
                FontSize = 48,
                FontWeight = FontWeights.Bold,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(40),
            };
            var window = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                Topmost = true,
                ShowActivated = false,
                ShowInTaskbar = false,
                SizeToContent = SizeToContent.WidthAndHeight,
                Background = new SolidColorBrush(Color.FromArgb(230, 59, 130, 246)),
                Content = label,
            };
            window.Show();
            var source = PresentationSource.FromVisual(window);
            var scale = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1;
            window.Left = (display.Left + display.Width / 2.0) * scale - window.ActualWidth / 2;
            window.Top = (display.Top + display.Height / 2.0) * scale - window.ActualHeight / 2;
            shown.Add(window);
            number++;
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            foreach (var w in shown) w.Close();
        };
        timer.Start();
    }
}
