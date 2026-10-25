using System.Numerics;
using System.Text;

namespace MxU.Slides.Core.Scripture;

/// <summary>Where a matched word sits in a verse's text, for showing it in bold.</summary>
public readonly record struct TextRange(int Start, int Length);

/// <summary>A verse a search found, how well it matched, and the words that matched.</summary>
public sealed record SearchHit(BibleVerse Verse, double Score, IReadOnlyList<TextRange> Matches);

/// <summary>
/// Word search over one translation: "love is patient", "God so loved", "shepherd". Windows only.
/// <para>
/// <b>The index.</b> Every verse is cut into words, and each word is reduced to a rough stem
/// (<see cref="Stem"/>: "loved", "loveth" and "loving" all become "lov"), so the KJV's old forms
/// and a modern query still meet. For each stem the index keeps the verses that have it (its
/// <em>postings</em>), and for each verse its stems in order. Building it takes a moment, once,
/// on the Bible thread (<see cref="BibleCatalog"/>).
/// </para>
/// <para>
/// <b>Ranking.</b> A verse scores for each query word it has, weighted by how rare the word is
/// (finding "patient" says more than finding "is"). Having every word adds more; having them
/// side by side in order (the phrase itself) adds most; having them close together adds some.
/// Shorter verses win ties, then Bible order. The last word counts as a beginning while it's
/// still being typed ("love is pat" finds "patient").
/// </para>
/// </summary>
public sealed class BibleSearch
{
    private readonly BibleVerse[] verses;
    private readonly int[][] tokens;          // each verse's stems, in order, as stem ids
    private readonly Dictionary<string, int> ids; // stem → id
    private readonly int[][] postings;        // stem id → the verses that have it (ascending)
    private readonly string[] sortedStems;    // for finding stems that start with what's typed

    private BibleSearch(BibleVerse[] verses, int[][] tokens, Dictionary<string, int> ids, int[][] postings)
    {
        this.verses = verses;
        this.tokens = tokens;
        this.ids = ids;
        this.postings = postings;
        sortedStems = [.. ids.Keys.Order(StringComparer.Ordinal)];
    }

    public static BibleSearch Build(Bible bible, CancellationToken cancel = default)
    {
        var verses = bible.AllVerses().ToArray();
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var lists = new List<List<int>>();
        var tokens = new int[verses.Length][];
        for (var v = 0; v < verses.Length; v++)
        {
            if ((v & 1023) == 0) cancel.ThrowIfCancellationRequested();
            var stems = new List<int>();
            foreach (var word in Words(verses[v].Text))
            {
                var stem = Stem(word.Word);
                if (!ids.TryGetValue(stem, out var id))
                {
                    id = ids.Count;
                    ids[stem] = id;
                    lists.Add([]);
                }
                stems.Add(id);
                var list = lists[id];
                if (list.Count == 0 || list[^1] != v) list.Add(v);
            }
            tokens[v] = [.. stems];
        }
        return new BibleSearch(verses, tokens, ids, [.. lists.Select(l => l.ToArray())]);
    }

    // Words so common that finding them says little: they still count, but lightly, and they
    // never on their own make a verse a candidate when the query has other words.
    private static readonly HashSet<string> StopWords =
    [
        "a", "an", "and", "are", "as", "at", "be", "but", "by", "for", "from", "had", "hath", "have", "he", "her", "him",
        "his", "i", "in", "is", "it", "me", "my", "not", "of", "on", "or", "our", "shall", "she", "so", "that", "the",
        "thee", "their", "them", "there", "they", "this", "thou", "thy", "to", "unto", "us", "was", "we", "were",
        "which", "who", "will", "with", "ye", "you", "your",
    ];

    private sealed record QueryTerm(string Word, int[] Ids, double Weight, bool IsStop);

