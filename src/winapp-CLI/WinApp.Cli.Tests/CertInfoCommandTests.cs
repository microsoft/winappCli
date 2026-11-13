// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using WinApp.Cli.Commands;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for the CertInfoCommand, covering parse validation, text output, JSON output, and error handling.
/// </summary>
[TestClass]
public class CertInfoCommandTests : BaseCommandTests
{
    private FileInfo _testCertificatePath = null!;

    [TestInitialize]
    public async Task Setup()
    {
        _testCertificatePath = new FileInfo(Path.Combine(_tempDirectory.FullName, "TestCert.pfx"));

        var certificateService = GetRequiredService<ICertificateService>();
        await certificateService.GenerateDevCertificateAsync(
            publisher: "CN=CertInfoTestPublisher",
            outputPath: _testCertificatePath,
            TestTaskContext,
            password: "testpassword",
            validDays: 30,
            cancellationToken: TestContext.CancellationToken);
    }

    // ── Parse-level tests ───────────────────────────────────────────────

    [TestMethod]
    public void Parse_AcceptsCertPathArgument()
    {
        var command = GetRequiredService<CertInfoCommand>();
        var args = new[] { _testCertificatePath.FullName };

        var parseResult = command.Parse(args);

        Assert.IsEmpty(parseResult.Errors,
            $"cert info should accept a cert path argument. Errors: {string.Join("; ", parseResult.Errors)}");
    }

    [TestMethod]
    public void Parse_AcceptsPasswordOption()
    {
        var command = GetRequiredService<CertInfoCommand>();
        var args = new[] { _testCertificatePath.FullName, "--password", "mypassword" };

        var parseResult = command.Parse(args);

        Assert.IsEmpty(parseResult.Errors,
            $"cert info should accept --password. Errors: {string.Join("; ", parseResult.Errors)}");
    }

    [TestMethod]
    public void Parse_AcceptsJsonOption()
    {
        var command = GetRequiredService<CertInfoCommand>();
        var args = new[] { _testCertificatePath.FullName, "--json" };

        var parseResult = command.Parse(args);

        Assert.IsEmpty(parseResult.Errors,
            $"cert info should accept --json. Errors: {string.Join("; ", parseResult.Errors)}");
    }

    [TestMethod]
    public void Parse_RejectsNonExistentFile()
    {
        var command = GetRequiredService<CertInfoCommand>();
        var args = new[] { Path.Combine(_tempDirectory.FullName, "nonexistent.pfx") };

        var parseResult = command.Parse(args);

        Assert.IsNotEmpty(parseResult.Errors,
            "cert info should reject a non-existent cert path at parse time");
    }

    // ── Invocation tests: text output ───────────────────────────────────

    [TestMethod]
    public async Task Invoke_DisplaysCertificateDetailsAsText()
    {
        var command = GetRequiredService<CertInfoCommand>();
        var args = new[] { _testCertificatePath.FullName, "--password", "testpassword" };

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, args);
        Assert.AreEqual(0, exitCode, "cert info should succeed with correct password");

