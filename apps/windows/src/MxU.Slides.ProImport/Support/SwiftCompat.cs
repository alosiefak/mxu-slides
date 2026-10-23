using System.Globalization;
using System.Text;
using MxU.Slides.Core.Library;
using RV = Pro.SerializationInterop.RVProtoData;

namespace MxU.Slides.ProImport;

/// <summary>Small helpers that reproduce Swift/Foundation string and number behaviour the Mac mapper relies on.</summary>
internal static class SwiftCompat
{
    public static string Raw(this RV.UUID? uuid) => uuid?.String ?? "";

    public static string Lower(this RV.UUID? uuid) => uuid.Raw().ToLowerInvariant();

    public static string NewId() => LibraryStore.NewId();

    public static int CharacterCount(this string text) => new StringInfo(text).LengthInTextElements;

    /// <summary>The character (grapheme) column of a UTF-16 offset, or the line's length when the offset splits a character.</summary>
    public static int Characters(string line, int utf16Offset)
    {
        var clamped = Math.Clamp(utf16Offset, 0, line.Length);
        if (clamped == line.Length) return line.CharacterCount();
        var starts = StringInfo.ParseCombiningCharacters(line);
        var index = Array.BinarySearch(starts, clamped);
        return index >= 0 ? index : starts.Length;
    }

    public static bool IsInvisible(Rune rune) =>
        Rune.IsWhiteSpace(rune) || rune.Value is 0x200B or 0x200C or 0x200D or 0xFEFF;

    public static bool AllInvisible(string text)
    {
        foreach (var rune in text.EnumerateRunes())
            if (!IsInvisible(rune)) return false;
        return true;
    }

    private static bool IsSpace(char c) => c == '\t' || char.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;

    public static string TrimmingWhitespaces(this string text)
    {
        var start = 0;
        var end = text.Length;
        while (start < end && IsSpace(text[start])) start++;
        while (end > start && IsSpace(text[end - 1])) end--;
        return text[start..end];
    }

    public static string LowerFirst(string name) =>
        name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];

    public static string CaseName<T>(T value) where T : struct, Enum => LowerFirst(value.ToString());

    /// <summary>Swift's <c>"\(double)"</c>: shortest round-trip digits, always with a decimal point.</summary>
    public static string Describe(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.') || text.Contains('E') || !double.IsFinite(value) ? text : text + ".0";
    }

    public static double Rounded(double value) => Math.Round(value, MidpointRounding.AwayFromZero);

    public static string Format(double value, int places) => value.ToString("F" + places, CultureInfo.InvariantCulture);

    public static string Pad(int value, int width) => value.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');

    /// <summary>Foundation's <c>URL(string:)?.path</c>: the percent-decoded path of an absolute URL.</summary>
    public static string? UrlPath(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri) ? Uri.UnescapeDataString(uri.AbsolutePath) : null;

    public static string NormalizedPath(string path)
    {
        var parts = new List<string>();
        var rooted = path.StartsWith('/') || path.StartsWith('\\');
        foreach (var part in path.Split('/', '\\'))
        {
            if (part.Length == 0 || part == ".") continue;
            if (part == ".." && parts.Count > 0 && parts[^1] != "..") parts.RemoveAt(parts.Count - 1);
            else parts.Add(part);
        }
        return (rooted ? "/" : "") + string.Join('/', parts);
    }

    public static string FullNormalizedPath(string path) => NormalizedPath(Path.GetFullPath(path));
}
