// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="SignCommand"/> that inject a fake <see cref="ICertificateService"/> so the
/// success and generic-exception branches of the handler are exercised deterministically. The
/// real-service <see cref="SignCommandTests"/> cover the InvalidOperationException path (signtool /
/// BuildTools unavailable); these cover the branches a real environment cannot reliably reach.
/// </summary>
[TestClass]
public class SignCommandFakeTests : BaseCommandTests
{
    private FakeCertificateService _fakeCert = null!;
    private FileInfo _file = null!;
    private FileInfo _cert = null!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _fakeCert = new FakeCertificateService();
        return services.AddSingleton<ICertificateService>(_fakeCert);
    }

    [TestInitialize]
    public void CreateInputs()
    {
        _file = new FileInfo(Path.Combine(_tempDirectory.FullName, "app.msix"));
        File.WriteAllText(_file.FullName, "package");
        _cert = new FileInfo(Path.Combine(_tempDirectory.FullName, "dev.pfx"));
        File.WriteAllText(_cert.FullName, "cert");
    }

    [TestMethod]
    public async Task Sign_Success_ReturnsZeroAndForwardsArguments()
    {
        var command = GetRequiredService<SignCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command,
            [_file.FullName, _cert.FullName, "--password", "s3cret", "--timestamp", "http://ts.example"]);

        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(_fakeCert.SignWasCalled);
        Assert.AreEqual(_file.FullName, _fakeCert.LastSignedFile!.FullName);
        Assert.AreEqual(_cert.FullName, _fakeCert.LastCertificatePath!.FullName);
        Assert.AreEqual("s3cret", _fakeCert.LastPassword);
        Assert.AreEqual("http://ts.example", _fakeCert.LastTimestampUrl);
    }

    [TestMethod]
    public async Task Sign_DefaultPassword_WhenNoPasswordOption()
    {
        var command = GetRequiredService<SignCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_file.FullName, _cert.FullName]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual("password", _fakeCert.LastPassword, "Password option should default to 'password'");
        Assert.IsNull(_fakeCert.LastTimestampUrl, "Timestamp should be null when --timestamp is omitted");
    }

    [TestMethod]
    public async Task Sign_InvalidOperationException_ReturnsErrorWithMessage()
    {
        _fakeCert.SignException = new InvalidOperationException("signtool missing");
        var command = GetRequiredService<SignCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_file.FullName, _cert.FullName]);

        Assert.AreEqual(1, exitCode);
        Assert.Contains("signtool missing", ConsoleStdErr.ToString());
    }

    [TestMethod]
    public async Task Sign_UnexpectedException_ReturnsErrorWithMessage()
    {
        _fakeCert.SignException = new IOException("disk exploded");
        var command = GetRequiredService<SignCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_file.FullName, _cert.FullName]);

        Assert.AreEqual(1, exitCode);
        Assert.Contains("disk exploded", ConsoleStdErr.ToString());
    }
}
