namespace MxU.Slides.VideoIO.Common;

/// <summary>
/// Diagnostics lines from the video classes ("decklink.start", "input.ndi", …), the
/// same categories the Mac writes to its DiagnosticsStore. The app subscribes once and
/// forwards them to its log. Raised on whatever thread noticed the event, so a handler
/// must be thread-safe and quick. Nothing is lost if nobody listens.
/// </summary>
public static class VideoIOLog
{
    /// <summary>(category, detail), e.g. ("decklink.start", "DeckLink Duo 2 (1): sending 1080p29.97").</summary>
    public static event Action<string, string>? Noted;

    internal static void Note(string category, string detail)
    {
        try { Noted?.Invoke(category, detail); }
        catch
        {
            // A broken log handler must never take a capture or output down with it.
        }
    }
}
