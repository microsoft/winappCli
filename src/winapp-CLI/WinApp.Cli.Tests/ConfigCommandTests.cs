// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class ConfigCommandTests() : BaseCommandTests(logLevel: Microsoft.Extensions.Logging.LogLevel.Warning)
{
    private UserSettings _settings = null!;

    // The file the DevTools toolbar reads and writes (DevToolsSettings.h: <state>\devtools-<name>.setting).
    private string ToolbarFile => Path.Combine(_settings.StateDirectory!, "devtools-DefaultMode.setting");

    [TestInitialize]
    public void UseTemporaryState()
    {
        _settings = GetRequiredService<UserSettings>();
        _settings.StateDirectory = _tempDirectory.CreateSubdirectory("state").FullName;
    }

    private async Task<(int ExitCode, string Output)> Run(params string[] args)
    {
        TestAnsiConsole.Clear(home: false);
        var exitCode = await ParseAndInvokeWithCaptureAsync(GetRequiredService<WinAppRootCommand>(), ["config", .. args]);
        return (exitCode, TestAnsiConsole.Output.Replace("\r\n", "\n"));
    }

    private static JsonElement Json(string output) => JsonDocument.Parse(output[output.IndexOf('{')..]).RootElement.Clone();

    [TestMethod]
    public async Task List_ShowsEveryKeyWithValueSourceAndTheScope()
    {
        var (exitCode, output) = await Run("list");
        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(output, "run.devtools = on (default)");
        StringAssert.Contains(output, "on, off or headless");
        StringAssert.Contains(output, "per-user winapp settings, separate from a project's winapp.yaml");
    }

    [TestMethod]
    public async Task List_Json_DescribesEachKey()
    {
        var (exitCode, output) = await Run("list", "--json");
        Assert.AreEqual(0, exitCode);
        var setting = Json(output).GetProperty("settings").EnumerateArray().Single();
        Assert.AreEqual("run.devtools", setting.GetProperty("key").GetString());
        Assert.AreEqual("on", setting.GetProperty("value").GetString());
        Assert.AreEqual("default", setting.GetProperty("source").GetString());
        Assert.AreEqual("on", setting.GetProperty("default").GetString());
        CollectionAssert.AreEqual(new List<string> { "on", "off", "headless" }, UserSettings.RunDevTools.Values.ToList());
        CollectionAssert.AreEqual(UserSettings.RunDevTools.Values.ToArray(),
            setting.GetProperty("values").EnumerateArray().Select(value => value.GetString()).ToArray());
        StringAssert.Contains(output, "isn't given", "JSON keeps apostrophes readable");
    }

    [TestMethod]
    [DataRow("off", "off")]
    [DataRow("HEADLESS", "headless")]
    [DataRow("On", "on")]
    public async Task SetGetUnset_RoundTrip(string typed, string stored)
    {
        Assert.AreEqual(0, (await Run("set", "run.devtools", typed)).ExitCode);
        Assert.AreEqual(stored, File.ReadAllText(ToolbarFile), "the toolbar reads the same file, in the same spelling");

        var (exitCode, output) = await Run("get", "run.devtools", "--json");
        Assert.AreEqual(0, exitCode);
        var json = Json(output);
        Assert.AreEqual("run.devtools", json.GetProperty("key").GetString());
        Assert.AreEqual(stored, json.GetProperty("value").GetString());
        Assert.AreEqual("setting", json.GetProperty("source").GetString());

        (exitCode, output) = await Run("unset", "run.devtools");
        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(output, "run.devtools = on (default)");
        Assert.IsFalse(File.Exists(ToolbarFile));
        Assert.AreEqual(0, (await Run("unset", "run.devtools")).ExitCode, "unsetting twice is fine");
    }

    [TestMethod]
    [DataRow("off", "off")]
    [DataRow("HEADLESS\r\n", "headless")]
    public async Task Get_ReadsWhatTheToolbarWrote(string written, string expected)
    {
        File.WriteAllText(ToolbarFile, written);
        StringAssert.Contains((await Run("get", "run.devtools")).Output, $"run.devtools = {expected} (setting)");
        Assert.AreEqual(Enum.Parse<DevToolsMode>(expected, ignoreCase: true), _settings.ReadDevToolsMode(),
            "winapp run reads the same value");
    }

    [TestMethod]
    public async Task Get_IgnoresAFileThatHoldsNoValidValue()
    {
        File.WriteAllText(ToolbarFile, "sometimes");
        StringAssert.Contains((await Run("get", "run.devtools")).Output, "run.devtools = on (default)");
        Assert.IsNull(_settings.ReadDevToolsMode());
    }

    [TestMethod]
    public void TheToolbarAndTheCliNameTheSameFile()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
        var native = Path.Combine(root, "src", "winapp-devtools", "native", "WinApp.DevTools.Native");
        StringAssert.Contains(File.ReadAllText(Path.Combine(native, "DevToolsSettings.h")),
            "return dir + L\"\\\\devtools-\" + name + L\".setting\";");
        StringAssert.Contains(File.ReadAllText(Path.Combine(native, "DevToolsOverlay.Toolbar.inc")),
            "DevToolsSettingsGetText(L\"DefaultMode\")");
        Assert.AreEqual("devtools-DefaultMode.setting", UserSettings.RunDevTools.FileName);
    }

    [TestMethod]
    [DoNotParallelize]
    [DataRow(new[] { "config", "get", "nope" }, "Unknown setting 'nope'. Known settings: run.devtools")]
    [DataRow(new[] { "config", "set", "nope", "on" }, "Unknown setting 'nope'. Known settings: run.devtools")]
    [DataRow(new[] { "config", "set", "run.devtools", "sometimes" }, "run.devtools must be on, off or headless, not 'sometimes'")]
    [DataRow(new[] { "config", "set", "run.devtools" }, "winapp config set run.devtools needs a value: on, off or headless")]
    [DataRow(new[] { "config", "unset" }, "winapp config unset needs a setting: run.devtools")]
    public async Task BadInput_IsOneLineNamingWhatIsAllowed(string[] args, string message)
    {
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(args);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual($"{UiSymbols.Error} {message}", stderr.Trim());
        Assert.AreEqual("", stdout.Trim());

        (stdout, stderr, exitCode) = await InvokeProgramAsync([.. args, "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(message, Json(stdout).GetProperty("error").GetString());
        Assert.AreEqual(1, stdout.Trim().Split('\n').Length, stdout);
    }

    [TestMethod]
    [DoNotParallelize]
    [DataRow("set")]
    [DataRow("get")]
    public async Task Help_IsHelpNotAMissingArgumentError(string verb)
    {
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(["config", verb, "--help"]);
        Assert.AreEqual(0, exitCode, stderr);
        StringAssert.Contains(stdout, "Usage:");
    }
    [TestMethod]
    [DoNotParallelize]
    public async Task RunsOnlyOnThisMachine()
    {
        var (_, stderr, exitCode) = await InvokeProgramAsync(["config", "set", "run.devtools", "off", "--on", "sandbox"]);
        Assert.AreNotEqual(0, exitCode);
        StringAssert.Contains(stderr, "does not accept --on");
    }

    [TestMethod]
    public void Telemetry_RecordsTheKeyAndValueByName()
    {
        string Context(params string[] args) => new WinApp.Cli.Telemetry.Events.CommandInvokedEvent(
            GetRequiredService<WinAppRootCommand>().Parse(args, WinAppParserConfiguration.Default).CommandResult, DateTime.UnixEpoch).Context;

        var set = Context("config", "set", "run.devtools", "HEADLESS");
        StringAssert.Contains(set, "\"key\":\"run.devtools\"");
        StringAssert.Contains(set, "\"value\":\"headless\"");
        Assert.DoesNotContain("[string]", set);
        StringAssert.Contains(Context("config", "get", "run.devtools"), "\"key\":\"run.devtools\"");
        // Free text the user typed never reaches telemetry: an unknown key or value is recorded as an error.
        var bad = Context("config", "set", "my-secret-key", "my-secret-value");
        Assert.DoesNotContain("my-secret", bad);
    }

    [TestMethod]
    public void DevToolsDefault_IsGone()
    {
        var root = GetRequiredService<WinAppRootCommand>();
        Assert.IsFalse(root.Subcommands.Single(c => c.Name == "devtools").Subcommands.Any(c => c.Name == "default"));
        Assert.IsNotEmpty(root.Parse(["devtools", "default", "off"], WinAppParserConfiguration.Default).Errors);
    }
}
