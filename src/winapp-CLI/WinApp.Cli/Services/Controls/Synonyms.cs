// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Controls;

internal static class Synonyms
{
    private static readonly SearchVocabulary Vocabulary = SearchVocabulary.LoadEmbedded();

    /// <summary>
    /// Multi-word phrases whose single-token form is appended to the query, keeping the
    /// original words. Keys are lowercase. From the shared vocabulary's phrases without
    /// <c>replace</c>.
    /// </summary>
    public static readonly Dictionary<string, string> Phrases = Vocabulary.Phrases
        .Where(p => !p.Replace)
        .ToDictionary(p => p.Phrase, p => p.Token, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Multi-word phrases replaced by their single-token form before anything else runs
    /// ("sign in" becomes "login"). From the shared vocabulary's phrases with <c>replace</c>.
    /// Longest first, so a phrase is never pre-empted by one it contains.
    /// </summary>
    public static readonly KeyValuePair<string, string>[] ReplacePhrases = Vocabulary.Phrases
        .Where(p => p.Replace)
        .OrderByDescending(p => p.Phrase.Length)
        .Select(p => KeyValuePair.Create(p.Phrase, p.Token))
        .ToArray();

    /// <summary>
    /// Algorithmic suffix-stripping stemmer for query words. Yields candidate base
    /// forms (without removing the original token from the query). Handles -s, -ed,
    /// -ing, and -able/-ible inflections including doubled-consonant cases (dropped →
    /// drop, dragging → drag) and silent-e cases (closing → close, themed → theme).
    /// The -able/-ible rule matters because agents describe intent adjectivally
    /// ("swipeable rows", "resizable panes") while the corpus indexes the verb.
    ///
    /// We emit multiple candidates per word to avoid false stems (e.g. "scrolled"
    /// stems to BOTH "scroll" and "scrol" — the wrong one matches nothing in the
    /// corpus, the right one matches; BM25 handles the rest). This is much smaller
    /// and easier to maintain than the previous hand-curated 60+ entry Stems dict.
    /// </summary>
    public static IEnumerable<string> Stem(string word)
    {
        if (string.IsNullOrEmpty(word)) yield break;

        if (word.Length > 5 && word.EndsWith("ing", StringComparison.Ordinal))
        {
            var b = word[..^3];                  // editing → edit, scrolling → scroll
            yield return b;
            yield return b + "e";                // closing → close, sharing → share
            if (b.Length >= 2 && b[^1] == b[^2]) // dragging → dragg → drag
                yield return b[..^1];
        }
        else if (word.Length > 4 && word.EndsWith("ed", StringComparison.Ordinal))
        {
            var b = word[..^2];                  // edited → edit, scrolled → scroll
            yield return b;
            yield return word[..^1];             // themed → theme
            if (b.Length >= 2 && b[^1] == b[^2]) // dropped → dropp → drop
                yield return b[..^1];
        }
        else if (word.Length > 7
                 && (word.EndsWith("able", StringComparison.Ordinal) || word.EndsWith("ible", StringComparison.Ordinal)))
        {
            var b = word[..^4];                  // swipeable → swipe, scrollable → scroll
            yield return b;
            yield return b + "e";                // resizable → resize, collapsible → collapse
            if (b.Length >= 2 && b[^1] == b[^2]) // draggable → dragg → drag
                yield return b[..^1];
        }
        else if (word.Length > 3 && word.EndsWith("s", StringComparison.Ordinal)
                 && !word.EndsWith("ss", StringComparison.Ordinal))
        {
            yield return word[..^1];             // tabs → tab, folders → folder
            if (word.Length > 4 && word.EndsWith("es", StringComparison.Ordinal))
                yield return word[..^2];         // boxes → box
        }
    }

    /// <summary>
    /// Manual exceptions to the algorithmic stemmer: words whose base form cannot be
    /// derived by suffix stripping (e.g. "paginated" → "page", not "paginat").
    /// Keep this list small — most morphology is handled by <see cref="Stem"/>.
    /// </summary>
    public static readonly Dictionary<string, string[]> StemExceptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["paginated"] = ["page"],
        ["paginating"] = ["page"],
        ["paging"] = ["page"],
        ["pages"] = ["page"],
        ["tabbed"] = ["tab", "tabs"],   // need both forms; "tabs" is in Map
    };

