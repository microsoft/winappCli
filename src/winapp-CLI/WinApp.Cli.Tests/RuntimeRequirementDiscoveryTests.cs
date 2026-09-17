// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.Tests;

/// <summary>
/// Deriving runtime requirements from what the build actually produced
/// (spec §"Runtime provisioning": "Runtime requirements are derived from the resolved project and
/// build artifacts").
/// </summary>
/// <remarks>
/// These read the same two artifacts Windows itself reads — the package manifest's dependencies and
/// the apphost's runtime configuration — so a requirement discovered here cannot disagree with the
/// one that would actually fail. Everything below runs on ordinary files with no Sandbox involved.
/// </remarks>
[TestClass]
public class RuntimeRequirementDiscoveryTests
{
    private static readonly string[] ExpectedFrameworks =
        ["Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App"];
    private static readonly Version[] AvailableFrameworkVersions =
        [new(8, 0, 0), new(8, 0, 12), new(8, 1, 4), new(9, 0, 3)];

    private string _root = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = TestPaths.TempRoot(nameof(RuntimeRequirementDiscoveryTests));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    [TestMethod]
    public async Task Discover_ReadsEveryDeclaredFrameworkDependency()
    {
        await WriteManifestAsync(
            "x64",
            ("Microsoft.WindowsAppRuntime.1.8", "8000.675.1142.0"),
            ("Microsoft.VCLibs.140.00.UWPDesktop", "14.0.33728.0"));

        var requirements = RuntimeRequirementDiscovery.Discover(new DirectoryInfo(_root), "arm64");

        // The VC runtime is declared exactly like the Windows App Runtime and is just as required.
        // Dropping it here because no payload exists for it would hide a real dependency.
        Assert.AreEqual(2, requirements.Packages.Count);
        Assert.AreEqual("Microsoft.WindowsAppRuntime.1.8", requirements.Packages[0].Name);
        Assert.AreEqual("8000.675.1142.0", requirements.Packages[0].MinVersion);
        Assert.AreEqual("Microsoft.VCLibs.140.00.UWPDesktop", requirements.Packages[1].Name);
    }

    [TestMethod]
    public async Task Discover_UsesCanonicalPackageManifestPrecedence()
    {
        await WriteManifestAsync(
            "arm64",
            ("Microsoft.WindowsAppRuntime.1.8", "8000.675.1142.0"));
        File.Move(
            TestPaths.Under(_root, "appxmanifest.xml"),
            TestPaths.Under(_root, "Package.appxmanifest"));

        var requirements = RuntimeRequirementDiscovery.Discover(new DirectoryInfo(_root), "x64");

        Assert.AreEqual("arm64", requirements.Architecture);
        Assert.AreEqual("Microsoft.WindowsAppRuntime.1.8", requirements.Packages.Single().Name);
    }

    [TestMethod]
    public async Task Discover_PrefersTheManifestArchitectureOverTheGuestsOwn()
    {
        await WriteManifestAsync("x64", ("Microsoft.WindowsAppRuntime.1.8", "8000.675.1142.0"));

        var requirements = RuntimeRequirementDiscovery.Discover(new DirectoryInfo(_root), "arm64");

        // The app was built for x64, so an arm64 runtime would not satisfy it however capable the
        // guest is of running one.
        Assert.AreEqual("x64", requirements.Architecture);
        Assert.AreEqual("x64", requirements.Packages[0].Architecture);
    }

    [TestMethod]
    public async Task Discover_WithNoManifestArchitecture_FallsBackToTheGuests()
    {
        await WriteManifestAsync("neutral", ("Microsoft.WindowsAppRuntime.1.8", "8000.675.1142.0"));

        var requirements = RuntimeRequirementDiscovery.Discover(new DirectoryInfo(_root), "arm64");

        Assert.AreEqual("arm64", requirements.Architecture);
    }

