using System.Globalization;
using System.Text;
using Point = MxU.Slides.Core.Scene.EditorGeometry.Point;

namespace MxU.Slides.Core.Scene;

/// <summary>
/// One point on a pen-drawn path: where it is, plus the two Bézier handles
/// that bend the curve arriving at it (<see cref="HandleIn"/>) and leaving it
/// (<see cref="HandleOut"/>). No handles means a sharp corner joined by
/// straight lines. Port of SlideScene/PathAnchors.swift.
/// </summary>
public readonly record struct PathAnchor(Point Point, Point? HandleIn = null, Point? HandleOut = null);

/// <summary>
/// Reads and writes the small SVG path language masks and custom shapes are
/// stored in ("M 0.25 0 L 0.75 0 C … Z"), as a list of <see cref="PathAnchor"/>s
/// that an editor can drag about. Only one sub-path is understood (a second
/// "M" is refused rather than flattened), and arcs aren't. Port of
/// SlideScene/PathAnchors.swift's PathAnchorCodec.
/// </summary>
public static class PathAnchorCodec
{
    /// <summary>
    /// Turns path data into anchors. The data's numbers are in unit space
    /// (0–1 across and down <paramref name="frame"/>); the anchors come back
    /// in <paramref name="frame"/>'s coordinates. Null when the data can't be
    /// read, has fewer than two points, or has more than one sub-path.
    /// </summary>
    public static (List<PathAnchor> Anchors, bool Closed)? Parse(string data, SceneRect frame)
    {
        var scanner = new Scanner(data);
        var anchors = new List<PathAnchor>();
        var closed = false;
        var sawMove = false;
        char? lastCommand = null;

        Point Absolute(double x, double y, Point? current, bool relative) =>
            relative && current is { } c
                ? new Point(c.X + x * frame.Width, c.Y + y * frame.Height)
                : new Point(frame.X + x * frame.Width, frame.Y + y * frame.Height);

        Point? ScanPoint(Point? current, bool relative) =>
            scanner.ScanDouble() is { } x && scanner.ScanDouble() is { } y ? Absolute(x, y, current, relative) : null;

        while (!scanner.IsAtEnd)
        {
            char command;
            if (scanner.ScanCharacter() is { } letter && char.IsLetter(letter))
            {
                command = letter;
            }
            else if (lastCommand is { } last)
            {
                // A number with no letter in front repeats the last command (after an M, it means L).
                scanner.StepBack();
                command = last == 'M' ? 'L' : last == 'm' ? 'l' : last;
            }
            else
            {
                return null;
            }
            lastCommand = command;
            var relative = char.IsLower(command);
            Point? current = anchors.Count > 0 ? anchors[^1].Point : null;

            switch (char.ToUpperInvariant(command))
            {
                case 'M':
                {
                    if (sawMove) return null;
                    sawMove = true;
                    if (ScanPoint(current, relative) is not { } p) return null;
                    anchors.Add(new PathAnchor(p));
                    break;
                }
                case 'L':
                {
                    if (ScanPoint(current, relative) is not { } p) return null;
                    anchors.Add(new PathAnchor(p));
                    break;
                }
                case 'H':
                {
                    if (scanner.ScanDouble() is not { } x || current is not { } c) return null;
                    anchors.Add(new PathAnchor(relative ? c with { X = c.X + x * frame.Width } : c with { X = frame.X + x * frame.Width }));
                    break;
                }
                case 'V':
                {
                    if (scanner.ScanDouble() is not { } y || current is not { } c) return null;
                    anchors.Add(new PathAnchor(relative ? c with { Y = c.Y + y * frame.Height } : c with { Y = frame.Y + y * frame.Height }));
                    break;
                }
                case 'C':
                {
                    if (ScanPoint(current, relative) is not { } c1 || ScanPoint(current, relative) is not { } c2
                        || ScanPoint(current, relative) is not { } p || anchors.Count == 0)
                        return null;
                    anchors[^1] = anchors[^1] with { HandleOut = c1 };
                    anchors.Add(new PathAnchor(p, HandleIn: c2));
                    break;
                }
                case 'Q':
                {
                    // A quadratic curve becomes the exact cubic with handles two thirds of the way to its control point.
                    if (ScanPoint(current, relative) is not { } q || ScanPoint(current, relative) is not { } p
                        || current is not { } p0 || anchors.Count == 0)
                        return null;
                    var c1 = new Point(p0.X + (q.X - p0.X) * 2 / 3, p0.Y + (q.Y - p0.Y) * 2 / 3);
                    var c2 = new Point(p.X + (q.X - p.X) * 2 / 3, p.Y + (q.Y - p.Y) * 2 / 3);
                    anchors[^1] = anchors[^1] with { HandleOut = c1 };
                    anchors.Add(new PathAnchor(p, HandleIn: c2));
                    break;
                }
                case 'Z':
                    closed = true;
                    break;
                default:
                    return null; // arcs and smooth-curve shorthands aren't understood
            }
        }
        if (anchors.Count < 2) return null;

        // A closed path that returns to its start by an explicit last point: drop the duplicate,
        // keeping the curve that arrives there on the first anchor.
        if (closed && anchors.Count > 2)
        {
            var first = anchors[0];
            var last = anchors[^1];
            var tolerance = Math.Max(frame.Width, frame.Height) * 0.002;
            if (Math.Abs(last.Point.X - first.Point.X) <= tolerance && Math.Abs(last.Point.Y - first.Point.Y) <= tolerance)
            {
                anchors[0] = first with { HandleIn = last.HandleIn };
                anchors.RemoveAt(anchors.Count - 1);
            }
        }
        return (anchors, closed);
    }

