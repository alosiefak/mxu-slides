using System.Globalization;
using MxU.Slides.Core.Library;
using MxU.Slides.Core.Model;

namespace MxU.Slides.Audio.Library;

/// <summary>
/// How long a playlist crossfades between tracks: the room's default unless the playlist
/// overrides it (0 = cut). Port of PresenterCore's PlaylistCrossfade.swift.
/// </summary>
public static class PlaylistCrossfade
{
    public const double DefaultSeconds = 3;
    public const double MaximumSeconds = 12;

    public static readonly IReadOnlyList<double> Presets = [1, 2, 4, 8, 12];

    /// <summary>The playlist's override, else the room default; never negative.</summary>
    public static double Resolve(double? @override, double roomDefault) => Math.Max(0, @override ?? roomDefault);

    /// <summary>Old playlists stored 0 to mean "use the default": that is now null.</summary>
    public static double? MigratedOverride(double? stored) => stored == 0 ? null : stored;

    /// <summary>"Off" or "3.5s" (in the user's number format).</summary>
    public static string Label(double seconds) =>
        seconds <= 0 ? "Off" : $"{seconds.ToString("0.###", CultureInfo.CurrentCulture)}s";

    /// <summary>Rewrites legacy 0-second overrides to follow the room default; returns how many changed.</summary>
    public static int MigrateCrossfadeDefaults(LibraryStore store)
    {
        var migrated = 0;
        foreach (var playlist in store.All<Playlist>())
        {
            var stored = playlist.CrossfadeSeconds;
            if (MigratedOverride(stored) == stored) continue;
            playlist.CrossfadeSeconds = null;
            store.Save(playlist);
            migrated++;
        }
        return migrated;
    }
}

/// <summary>The mixer strips' sizes, so the fader fills the window without hiding its grab bar. Port of MixerStripLayout.swift.</summary>
public static class MixerStripLayout
{
    public const double MinimumFaderHeight = 92;
    public const double FixedChrome = 225;
    public const double StripChrome = 133;
    public const double GrabBarReach = 4;

    public static double FaderHeight(double viewportHeight, double scrollerHeight) =>
        viewportHeight > 0 ? Math.Max(MinimumFaderHeight, viewportHeight - FixedChrome - scrollerHeight) : MinimumFaderHeight;

    /// <summary>A whole strip's height (fold cards match it).</summary>
    public static double StripHeight(double viewportHeight, double scrollerHeight) =>
        FaderHeight(viewportHeight, scrollerHeight) + StripChrome;

    /// <summary>A click near the grab bar lands on it (just inside its ends).</summary>
    public static double GrabBarY(double y, double barMinY, double barMaxY) => Math.Min(Math.Max(y, barMinY + 1), barMaxY - 1);
}