    [TestMethod]
    public async Task Discover_NeutralManifestUsesItsExecutableArchitecture()
    {
        await File.WriteAllTextAsync(Path.Join(_root, "appxmanifest.xml"), """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="Contoso.App" Publisher="CN=Contoso" Version="1.0.0.0" ProcessorArchitecture="neutral" />
              <Applications><Application Id="App" Executable="app.exe" EntryPoint="Windows.FullTrustApplication" /></Applications>
            </Package>
            """, TestContext.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Join(_root, "app.exe"), MinimalPe.ForArchitecture("x86"), TestContext.CancellationToken);
        await WriteRuntimeConfigAsync("App", Framework("Microsoft.NETCore.App", "8.0.0"));

        var requirements = RuntimeRequirementDiscovery.Discover(new DirectoryInfo(_root), null);

        Assert.AreEqual("x86", requirements.Architecture);
        Assert.AreEqual("x86", requirements.Frameworks.Single().Architecture);
    }

    [TestMethod]
    public async Task Discover_UnknownArchitectureFailsOnlyWhenSharedRuntimesAreRequired()
    {
        var native = RuntimeRequirementDiscovery.Discover(new DirectoryInfo(_root), null);
        Assert.IsTrue(native.IsEmpty);

        await WriteRuntimeConfigAsync("App", """
            {"runtimeOptions":{"includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"8.0.12"}]}}
            """);
        Assert.IsTrue(RuntimeRequirementDiscovery.Discover(new DirectoryInfo(_root), null).IsEmpty);

        await WriteRuntimeConfigAsync("App", Framework("Microsoft.NETCore.App", "8.0.0"));
        var error = Assert.ThrowsExactly<ExecutionTargetException>(() =>
            RuntimeRequirementDiscovery.Discover(new DirectoryInfo(_root), null));
        StringAssert.Contains(error.Message, "architecture could not be determined");
    }

    [TestMethod]
    public async Task Discover_ReadsSharedFrameworksFromTheRuntimeConfiguration()
    {
        await WriteRuntimeConfigAsync(
            "App",
            """
            {
              "runtimeOptions": {
                "tfm": "net10.0-windows10.0.19041.0",
                "frameworks": [
                  { "name": "Microsoft.NETCore.App", "version": "10.0.0" },
                  { "name": "Microsoft.WindowsDesktop.App", "version": "10.0.0" }
                ]
              }
            }
            """);

        var requirements = RuntimeRequirementDiscovery.Discover(new DirectoryInfo(_root), "x64");

        CollectionAssert.AreEqual(
            ExpectedFrameworks,
            requirements.Frameworks.Select(framework => framework.Name).ToArray());
    }

    [TestMethod]
    public async Task Discover_UnpackagedBuildReadsExactRuntimePackageRatherThanUmbrellaFromDepsJson()
    {
        await File.WriteAllTextAsync(
            TestPaths.Under(_root, "App.deps.json"),
            """
            {
              "libraries": {
                "Microsoft.WindowsAppSDK/1.8.260317003": { "type": "package" },
                "Microsoft.WindowsAppSDK.Runtime/1.8.260209005": { "type": "package" },
                "Contoso.App/1.0.0": { "type": "project" }
              }
            }
            """,
            TestContext.CancellationToken);

        var requirements = RuntimeRequirementDiscovery.Discover(new DirectoryInfo(_root), "arm64");

        Assert.AreEqual("1.8.260209005", requirements.WindowsAppRuntimeVersion);
        Assert.IsFalse(requirements.IsEmpty);
    }

    [TestMethod]
    public async Task Discover_ComponentOnlyWindowsAppSdkUsesItsRestoredRuntimeVersion()
    {
        await File.WriteAllTextAsync(Path.Join(_root, "App.deps.json"),
            """
            {"libraries":{
                "Microsoft.WindowsAppSDK.WinUI/1.8.251106002":{"type":"package"},
                "Microsoft.WindowsAppSDK.Runtime/1.8.260209005":{"type":"package"}
            }}
            """, TestContext.CancellationToken);

        var requirements = RuntimeRequirementDiscovery.Discover(new DirectoryInfo(_root), "x86");

        Assert.IsFalse(requirements.IsEmpty);
        Assert.AreEqual("x86", requirements.Architecture);
        Assert.AreEqual("1.8.260209005", requirements.WindowsAppRuntimeVersion);
    }

