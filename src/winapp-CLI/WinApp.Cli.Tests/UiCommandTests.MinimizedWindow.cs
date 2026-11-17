// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;
using Spectre.Console.Testing;
using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Tests;

public partial class UiCommandTests
{
    private async Task<int> RunWithMinimizedTargetAsync(System.CommandLine.Command command, string[] args, bool minimized = true)
    {
        _fakeTargetResolver.TargetResult.WindowHandle = 4242;
        _fakeTargetResolver.TargetResult.WindowTitle = "Calculator";
        var previous = UiErrors.s_isWindowMinimized;
        UiErrors.s_isWindowMinimized = hwnd => minimized && hwnd == 4242;
        try
        {
            return await ParseAndInvokeWithCaptureAsync(command, args);
        }
        finally
        {
            UiErrors.s_isWindowMinimized = previous;
        }
    }

    [TestMethod]
    public async Task ElementNotFound_MinimizedTarget_TellsHowToRestoreTheWindow()
    {
        _fakeUia.FindSingleResult = null;

        var exitCode = await RunWithMinimizedTargetAsync(
            GetRequiredService<UiInvokeCommand>(), ["num7Button", "-a", "calculator", "--json"]);

        Assert.AreEqual(1, exitCode);
        AssertJsonErrorCode("element_not_found");
        var error = ConsoleStdErr.ToString();
        Assert.Contains("\"recoveryHint\"", error);
        Assert.Contains("is minimized", error);
        Assert.Contains("focus -w 4242", error);
        Assert.IsEmpty(_fakeDesktopForeground.RestoreRequests, "A failed lookup must not restore the window itself.");
    }

    [TestMethod]
    [DoNotParallelize] // redirects the process-wide Console.Error to capture the JSON error envelope
    [DataRow("touch")]
    [DataRow("pen")]
    public async Task PointerElementNotFound_MinimizedTarget_TellsHowToRestoreTheWindow(string verb)
    {
        _fakeUia.FindSingleResult = null;
        System.CommandLine.Command command = verb == "touch"
            ? GetRequiredService<UiTouchCommand>()
            : GetRequiredService<UiPenCommand>();

        // touch and pen write the JSON error straight to Console.Error.
        var previousErr = Console.Error;
        using var errCapture = new StringWriter();
        Console.SetError(errCapture);
        int exitCode;
        try
        {
            exitCode = await RunWithMinimizedTargetAsync(command, ["num7Button", "-a", "calculator", "--json"]);
        }
        finally
        {
            Console.SetError(previousErr);
        }

        Assert.AreEqual(1, exitCode);
        var error = errCapture.ToString();
        AssertJsonErrorCodeIn(error, "element_not_found");
        Assert.Contains("\"recoveryHint\"", error);
        Assert.Contains("focus -w 4242", error);
        Assert.IsEmpty(_fakeDesktopForeground.RestoreRequests, "A failed lookup must not restore the window itself.");
    }

    [TestMethod]
    public async Task ElementNotFound_VisibleTarget_KeepsSelectorAdvice()
    {
        _fakeUia.FindSingleResult = null;

        var exitCode = await RunWithMinimizedTargetAsync(
            GetRequiredService<UiInvokeCommand>(), ["num7Button", "-a", "calculator", "--json"], minimized: false);

        Assert.AreEqual(1, exitCode);
        Assert.IsFalse(ConsoleStdErr.ToString().Contains("minimized", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Search_NoMatchesInMinimizedTarget_WarnsHowToRestore()
    {
        _fakeUia.SearchResult = [];
        var previousAmbient = AnsiConsole.Console;
        var ambient = new TestConsole();
        AnsiConsole.Console = ambient;
        int exitCode;
        try
        {
            exitCode = await RunWithMinimizedTargetAsync(
                GetRequiredService<UiSearchCommand>(), ["CalculatorResults", "-a", "calculator"]);
        }
        finally
        {
            AnsiConsole.Console = previousAmbient;
        }

        Assert.AreEqual(1, exitCode);
        Assert.Contains("\"Calculator\" (HWND 4242) is minimized", ambient.Output);
        Assert.Contains("focus -w 4242", ambient.Output);
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(true, true)]
    [DataRow(false, true)]
    public async Task Search_Json_EmitsRecoveryOnlyForEmptyMinimizedTarget(bool minimized, bool hasMatches)
    {
        _fakeUia.SearchResult = hasMatches
            ? [new UiElement { Type = "Button", Name = "Seven", Selector = "btn-seven-1" }]
            : [];
        var command = new UiSearchCommand();
        var handler = new UiSearchCommand.Handler(
            _fakeTargetResolver, _fakeUia, GetRequiredService<IUiSelectorParser>(),
            TestAnsiConsole, _fakeDesktopLock, NullLogger<UiSearchCommand>.Instance);
        command.SetAction((result, ct) => handler.InvokeAsync(result, ct));

        var exitCode = await RunWithMinimizedTargetAsync(
            command, ["Seven", "-a", "calculator", "--json"], minimized);

        Assert.AreEqual(hasMatches ? 0 : 1, exitCode);
        using var output = JsonDocument.Parse(TestAnsiConsole.Output);
        var result = output.RootElement;
        Assert.AreEqual(3, result.EnumerateObject().Count());
        Assert.AreEqual(hasMatches ? 1 : 0, result.GetProperty("matchCount").GetInt32());
        Assert.IsFalse(result.GetProperty("hasMore").GetBoolean());
        Assert.AreEqual(hasMatches ? 1 : 0, result.GetProperty("matches").GetArrayLength());
        if (hasMatches)
        {
            Assert.AreEqual("btn-seven-1", result.GetProperty("matches")[0].GetProperty("selector").GetString());
        }

        if (minimized && !hasMatches)
        {
            using var error = JsonDocument.Parse(ConsoleStdErr.ToString());
            var info = error.RootElement.GetProperty("error");
            Assert.AreEqual("element_not_found", info.GetProperty("code").GetString());
            Assert.AreEqual("Seven", info.GetProperty("selector").GetString());
            var hint = info.GetProperty("recoveryHint").GetString();
            Assert.Contains("is minimized", hint);
            Assert.Contains("focus -w 4242", hint);
        }
        else
        {
            Assert.AreEqual("", ConsoleStdErr.ToString());
        }
        Assert.IsEmpty(_fakeDesktopForeground.RestoreRequests, "Search must not restore the window itself.");
    }
}
