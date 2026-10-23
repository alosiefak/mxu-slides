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
            player.Failed = true;
            Log.Warn($"Video failed to play ({media.Name}): {e.ErrorException?.Message}");
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