        var output = TestAnsiConsole.Output;
        Assert.Contains("Subject:", output);
        Assert.Contains("CertInfoTestPublisher", output);
        Assert.Contains("Thumbprint:", output);
        Assert.Contains("Serial Number:", output);
        Assert.Contains("Not Before:", output);
        Assert.Contains("Not After:", output);
        Assert.Contains("Has Private Key: True", output);
    }

    [TestMethod]
    public async Task Invoke_DisplaysPublicCerDetails()
    {
        // A public-only .cer (as produced by `cert generate --export-cer`) must be readable by
        // `cert info` — it has no private key, so the password is irrelevant.
        var cerPath = new FileInfo(Path.Join(_tempDirectory.FullName, "public-info.cer"));
        using (var rsa = RSA.Create(2048))
        {
            var req = new CertificateRequest("CN=CertInfoCerPublisher", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30));
            File.WriteAllBytes(cerPath.FullName, cert.Export(X509ContentType.Cert));
        }

        var command = GetRequiredService<CertInfoCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [cerPath.FullName]);
        Assert.AreEqual(0, exitCode, "cert info should read a public .cer");

        var output = TestAnsiConsole.Output;
        Assert.Contains("CertInfoCerPublisher", output);
        Assert.Contains("Has Private Key: False", output);
    }

    // ── Invocation tests: JSON output ───────────────────────────────────

    [TestMethod]
    public async Task Invoke_JsonOutputIsValidAndComplete()
    {
        var command = GetRequiredService<CertInfoCommand>();
        var args = new[] { _testCertificatePath.FullName, "--password", "testpassword", "--json" };

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, args);
        Assert.AreEqual(0, exitCode, "cert info --json should succeed");

        var output = TestAnsiConsole.Output.Trim();
        var jsonDoc = JsonDocument.Parse(output);
        var root = jsonDoc.RootElement;

        Assert.IsTrue(root.TryGetProperty("subject", out var subject), "JSON should contain 'subject'");
        Assert.Contains("CertInfoTestPublisher", subject.GetString()!);

        Assert.IsTrue(root.TryGetProperty("issuer", out _), "JSON should contain 'issuer'");
        Assert.IsTrue(root.TryGetProperty("thumbprint", out _), "JSON should contain 'thumbprint'");
        Assert.IsTrue(root.TryGetProperty("serialNumber", out _), "JSON should contain 'serialNumber'");
        Assert.IsTrue(root.TryGetProperty("notBefore", out _), "JSON should contain 'notBefore'");
        Assert.IsTrue(root.TryGetProperty("notAfter", out _), "JSON should contain 'notAfter'");
        Assert.IsTrue(root.TryGetProperty("hasPrivateKey", out var hasKey), "JSON should contain 'hasPrivateKey'");
        Assert.IsTrue(hasKey.GetBoolean(), "hasPrivateKey should be true for a PFX");
    }

    // ── Invocation tests: error handling ────────────────────────────────

    [TestMethod]
    public async Task Invoke_WrongPasswordReturnsError()
    {
        var command = GetRequiredService<CertInfoCommand>();
        var args = new[] { _testCertificatePath.FullName, "--password", "wrongpassword" };

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, args);

        Assert.AreEqual(1, exitCode, "cert info with wrong password should fail with exit code 1");
    }

    // ── Invocation tests: JSON error output ─────────────────────────────

    [TestMethod]
    public async Task JsonError_WrongPasswordOutputsJsonError()
    {
        var command = GetRequiredService<CertInfoCommand>();
        var args = new[] { _testCertificatePath.FullName, "--password", "wrongpassword", "--json" };

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, args);
        Assert.AreEqual(1, exitCode, "cert info --json with wrong password should fail");

        var output = TestAnsiConsole.Output.Trim();
        var jsonDoc = JsonDocument.Parse(output);
        var root = jsonDoc.RootElement;

        Assert.IsTrue(root.TryGetProperty("error", out var errorProp), "JSON error output should contain 'error' property");
        Assert.IsFalse(string.IsNullOrEmpty(errorProp.GetString()), "error message should not be empty");
    }

    // ── Invocation tests: file removed between parse and invocation (TOCTOU) ──

    [TestMethod]
    public async Task Invoke_FileRemovedAfterParse_ReturnsError()
    {
        // CertPathArgument.AcceptExistingOnly() rejects a missing file at parse time, so the
        // handler's defensive re-check (certPath.Refresh(); !Exists) is only reachable when the
        // file exists at parse time but disappears before the handler reads it.
        var tempCert = new FileInfo(Path.Combine(_tempDirectory.FullName, "toctou.pfx"));
        File.WriteAllText(tempCert.FullName, "placeholder");
        var command = GetRequiredService<CertInfoCommand>();
        var parseResult = command.Parse([tempCert.FullName]);
        Assert.IsEmpty(parseResult.Errors, "The file exists at parse time");
        tempCert.Delete();

        parseResult.InvocationConfiguration.Output = TestAnsiConsole.Profile.Out.Writer;
        parseResult.InvocationConfiguration.Error = ConsoleStdErr;
        var exitCode = await parseResult.InvokeAsync(parseResult.InvocationConfiguration, TestContext.CancellationToken);

        Assert.AreEqual(1, exitCode);
        Assert.Contains("Certificate file not found", ConsoleStdErr.ToString());
    }

    [TestMethod]
    public async Task Invoke_JsonFileRemovedAfterParse_OutputsJsonError()
    {
        // Same TOCTOU as above, but the --json branch emits a structured error instead of logging.
        var tempCert = new FileInfo(Path.Combine(_tempDirectory.FullName, "toctou-json.pfx"));
        File.WriteAllText(tempCert.FullName, "placeholder");
        var command = GetRequiredService<CertInfoCommand>();
        var parseResult = command.Parse([tempCert.FullName, "--json"]);
        Assert.IsEmpty(parseResult.Errors, "The file exists at parse time");
        tempCert.Delete();

        parseResult.InvocationConfiguration.Output = TestAnsiConsole.Profile.Out.Writer;
        parseResult.InvocationConfiguration.Error = ConsoleStdErr;
        var exitCode = await parseResult.InvokeAsync(parseResult.InvocationConfiguration, TestContext.CancellationToken);

        Assert.AreEqual(1, exitCode);
        var root = JsonDocument.Parse(TestAnsiConsole.Output.Trim()).RootElement;
        Assert.IsTrue(root.TryGetProperty("error", out var errorProp), "JSON error output should contain 'error' property");
        Assert.Contains("Certificate file not found", errorProp.GetString()!);
    }
}
