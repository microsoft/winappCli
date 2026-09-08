// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests the Authenticode gate on cppwinrt.exe. The tool is downloaded from whatever NuGet feeds the
/// project configures and then executed, so a custom or compromised feed could otherwise supply an
/// unsigned replacement that runs as the invoking user. Build tools were already gated this way; this
/// pins the same guarantee for cppwinrt.
/// </summary>
[TestClass]
[DoNotParallelize] // mutates the process-wide CppWinrtService.SignatureVerifier seam
public class CppWinrtSignatureVerificationTests : BaseCommandTests
{
    /// <summary>
    /// Writes a stand-in cppwinrt.exe that would succeed if it were ever executed, so a passing
    /// "did not run" assertion can only come from the signature gate.
    /// </summary>
    private (FileInfo Exe, FileInfo Winmd, DirectoryInfo Output) CreateFixture()
    {
        var binDir = _tempDirectory.CreateSubdirectory("bin");
        var exe = new FileInfo(Path.Join(binDir.FullName, "cppwinrt.cmd"));
        File.WriteAllText(exe.FullName, "@echo off\r\nexit /b 0\r\n");

        var winmd = new FileInfo(Path.Join(_tempDirectory.FullName, "Test.winmd"));
        File.WriteAllText(winmd.FullName, "winmd");

        return (exe, winmd, _tempDirectory.CreateSubdirectory("out"));
    }

    [TestMethod]
    public async Task RunWithRspAsync_UnsignedTool_ThrowsAndDoesNotRunIt()
    {
        var original = CppWinrtService.SignatureVerifier;
        try
        {
            CppWinrtService.SignatureVerifier = static (_, _) => false;

            var (exe, winmd, output) = CreateFixture();
            var service = new CppWinrtService(NullLogger<CppWinrtService>.Instance);

            var ex = await Assert.ThrowsExactlyAsync<BuildToolSignatureException>(
                () => service.RunWithRspAsync(exe, [winmd], output, _tempDirectory, TestTaskContext, TestContext.CancellationToken));

            StringAssert.Contains(ex.Message, exe.Name, StringComparison.Ordinal);
            // The gate must run before the response file is written, so nothing is handed to the tool.
            Assert.IsFalse(
                File.Exists(Path.Join(output.FullName, ".cppwinrt.rsp")),
                "the response file must not be written for a tool that is never going to run");
        }
        finally
        {
            CppWinrtService.SignatureVerifier = original;
        }
    }

    [TestMethod]
    public async Task RunWithRspAsync_SignedTool_Runs()
    {
        var original = CppWinrtService.SignatureVerifier;
        try
        {
            CppWinrtService.SignatureVerifier = static (_, _) => true;

            var (exe, winmd, output) = CreateFixture();
            var service = new CppWinrtService(NullLogger<CppWinrtService>.Instance);

            await service.RunWithRspAsync(exe, [winmd], output, _tempDirectory, TestTaskContext, TestContext.CancellationToken);

            Assert.IsTrue(File.Exists(Path.Join(output.FullName, ".cppwinrt.rsp")));
        }
        finally
        {
            CppWinrtService.SignatureVerifier = original;
        }
    }

    /// <summary>
    /// The seam must default to the real verifier, so production is actually gated rather than merely
    /// gate-able. Compares the captured delegates' target methods rather than the delegates themselves,
    /// because each method-group conversion produces a distinct delegate instance.
    /// </summary>
    [TestMethod]
    public void SignatureVerifier_DefaultsToTheRealAuthenticodeVerifier()
    {
        Assert.AreEqual(
            GlobalTestSetup.ProductionSignatureVerifier.Method,
            GlobalTestSetup.ProductionCppWinrtSignatureVerifier.Method,
            "cppwinrt must use the same production Authenticode verifier as the build tools.");
    }
}
