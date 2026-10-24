namespace MxU.Slides.Broadcast.Media;

/// <summary>
/// Decides what a replay does with each decoded picture once its time has come: show it, drop it
/// (it's late, and showing it would put the pictures behind the sound), or skip ahead.
/// <para>
/// The Mac drops any picture more than a quarter of a second late and nothing more: its decoder
/// is the Mac's hardware one, which always keeps up. A PC may decode in software, slower than
/// the file plays. Dropping alone can't catch up then (a dropped picture has still been decoded),
/// so the pictures would fall further behind and every one would be dropped: a frozen stream.
/// Two Windows rules keep pictures coming while the sound plays on time:
/// </para>
/// <list type="bullet">
/// <item>at least one picture every half second is shown, even a late one;</item>
/// <item>more than a second behind, the reader jumps to the present (<see cref="SkipTo"/>),
/// and doesn't jump again until it has decoded up to where it jumped.</item>
/// </list>
/// Times are seconds: <c>now</c> and <c>due</c> on the host clock, <c>position</c> in the file.
/// </summary>
public sealed class ReplayPacer
{
    /// <summary>A picture later than this is dropped (the Mac's lateFrameThreshold).</summary>
    public const double LateFrameThreshold = 0.25;

    /// <summary>The longest the replay goes without showing a picture.</summary>
    public const double LongestWithoutAPicture = 0.5;

    /// <summary>A picture this late means the decoder can't keep up: skip ahead.</summary>
    public const double SkipAheadAfter = 1.0;

    /// <summary>How far past the present a skip aims, since decoding up to it takes time too.</summary>
    public const double SkipLead = 0.25;

    public enum Decision
    {
        Show,
        Drop,
        SkipAhead,
    }

    private double lastShown = double.NaN;
    private double skippingTo = double.NaN;

    /// <summary>Where in the file to jump after <see cref="Decision.SkipAhead"/>.</summary>
    public double SkipTo { get; private set; }

    /// <summary>How many times the replay has skipped ahead.</summary>
    public int Skips { get; private set; }

    public Decision Decide(double now, double due, double position)
    {
        var behind = now - due;
        // After a jump the reader restarts at the key frame before it: those pictures are late, but
        // jumping again would land on the same key frame for ever.
        var catchingUp = !double.IsNaN(skippingTo) && position < skippingTo;
        if (!catchingUp) skippingTo = double.NaN;
        if (behind > SkipAheadAfter && !catchingUp)
        {
            SkipTo = position + behind + SkipLead;
            skippingTo = SkipTo;
            Skips++;
            return Decision.SkipAhead;
        }
        if (behind <= LateFrameThreshold || double.IsNaN(lastShown) || now - lastShown >= LongestWithoutAPicture)
        {
            lastShown = now;
            return Decision.Show;
        }
        return Decision.Drop;
    }
}
