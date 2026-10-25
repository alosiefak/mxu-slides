using System.Text.Json.Serialization;

namespace MxU.Slides.Core.Motion;

/// <summary>
/// Everything needed to make a motion background again, exactly: the style, its colours,
/// how fast and how strong it is, how long the loop is, and the picture size and frame rate.
/// The same recipe always gives the same video, so it's what the library keeps (in
/// <see cref="Library.WindowsExtras"/>, under <see cref="ExtraKind"/>, keyed by the media item's id)
/// so "Edit motion background…" can open it again and make a variation.
/// <para>
/// The video itself is an ordinary H.264 MP4 media item, which the Mac plays like any other. The
/// recipe is Windows-only and lives where the Mac never looks, so nothing new is ever written
/// into a shared document.
/// </para>
/// </summary>
public sealed record MotionRecipe
{
    /// <summary>The <see cref="Library.WindowsExtras"/> kind recipes are saved under.</summary>
    public const string ExtraKind = "motion-background";

    public const int MinSeconds = 10;
    public const int MaxSeconds = 30;
    public const double MinSpeed = 0.25;
    public const double MaxSpeed = 2;

    /// <summary>The style's id (<see cref="MotionStyle.Id"/>), such as "aurora".</summary>
    [JsonPropertyName("style")]
    public string Style { get; init; } = MotionStyles.All[0].Id;

    /// <summary>The preset these colours came from, or null for colours picked by hand.</summary>
    [JsonPropertyName("palette")]
    public string? Palette { get; init; }

    /// <summary>
    /// Two to four "#RRGGBB" colours. A preset's are copied in too, so a recipe makes the same
    /// video even if a later version of the app changes the preset.
    /// </summary>
    [JsonPropertyName("colors")]
    public IReadOnlyList<string> Colors { get; init; } = [];

    /// <summary>How fast it moves: 1 is the style's natural pace, ¼ very slow, 2 twice as fast.</summary>
    [JsonPropertyName("speed")]
    public double Speed { get; init; } = 1;

    /// <summary>How strong it is, 0–1: brightness and contrast of the light, the clouds or the particles.</summary>
    [JsonPropertyName("intensity")]
    public double Intensity { get; init; } = 0.6;

    /// <summary>The loop's length in whole seconds (10–30).</summary>
    [JsonPropertyName("seconds")]
    public int Seconds { get; init; } = 20;

    /// <summary>Picture height: 1080 (1920 × 1080) or 2160 (3840 × 2160, 4K).</summary>
    [JsonPropertyName("height")]
    public int Height { get; init; } = 1080;

    /// <summary>30 or 60 frames a second.</summary>
    [JsonPropertyName("frameRate")]
    public int FrameRate { get; init; } = 30;

    /// <summary>Which of the style's endless variations: a different number moves everything somewhere else.</summary>
    [JsonPropertyName("variation")]
    public int Variation { get; init; } = 1;

    /// <summary>The media item's name when it was made.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>
    /// The drawing code's version when this was made. If a later version draws a style
    /// differently, an old recipe still opens; it just renders the new way.
    /// </summary>
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    /// <summary>Picture width: 16:9.</summary>
    [JsonIgnore]
    public int Width => Height * 16 / 9;

    /// <summary>How many frames the loop has: frame i is shown at i ÷ frame rate seconds.</summary>
    [JsonIgnore]
    public int FrameCount => LoopTiming.FrameCount(Seconds, FrameRate);

    /// <summary>A recipe for <paramref name="style"/> in its own palette.</summary>
    public static MotionRecipe For(MotionStyle style) => new()
    {
        Style = style.Id,
        Palette = style.DefaultPalette,
        Colors = MotionPalette.Find(style.DefaultPalette)?.Colors ?? MotionPalette.Presets[0].Colors,
    };

    /// <summary>
    /// The recipe with everything in range: a known style, 2–4 real colours (the style's own
    /// palette when there aren't), speed ¼–2, intensity 0–1, 10–30 seconds, 1080 or 2160, 30 or 60.
    /// Used before drawing, so a damaged or hand-edited recipe can't make a broken video.
    /// </summary>
    public MotionRecipe Normalized()
    {
        var style = MotionStyles.Find(Style) ?? MotionStyles.All[0];
        var colors = Colors?.Where(c => MotionColor.FromHex(c) is not null).Take(MotionPalette.MaxColors).ToList() ?? [];
        var palette = Palette;
        if (colors.Count < MotionPalette.MinColors)
        {
            var preset = MotionPalette.Find(Palette) ?? MotionPalette.Find(style.DefaultPalette) ?? MotionPalette.Presets[0];
            colors = [.. preset.Colors];
            palette = preset.Name;
        }
        return this with
        {
            Style = style.Id,
            Palette = palette,
            Colors = colors,
            Speed = double.IsFinite(Speed) ? Math.Clamp(Speed, MinSpeed, MaxSpeed) : 1,
            Intensity = double.IsFinite(Intensity) ? Math.Clamp(Intensity, 0, 1) : 0.6,
            Seconds = Math.Clamp(Seconds, MinSeconds, MaxSeconds),
            Height = Height >= 2160 ? 2160 : 1080,
            FrameRate = FrameRate >= 60 ? 60 : 30,
        };
    }

    /// <summary>"Aurora · Northern Lights" (or "Aurora · Custom colours"), the name a new background gets.</summary>
    public string SuggestedName()
    {
        var style = MotionStyles.Find(Style) ?? MotionStyles.All[0];
        return $"{style.Name} · {Palette ?? "Custom colours"}";
    }

    /// <summary>A rough file size, for the window: the bitrate the writer is given, times the length.</summary>
    public long EstimatedBytes() => (long)LoopTiming.Bitrate(Width, Height, FrameRate) / 8 * Seconds;
}
