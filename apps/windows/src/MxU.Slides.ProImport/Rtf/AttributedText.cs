using System.Globalization;

namespace MxU.Slides.ProImport.Rtf;

/// <summary>An sRGB colour read from RTF, already converted the way Cocoa converts it (see <see cref="FromGenericRgb"/>).</summary>
internal readonly record struct RtfColor(double Red, double Green, double Blue, double Alpha = 1)
{
    private static readonly double[] GenericToSrgb =
    [
        1.0252524586, -0.0265475337, 0.0012950751,
        0.0193935069, 0.9480280089, 0.0325784842,
        -0.0017695316, -0.0014423226, 1.0032118541,
    ];

    /// <summary>
    /// Cocoa reads RTF colour-table entries as calibrated ("Generic") RGB, gamma 1.8, and
    /// the Mac mapper converts them to sRGB — so pure red in the table lands as #FF2600.
    /// </summary>
    public static RtfColor FromGenericRgb(double red, double green, double blue)
    {
        const double gamma = 461.0 / 256.0;
        var linear = new[] { red, green, blue }.Select(c => Math.Pow(Math.Clamp(c, 0, 1), gamma)).ToArray();
        double Row(int row) => Encode(
            GenericToSrgb[row * 3] * linear[0] + GenericToSrgb[row * 3 + 1] * linear[1] + GenericToSrgb[row * 3 + 2] * linear[2]);
        return new RtfColor(Row(0), Row(1), Row(2));
    }

    public static RtfColor FromGray(double white)
    {
        var value = Encode(Math.Pow(Math.Clamp(white, 0, 1), 2.2));
        return new RtfColor(value, value, value);
    }

    private static double Encode(double linear)
    {
        var c = Math.Clamp(linear, 0, 1);
        return c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
    }

    public string Hex
    {
        get
        {
            static int Channel(double value) => (int)Math.Round(Math.Clamp(value, 0, 1) * 255, MidpointRounding.AwayFromZero);
            return string.Create(CultureInfo.InvariantCulture,
                $"#{Channel(Red):X2}{Channel(Green):X2}{Channel(Blue):X2}{Channel(Alpha):X2}");
        }
    }
}

/// <summary>Cocoa's <c>NSParagraphStyle</c> indents, in points (tailIndent ≤ 0 is measured from the trailing edge).</summary>
internal sealed record RtfParagraphStyle(double FirstLineHeadIndent = 0, double HeadIndent = 0, double TailIndent = 0)
{
    public static readonly RtfParagraphStyle Default = new();
}

/// <summary>The attributes the mapper reads from <c>NSAttributedString(rtf:)</c>.</summary>
internal sealed record RtfAttributes(
    string FontName, double FontSize, RtfColor? Foreground, RtfColor? Background,
    double Kern, int Underline, int Strikethrough, RtfParagraphStyle Paragraph);

/// <summary>Plain text plus maximal runs of identical attributes, like an <c>NSAttributedString</c>.</summary>
internal sealed class AttributedText(string text, IReadOnlyList<AttributedText.Run> runs)
{
    public readonly record struct Run(int Start, int Length, RtfAttributes Attributes)
    {
        public int End => Start + Length;
    }

    public string Text { get; } = text;

    public int Length => Text.Length;

    public IReadOnlyList<Run> Runs { get; } = runs;

    public static AttributedText? FromRtf(ReadOnlySpan<byte> data) => RtfReader.Read(data);

    public RtfAttributes AttributesAt(int index)
    {
        int low = 0, high = Runs.Count - 1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (index < Runs[mid].Start) high = mid - 1;
            else if (index >= Runs[mid].End) low = mid + 1;
            else return Runs[mid].Attributes;
        }
        throw new ArgumentOutOfRangeException(nameof(index));
    }

    /// <summary>The runs overlapping a range, clipped to it.</summary>
    public IEnumerable<Run> RunsIn(int start, int length)
    {
        var end = start + length;
        foreach (var run in Runs)
        {
            if (run.End <= start || run.Start >= end) continue;
            var clippedStart = Math.Max(run.Start, start);
            yield return new Run(clippedStart, Math.Min(run.End, end) - clippedStart, run.Attributes);
        }
    }

    /// <summary>Maximal ranges over which one attribute keeps the same value (<c>enumerateAttribute</c>).</summary>
    public IEnumerable<(int Start, int Length, T Value)> Ranges<T>(Func<RtfAttributes, T> select)
    {
        var comparer = EqualityComparer<T>.Default;
        int? start = null;
        T current = default!;
        var end = 0;
        foreach (var run in Runs)
        {
            var value = select(run.Attributes);
            if (start is not null && comparer.Equals(value, current))
            {
                end = run.End;
                continue;
            }
            if (start is { } open) yield return (open, end - open, current);
            start = run.Start;
            end = run.End;
            current = value;
        }
        if (start is { } last) yield return (last, end - last, current);
    }
}
