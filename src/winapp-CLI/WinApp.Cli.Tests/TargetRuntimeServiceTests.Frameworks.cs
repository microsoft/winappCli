// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Tests;

/// <summary>
/// Provisioning the shared .NET runtimes a framework-dependent app needs, into a per-user root
/// inside the guest (spec §"Runtime provisioning").
/// </summary>
/// <remarks>
/// Separated from the package half because the mechanism is different in every respect that matters:
/// the payload is a portable layout rather than an MSIX, it is unpacked rather than registered, and
/// the result has to reach the launched process through its environment rather than through the
/// Windows package graph.
/// </remarks>
public partial class TargetRuntimeServiceTests
{
    [TestMethod]
    public async Task Ensure_InstallsTheSharedFrameworkAndPinsTheLaunchToTheManagedRoot()
    {
        await WriteRuntimeConfigAsync("Microsoft.WindowsDesktop.App", "10.0.0");

        var core = await WriteLayoutAsync("Microsoft.NETCore.App", "10.0.2");
        var desktop = await WriteLayoutAsync("Microsoft.WindowsDesktop.App", "10.0.2");

        await using var harness = new Harness(
            _guestManaged, _stateRoot, sharedFrameworkRoot: TestPaths.Under(_root, "no-dotnet"));

        harness.Frameworks.Layouts["Microsoft.NETCore.App"] = core;
        harness.Frameworks.Layouts["Microsoft.WindowsDesktop.App"] = desktop;

        var result = await harness.EnsureAsync(_hostSource, TestContext.CancellationToken);

        // A desktop app's runtime configuration names only the desktop framework, but it cannot load
        // without the core runtime underneath it, so both are provisioned.
        Assert.IsTrue(result.Report!.Satisfied);
        Assert.AreEqual(2, result.Report.Items.Count);
        Assert.IsTrue(result.Report.Items.TrueForAll(item => item.Installed));

        var managedRoot = TestPaths.Under(_guestManaged, TargetRuntimeService.DotNetRootFolderName);
        Assert.IsTrue(Directory.Exists(Path.Join(managedRoot, "shared", "Microsoft.NETCore.App", "10.0.2")));
        Assert.IsTrue(Directory.Exists(Path.Join(managedRoot, "host", "fxr", "10.0.2")));

        // A per-user root is only discoverable to an apphost through DOTNET_ROOT, so the launch has
        // to carry it — and the guest also records it per-user, for a process winapp did not start.
        Assert.AreEqual(managedRoot, result.LaunchEnvironment["DOTNET_ROOT"]);
        Assert.Contains(managedRoot, harness.ConfiguredDiscoveryRoots);
    }

    [TestMethod]
    public async Task Ensure_WhenTheGuestAlreadyHasTheFramework_InstallsNothingAndLeavesTheLaunchAlone()
    {
        await WriteRuntimeConfigAsync("Microsoft.NETCore.App", "10.0.0");

        var installed = TestPaths.Under(_root, "guest-dotnet");
        WriteInstalledFramework(installed, "Microsoft.NETCore.App", "10.0.5");

        await using var harness = new Harness(_guestManaged, _stateRoot, sharedFrameworkRoot: installed);
        harness.Frameworks.Layouts["Microsoft.NETCore.App"] = await WriteLayoutAsync("Microsoft.NETCore.App", "10.0.2");

        var result = await harness.EnsureAsync(_hostSource, TestContext.CancellationToken);

        Assert.IsTrue(result.Report!.Satisfied);
        Assert.AreEqual("10.0.5", result.Report.Items.Single().PresentVersion);
        Assert.IsFalse(result.Report.Items.Single().Installed);

        // Nothing was installed into the managed root, so pinning DOTNET_ROOT to it would break a
        // launch that works perfectly well against the guest's own installation.
        Assert.IsEmpty(result.LaunchEnvironment);
        Assert.IsEmpty(harness.ConfiguredDiscoveryRoots);
    }

    [TestMethod]
    public async Task Ensure_WhenOnlyADifferentMajorIsInstalled_DoesNotRollForwardOntoIt()
    {
        await WriteRuntimeConfigAsync("Microsoft.NETCore.App", "8.0.0");

        var installed = TestPaths.Under(_root, "guest-dotnet");
        WriteInstalledFramework(installed, "Microsoft.NETCore.App", "10.0.5");

        await using var harness = new Harness(_guestManaged, _stateRoot, sharedFrameworkRoot: installed);

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => harness.EnsureAsync(_hostSource, TestContext.CancellationToken));

