using System.Globalization;
using Point = MxU.Slides.Core.Scene.EditorGeometry.Point;

namespace MxU.Slides.Core.Scene;

/// <summary>One drawing step of a path: move, line, quadratic or cubic curve, or close.</summary>
public abstract record SvgPathElement
{
    private SvgPathElement() { }

    public sealed record MoveTo(Point To) : SvgPathElement;
    public sealed record LineTo(Point To) : SvgPathElement;
    public sealed record QuadTo(Point Control, Point To) : SvgPathElement;
    public sealed record CubicTo(Point Control1, Point Control2, Point To) : SvgPathElement;
    public sealed record Close : SvgPathElement;
}

/// <summary>
/// Reads the small SVG path language shapes, masks and text paths are stored in
/// ("M 0 0.5 L 1 0.5", "M 0.5 0 C … Z"). The numbers are in unit space (0–1 across and
/// down a frame); the elements come back in the frame's own coordinates. Understands
/// M L H V C Q Z, upper case (absolute) and lower case (relative), and a bare number
/// repeating the last command. Port of the Mac's SVGPathParser (RenderEngine/ShapeRasterizer.swift).
/// </summary>
public static class SvgPath
{
    /// <summary>The path's elements in <paramref name="frame"/>, or null when the data can't be read or draws nothing.</summary>
    public static List<SvgPathElement>? Parse(string? data, SceneRect frame)
    {
        if (string.IsNullOrEmpty(data)) return null;
        var scanner = new Scanner(data);
        var elements = new List<SvgPathElement>();
        var current = new Point(0, 0);
        var subpathStart = current;
        char? lastCommand = null;

        // Relative numbers are offsets scaled by the frame, as on the Mac.
        Point At(double x, double y, bool relative) => relative
            ? new Point(current.X + x * frame.Width, current.Y + y * frame.Height)
            : new Point(frame.X + x * frame.Width, frame.Y + y * frame.Height);

        while (!scanner.IsAtEnd)
        {
            char command;
            if (scanner.ScanCharacter() is { } letter && char.IsLetter(letter))
            {
                command = letter;
            }
            else if (lastCommand is { } last)
            {
                // A number with no letter repeats the last command (after an M it means L).
                scanner.StepBack();
                command = last == 'M' ? 'L' : last == 'm' ? 'l' : last;
            }
            else
            {
                return null;
            }
            lastCommand = command;
            var relative = char.IsLower(command);

            switch (char.ToUpperInvariant(command))
            {
                case 'M':
                    if (scanner.ScanDouble() is not { } mx || scanner.ScanDouble() is not { } my) return null;
                    current = At(mx, my, relative);
                    subpathStart = current;
                    elements.Add(new SvgPathElement.MoveTo(current));
                    break;
                case 'L':
                    if (scanner.ScanDouble() is not { } lx || scanner.ScanDouble() is not { } ly) return null;
                    current = At(lx, ly, relative);
                    elements.Add(new SvgPathElement.LineTo(current));
                    break;
                case 'H':
                    if (scanner.ScanDouble() is not { } hx) return null;
                    current = current with { X = relative ? current.X + hx * frame.Width : frame.X + hx * frame.Width };
                    elements.Add(new SvgPathElement.LineTo(current));
                    break;
                case 'V':
                    if (scanner.ScanDouble() is not { } vy) return null;
                    current = current with { Y = relative ? current.Y + vy * frame.Height : frame.Y + vy * frame.Height };
                    elements.Add(new SvgPathElement.LineTo(current));
                    break;
                case 'C':
                {
                    if (scanner.ScanDouble() is not { } x1 || scanner.ScanDouble() is not { } y1
                        || scanner.ScanDouble() is not { } x2 || scanner.ScanDouble() is not { } y2
                        || scanner.ScanDouble() is not { } x || scanner.ScanDouble() is not { } y)
                        return null;
                    var c1 = At(x1, y1, relative);
                    var c2 = At(x2, y2, relative);
                    current = At(x, y, relative);
                    elements.Add(new SvgPathElement.CubicTo(c1, c2, current));
                    break;
                }
                case 'Q':
                {
                    if (scanner.ScanDouble() is not { } x1 || scanner.ScanDouble() is not { } y1
                        || scanner.ScanDouble() is not { } x || scanner.ScanDouble() is not { } y)
                        return null;
                    var c = At(x1, y1, relative);
                    current = At(x, y, relative);
                    elements.Add(new SvgPathElement.QuadTo(c, current));
                    break;
                }
                case 'Z':
                    elements.Add(new SvgPathElement.Close());
                    current = subpathStart;
                    break;
                default:
                    return null; // arcs and the smooth-curve shorthands aren't understood (nor on the Mac)
            }
        }
        return elements.Count == 0 ? null : elements;
    }

    /// <summary>
    /// Reads path data a token at a time like Foundation's Scanner with spaces, newlines
    /// and commas skipped: one character, or one number.
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

/// <summary>The Mac's ready-made text paths (the inspector's Path menu), in unit space. Port of RenderEngine/PathPresets.swift.</summary>
public static class PathPresets
{
    public const string Ellipse =
        "M 0.5 0 C 0.77614 0 1 0.22386 1 0.5 " +
        "C 1 0.77614 0.77614 1 0.5 1 " +
        "C 0.22386 1 0 0.77614 0 0.5 " +
        "C 0 0.22386 0.22386 0 0.5 0 Z";

    public const string RectanglePerimeter =
        "M 0.15 0 L 0.85 0 Q 1 0 1 0.15 L 1 0.85 " +
        "Q 1 1 0.85 1 L 0.15 1 Q 0 1 0 0.85 L 0 0.15 Q 0 0 0.15 0 Z";

    public const string Line = "M 0 0.5 L 1 0.5";

    public const string Arc = "M 0 1 Q 0.5 0 1 1";
}
