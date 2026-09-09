// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Tests;

/// <summary>
/// Turning an official .NET payload into a portable layout the guest can unpack
/// (spec §"Runtime provisioning" steps 2 and 3).
/// </summary>
/// <remarks>
/// The two accepted sources are a .NET installation the host already has and the official
/// <c>Microsoft.*.App.Runtime.win-{arch}</c> runtime packs. Both are payloads Microsoft published;
/// neither is generated here. What is covered below is that the transformation is faithful, that an
/// incomplete result is refused rather than staged, and that nothing outside those two known package
/// families is ever laid out.
/// </remarks>
[TestClass]
public class RuntimeFrameworkResolverTests
{
    private const string Core = "Microsoft.NETCore.App";
    private const string Desktop = "Microsoft.WindowsDesktop.App";

    private string _root = null!;
    private string _nugetCache = null!;
    private string _winappCache = null!;
    private FakeNugetService _nuget = null!;
    private FakePackageInstallationService _installer = null!;
    private RuntimeFrameworkResolver _resolver = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = TestPaths.TempRoot(nameof(RuntimeFrameworkResolverTests));
        _nugetCache = TestPaths.Under(_root, "nuget");
        _winappCache = TestPaths.Under(_root, "winapp");

        Directory.CreateDirectory(_nugetCache);
        Directory.CreateDirectory(_winappCache);

        _nuget = new FakeNugetService { CacheDirectory = new DirectoryInfo(_nugetCache) };
        _installer = new FakePackageInstallationService();

        _resolver = new RuntimeFrameworkResolver(
            _nuget,
            _installer,
            new FakeWinappDirectoryService(new DirectoryInfo(_winappCache)))
        {
            // No host installation unless a test builds one; the machine running these tests has a
            // real .NET installed, and probing it would make the result depend on the machine.
            HostDotNetRoots = () => [],
        };
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
    public async Task Resolve_BuildsALayoutFromTheOfficialRuntimePack()
    {
        WritePack(Core, "10.0.2", "x64");

        var payload = await ResolveAsync(Core, "10.0.0", "x64");

        Assert.IsNotNull(payload);
        Assert.AreEqual("10.0.2", payload.Version);

        var entries = ReadEntries(payload.Archive);

        // The pack splits a shared framework into managed and native halves; recombining them is the
        // whole transformation, and the host resolver moves to where a .NET root exposes it.
        Assert.Contains($"shared/{Core}/10.0.2/System.Private.CoreLib.dll", entries);
        Assert.Contains($"shared/{Core}/10.0.2/coreclr.dll", entries);
        Assert.Contains($"shared/{Core}/10.0.2/{Core}.deps.json", entries);
        Assert.Contains("host/fxr/10.0.2/hostfxr.dll", entries);
        Assert.DoesNotContain($"shared/{Core}/10.0.2/hostfxr.dll", entries);

        Assert.AreEqual(0, _installer.EnsurePackageCalls.Count, "a cached pack must not trigger a restore");
    }

    [TestMethod]
    public async Task Resolve_PrefersACompleteInstallationTheHostAlreadyHas()
    {
        WritePack(Core, "10.0.2", "x64");

        var installed = TestPaths.Under(_root, "dotnet");
        WriteInstallation(installed, "10.0.4");
        _resolver.HostDotNetRoots = () => [installed];

        var payload = await ResolveAsync(Core, "10.0.0", "x64");

        // A shipped shared-framework folder needs no assembly at all: its deps.json, runtimeconfig,
        // and .version are already beside the assemblies.
        Assert.AreEqual("10.0.4", payload!.Version);
        Assert.Contains($"shared/{Core}/10.0.4/.version", ReadEntries(payload.Archive));
    }

    [TestMethod]
    public async Task Resolve_IgnoresAHostInstallationForAnotherArchitecture()
    {
        var installed = TestPaths.Under(_root, "dotnet-x86");
        WriteInstallation(installed, "10.0.4", architecture: "x86");
        _resolver.HostDotNetRoots = () => [installed];

        WritePack(Core, "10.0.2", "arm64");

        var payload = await ResolveAsync(Core, "10.0.0", "arm64");

        // An x86 shared framework cannot host an arm64 apphost, however new it is, so the pack for
        // the right architecture wins.
        Assert.AreEqual("10.0.2", payload!.Version);
    }

    [TestMethod]
    public async Task Resolve_PrefersTheOldestPackThatSatisfiesTheConstraint()
    {
        WritePack(Core, "10.0.2", "x64");
        WritePack(Core, "10.0.9", "x64");

        var payload = await ResolveAsync(Core, "10.0.1", "x64");

        Assert.AreEqual("10.0.2", payload!.Version);
    }

    [TestMethod]
    public async Task Resolve_NeverRollsForwardOntoADifferentMajor()
    {
        WritePack(Core, "10.0.2", "x64");

        var payload = await ResolveAsync(Core, "8.0.0", "x64");

        // .NET rolls forward across patches and minors only. A 10.x pack does not satisfy an 8.0
        // application, and laying one out would produce a guest that reports success and fails to
        // start.
        Assert.IsNull(payload);
    }

    [TestMethod]
    public async Task Resolve_RefusesAPackThatIsMissingWhatARuntimeNeeds()
    {
        WritePack(Core, "10.0.2", "x64", includeNative: false);

        var payload = await ResolveAsync(Core, "10.0.0", "x64");

        // Managed assemblies with no runtime beneath them look like a framework folder and can host
        // nothing. Refusing here turns that into a resolution failure the user is told about.
        Assert.IsNull(payload);
    }

