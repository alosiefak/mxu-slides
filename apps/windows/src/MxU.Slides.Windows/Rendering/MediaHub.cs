using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MxU.Slides.Core.Model;
using MxU.Slides.Windows.Infrastructure;

namespace MxU.Slides.Windows.Rendering;

/// <summary>
/// Owns decoded images and video players for every view. Images decode off
/// the UI thread and are cached by size; videos are shared by playback key,
/// so the audience output and the operator preview draw the same player and
/// stay frame-for-frame in sync. Players are reference counted and closed a
/// moment after the last view lets go.
/// </summary>
public sealed class MediaHub
{
    public sealed record Resolved(string Path, MediaKind Kind, string Name);

    private sealed class Player
    {
        public required MediaPlayer MediaPlayer { get; init; }
        public required string MediaId { get; init; }
        public int References;
        public bool Opened;
        public bool Failed;
        public string? FailureReason;
        public bool RetriedSilently;
        public bool Loops;
        public bool Playing;
        public DispatcherTimer? CloseTimer;
    }

    private readonly Func<string, Resolved?> resolve;
    private readonly Dispatcher dispatcher;
    private readonly Dictionary<string, BitmapSource> images = new(StringComparer.Ordinal);
    private readonly HashSet<string> decoding = new(StringComparer.Ordinal);
    private readonly HashSet<string> failedImages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Player> players = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Size> naturalSizes = new(StringComparer.Ordinal);

    public MediaHub(Func<string, Resolved?> resolve)
    {
        this.resolve = resolve;
        dispatcher = Dispatcher.CurrentDispatcher;
    }

    /// <summary>Raised on the UI thread when an image finishes decoding or a video opens, with its media id.</summary>
    public event Action<string>? MediaReady;

    public double VideoVolume { get; set; } = 1;

    public Resolved? Resolve(string mediaId) => resolve(mediaId);

    public MediaKind? KindOf(string mediaId) => resolve(mediaId)?.Kind;

    public string? NameOf(string mediaId) => resolve(mediaId)?.Name;

    // MARK: Images

    /// <summary>The decoded image at roughly <paramref name="pixelWidth"/>, or null while it decodes (MediaReady follows).</summary>
    public BitmapSource? Image(string mediaId, int pixelWidth)
    {
        var bucket = pixelWidth <= 480 ? 480 : pixelWidth <= 1280 ? 1280 : 3840;
        var key = $"{mediaId}@{bucket}";
        if (images.TryGetValue(key, out var image)) return image;
        // Any larger cached decode is a fine stand-in while the right size loads.
        var larger = images.Where(p => p.Key.StartsWith(mediaId + "@", StringComparison.Ordinal)).Select(p => p.Value).FirstOrDefault();
        if (failedImages.Contains(key) || !decoding.Add(key)) return larger;
        if (resolve(mediaId) is not { Kind: MediaKind.Image } media)
        {
            decoding.Remove(key);
            failedImages.Add(key);
            return null;
        }
        Task.Run(() => Decode(media.Path, bucket)).ContinueWith(task =>
        {
            decoding.Remove(key);
            if (task.Result is { } decoded) images[key] = decoded;
            else failedImages.Add(key);
            MediaReady?.Invoke(mediaId);
        }, TaskScheduler.FromCurrentSynchronizationContext());
        return larger;
    }

    private static BitmapSource? Decode(string path, int maxWidth)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            BitmapSource source = frame;
            if (frame.PixelWidth > maxWidth)
            {
                var scale = (double)maxWidth / frame.PixelWidth;
                source = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
            }
            source = Oriented(source, frame.Metadata as BitmapMetadata);
            var cached = new CachedBitmap(source, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            cached.Freeze();
            return cached;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not decode image {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Applies EXIF orientation so phone photos are upright.</summary>
    private static BitmapSource Oriented(BitmapSource source, BitmapMetadata? metadata)
    {
        ushort orientation = 1;
        try
        {
            if (metadata?.GetQuery("System.Photo.Orientation") is ushort value) orientation = value;
        }
        catch
        {
        }
        Transform? transform = orientation switch
        {
            3 => new RotateTransform(180),
            6 => new RotateTransform(90),
            8 => new RotateTransform(270),
            2 => new ScaleTransform(-1, 1),
            4 => new ScaleTransform(1, -1),
            _ => null,
        };
        return transform is null ? source : new TransformedBitmap(source, transform);
    }

    // MARK: Poster frames

    private readonly Dictionary<string, BitmapSource> posters = new(StringComparer.Ordinal);
    private readonly HashSet<string> posterFailed = new(StringComparer.Ordinal);
    private readonly Queue<string> posterQueue = new();
    private bool posterBusy;

    /// <summary>Folder where poster frames are cached between launches.</summary>
    public string? PosterCache { get; set; }

    /// <summary>A still from a video for thumbnails, or null while it is made (MediaReady follows).</summary>
    public BitmapSource? Poster(string mediaId)
    {
        if (posters.TryGetValue(mediaId, out var poster)) return poster;
        if (posterFailed.Contains(mediaId) || posterQueue.Contains(mediaId)) return null;
        if (resolve(mediaId) is not { Kind: MediaKind.Video } media) return null;
        var cached = CachedPosterPath(media);
        if (cached is not null && File.Exists(cached))
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(cached);
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();
                posters[mediaId] = image;
                return image;
            }
            catch
            {
                File.Delete(cached);
            }
        }
        posterQueue.Enqueue(mediaId);
        PumpPosters();
        return null;
    }

