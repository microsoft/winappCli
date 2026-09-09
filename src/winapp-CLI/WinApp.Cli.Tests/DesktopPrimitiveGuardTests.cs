// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.RegularExpressions;

namespace WinApp.Cli.Tests;

/// <summary>
/// Guards the coordination invariant that every desktop-wide foreground or window-state change goes
/// through <c>IDesktopForegroundService</c> (issue #764).
/// </summary>
/// <remarks>
/// The value of coordination is that a reviewer can answer "what can move the foreground?" by reading
/// one file. A single direct <c>PInvoke.SetForegroundWindow</c> added later — even one that happens to
/// sit inside a desktop section today — silently reopens that question, and the next refactor may move
/// it outside the section. Asserting over the source keeps the invariant true by construction rather
/// than by convention.
/// </remarks>
[TestClass]
public class DesktopPrimitiveGuardTests
{
    /// <summary>The one file allowed to call the primitives directly.</summary>
    private const string ForegroundServiceFile = "IDesktopForegroundService.cs";

    private static readonly Regex s_directPrimitiveCall = new(
        @"PInvoke\s*\.\s*(SetForegroundWindow|ShowWindow)\s*\(",
        RegexOptions.Compiled);

    [TestMethod]
    public void NoProductionCodeCallsForegroundPrimitivesOutsideTheForegroundService()
    {
        var productionRoot = FindProductionRoot();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(productionRoot, "*.cs", SearchOption.AllDirectories))
        {
            // Skip build output: CsWin32 emits the P/Invoke declarations themselves under obj\.
            var relative = Path.GetRelativePath(productionRoot, file);
            if (relative.Contains($"obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || relative.Contains($"bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileName(file), ForegroundServiceFile, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            foreach (Match match in s_directPrimitiveCall.Matches(text))
            {
                var line = text.Take(match.Index).Count(c => c == '\n') + 1;
                offenders.Add($"{relative}({line}): {match.Value.Trim()}");
            }
        }

        Assert.AreEqual(
            0,
            offenders.Count,
            "Desktop foreground and window-state changes must go through IDesktopForegroundService so "
                + "they are coordinated and reviewable in one place. Offending call sites:\n"
                + string.Join("\n", offenders));
    }

    [TestMethod]
    public void TheGuardActuallyMatchesADirectPrimitiveCall()
    {
        // A source-scanning assertion is worthless if its pattern silently stops matching, so pin the
        // pattern against the exact shapes it is meant to catch.
        Assert.IsTrue(s_directPrimitiveCall.IsMatch("Windows.Win32.PInvoke.SetForegroundWindow(hwnd);"));
        Assert.IsTrue(s_directPrimitiveCall.IsMatch("PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_RESTORE);"));
        Assert.IsTrue(s_directPrimitiveCall.IsMatch("PInvoke . SetForegroundWindow ("));
        Assert.IsFalse(s_directPrimitiveCall.IsMatch("_desktopForeground.RequestForeground(hwnd);"));
        Assert.IsFalse(s_directPrimitiveCall.IsMatch("// mentions SetForegroundWindow in prose"));
    }

    /// <summary>Locates <c>src\winapp-CLI\WinApp.Cli</c> by walking up from the test binaries.</summary>
    private static string FindProductionRoot()
    {
        var directory = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && directory is not null; i++)
        {
            var candidate = Path.Combine(directory, "src", "winapp-CLI", "WinApp.Cli");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar));
        }

        throw new AssertInconclusiveException(
            "The WinApp.Cli production sources were not found, so the desktop-primitive guard could not run.");
    }
}
