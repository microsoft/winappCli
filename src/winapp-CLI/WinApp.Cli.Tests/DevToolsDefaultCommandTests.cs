// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinApp.Cli.Commands;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class DevToolsDefaultCommandTests() : BaseCommandTests(logLevel: Microsoft.Extensions.Logging.LogLevel.Warning)
{
    private string _state = null!;

    [TestInitialize]
    public void UseTemporaryState()
    {
        _state = _tempDirectory.CreateSubdirectory("state").FullName;
        var handler = GetRequiredService<DevToolsDefaultCommand.Handler>();
        handler.StateDirectory = _state;
        handler.ReadCiVariable = () => null;
    }

    private Task<int> Run(params string[] args) =>
        ParseAndInvokeWithCaptureAsync(GetRequiredService<WinAppRootCommand>(), ["devtools", "default", .. args]);

    [TestMethod]
    public async Task WithoutAValue_ShowsTheBuiltInDefault()
    {
        Assert.AreEqual(0, await Run());
        StringAssert.Contains(TestAnsiConsole.Output, "DevTools default: on (built in)");
        Assert.IsFalse(File.Exists(DevToolsDefaultSetting.FilePath(_state)));
    }

    [TestMethod]
    [DataRow("off", DevToolsMode.Off)]
    [DataRow("headless", DevToolsMode.Headless)]
    [DataRow("On", DevToolsMode.On)]
    public async Task AValue_IsSavedAndThenReportedAsTheUsersSetting(string value, DevToolsMode mode)
    {
        Assert.AreEqual(0, await Run(value));
        Assert.AreEqual(mode, DevToolsDefaultSetting.Read(_state));
        Assert.AreEqual(0, await Run("--json"));
        var json = TestAnsiConsole.Output[TestAnsiConsole.Output.IndexOf('{')..];
        using var document = JsonDocument.Parse(json);
        Assert.AreEqual(mode.ToString().ToLowerInvariant(), document.RootElement.GetProperty("mode").GetString());
        Assert.AreEqual("setting", document.RootElement.GetProperty("source").GetString());
    }

    [TestMethod]
    public async Task AnUnknownValue_IsRejectedAndNothingIsSaved()
    {
        Assert.AreNotEqual(0, await Run("sometimes"));
        Assert.IsFalse(File.Exists(DevToolsDefaultSetting.FilePath(_state)));
    }

    [TestMethod]
    public async Task InCi_SaysRunsStayOff()
    {
        GetRequiredService<DevToolsDefaultCommand.Handler>().ReadCiVariable = () => "true";
        Assert.AreEqual(0, await Run());
        StringAssert.Contains(TestAnsiConsole.Output, "CI is set");
    }

    [TestMethod]
    public async Task OnSandbox_IsRefused()
    {
        Assert.AreEqual(1, await Run("off", "--on", "sandbox", "--json"));
        Assert.IsFalse(File.Exists(DevToolsDefaultSetting.FilePath(_state)));
    }
}