    /// <summary>The best <paramref name="limit"/> verses for <paramref name="query"/>, best first.</summary>
    public IReadOnlyList<SearchHit> Search(string query, int limit = 50, CancellationToken cancel = default)
    {
        var words = Words(query).Select(w => w.Word).Take(16).ToList();
        if (words.Count == 0 || verses.Length == 0) return [];
        var stillTyping = char.IsLetterOrDigit(query[^1]) && words[^1].Length >= 2;
        var count = verses.Length;

        var terms = new List<QueryTerm>();
        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            var matching = new List<int>();
            if (ids.TryGetValue(Stem(word), out var exact)) matching.Add(exact);
            if (stillTyping && i == words.Count - 1) matching.AddRange(StartingWith(word));
            var distinct = matching.Distinct().ToArray();
            var postingsCount = Math.Min(count, distinct.Sum(id => postings[id].Length));
            var isStop = StopWords.Contains(word);
            var weight = postingsCount == 0 ? 1 : Math.Log(1 + (double)count / postingsCount);
            terms.Add(new QueryTerm(word, distinct, isStop ? weight * 0.25 : weight, isStop));
        }
        if (terms.All(t => t.Ids.Length == 0)) return [];
        var totalWeight = terms.Sum(t => t.Weight);

        // Pass 1: the verses with any of the query's telling words (or any word at all, when
        // every word is a common one) become candidates and score each word's weight once.
        var score = new double[count];
        var found = new int[count];
        var leading = terms.Any(t => !t.IsStop && t.Ids.Length > 0);
        for (var t = 0; t < terms.Count; t++)
        {
            if (leading && terms[t].IsStop) continue;
            foreach (var id in terms[t].Ids)
                foreach (var v in postings[id])
                {
                    if ((found[v] & (1 << t)) != 0) continue;
                    found[v] |= 1 << t;
                    score[v] += terms[t].Weight;
                }
        }
        cancel.ThrowIfCancellationRequested();
        // Pass 2: the common words add to verses that are already candidates.
        if (leading)
        {
            for (var t = 0; t < terms.Count; t++)
            {
                if (!terms[t].IsStop) continue;
                foreach (var id in terms[t].Ids)
                    foreach (var v in postings[id])
                    {
                        if (found[v] == 0 || (found[v] & (1 << t)) != 0) continue;
                        found[v] |= 1 << t;
                        score[v] += terms[t].Weight;
                    }
            }
        }
        cancel.ThrowIfCancellationRequested();

        // Which query words each stem id stands for, for the phrase and closeness checks.
        var meaning = new Dictionary<int, int>();
        for (var t = 0; t < terms.Count; t++)
            foreach (var id in terms[t].Ids)
                meaning[id] = meaning.GetValueOrDefault(id) | (1 << t);
        var all = (1 << terms.Count) - 1;

        var ranked = new List<(int Verse, double Score)>();
        for (var v = 0; v < count; v++)
        {
            if (found[v] == 0) continue;
            var s = 100 * score[v] / totalWeight;
            var matched = BitOperations.PopCount((uint)found[v]);
            if (found[v] == all) s += 25;
            if (terms.Count > 1 && matched > 1) s += Closeness(tokens[v], meaning, terms.Count, found[v]);
            s -= 0.05 * tokens[v].Length;
            ranked.Add((v, s));
        }
        cancel.ThrowIfCancellationRequested();
        ranked.Sort((a, b) => b.Score != a.Score ? b.Score.CompareTo(a.Score) : a.Verse.CompareTo(b.Verse));

        // The same word as typed (not just the same stem) breaks near-ties among the best:
        // "love" before "loveth" when someone types "love".
        var top = ranked.Take(Math.Max(limit * 4, 100)).Select(r =>
        {
            var exact = Words(verses[r.Verse].Text).Select(w => w.Word).Intersect(words).Count();
            return (r.Verse, Score: r.Score + 2 * exact);
        }).ToList();
        top.Sort((a, b) => b.Score != a.Score ? b.Score.CompareTo(a.Score) : a.Verse.CompareTo(b.Verse));

