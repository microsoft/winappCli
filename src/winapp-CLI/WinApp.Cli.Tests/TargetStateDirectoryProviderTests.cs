// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.ExecutionTargets.WindowsSandbox;

namespace WinApp.Cli.Tests;

[TestClass]
[DoNotParallelize] // The environment overrides are process-wide.
public class TargetStateDirectoryProviderTests
{
    [TestMethod]
    public void DefaultDirectory_UsesUserProfileRatherThanPackageStorage()
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
        var provider = new TargetStateDirectoryProvider { UserProfileProvider = () => profile };
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
    public void DefaultDirectory_IsIndependentOfCacheOverride()
    {
        var previous = Environment.GetEnvironmentVariable("WINAPP_CLI_CACHE_DIRECTORY");
        var profile = Path.Join(Path.GetTempPath(), $"winapp-profile-{Guid.NewGuid():N}");
        try
        {
            Environment.SetEnvironmentVariable("WINAPP_CLI_CACHE_DIRECTORY", Path.Join(profile, "other-cache"));
            var provider = new TargetStateDirectoryProvider { UserProfileProvider = () => profile };

            var root = provider.GetTargetRoot(WindowsSandboxTarget.Default, create: false);

            Assert.AreEqual(
                Path.Join(profile, ".winapp", "state", "targets", WindowsSandboxTarget.Default.StateKey),
                root.FullName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WINAPP_CLI_CACHE_DIRECTORY", previous);
        }
    }

    [TestMethod]
    public void ExplicitOverride_WinsOverProfile()
    {
        var rootOverride = Path.Join(Path.GetTempPath(), "override");
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
        var previous = Environment.GetEnvironmentVariable(TargetStateDirectoryProvider.RootOverrideVariable);
        var rootOverride = Path.Join(Path.GetTempPath(), $"winapp-target-override-{Guid.NewGuid():N}");
        try
        {
            Environment.SetEnvironmentVariable(TargetStateDirectoryProvider.RootOverrideVariable, rootOverride);
            var provider = new TargetStateDirectoryProvider
            {
                UserProfileProvider = () => throw new AssertFailedException("The profile must not be consulted."),
            };

            Assert.AreEqual(
                Path.Join(rootOverride, WindowsSandboxTarget.Default.StateKey),
                provider.GetTargetRoot(WindowsSandboxTarget.Default, create: false).FullName);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TargetStateDirectoryProvider.RootOverrideVariable, previous);
        }
    }

    [TestMethod]
    public void InvalidUserProfile_FailsExplicitly()
    {
        var provider = new TargetStateDirectoryProvider { UserProfileProvider = () => "relative\\profile" };

        var ex = Assert.ThrowsExactly<ExecutionTargetException>(
            () => provider.GetTargetRoot(WindowsSandboxTarget.Default, create: false));

        Assert.AreEqual(ExecutionTargetErrorCodes.StateUnavailable, ex.Error.Code);
        StringAssert.Contains(ex.Error.UserAction, "%USERPROFILE%\\.winapp\\state");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void UnusableStateDirectory_ReportsStorageError(bool useOverride)
    {
        var profile = Path.Join(Path.GetTempPath(), $"winapp-profile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(profile);
        var occupied = Path.Join(profile, useOverride ? "targets" : ".winapp");
        File.WriteAllText(occupied, "not a directory");

        try
        {
            var provider = new TargetStateDirectoryProvider(useOverride ? occupied : null)
            {
                UserProfileProvider = () => profile,
            };

            Assert.IsFalse(provider.GetTargetRoot(WindowsSandboxTarget.Default, create: false).Exists);

            var ex = Assert.ThrowsExactly<ExecutionTargetException>(
                () => provider.GetTargetRoot(WindowsSandboxTarget.Default));

            Assert.AreEqual(ExecutionTargetErrorCodes.StateUnavailable, ex.Error.Code);
            Assert.IsInstanceOfType<IOException>(ex.InnerException);
            StringAssert.Contains(ex.Error.UserAction, "writable");
        }
        finally
        {
            File.Delete(occupied);
            Directory.Delete(profile);
        }
    }
}
