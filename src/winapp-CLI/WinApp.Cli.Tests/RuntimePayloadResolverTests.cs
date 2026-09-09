// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Resolving official runtime payloads from the host's caches first, and acquiring only when they
/// cannot satisfy the constraint (spec §"Runtime provisioning" steps 2 and 3).
/// </summary>
/// <remarks>
/// Cache-first is not only faster; it is what makes a warm run work offline, which is the normal
/// case once the host has already restored and built the application.
/// </remarks>
[TestClass]
public class RuntimePayloadResolverTests
{
    private const string PackageName = "Microsoft.WindowsAppRuntime.1.8";
    private const string MicrosoftPublisher = "CN=Microsoft Corporation";

    private string _root = null!;
    private FakeNugetService _nuget = null!;
    private FakePackageInstallationService _installer = null!;
    private ScriptedVcLibsAcquirer _vcLibs = null!;
    private RuntimePayloadResolver _resolver = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = TestPaths.TempRoot(nameof(RuntimePayloadResolverTests));
        Directory.CreateDirectory(_root);

        _nuget = new FakeNugetService { CacheDirectory = new DirectoryInfo(_root) };
        _installer = new FakePackageInstallationService();
        _vcLibs = new ScriptedVcLibsAcquirer();
        _resolver = new RuntimePayloadResolver(_nuget, _installer, _vcLibs);
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
    public async Task Resolve_FindsACachedPayloadWithoutAcquiringAnything()
    {
        WritePayload("1.8.251106002", "x64", PackageName, "8000.675.1142.0");

        var payload = await ResolveOneAsync(PackageName, "8000.675.1142.0", "x64");

        Assert.IsNotNull(payload);
        Assert.AreEqual("8000.675.1142.0", payload.Version);
        Assert.AreEqual(0, _installer.EnsurePackageCalls.Count, "a cached payload must not trigger a download");
    }

    [TestMethod]
    public async Task Resolve_IdentifiesPayloadsByTheirManifestRatherThanTheirFileName()
    {
        // The inventory file's recorded identities are known to differ from what the packages
        // contain, and identity is the whole basis for deciding whether a constraint is met.
        WritePayload("1.8.251106002", "x64", PackageName, "8000.675.1142.0", fileName: "not-the-package-name.msix");

        var payload = await ResolveOneAsync(PackageName, "8000.675.1142.0", "x64");

        Assert.IsNotNull(payload);
        Assert.AreEqual(PackageName, payload.PackageName);
    }

    [TestMethod]
    public async Task Resolve_PrefersTheOldestVersionThatActuallySatisfies()
    {
        WritePayload("1.8.250916003", "x64", PackageName, "8000.600.0.0");
        WritePayload("1.8.251106002", "x64", PackageName, "8000.675.1142.0");
        WritePayload("1.8.260101001", "x64", PackageName, "8000.900.0.0");

        var payload = await ResolveOneAsync(PackageName, "8000.675.1142.0", "x64");

        // Installing a much newer runtime than the app was built against is not a downgrade, but it
        // is a difference between the local run and the guest one — and those are what make a
        // Sandbox failure hard to reproduce.
        Assert.AreEqual("8000.675.1142.0", payload!.Version);
    }

    [TestMethod]
    public async Task Resolve_IgnoresPayloadsForAnotherArchitecture()
    {
        WritePayload("1.8.251106002", "arm64", PackageName, "8000.675.1142.0");

        var payload = await ResolveOneAsync(PackageName, "8000.675.1142.0", "x64");

        // A same-name package built for another architecture does not satisfy the dependency, and
        // treating it as if it did would fail at registration instead of here.
        Assert.IsNull(payload);
    }

    [TestMethod]
    public async Task Resolve_IgnoresPayloadsFromAnotherPublisher()
    {
        WritePayload("1.8.251106002", "x64", PackageName, "8000.675.1142.0", publisher: "CN=Someone Else");

        var payload = await ResolveOneAsync(PackageName, "8000.675.1142.0", "x64", MicrosoftPublisher);

        // Windows resolves a framework dependency on (name, publisher). Staging a same-named package
        // from a different publisher would install something that cannot satisfy the dependency.
        Assert.IsNull(payload);
    }

    [TestMethod]
    public async Task Resolve_IgnoresPayloadsOlderThanTheConstraint()
    {
        WritePayload("1.8.250916003", "x64", PackageName, "8000.600.0.0");

        var payload = await ResolveOneAsync(PackageName, "8000.675.1142.0", "x64");

        Assert.IsNull(payload);
    }

