using System.Buffers;
using System.Globalization;
using System.Text;

namespace MxU.Slides.Core.Lyrics;

/// <summary>
/// The Swift / Foundation string semantics the lyric code is written against, so
/// the port can read line-for-line like the Mac sources. A Swift <c>Character</c>
/// is an extended grapheme cluster, so counts and columns here are in graphemes.
/// </summary>
internal static class SwiftString
{
    /// <summary><c>UUID().uuidString</c>.</summary>
    public static string Uuid() => Guid.NewGuid().ToString().ToUpperInvariant();

    /// <summary><c>CharacterSet.whitespaces</c>: tab and Unicode Zs.</summary>
    public static bool IsWhitespace(Rune rune) =>
        rune.Value == '\t' || Rune.GetUnicodeCategory(rune) == UnicodeCategory.SpaceSeparator;

    /// <summary><c>CharacterSet.newlines</c>: U+000A–U+000D, U+0085, U+2028, U+2029.</summary>
    public static bool IsNewline(Rune rune) => rune.Value is >= 0x0A and <= 0x0D or 0x85 or 0x2028 or 0x2029;

    private static readonly char[] Newlines = [(char)0x0A, (char)0x0B, (char)0x0C, (char)0x0D, (char)0x85, (char)0x2028, (char)0x2029];

    /// <summary><c>components(separatedBy: .newlines)</c>.</summary>
    public static string[] ComponentsSeparatedByNewlines(this string text) => text.Split(Newlines);

    /// <summary><c>trimmingCharacters(in:)</c> over Unicode scalars.</summary>
    public static string Trimming(this string text, Func<Rune, bool> set)
    {
        int start = 0, end = text.Length;
        while (start < end
               && Rune.DecodeFromUtf16(text.AsSpan(start, end - start), out var rune, out var length) == OperationStatus.Done
               && set(rune))
        {
            start += length;
        }
        while (end > start
               && Rune.DecodeLastFromUtf16(text.AsSpan(start, end - start), out var rune, out var length) == OperationStatus.Done
               && set(rune))
        {
            end -= length;
        }
        return start == 0 && end == text.Length ? text : text[start..end];
    }

    public static string TrimmingWhitespaces(this string text) => text.Trimming(IsWhitespace);

    public static string TrimmingWhitespacesAndNewlines(this string text) =>
        text.Trimming(rune => IsWhitespace(rune) || IsNewline(rune));

    /// <summary><c>CharacterSet.punctuationCharacters</c>: Unicode P*.</summary>
    public static string TrimmingPunctuation(this string text) => text.Trimming(Rune.IsPunctuation);

    /// <summary><c>split(separator:maxSplits:)</c>, which omits empty pieces.</summary>
    public static List<string> SplitOmittingEmpty(this string text, char separator, int maxSplits = int.MaxValue)
    {
        var result = new List<string>();
        if (maxSplits == 0 || text.Length == 0)
        {
            if (text.Length > 0) result.Add(text);
            return result;
        }
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != separator) continue;
            var appended = index > start;
            if (appended) result.Add(text[start..index]);
            start = index + 1;
            if (appended && result.Count == maxSplits) break;
        }
        if (start != text.Length) result.Add(text[start..]);
        return result;
    }

    /// <summary><c>Array(string)</c>: the string's Swift Characters.</summary>
    public static List<string> Characters(this string text)
    {
        var characters = new List<string>();
        for (var index = 0; index < text.Length;)
        {
            var length = StringInfo.GetNextTextElementLength(text.AsSpan(index));
            characters.Add(text.Substring(index, length));
            index += length;
        }
        return characters;
    }

    /// <summary><c>string.count</c>.</summary>
    public static int CharacterCount(this string text) => new StringInfo(text).LengthInTextElements;

    /// <summary>The UTF-16 offset of <c>index(startIndex, offsetBy: characters)</c>.</summary>
    public static int Utf16Offset(this string text, int characters)
    {
        var offset = 0;
        for (var count = 0; count < characters && offset < text.Length; count++)
        {
            offset += StringInfo.GetNextTextElementLength(text.AsSpan(offset));
        }
        return offset;
    }

    /// <summary><c>String(string.prefix(1))</c>.</summary>
    public static string FirstCharacter(this string text) =>
        text.Length == 0 ? "" : text[..StringInfo.GetNextTextElementLength(text)];

    /// <summary><c>String(string.dropFirst())</c>.</summary>
    public static string DropFirst(this string text) =>
        text.Length == 0 ? "" : text[StringInfo.GetNextTextElementLength(text)..];

    /// <summary><c>Character.isLetter</c>: the first scalar is alphabetic.</summary>
    public static bool IsLetter(string character) =>
        Rune.TryGetRuneAt(character, 0, out var rune)
        && (Rune.IsLetter(rune) || Rune.GetUnicodeCategory(rune) == UnicodeCategory.LetterNumber);

    /// <summary><c>Character.isUppercase</c>.</summary>
    public static bool IsUppercase(string character) =>
        character.Length > 0
        && ((Rune.TryGetRuneAt(character, 0, out var rune) && rune.Utf16SequenceLength == character.Length && Rune.IsUpper(rune))
            || (character.ToUpperInvariant() == character && character.ToLowerInvariant() != character));

    /// <summary><c>CharacterSet.letters</c>: Unicode L* and M*.</summary>
    public static bool IsLetterOrMark(Rune rune) =>
        Rune.IsLetter(rune) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    /// <summary><c>capitalized</c>: each word's first letter uppercased, the rest lowercased.</summary>
    public static string Capitalized(this string text)
    {
        var builder = new StringBuilder(text.Length);
        var previous = ' ';
        foreach (var character in text)
        {
            var inWord = char.IsLetterOrDigit(previous) || previous is '\'' or '’';
            builder.Append(inWord ? char.ToLowerInvariant(character) : char.ToUpperInvariant(character));
            previous = character;
        }
        return builder.ToString();
    }

    /// <summary><c>caseInsensitiveCompare(_:) == .orderedSame</c>.</summary>
    public static bool CaseInsensitiveEquals(string a, string b) =>
        string.Compare(a, b, CultureInfo.InvariantCulture, CompareOptions.IgnoreCase) == 0;

    /// <summary><c>localizedCaseInsensitiveContains(_:)</c>.</summary>
    public static bool LocalizedCaseInsensitiveContains(this string text, string other) =>
        text.Contains(other, StringComparison.CurrentCultureIgnoreCase);
}
