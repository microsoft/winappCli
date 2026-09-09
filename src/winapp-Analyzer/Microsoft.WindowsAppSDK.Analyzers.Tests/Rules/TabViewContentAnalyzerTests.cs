// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Threading.Tasks;
using Microsoft.WindowsAppSDK.Analyzers.Rules;
using Xunit;

namespace Microsoft.WindowsAppSDK.Analyzers.Tests.Rules;

public sealed class TabViewContentAnalyzerTests
{
    [Fact]
    public async Task Wui2001FlagsRawTextBoxAsTabContent()
    {
        // Heuristic fallback path: variable named "tab*" + raw control assignment.
        await new AnalyzerTest<TabViewContentAnalyzer>()
            .WithSource(@"
class TextBox {}
class TabViewItem { public object? Content { get; set; } }
class C { void M() { var tabItem = new TabViewItem(); tabItem.Content = new TextBox(); } }")
            .ExpectDiagnostic(DiagnosticIds.TabViewRawContent)
            .RunAsync();
    }

    [Fact]
    public async Task Wui2001DoesNotFlagFrameAsContent()
    {
        await new AnalyzerTest<TabViewContentAnalyzer>()
            .WithSource(@"
class Frame {}
class TabViewItem { public object? Content { get; set; } }
class C { void M() { var tabItem = new TabViewItem(); tabItem.Content = new Frame(); } }")
            .RunAsync();
    }

    [Fact]
    public async Task Wui2001DoesNotFlagContentAssignmentOnNonTabType()
    {
        // False-positive guard: ContentControl.Content assignment on non-tab variable.
        await new AnalyzerTest<TabViewContentAnalyzer>()
            .WithSource(@"
class TextBox {}
class ContentControl { public object? Content { get; set; } }
class C { void M() { var panel = new ContentControl(); panel.Content = new TextBox(); } }")
            .RunAsync();
    }
}
