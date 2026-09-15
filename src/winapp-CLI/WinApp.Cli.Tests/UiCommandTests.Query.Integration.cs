// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;

namespace WinApp.Cli.Tests;

public partial class UiCommandTests
{
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
        fx.OnUiThread(() =>
        {
            fx.Form.Controls["delayedRoot"]!.Dispose();
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
