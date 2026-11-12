namespace MxU.Slides.Core.Media;

/// <summary>
/// Notices a run of something going wrong and reports it once, when it ends: how long the
/// picture was black, how long a live input gave no frames. Feed it every picture with whether
/// the problem holds; while it holds nothing is reported (a diagnostics line per frame would
/// flood the log), and the first good picture after it hands back when the run started and how
/// long it lasted. Not thread-safe: one per picture source. Port of the Mac's
/// RenderEngine/DropoutWatch.swift.
/// </summary>
public sealed class DropoutWatch
{
    private double? since;

    /// <param name="holds">True while the problem is there (this picture is black).</param>
    /// <param name="seconds">This picture's time, on any clock that only moves forward.</param>
    /// <returns>The run that just ended (its start and length), or null.</returns>
    public (double Start, double Seconds)? Update(bool holds, double seconds)
    {
        if (holds)
        {
            since ??= seconds;
            return null;
        }
        if (since is not { } start) return null;
        since = null;
        return (start, seconds - start);
    }
}
