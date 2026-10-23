using System.Text;

namespace MxU.Slides.ProImport.Rtf;

/// <summary>
/// Reads the RTF that ProPresenter stores for text boxes into an <see cref="AttributedText"/>,
/// matching what <c>NSAttributedString(rtf:)</c> gives the Mac mapper: <c>\par</c> becomes "\n",
/// <c>\line</c> U+2028, fonts come from the font table, sizes from <c>\fs</c>, colours from the
/// colour tables, plus kerning, underline, strikethrough, background and paragraph indents.
/// </summary>
internal static class RtfReader
{
    public static AttributedText? Read(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return null;
        var start = 0;
        while (start < data.Length && data[start] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') start++;
        if (!data[start..].StartsWith("{\\rtf"u8)) return null;
        return new Parser(data[start..].ToArray()).Parse();
    }

    private enum Destination { Text, Skip, FontTable, ColorTable, ExpandedColorTable }

    private enum ColorSpace { None, GenericRgb, Srgb, Gray }

    private struct GroupState
    {
        public Destination Destination;
        public int Font;
        public double FontSize;
        public int Foreground;
        public int Background;
        public double Kern;
        public int Underline;
        public int Strikethrough;
        public double LeftIndent;
        public double FirstIndent;
        public double RightIndent;
        public int UnicodeSkip;
    }

    private readonly record struct CharAttributes(
        int Font, double FontSize, int Foreground, int Background, double Kern, int Underline, int Strikethrough);

    private sealed class Parser(byte[] data)
    {
        private readonly Stack<GroupState> stack = new();
        private GroupState state = new() { Destination = Destination.Text, Font = -1, FontSize = 12, UnicodeSkip = 1 };
        private readonly StringBuilder text = new();
        private readonly List<(int Start, int Length, CharAttributes Attributes)> charRuns = [];
        private readonly List<(int Start, int End, RtfParagraphStyle Style)> paragraphs = [];
        private int paragraphStart;
        private (double Left, double First, double Right) trailingIndents;

        private readonly Dictionary<int, (string Name, int Charset)> fonts = [];
        private int defaultFont = -1;
        private int codePage = 1252;
        private int fontEntry = -1;
        private int fontCharset;
        private readonly StringBuilder fontName = new();

        private readonly List<RtfColor?> colors = [];
        private int? red, green, blue;
        private readonly List<RtfColor?> expandedColors = [];
        private ColorSpace space;
        private readonly List<double> components = [];

        private readonly List<byte> pendingBytes = [];
        private bool ignorableNext;
        private int fallbackToSkip;
        private int position;

        public AttributedText Parse()
        {
            while (position < data.Length)
            {
                var b = data[position];
                if (fallbackToSkip > 0 && b is not ((byte)'{' or (byte)'}'))
                {
                    SkipFallback();
                    continue;
                }
                fallbackToSkip = 0;
                switch (b)
                {
                    case (byte)'{':
                        Flush();
                        stack.Push(state);
                        position++;
                        break;
                    case (byte)'}':
                        Flush();
                        if (stack.Count == 0)
                        {
                            position = data.Length;
                            break;
                        }
                        state = stack.Pop();
                        position++;
                        break;
                    case (byte)'\\':
                        Control();
                        break;
                    case (byte)'\r' or (byte)'\n':
                        position++;
                        break;
                    default:
                        position++;
                        if (b >= 0x80)
                        {
                            pendingBytes.Add(b);
                        }
                        else
                        {
                            Flush();
                            Literal((char)b);
                        }
                        break;
                }
            }
            Flush();
            if (paragraphStart < text.Length) paragraphs.Add((paragraphStart, text.Length, StyleOf(trailingIndents)));
            return Build();
        }

        private void SkipFallback()
        {
            var b = data[position];
            if (b is (byte)'\r' or (byte)'\n')
            {
                position++;
                return;
            }
            fallbackToSkip--;
            if (b != (byte)'\\')
            {
                position++;
                return;
            }
            if (position + 1 < data.Length && data[position + 1] == (byte)'\'')
            {
                position = Math.Min(data.Length, position + 4);
                return;
            }
            ReadControl(out _, out _);
        }

        private void Control()
        {
            if (position + 1 >= data.Length)
            {
                position++;
                return;
            }
            var next = data[position + 1];
            if (next == (byte)'\'')
            {
                position += 2;
                var hex = position + 2 <= data.Length ? Encoding.ASCII.GetString(data, position, 2) : "";
                position = Math.Min(data.Length, position + 2);
                if (byte.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var value)) pendingBytes.Add(value);
                return;
            }
            Flush();
            if (!IsLetter(next))
            {
                position += 2;
                switch ((char)next)
                {
                    case '\\' or '{' or '}': Literal((char)next); break;
                    case '*': ignorableNext = true; break;
                    case '~': Emit("\u00A0"); break;
                    case '_': Emit("\u2011"); break;
                    case '\r' or '\n': Paragraph(); break;
                }
                return;
            }
            ReadControl(out var word, out var parameter);
            Word(word, parameter);
        }

        private void ReadControl(out string word, out int? parameter)
        {
            position++;
            var start = position;
            if (position < data.Length && !IsLetter(data[position]))
            {
                position++;
                word = ((char)data[start]).ToString();
                parameter = null;
                return;
            }
            while (position < data.Length && IsLetter(data[position])) position++;
            word = Encoding.ASCII.GetString(data, start, position - start);
            var numberStart = position;
            if (position < data.Length && data[position] == (byte)'-') position++;
            while (position < data.Length && data[position] is >= (byte)'0' and <= (byte)'9') position++;
            parameter = int.TryParse(Encoding.ASCII.GetString(data, numberStart, position - numberStart), out var value)
                ? value : null;
            if (position < data.Length && data[position] == (byte)' ') position++;
            if (word == "bin" && parameter is > 0) position = Math.Min(data.Length, position + parameter.Value);
        }

        private static bool IsLetter(byte b) => b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z';

        private static readonly HashSet<string> SkippedDestinations =
        [
            "stylesheet", "info", "pict", "object", "header", "headerl", "headerr", "headerf", "footer", "footerl",
            "footerr", "footerf", "footnote", "xmlnstbl", "listtable", "listoverridetable", "revtbl", "rsidtbl",
            "generator", "themedata", "colorschememapping", "latentstyles", "datastore", "fldinst", "filetbl",
            "pgdsctbl", "nonshppict", "bkmkstart", "bkmkend",
        ];

        private void Word(string word, int? parameter)
        {
            if (ignorableNext)
            {
                ignorableNext = false;
                state.Destination = word == "expandedcolortbl" ? Destination.ExpandedColorTable : Destination.Skip;
                return;
            }
            switch (word)
            {
                case "fonttbl": state.Destination = Destination.FontTable; return;
                case "colortbl": state.Destination = Destination.ColorTable; return;
                case "deff": defaultFont = parameter ?? 0; return;
                case "ansicpg": codePage = parameter ?? 1252; return;
                case "ansi": codePage = 1252; return;
                case "mac": codePage = 10000; return;
                case "pc": codePage = 437; return;
                case "pca": codePage = 850; return;
            }
            if (SkippedDestinations.Contains(word))
            {
                state.Destination = Destination.Skip;
                return;
            }
            switch (state.Destination)
            {
                case Destination.FontTable: FontTableWord(word, parameter); return;
                case Destination.ColorTable: ColorTableWord(word, parameter); return;
                case Destination.ExpandedColorTable: ExpandedColorWord(word, parameter); return;
                case Destination.Skip: return;
            }
            switch (word)
            {
                case "par" or "sect" or "page": Paragraph(); break;
                case "line": Emit("\u2028"); break;
                case "tab": Emit("\t"); break;
                case "emdash": Emit("\u2014"); break;
                case "endash": Emit("\u2013"); break;
                case "bullet": Emit("\u2022"); break;
                case "lquote": Emit("\u2018"); break;
                case "rquote": Emit("\u2019"); break;
                case "ldblquote": Emit("\u201C"); break;
                case "rdblquote": Emit("\u201D"); break;
                case "emspace": Emit("\u2003"); break;
                case "enspace": Emit("\u2002"); break;
                case "qmspace": Emit("\u2005"); break;
                case "zwj": Emit("\u200D"); break;
                case "zwnj": Emit("\u200C"); break;
                case "u":
                    var code = parameter ?? 0;
                    if (code < 0) code += 65536;
                    Emit(((char)code).ToString());
                    fallbackToSkip = state.UnicodeSkip;
                    break;
                case "uc": state.UnicodeSkip = Math.Max(0, parameter ?? 1); break;
                case "f": state.Font = parameter ?? 0; break;
                case "fs": if (parameter is > 0) state.FontSize = parameter.Value / 2.0; break;
                case "cf": state.Foreground = parameter ?? 0; break;
                case "cb" or "highlight": state.Background = parameter ?? 0; break;
                case "expnd": state.Kern = (parameter ?? 0) / 4.0; break;
                case "expndtw": state.Kern = (parameter ?? 0) / 20.0; break;
                case "ul" or "uld" or "uldash" or "uldashd" or "uldashdd" or "uldb" or "ulhwave" or "ulldash" or "ulth"
                    or "ulthd" or "ulthdash" or "ulthdashd" or "ulthdashdd" or "ulthldash" or "ululdbwave" or "ulw" or "ulwave":
                    state.Underline = parameter == 0 ? 0 : 1;
                    break;
                case "ulnone": state.Underline = 0; break;
                case "strike" or "striked": state.Strikethrough = parameter == 0 ? 0 : 1; break;
                case "plain":
                    state.Font = defaultFont;
                    state.FontSize = 12;
                    state.Foreground = 0;
                    state.Background = 0;
                    state.Kern = 0;
                    state.Underline = 0;
                    state.Strikethrough = 0;
                    break;
                case "pard":
                    state.LeftIndent = 0;
                    state.FirstIndent = 0;
                    state.RightIndent = 0;
                    break;
                case "li" or "lin": state.LeftIndent = (parameter ?? 0) / 20.0; break;
                case "fi": state.FirstIndent = (parameter ?? 0) / 20.0; break;
                case "ri" or "rin": state.RightIndent = (parameter ?? 0) / 20.0; break;
            }
        }

        private void FontTableWord(string word, int? parameter)
        {
            switch (word)
            {
                case "f":
                    fontEntry = parameter ?? 0;
                    fontCharset = 0;
                    fontName.Clear();
                    break;
                case "fcharset":
                    fontCharset = parameter ?? 0;
                    break;
            }
        }

        private void ColorTableWord(string word, int? parameter)
        {
            switch (word)
            {
                case "red": red = parameter ?? 0; break;
                case "green": green = parameter ?? 0; break;
                case "blue": blue = parameter ?? 0; break;
            }
        }

        private void ExpandedColorWord(string word, int? parameter)
        {
            switch (word)
            {
                case "csgenericrgb": space = ColorSpace.GenericRgb; break;
                case "cssrgb": space = ColorSpace.Srgb; break;
                case "csgray" or "csgenericgray": space = ColorSpace.Gray; break;
                case "c": components.Add((parameter ?? 0) / 100000.0); break;
            }
        }

        private void Literal(char c)
        {
            switch (state.Destination)
            {
                case Destination.FontTable when c == ';':
                    if (fontEntry >= 0) fonts[fontEntry] = (fontName.ToString().Trim(), fontCharset);
                    fontName.Clear();
                    break;
                case Destination.ColorTable when c == ';':
                    colors.Add(red is null && green is null && blue is null
                        ? null
                        : RtfColor.FromGenericRgb((red ?? 0) / 255.0, (green ?? 0) / 255.0, (blue ?? 0) / 255.0));
                    red = green = blue = null;
                    break;
                case Destination.ExpandedColorTable when c == ';':
                    expandedColors.Add(space switch
                    {
                        ColorSpace.GenericRgb when components.Count >= 3 => RtfColor.FromGenericRgb(components[0], components[1], components[2]),
                        ColorSpace.Srgb when components.Count >= 3 => new RtfColor(components[0], components[1], components[2]),
                        ColorSpace.Gray when components.Count >= 1 => RtfColor.FromGray(components[0]),
                        _ => null,
                    });
                    space = ColorSpace.None;
                    components.Clear();
                    break;
                default:
                    Emit(c.ToString());
                    break;
            }
        }

        private void Flush()
        {
            if (pendingBytes.Count == 0) return;
            var bytes = pendingBytes.ToArray();
            pendingBytes.Clear();
            var page = state.Destination == Destination.Text && fonts.TryGetValue(state.Font, out var font) && CharsetCodePage(font.Charset) is { } charsetPage
                ? charsetPage : codePage;
            var encoding = CodePagesEncodingProvider.Instance.GetEncoding(page) ?? Encoding.Latin1;
            Emit(encoding.GetString(bytes));
        }

        private static int? CharsetCodePage(int charset) => charset switch
        {
            77 => 10000, 128 => 932, 129 => 949, 134 => 936, 136 => 950, 161 => 1253, 162 => 1254,
            177 => 1255, 178 => 1256, 186 => 1257, 204 => 1251, 222 => 874, 238 => 1250,
            _ => null,
        };

        private void Emit(string value)
        {
            switch (state.Destination)
            {
                case Destination.FontTable:
                    fontName.Append(value);
                    return;
                case Destination.Text:
                    break;
                default:
                    return;
            }
            if (value.Length == 0) return;
            var attributes = new CharAttributes(
                state.Font, state.FontSize, state.Foreground, state.Background, state.Kern, state.Underline, state.Strikethrough);
            var start = text.Length;
            text.Append(value);
            trailingIndents = (state.LeftIndent, state.FirstIndent, state.RightIndent);
            if (charRuns.Count > 0 && charRuns[^1].Attributes == attributes && charRuns[^1].Start + charRuns[^1].Length == start)
                charRuns[^1] = (charRuns[^1].Start, charRuns[^1].Length + value.Length, attributes);
            else
                charRuns.Add((start, value.Length, attributes));
        }

        private void Paragraph()
        {
            if (state.Destination != Destination.Text) return;
            Emit("\n");
            paragraphs.Add((paragraphStart, text.Length, CurrentParagraph()));
            paragraphStart = text.Length;
        }

        private RtfParagraphStyle CurrentParagraph() => StyleOf((state.LeftIndent, state.FirstIndent, state.RightIndent));

        private static RtfParagraphStyle StyleOf((double Left, double First, double Right) indents) =>
            new(indents.Left + indents.First, indents.Left, -indents.Right);

        private string FontName(int index)
        {
            if (fonts.TryGetValue(index, out var font) && font.Name.Length > 0) return font.Name;
            if (fonts.TryGetValue(defaultFont, out var fallback) && fallback.Name.Length > 0) return fallback.Name;
            return "Helvetica";
        }

        private RtfColor? Color(int index)
        {
            if (index <= 0) return null;
            if (index < expandedColors.Count && expandedColors[index] is { } expanded) return expanded;
            return index < colors.Count ? colors[index] : null;
        }

        private AttributedText Build()
        {
            var boundaries = new SortedSet<int> { 0, text.Length };
            foreach (var run in charRuns) boundaries.Add(run.Start + run.Length);
            foreach (var paragraph in paragraphs) boundaries.Add(paragraph.End);
            var runs = new List<AttributedText.Run>();
            int charIndex = 0, paragraphIndex = 0, at = 0;
            foreach (var end in boundaries.Where(b => b > 0))
            {
                while (charRuns[charIndex].Start + charRuns[charIndex].Length <= at) charIndex++;
                while (paragraphs[paragraphIndex].End <= at) paragraphIndex++;
                var a = charRuns[charIndex].Attributes;
                var attributes = new RtfAttributes(
                    FontName(a.Font), a.FontSize, Color(a.Foreground), Color(a.Background),
                    a.Kern, a.Underline, a.Strikethrough, paragraphs[paragraphIndex].Style);
                if (runs.Count > 0 && runs[^1].Attributes == attributes)
                    runs[^1] = runs[^1] with { Length = runs[^1].Length + end - at };
                else
                    runs.Add(new AttributedText.Run(at, end - at, attributes));
                at = end;
            }
            return new AttributedText(text.ToString(), runs);
        }
    }
}