    /// <summary>
    /// Writes anchors as path data in the unit space of their own bounding box,
    /// handles included, and returns that box (whole numbers, at least 1×1) as the
    /// frame to place it in. Null for fewer than two anchors.
    /// </summary>
    public static (string PathData, SceneRect Frame)? Encode(IReadOnlyList<PathAnchor> anchors, bool closed)
    {
        if (anchors.Count < 2) return null;
        var points = new List<Point>();
        foreach (var anchor in anchors)
        {
            points.Add(anchor.Point);
            if (anchor.HandleIn is { } hIn) points.Add(hIn);
            if (anchor.HandleOut is { } hOut) points.Add(hOut);
        }
        var minX = points.Min(p => p.X);
        var minY = points.Min(p => p.Y);
        var bounds = Integral(new SceneRect(minX, minY,
            Math.Max(points.Max(p => p.X) - minX, 1), Math.Max(points.Max(p => p.Y) - minY, 1)));
        string Unit(Point p) => $"{Format((p.X - bounds.X) / bounds.Width)} {Format((p.Y - bounds.Y) / bounds.Height)}";
        return (Write(anchors, closed, Unit), bounds);
    }

    /// <summary>
    /// Writes anchors that are already in unit space (an output mask drawn over
    /// the whole screen) as they are, without fitting them to their own box.
    /// Null for fewer than two anchors.
    /// </summary>
    public static string? EncodeAbsolute(IReadOnlyList<PathAnchor> anchors, bool closed) =>
        anchors.Count < 2 ? null : Write(anchors, closed, p => $"{Format(p.X)} {Format(p.Y)}");

    private static string Write(IReadOnlyList<PathAnchor> anchors, bool closed, Func<Point, string> unit)
    {
        var output = new StringBuilder("M ").Append(unit(anchors[0].Point));
        var segments = closed ? anchors.Count : anchors.Count - 1;
        for (var i = 0; i < segments; i++)
        {
            var from = anchors[i];
            var to = anchors[(i + 1) % anchors.Count];
            if (from.HandleOut is null && to.HandleIn is null)
                output.Append(" L ").Append(unit(to.Point));
            else
                output.Append(" C ").Append(unit(from.HandleOut ?? from.Point))
                    .Append(' ').Append(unit(to.HandleIn ?? to.Point))
                    .Append(' ').Append(unit(to.Point));
        }
        if (closed) output.Append(" Z");
        return output.ToString();
    }

    /// <summary>Swift's String(format: "%.4f"): four decimals, always a dot.</summary>
    private static string Format(double value) => value.ToString("F4", CultureInfo.InvariantCulture);

    /// <summary>CGRect.integral: the smallest whole-number rectangle that contains it.</summary>
    private static SceneRect Integral(SceneRect r)
    {
        var x = Math.Floor(r.X);
        var y = Math.Floor(r.Y);
        return new SceneRect(x, y, Math.Ceiling(r.Right) - x, Math.Ceiling(r.Bottom) - y);
    }

    /// <summary>
    /// Reads path data a token at a time like Foundation's Scanner with spaces,
    /// newlines and commas skipped: one character, or one number.
    /// </summary>
    private sealed class Scanner(string text)
    {
        private int index;
        private int lastStart;

        public bool IsAtEnd
        {
            get
            {
                Skip();
                return index >= text.Length;
            }
        }

        public char? ScanCharacter()
        {
            Skip();
            if (index >= text.Length) return null;
            lastStart = index;
            return text[index++];
        }

        /// <summary>Puts back the character <see cref="ScanCharacter"/> just read.</summary>
        public void StepBack() => index = lastStart;

        public double? ScanDouble()
        {
            Skip();
            var start = index;
            var i = index;
            if (i < text.Length && text[i] is '+' or '-') i++;
            var digits = 0;
            while (i < text.Length && char.IsAsciiDigit(text[i])) { i++; digits++; }
            if (i < text.Length && text[i] == '.')
            {
                i++;
                while (i < text.Length && char.IsAsciiDigit(text[i])) { i++; digits++; }
            }
            if (digits == 0) return null;
            // An exponent counts only when digits follow it ("1e5", "2E-3"); "1e" stops before the e.
            if (i < text.Length && text[i] is 'e' or 'E')
            {
                var j = i + 1;
                if (j < text.Length && text[j] is '+' or '-') j++;
                if (j < text.Length && char.IsAsciiDigit(text[j]))
                {
                    while (j < text.Length && char.IsAsciiDigit(text[j])) j++;
                    i = j;
                }
            }
            if (!double.TryParse(text.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return null;
            index = i;
            return value;
        }

        private void Skip()
        {
            while (index < text.Length && (char.IsWhiteSpace(text[index]) || text[index] == ',')) index++;
        }
    }
}
