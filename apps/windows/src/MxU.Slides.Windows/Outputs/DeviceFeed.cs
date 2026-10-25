using System.Windows.Controls;
using System.Windows.Media;
using MxU.Slides.Core.Media;
using MxU.Slides.Core.Outputs;
using MxU.Slides.Core.Scene;
using MxU.Slides.Windows.Rendering;

namespace MxU.Slides.Windows.Outputs;

/// <summary>
/// A screen sent somewhere other than a display — an NDI source or a DeckLink card. Its
/// slices are laid out on a hidden surface the size of the screen, exactly as an
/// <see cref="OutputWindow"/> lays them out on a display, and <see cref="HiddenCapture"/>
/// sends that surface to the device's sink at the screen's frame rate.
/// </summary>
public sealed class DeviceFeed : IDisposable
{
    private readonly Canvas surface = new() { Background = Brushes.Black, ClipToBounds = true };
    private readonly MediaHub media;
    private readonly IProgramSink sink;
    private readonly List<(SliceView View, SceneRect Placement)> slices = [];
    private HiddenCapture? capture;

    public DeviceFeed(string deviceId, IProgramSink sink, MediaHub media)
    {
        DeviceId = deviceId;
        this.sink = sink;
        this.media = media;
        Label = OutputDevice.Kind(deviceId) == OutputDeviceKind.Ndi ? $"NDI: {OutputDevice.Value(deviceId)}" : "DeckLink";
    }

    public string DeviceId { get; }

    /// <summary>For the toolbar: "NDI: Stage feed" or "DeckLink".</summary>
    public string Label { get; }

    public int Width { get; private set; } = 1920;
    public int Height { get; private set; } = 1080;
    public IReadOnlyList<SliceView> Slices => [.. slices.Select(s => s.View)];

    /// <summary>Shows exactly these slices on a <paramref name="width"/> × <paramref name="height"/> surface (restarting the capture if the size changed).</summary>
    public void SetSlices(IReadOnlyList<(Guid SliceId, Guid ScreenId, SceneRect SourceRect, SceneRect Placement, string PlaybackScope)> wanted, int width, int height)
    {
        foreach (var gone in slices.Where(s => wanted.All(w => w.SliceId != s.View.SliceId)).ToList())
        {
            surface.Children.Remove(gone.View);
            gone.View.View.Present((RenderScene?)null, SceneTransition.Cut);
            slices.Remove(gone);
        }
        foreach (var w in wanted)
        {
            var index = slices.FindIndex(s => s.View.SliceId == w.SliceId);
            if (index < 0)
            {
                var view = new SliceView(w.SliceId, w.ScreenId, media, w.PlaybackScope);
                view.View.CopiedInSoftware = () => capture?.UsesSoftwareCopy == true;
                surface.Children.Add(view);
                slices.Add((view, w.Placement));
                index = slices.Count - 1;
            }
            slices[index] = (slices[index].View, w.Placement);
            slices[index].View.SourceRect = w.SourceRect;
        }
        foreach (var (view, placement) in slices)
        {
            Canvas.SetLeft(view, placement.X * width);
            Canvas.SetTop(view, placement.Y * height);
            view.Width = placement.Width * width;
            view.Height = placement.Height * height;
        }
        if (capture is null || width != Width || height != Height)
        {
            Width = width;
            Height = height;
            capture?.Dispose();
            capture = new HiddenCapture(surface, width, height, 30, () => [sink], $"MxU Slides {Label}");
        }
    }

    public void Dispose()
    {
        capture?.Dispose();
        capture = null;
        foreach (var (view, _) in slices) view.View.Present((RenderScene?)null, SceneTransition.Cut);
        if (sink is IDisposable disposable) disposable.Dispose();
    }
}
