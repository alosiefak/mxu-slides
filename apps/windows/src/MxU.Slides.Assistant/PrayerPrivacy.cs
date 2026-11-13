using System.Text.RegularExpressions;

namespace MxU.Slides.Assistant;

/// <summary>
/// Keeps prayer requests on this PC. Sermon notes and service plans often carry a list like
/// "Prayer requests: Mary Okafor (surgery Tuesday), the Bello family". Those are people's private
/// troubles, so the lines of such a list are never sent to the assistant: each is replaced by a
/// placeholder before anything leaves, and the window that asked says how many were held back.
/// <para>
/// A list starts at a line that names prayer ("Prayer requests", "Pray for:", "Praise reports")
/// and runs until a blank line or the next heading-like line. A single line such as "Pray for
/// Mary this week" is held back on its own. It errs towards holding back too much.
/// </para>
/// </summary>
public static partial class PrayerPrivacy
{
    /// <summary>What a held-back line is replaced by, so line numbers still match the note.</summary>
    public const string Placeholder = "(private: not sent)";

    /// <param name="Lines">The lines safe to send, same count and order as given.</param>
    /// <param name="Withheld">Which lines (by index) were replaced.</param>
    public sealed record Result(IReadOnlyList<string> Lines, IReadOnlyList<int> Withheld)
    {
        /// <summary>"3 prayer-request lines were kept private (not sent)."; null when none were.</summary>
        public string? Warning => Withheld.Count == 0 ? null
            : $"{Withheld.Count} prayer-request line{(Withheld.Count == 1 ? " was" : "s were")} kept private (not sent).";
    }

    /// <param name="startsSection">True for a line that starts something new (a heading), which ends a prayer list.</param>
    public static Result Strip(IReadOnlyList<string> lines, Func<int, bool>? startsSection = null)
    {
        var kept = new List<string>(lines.Count);
        var withheld = new List<int>();
        var inList = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var blank = string.IsNullOrWhiteSpace(line);
            var opens = PrayerHeading().IsMatch(line);
            if (inList && !opens && (blank || startsSection?.Invoke(i) == true || OtherHeading().IsMatch(line))) inList = false;
            if (opens) inList = true;
            if ((inList || PrayerLine().IsMatch(line)) && !blank)
            {
                kept.Add(Placeholder);
                withheld.Add(i);
            }
            else
            {
                kept.Add(line);
            }
        }
        return new Result(kept, withheld);
    }

    // "Prayer requests", "Prayer list:", "Pray for:", "Praises and prayers", "Praise reports".
    [GeneratedRegex(@"^\W*(prayers?\s*(requests?|list|needs|points|concerns|and\s+praises?)?|pray\s+for|praises?(\s+reports?|\s+and\s+prayers?)?|those\s+(who\s+are\s+)?(sick|ill|in\s+hospital))\s*[:\-–—]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrayerHeading();

    // One line that names someone to pray for: "Pray for Mary's surgery", "Prayers for the Bello
    // family", "Prayer request: …", "keep Tunde in your prayers". The name must start with a
    // capital, so a sermon point like "Pray for your enemies" is still sent.
    [GeneratedRegex(@"(?i:\bpray(?:ing|ers?)?\s+for)\s+(?:(?i:the|our|sister|brother|pastor|mr|mrs|ms)\.?\s+)*[A-Z][a-z]+|(?i:\bprayer\s+requests?\s*[:\-–—])|(?i:\bin\s+(?:your|our)\s+prayers\b)",
        RegexOptions.CultureInvariant)]
    private static partial Regex PrayerLine();

    // A short line ending in a colon ("Announcements:"), which starts something else.
    [GeneratedRegex(@"^\s*[\p{L}][\p{L}\s]{1,30}:\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex OtherHeading();
}
