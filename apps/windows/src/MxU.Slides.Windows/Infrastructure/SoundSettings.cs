using System.Text.Json.Serialization;
using MxU.Slides.Audio.Library;
using MxU.Slides.Audio.Routing;

namespace MxU.Slides.Windows.Infrastructure;

/// <summary>
/// This PC's sound setup, kept in settings.json (it names this PC's devices, so it isn't in
/// the library): the audio outputs, the mixes and which one video sound plays on, and the
/// music's fades. The outputs and mixes are in the Mac's own JSON shape (its "audio.outputs"
/// and "audio.mixes" preferences); the rest are its "audio.media.mixId",
/// "audio.fadeEnabled", "audio.fadeSeconds" and "audio.crossfadeSeconds".
/// </summary>
public sealed class SoundSettings
{
    [JsonPropertyName("outputs")]
    public List<AudioOutputEntry> Outputs { get; set; } = [];

    [JsonPropertyName("mixes")]
    public List<AudioMixEntry> Mixes { get; set; } = [];

    /// <summary>The mix video sound plays on; null = the main mix (with the music).</summary>
    [JsonPropertyName("mediaMixId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MediaMixId { get; set; }

    /// <summary>Fade music in and out when it starts, pauses and stops.</summary>
    [JsonPropertyName("fadeEnabled")]
    public bool FadeEnabled { get; set; } = true;

    [JsonPropertyName("fadeSeconds")]
    public double FadeSeconds { get; set; } = 1.5;

    /// <summary>The room's crossfade between songs, for playlists that don't set their own.</summary>
    [JsonPropertyName("crossfadeSeconds")]
    public double CrossfadeSeconds { get; set; } = PlaylistCrossfade.DefaultSeconds;
}
