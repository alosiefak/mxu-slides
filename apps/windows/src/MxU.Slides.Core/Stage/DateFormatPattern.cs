using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace MxU.Slides.Core.Stage;

/// <summary>
/// Formats dates with the Unicode (Apple DateFormatter.dateFormat) patterns stored
/// in clock links, e.g. "h:mm a". Translates the common fields to a .NET custom
/// format; every other character is literal, quotes fence literal text, and
/// "a" always reads AM/PM. Dates are shown in the local time zone.
/// </summary>
public static class DateFormatPattern
{
    private static readonly ConcurrentDictionary<string, string> Translations = new();
    private static readonly ConcurrentDictionary<string, CultureInfo> Cultures = new();

    public static string Format(DateTimeOffset date, string pattern, TimeZoneInfo? zone = null, CultureInfo? culture = null)
    {
        var format = Translations.GetOrAdd(pattern, ToDotNet);
        if (format.Length == 0) return "";
        var local = TimeZoneInfo.ConvertTime(date, zone ?? TimeZoneInfo.Local);
        return local.ToString(format, WithAmPm(culture ?? CultureInfo.CurrentCulture));
    }

    /// <summary>The .NET custom format string equivalent to a Unicode date pattern.</summary>
    public static string ToDotNet(string pattern)
    {
        var output = new StringBuilder();
        var fields = 0;
        var quoted = false;
        var index = 0;
        while (index < pattern.Length)
        {
            var c = pattern[index];
            if (c == '\'')
            {
                if (index + 1 < pattern.Length && pattern[index + 1] == '\'')
                {
                    output.Append("\\'");
                    index += 2;
                }
                else
                {
                    quoted = !quoted;
                    index++;
                }
                continue;
            }
            if (quoted || !char.IsAsciiLetter(c))
            {
                output.Append('\\').Append(c);
                index++;
                continue;
            }
            var end = index;
            while (end < pattern.Length && pattern[end] == c) end++;
            var field = Field(c, end - index);
            if (field is null)
                for (var i = index; i < end; i++) output.Append('\\').Append(c);
            else
            {
                output.Append(field);
                fields++;
            }
            index = end;
        }
        // A lone one-letter field ("h", "d"…) would read as a .NET standard format.
        var result = output.ToString();
        return fields == 1 && result.Length == 1 ? "%" + result : result;
    }

    private static string? Field(char letter, int count) => letter switch
    {
        'h' => count == 1 ? "h" : "hh",
        'H' => count == 1 ? "H" : "HH",
        'm' => count == 1 ? "m" : "mm",
        's' => count == 1 ? "s" : "ss",
        'a' => "tt",
        'E' => count >= 4 ? "dddd" : "ddd",
        'M' => count switch { 1 => "M", 2 => "MM", 3 => "MMM", _ => "MMMM" },
        'L' => count switch { 1 => "M", 2 => "MM", 3 => "MMM", _ => "MMMM" },
        'd' => count == 1 ? "d" : "dd",
        'y' => count == 2 ? "yy" : "yyyy",
        'S' => new string('f', Math.Min(count, 7)),
        _ => null,
    };

    private static CultureInfo WithAmPm(CultureInfo culture) => Cultures.GetOrAdd(culture.Name, _ =>
    {
        var copy = (CultureInfo)culture.Clone();
        copy.DateTimeFormat.AMDesignator = "AM";
        copy.DateTimeFormat.PMDesignator = "PM";
        return CultureInfo.ReadOnly(copy);
    });
}