    private string? CachedPosterPath(Resolved media) =>
        PosterCache is null ? null : Path.Combine(PosterCache, Path.GetFileNameWithoutExtension(media.Path) + ".png");

    /// <summary>Grabs posters one at a time: open muted, seek a second in, draw the frame.</summary>
    private void PumpPosters()
    {
        if (posterBusy || posterQueue.Count == 0) return;
        var mediaId = posterQueue.Dequeue();
        if (resolve(mediaId) is not { Kind: MediaKind.Video } media)
        {
            PumpPosters();
            return;
        }
        posterBusy = true;
        var player = new MediaPlayer { IsMuted = true, Volume = 0, ScrubbingEnabled = true };
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        void Finish(BitmapSource? frame)
        {
            timeout.Stop();
            player.Close();
            if (frame is null) posterFailed.Add(mediaId);
            else posters[mediaId] = frame;
            posterBusy = false;
            MediaReady?.Invoke(mediaId);
            PumpPosters();
        }
        timeout.Tick += (_, _) => Finish(null);
        player.MediaFailed += (_, _) => Finish(null);
        player.MediaOpened += (_, _) =>
        {
            var length = player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan : TimeSpan.Zero;
            player.Position = length > TimeSpan.FromSeconds(3) ? TimeSpan.FromSeconds(1) : TimeSpan.FromTicks(length.Ticks / 3);
            var settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            settle.Tick += (_, _) =>
            {
                settle.Stop();
                try
                {
                    var w = Math.Max(1, player.NaturalVideoWidth);
                    var h = Math.Max(1, player.NaturalVideoHeight);
                    var scale = Math.Min(1, 480.0 / w);
                    var visual = new DrawingVisual();
                    using (var dc = visual.RenderOpen())
                        dc.DrawVideo(player, new Rect(0, 0, w * scale, h * scale));
                    var bitmap = new RenderTargetBitmap((int)(w * scale), (int)(h * scale), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(visual);
                    bitmap.Freeze();
                    SavePoster(media, bitmap);
                    Finish(bitmap);
                }
                catch (Exception ex)
                {
                    Log.Warn($"No poster for {media.Name}: {ex.Message}");
                    Finish(null);
                }
            };
            settle.Start();
        };
        timeout.Start();
        player.Open(new Uri(media.Path));
        player.Pause();
    }

    private void SavePoster(Resolved media, BitmapSource bitmap)
    {
        if (CachedPosterPath(media) is not { } path) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(path);
            encoder.Save(file);
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't cache poster: {ex.Message}");
        }
    }

    // MARK: Video

    /// <summary>
    /// The shared player for <paramref name="playbackKey"/>, created (and started) on first use.
    /// Callers pair each <see cref="Retain"/> with a <see cref="Release"/>.
    /// </summary>
    public MediaPlayer? Video(string playbackKey, string mediaId, bool loops, bool audible)
    {
        if (players.TryGetValue(playbackKey, out var existing)) return existing.Failed ? null : existing.MediaPlayer;
        if (resolve(mediaId) is not { Kind: MediaKind.Video } media) return null;

        var player = new Player { MediaPlayer = new MediaPlayer(), MediaId = mediaId, Loops = loops };
        var mp = player.MediaPlayer;
        mp.Volume = audible ? VideoVolume : 0;
        mp.IsMuted = !audible;
        mp.ScrubbingEnabled = false;
        mp.MediaOpened += (_, _) =>
        {
            player.Opened = true;
            naturalSizes[mediaId] = new Size(mp.NaturalVideoWidth, mp.NaturalVideoHeight);
            MediaReady?.Invoke(mediaId);
        };
        mp.MediaFailed += (_, e) =>
        {
            // A PC with no working sound output (speakers unplugged, a server) can't open a video
            // with sound; show it silently rather than not at all.
            if (!mp.IsMuted && !player.RetriedSilently)
            {
                player.RetriedSilently = true;
                Log.Warn($"Video couldn't play with sound ({media.Name}): {e.ErrorException?.Message}. Trying again without sound.");
                mp.Close();
                mp.IsMuted = true;
                mp.Volume = 0;
                mp.Open(new Uri(media.Path));
                if (player.Playing) mp.Play();
                return;
            }
            player.Failed = true;
            player.FailureReason = e.ErrorException?.Message ?? "unknown error";
            Log.Warn($"Video failed to play ({media.Name}): {player.FailureReason}");
            MediaReady?.Invoke(mediaId);
        };
        mp.MediaEnded += (_, _) =>
        {
            if (player.Loops)
            {
                mp.Position = TimeSpan.Zero;
                mp.Play();
            }
            else
            {
                mp.Pause();
                player.Playing = false;
                VideoEnded?.Invoke(playbackKey);
            }
        };
        players[playbackKey] = player;
        mp.Open(new Uri(media.Path));
        mp.Play();
        player.Playing = true;
        return mp;
    }

