namespace MxU.Slides.Broadcast;

public enum BroadcastLogLevel { Info, Warning, Error }

/// <summary>One line of streaming or recording diagnostics. Never contains a stream key.</summary>
public sealed record BroadcastLogEntry(BroadcastLogLevel Level, string Category, string Message);

/// <summary>
/// Where the engine's diagnostics go: the Mac writes them to os.Logger and DiagnosticsStore;
/// here they are raised as <see cref="Written"/> so the app can send them to its own log.
/// Raised on whichever engine thread noticed the event; handlers must be quick and thread-safe.
/// </summary>
public static class BroadcastLog
{
    public static event Action<BroadcastLogEntry>? Written;

    public static void Info(string category, string message) => Write(BroadcastLogLevel.Info, category, message);

    public static void Warning(string category, string message) => Write(BroadcastLogLevel.Warning, category, message);

    public static void Error(string category, string message) => Write(BroadcastLogLevel.Error, category, message);

    private static void Write(BroadcastLogLevel level, string category, string message)
    {
        try
        {
            Written?.Invoke(new BroadcastLogEntry(level, category, message));
        }
        catch
        {
            // A broken log handler must never take a stream down.
        }
    }
}
