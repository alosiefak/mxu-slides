using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MxU.Slides.Production.Devices;

namespace MxU.Slides.Windows.Views.Production;

/// <summary>Small pieces the production windows share: the status light, device icons, hopping to the UI thread.</summary>
internal static class ProductionUi
{
    /// <summary>The colour of a status light: green online, amber connecting, red a problem, grey off or idle.</summary>
    public static Brush StateBrush(DeviceState state) => state switch
    {
        DeviceState.Online => EditorKit.Res<Brush>("Good"),
        DeviceState.Connecting => EditorKit.Res<Brush>("Warn"),
        DeviceState.Problem => EditorKit.Res<Brush>("Live"),
        _ => EditorKit.Res<Brush>("FaintText"),
    };

    /// <summary>A round status light, with the status as its tooltip.</summary>
    public static Ellipse Dot(DeviceStatus status, double size = 9)
    {
        var dot = new Ellipse { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        Paint(dot, status);
        return dot;
    }

    /// <summary>Gives a light the colour and tooltip of a status (so a row can be repainted, not made again).</summary>
    public static void Paint(Ellipse dot, DeviceStatus status)
    {
        dot.Fill = StateBrush(status.State);
        dot.ToolTip = $"{StateWord(status.State)}: {status.Detail}";
    }

    public static string StateWord(DeviceState state) => state switch
    {
        DeviceState.Online => "Connected",
        DeviceState.Connecting => "Connecting",
        DeviceState.Problem => "Not answering",
        DeviceState.Idle => "Ready",
        _ => "Off",
    };

    /// <summary>The icon (one of EditorKit's glyph names) for a kind of device; null is the lights.</summary>
    public static string GlyphName(DeviceKind? kind) => kind switch
    {
        DeviceKind.Atem => "screens",
        DeviceKind.Obs or DeviceKind.Vmix => "broadcast",
        _ => "light",
    };

    /// <summary>Runs <paramref name="action"/> on the UI thread (device threads raise events; WPF needs its own thread).</summary>
    public static void OnUi(DispatcherObject owner, Action action)
    {
        if (owner.Dispatcher.HasShutdownStarted) return;
        owner.Dispatcher.BeginInvoke(action);
    }

    /// <summary>A muted line of text that can be changed later.</summary>
    public static TextBlock Line(string text, double size = 11) => new()
    {
        Text = text, FontSize = size, Foreground = EditorKit.Res<Brush>("MutedText"), TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center,
    };
}
