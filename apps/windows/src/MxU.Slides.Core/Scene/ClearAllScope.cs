namespace MxU.Slides.Core.Scene;

/// <summary>Names for the show's functions, as the Mac shows and stores them. Port of ShowFunction in SlideScene/ShowState.swift.</summary>
public static class ShowFunctions
{
    /// <summary>Every function, in the Mac's order (the Clear buttons and the checklist use it).</summary>
    public static readonly ShowFunction[] All = Enum.GetValues<ShowFunction>();

    public static string DisplayName(this ShowFunction function) => function switch
    {
        ShowFunction.Slides => "Slides",
        ShowFunction.Media => "Media",
        ShowFunction.Overlays => "Overlays",
        ShowFunction.Audio => "Music",
        ShowFunction.Alerts => "Alerts",
        _ => "Inputs",
    };

    /// <summary>The stored and API name: "slides", "media", "overlays", "audio", "alerts", "inputs".</summary>
    public static string Raw(this ShowFunction function) => function switch
    {
        ShowFunction.Slides => "slides",
        ShowFunction.Media => "media",
        ShowFunction.Overlays => "overlays",
        ShowFunction.Audio => "audio",
        ShowFunction.Alerts => "alerts",
        _ => "inputs",
    };

    public static ShowFunction? Parse(string? raw) => All.Cast<ShowFunction?>().FirstOrDefault(f => f!.Value.Raw() == raw);

    /// <summary>A function named by a remote: the old "tracks" clear is Music now.</summary>
    public static ShowFunction? FromApiName(string? name) => Parse(name == "tracks" ? "audio" : name);
}

/// <summary>
/// What Clear All (F1) clears: every function the operator's checklist ticks. By default it
/// leaves Inputs up, so a camera feed under the slides survives a panic clear. The checklist is
/// this computer's (settings.json), stored as the functions it skips, comma-separated.
/// Port of ClearAllScope in SlideScene/ShowState.swift.
/// </summary>
public static class ClearAllScope
{
    public static readonly IReadOnlySet<ShowFunction> DefaultSkips = new HashSet<ShowFunction> { ShowFunction.Inputs };

    /// <summary>
    /// The functions Clear All leaves alone. A saved checklist wins (names it no longer knows, like
    /// the Mac's retired "signage" and "tracks" rows, drop); without one, an old "Clear All includes
    /// music: off" switch carries over.
    /// </summary>
    public static HashSet<ShowFunction> Skips(string? stored, bool? legacyIncludesAudio)
    {
        if (stored is not null)
            return [.. stored.Split(',').Select(ShowFunctions.Parse).OfType<ShowFunction>()];
        return legacyIncludesAudio == false ? [.. DefaultSkips, ShowFunction.Audio] : [.. DefaultSkips];
    }

    /// <summary>Skips as stored: comma-separated, in the functions' order.</summary>
    public static string Encode(IEnumerable<ShowFunction> skips)
    {
        var set = skips.ToHashSet();
        return string.Join(",", ShowFunctions.All.Where(set.Contains).Select(f => f.Raw()));
    }

    /// <summary>The functions Clear All clears: all but the skipped ones.</summary>
    public static HashSet<ShowFunction> Swept(IEnumerable<ShowFunction> skips)
    {
        var swept = ShowFunctions.All.ToHashSet();
        swept.ExceptWith(skips);
        return swept;
    }

    /// <summary>
    /// Ticks or unticks one function in the checklist. Returns what to store: the new skips, and
    /// the old "includes music" switch kept in step (the Mac writes both, for older builds).
    /// </summary>
    public static (string Stored, bool LegacyIncludesAudio) Set(ShowFunction function, bool swept, string? stored, bool? legacyIncludesAudio)
    {
        var skips = Skips(stored, legacyIncludesAudio);
        if (swept) skips.Remove(function);
        else skips.Add(function);
        return (Encode(skips), !skips.Contains(ShowFunction.Audio));
    }
}

/// <summary>Whether a clear stops the multitrack stems. Port of TracksClear in SlideScene/ShowState.swift.</summary>
public static class TracksClear
{
    /// <summary>Stems are music: Clear Music (and a Clear All that clears music) stops them.</summary>
    public static bool StopsTracks(IReadOnlySet<ShowFunction> functions) => functions.Contains(ShowFunction.Audio);

    /// <summary>A timeline whose clock is its stems stops with them; one following timecode keeps following.</summary>
    public static bool StopsTimeline(bool ownClockPlaying, bool playingTracks) => ownClockPlaying && playingTracks;
}
