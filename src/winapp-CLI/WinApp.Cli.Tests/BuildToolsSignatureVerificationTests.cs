// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Coverage for the Authenticode gate applied to build tools before they are executed.
/// The tools are downloaded over the network and then run, and <c>winapp tool</c> lets the
/// caller name any executable in the package, so the gate is what stands between an
/// unsigned binary and <see cref="System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo)"/>.
/// </summary>
[TestClass]
[DoNotParallelize] // mutates the process-wide BuildToolsService.SignatureVerifier seam
public class BuildToolsSignatureVerificationTests : BaseCommandTests
{
    private DirectoryInfo _binDir = null!;

    [TestCleanup]
    public void RestoreVerifier()
    {
        // Hand the gate back in the state the rest of the assembly expects.
        BuildToolsService.SignatureVerifier = static (_, _) => true;
    }

    [TestInitialize]
    public void Setup()
    {
        // Mirror the NuGet cache layout the resolver expects, under the cache directory the
        // service is actually pointed at. Using _testWinappDirectory here would miss it entirely
        // and let the resolver fall through to installing the real package.
        var packagesDir = Path.Join(_testCacheDirectory.FullName, "packages");
        _binDir = Directory.CreateDirectory(Path.Join(
            packagesDir,
            BuildToolsService.BUILD_TOOLS_PACKAGE.ToLowerInvariant(),
            "10.0.26100.1742",
            "bin",
            "10.0.26100.0",
            "x64"));

        File.WriteAllText(Path.Join(_binDir.FullName, "mt.exe"), "fake tool");
    }

    [TestMethod]
    public async Task EnsureBuildToolAvailableAsync_WhenToolIsSigned_ReturnsPath()
    {
        BuildToolsService.SignatureVerifier = static (_, _) => true;
        var service = GetRequiredService<IBuildToolsService>();

        var tool = await service.EnsureBuildToolAvailableAsync("mt.exe", TestTaskContext, TestContext.CancellationToken);

        Assert.AreEqual("mt.exe", tool.Name);
    }

    [TestMethod]
    public async Task EnsureBuildToolAvailableAsync_WhenToolIsNotSigned_ThrowsAndDoesNotReturnPath()
    {
        BuildToolsService.SignatureVerifier = static (_, _) => false;
        var service = GetRequiredService<IBuildToolsService>();

        var ex = await Assert.ThrowsExactlyAsync<BuildToolSignatureException>(
            async () => await service.EnsureBuildToolAvailableAsync("mt.exe", TestTaskContext, TestContext.CancellationToken));

        Assert.Contains("not validly signed by Microsoft", ex.Message);
    }

    [TestMethod]
    public async Task EnsureBuildToolAvailableAsync_WhenToolIsNotSigned_MessageNamesTheToolAndTheRemedy()
    {
        BuildToolsService.SignatureVerifier = static (_, _) => false;
        var service = GetRequiredService<IBuildToolsService>();

        var ex = await Assert.ThrowsExactlyAsync<BuildToolSignatureException>(
            async () => await service.EnsureBuildToolAvailableAsync("mt.exe", TestTaskContext, TestContext.CancellationToken));

        Assert.Contains("mt.exe", ex.Message);
        Assert.Contains("NuGet cache", ex.Message);
    }

    [TestMethod]
    public async Task EnsureBuildToolAvailableAsync_VerifiesOnEveryResolution()
    {
        var calls = 0;
        BuildToolsService.SignatureVerifier = (_, _) => { calls++; return true; };
        var service = GetRequiredService<IBuildToolsService>();

        for (var i = 0; i < 3; i++)
        {
            await service.EnsureBuildToolAvailableAsync("mt.exe", TestTaskContext, TestContext.CancellationToken);
        }

        Assert.AreEqual(3, calls, "Every resolution must re-verify the binary currently on disk.");
    }

    [TestMethod]
    public async Task EnsureBuildToolAvailableAsync_ReverifiesABinarySwappedWithMatchingMetadata()
    {
        var calls = 0;
        BuildToolsService.SignatureVerifier = (_, _) => { calls++; return true; };
        var service = GetRequiredService<IBuildToolsService>();
        var toolPath = Path.Join(_binDir.FullName, "mt.exe");

        await service.EnsureBuildToolAvailableAsync("mt.exe", TestTaskContext, TestContext.CancellationToken);

        // Different bytes, identical length and timestamp — the metadata a memoizing cache would
        // key on, and all of it reproducible by whoever can write the file.
        var original = new FileInfo(toolPath);
        var length = (int)original.Length;
        var timestamp = original.LastWriteTimeUtc;
        File.WriteAllText(toolPath, new string('x', length));
        File.SetLastWriteTimeUtc(toolPath, timestamp);

        await service.EnsureBuildToolAvailableAsync("mt.exe", TestTaskContext, TestContext.CancellationToken);

        Assert.AreEqual(2, calls, "A swapped binary must not inherit the previous verdict.");
    }

    [TestMethod]
    public void SignatureVerifier_DefaultsToTheRealAuthenticodeVerifier()
    {
        // The delegate captured before the suite opened the gate. Asserting on a verifier this test
        // assigned itself would still pass if production were wired to an always-open gate.
        var productionVerifier = GlobalTestSetup.ProductionSignatureVerifier;

        var unsigned = Path.Join(_binDir.FullName, "definitely-not-signed.exe");
        File.WriteAllText(unsigned, "not a PE file at all");

        Assert.IsFalse(
            productionVerifier(unsigned, NullLogger.Instance),
            "The real verifier must reject a file that carries no Authenticode signature.");
    }
}