    [TestMethod]
    [DataRow("Disable", "8.0.0")]
    [DataRow("LatestPatch", "8.0.12")]
    [DataRow("Minor", "8.0.12")]
    [DataRow("Major", "8.0.12")]
    [DataRow("LatestMinor", "8.1.4")]
    [DataRow("LatestMajor", "9.0.3")]
    public async Task Discover_PreservesRollForwardSelection(string policy, string expected)
    {
        await WriteRuntimeConfigAsync("App", $$"""
            {"runtimeOptions":{"rollForward":"{{policy}}",
                "framework":{"name":"Microsoft.NETCore.App","version":"8.0.0"} } }
            """);
        var requirement = RuntimeRequirementDiscovery.Discover(new DirectoryInfo(_root), "x64").Frameworks.Single();

        Assert.AreEqual(Version.Parse(expected), requirement.SelectVersion(AvailableFrameworkVersions));
    }

    [TestMethod]
    public async Task Discover_DisableRejectsNewerPatchesAndFrameworkOverrideWins()
    {
        await WriteRuntimeConfigAsync("App", """
            {"runtimeOptions":{"rollForward":"LatestMajor","framework":{
                "name":"Microsoft.NETCore.App","version":"8.0.0","rollForward":"Disable"}}}
            """);
        var requirement = RuntimeRequirementDiscovery.Discover(new DirectoryInfo(_root), "x64").Frameworks.Single();

        Assert.IsTrue(requirement.IsSatisfiedBy(new Version(8, 0, 0)));
        Assert.IsFalse(requirement.IsSatisfiedBy(new Version(8, 0, 12)));
        Assert.IsNull(requirement.SelectVersion([new Version(8, 0, 12)]));
    }

    [TestMethod]
    public async Task Discover_DefaultRollForwardUsesTheNearestAvailableMinor()
    {
        await WriteRuntimeConfigAsync("App", Framework("Microsoft.NETCore.App", "8.0.0"));
        var requirement = RuntimeRequirementDiscovery.Discover(new DirectoryInfo(_root), "x64").Frameworks.Single();

        Assert.AreEqual(new Version(8, 1, 4), requirement.SelectVersion(
            [new Version(8, 1, 0), new Version(8, 1, 4), new Version(8, 2, 7), new Version(9, 0, 0)]));
    }

    [TestMethod]
    [DataRow(0, null)]
    [DataRow(1, "8.0.1")]
    [DataRow(2, "8.0.1")]
    public async Task Discover_ApplyPatchesFalsePreservesLegacyResolution(int rollForward, string? expected)
    {
        await WriteRuntimeConfigAsync("App", $$"""
            {"runtimeOptions":{"applyPatches":false,"rollForwardOnNoCandidateFx":{{rollForward}},
                "framework":{"name":"Microsoft.NETCore.App","version":"8.0.0"} } }
            """);
        var requirement = RuntimeRequirementDiscovery.Discover(new DirectoryInfo(_root), "x64").Frameworks.Single();
        Assert.AreEqual(expected, requirement.SelectVersion(
            [new Version(8, 0, 1), new Version(8, 0, 12), new Version(8, 1, 0)])?.ToString());
    }

    [TestMethod]
    public async Task Discover_ConflictingReferencesDoNotLoseTheStricterPolicy()
    {
        await WriteRuntimeConfigAsync("A", """
            {"runtimeOptions":{"framework":{
                "name":"Microsoft.NETCore.App","version":"8.0.0","rollForward":"Disable"}}}
            """);
        await WriteRuntimeConfigAsync("B", Framework("Microsoft.NETCore.App", "8.0.3"));
        var requirement = RuntimeRequirementDiscovery.Discover(new DirectoryInfo(_root), "x64").Frameworks.Single();

        Assert.IsNull(requirement.SelectVersion([new Version(8, 0, 0), new Version(8, 0, 3)]));
    }

    [TestMethod]
    [DataRow("\"rollForward\":\"NotAPolicy\"")]
    [DataRow("\"rollForward\":\"Minor\",\"applyPatches\":false")]
    [DataRow("\"rollForwardOnNoCandidateFx\":3")]
    public async Task Discover_UnsupportedConfigurationReportsAnActionableError(string settings)
    {
        await WriteRuntimeConfigAsync("App", $$"""
            {"runtimeOptions":{ {{settings}},
                "framework":{"name":"Microsoft.NETCore.App","version":"8.0.0"} } }
            """);
        var error = Assert.ThrowsExactly<ExecutionTargetException>(() =>
            RuntimeRequirementDiscovery.Discover(new DirectoryInfo(_root), "x64"));

        StringAssert.Contains(error.Message, "App.runtimeconfig.json");
        Assert.AreEqual(ExecutionTargetErrorCodes.RuntimeProvisionFailed, error.Error.Code);
    }

