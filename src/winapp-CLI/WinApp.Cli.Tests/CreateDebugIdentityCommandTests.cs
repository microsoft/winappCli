// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="CreateDebugIdentityCommand"/>: manifest resolution (explicit and
/// auto-detected), option pass-through to <see cref="IMsixService.AddSparseIdentityAsync"/>,
/// success output, and the failure branch. A fake MSIX service keeps the tests off the real
/// package-registration APIs.
/// </summary>
[TestClass]
public class CreateDebugIdentityCommandTests : BaseCommandTests
{
    private FakeMsixService _fakeMsixService = null!;

    private const string ManifestContent = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
                 IgnorableNamespaces="uap">
          <Identity Name="TestPackage" Publisher="CN=TestPublisher" Version="1.0.0.0" />
          <Properties>
            <DisplayName>Test Package</DisplayName>
            <PublisherDisplayName>Test Publisher</PublisherDisplayName>
            <Logo>Assets\Logo.png</Logo>
          </Properties>
          <Applications>
            <Application Id="TestApp" Executable="TestApp.exe" EntryPoint="Windows.FullTrustApplication" />
          </Applications>
        </Package>
        """;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _fakeMsixService = new FakeMsixService();
        return services.AddSingleton<IMsixService>(_fakeMsixService);
    }

    private FileInfo CreateManifest(string? directory = null)
    {
        directory ??= _tempDirectory.FullName;
        var path = Path.Combine(directory, "appxmanifest.xml");
        File.WriteAllText(path, ManifestContent);
        return new FileInfo(path);
    }

    // ── Parse-level tests ───────────────────────────────────────────────

    [TestMethod]
    public void Parse_NonExistentEntryPoint_ProducesError()
    {
        var command = GetRequiredService<CreateDebugIdentityCommand>();
        var missing = Path.Combine(_tempDirectory.FullName, "missing.exe");

        var parseResult = command.Parse([missing]);

        Assert.IsNotEmpty(parseResult.Errors, "entrypoint uses AcceptExistingOnly and should reject a missing file");
    }

    [TestMethod]
    public void Parse_NonExistentManifest_ProducesError()
    {
        var command = GetRequiredService<CreateDebugIdentityCommand>();
        var missing = Path.Combine(_tempDirectory.FullName, "missing.xml");

        var parseResult = command.Parse(["--manifest", missing]);

        Assert.IsNotEmpty(parseResult.Errors, "--manifest uses AcceptExistingOnly and should reject a missing file");
    }

    // ── Invocation tests ────────────────────────────────────────────────

    [TestMethod]
    public async Task Create_WithExplicitManifest_Succeeds()
    {
        var manifest = CreateManifest(Directory.CreateDirectory(Path.Combine(_tempDirectory.FullName, "explicit")).FullName);
        var command = GetRequiredService<CreateDebugIdentityCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifest.FullName]);

        Assert.AreEqual(0, exitCode);
        Assert.HasCount(1, _fakeMsixService.AddSparseIdentityCalls);
        Assert.AreEqual(manifest.FullName, _fakeMsixService.AddSparseIdentityCalls[0].ManifestPath);
    }

    [TestMethod]
    public async Task Create_AutoDetectsManifestInCurrentDirectory()
    {
        var manifest = CreateManifest(); // created in the current directory (_tempDirectory)
        var command = GetRequiredService<CreateDebugIdentityCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, []);

        Assert.AreEqual(0, exitCode);
        Assert.HasCount(1, _fakeMsixService.AddSparseIdentityCalls);
        Assert.Contains(manifest.Name, _fakeMsixService.AddSparseIdentityCalls[0].ManifestPath!);
    }

    [TestMethod]
    public async Task Create_PassesNoInstallAndKeepIdentityFlags()
    {
        CreateManifest();
        var command = GetRequiredService<CreateDebugIdentityCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--no-install", "--keep-identity"]);

        Assert.AreEqual(0, exitCode);
        var call = _fakeMsixService.AddSparseIdentityCalls[0];
        Assert.IsTrue(call.NoInstall, "--no-install should be forwarded");
        Assert.IsTrue(call.KeepIdentity, "--keep-identity should be forwarded");
    }

    [TestMethod]
    public async Task Create_ForwardsEntryPointArgument()
    {
        CreateManifest();
        var exePath = Path.Combine(_tempDirectory.FullName, "myapp.exe");
        await File.WriteAllTextAsync(exePath, "binary");
        var command = GetRequiredService<CreateDebugIdentityCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [exePath]);

        Assert.AreEqual(0, exitCode);
        var call = _fakeMsixService.AddSparseIdentityCalls[0];
        Assert.AreEqual(exePath, call.EntryPoint);
    }

    [TestMethod]
    public async Task Create_DefaultFlagsAreFalse()
    {
        CreateManifest();
        var command = GetRequiredService<CreateDebugIdentityCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, []);

        Assert.AreEqual(0, exitCode);
        var call = _fakeMsixService.AddSparseIdentityCalls[0];
        Assert.IsFalse(call.NoInstall);
        Assert.IsFalse(call.KeepIdentity);
        Assert.IsNull(call.EntryPoint, "No entrypoint argument means null should be forwarded");
    }

    [TestMethod]
    public async Task Create_ServiceThrows_ReturnsErrorWithMessage()
    {
        CreateManifest();
        _fakeMsixService.SparseExceptionToThrow = new InvalidOperationException("registration blew up");
        var command = GetRequiredService<CreateDebugIdentityCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, []);

        Assert.AreEqual(1, exitCode);
        var stderr = ConsoleStdErr.ToString();
        Assert.Contains("Failed to add package identity", stderr);
        Assert.Contains("registration blew up", stderr);
    }

    [TestMethod]
    public async Task Create_ServiceThrowsWithBlankMessage_StillReturnsError()
    {
        CreateManifest();
        // Exercises the whitespace-message branch that falls back to the outer exception message.
        _fakeMsixService.SparseExceptionToThrow = new Exception("   ");
        var command = GetRequiredService<CreateDebugIdentityCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, []);

        Assert.AreEqual(1, exitCode);
        Assert.Contains("Failed to add package identity", ConsoleStdErr.ToString());
    }
}
