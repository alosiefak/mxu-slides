using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MxU.Slides.Core.MotionTemplates;
using MxU.Slides.Core.Scene;
using MxU.Slides.MotionTemplates.Lottie;

namespace MxU.Slides.Windows.Rendering;

/// <summary>
/// Moving previews of motion templates, for the template window and the Overlays bin. Each one is
/// an <see cref="System.Windows.Controls.Image"/> showing a template played by its own
/// <see cref="MotionPlayers"/> (the same drawing as on the program, just smaller), on a thread of
/// its own. A lower third plays through (In, Hold, Out), pauses, and plays again; a template
/// without markers loops. Pictures are copied to the screen on each refresh, only while something plays.
/// </summary>
internal sealed class MotionPreviews : IDisposable
{
    public sealed class Preview
    {
        internal Preview(string id, int width, int height)
        {
            Id = id;
            Width = width;
            Height = height;
        }

        public string Id { get; }

        public int Width { get; }

        public int Height { get; }

        /// <summary>The element to put in the window.</summary>
        public Image Image { get; } = new() { Stretch = Stretch.Uniform };

        /// <summary>New pictures shown so far (the self-test checks they move).</summary>
        public int FramesShown { get; internal set; }

        public bool IsPlaying { get; internal set; }

        internal WriteableBitmap? Bitmap;
        internal int Version;
        internal LottieDocument? Document;
        internal MotionTimeline? Timeline;
        internal double RestartAt = double.NaN;
    }

    private const double Pause = 0.8;

    private readonly MotionPlayers players;
    private readonly List<Preview> previews = [];
    private bool hooked;
    private int next;

    public MotionPreviews(ILottieFonts fonts) => players = new MotionPlayers(fonts);

    public Preview Add(int width, int height)
    {
        var preview = new Preview($"motion::preview:{++next}", width, height);
        previews.Add(preview);
        return preview;
    }

    /// <summary>Plays <paramref name="document"/> in <paramref name="preview"/>: through and again (with markers), or as a loop.</summary>
    public void Play(Preview preview, LottieDocument document, MotionTimeline timeline)
    {
        preview.Document = document;
        preview.Timeline = timeline.Segments.HasMarkers ? timeline with { Playback = MotionPlayback.Once } : timeline with { Playback = MotionPlayback.Loop };
        preview.RestartAt = double.NaN;
        var (w, h) = LottieRenderer.FittedSize(document, preview.Width, preview.Height);
        players.Start(preview.Id, document, preview.Timeline, w, h, ShowState.HostClock());
        preview.Version = 0;
        preview.IsPlaying = true;
        Hook();
    }

    /// <summary>New field values while it plays: shown from the next picture, without starting again.</summary>
    public void Update(Preview preview, LottieDocument document)
    {
        preview.Document = document;
        if (preview.IsPlaying) players.Update(preview.Id, document);
    }

    /// <summary>Stops it and forgets it (its tile is gone).</summary>
    public void Remove(Preview preview)
    {
        Stop(preview);
        previews.Remove(preview);
    }

    /// <summary>Stops it, leaving its last picture showing.</summary>
    public void Stop(Preview preview)
    {
        players.Stop(preview.Id);
        preview.IsPlaying = false;
    }

    private void Hook()
    {
        if (hooked) return;
        CompositionTarget.Rendering += OnFrame;
        hooked = true;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = ShowState.HostClock();
        players.Request(now + 1 / 60.0);
        foreach (var preview in previews.Where(p => p.IsPlaying))
        {
            var motion = players[preview.Id];
            // A play-through has finished: show its last picture for a moment, then start again.
            if (motion?.IsFinished == true && preview.Document is { } document && preview.Timeline is { } timeline)
            {
                if (double.IsNaN(preview.RestartAt)) preview.RestartAt = now + Pause;
                else if (now >= preview.RestartAt)
                {
                    preview.RestartAt = double.NaN;
                    var (w, h) = LottieRenderer.FittedSize(document, preview.Width, preview.Height);
                    players.Start(preview.Id, document, timeline, w, h, now);
                    preview.Version = 0;
                }
            }
            if (players.CopyLatest(preview.Id, ref preview.Version, (frame, changed) =>
            {
                if (preview.Bitmap is null || preview.Bitmap.PixelWidth != frame.Width || preview.Bitmap.PixelHeight != frame.Height)
                {
                    preview.Bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Pbgra32, null);
                    preview.Image.Source = preview.Bitmap;
                    changed = new PixelRect(0, 0, frame.Width, frame.Height);
                }
                if (!changed.IsEmpty)
                    preview.Bitmap.WritePixels(new Int32Rect(changed.X, changed.Y, changed.Width, changed.Height), frame.Pixels, frame.Stride,
                        changed.Y * frame.Stride + changed.X * 4);
            })) preview.FramesShown++;
        }
        if (!previews.Any(p => p.IsPlaying))
        {
            CompositionTarget.Rendering -= OnFrame;
            hooked = false;
        }
    }

    public void Dispose()
    {
        if (hooked) CompositionTarget.Rendering -= OnFrame;
        hooked = false;
        players.Dispose();
    }
}