    [TestMethod]
    public async Task PlanId_ChangesWhenOnlyRollForwardChanges()
    {
        await WriteRuntimeConfigAsync("App", Framework("Microsoft.NETCore.App", "8.0.0"));
        var first = RuntimeRequirementDiscovery.Discover(new DirectoryInfo(_root), "x64");
        await WriteRuntimeConfigAsync("App", """
            {"runtimeOptions":{"rollForward":"Disable",
                "framework":{"name":"Microsoft.NETCore.App","version":"8.0.0"}}}
            """);
        var second = RuntimeRequirementDiscovery.Discover(new DirectoryInfo(_root), "x64");

        Assert.AreNotEqual(first.PlanId, second.PlanId);
    }

    [TestMethod]
    public async Task Discover_SelfContainedApp_RequiresNoSharedFramework()
    {
        await WriteRuntimeConfigAsync(
            "App",
            """
            {
              "runtimeOptions": {
                "tfm": "net10.0-windows10.0.19041.0",
                "includedFrameworks": [
                  { "name": "Microsoft.NETCore.App", "version": "10.0.0" }
                ]
              }
            }
            """);

        var requirements = RuntimeRequirementDiscovery.Discover(new DirectoryInfo(_root), "x64");

        // The payload ships beside the apphost. Asking a guest for it would fail exactly the apps
        // that need nothing at all.
        Assert.AreEqual(0, requirements.Frameworks.Count);
        Assert.IsTrue(requirements.IsEmpty);
    }

    [TestMethod]
    public async Task Discover_SeveralRuntimeConfigurations_TakesTheHighestConstraint()
    {
        await WriteRuntimeConfigAsync("A", Framework("Microsoft.NETCore.App", "10.0.0"));
        await WriteRuntimeConfigAsync("B", Framework("Microsoft.NETCore.App", "10.0.3"));

        var requirements = RuntimeRequirementDiscovery.Discover(new DirectoryInfo(_root), "x64");

        // Enumeration order is not guaranteed, so taking the first would make the discovered
        // requirement depend on the filesystem rather than on the build.
        Assert.AreEqual(1, requirements.Frameworks.Count);
        Assert.AreEqual("10.0.3", requirements.Frameworks[0].MinVersion);
    }

    [TestMethod]
    public async Task Discover_PrereleaseFrameworkVersions_AreReportedAsUnsupported()
    {
        await WriteRuntimeConfigAsync("A", Framework("Microsoft.NETCore.App", "10.0.0-preview.7"));
        await WriteRuntimeConfigAsync("B", Framework("Microsoft.NETCore.App", "9.0.5"));

        var error = Assert.ThrowsExactly<ExecutionTargetException>(() =>
            RuntimeRequirementDiscovery.Discover(new DirectoryInfo(_root), "x64"));
        StringAssert.Contains(error.Message, "stable major.minor.patch");
    }

    [TestMethod]
    public void Discover_NativeBuildOutput_HasNoRequirementsAtAll()
    {
        File.WriteAllText(TestPaths.Under(_root, "app.exe"), "native");

        var requirements = RuntimeRequirementDiscovery.Discover(new DirectoryInfo(_root), "x64");

        Assert.IsTrue(requirements.IsEmpty);
    }

    [TestMethod]
    public async Task Discover_MalformedRuntimeConfiguration_IsReportedInsteadOfDroppingRequirements()
    {
        await WriteRuntimeConfigAsync("A", "{ not json");

        Assert.ThrowsExactly<ExecutionTargetException>(() =>
            RuntimeRequirementDiscovery.Discover(new DirectoryInfo(_root), "x64"));
    }

