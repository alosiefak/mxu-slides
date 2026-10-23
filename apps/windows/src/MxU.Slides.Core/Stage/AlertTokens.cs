using System.Text;

namespace MxU.Slides.Core.Stage;

/// <summary>
/// "{name}" fill slots in alert messages: names are case-insensitive, empty "{}"
/// and unclosed braces pass through. Port of PresenterCore/AlertTokens.swift.
/// </summary>
public static class AlertTokens
{
    public static List<string> TokenNames(string template)
    {
        var names = new List<string>();
        var seen = new HashSet<string>();
        foreach (var name in RawTokens(template))
            if (seen.Add(name.ToLowerInvariant()))
                names.Add(name);
        return names;
    }

    public static string Compose(string template, IReadOnlyDictionary<string, string> values)
    {
        var lookup = new Dictionary<string, string>();
        foreach (var (key, value) in values) lookup.TryAdd(key.ToLowerInvariant(), value);
        var result = new StringBuilder();
        var index = 0;
        while (index < template.Length)
        {
            if (Token(template, index) is (string name, int next))
            {
                result.Append(lookup.GetValueOrDefault(name.ToLowerInvariant()) ?? "");
                index = next;
                continue;
            }
            result.Append(template[index]);
            index++;
        }
        return result.ToString();
    }

    private static List<string> RawTokens(string template)
    {
        var tokens = new List<string>();
        var index = 0;
        while (index < template.Length)
        {
            if (Token(template, index) is (string name, int next))
            {
                tokens.Add(name);
                index = next;
                continue;
            }
            index++;
        }
        return tokens;
    }

    /// <summary>The token opening at <paramref name="index"/>, and where scanning resumes after it.</summary>
    private static (string Name, int Next)? Token(string template, int index)
    {
        if (template[index] != '{') return null;
        var close = template.IndexOf('}', index);
        if (close <= index + 1) return null;
        var name = template[(index + 1)..close];
        return name.Contains('{') ? null : (name, close + 1);
    }
}
