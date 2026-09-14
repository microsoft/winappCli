// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// PackageCommand tests that inject a fake <see cref="IMsixService"/> so the signing,
/// bundle, and failure branches can be exercised deterministically without the real
/// MSIX toolchain (makeappx/signtool), which is unavailable in the test environment.
/// </summary>
[TestClass]
public class PackageCommandFakeMsixTests : BaseCommandTests
{
    private FakeMsixService _fakeMsixService = null!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _fakeMsixService = new FakeMsixService();
        return services.AddSingleton<IMsixService>(_fakeMsixService);
    }

    [TestMethod]
    public async Task PackageCommand_MissingInputFolder_ReturnsErrorAndDoesNotPackage()
    {
        // Arrange — a single input folder that does not exist. The argument is not
        // AcceptExistingOnly, so validation happens inside the handler.
        var missing = Path.Combine(_tempDirectory.FullName, "no-such-folder");
        var packageCommand = GetRequiredService<PackageCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(packageCommand, new[] { missing });

        // Assert — error reported, and the MSIX service was never invoked.
        Assert.AreEqual(1, exitCode, "Packaging a non-existent input folder should fail");
        StringAssert.Contains(ConsoleStdErr.ToString(), "Input folder(s) not found");
        Assert.AreEqual(0, _fakeMsixService.CreatePackageCalls.Count, "Package creation should not run when input is missing");
    }

    [TestMethod]
    public async Task PackageCommand_SingleFolder_SignedResult_Succeeds()
    {
        // Arrange — the service reports the produced package was signed.
        _fakeMsixService.PackageSigned = true;
        var packageCommand = GetRequiredService<PackageCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(packageCommand, new[] { _tempDirectory.FullName });

        // Assert
        Assert.AreEqual(0, exitCode, "Signed single-package creation should succeed");
        Assert.AreEqual(1, _fakeMsixService.CreatePackageCalls.Count, "The MSIX package service should be invoked once");
        StringAssert.Contains(TestAnsiConsole.Output, "Package has been signed",
            "The signed-package confirmation message should be shown to the user");
    }

    [TestMethod]
    public async Task PackageCommand_Folder_ProjectOnlyOption_Rejected()
    {
        // Build/project options require a .csproj; on a folder input they cannot take effect and must be
        // rejected rather than silently ignored (e.g. --arch would not restage the runtime for that arch).
        var packageCommand = GetRequiredService<PackageCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(packageCommand, new[] { _tempDirectory.FullName, "--arch", "x64" });

        Assert.AreEqual(1, exitCode, "a project-only option must be rejected for a folder input");
        Assert.AreEqual(0, _fakeMsixService.CreatePackageCalls.Count, "no packaging must run when a project-only option is used with a folder");
        StringAssert.Contains(ConsoleStdErr.ToString(), "require a .csproj input");
    }

    [TestMethod]
    public async Task PackageCommand_Folder_NoSignWithCert_Rejected()
    {
        // --no-sign forces an unsigned artifact and is mutually exclusive with --cert on folder inputs too,
        // not just project mode. It must be rejected before any packaging runs.
        var pfx = new FileInfo(Path.Combine(_tempDirectory.FullName, "dev.pfx"));
        await File.WriteAllTextAsync(pfx.FullName, "pfx", TestContext.CancellationToken);
        var packageCommand = GetRequiredService<PackageCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(packageCommand, new[] { _tempDirectory.FullName, "--no-sign", "--cert", pfx.FullName });

        Assert.AreEqual(1, exitCode, "--no-sign combined with --cert must be rejected");
        Assert.AreEqual(0, _fakeMsixService.CreatePackageCalls.Count, "no packaging must run when the signing options conflict");
        StringAssert.Contains(ConsoleStdErr.ToString(), "--no-sign cannot be combined with --cert");
    }

    [TestMethod]
    public async Task PackageCommand_Bundle_NoSignWithGenerateCert_Rejected()
    {
        // The same conflict applies to the multi-folder bundle path.
        var folderA = _tempDirectory.CreateSubdirectory("arch-a");
        var folderB = _tempDirectory.CreateSubdirectory("arch-b");
        var packageCommand = GetRequiredService<PackageCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(packageCommand, new[] { folderA.FullName, folderB.FullName, "--no-sign", "--generate-cert" });

        Assert.AreEqual(1, exitCode, "--no-sign combined with --generate-cert must be rejected");
        Assert.AreEqual(0, _fakeMsixService.CreateBundleCalls.Count, "no bundling must run when the signing options conflict");
        StringAssert.Contains(ConsoleStdErr.ToString(), "--no-sign cannot be combined with --cert or --generate-cert");
    }

    [TestMethod]
    public async Task PackageCommand_SingleFolder_ServiceThrows_ReturnsError()
    {
        // Arrange — package creation blows up inside the service.
        _fakeMsixService.PackageExceptionToThrow = new InvalidOperationException("makeappx exploded");
        var packageCommand = GetRequiredService<PackageCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(packageCommand, new[] { _tempDirectory.FullName });

        // Assert — the catch block converts the exception into a friendly error + exit 1.
        Assert.AreEqual(1, exitCode, "A packaging exception should produce a non-zero exit code");
        StringAssert.Contains(ConsoleStdErr.ToString(), "Failed to create MSIX package");
        StringAssert.Contains(ConsoleStdErr.ToString(), "makeappx exploded");
    }

    [TestMethod]
    public async Task PackageCommand_MultipleFolders_SignedBundle_Succeeds()
    {
        // Arrange — two distinct input folders trigger bundle creation; report it signed.
        var folderA = _tempDirectory.CreateSubdirectory("arch-a");
        var folderB = _tempDirectory.CreateSubdirectory("arch-b");
        _fakeMsixService.BundleSigned = true;
        var packageCommand = GetRequiredService<PackageCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(packageCommand, new[] { folderA.FullName, folderB.FullName });

        // Assert
        Assert.AreEqual(0, exitCode, "Signed bundle creation should succeed");
        Assert.AreEqual(1, _fakeMsixService.CreateBundleCalls.Count, "The MSIX bundle service should be invoked once");
        Assert.AreEqual(0, _fakeMsixService.CreatePackageCalls.Count, "Single-package path should not run for multiple folders");
        StringAssert.Contains(TestAnsiConsole.Output, "Bundle has been signed",
            "The signed-bundle confirmation message should be shown to the user");
    }

    [TestMethod]
    public async Task PackageCommand_MultipleFolders_UnsignedBundle_Succeeds()
    {
        // Arrange — two folders, bundle reported unsigned (exercises the unsigned guidance branch).
        var folderA = _tempDirectory.CreateSubdirectory("arch-a");
        var folderB = _tempDirectory.CreateSubdirectory("arch-b");
        _fakeMsixService.BundleSigned = false;
        var packageCommand = GetRequiredService<PackageCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(packageCommand, new[] { folderA.FullName, folderB.FullName });

        // Assert
        Assert.AreEqual(0, exitCode, "Unsigned bundle creation should still succeed");
        Assert.AreEqual(1, _fakeMsixService.CreateBundleCalls.Count, "The MSIX bundle service should be invoked once");
        var output = TestAnsiConsole.Output;
        StringAssert.Contains(output, "unsigned", "The unsigned-bundle guidance should be shown to the user");
        StringAssert.Contains(output, "sideload", "The unsigned-bundle guidance should explain the sideload signing step");
    }

    [TestMethod]
    public async Task PackageCommand_MultipleFolders_ServiceThrows_ReturnsError()
    {
        // Arrange — bundle creation fails inside the service.
        var folderA = _tempDirectory.CreateSubdirectory("arch-a");
        var folderB = _tempDirectory.CreateSubdirectory("arch-b");
        _fakeMsixService.BundleExceptionToThrow = new InvalidOperationException("bundle merge failed");
        var packageCommand = GetRequiredService<PackageCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(packageCommand, new[] { folderA.FullName, folderB.FullName });

        // Assert — the bundle catch block reports the failure.
        Assert.AreEqual(1, exitCode, "A bundle exception should produce a non-zero exit code");
        StringAssert.Contains(ConsoleStdErr.ToString(), "Failed to create MSIX bundle");
        StringAssert.Contains(ConsoleStdErr.ToString(), "bundle merge failed");
    }
}