    [TestMethod]
    public async Task Resolve_BuildsTheDesktopLayoutWithoutAHostResolver()
    {
        WritePack(Desktop, "10.0.2", "x64");

        var payload = await ResolveAsync(Desktop, "10.0.0", "x64");

        var entries = ReadEntries(payload!.Archive);

        // The resolver belongs to the core framework's layout. A desktop layout that carried one too
        // would publish a second copy of the same versioned folder for no reason.
        Assert.Contains($"shared/{Desktop}/10.0.2/WindowsBase.dll", entries);
        Assert.IsFalse(entries.Any(entry => entry.StartsWith("host/fxr/", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Resolve_WhenNoPackIsCached_RestoresTheExactVersionThroughTheExistingPath()
    {
        var payload = await ResolveAsync(Core, "10.0.2", "x64");

        Assert.IsNull(payload, "nothing was cached, and the fake installer writes nothing");

        // The exact version is asked for: a runtime pack is not a tool winapp is choosing, it is the
        // runtime the build already resolved against.
        var acquired = _installer.EnsurePackageCalls.Single();
        Assert.AreEqual("Microsoft.NETCore.App.Runtime.win-x64", acquired.PackageName);
        Assert.AreEqual("10.0.2", acquired.Version);
    }

    [TestMethod]
    public async Task Resolve_NeverLaysOutAFrameworkOutsideTheKnownPackages()
    {
        var payload = await ResolveAsync("Contoso.Runtime.App", "1.0.0", "x64");

        // Provisioning a framework means knowing what a valid layout of it looks like. Anything else
        // is verified in the guest and named if missing, never assembled from a guess.
        Assert.IsNull(payload);
        Assert.AreEqual(0, _installer.EnsurePackageCalls.Count);
    }

    [TestMethod]
    public async Task Resolve_ReusesAnArchiveItAlreadyBuilt()
    {
        WritePack(Core, "10.0.2", "x64");

        var first = await ResolveAsync(Core, "10.0.0", "x64");
        var stamp = first!.Archive.LastWriteTimeUtc;

        var second = await ResolveAsync(Core, "10.0.0", "x64");

        // Tens of megabytes are assembled once; every later run stages an archive the host has.
        Assert.AreEqual(first.Archive.FullName, second!.Archive.FullName);
        Assert.AreEqual(stamp, second.Archive.LastWriteTimeUtc);
    }

    private Task<RuntimeFrameworkPayload?> ResolveAsync(string name, string minVersion, string architecture) =>
        _resolver.ResolveAsync(
            new RuntimeFrameworkRequirement
            {
                Name = name,
                MinVersion = minVersion,
                Architecture = architecture,
            },
            new DirectoryInfo(_root),
            CreateTaskContext(),
            TestContext.CancellationToken);

    private static List<string> ReadEntries(FileInfo archive)
    {
        using var zip = ZipFile.OpenRead(archive.FullName);
        return [.. zip.Entries.Select(entry => entry.FullName)];
    }

    /// <summary>Writes a runtime pack shaped like the published one.</summary>
    private void WritePack(string framework, string version, string architecture, bool includeNative = true)
    {
        var packId = framework == Core
            ? $"microsoft.netcore.app.runtime.win-{architecture}"
            : $"microsoft.windowsdesktop.app.runtime.win-{architecture}";

        var runtimes = Path.Join(
            _nuget.GetNuGetGlobalPackagesDir().FullName, packId, version, "runtimes", $"win-{architecture}");

        var lib = Path.Join(runtimes, "lib", "net10.0");
        Directory.CreateDirectory(lib);

        File.WriteAllText(Path.Join(lib, $"{framework}.deps.json"), "{}");
        File.WriteAllText(Path.Join(lib, $"{framework}.runtimeconfig.json"), "{}");

        if (framework == Desktop)
        {
            File.WriteAllText(Path.Join(lib, "WindowsBase.dll"), "managed");
            File.WriteAllText(Path.Join(lib, "System.Windows.Forms.dll"), "managed");
        }
        else
        {
            File.WriteAllText(Path.Join(lib, "System.Private.CoreLib.dll"), "managed");
        }

        if (!includeNative)
        {
            return;
        }

        var native = Path.Join(runtimes, "native");
        Directory.CreateDirectory(native);

        if (framework == Core)
        {
            File.WriteAllText(Path.Join(native, "coreclr.dll"), "native");
            File.WriteAllText(Path.Join(native, "hostpolicy.dll"), "native");
            File.WriteAllText(Path.Join(native, "hostfxr.dll"), "native");
        }
        else
        {
            File.WriteAllText(Path.Join(native, "wpfgfx_cor3.dll"), "native");
        }
    }

    /// <summary>
    /// Writes a shipped .NET installation, with a real PE so the architecture probe is exercised.
    /// </summary>
    private static void WriteInstallation(string root, string version, string architecture = "x64")
    {
        var shared = Path.Join(root, "shared", Core, version);
        Directory.CreateDirectory(shared);

        File.WriteAllText(Path.Join(shared, ".version"), version);
        File.WriteAllText(Path.Join(shared, $"{Core}.deps.json"), "{}");
        File.WriteAllText(Path.Join(shared, "System.Private.CoreLib.dll"), "managed");
        File.WriteAllText(Path.Join(shared, "coreclr.dll"), "native");
        File.WriteAllBytes(Path.Join(shared, "hostpolicy.dll"), MinimalPe.ForArchitecture(architecture));

        var fxr = Path.Join(root, "host", "fxr", version);
        Directory.CreateDirectory(fxr);
        File.WriteAllText(Path.Join(fxr, "hostfxr.dll"), "native");
    }

    private static TaskContext CreateTaskContext() =>
        new(new GroupableTask("framework-test", null), null, new TestConsole(), NullLogger.Instance, new Lock());
}
