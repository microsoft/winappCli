// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;

namespace WinApp.Cli.Tests;

public partial class UiCommandTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task QueryOptions_RealRetainedReadRaceRetriesQueryWithoutSerializedResolution(bool property)
    {
        if (!Environment.UserInteractive) { Assert.Inconclusive("Requires an interactive desktop."); }
        using var fx = new UiaTestFixture();
        var svc = RealStringQueryService(fx);
        var target = _fakeTargetResolver.TargetResult;
        var selector = new UiSelector { Root = new() { Query = "fixtureForm" }, Query = "txtValue", ControlType = "Edit" };
        var original = await svc.FindSingleElementAsync(target, selector, CancellationToken.None);
        Assert.IsNotNull(original);
        var nativeProcessId = UiAutomationService.s_getElementProcessId;
        var nativeRoot = UiAutomationService.s_getRootElement;
        var reads = 0;
        var lookups = 0;
        try
        {
            UiAutomationService.s_getRootElement = (service, queryTarget, strict) =>
            {
                lookups++;
                return nativeRoot(service, queryTarget, strict);
            };
            UiAutomationService.s_getElementProcessId = element =>
            {
                if (++reads == 1)
                {
                    fx.OnUiThread(() =>
                    {
                        var replacement = new TextBox { Name = "txtValue", Text = "ready" };
                        fx.Form.Controls.Add(replacement);
                        _ = replacement.Handle;
                        fx.ValueBox.Dispose();
                    });
                    throw new System.Runtime.InteropServices.COMException(
                        "Retained provider disappeared.", unchecked((int)0x80040201));
                }
                return nativeProcessId(element);
            };
            var args = new List<string>
            {
                "txtValue", "-w", fx.Hwnd.ToString(), "--root", "fixtureForm", "--type", "Edit",
                "--value", "ready", "--timeout", "5000", "--json",
            };
            if (property) { args.AddRange(["--property", "Value"]); }
            var exit = await ParseAndInvokeWithCaptureAsync(RealStringQueryCommand("wait-for", svc), args.ToArray());

            Assert.AreEqual(0, exit, $"{TestAnsiConsole.Output} {ConsoleStdErr}");
            Assert.AreEqual(2, reads);
            Assert.AreEqual(2, lookups, "Retry must resolve the complete root-constrained query.");
            Assert.AreEqual(1, _fakePollDelay.CallCount);
            Assert.AreEqual(0, svc.SerializedElementResolutionCount);
            Assert.IsFalse(TestAnsiConsole.Output.Contains(original.Selector!, StringComparison.Ordinal));
            StringAssert.Contains(TestAnsiConsole.Output, "ready");
        }
        finally { UiAutomationService.ResetNativeSeams(); }
    }

    [TestMethod]
    [DataRow("search", false)]
    [DataRow("search", true)]
    [DataRow("wait-for", false)]
    [DataRow("wait-for", true)]
    public async Task QueryOptions_RealApp_RootSlugAcrossDuplicateWindows(string command, bool useSecondWindow)
    {
        if (!Environment.UserInteractive) { Assert.Inconclusive("Requires an interactive desktop."); }
        using var first = new UiaTestFixture();
        using var second = new UiaTestFixture();
        var selected = useSecondWindow ? second : first;
        var hwnd = selected.Hwnd.ToString();
        selected.OnUiThread(() => selected.InvokableChildLabel.AccessibleName = "Inside selected window");
        var rootResult = await RunQueryProcessAsync(
            ["ui", "search", "pnlInsideInvoke", "-w", hwnd, "--type", "Pane", "--json"]);
        Assert.AreEqual(0, rootResult.ExitCode, rootResult.Stderr);
        var roots = JsonSerializer.Deserialize<JsonElement>(rootResult.Stdout).GetProperty("matches");
        Assert.AreEqual(1, roots.GetArrayLength(), rootResult.Stdout);
        var slug = roots[0].GetProperty("selector").GetString()!;

        var scoped = await RunQueryProcessAsync(
            ["ui", "search", "Inside", "-w", hwnd, "--root", slug, "--type", "Text", "--json"]);
        Assert.AreEqual(0, scoped.ExitCode, scoped.Stderr);
        StringAssert.Contains(scoped.Stdout, "Inside selected window");

        var args = new List<string>
        {
            "ui", command, "Inside", "-a", selected.ProcessId.ToString(),
            "--root", slug, "--type", "Text", "--json",
        };
        if (command == "wait-for") { args.AddRange(["--gone", "--timeout", "1000"]); }
        var result = await RunQueryProcessAsync(args.ToArray());
        Assert.AreEqual(command == "search" ? 0 : 1, result.ExitCode, $"{result.Stdout} {result.Stderr}");
        var json = JsonSerializer.Deserialize<JsonElement>(result.Stdout);
        if (command == "search")
        {
            Assert.AreEqual(1, json.GetProperty("matchCount").GetInt32());
            Assert.AreEqual("Inside selected window", json.GetProperty("matches")[0].GetProperty("name").GetString());
        }
        else
        {
            Assert.IsTrue(json.GetProperty("timedOut").GetBoolean(), result.Stdout);
            Assert.IsTrue(json.GetProperty("waitedMs").GetInt32() >= 1000, result.Stdout);

            var disappearance = RunQueryProcessAsync(
                ["ui", "wait-for", "Inside", "-a", selected.ProcessId.ToString(),
                 "--root", slug, "--type", "Text", "--gone", "--timeout", "10000", "--json"]);
            await Task.Delay(1500);
            Assert.IsFalse(disappearance.IsCompleted, "The root and its descendant still exist.");
            selected.OnUiThread(() => selected.InvokableMiddlePanel.Dispose());
            var gone = await disappearance;
            Assert.AreEqual(0, gone.ExitCode, $"{gone.Stdout} {gone.Stderr}");
            var goneJson = JsonSerializer.Deserialize<JsonElement>(gone.Stdout);
            Assert.IsFalse(goneJson.GetProperty("found").GetBoolean());
            Assert.IsFalse(goneJson.GetProperty("timedOut").GetBoolean());
        }
    }

    [TestMethod]
    public async Task QueryOptions_RealApp_DelayedRootAndReadCommands()
    {
        if (!Environment.UserInteractive) { Assert.Inconclusive("Requires an interactive desktop."); }
        using var fx = new UiaTestFixture();
        var hwnd = fx.Hwnd.ToString();
        var wait = RunQueryProcessAsync(
            ["ui", "wait-for", "delayedValue", "-w", hwnd, "--root", "delayedRoot", "--type", "TextBox",
             "--value", "ready", "--timeout", "10000", "--json"]);
        await Task.Delay(1500);
        if (wait.IsCompleted)
        {
            var early = await wait;
            Assert.Fail($"Wait exited before the root appeared: {early.ExitCode} {early.Stdout} {early.Stderr}");
        }
        fx.OnUiThread(() =>
        {
            var root = new Panel { Name = "delayedRoot", Width = 300, Height = 100 };
            root.Controls.Add(new TextBox { Name = "delayedValue", Text = "ready", Width = 200 });
            fx.Form.Controls.Add(root);
            root.BringToFront();
        });
        var result = await wait;
        Assert.AreEqual(0, result.ExitCode, result.Stderr);
        var json = JsonSerializer.Deserialize<JsonElement>(result.Stdout);
        Assert.IsTrue(json.GetProperty("found").GetBoolean());
        var className = json.GetProperty("element").GetProperty("className").GetString()!;

        foreach (var command in new[] { "search", "get-property", "get-value" })
        {
            var read = await RunQueryProcessAsync(
                ["ui", command, "delayedValue", "-w", hwnd, "--root", "delayedRoot",
                 "--type", "Edit", "--class-name", className.ToUpperInvariant(), "--json"]);
            Assert.AreEqual(0, read.ExitCode, $"{command}: {read.Stderr}");
            Assert.IsTrue(read.Stdout.Contains("ready", StringComparison.OrdinalIgnoreCase), read.Stdout);
        }
        var invalid = await RunQueryProcessAsync(
            ["ui", "search", "delayedValue", "-w", hwnd, "--type", "NotAType", "--json"]);
        Assert.AreEqual(1, invalid.ExitCode);
        StringAssert.Contains(invalid.Stderr, "invalid_arguments");

        var replacementWait = RunQueryProcessAsync(
            ["ui", "wait-for", "delayedValue", "-w", hwnd, "--root", "delayedRoot",
             "--type", "Edit", "--value", "replaced", "--timeout", "10000", "--json"]);
        await Task.Delay(1500);
        Assert.IsFalse(replacementWait.IsCompleted, "Wait should keep polling the existing, not-ready root.");
        fx.OnUiThread(() => fx.Form.Controls["delayedRoot"]!.Dispose());
        // Disposal can return before UIA removes the provider. Do not briefly expose two roots
        // with the same ID: that is genuine ambiguity, not the replacement race under test.
        var removed = await RunQueryProcessAsync(
            ["ui", "wait-for", "delayedRoot", "-w", hwnd, "--type", "Pane",
             "--gone", "--timeout", "5000", "--json"]);
        Assert.AreEqual(0, removed.ExitCode, $"{removed.Stdout} {removed.Stderr}");
        Assert.IsFalse(JsonSerializer.Deserialize<JsonElement>(removed.Stdout).GetProperty("timedOut").GetBoolean());
        fx.OnUiThread(() =>
        {
            var replacement = new Panel { Name = "delayedRoot", Width = 300, Height = 100 };
            replacement.Controls.Add(new TextBox { Name = "delayedValue", Text = "replaced", Width = 200 });
            fx.Form.Controls.Add(replacement);
            replacement.BringToFront();
        });
        var replaced = await replacementWait;
        Assert.AreEqual(0, replaced.ExitCode, replaced.Stderr);
        Assert.IsTrue(JsonSerializer.Deserialize<JsonElement>(replaced.Stdout).GetProperty("found").GetBoolean());
    }

    private static Task<(int ExitCode, string Stdout, string Stderr)> RunQueryProcessAsync(string[] args)
    {
        // Test project references copy the self-contained apphost without its runtime.
        // Use the test host's framework/dependency configuration for normal targeted test runs.
        // A published CLI can be supplied to repeat the same real-app regression under NativeAOT.
        if (Environment.GetEnvironmentVariable("WINAPP_QUERY_TEST_CLI") is { Length: > 0 } executable)
        {
            return RunProcessAsync(executable, args, TimeSpan.FromSeconds(20));
        }
        return RunProcessAsync("dotnet",
            ["exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "WinApp.Cli.Tests.runtimeconfig.json"),
             "--depsfile", Path.Combine(AppContext.BaseDirectory, "WinApp.Cli.Tests.deps.json"),
             Path.Combine(AppContext.BaseDirectory, "winapp.dll"), .. args], TimeSpan.FromSeconds(20));
    }
}
