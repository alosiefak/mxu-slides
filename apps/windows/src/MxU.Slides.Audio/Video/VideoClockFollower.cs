namespace MxU.Slides.Audio.Video;

/// <summary>
/// What the video player said at one moment: where its picture was in the file (seconds),
/// when that was read (<see cref="Core.Scene.ShowState.HostClock"/> seconds), whether it is
/// meant to be playing, and its play rate.
/// </summary>
public readonly record struct VideoClockSample(double Position, double HostTime, bool Playing, double Rate = 1);

/// <summary>
/// What the sound's own clock said: the file second it was playing at a host time, how fast it
/// moves (file seconds per second), and which <see cref="VideoSoundCommand.Generation"/> of
/// landing it belongs to (so a reading from before a jump is never mistaken for one after it).
/// </summary>
public readonly record struct SoundClockReading(double Position, double HostTime, double Speed, int Generation);

public enum VideoSoundStep
{
    /// <summary>Be silent and stay where you are (the video is paused, still opening, or stalled).</summary>
    Hold,

    /// <summary>Be at <see cref="VideoSoundCommand.Position"/> at <see cref="VideoSoundCommand.HostTime"/>, moving at the video's rate from there.</summary>
    Land,

    /// <summary>Keep playing, at <see cref="VideoSoundCommand.Speed"/> (a hair faster or slower to close a small gap).</summary>
    Steer,
}

/// <summary>One instruction for the sound. Each Hold and each Land gets a new <see cref="Generation"/>; a Steer carries its Land's.</summary>
public readonly record struct VideoSoundCommand(VideoSoundStep Step, double Position, double HostTime, double Speed, int Generation)
{
    public static readonly VideoSoundCommand Idle = new(VideoSoundStep.Hold, 0, 0, 1, 0);
}

/// <summary>
/// Keeps a video's sound with its picture. The picture is WPF's MediaPlayer, which plays on
/// its own clock and can't be told which sound device to use; so the sound plays separately
/// through our own engine, and this decides, each time the video's clock is read, what the
/// sound should do:
/// <list type="bullet">
/// <item>the picture isn't moving (paused, opening, buffering): <b>hold</b>, silent;</item>
/// <item>it starts moving, or jumps (a seek, a loop): <b>land</b> the sound exactly where the picture is;</item>
/// <item>it is moving and the sound is close: <b>steer</b>, playing up to 0.5% faster or slower
/// until the gap closes, so two clocks that drift apart (the video's and the sound card's)
/// never need a jump.</item>
/// </list>
/// The steering is the multitrack's timecode chase (MultitrackChase.swift) with
/// video-sized numbers: it lands sooner (a tenth of a second off is visible on a face) and
/// steers harder. A pure function of what it is told, so it is tested without devices.
/// <para>Threading: one caller at a time (the player calls it under its lock).</para>
/// </summary>
public sealed class VideoClockFollower
{
    /// <summary>Playing, but the picture hasn't moved for this long: treat it as stalled.</summary>
    public const double StallSeconds = 0.25;

    /// <summary>Sound this far from the picture, twice in a row, lands again.</summary>
    public const double LandSeconds = 0.1;

    /// <summary>Sound this far from the picture lands at once (the video was seeked or looped).</summary>
    public const double JumpSeconds = 0.4;

    // The steering: the error is smoothed over half a second, then a proportional and an
    // integral term pick the speed, never more than 0.5% off (a pitch change nobody hears).
    public const double Smoothing = 0.5;
    public const double Proportional = 0.5;
    public const double Integral = 0.0625;
    public const double MaxDeviation = 0.005;

    private VideoClockSample? previous;
    private bool moving;
    private double lastMove = double.NegativeInfinity;
    private bool sounding;
    private bool jumped;
    private int generation;
    private int strikes;
    private double? smoothed;
    private double? lastTime;
    private double drift;
    private double speed = 1;

    /// <summary>The sound minus the picture at the last reading (seconds; positive = the sound is ahead), or null.</summary>
    public double? LastError { get; private set; }

    /// <summary>True while the sound is meant to be playing.</summary>
    public bool IsSounding => sounding;

    /// <summary>How many times the sound has been landed (diagnostics).</summary>
    public int Landings { get; private set; }

    /// <summary>
    /// The video was seeked, restarted or looped: land again once it is seen moving from its
    /// new place (two readings after the jump), not before: a player often pauses a moment to
    /// buffer after a seek, and sound landed during that pause would run ahead of the picture.
    /// </summary>
    public void Discontinuity()
    {
        jumped = true;
        moving = false;
        previous = null;
    }

    /// <summary>The next instruction, given the video's clock and (when it is playing) the sound's.</summary>
    public VideoSoundCommand Step(VideoClockSample video, SoundClockReading? sound)
    {
        var rate = double.IsFinite(video.Rate) && video.Rate > 0 ? Math.Clamp(video.Rate, 0.25, 4) : 1;
        if (previous is { } last)
        {
            var elapsed = video.HostTime - last.HostTime;
            var advanced = video.Position - last.Position;
            if (advanced > 0 && elapsed > 0 && advanced <= elapsed * rate + StallSeconds)
            {
                // It moved about as far as time passed: the picture is running.
                moving = true;
                lastMove = video.HostTime;
            }
            else if (Math.Abs(advanced) > 1e-6)
            {
                // It moved backwards, or much further than time allows: a seek or a loop.
                Discontinuity();
            }
        }
        previous = video;
        if (!video.Playing) moving = false;

        var running = video.Playing && moving && video.HostTime - lastMove <= StallSeconds;
        if (!running) return Hold();
        if (!sounding || jumped) return Land(video, rate);
        if (sound is not { } heard || heard.Generation != generation)
        {
            // Nothing heard since the last landing yet: carry on.
            return new VideoSoundCommand(VideoSoundStep.Steer, 0, 0, speed, generation);
        }

        var error = heard.Position + (video.HostTime - heard.HostTime) * heard.Speed - video.Position;
        LastError = error;
        if (Math.Abs(error) > JumpSeconds) return Land(video, rate);
        if (Math.Abs(error) > LandSeconds)
        {
            if (++strikes >= 2) return Land(video, rate);
        }
        else
        {
            strikes = 0;
        }
        var dt = lastTime is { } then ? Math.Clamp(video.HostTime - then, 0, 1) : 0;
        lastTime = video.HostTime;
        var held = smoothed is { } s ? s + (error - s) * dt / (Smoothing + dt) : error;
        smoothed = held;
        var wanted = -(Proportional * held + drift);
        var limited = Math.Clamp(wanted, -MaxDeviation, MaxDeviation);
        // Only integrate while not pinned at the limit, so the drift term can't wind up.
        if (wanted == limited) drift += Integral * held * dt;
        speed = rate * (1 + limited);
        return new VideoSoundCommand(VideoSoundStep.Steer, 0, 0, speed, generation);
    }

    private VideoSoundCommand Hold()
    {
        if (sounding)
        {
            sounding = false;
            generation++;
        }
        ResetSteering();
        return new VideoSoundCommand(VideoSoundStep.Hold, 0, 0, 1, generation);
    }

    private VideoSoundCommand Land(VideoClockSample video, double rate)
    {
        sounding = true;
        jumped = false;
        generation++;
        Landings++;
        ResetSteering();
        speed = rate;
        return new VideoSoundCommand(VideoSoundStep.Land, video.Position, video.HostTime, rate, generation);
    }

    private void ResetSteering()
    {
        strikes = 0;
        smoothed = null;
        lastTime = null;
        drift = 0;
    }
}
