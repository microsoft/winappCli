// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;
using WinApp.Cli.Models;

namespace WinApp.Cli.Tests;

/// <summary>
/// Covers <c>winapp ui search</c>: missing-app, the non-JSON render path (value/toggle/expand/scroll/
/// bounds decorations, invoke-via ancestor hint, truncation), the no-match exit code, and the
/// COMException / generic error branches. Exercised through the command layer with fakes.
/// </summary>
public partial class UiCommandTests
{
    [TestMethod]
    public async Task Search_MissingApp_ReturnsError()
    {
        var command = GetRequiredService<UiSearchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["button"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Search_NonJson_RendersMatchesWithDecorationsAndTruncation()
    {
        _fakeUia.SearchResult =
        [
            new UiElement
            {
                Id = "e0",
                Type = "Text",
                Name = "Label",
                Selector = "txt-label-1",
                InvokableAncestor = new UiElement { Id = "e9", Type = "Button", Name = "Submit", Selector = "btn-submit-9" },
            },
            new UiElement
            {
                Id = "e1",
                Type = "CheckBox",
                Name = "Enabled",
                Selector = "chk-enabled-2",
                Value = "checked",
                ToggleState = "on",
                ExpandState = "expanded",
                ScrollDir = "vh",
                X = 5,
                Y = 6,
                Width = 120,
                Height = 24,
            },
            new UiElement { Id = "e2", Type = "Button", Name = "Extra", Selector = "btn-extra-3" },
        ];

        var command = GetRequiredService<UiSearchCommand>();
        // --max 2 with 3 available matches trips the "hasMore" truncation branch.
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["e", "-a", "TestApp", "--max", "2"]);

        Assert.AreEqual(0, exitCode);
        Assert.Contains("txt-label-1", TestAnsiConsole.Output);
        Assert.Contains("invoke via", TestAnsiConsole.Output);
        Assert.Contains("btn-submit-9", TestAnsiConsole.Output);
        Assert.Contains("chk-enabled-2", TestAnsiConsole.Output);
        Assert.Contains("checked", TestAnsiConsole.Output);
        // The third match is truncated away.
        Assert.IsFalse(TestAnsiConsole.Output.Contains("btn-extra-3"));
    }

    [TestMethod]
    public async Task Search_NonJson_NoMatches_ReturnsError()
    {
        _fakeUia.SearchResult = [];
        var command = GetRequiredService<UiSearchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["nothing", "-a", "TestApp"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Search_Com_ReturnsError()
    {
        _fakeUia.SearchThrow = FakeComException;
        var command = GetRequiredService<UiSearchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["button", "-a", "TestApp"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Search_Generic_ReturnsError()
    {
        _fakeUia.SearchThrow = FakeGenericException;
        var command = GetRequiredService<UiSearchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["button", "-a", "TestApp"]);
        Assert.AreEqual(1, exitCode);
    }
}
