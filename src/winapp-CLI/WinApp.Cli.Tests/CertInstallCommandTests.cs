// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Services;
using static WinApp.Cli.Services.CertificateService;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="CertInstallCommand"/>. Installing to LocalMachine\TrustedPeople requires
/// elevation, so a fake <see cref="ICertificateService"/> is used to deterministically cover the
/// installed, already-installed, and failure branches without touching the machine store.
/// </summary>
[TestClass]
public class CertInstallCommandTests : BaseCommandTests
{
    private FakeCertificateService _fakeCert = null!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _fakeCert = new FakeCertificateService();
        return services.AddSingleton<ICertificateService>(_fakeCert);
    }

    private FileInfo CreateCertFile(string name = "devcert.pfx")
    {
        var path = Path.Combine(_tempDirectory.FullName, name);
        File.WriteAllText(path, "not-a-real-cert");
        return new FileInfo(path);
    }

    // ── Parse-level tests ───────────────────────────────────────────────

    [TestMethod]
    public void Parse_AcceptsExistingCertPath()
    {
        var cert = CreateCertFile();
        var command = GetRequiredService<CertInstallCommand>();

        var parseResult = command.Parse([cert.FullName]);

        Assert.IsEmpty(parseResult.Errors, $"Errors: {string.Join("; ", parseResult.Errors)}");
    }

    [TestMethod]
    public void Parse_RejectsNonExistentCertPath()
    {
        var command = GetRequiredService<CertInstallCommand>();
        var missing = Path.Combine(_tempDirectory.FullName, "missing.pfx");

        var parseResult = command.Parse([missing]);

        Assert.IsNotEmpty(parseResult.Errors, "cert-path uses AcceptExistingOnly and should reject a missing file");
    }

    [TestMethod]
    public void Parse_PasswordDefaultsToPassword()
    {
        var cert = CreateCertFile();
        var command = GetRequiredService<CertInstallCommand>();

        var parseResult = command.Parse([cert.FullName]);

        Assert.AreEqual("password", parseResult.GetValue(CertInstallCommand.PasswordOption));
        Assert.IsFalse(parseResult.GetValue(CertInstallCommand.ForceOption));
    }

    // ── Invocation tests ────────────────────────────────────────────────

    [TestMethod]
    public async Task Install_NewCertificate_Succeeds()
    {
        var cert = CreateCertFile();
        _fakeCert.InstallResult = true;
        var command = GetRequiredService<CertInstallCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [cert.FullName]);

        Assert.AreEqual(0, exitCode);
        Assert.HasCount(1, _fakeCert.InstallCalls);
        Assert.AreEqual(cert.FullName, _fakeCert.InstallCalls[0].CertPath);
        Assert.AreEqual("password", _fakeCert.InstallCalls[0].Password);
        Assert.IsFalse(_fakeCert.InstallCalls[0].Force);
    }

    [TestMethod]
    public async Task Install_AlreadyInstalled_Succeeds()
    {
        var cert = CreateCertFile();
        _fakeCert.InstallResult = false; // already present
        var command = GetRequiredService<CertInstallCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [cert.FullName]);

        Assert.AreEqual(0, exitCode, "An already-installed certificate is not an error");
    }

    [TestMethod]
    public async Task Install_ForwardsForceAndPassword()
    {
        var cert = CreateCertFile();
        var command = GetRequiredService<CertInstallCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [cert.FullName, "--password", "s3cret", "--force"]);

        Assert.AreEqual(0, exitCode);
        var call = _fakeCert.InstallCalls[0];
        Assert.AreEqual("s3cret", call.Password);
        Assert.IsTrue(call.Force);
    }

    [TestMethod]
    public async Task Install_ServiceThrows_ReturnsError()
    {
        var cert = CreateCertFile();
        _fakeCert.InstallException = new InvalidOperationException("Administrator privileges are required");
        var command = GetRequiredService<CertInstallCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [cert.FullName]);

        Assert.AreEqual(1, exitCode);
        var stderr = ConsoleStdErr.ToString();
        Assert.Contains("Failed to install certificate", stderr);
        Assert.Contains("Administrator privileges are required", stderr);
    }

    /// <summary>
    /// Fake certificate service that records install calls and returns a configurable result or
    /// throws. Only <see cref="InstallCertificate"/> is exercised by these tests.
    /// </summary>
    private sealed class FakeCertificateService : ICertificateService
    {
        public List<(string CertPath, string Password, bool Force)> InstallCalls { get; } = [];
        public bool InstallResult { get; set; } = true;
        public Exception? InstallException { get; set; }

        public bool InstallCertificate(FileInfo certPath, string password, bool force, TaskContext taskContext)
        {
            InstallCalls.Add((certPath.FullName, password, force));
            if (InstallException != null)
            {
                throw InstallException;
            }
            return InstallResult;
        }

        public Task<CertificateResult> GenerateDevCertificateWithInferenceAsync(
            FileInfo outputPath, TaskContext taskContext, string? explicitPublisher = null,
            FileInfo? manifestPath = null, string password = "password", int validDays = 365,
            bool updateGitignore = true, bool install = false, bool exportCer = false,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CertificateResult> GenerateDevCertificateAsync(
            string publisher, FileInfo outputPath, TaskContext taskContext, string password = "password",
            int validDays = 365, bool exportCer = false, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SignFileAsync(FileInfo filePath, FileInfo certificatePath, TaskContext taskContext,
            string? password = "password", string? timestampUrl = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
