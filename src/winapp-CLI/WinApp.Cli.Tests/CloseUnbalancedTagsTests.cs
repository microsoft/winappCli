// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.Controls;

namespace WinApp.Cli.Tests;

/// <summary>
/// Hermetic tests for <see cref="ControlSnippetText.CloseUnbalancedTags"/> — the XAML
/// repair every fetched sample passes through. Two invariants: a sample is never
/// shortened (find-ui results are paste-ready, issue #716), and a fragment left with
/// open elements gets its closers so <see cref="ScenarioSanitizer"/> doesn't drop it.
/// </summary>
[TestClass]
public class CloseUnbalancedTagsTests
{
    [TestMethod]
    public void LongBalancedMarkup_ReturnedWhole()
    {
        // The single most important property: no cap, no "...truncated" marker, no cut.
        var inner = string.Concat(Enumerable.Repeat("<TextBlock Text=\"padding padding padding\" />\n", 200));
        var xaml = $"<StackPanel>\n{inner}</StackPanel>";

        var result = ControlSnippetText.CloseUnbalancedTags(xaml);

        Assert.AreEqual(xaml, result);
        StringAssert.Contains(result, "</StackPanel>");
        Assert.IsFalse(result.Contains("truncated"), "samples must be emitted whole");
    }

    [TestMethod]
    public void UnclosedElements_ClosersAppendedInnermostFirst()
    {
        var result = ControlSnippetText.CloseUnbalancedTags("<StackPanel><Grid><TextBox Text=\"x\" />");

        Assert.AreEqual("<StackPanel><Grid><TextBox Text=\"x\" /></Grid></StackPanel>", result);
    }

    [TestMethod]
    public void TagTextInsideComment_NotTreatedAsElement()
    {
        // "ObservableCollection<CustomDataObject>" inside a comment must not produce a
        // bogus </CustomDataObject> closer.
        var xaml = "<Grid>\n  <!-- bind to ObservableCollection<CustomDataObject> -->\n  <TextBlock />\n</Grid>";

        var result = ControlSnippetText.CloseUnbalancedTags(xaml);

        Assert.AreEqual(xaml, result);
    }
}
