// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.ExecutionTargets.WindowsSandbox;

namespace WinApp.Cli.Tests;

[TestClass]
public class TargetStateDirectoryProviderTests
{
    [TestMethod]
    public void PackagedProcess_UsesPhysicalLocalAppDataPath()
    {
        var localCache = Path.Join(Path.GetTempPath(), "Packages", "winapp", "LocalCache");
        var provider = new TargetStateDirectoryProvider
        {
            PackagedLocalAppDataProvider = () => Path.Join(localCache, "Local"),
            LocalAppDataProvider = () => throw new AssertFailedException("The unpackaged path must not be used."),
        };

        var root = provider.GetTargetRoot(WindowsSandboxTarget.Default, create: false);

        Assert.AreEqual(
            Path.Join(localCache, "Local", "Microsoft", "WinApp", "Targets", WindowsSandboxTarget.Default.StateKey),
            root.FullName);
    }

    [TestMethod]
    public void UnpackagedProcess_UsesOrdinaryLocalAppDataPath()
    {
        var localAppData = Path.Join(Path.GetTempPath(), "Local");
        var provider = new TargetStateDirectoryProvider
        {
            PackagedLocalAppDataProvider = () => null,
            LocalAppDataProvider = () => localAppData,
        };

        var root = provider.GetTargetRoot(WindowsSandboxTarget.Default, create: false);

        Assert.AreEqual(
            Path.Join(localAppData, "Microsoft", "WinApp", "Targets", WindowsSandboxTarget.Default.StateKey),
            root.FullName);
    }

    [TestMethod]
    public void ExplicitOverride_WinsOverPackagedPath()
    {
        var rootOverride = Path.Join(Path.GetTempPath(), "override");
        var provider = new TargetStateDirectoryProvider(rootOverride)
        {
            PackagedLocalAppDataProvider = () => throw new AssertFailedException("The packaged path must not be consulted."),
            LocalAppDataProvider = () => throw new AssertFailedException("The unpackaged path must not be consulted."),
        };

        var root = provider.GetTargetRoot(WindowsSandboxTarget.Default, create: false);

        Assert.AreEqual(Path.Join(rootOverride, WindowsSandboxTarget.Default.StateKey), root.FullName);
    }
}
