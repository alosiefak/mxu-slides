using System.Globalization;
using System.Text.RegularExpressions;
using MxU.Slides.Core.Scripture;

namespace MxU.Slides.Assistant.Spotting;

/// <summary>
/// Finds Bible references in a sentence as it was <em>spoken</em>: "turn with me to Romans
/// chapter eight verse twenty-eight". The Bible window's parser (<see cref="PassageReference"/>)
/// reads what people type ("Rom 8:28"), so this first rewrites speech into that form: number
/// words become digits, "chapter" is dropped, "first Corinthians" becomes "1 Corinthians". Then
/// it looks for a book's full name followed by at least a chapter number, and checks the result
/// against the Bible. No network, no AI: this is the spotter's first and main net.
/// <para>
/// Only a book's full name counts. The Bible window's short forms would catch everyday words
/// here ("is" is Isaiah, "am" is Amos), and a book with no number after it is ignored, because
/// "John", "Mark", "Acts" and "Numbers" are ordinary words in a sermon.
/// </para>
/// </summary>
public static partial class SpokenScripture
{
    /// <summary>A reference found in speech, and the words that named it.</summary>
    public sealed record Found(PassageReference Reference, string Heard);

    private static readonly string[] Ones =
        ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen",
         "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"];

    private static readonly string[] Tens = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];

    private static readonly Dictionary<string, string> Ordinals = new(StringComparer.Ordinal)
    {
        ["first"] = "1", ["second"] = "2", ["third"] = "3", ["1st"] = "1", ["2nd"] = "2", ["3rd"] = "3", ["1"] = "1", ["2"] = "2", ["3"] = "3",
    };

    // Each book by the words of its spoken name: "1 corinthians", "song of solomon", "psalm".
    private static readonly List<(string[] Words, BibleBook Book)> Names = BuildNames();

    private static List<(string[] Words, BibleBook Book)> BuildNames()
    {
        var names = BibleBooks.All.Select(b => (b.Name.ToLowerInvariant().Split(' '), b)).ToList();
        void Also(string spoken, string code) => names.Add((spoken.Split(' '), BibleBooks.All.First(b => b.Code == code)));
        Also("psalm", "PSA");
        Also("song of songs", "SOL");
        Also("revelations", "REV");
        // Longest first, so "song of solomon" is tried before a one-word name.
        return [.. names.OrderByDescending(n => n.Item1.Length)];
    }

    /// <summary>A number as it is said: 28 is "twenty eight", 119 is "one hundred nineteen". (The speech grammar is built from these.)</summary>
    public static string SpokenNumber(int number)
    {
        var (hundreds, rest) = (number / 100, number % 100);
        var words = new List<string>();
        if (hundreds > 0) words.AddRange([Ones[hundreds], "hundred"]);
        if (rest >= 20)
        {
            words.Add(Tens[rest / 10]);
            if (rest % 10 > 0) words.Add(Ones[rest % 10]);
        }
        else if (rest > 0 || hundreds == 0)
        {
            words.Add(Ones[rest]);
        }
        return string.Join(' ', words);
    }

    /// <summary>
    /// The sentence as words, lower case, with spoken numbers written as digits and "3:16" or
    /// "16-18" split into their parts ("3", ":", "16").
    /// </summary>
    public static List<string> Tokens(string heard)
    {
        var raw = Splitter().Matches(heard.ToLowerInvariant().Replace('’', '\'')).Select(m => m.Value).ToList();
        var tokens = new List<string>();
        for (var i = 0; i < raw.Count; i++)
        {
            var word = raw[i];
            var ones = Array.IndexOf(Ones, word);
            var tens = Array.IndexOf(Tens, word);
            if (word.Length > 0 && tens >= 2)
            {
                // "twenty" then perhaps "eight".
                var unit = i + 1 < raw.Count ? Array.IndexOf(Ones, raw[i + 1]) : -1;
                if (unit is >= 1 and <= 9) i++;
                else unit = 0;
                tokens.Add((tens * 10 + unit).ToString(CultureInfo.InvariantCulture));
            }
            else if (ones >= 1 && i + 1 < raw.Count && raw[i + 1] == "hundred")
            {
                // "one hundred [and] nineteen": the rest is read by the next turns of the loop and added on.
                i++;
                if (i + 1 < raw.Count && raw[i + 1] == "and") i++;
                var rest = 0;
                if (i + 1 < raw.Count)
                {
                    var restTens = Array.IndexOf(Tens, raw[i + 1]);
                    var restOnes = Array.IndexOf(Ones, raw[i + 1]);
                    if (raw[i + 1].Length > 0 && restTens >= 2)
                    {
                        i++;
                        rest = restTens * 10;
                        var unit = i + 1 < raw.Count ? Array.IndexOf(Ones, raw[i + 1]) : -1;
                        if (unit is >= 1 and <= 9)
                        {
                            i++;
                            rest += unit;
                        }
                    }
                    else if (restOnes >= 1)
                    {
                        i++;
                        rest = restOnes;
                    }
                }
                tokens.Add((ones * 100 + rest).ToString(CultureInfo.InvariantCulture));
            }
            else if (ones >= 0)
            {
                tokens.Add(ones.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                tokens.Add(word);
            }
        }
        return tokens;
    }

    /// <summary>Every reference named in the sentence that the Bible holds, in the order spoken.</summary>
    public static List<Found> Find(string heard, Bible bible)
    {
        var tokens = Tokens(heard);
        var found = new List<Found>();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (BookAt(tokens, i) is not { } named) continue;
            var after = named.Start + named.Length;
            // What follows the name: numbers and the little words between them.
            var tail = new List<string>();
            var at = after;
            for (; at < tokens.Count; at++)
            {
                var token = tokens[at];
                if (token == "chapter" || (token == "and" && at + 1 < tokens.Count && tokens[at + 1] is "verse" or "verses")) continue;
                if (IsNumber(token) || token is ":" or "-" or "verse" or "verses" or "to" or "through" or "and") tail.Add(token);
                else break;
            }
            // Drop words off the end until it reads ("John 3 16 and" → "John 3 16").
            while (tail.Count > 0 && !IsNumber(tail[^1])) tail.RemoveAt(tail.Count - 1);
            if (tail.Count == 0) continue; // a book's name with no number: an ordinary word
            if (Read(named.Book, tail, bible) is not { } reference) continue;
            found.Add(new Found(reference, string.Join(' ', tokens.Skip(named.Start).Take(at - named.Start))));
            i = at - 1;
        }
        return found;
    }

    private static PassageReference? Read(BibleBook book, List<string> tail, Bible bible)
    {
        // A recognizer often runs chapter and verse together: "John 316". When that isn't a
        // chapter of the book, try it as chapter 3 verse 16, then as chapter 31 verse 6.
        if (tail.Count == 1 && tail[0].Length is 3 or 4 && int.Parse(tail[0], CultureInfo.InvariantCulture) > book.Chapters)
        {
            foreach (var cut in new[] { 1, 2 })
            {
                var (chapter, verse) = (int.Parse(tail[0][..cut], CultureInfo.InvariantCulture), int.Parse(tail[0][cut..], CultureInfo.InvariantCulture));
                if (bible.Contains(new VerseRef(book.Index, chapter, verse)))
                    return PassageReference.Of(new VerseRef(book.Index, chapter, verse));
            }
            return null;
        }
        // "Psalm one nineteen" is heard as 1 and 19: only the Psalms run past a hundred.
        if (book.Chapters > 100 && tail.Count >= 2 && tail[0] == "1" && tail[1].Length == 2 && int.Parse(tail[1], CultureInfo.InvariantCulture) <= book.Chapters - 100)
            tail = [$"1{tail[1]}", .. tail.Skip(2)];
        if (PassageReference.Parse($"{book.Name} {string.Join(' ', tail)}") is not { } reference) return null;
        var passage = bible.Read(reference);
        return passage.IsEmpty ? null : passage.Reference;
    }

    private static bool IsNumber(string token) => token.Length is > 0 and <= 3 && token.All(char.IsAsciiDigit) || token.Length == 4 && token.All(char.IsAsciiDigit);

    // The book named at this place, with the tokens its name takes; a "1", "first" or "1st" before
    // "Corinthians" belongs to it.
    private static (BibleBook Book, int Start, int Length)? BookAt(List<string> tokens, int i)
    {
        foreach (var (words, book) in Names)
        {
            var numbered = char.IsAsciiDigit(words[0][0]);
            var name = numbered ? words[1..] : words;
            if (i + name.Length > tokens.Count || !name.SequenceEqual(tokens.Skip(i).Take(name.Length))) continue;
            if (!numbered)
            {
                // "John" after "first" is 1 John, which the numbered entry finds; don't also read it as John.
                if (i > 0 && Ordinals.ContainsKey(tokens[i - 1]) && Names.Any(n => char.IsAsciiDigit(n.Words[0][0]) && n.Words[1..].SequenceEqual(name))) continue;
                return (book, i, name.Length);
            }
            if (i > 0 && Ordinals.TryGetValue(tokens[i - 1], out var ordinal) && ordinal == words[0]) return (book, i - 1, name.Length + 1);
        }
        return null;
    }

    /// <summary>
    /// True when a sentence with no reference in it still sounds as if it points at a passage:
    /// "the story of the prodigal son", "as Paul wrote", "the Bible says". Only such sentences
    /// are ever sent to the assistant, one at a time; the rest of the sermon stays on this PC.
    /// </summary>
    public static bool SoundsLikeAnAllusion(string heard) => Allusion().IsMatch(heard);

    [GeneratedRegex(@"\d+(?:st|nd|rd|th)?|[a-z']+|[:\-–—]")]
    private static partial Regex Splitter();

    [GeneratedRegex(
        @"\b(parable|story|account|miracle|vision)\s+of\b|\b(prodigal|good\s+samaritan|sermon\s+on\s+the\s+mount|beatitudes|lord'?s\s+prayer|ten\s+commandments|feeding\s+of|five\s+thousand|burning\s+bush|golden\s+rule|great\s+commission|armou?r\s+of\s+god|fruits?\s+of\s+the\s+spirit|love\s+chapter|road\s+to\s+(emmaus|damascus)|garden\s+of\s+(eden|gethsemane)|last\s+supper|lord\s+is\s+my\s+shepherd|valley\s+of\s+dry\s+bones|walls?\s+of\s+jericho|david\s+and\s+goliath|lions'?\s+den|fiery\s+furnace|woman\s+at\s+the\s+well|lost\s+(sheep|coin|son))\b|\b(paul|peter|james|john|david|moses|isaiah|jeremiah|jesus|solomon)\s+(said|says|wrote|writes|tells|told|prayed|asks?)\b|\b(the\s+bible|scripture|the\s+psalmist|the\s+apostle(\s+\w+)?|the\s+prophet(\s+\w+)?)\s+(says|said|tells|writes|wrote)\b|\bit\s+is\s+written\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Allusion();
}