    [TestMethod]
    public async Task Resolve_ReturnsTheWholeRuntimeInventoryBesideTheDeclaredFramework()
    {
        // What a real cached runtime directory holds. Only the Framework is ever declared, and a
        // guest that got only the Framework still cannot start a WinUI app.
        WritePayload("1.8.251106002", "x64", PackageName, "8000.675.1142.0");
        WritePayload("1.8.251106002", "x64", "Microsoft.WinAppRuntime.DDLM.8000.675.1142.0-x6", "8000.675.1142.0");
        WritePayload("1.8.251106002", "x64", "MicrosoftCorporationII.WinAppRuntime.Main.1.8", "8000.675.1142.0");
        WritePayload("1.8.251106002", "x64", "MicrosoftCorporationII.WinAppRuntime.Singleton", "8000.675.1142.0");

        var resolved = await ResolveAsync(Requirement(PackageName, "8000.675.1142.0", "x64"));

        Assert.AreEqual(4, resolved.Count);
        Assert.IsTrue(resolved.All(entry => entry.Payload is not null));

        CollectionAssert.AreEquivalent(
            new[]
            {
                PackageName,
                "Microsoft.WinAppRuntime.DDLM.8000.675.1142.0-x6",
                "MicrosoftCorporationII.WinAppRuntime.Main.1.8",
                "MicrosoftCorporationII.WinAppRuntime.Singleton",
            },
            resolved.Select(entry => entry.Requirement.Name).ToArray());

        // Every sibling is a requirement in its own right, at its own identity, so the guest
        // verifies it rather than installing it hopefully.
        var singleton = resolved.Single(entry =>
            entry.Requirement.Name == "MicrosoftCorporationII.WinAppRuntime.Singleton");

        Assert.IsTrue(singleton.Requirement.Derived);
        Assert.AreEqual("8000.675.1142.0", singleton.Requirement.MinVersion);
        Assert.AreEqual("x64", singleton.Requirement.Architecture);
    }

    [TestMethod]
    public async Task Resolve_UnpackagedBuildUsesTheExactRestoredRuntimeInventory()
    {
        const string SdkVersion = "1.8.260317003";
        WritePayload(SdkVersion, "arm64", PackageName, "8000.806.2252.0");
        WritePayload(
            SdkVersion,
            "arm64",
            "Microsoft.WinAppRuntime.DDLM.8000.806.2252.0-a6",
            "8000.806.2252.0");
        WritePayload(
            SdkVersion,
            "arm64",
            "MicrosoftCorporationII.WinAppRuntime.Main.1.8",
            "8000.806.2252.0");
        WritePayload(
            SdkVersion,
            "arm64",
            "MicrosoftCorporationII.WinAppRuntime.Singleton",
            "8000.806.2252.0");

        var resolved = await _resolver.ResolveAsync(
            new RuntimeRequirements("arm64", [], [], SdkVersion),
            new DirectoryInfo(_root),
            CreateTaskContext(),
            TestContext.CancellationToken);

        Assert.AreEqual(4, resolved.Count);
        Assert.IsTrue(resolved.All(entry => entry.Requirement.Derived));
        Assert.IsTrue(resolved.All(entry => entry.Payload is not null));
    }

    [TestMethod]
    public async Task Resolve_TakesTheInventoryFromTheRuntimeThatSatisfiesTheDeclaredFramework()
    {
        WritePayload("1.8.250916003", "x64", PackageName, "8000.600.0.0");
        WritePayload("1.8.250916003", "x64", "MicrosoftCorporationII.WinAppRuntime.Main.1.8", "8000.600.0.0");

        WritePayload("1.8.251106002", "x64", PackageName, "8000.675.1142.0");
        WritePayload("1.8.251106002", "x64", "MicrosoftCorporationII.WinAppRuntime.Main.1.8", "8000.675.1142.0");

        var resolved = await ResolveAsync(Requirement(PackageName, "8000.675.1142.0", "x64"));

        // The siblings must come from the same directory as the Framework that was chosen. Mixing
        // versions across cached runtimes is how a Framework ends up beside a Main that does not
        // match it.
        Assert.AreEqual(2, resolved.Count);
        Assert.IsTrue(resolved.All(entry => entry.Payload!.Version == "8000.675.1142.0"));
    }

    [TestMethod]
    public async Task Resolve_WhenTheCacheCannotSatisfyIt_AcquiresThroughTheExistingOfficialPath()
    {
        var payload = await ResolveOneAsync(PackageName, "8000.675.1142.0", "x64");

        Assert.IsNull(payload, "nothing was cached, and the fake installer writes nothing");

        // The workspace's pinned version wins when there is one, so no version is forced here — this
        // is the same resolution `winapp restore` performs, not a second download path.
        var acquired = _installer.EnsurePackageCalls.Single();
        Assert.AreEqual(BuildToolsService.WINAPP_SDK_RUNTIME_PACKAGE, acquired.PackageName);
        Assert.IsNull(acquired.Version);
    }

