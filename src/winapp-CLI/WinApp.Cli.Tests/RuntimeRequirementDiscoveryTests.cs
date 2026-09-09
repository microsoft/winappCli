// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Orchestration;

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
    public async Task Discover_UnpackagedBuildReadsWindowsAppSdkFromDepsJson()
    {
        await File.WriteAllTextAsync(
            TestPaths.Under(_root, "App.deps.json"),
            """
            {
              "libraries": {
                "Microsoft.WindowsAppSDK/1.8.260317003": { "type": "package" },
                "Contoso.App/1.0.0": { "type": "project" }
              }
            }
            """,
            TestContext.CancellationToken);

        var requirements = RuntimeRequirementDiscovery.Discover(new DirectoryInfo(_root), "arm64");

        Assert.AreEqual("1.8.260317003", requirements.WindowsAppSdkVersion);
        Assert.IsFalse(requirements.IsEmpty);
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
    public async Task Discover_PrereleaseFrameworkVersions_StillOrder()
    {
        await WriteRuntimeConfigAsync("A", Framework("Microsoft.NETCore.App", "10.0.0-preview.7"));
        await WriteRuntimeConfigAsync("B", Framework("Microsoft.NETCore.App", "9.0.5"));

        var requirements = RuntimeRequirementDiscovery.Discover(new DirectoryInfo(_root), "x64");

        // A version Version.TryParse rejects must not silently lose to every other candidate.
        Assert.AreEqual("10.0.0-preview.7", requirements.Frameworks[0].MinVersion);
    }

    [TestMethod]
    public void Discover_NativeBuildOutput_HasNoRequirementsAtAll()
    {
        File.WriteAllText(TestPaths.Under(_root, "app.exe"), "native");

        var requirements = RuntimeRequirementDiscovery.Discover(new DirectoryInfo(_root), "x64");

        Assert.IsTrue(requirements.IsEmpty);
    }

    [TestMethod]
    public async Task Discover_MalformedRuntimeConfiguration_IsIgnoredRatherThanFatal()
    {
        await WriteRuntimeConfigAsync("A", "{ not json");

        var requirements = RuntimeRequirementDiscovery.Discover(new DirectoryInfo(_root), "x64");

        // Refusing to run because one artifact is unreadable would be worse than letting the launch
        // report the real problem.
        Assert.AreEqual(0, requirements.Frameworks.Count);
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
    public async Task Discover_ADesktopApp_AlsoRequiresTheCoreRuntimeUnderneathIt()
    {
        await WriteRuntimeConfigAsync("App", Framework("Microsoft.WindowsDesktop.App", "10.0.2"));

        var requirements = RuntimeRequirementDiscovery.Discover(new DirectoryInfo(_root), "x64");

        // A WPF or WinForms runtime configuration names only the desktop framework, but that
        // framework is layered on the core runtime and cannot load without it. Provisioning only
        // what was written down would report a satisfied graph the app then fails to start against.
        CollectionAssert.AreEqual(
            ExpectedFrameworks,
            requirements.Frameworks.Select(framework => framework.Name).ToArray());

        Assert.IsTrue(requirements.Frameworks.All(framework => framework.MinVersion == "10.0.2"));
        Assert.IsTrue(requirements.Frameworks.All(framework => framework.Architecture == "x64"));
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