    /// <summary>Raised when a non-looping video reaches its end, with its playback key.</summary>
    public event Action<string>? VideoEnded;

    public Size? NaturalSize(string mediaId) => naturalSizes.TryGetValue(mediaId, out var size) && size.Width > 0 ? size : null;

    public bool IsVideoFailed(string playbackKey) => players.TryGetValue(playbackKey, out var p) && p.Failed;

    /// <summary>Why the video for a key couldn't play, for diagnostics; null while it plays or is opening.</summary>
    public string? VideoFailureReason(string playbackKey) => players.TryGetValue(playbackKey, out var p) ? p.FailureReason : null;

    public void Retain(string playbackKey)
    {
        if (!players.TryGetValue(playbackKey, out var player)) return;
        player.References++;
        player.CloseTimer?.Stop();
        player.CloseTimer = null;
    }

    public void Release(string playbackKey)
    {
        if (!players.TryGetValue(playbackKey, out var player)) return;
        player.References = Math.Max(0, player.References - 1);
        if (player.References > 0) return;
        player.CloseTimer?.Stop();
        player.CloseTimer = new DispatcherTimer(TimeSpan.FromSeconds(1.5), DispatcherPriority.Background, (_, _) =>
        {
            if (player.References > 0) return;
            player.CloseTimer?.Stop();
            players.Remove(playbackKey);
            try
            {
                player.MediaPlayer.Stop();
                player.MediaPlayer.Close();
            }
            catch (Exception ex)
            {
                Log.Warn($"Closing video player: {ex.Message}");
            }
        }, dispatcher);
        player.CloseTimer.Start();
    }

    /// <summary>Playback position and length of the live video for a key, for transport controls and countdowns.</summary>
    public (TimeSpan Position, TimeSpan? Duration, bool Playing)? Transport(string playbackKey)
    {
        if (!players.TryGetValue(playbackKey, out var p) || !p.Opened) return null;
        var mp = p.MediaPlayer;
        return (mp.Position, mp.NaturalDuration.HasTimeSpan ? mp.NaturalDuration.TimeSpan : null, p.Playing);
    }

    public IEnumerable<(string Key, string MediaId)> ActiveVideos => players.Select(p => (p.Key, p.Value.MediaId));

    public void Pause(string playbackKey)
    {
        if (!players.TryGetValue(playbackKey, out var p)) return;
        p.MediaPlayer.Pause();
        p.Playing = false;
    }

    public void Play(string playbackKey)
    {
        if (!players.TryGetValue(playbackKey, out var p)) return;
        p.MediaPlayer.Play();
        p.Playing = true;
    }

    public void Restart(string playbackKey)
    {
        if (!players.TryGetValue(playbackKey, out var p)) return;
        p.MediaPlayer.Position = TimeSpan.Zero;
        p.MediaPlayer.Play();
        p.Playing = true;
    }

    /// <summary>Jumps to a position, clamped to the video's length; playing or paused stays as it was.</summary>
    public void Seek(string playbackKey, TimeSpan position)
    {
        if (!players.TryGetValue(playbackKey, out var p)) return;
        var mp = p.MediaPlayer;
        if (mp.NaturalDuration.HasTimeSpan && position > mp.NaturalDuration.TimeSpan) position = mp.NaturalDuration.TimeSpan;
        mp.Position = position < TimeSpan.Zero ? TimeSpan.Zero : position;
    }

    public void ApplyVolume()
    {
        foreach (var p in players.Values)
            if (!p.MediaPlayer.IsMuted) p.MediaPlayer.Volume = VideoVolume;
    }

    /// <summary>Forgets cached decodes for a media item (after it is replaced or relinked).</summary>
    public void Invalidate(string mediaId)
    {
        foreach (var key in images.Keys.Where(k => k.StartsWith(mediaId + "@", StringComparison.Ordinal)).ToList()) images.Remove(key);
        failedImages.RemoveWhere(k => k.StartsWith(mediaId + "@", StringComparison.Ordinal));
    }
}
