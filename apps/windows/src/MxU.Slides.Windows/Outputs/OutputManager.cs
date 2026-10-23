using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Infrastructure;
using MxU.Slides.Windows.Rendering;

namespace MxU.Slides.Windows.Outputs;

/// <summary>
/// Puts the audience and stage outputs on the displays chosen in Screens,
/// follows displays being plugged in or out mid-service, and keeps the
/// computer awake while anything is showing.
/// </summary>
public sealed class OutputManager
{
    private readonly AppSettings settings;
    private readonly Window main;
    private readonly Dictionary<OutputRole, OutputWindow> windows = [];
    private readonly DispatcherTimer displayDebounce;

    public OutputManager(AppSettings settings, Window main, MediaHub media)
    {
        this.settings = settings;
        this.main = main;
        foreach (var role in Enum.GetValues<OutputRole>())
        {
            var window = new OutputWindow(role, media) { Owner = null };
            window.IsVisibleChanged += (_, _) => { UpdateKeepAwake(); StatusChanged?.Invoke(); };
            windows[role] = window;
        }
        displayDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(800), DispatcherPriority.Normal, (_, _) =>
        {
            displayDebounce!.Stop();
            Log.Info("Displays changed; re-placing outputs");
            Apply();
            DisplaysChanged?.Invoke();
        }, main.Dispatcher);
        displayDebounce.Stop();
        SystemEvents.DisplaySettingsChanged += (_, _) => main.Dispatcher.BeginInvoke(() => displayDebounce.Start());
    }

    public event Action? StatusChanged;
    public event Action? DisplaysChanged;

    public OutputWindow this[OutputRole role] => windows[role];

    public bool IsShowing(OutputRole role) => windows[role].IsVisible;

    public bool IsOnDisplay(OutputRole role) => windows[role].IsVisible && !windows[role].IsPreviewWindow;

    public string StatusText(OutputRole role)
    {
        var w = windows[role];
        if (!w.IsVisible) return "Off";
        return w.IsPreviewWindow ? "Preview window" : w.Display?.FriendlyName ?? "On";
    }

    /// <summary>Places each output on its assigned display, or hides it when that display is missing.</summary>
    public void Apply()
    {
        var displays = Displays.All();
        var mainHandle = new WindowInteropHelper(main).Handle;
        var mainDisplay = mainHandle == IntPtr.Zero ? null : Displays.ForWindow(mainHandle);
        foreach (var (role, window) in windows)
        {
            var assigned = role == OutputRole.Audience ? settings.AudienceDisplay : settings.StageDisplay;
            var display = displays.FirstOrDefault(d => d.DeviceName == assigned);
            if (display is null)
            {
                if (!window.IsPreviewWindow && window.IsVisible)
                {
                    Log.Warn($"{role} display {assigned ?? "(none)"} is not connected; output hidden");
                    window.Hide();
                }
                continue;
            }
            if (display.DeviceName == mainDisplay?.DeviceName && displays.Count > 1)
                Log.Warn($"{role} output shares a display with the main window");
            window.PlaceOn(display);
        }
        UpdateKeepAwake();
        StatusChanged?.Invoke();
    }

    public void ShowPreviewWindow(OutputRole role) => windows[role].ShowAsPreviewWindow(main);

    public void Hide(OutputRole role) => windows[role].Hide();

    public void Toggle(OutputRole role)
    {
        if (windows[role].IsVisible) Hide(role);
        else if ((role == OutputRole.Audience ? settings.AudienceDisplay : settings.StageDisplay) is { } device
                 && Displays.Find(device) is not null)
            Apply();
        else ShowPreviewWindow(role);
    }

    public void Present(OutputRole role, RenderScene? scene, SceneTransition transition) => windows[role].View.Present(scene, transition);

    public void Present(OutputRole role, Core.Animation.AnimatedScene? scene, SceneTransition transition) => windows[role].View.Present(scene, transition);

    public void Update(OutputRole role, RenderScene scene) => windows[role].View.Update(scene);

    public void Update(OutputRole role, Core.Animation.AnimatedScene scene) => windows[role].View.Update(scene);

    public void CloseAll()
    {
        foreach (var window in windows.Values)
        {
            window.AllowClose = true;
            window.Close();
        }
        KeepAwake.Set(false);
    }

    private void UpdateKeepAwake() =>
        KeepAwake.Set(settings.PreventSleep && windows.Values.Any(w => w.IsVisible && !w.IsPreviewWindow));

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
