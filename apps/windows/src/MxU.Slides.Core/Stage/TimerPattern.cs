using System.Globalization;
using System.Text;

namespace MxU.Slides.Core.Stage;

/// <summary>
/// Custom timer read-outs. Lower-case d h m s roll over (seconds in the minute),
/// upper-case H M S are totals, f is tenths/hundredths/thousandths, doubling pads,
/// and quotes fence literal text. Port of SlideScene/TimerPattern.swift.
/// </summary>
public static class TimerPattern
{
    public static string Text(double seconds, string pattern)
    {
        var magnitude = Math.Abs(seconds);
        var whole = (long)Math.Truncate(magnitude);
        var body = new StringBuilder();
        foreach (var piece in Pieces(pattern))
            body.Append(piece.Literal ?? Field(piece.Letter, piece.Width, whole, magnitude));

        var negative = seconds < 0 && (whole > 0 || HasFraction(pattern));
        return negative ? "-" + body : body.ToString();
    }

    public static bool HasFraction(string pattern) => Pieces(pattern).Any(p => p.Literal is null && p.Letter == 'f');

    private readonly record struct Piece(string? Literal, char Letter = '\0', int Width = 0);

    private const string TokenLetters = "dhmsHMSf";

    private static List<Piece> Pieces(string pattern)
    {
        var pieces = new List<Piece>();
        var literal = new StringBuilder();
        var quoted = false;
        var index = 0;
        while (index < pattern.Length)
        {
            var c = pattern[index];
            var next = index + 1;
            if (c == '\'')
            {
                if (next < pattern.Length && pattern[next] == '\'')
                {
                    literal.Append('\'');
                    index = next + 1;
                }
                else
                {
                    quoted = !quoted;
                    index = next;
                }
            }
            else if (quoted || !TokenLetters.Contains(c))
            {
                literal.Append(c);
                index = next;
            }
            else
            {
                if (literal.Length > 0)
                {
                    pieces.Add(new Piece(literal.ToString()));
                    literal.Clear();
                }
                var end = index;
                while (end < pattern.Length && pattern[end] == c) end++;
                pieces.Add(new Piece(null, c, end - index));
                index = end;
            }
        }
        if (literal.Length > 0) pieces.Add(new Piece(literal.ToString()));
        return pieces;
    }

    private static string Field(char letter, int width, long whole, double magnitude) => letter switch
    {
        'd' => Padded(whole / 86400, width),
        'h' => Padded(whole / 3600 % 24, width),
        'm' => Padded(whole / 60 % 60, width),
        's' => Padded(whole % 60, width),
        'H' => Padded(whole / 3600, width),
        'M' => Padded(whole / 60, width),
        'S' => Padded(whole, width),
        'f' => Fraction(magnitude - whole, Math.Min(width, 3)),
        _ => "",
    };

    private static string Padded(long value, int width) =>
        width > 1 ? value.ToString("D" + width, CultureInfo.InvariantCulture) : value.ToString(CultureInfo.InvariantCulture);

    private static string Fraction(double part, int digits)
    {
        var scale = (long)Math.Pow(10, digits);
        var value = Math.Min(scale - 1, (long)Math.Truncate(part * scale + 1e-6));
        return value.ToString("D" + digits, CultureInfo.InvariantCulture);
    }
}
