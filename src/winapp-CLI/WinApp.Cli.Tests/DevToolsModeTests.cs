// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class DevToolsModeTests
{
    private static DevToolsResolution Resolve(DevToolsMode? requested = null, bool ci = false, bool incompatible = false,
        bool winUI = true, DevToolsMode? setting = null) =>
        DevToolsResolution.Resolve(requested, ci, incompatible, winUI, setting);

    [TestMethod]
    [DataRow(DevToolsMode.On)]
    [DataRow(DevToolsMode.Off)]
    [DataRow(DevToolsMode.Headless)]
    public void Row1_TheFlagWinsOverEverythingElse(DevToolsMode requested)
    {
        Assert.AreEqual(new DevToolsResolution(requested, DevToolsModeSource.Explicit),
            Resolve(requested, ci: true, incompatible: true, winUI: false, setting: DevToolsMode.Off));
    }

    [TestMethod]
    public void Row2_CiTurnsItOff()
    {
        Assert.AreEqual(new DevToolsResolution(DevToolsMode.Off, DevToolsModeSource.Ci),
            Resolve(ci: true, setting: DevToolsMode.On));
    }

    [TestMethod]
    public void Row3_AnOptionDevToolsCannotWorkWithTurnsItOff()
    {
        Assert.AreEqual(new DevToolsResolution(DevToolsMode.Off, DevToolsModeSource.IncompatibleOption),
            Resolve(incompatible: true, setting: DevToolsMode.On));
    }

    [TestMethod]
    public void Row4_ANonWinUIOrNonProjectRunIsOff()
    {
        Assert.AreEqual(new DevToolsResolution(DevToolsMode.Off, DevToolsModeSource.NotWinUI),
            Resolve(winUI: false, setting: DevToolsMode.On));
    }

    [TestMethod]
    [DataRow(DevToolsMode.On)]
    [DataRow(DevToolsMode.Off)]
    [DataRow(DevToolsMode.Headless)]
    public void Row5_TheUsersSettingDecidesAWinUIProjectRun(DevToolsMode setting)
    {
        Assert.AreEqual(new DevToolsResolution(setting, DevToolsModeSource.Setting), Resolve(setting: setting));
    }

    [TestMethod]
    public void Row6_WithNothingElseItIsOn()
    {
        var resolution = Resolve();
        Assert.AreEqual(new DevToolsResolution(DevToolsMode.On, DevToolsModeSource.Default), resolution);
        Assert.IsTrue(resolution.Enabled && resolution.ShowToolbar && resolution.FailOpen);
    }

    [TestMethod]
    public void OnlyAModeFromTheSettingOrTheDefaultFailsOpen()
    {
        Assert.IsTrue(Resolve(setting: DevToolsMode.Headless).FailOpen);
        Assert.IsFalse(Resolve(DevToolsMode.On).FailOpen);
        Assert.IsFalse(Resolve(DevToolsMode.Headless).ShowToolbar);
    }

    [TestMethod]
    [DataRow("true", true)]
    [DataRow("1", true)]
    [DataRow("github", true)]
    [DataRow("", false)]
    [DataRow("  ", false)]
    [DataRow(null, false)]
    [DataRow("0", false)]
    [DataRow("false", false)]
    [DataRow("False", false)]
    public void CiIsAnyValueButEmptyFalseOrZero(string? value, bool ci)
    {
        Assert.AreEqual(ci, DevToolsResolution.IsCi(value));
    }

    [TestMethod]
    public void TheSettingRoundTripsAsAWordAndIgnoresAnythingElse()
    {
        var settings = new UserSettings { StateDirectory = Path.Combine(Path.GetTempPath(), "winapp-devtools-mode-" + Guid.NewGuid().ToString("N")) };
        var file = settings.FilePath(UserSettings.RunDevTools);
        try
        {
            Assert.IsNull(settings.ReadDevToolsMode());
            foreach (var mode in Enum.GetValues<DevToolsMode>())
            {
                settings.Set(UserSettings.RunDevTools, new(mode.ToString().ToLowerInvariant()));
                Assert.AreEqual(mode.ToString().ToLowerInvariant(), File.ReadAllText(file));
                Assert.AreEqual(mode, settings.ReadDevToolsMode());
            }
            foreach (var junk in new[] { "1", "maybe", "" })
            {
                File.WriteAllText(file, junk);
                Assert.IsNull(settings.ReadDevToolsMode(), junk);
            }
        }
        finally
        {
            Directory.Delete(settings.StateDirectory, recursive: true);
        }
    }
}