        // .NET rolls forward across patches and minors, never across majors. Accepting 10.0.5 for an
        // app built against 8.0 would report a graph the apphost then refuses to resolve.
        Assert.AreEqual(ExecutionTargetErrorCodes.RuntimeProvisionFailed, failure.Error.Code);
        StringAssert.Contains(failure.Error.Message, "Microsoft.NETCore.App");
    }

    [TestMethod]
    public async Task Ensure_WhenTheGuestCanOnlyServePartOfTheGraph_ProvisionsAllOfItIntoTheManagedRoot()
    {
        await WriteRuntimeConfigAsync("Microsoft.WindowsDesktop.App", "10.0.0");

        // The guest has the core runtime but not the desktop one — the ordinary Windows Sandbox
        // situation once anything has installed .NET into it.
        var guestInstall = TestPaths.Under(_root, "guest-dotnet");
        WriteInstalledFramework(guestInstall, "Microsoft.NETCore.App", "10.0.9");

        await using var harness = new Harness(_guestManaged, _stateRoot, sharedFrameworkRoot: guestInstall);

        harness.Frameworks.Layouts["Microsoft.NETCore.App"] = await WriteLayoutAsync("Microsoft.NETCore.App", "10.0.2");
        harness.Frameworks.Layouts["Microsoft.WindowsDesktop.App"] =
            await WriteLayoutAsync("Microsoft.WindowsDesktop.App", "10.0.2");

        var result = await harness.EnsureAsync(_hostSource, TestContext.CancellationToken);

        // DOTNET_ROOT is exclusive: an apphost pointed at a root resolves everything from there and
        // consults nothing else. Installing only the missing framework and pinning the launch would
        // hide the core runtime the guest did have.
        Assert.IsTrue(result.Report!.Satisfied);
        Assert.IsTrue(result.Report.Items.TrueForAll(item => item.Installed));

        var managedRoot = TestPaths.Under(_guestManaged, TargetRuntimeService.DotNetRootFolderName);
        Assert.IsTrue(Directory.Exists(Path.Join(managedRoot, "shared", "Microsoft.NETCore.App", "10.0.2")));
        Assert.IsTrue(Directory.Exists(Path.Join(managedRoot, "shared", "Microsoft.WindowsDesktop.App", "10.0.2")));
        Assert.AreEqual(managedRoot, result.LaunchEnvironment["DOTNET_ROOT"]);
    }

    [TestMethod]
    public async Task Ensure_WhenTheManagedRootIsNeededAndOneLayoutIsMissing_FailsRatherThanPinningAPartialRoot()
    {
        await WriteRuntimeConfigAsync("Microsoft.WindowsDesktop.App", "10.0.0");

        var guestInstall = TestPaths.Under(_root, "guest-dotnet");
        WriteInstalledFramework(guestInstall, "Microsoft.NETCore.App", "10.0.9");

        await using var harness = new Harness(_guestManaged, _stateRoot, sharedFrameworkRoot: guestInstall);

        // Only the desktop layout resolved on the host.
        harness.Frameworks.Layouts["Microsoft.WindowsDesktop.App"] =
            await WriteLayoutAsync("Microsoft.WindowsDesktop.App", "10.0.2");

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => harness.EnsureAsync(_hostSource, TestContext.CancellationToken));

        // Pinning the launch to a root with only half the graph in it would produce a startup
        // failure naming a framework this pass reported as present.
        Assert.AreEqual(ExecutionTargetErrorCodes.RuntimeProvisionFailed, failure.Error.Code);
        StringAssert.Contains(failure.Error.Message, "Microsoft.NETCore.App");

        // And the guest must not have recorded a per-user root that cannot resolve the graph: doing
        // so would break apps started by hand that the machine-wide installation could serve.
        Assert.IsEmpty(harness.ConfiguredDiscoveryRoots);
    }

    [TestMethod]
    public async Task Ensure_WithAMissingSharedFrameworkAndNoLayout_FailsNamingIt()
    {
        await WriteRuntimeConfigAsync("Microsoft.WindowsDesktop.App", "10.0.0");

        await using var harness = new Harness(
            _guestManaged, _stateRoot, sharedFrameworkRoot: TestPaths.Under(_root, "no-dotnet"));

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => harness.EnsureAsync(_hostSource, TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.RuntimeProvisionFailed, failure.Error.Code);
        StringAssert.Contains(failure.Error.Message, "Microsoft.WindowsDesktop.App");
    }
}
