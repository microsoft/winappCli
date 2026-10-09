// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Spectre.Console;
using Spectre.Console.Testing;
using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;

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
        StringAssert.Contains(error, "\"recoveryHint\"");
        StringAssert.Contains(error, "is minimized");
        StringAssert.Contains(error, "focus -w 4242");
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
        StringAssert.Contains(ambient.Output, "\"Calculator\" (HWND 4242) is minimized");
        StringAssert.Contains(ambient.Output, "focus -w 4242");
    }
}
