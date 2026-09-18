// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.ExecutionTargets.WindowsSandbox;

namespace WinApp.Cli.Tests;

[TestClass]
[DoNotParallelize] // The root and cache environment overrides are process-wide.
public class TargetStateDirectoryProviderTests
{
    private string? _previousRoot;

    [TestInitialize]
    public void Setup()
    {
        _previousRoot = Environment.GetEnvironmentVariable(TargetStateDirectoryProvider.RootOverrideVariable);
        Environment.SetEnvironmentVariable(TargetStateDirectoryProvider.RootOverrideVariable, null);
    }

    [TestCleanup]
    public void Cleanup() =>
        Environment.SetEnvironmentVariable(TargetStateDirectoryProvider.RootOverrideVariable, _previousRoot);

    [TestMethod]
    public void DefaultDirectory_IsSharedUserStateRegardlessOfCacheOverride()
    {
        var previousCache = Environment.GetEnvironmentVariable("WINAPP_CLI_CACHE_DIRECTORY");
        try
        {
            var expected = Path.Join(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".winapp", "state", "targets", WindowsSandboxTarget.Default.StateKey);
            foreach (var cache in new[] { Path.Join(Path.GetTempPath(), "cache-one"), Path.Join(Path.GetTempPath(), "cache-two") })
            {
                Environment.SetEnvironmentVariable("WINAPP_CLI_CACHE_DIRECTORY", cache);
                var provider = new TargetStateDirectoryProvider();

                Assert.AreEqual(expected, provider.GetTargetRoot(WindowsSandboxTarget.Default, create: false).FullName);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("WINAPP_CLI_CACHE_DIRECTORY", previousCache);
        }
    }

    [TestMethod]
    public void DefaultDirectory_UsesProfileRootWithoutCreatingIt()
    {
        var profile = Path.Join(Path.GetTempPath(), $"winapp-profile-{Guid.NewGuid():N}");
        var provider = new TargetStateDirectoryProvider
        {
            UserProfileProvider = () => profile,
        };

        var root = provider.GetTargetRoot(WindowsSandboxTarget.Default, create: false);

        Assert.AreEqual(
            Path.Join(profile, ".winapp", "state", "targets", WindowsSandboxTarget.Default.StateKey),
            root.FullName);
        Assert.IsFalse(Directory.Exists(profile));
    }

    [TestMethod]
    public void DefaultDirectory_CreatesTargetUnderUserState()
    {
        var profile = Path.Join(Path.GetTempPath(), $"winapp-profile-{Guid.NewGuid():N}");
        var provider = new TargetStateDirectoryProvider
        {
            UserProfileProvider = () => profile,
        };

        try
        {
            var root = provider.GetTargetRoot(WindowsSandboxTarget.Default);

            Assert.AreEqual(
                Path.Join(profile, ".winapp", "state", "targets", WindowsSandboxTarget.Default.StateKey),
                root.FullName);
            Assert.IsTrue(root.Exists);
        }
        finally
        {
            if (Directory.Exists(profile))
            {
                Directory.Delete(profile, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ExplicitOverride_WinsOverEnvironmentAndProfile()
    {
        var rootOverride = Path.Join(Path.GetTempPath(), "override");
        Environment.SetEnvironmentVariable(TargetStateDirectoryProvider.RootOverrideVariable, @"\\server\unused");
        var provider = new TargetStateDirectoryProvider(rootOverride)
        {
            UserProfileProvider = () => throw new AssertFailedException("The profile must not be consulted."),
        };

        var root = provider.GetTargetRoot(WindowsSandboxTarget.Default, create: false);

        Assert.AreEqual(Path.Join(rootOverride, WindowsSandboxTarget.Default.StateKey), root.FullName);
    }

    [TestMethod]
    public void EnvironmentOverride_WinsOverProfile()
    {
        var rootOverride = Path.Join(Path.GetTempPath(), "override");
        Environment.SetEnvironmentVariable(TargetStateDirectoryProvider.RootOverrideVariable, rootOverride);
        var provider = new TargetStateDirectoryProvider
        {
            UserProfileProvider = () => throw new AssertFailedException("The profile must not be consulted."),
        };

        var root = provider.GetTargetRoot(WindowsSandboxTarget.Default, create: false);

        Assert.AreEqual(Path.Join(rootOverride, WindowsSandboxTarget.Default.StateKey), root.FullName);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("relative\\profile")]
    [DataRow(@"\\server\share\profile")]
    public void InvalidProfile_FailsWithoutFallingBack(string profile)
    {
        var provider = new TargetStateDirectoryProvider { UserProfileProvider = () => profile };

        var ex = Assert.ThrowsExactly<ExecutionTargetException>(
            () => provider.GetTargetRoot(WindowsSandboxTarget.Default, create: false));

        Assert.AreEqual(ExecutionTargetErrorCodes.TargetStale, ex.Error.Code);
        StringAssert.Contains(ex.Error.UserAction, "%USERPROFILE%\\.winapp\\state");
    }

    [TestMethod]
    [DataRow("relative\\targets")]
    [DataRow(@"\\server\share\targets")]
    [DataRow(@"\\?\UNC\server\share\targets")]
    public void InvalidOverride_FailsWithoutFallingBack(string rootOverride)
    {
        var provider = new TargetStateDirectoryProvider(rootOverride)
        {
            UserProfileProvider = () => throw new AssertFailedException("An invalid override must not fall back."),
        };

        var ex = Assert.ThrowsExactly<ExecutionTargetException>(
            () => provider.GetTargetRoot(WindowsSandboxTarget.Default, create: false));

        Assert.AreEqual(ExecutionTargetErrorCodes.TargetStale, ex.Error.Code);
    }
}
