using MxU.Slides.Core.Threading;

namespace MxU.Slides.Audio;

/// <summary>
/// The audio engines' own background threads. Nothing a service depends on (a playlist
/// moving on when a song ends, a multitrack handover, the next song's stems loading, a
/// device coming back) waits for .NET's shared thread pool, which an import, a slow disk or
/// a busy machine can tie up. Timers use <see cref="ShowTimeProvider.Shared"/> for the same reason.
/// </summary>
internal static class AudioThreads
{
    /// <summary>Notifications, in order: a track played to its end, a song handed over, an output's status changed, the controller's events.</summary>
    public static WorkQueue Events { get; } = new("MxU audio events", ThreadPriority.AboveNormal);

    /// <summary>Chores that mustn't hold up the notifications: closing decoders, restarting an input whose device came back.</summary>
    public static WorkQueue Chores { get; } = new("MxU audio chores");

    /// <summary>Decoding the coming songs' stems ahead of time.</summary>
    public static WorkQueue Preload { get; } = new("MxU stem preload");

    /// <summary>Windows' audio device calls: its COM objects want a multithreaded-apartment thread.</summary>
    public static WorkQueue Devices { get; } = new("MxU audio devices", apartment: ApartmentState.MTA);
}