    [TestMethod]
    public async Task Resolve_NeverRestoresAPackageForADependencyOutsideTheWindowsAppRuntime()
    {
        await ResolveOneAsync("Contoso.SomeFramework", "1.0.0.0", "x64");

        // Keeping acquisition narrow is what stops this from becoming "download whatever a manifest
        // happens to name". Anything else is verified in the guest, never fetched.
        Assert.AreEqual(0, _installer.EnsurePackageCalls.Count);
    }

    [TestMethod]
    public async Task Resolve_DelegatesTheVcRuntimeToItsOwnNarrowAcquirer()
    {
        var requirement = Requirement("Microsoft.VCLibs.140.00.UWPDesktop", "14.0.33728.0", "x64");

        var acquired = new RuntimePayload(
            new FileInfo(Path.Join(_root, "vclibs.appx")),
            "Microsoft.VCLibs.140.00.UWPDesktop",
            "14.0.33728.0",
            "x64",
            MicrosoftPublisher);

        _vcLibs.Payloads[requirement.Name] = acquired;

        var resolved = await ResolveAsync(requirement);

        Assert.AreSame(acquired, resolved.Single().Payload);
        Assert.AreEqual(0, _installer.EnsurePackageCalls.Count);
    }

    [TestMethod]
    public async Task Resolve_SkipsAnUnreadablePayloadRatherThanFailing()
    {
        var directory = MsixDirectory("1.8.251106002", "x64");
        await File.WriteAllTextAsync(
            Path.Join(directory, "corrupt.msix"), "not a zip", TestContext.CancellationToken);

        WritePayload("1.8.251106002", "x64", PackageName, "8000.675.1142.0");

        var payload = await ResolveOneAsync(PackageName, "8000.675.1142.0", "x64");

        // One corrupt file in a cache winapp does not own must not fail the whole run when a good
        // sibling copy is right beside it.
        Assert.IsNotNull(payload);
    }

    private static RuntimePackageRequirement Requirement(
        string name,
        string minVersion,
        string architecture,
        string? publisher = null) =>
        new()
        {
            Name = name,
            MinVersion = minVersion,
            Architecture = architecture,
            Publisher = publisher,
        };

    private async Task<RuntimePayload?> ResolveOneAsync(
        string name,
        string minVersion,
        string architecture,
        string? publisher = null)
    {
        var resolved = await ResolveAsync(Requirement(name, minVersion, architecture, publisher));
        return resolved.Single().Payload;
    }

    private async Task<IReadOnlyList<ResolvedRuntimePackage>> ResolveAsync(RuntimePackageRequirement requirement) =>
        await _resolver.ResolveAsync(
            new RuntimeRequirements(requirement.Architecture, [requirement], []),
            new DirectoryInfo(_root),
            CreateTaskContext(),
            TestContext.CancellationToken);

    private string MsixDirectory(string packageVersion, string architecture)
    {
        var directory = Path.Join(
            _nuget.GetNuGetGlobalPackagesDir().FullName,
            BuildToolsService.WINAPP_SDK_RUNTIME_PACKAGE.ToLowerInvariant(),
            packageVersion,
            "tools",
            "MSIX",
            $"win10-{architecture}");

        Directory.CreateDirectory(directory);
        return directory;
    }

    private void WritePayload(
        string packageVersion,
        string architecture,
        string identityName,
        string identityVersion,
        string? fileName = null,
        string? publisher = null)
    {
        var path = Path.Join(
            MsixDirectory(packageVersion, architecture),
            fileName ?? $"{identityName}.msix");

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry("AppxManifest.xml").Open());

        writer.Write($"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="{identityName}" Publisher="{publisher ?? MicrosoftPublisher}" Version="{identityVersion}" ProcessorArchitecture="{architecture}" />
            </Package>
            """);
    }

    private static TaskContext CreateTaskContext() =>
        new(new GroupableTask("payload-test", null), null, new TestConsole(), NullLogger.Instance, new Lock());

    /// <summary>A VC runtime acquirer that returns exactly what a test decided is obtainable.</summary>
    private sealed class ScriptedVcLibsAcquirer : IVcLibsPayloadAcquirer
    {
        public Dictionary<string, RuntimePayload> Payloads { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<RuntimePayload?> TryAcquireAsync(
            RuntimePackageRequirement requirement,
            DirectoryInfo projectRoot,
            TaskContext taskContext,
            CancellationToken cancellationToken) =>
            Task.FromResult(Payloads.GetValueOrDefault(requirement.Name));
    }
}
