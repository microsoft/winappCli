// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class WinUIOutputDetectorTests
{
    private DirectoryInfo _folder = null!;

    [TestInitialize]
    public void Setup() => _folder = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"WinUIOutputDetectorTests_{Guid.NewGuid():N}"));

    [TestCleanup]
    public void Cleanup() => _folder.Delete(recursive: true);

    [TestMethod]
    public void EmptyFolder_IsNotWinUI3()
    {
        Assert.IsFalse(WinUIOutputDetector.MayContainWinUI3(_folder));
    }

    [TestMethod]
    public void NonWinUIOutput_IsNotWinUI3()
    {
        foreach (var name in new[] { "App.exe", "App.dll", "PresentationFramework.dll", "chrome_elf.dll", "App.runtimeconfig.json" })
        {
            File.WriteAllText(Path.Combine(_folder.FullName, name), "");
        }

        Assert.IsFalse(WinUIOutputDetector.MayContainWinUI3(_folder));
    }

    [TestMethod]
    [DataRow("Microsoft.WinUI.dll")]
    [DataRow("Microsoft.UI.Xaml.dll")]
    [DataRow("Microsoft.WindowsAppRuntime.Bootstrap.dll")]
    [DataRow("App.xbf")]
    [DataRow("resources.pri")]
    [DataRow("App.build.appxrecipe")]
    [DataRow("nested/Microsoft.WinUI.dll")]
    [DataRow("nested/Microsoft.UI.Xaml.dll")]
    [DataRow("nested/Views/Page.XBF")]
    public void PossibleWinUIOutput_IsNotRefused(string marker)
    {
        var path = Path.Combine(_folder.FullName, marker);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");

        Assert.IsTrue(WinUIOutputDetector.MayContainWinUI3(_folder));
    }

    [TestMethod]
    public void NonWinUIOutputWithStagedResourceIndex_IsNotWinUI3()
    {
        var staging = _folder.CreateSubdirectory("AppX");
        File.WriteAllText(Path.Combine(staging.FullName, "resources.pri"), "");

        Assert.IsFalse(WinUIOutputDetector.MayContainWinUI3(_folder));
    }

    [TestMethod]
    public void UnavailableFolder_IsInconclusive()
    {
        Assert.IsTrue(WinUIOutputDetector.MayContainWinUI3(new DirectoryInfo(
            Path.Combine(_folder.FullName, "missing"))));
    }
}
