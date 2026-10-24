namespace MxU.Slides.VideoIO.Capture;

/// <summary>
/// Which of a camera's formats to capture in, and at what rate. A camera lists many
/// formats (size × pixel type × frame rate); the app keeps the size the device starts in
/// and picks the rate: the operator's preferred rate if the device offers it, else the
/// best "automatic" one. Automatic prefers the 30 class (29.97/30, the rate the show
/// runs at), then the fastest class up to 60, then the fractional broadcast rate
/// (29.97 over 30), then the pixel type the device already uses, then the earlier format.
/// Rates above 60 are ignored (they cost bandwidth the show can't use).
/// Port of MediaEngine/CaptureFormatChoice.swift.
/// </summary>
/// <param name="Index">Position of the chosen format in the device's list.</param>
public sealed record CaptureFormatChoice(int Index, double FrameRate)
{
    /// <summary>
    /// One format a device offers. <see cref="Subtype"/> is its pixel type as a FourCC
    /// ('NV12', 'YUY2', 'MJPG'); Windows lists one rate per format, the Mac a range, so
    /// <see cref="FrameRates"/> usually has one entry here.
    /// </summary>
    public sealed record Candidate(int Index, int Width, int Height, uint Subtype, IReadOnlyList<double> FrameRates);

    internal const double Ceiling = 60.5;

    /// <summary>
    /// Inputs on the same device must share one rate (a device runs one format at a time):
    /// the first input that chose a rate sets it for the device.
    /// </summary>
    public static IReadOnlyDictionary<string, double> SharedRates(IEnumerable<(string Device, double? Rate)> picks)
    {
        var shared = new Dictionary<string, double>();
        foreach (var (device, rate) in picks)
            if (rate is { } value) shared.TryAdd(device, value);
        return shared;
    }

    /// <summary>The rates the operator can choose for the device's current size, rounded to 0.01, slowest first.</summary>
    public static IReadOnlyList<double> Rates(IEnumerable<Candidate> candidates, Candidate active) =>
        [.. candidates
            .Where(c => c.Width == active.Width && c.Height == active.Height)
            .SelectMany(c => c.FrameRates)
            .Where(rate => rate > 0 && rate <= Ceiling)
            .Select(rate => Math.Round(rate * 100, MidpointRounding.AwayFromZero) / 100)
            .Distinct()
            .Order()];

    /// <summary>The format to switch to, or null when nothing at the current size has a usable rate.</summary>
    public static CaptureFormatChoice? Best(IReadOnlyList<Candidate> candidates, Candidate active, double? preferred = null)
    {
        var ranked = candidates
            .Where(c => c.Width == active.Width && c.Height == active.Height)
            .SelectMany(c => c.FrameRates.Where(rate => rate > 0 && rate <= Ceiling).Select(rate => new Ranked(c, rate, active)))
            .ToList();
        var picked = preferred is { } wanted ? ranked.Where(entry => Math.Abs(entry.FrameRate - wanted) < 0.01).ToList() : [];
        Ranked? best = null;
        foreach (var entry in picked.Count > 0 ? picked : ranked)
            if (best is null || Compare(best.Key, entry.Key) < 0) best = entry;
        return best is null ? null : new CaptureFormatChoice(best.Candidate.Index, best.FrameRate);
    }

    /// <summary>A Windows format subtype ("NV12", "MJPG") packed like a FourCC: first letter in the low byte.</summary>
    public static uint FourCC(string subtype)
    {
        uint code = 0;
        for (var i = 0; i < 4; i++) code |= (uint)(i < subtype.Length ? (byte)subtype[i] : (byte)' ') << (8 * i);
        return code;
    }

    /// <summary>A candidate from a Windows MediaFrameFormat's numbers (frame rate as numerator/denominator).</summary>
    public static Candidate CandidateFor(int index, int width, int height, string subtype, uint frameRateNumerator, uint frameRateDenominator) =>
        new(index, width, height, FourCC(subtype), frameRateDenominator == 0 ? [] : [(double)frameRateNumerator / frameRateDenominator]);

    /// <summary>Compares the rank keys element by element, like Swift's lexicographicallyPrecedes.</summary>
    private static int Compare(int[] left, int[] right)
    {
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
            if (left[i] != right[i]) return left[i].CompareTo(right[i]);
        return left.Length.CompareTo(right.Length);
    }

    private sealed class Ranked(Candidate candidate, double frameRate, Candidate active)
    {
        public Candidate Candidate { get; } = candidate;
        public double FrameRate { get; } = frameRate;

        public int[] Key { get; } = Rank(candidate, frameRate, active);

        private static int[] Rank(Candidate candidate, double frameRate, Candidate active)
        {
            var rateClass = (int)Math.Round(frameRate, MidpointRounding.AwayFromZero);
            return
            [
                rateClass == 30 ? 1 : 0,
                rateClass,
                frameRate < rateClass ? 1 : 0,
                candidate.Subtype == active.Subtype ? 1 : 0,
                -candidate.Index,
            ];
        }
    }
}
