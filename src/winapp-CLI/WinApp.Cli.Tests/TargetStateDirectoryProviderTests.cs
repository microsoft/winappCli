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
    private string _testRoot = null!;

    [TestInitialize]
    public void Setup()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"target-state-provider-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);
        _previousRoot = Environment.GetEnvironmentVariable(TargetStateDirectoryProvider.RootOverrideVariable);
        Environment.SetEnvironmentVariable(TargetStateDirectoryProvider.RootOverrideVariable, null);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Environment.SetEnvironmentVariable(TargetStateDirectoryProvider.RootOverrideVariable, _previousRoot);
        Directory.Delete(_testRoot, recursive: true);
    }

    [TestMethod]
    public void DefaultDirectory_IsSharedUserStateRegardlessOfCacheOverride()
    {
        var previousCache = Environment.GetEnvironmentVariable("WINAPP_CLI_CACHE_DIRECTORY");
        try
        {
            var profile = Path.Join(_testRoot, "profile");
            var expected = Path.Join(
                profile, ".winapp", "state", "targets", WindowsSandboxTarget.Default.StateKey);
            foreach (var cache in new[] { Path.Join(_testRoot, "cache-one"), Path.Join(_testRoot, "cache-two") })
            {
                Environment.SetEnvironmentVariable("WINAPP_CLI_CACHE_DIRECTORY", cache);
                var provider = new TargetStateDirectoryProvider { UserProfileProvider = () => profile };

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
        var profile = Path.Join(_testRoot, "profile");
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
        var profile = Path.Join(_testRoot, "profile");
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
        var rootOverride = Path.Join(_testRoot, "override");
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
        var rootOverride = Path.Join(_testRoot, "override");
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

        Assert.AreEqual(ExecutionTargetErrorCodes.StateUnavailable, ex.Error.Code);
        StringAssert.Contains(ex.Error.UserAction, "%USERPROFILE%\\.winapp\\state");
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
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

        Assert.AreEqual(ExecutionTargetErrorCodes.StateUnavailable, ex.Error.Code);
    }

    [TestMethod]
    public void InvalidEnvironmentOverride_FailsWithoutFallingBack()
    {
        Environment.SetEnvironmentVariable(TargetStateDirectoryProvider.RootOverrideVariable, " ");
        var provider = new TargetStateDirectoryProvider
        {
            UserProfileProvider = () => throw new AssertFailedException("An invalid override must not fall back."),
        };

        var ex = Assert.ThrowsExactly<ExecutionTargetException>(
            () => provider.GetTargetRoot(WindowsSandboxTarget.Default, create: false));

        Assert.AreEqual(ExecutionTargetErrorCodes.StateUnavailable, ex.Error.Code);
    }

    [TestMethod]
    public void FileBlockingDirectory_ReportsStateUnavailable()
    {
        var blocked = Path.Join(_testRoot, "targets");
        File.WriteAllText(blocked, "keep");

        var ex = Assert.ThrowsExactly<ExecutionTargetException>(
            () => new TargetStateDirectoryProvider(blocked).GetTargetRoot(WindowsSandboxTarget.Default));

        Assert.AreEqual(ExecutionTargetErrorCodes.StateUnavailable, ex.Error.Code);
        Assert.AreEqual("keep", File.ReadAllText(blocked));
    }
}
