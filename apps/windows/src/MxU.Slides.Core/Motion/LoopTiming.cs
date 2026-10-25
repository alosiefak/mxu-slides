namespace MxU.Slides.Core.Motion;

/// <summary>
/// When each frame of a loop happens. A motion background isn't recorded in real time; each
/// frame is drawn on its own at an exact moment, so a slow PC makes exactly the same video as a
/// fast one, just more slowly:
/// <list type="bullet">
/// <item>a loop of S seconds at F frames a second has S × F frames, numbered 0 to S × F − 1;</item>
/// <item>frame i is drawn at loop position i ÷ (S × F) (<see cref="Phase"/>), and is stamped
/// i ÷ F seconds into the file (<see cref="TimeHns"/>);</item>
/// <item>frame S × F would be at position 1, which is the same as 0: the frame after the last is
/// the first. That's what makes it seamless when a player jumps from the end back to the start.</item>
/// </list>
/// </summary>
public static class LoopTiming
{
    /// <summary>Media Foundation's clock: 100-nanosecond units, ten million a second.</summary>
    public const long TicksPerSecond = 10_000_000;

    public static int FrameCount(int seconds, int frameRate) => Math.Max(1, seconds * frameRate);

    /// <summary>Where frame <paramref name="frame"/> sits in a loop of <paramref name="frameCount"/> frames (0 ≤ phase &lt; 1; any frame number, wrapped).</summary>
    public static double Phase(long frame, int frameCount)
    {
        var wrapped = ((frame % frameCount) + frameCount) % frameCount;
        return (double)wrapped / frameCount;
    }

    /// <summary>
    /// Frame <paramref name="frame"/>'s time stamp in the file, rounded to the nearest tick
    /// with whole-number maths, so stamps never drift over a long loop (at 30 fps a frame is
    /// 333 333⅓ ticks: they go 0, 333 333, 666 667, 1 000 000…).
    /// </summary>
    public static long TimeHns(long frame, int frameRate) =>
        (frame * 2 * TicksPerSecond + frameRate) / (2L * frameRate);

    /// <summary>How long frame <paramref name="frame"/> is shown: up to the next one's stamp.</summary>
    public static long DurationHns(long frame, int frameRate) => TimeHns(frame + 1, frameRate) - TimeHns(frame, frameRate);

    /// <summary>
    /// The H.264 bitrate a motion background is written at: about 0.2 bits per pixel each frame,
    /// generous because smooth gradients are what video compression handles worst (too few
    /// bits and a soft glow turns into rings). 12 Mbit/s for 1080p30, 60 for 4K60.
    /// </summary>
    public static int Bitrate(int width, int height, int frameRate) =>
        (int)Math.Clamp(width * (long)height * frameRate * 0.2, 2_000_000, 60_000_000);
}