    [TestMethod]
    public async Task Discover_CarriesTheDeclaredPublisherThrough()
    {
        await WriteManifestAsync("x64", ("Microsoft.WindowsAppRuntime.1.8", "8000.675.1142.0"));

        var requirements = RuntimeRequirementDiscovery.Discover(new DirectoryInfo(_root), "x64");

        // Windows resolves a framework dependency on (name, publisher). Dropping the publisher would
        // let a same-named package from anyone else look like a match.
        Assert.AreEqual("CN=Microsoft Corporation", requirements.Packages[0].Publisher);
    }

    [TestMethod]
    public async Task Discover_ADesktopApp_LeavesCoreVersionToTheSelectedDesktopRuntime()
    {
        await WriteRuntimeConfigAsync("App", Framework("Microsoft.WindowsDesktop.App", "10.0.2"));

        var requirements = RuntimeRequirementDiscovery.Discover(new DirectoryInfo(_root), "x64");

        Assert.AreEqual("Microsoft.WindowsDesktop.App", requirements.Frameworks.Single().Name);
        Assert.AreEqual("10.0.2", requirements.Frameworks.Single().MinVersion);
    }

    [TestMethod]
    public async Task Discover_AnExplicitCoreVersionIsNotLoweredByTheImpliedOne()
    {
        await WriteRuntimeConfigAsync(
            "App",
            """
            {
              "runtimeOptions": {
                "frameworks": [
                  { "name": "Microsoft.NETCore.App", "version": "10.0.7" },
                  { "name": "Microsoft.WindowsDesktop.App", "version": "10.0.2" }
                ]
              }
            }
            """);

        var requirements = RuntimeRequirementDiscovery.Discover(new DirectoryInfo(_root), "x64");

        Assert.AreEqual(
            "10.0.7",
            requirements.Frameworks.Single(framework => framework.Name == "Microsoft.NETCore.App").MinVersion);
    }

    [TestMethod]
    public async Task PlanId_IsContentAddressedAndOrderIndependent()
    {
        await WriteManifestAsync(
            "x64",
            ("Microsoft.WindowsAppRuntime.1.8", "8000.675.1142.0"),
            ("Microsoft.VCLibs.140.00.UWPDesktop", "14.0.33728.0"));

        var first = RuntimeRequirementDiscovery.Discover(new DirectoryInfo(_root), "x64");

        await WriteManifestAsync(
            "x64",
            ("Microsoft.VCLibs.140.00.UWPDesktop", "14.0.33728.0"),
            ("Microsoft.WindowsAppRuntime.1.8", "8000.675.1142.0"));

        var reordered = RuntimeRequirementDiscovery.Discover(new DirectoryInfo(_root), "x64");

        // The staged copy is shared by every deployment that needs the same graph, so the identity
        // has to depend on the graph and nothing else — including the order it was declared in.
        Assert.AreEqual(first.PlanId, reordered.PlanId);

        await WriteManifestAsync("x64", ("Microsoft.WindowsAppRuntime.1.8", "8000.999.0.0"));
        var changed = RuntimeRequirementDiscovery.Discover(new DirectoryInfo(_root), "x64");

        Assert.AreNotEqual(first.PlanId, changed.PlanId);
    }

    private static string Framework(string name, string version) =>
        $$"""
        { "runtimeOptions": { "framework": { "name": "{{name}}", "version": "{{version}}" } } }
        """;

    private Task WriteManifestAsync(string architecture, params (string Name, string MinVersion)[] dependencies)
    {
        var declared = string.Concat(dependencies.Select(dependency =>
            $"""<PackageDependency Name="{dependency.Name}" MinVersion="{dependency.MinVersion}" Publisher="CN=Microsoft Corporation" />"""));

        var manifest = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="Contoso.App" Publisher="CN=Contoso" Version="1.0.0.0" ProcessorArchitecture="{architecture}" />
              <Dependencies>
                <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.22621.0" />
                {declared}
              </Dependencies>
            </Package>
            """;

        return File.WriteAllTextAsync(
            TestPaths.Under(_root, "appxmanifest.xml"), manifest, TestContext.CancellationToken);
    }

    private Task WriteRuntimeConfigAsync(string assemblyName, string contents) =>
        File.WriteAllTextAsync(
            TestPaths.Under(_root, $"{assemblyName}.runtimeconfig.json"),
            contents,
            TestContext.CancellationToken);
}
