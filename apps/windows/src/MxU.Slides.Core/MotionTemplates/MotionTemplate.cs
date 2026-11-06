using System.Text.Json.Serialization;

namespace MxU.Slides.Core.MotionTemplates;

/// <summary>How a template is used in the show.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MotionUse>))]
public enum MotionUse
{
    /// <summary>Over the slides, on the overlay layer: a lower third, a bug, a title. The Mac sees a still overlay.</summary>
    Overlay,

    /// <summary>Full screen, behind the slides, on the background layer. The Mac sees a rendered video.</summary>
    Background,
}

/// <summary>
/// A motion template in the library's catalogue: an imported Lottie file (or a starter one). It's
/// Windows-only, so it lives in the Windows extras (kind <see cref="MotionTemplateStore.CatalogueKind"/>,
/// keyed by <see cref="Id"/>), and the animation itself is kept by its hash
/// (<see cref="MotionTemplateStore.LottieKind"/>). Using a template makes a <see cref="MotionTemplateUse"/>.
/// </summary>
public sealed record MotionTemplateInfo
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>SHA-256 of the self-contained animation JSON (see <see cref="MotionTemplateStore.Lottie"/>).</summary>
    public required string LottieHash { get; init; }

    /// <summary>The file it was imported from ("name-strap.json"), for the catalogue; null for starters.</summary>
    public string? SourceFileName { get; init; }

    /// <summary>Unix seconds.</summary>
    public double ImportedAt { get; init; }

    /// <summary>One the app made (the starter templates); put back when missing.</summary>
    public bool Starter { get; init; }

    /// <summary>What it looks like it's for: a design that covers the whole picture is a background, anything else an overlay.</summary>
    public MotionUse SuggestedUse { get; init; }

    public double Width { get; init; }

    public double Height { get; init; }

    public double FrameRate { get; init; }

    public double Seconds { get; init; }

    /// <summary>A line about it for the gallery ("Name and title over a coloured plate").</summary>
    public string? Description { get; init; }
}

/// <summary>
/// One use of a template in the show: Speaker = "Pastor Grace Okafor", in the church's blue. Each
/// use keeps its own values. It's kept beside the shared document that stands for it on the Mac
/// (an <see cref="Model.Overlay"/> showing a still, or a <see cref="Model.MediaItem"/> video),
/// in the Windows extras under <see cref="MotionTemplateStore.UseKind"/>, fingerprinted by that
/// document, so a Mac's later edit to it wins.
/// </summary>
public sealed record MotionTemplateUse
{
    /// <summary>The catalogue entry it was made from (it may since have been removed: the use carries its own animation hash).</summary>
    public string? TemplateId { get; init; }

    public required string LottieHash { get; init; }

    public required MotionUse Use { get; init; }

    /// <summary>The field values (<see cref="MotionFields"/> ids to values). Missing ones show the template's own.</summary>
    public Dictionary<string, string> Values { get; init; } = [];

    public MotionPlayback Playback { get; init; } = MotionPlayback.Loop;

    /// <summary>The library item the Mac shows instead: the still (PNG) of an overlay, or the video of a background.</summary>
    public required string FallbackMediaId { get; init; }

    /// <summary>
    /// What the fallback was made from (<see cref="MotionTemplateStore.FallbackStamp"/>): when the
    /// values change, it no longer matches and the still or video is made again.
    /// </summary>
    public string? FallbackStamp { get; init; }

    /// <summary>A background's colour behind the animation in its video (and where the animation is see-through), "#RRGGBB".</summary>
    public string BackgroundColor { get; init; } = "#000000";
}
