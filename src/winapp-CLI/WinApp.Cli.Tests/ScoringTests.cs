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
    [DataRow("LanguageModel", "language model")]
    [DataRow("LanguageModel", "  LANGUAGE   MODEL  ")]
    [DataRow("NavigationView", "navigation view")]
    [DataRow("GenerateResponseAsync", "generate response async")]
    [DataRow("XMLHttpRequest", "xml http request")]
    [DataRow("Int32", "int 32")]
    public void GetMatchScore_CompleteIdentifierWords_OutrankPartialMatches(string name, string query)
    {
        Assert.AreEqual(90, Scoring.GetMatchScore(name, "Example." + name, query));
        Assert.AreEqual(100, Scoring.GetMatchScore(name, "Example." + name, name));
    }

    [TestMethod]
    [DataRow("Language", "Windows.ApplicationModel.Example.Language", "language model")]
    [DataRow("LanguageModelContext", "Example.LanguageModelContext", "language model")]
    [DataRow("LanguageModel", "Example.LanguageModel", "lang uage model")]
    [DataRow("LanguageModel", "Example.LanguageModel", "model language")]
    public void GetMatchScore_IncompleteOrScatteredWords_AreNotCompleteIdentifierMatches(
        string name, string fullName, string query)
    {
        Assert.IsLessThan(90, Scoring.GetMatchScore(name, fullName, query));
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
    public void GetDescriptionScore_EveryQueryWordIsInTheSummary_Matches()
    {
        // "text generation" describes what the caller wants to do. LanguageModel is named
        // nothing like it, and its summary is the only place that intent is written down.
        int score = Scoring.GetDescriptionScore(
            "Provides text generation and embeddings using an on-device language model.",
            "text generation");

        Assert.IsGreaterThan(0, score);
    }

    [TestMethod]
    public void GetDescriptionScore_RanksBelowEveryNameMatch()
    {
        // A summary hit must never outrank a name hit, or a prose coincidence displaces
        // the API the caller actually named.
        int prose = Scoring.GetDescriptionScore("Sets the scrolling mode.", "scrolling mode");
        int weakestNameMatch = Scoring.GetMatchScore("Button", "Windows.UI.Xaml.Controls.Button", "Buton");

        Assert.IsLessThan(weakestNameMatch, prose);
    }

    [TestMethod]
    public void GetDescriptionScore_OnlySomeQueryWordsAppear_DoesNotMatch()
    {
        Assert.AreEqual(
            0,
            Scoring.GetDescriptionScore("Provides text layout and measurement.", "text generation"));
    }

    [TestMethod]
    public void GetDescriptionScore_NoSummary_DoesNotMatch()
    {
        Assert.AreEqual(0, Scoring.GetDescriptionScore(null, "text generation"));
    }

    [TestMethod]
    public void GetMatchScore_EmptyQuery_ScoresNothing()
    {
        Assert.AreEqual(0, Scoring.GetMatchScore("Button", "Windows.UI.Xaml.Controls.Button", "   "));
    }
}
