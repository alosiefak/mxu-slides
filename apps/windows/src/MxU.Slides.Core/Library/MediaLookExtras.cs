using MxU.Slides.Core.Model;

namespace MxU.Slides.Core.Library;

/// <summary>How a media item fills the screen when it's shown on its own (a background or a full-screen video).</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<MediaLookFit>))]
public enum MediaLookFit
{
    /// <summary>Covers the whole screen, trimming what doesn't fit (the default, and what a Mac does).</summary>
    Fill,

    /// <summary>Shows the whole picture, with bars where its shape differs from the screen's.</summary>
    Fit,

    /// <summary>Squeezes or stretches the picture to the screen's shape.</summary>
    Stretch,
}

/// <summary>
/// What to cut off a picture's edges, as fractions of its width and height (0.1 = a tenth). The
/// part left is what <see cref="MediaLook.Fit"/> places on the screen.
/// </summary>
public sealed record MediaCrop(double Left, double Top, double Right, double Bottom)
{
    public static readonly MediaCrop None = new(0, 0, 0, 0);

    /// <summary>True when nothing is cut off.</summary>
    public bool IsNone => Left <= 0.0001 && Top <= 0.0001 && Right <= 0.0001 && Bottom <= 0.0001;

    /// <summary>
    /// The same crop kept sensible: each edge 0 to 0.45, so at least a tenth of the picture is
    /// always left in each direction and a slip of the slider never leaves nothing to show.
    /// </summary>
    public MediaCrop Clamped()
    {
        static double Edge(double v) => double.IsFinite(v) ? Math.Round(Math.Clamp(v, 0, 0.45), 4) : 0;
        return new MediaCrop(Edge(Left), Edge(Top), Edge(Right), Edge(Bottom));
    }

    /// <summary>The part of the picture kept, in its own unit square (what the renderer's source rectangle is).</summary>
    public (double X, double Y, double Width, double Height) Kept() => (Left, Top, Math.Max(0.01, 1 - Left - Right), Math.Max(0.01, 1 - Top - Bottom));
}

/// <summary>
/// A media item's look on Windows: how it fills the screen, how big it is (1 = the screen), what's
/// cropped off its edges, and how loud a video plays (1 = as recorded). The shared MediaItem has
/// none of these, so they're a Windows extra; a Mac shows the item filling the screen at full volume.
/// </summary>
public sealed record MediaLook(MediaLookFit Fit = MediaLookFit.Fill, double Scale = 1, MediaCrop? Crop = null, double Volume = 1)
{
    public static readonly MediaLook Default = new();

    public const double MinimumScale = 0.1;
    public const double MaximumScale = 4;
    public const double MaximumVolume = 1;

    /// <summary>True when it looks and sounds exactly as a Mac shows it (nothing worth keeping).</summary>
    public bool IsDefault => Fit == MediaLookFit.Fill && Math.Abs(Scale - 1) < 0.0005 && (Crop?.IsNone ?? true) && Math.Abs(Volume - 1) < 0.0005;

    /// <summary>The same look with every number kept in range.</summary>
    public MediaLook Tidied() => this with
    {
        Scale = double.IsFinite(Scale) ? Math.Round(Math.Clamp(Scale, MinimumScale, MaximumScale), 3) : 1,
        Crop = Crop?.Clamped() is { IsNone: false } crop ? crop : null,
        Volume = double.IsFinite(Volume) ? Math.Round(Math.Clamp(Volume, 0, MaximumVolume), 3) : 1,
    };
}

/// <summary>
/// The Windows-only looks of media items (<see cref="MediaLook"/>), kept beside the library as
/// Windows extras ("Windows extras/media-look/&lt;media id&gt;.json"). Each is saved against a
/// fingerprint of the item's file: a crop or a zoom is made for one picture, so if a Mac replaces
/// the file the look is dropped and the new picture shows whole. Looking a look up happens every
/// time the outputs are drawn, so answers are remembered and forgotten when an extra changes.
/// Windows-only (the Mac has no twin).
/// </summary>
public sealed class MediaLookExtras
{
    /// <summary>The extras folder these live in.</summary>
    public const string Kind = "media-look";

    private readonly WindowsExtras extras;
    private readonly Dictionary<(string Id, string Basis), MediaLook?> remembered = [];
    private readonly Lock gate = new();

    public MediaLookExtras(WindowsExtras extras)
    {
        this.extras = extras;
        extras.Changed += (kind, _) =>
        {
            if (kind != Kind) return;
            lock (gate) remembered.Clear();
        };
    }

    /// <summary>The shared part a look is made against: the item's file.</summary>
    public static string BasisOf(MediaItem item) => WindowsExtras.Fingerprint(item.FileHash);

    /// <summary>The item's look, or the default when it has none (or its file has changed since).</summary>
    public MediaLook For(MediaItem item)
    {
        var key = (item.Id, BasisOf(item));
        lock (gate)
        {
            if (remembered.TryGetValue(key, out var known)) return known ?? MediaLook.Default;
        }
        var look = extras.Get<MediaLook>(Kind, item.Id, key.Item2);
        lock (gate) remembered[key] = look;
        return look ?? MediaLook.Default;
    }

    /// <summary>Saves an item's look (tidied); the default look is no extra at all, so it's deleted.</summary>
    public void Save(MediaItem item, MediaLook look)
    {
        var tidied = look.Tidied();
        if (tidied.IsDefault) extras.Delete(Kind, item.Id);
        else extras.Save(Kind, item.Id, tidied, BasisOf(item));
    }

    /// <summary>A duplicated item takes the look with it.</summary>
    public void Copied(MediaItem from, MediaItem to)
    {
        var look = For(from);
        if (!look.IsDefault) Save(to, look);
    }
}
