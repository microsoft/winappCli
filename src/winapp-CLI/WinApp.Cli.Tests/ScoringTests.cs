// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.ApiSearch;

namespace WinApp.Cli.Tests;

/// <summary>
/// Covers how a query is scored against an API name. The bands are consumed in two very
/// different places — search ranking, and the "did you mean" suggestions behind
/// <c>check-property</c>, which keep anything at or above 40 — so a change that looks
/// like better ranking can quietly delete a suggestion. These lock the behavior that has
/// to survive any scoring change.
/// </summary>
[TestClass]
public sealed class ScoringTests
{
    [TestMethod]
    [DataRow("ScrollMode", "ScrollMode", 100, DisplayName = "exact name")]
    [DataRow("ScrollMode", "scrollmode", 100, DisplayName = "exact name, any case")]
    [DataRow("ScrollMode", "Scroll", 80, DisplayName = "prefix")]
    [DataRow("ScrollMode", "Mode", 60, DisplayName = "a whole word inside the name")]
    [DataRow("ContentTemplate", "Content", 80, DisplayName = "a leading word is a prefix")]
    [DataRow("OnPropertyChanged", "PropertyChanged", 60, DisplayName = "several words inside the name")]
    [DataRow("ScrollViewer", "SV", 50, DisplayName = "acronym")]
    public void GetMatchScore_KeepsTheMatchesCallersDependOn(string name, string query, int expected)
    {
        Assert.AreEqual(expected, Scoring.GetMatchScore(name, "Windows.UI.Xaml.Controls." + name, query));
    }

    [TestMethod]
    public void GetMatchScore_MultiWordQueryMatchingSeveralWords_StaysAtOrAboveTheSuggestionThreshold()
    {
        // check-property keeps suggestions scoring 40 or more. A query whose words all
        // appear in the name has to stay at that floor or the suggestion disappears.
        int score = Scoring.GetMatchScore("AcrylicBrush", "Windows.UI.Xaml.Media.AcrylicBrush", "acrylic brush");

        Assert.IsGreaterThanOrEqualTo(40, score);
    }

    [TestMethod]
    public void GetMatchScore_TypoInTheName_StillScoresSomething()
    {
        // Fuzzy matching exists so a mistyped name still finds its API.
        Assert.IsGreaterThan(0, Scoring.GetMatchScore("Button", "Windows.UI.Xaml.Controls.Button", "Buton"));
    }

    [TestMethod]
    [DataRow("ScrollMode", "llm", DisplayName = "letters spanning two words (scroLLMode)")]
    [DataRow("ScrollViewer", "llm", DisplayName = "no such letters in order from any word")]
    [DataRow("ListViewBase", "llm", DisplayName = "letters in order but not from a word start")]
    public void GetMatchScore_LettersThatDoNotStartAWord_ScoreNothing(string name, string query)
    {
        // 'llm' is a substring of scroLLMode and a subsequence of many API names. Ranking
        // scrolling APIs for it is worse than returning nothing: it reads as an answer.
        Assert.AreEqual(0, Scoring.GetMatchScore(name, "Windows.UI.Xaml.Controls." + name, query));
    }

    [TestMethod]
    public void GetMatchScore_EmptyQuery_ScoresNothing()
    {
        Assert.AreEqual(0, Scoring.GetMatchScore("Button", "Windows.UI.Xaml.Controls.Button", "   "));
    }
}
