// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.ApiSearch;

/// <summary>
/// Lexical relevance scoring for the <c>find-api</c> search: exact &gt; prefix &gt;
/// contains &gt; acronym &gt; all-terms &gt; fuzzy subsequence.
/// </summary>
/// <remarks>
/// Every band below <c>prefix</c> requires the match to begin where a word begins.
/// Matching anywhere at all turns a short query into noise: <c>llm</c> is a substring of
/// <c>scroLLMode</c> and a subsequence of much of the API surface, so searching for a
/// language model returned scrolling APIs. An honest miss is more useful than a
/// confident unrelated match.
/// </remarks>
internal static class Scoring
{
    public static int GetMatchScore(string name, string fullName, string query)
    {
        string text = query.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }
        if (name.Equals(text, StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }
        if (name.StartsWith(text, StringComparison.OrdinalIgnoreCase))
        {
            return 80;
        }
        if (ContainsAtWordStart(name, text) || ContainsAtWordStart(fullName, text))
        {
            return 60;
        }
        string acronym = new string(name.Where(char.IsUpper).ToArray());
        if (acronym.Length >= 2 && acronym.Contains(text, StringComparison.OrdinalIgnoreCase))
        {
            return 50;
        }
        string[] terms = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length > 1)
        {
            bool allTermsMatch = terms.All(term =>
                ContainsAtWordStart(name, term) || ContainsAtWordStart(fullName, term));
            if (allTermsMatch)
            {
                return 40;
            }
        }
        if (IsFuzzySubsequenceFromWordStart(name, text))
        {
            return 20;
        }
        return 0;
    }

    /// <summary>
    /// Whether <paramref name="needle"/> appears in <paramref name="haystack"/> starting
    /// where a word starts, so <c>Mode</c> matches <c>ScrollMode</c> but <c>llm</c> does not.
    /// </summary>
    private static bool ContainsAtWordStart(string haystack, string needle)
    {
        int index = haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            if (IsWordStart(haystack, index))
            {
                return true;
            }
            int next = haystack.IndexOf(needle, index + 1, StringComparison.OrdinalIgnoreCase);
            index = next;
        }
        return false;
    }

    /// <summary>
    /// Where a word begins in an API name: the start, anything after a separator, and the
    /// capital that opens a new word in <c>PascalCase</c> — including the last capital of
    /// a run, so <c>Stream</c> is found in <c>IOStream</c>.
    /// </summary>
    private static bool IsWordStart(string text, int index)
    {
        if (index == 0)
        {
            return true;
        }
        char current = text[index];
        char previous = text[index - 1];
        if (!char.IsLetterOrDigit(previous))
        {
            return true;
        }
        if (!char.IsUpper(current))
        {
            // A digit run reads as its own word ("Int32" is "Int" then "32").
            return char.IsDigit(current) != char.IsDigit(previous);
        }
        return !char.IsUpper(previous)
            || (index + 1 < text.Length && char.IsLower(text[index + 1]));
    }

    /// <summary>
    /// Typo tolerance: the query's characters appear in order, starting from a word.
    /// Anchoring to a word start is what separates <c>Buton</c> finding <c>Button</c>
    /// from a three-letter query matching most of the API surface by coincidence.
    /// </summary>
    private static bool IsFuzzySubsequenceFromWordStart(string text, string pattern)
    {
        for (int start = 0; start < text.Length; start++)
        {
            if (!IsWordStart(text, start)
                || char.ToLowerInvariant(text[start]) != char.ToLowerInvariant(pattern[0]))
            {
                continue;
            }
            if (IsSubsequence(text, pattern, start))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsSubsequence(string text, string pattern, int start)
    {
        string haystack = text.ToLowerInvariant();
        string needle = pattern.ToLowerInvariant();
        int startIndex = start;
        foreach (char c in needle)
        {
            int idx = haystack.IndexOf(c, startIndex);
            if (idx < 0)
            {
                return false;
            }
            startIndex = idx + 1;
        }
        return true;
    }
}
