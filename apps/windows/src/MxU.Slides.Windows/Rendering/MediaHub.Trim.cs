using System.Runtime.CompilerServices;
using System.Windows.Media;
using MxU.Slides.Core.Media;

namespace MxU.Slides.Windows.Rendering;

/// <summary>
/// A video's trim and speed (the media item's in point, out point and play rate), as the Mac's
/// MediaPlayer plays them: it starts at the in point, a pass ends at the out point (or the file's
/// end), a looping video goes back to the in point, and everything runs at the play rate.
/// <para>
/// WPF's MediaPlayer has no "stop here" setting, so while a trimmed video plays its position is
/// checked on every screen refresh (CompositionTarget.Rendering, on the UI thread, which is where a
/// MediaPlayer must be used; never the shared thread pool). <see cref="VideoEnded"/> is raised once
/// per pass, at the out point or the end, for looping and non-looping videos alike.
/// </para>
/// </summary>
public sealed partial class MediaHub
{
    /// <summary>Where a player is in its window, and whether this pass has already ended.</summary>
    private sealed class Pass
    {
        public PlaybackOptions? Options;
        public double Start;
        public double End = double.PositiveInfinity;
        public double Rate = 1;
        public bool Resolved;
        public bool EndRaised;

        /// <summary>The out point is before the file's end, so the pass has to be watched for.</summary>
        public bool Watched;

        /// <summary>Just looped back: wait until the player reports a position back inside the window (seeking takes a moment).</summary>
        public bool Seeking;
    }

    /// <summary>WPF reports no frame rate; 30 a second is what the Mac assumes when a file doesn't say.</summary>
    private const double AssumedFrameDuration = 1.0 / 30;

    private readonly ConditionalWeakTable<Player, Pass> passes = new();
    private bool watchingPasses;

    /// <summary>A media item's trim and speed by media id (set by the app from the library), or null to play it whole.</summary>
    public Func<string, PlaybackOptions?>? TrimOf { get; set; }

    private Pass PassOf(Player player) => passes.GetValue(player, _ => new Pass());

    /// <summary>A new player: remembers its trim, until its length is known when it opens.</summary>
    private void BeginPass(Player player) => PassOf(player).Options = TrimOf?.Invoke(player.MediaId);

    /// <summary>The player has opened: it now knows its length, so jump to the in point and set the speed.</summary>
    private void StartPass(Player player)
    {
        var pass = PassOf(player);
        var mp = player.MediaPlayer;
        var duration = mp.NaturalDuration.HasTimeSpan ? mp.NaturalDuration.TimeSpan.TotalSeconds : 0;
        if (pass.Options is { } options && duration > 0)
        {
            (pass.Start, pass.End, pass.Rate) = options.Resolved(duration, AssumedFrameDuration);
            pass.Watched = pass.End < duration - AssumedFrameDuration / 2;
            if (pass.Start > 0) mp.Position = TimeSpan.FromSeconds(pass.Start);
        }
        else
        {
            pass.End = duration > 0 ? duration : double.PositiveInfinity;
        }
        pass.Resolved = true;
        pass.EndRaised = false;
        ApplyRate(player);
        if (pass.Watched) WatchPasses();
    }

    /// <summary>The speed, again after every Play (WPF can forget it when playback restarts).</summary>
    private void ApplyRate(Player player)
    {
        var rate = passes.TryGetValue(player, out var pass) ? pass.Rate : 1;
        if (Math.Abs(player.MediaPlayer.SpeedRatio - rate) > 1e-6) player.MediaPlayer.SpeedRatio = rate;
    }

    /// <summary>
    /// A pass has finished (the file ended, or the out point was reached). A looping video goes back to
    /// its in point and plays on; any other pauses there. Either way <see cref="VideoEnded"/> is raised, once.
    /// </summary>
    private void PassEnded(Player player, string playbackKey)
    {
        var pass = PassOf(player);
        var mp = player.MediaPlayer;
        if (player.Loops)
        {
            mp.Position = TimeSpan.FromSeconds(pass.Start);
            mp.Play();
            ApplyRate(player);
            pass.EndRaised = false;
            pass.Seeking = true;
            // Its sound, played by our engine, goes back with it (MediaHub.Sound.cs).
            SoundJumped(playbackKey, TimeSpan.FromSeconds(pass.Start));
        }
        else
        {
            if (pass.EndRaised) return;
            mp.Pause();
            player.Playing = false;
            pass.EndRaised = true;
            SoundTransportChanged(playbackKey);
        }
        VideoEnded?.Invoke(playbackKey);
    }

    /// <summary>Where a pass starts (a restart, or a seek of 0, goes there).</summary>
    private TimeSpan PassStart(Player player) => TimeSpan.FromSeconds(passes.TryGetValue(player, out var pass) ? pass.Start : 0);

    /// <summary>A position inside the window, from one measured from the window's start.</summary>
    private TimeSpan InWindow(Player player, TimeSpan position)
    {
        if (!passes.TryGetValue(player, out var pass) || !pass.Resolved) return position;
        var seconds = Math.Clamp(pass.Start + position.TotalSeconds, pass.Start, double.IsInfinity(pass.End) ? double.MaxValue : pass.End);
        pass.EndRaised = false;
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>The transport as the window sees it: position from the in point, length of the window (as the Mac reports it).</summary>
    private (TimeSpan Position, TimeSpan? Duration) WindowTransport(Player player, TimeSpan position, TimeSpan? duration)
    {
        if (!passes.TryGetValue(player, out var pass) || !pass.Resolved || pass.Options is null) return (position, duration);
        var length = double.IsInfinity(pass.End) ? duration?.TotalSeconds ?? 0 : pass.End - pass.Start;
        var at = Math.Clamp(position.TotalSeconds - pass.Start, 0, Math.Max(0, length));
        return (TimeSpan.FromSeconds(at), TimeSpan.FromSeconds(length));
    }

    /// <summary>A player's state in one line, for a self-test report when something didn't play as expected.</summary>
    public string DescribeVideo(string playbackKey)
    {
        if (!players.TryGetValue(playbackKey, out var player)) return "no player";
        var pass = PassOf(player);
        string at;
        try { at = $"{player.MediaPlayer.Position.TotalSeconds:0.00} s in the file"; }
        catch (InvalidOperationException) { at = "position unknown"; }
        return $"opened {player.Opened}, playing {player.Playing}, loops {player.Loops}, views {player.References}, {at}; "
            + $"pass {pass.Start:0.00}–{pass.End:0.00} s at ×{pass.Rate:0.##}, ended {pass.EndRaised}, seeking {pass.Seeking}, watched {pass.Watched}, failed {player.Failed}";
    }

    private void WatchPasses()
    {
        if (watchingPasses) return;
        CompositionTarget.Rendering += OnPassFrame;
        watchingPasses = true;
    }

    // Every refresh while a trimmed video plays: has it reached its out point?
    private void OnPassFrame(object? sender, EventArgs e)
    {
        var watching = false;
        foreach (var (key, player) in players.ToList())
        {
            if (!passes.TryGetValue(player, out var pass) || !pass.Watched || player.Failed || !player.Opened) continue;
            watching = true;
            if (!player.Playing || pass.EndRaised) continue;
            var position = player.MediaPlayer.Position.TotalSeconds;
            if (pass.Seeking)
            {
                if (position < pass.End - 0.1) pass.Seeking = false;
                continue;
            }
            if (PlaybackOptions.PassIsOver(position, pass.End, AssumedFrameDuration)) PassEnded(player, key);
        }
        if (watching) return;
        CompositionTarget.Rendering -= OnPassFrame;
        watchingPasses = false;
    }
}