        return [.. top.Take(limit).Select(r => new SearchHit(verses[r.Verse], Math.Round(r.Score, 2), Highlights(verses[r.Verse].Text, meaning)))];
    }

    // The phrase itself (every query word, in order, side by side) scores 60; otherwise the closer
    // together the matched words sit, the more (up to 20).
    private static double Closeness(int[] stems, Dictionary<int, int> meaning, int termCount, int matchedMask)
    {
        var masks = new int[stems.Length];
        for (var p = 0; p < stems.Length; p++) masks[p] = meaning.GetValueOrDefault(stems[p]);
        if (BitOperations.PopCount((uint)matchedMask) == termCount)
        {
            for (var p = 0; p + termCount <= stems.Length; p++)
            {
                var phrase = true;
                for (var k = 0; k < termCount && phrase; k++) phrase = (masks[p + k] & (1 << k)) != 0;
                if (phrase) return 60;
            }
        }
        // The shortest stretch of the verse that holds every matched word.
        var best = int.MaxValue;
        var need = BitOperations.PopCount((uint)matchedMask);
        var counts = new int[termCount];
        var have = 0;
        var left = 0;
        for (var right = 0; right < stems.Length; right++)
        {
            for (var t = 0; t < termCount; t++)
                if ((masks[right] & (1 << t)) != 0 && counts[t]++ == 0) have++;
            while (have == need)
            {
                best = Math.Min(best, right - left + 1);
                for (var t = 0; t < termCount; t++)
                    if ((masks[left] & (1 << t)) != 0 && --counts[t] == 0) have--;
                left++;
            }
        }
        return best == int.MaxValue ? 0 : 20.0 * need / best;
    }

    // The words in a found verse that match the query, for bold in the results.
    private List<TextRange> Highlights(string text, Dictionary<int, int> meaning) =>
        [.. Words(text)
            .Where(w => ids.TryGetValue(Stem(w.Word), out var id) && meaning.ContainsKey(id))
            .Select(w => new TextRange(w.Start, w.Length))];

    private IEnumerable<int> StartingWith(string prefix)
    {
        var at = Array.BinarySearch(sortedStems, prefix, StringComparer.Ordinal);
        if (at < 0) at = ~at;
        // The most-used 200 forms are plenty for a word still being typed.
        var found = new List<int>();
        for (; at < sortedStems.Length && sortedStems[at].StartsWith(prefix, StringComparison.Ordinal); at++) found.Add(ids[sortedStems[at]]);
        return found.OrderByDescending(id => postings[id].Length).Take(200);
    }

    // MARK: Words

    /// <summary>
    /// The words of a text, lower case, with where each sits. Apostrophes inside a word stay out
    /// of it ("doesn’t" → "doesnt") and a possessive "’s" is dropped ("Lord’s" → "lord").
    /// </summary>
    public static IEnumerable<(int Start, int Length, string Word)> Words(string text)
    {
        var i = 0;
        var word = new StringBuilder();
        while (i < text.Length)
        {
            if (!char.IsLetterOrDigit(text[i]))
            {
                i++;
                continue;
            }
            var start = i;
            word.Clear();
            while (i < text.Length)
            {
                var c = text[i];
                if (char.IsLetterOrDigit(c))
                {
                    word.Append(char.ToLowerInvariant(c));
                    i++;
                }
                else if (c is '\'' or '’' && i + 1 < text.Length && char.IsLetter(text[i + 1]) && word.Length > 0)
                {
                    // "Lord’s": the possessive goes; "doesn’t": the apostrophe goes.
                    if ((text[i + 1] is 's' or 'S') && (i + 2 >= text.Length || !char.IsLetter(text[i + 2])))
                    {
                        i += 2;
                        break;
                    }
                    i++;
                }
                else
                {
                    break;
                }
            }
            yield return (start, i - start, word.ToString());
        }
    }

    private static readonly string[] Suffixes = ["eth", "est", "ing", "ed", "es", "s"];

    /// <summary>
    /// A rough stem, enough to match old and new forms of a word: one ending off ("-eth", "-est",
    /// "-ing", "-ed", "-es", "-s") if at least three letters stay, then a final "e" ("loved",
    /// "loveth", "loving", "loves" and "love" are all "lov"). It doesn't need to be a real word,
    /// only the same for the query and the verse.
    /// </summary>
    public static string Stem(string word)
    {
        foreach (var suffix in Suffixes)
        {
            if (word.Length - suffix.Length >= 3 && word.EndsWith(suffix, StringComparison.Ordinal)
                && !(suffix == "s" && word.EndsWith("ss", StringComparison.Ordinal)))
            {
                word = word[..^suffix.Length];
                break;
            }
        }
        return word.Length > 3 && word[^1] == 'e' ? word[..^1] : word;
    }
}