    /// <summary>
    /// Replace the phrases in <see cref="ReplacePhrases"/> with their tokens, then append
    /// single-token equivalents for known multi-word phrases AND base forms
    /// for inflected words, KEEPING the original words. So "file picker" becomes
    /// tokens {file, picker, filepicker} and "tabbed editor" becomes {tabbed, editor,
    /// tab, tabs, edit} — BM25 can match on either form. Synonym expansion still runs
    /// after this step, so "tabs" → "tabview" via Map.
    /// </summary>
    public static string Preprocess(string query)
    {
        var lower = ApplyReplacements(query.ToLowerInvariant());
        var sb = new System.Text.StringBuilder(lower);
        foreach (var (phrase, replacement) in Phrases)
        {
            int searchFrom = 0;
            while (true)
            {
                int idx = lower.IndexOf(phrase, searchFrom, StringComparison.Ordinal);
                if (idx < 0) break;
                searchFrom = idx + phrase.Length;
                // Word-boundary check
                bool leftOk = idx == 0 || !IsWordChar(lower[idx - 1]);
                bool rightOk = searchFrom == lower.Length || !IsWordChar(lower[searchFrom]);
                if (!leftOk || !rightOk) continue;
                // Append the merged token instead of replacing — keeps both forms searchable
                sb.Append(' ').Append(replacement);
            }
        }

        // Stemming: append base forms for inflected query words. Whole-word match on
        // the lowercased original (we don't restem the appended phrase tokens — those
        // are already canonical). Manual exceptions take precedence over the
        // algorithmic stemmer for irregular cases (paginated → page, tabbed → tabs).
        foreach (var word in lower.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (StemExceptions.TryGetValue(word, out var bases))
            {
                foreach (var b in bases) sb.Append(' ').Append(b);
            }
            else
            {
                foreach (var b in Stem(word)) sb.Append(' ').Append(b);
            }
        }

        return sb.ToString();
    }

    private static string ApplyReplacements(string lower)
    {
        foreach (var (phrase, token) in ReplacePhrases)
        {
            int searchFrom = 0;
            while (true)
            {
                int idx = lower.IndexOf(phrase, searchFrom, StringComparison.Ordinal);
                if (idx < 0) break;
                int end = idx + phrase.Length;
                bool leftOk = idx == 0 || !IsWordChar(lower[idx - 1]);
                bool rightOk = end == lower.Length || !IsWordChar(lower[end]);
                if (!leftOk || !rightOk)
                {
                    searchFrom = end;
                    continue;
                }
                lower = string.Concat(lower.AsSpan(0, idx), token, lower.AsSpan(end));
                searchFrom = idx + token.Length;
            }
        }
        return lower;
    }

    private static readonly char[] WordSeparators = [' ', '\t', '\n', '\r', ',', '.', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\''];

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// Maps query terms to the extra terms they should also match: common cross-platform and
    /// descriptive UI vocabulary to WinUI 3 control names, so agents familiar with
    /// HTML/React/WPF/iOS terminology still find the right controls. Only one-way
    /// expansions are used: the site's two-way groups can turn an unrelated control
    /// name into an exact-name match (for example, audio into Sound).
    /// Keys and values are lowercase; lookup is case-insensitive.
    /// </summary>
    public static readonly Dictionary<string, string[]> Map = Vocabulary.Expansions
        .ToDictionary(e => e.Term, e => e.To, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Expand a tokenized query by appending synonyms for each known term.
    /// Returns the original tokens plus any synonym tokens.
    ///
    /// Compound-suffix guard: if the query already contains a more-specific compound
    /// ending with the current token (e.g. "file picker" → tokens [file, picker, filepicker]),
    /// we skip synonym expansion for the bare token. The compound form already pins
    /// the user's intent; expanding the bare suffix would pull in unrelated controls
    /// (e.g. "picker" → ComboBox/DatePicker/TimePicker, which all dilute "file picker"
    /// when the user clearly wants the file picker pattern).
    /// </summary>
    public static string[] Expand(string[] queryWords)
    {
        var result = new List<string>(queryWords);
        var seen = new HashSet<string>(queryWords, StringComparer.OrdinalIgnoreCase);
        var queryWordSet = new HashSet<string>(queryWords, StringComparer.OrdinalIgnoreCase);
        foreach (var w in queryWords)
        {
            if (Map.TryGetValue(w, out var syns))
            {
                bool hasCompoundParent = queryWordSet.Any(other =>
                    other.Length > w.Length && other.EndsWith(w, StringComparison.OrdinalIgnoreCase));
                if (hasCompoundParent) continue;

                foreach (var s in syns)
                {
                    if (seen.Add(s)) result.Add(s);
                }
            }
        }
        return result.ToArray();
    }
}
