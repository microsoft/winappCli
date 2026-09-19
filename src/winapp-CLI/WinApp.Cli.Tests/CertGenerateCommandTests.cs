// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Extensions.Logging;
using WinApp.Cli.Commands;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for the CertGenerateCommand: option parsing, path validation, --export-cer, and --json output.
/// </summary>
[TestClass]
public class CertGenerateCommandTests : BaseCommandTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EmptyPassword_ExistingOutputSkip_PreservesNoOp(bool json)
    {
        var path = Path.Join(_tempDirectory.FullName, "skip.pfx");
        await File.WriteAllTextAsync(path, "existing certificate");
        var args = new List<string> { "--output", path, "--if-exists", "skip", "--password", "" };
        if (json) { args.Add("--json"); }

        var exitCode = await ParseAndInvokeWithCaptureAsync(GetRequiredService<CertGenerateCommand>(), args.ToArray());

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual("existing certificate", await File.ReadAllTextAsync(path));
    }

    [TestMethod]
    public async Task EmptyPassword_NonJson_ReturnsError()
    {
        var command = GetRequiredService<CertGenerateCommand>();
        var pfxPath = Path.Join(_tempDirectory.FullName, "empty-pw.pfx");

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["--publisher", "CN=EmptyPwTest", "--output", pfxPath, "--password", ""]);

        Assert.AreEqual(1, exitCode, "An explicitly empty --password must be rejected before generating a PFX.");
        StringAssert.Contains(ConsoleStdErr.ToString(), "password cannot be empty");
        Assert.IsFalse(File.Exists(pfxPath), "No certificate should be created for an empty password.");
    }

    // ── explicit --manifest publisher resolution (issue #839) ───────────

    [TestMethod]
    public async Task ExplicitManifest_MissingPublisher_NonJson_ReturnsError()
    {
        // A named --manifest that has no usable Identity/@Publisher must fail with an actionable
        // error instead of silently falling back to the OS user name (issue #839).
        var command = GetRequiredService<CertGenerateCommand>();
        var manifestPath = Path.Join(_tempDirectory.FullName, "NoPublisher.appxmanifest");
        await File.WriteAllTextAsync(manifestPath, """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="FlowHarnessApp" Version="1.0.0.0" />
            </Package>
            """);
        var pfxPath = Path.Join(_tempDirectory.FullName, "no-publisher.pfx");

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["--manifest", manifestPath, "--output", pfxPath, "--password", "testpw"]);

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(ConsoleStdErr.ToString(), "Could not extract the publisher from the manifest");
        Assert.IsFalse(File.Exists(pfxPath), "No certificate should be created when the manifest has no publisher.");
    }

    [TestMethod]
    public async Task ExplicitManifest_PublisherOnly_UsesManifestPublisher()
    {
        // A partially-complete manifest (valid Identity/@Publisher, no Applications element) is common
        // mid-development. The certificate must use that publisher, not the OS user name (issue #839).
        var command = GetRequiredService<CertGenerateCommand>();
        var manifestPath = Path.Join(_tempDirectory.FullName, "Partial.appxmanifest");
        await File.WriteAllTextAsync(manifestPath, """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="FlowHarnessApp" Publisher="CN=FlowHarnessPublisher, O=Fabrikam Inc, C=US" Version="1.0.0.0" />
            </Package>
            """);
        var pfxPath = Path.Join(_tempDirectory.FullName, "partial.pfx");

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["--manifest", manifestPath, "--output", pfxPath, "--password", "testpw"]);

        Assert.AreEqual(0, exitCode, "A manifest with a valid publisher but no Applications element must still succeed.");
        Assert.IsTrue(File.Exists(pfxPath), "The certificate should be created.");
        using var cert = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(pfxPath, "testpw");
        StringAssert.Contains(cert.Subject, "FlowHarnessPublisher");
    }

    [TestMethod]
    public async Task ExplicitManifest_Canceled_DoesNotReportAsManifestError()
    {
        // Cancellation during manifest reading must not be translated into a "Could not extract the
        // publisher" input error by the broad catch around extraction — that would misreport a
        // cancellation as bad user input (especially for manifests on slow/unavailable paths).
        var command = GetRequiredService<CertGenerateCommand>();
        var manifestPath = Path.Join(_tempDirectory.FullName, "Cancel.appxmanifest");
        await File.WriteAllTextAsync(manifestPath, """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="FlowHarnessApp" Publisher="CN=CancelPublisher" Version="1.0.0.0" />
            </Package>
            """);
        var pfxPath = Path.Join(_tempDirectory.FullName, "cancel.pfx");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["--manifest", manifestPath, "--output", pfxPath, "--password", "testpw"], cts.Token);

        Assert.AreNotEqual(0, exitCode, "A canceled operation must not report success.");
        StringAssert.DoesNotMatch(ConsoleStdErr.ToString(), new System.Text.RegularExpressions.Regex("Could not extract the publisher from the manifest"),
            "Cancellation must not be misreported as a manifest extraction error.");
        Assert.IsFalse(File.Exists(pfxPath), "No certificate should be created when the operation is canceled.");
    }

    [TestMethod]
    public void OutputOption_AcceptsPlainFileName()
    {
        // Arrange
        var command = GetRequiredService<CertGenerateCommand>();
        var args = new[] { "--output", "devcert.pfx", "--publisher", "TestPublisher" };

        // Act
        var parseResult = command.Parse(args);

        // Assert
        Assert.IsEmpty(parseResult.Errors,
            $"Plain file name should be accepted. Errors: {string.Join("; ", parseResult.Errors)}");
    }

    [TestMethod]
    public void OutputOption_AcceptsRelativePath()
    {
        // Arrange
        var command = GetRequiredService<CertGenerateCommand>();
        var args = new[] { "--output", "certs/devcert.pfx", "--publisher", "TestPublisher" };

        // Act
        var parseResult = command.Parse(args);

        // Assert
        Assert.IsEmpty(parseResult.Errors,
            $"Relative path should be accepted. Errors: {string.Join("; ", parseResult.Errors)}");
    }

    [TestMethod]
    public void OutputOption_AcceptsAbsolutePath()
    {
        // Arrange
        var command = GetRequiredService<CertGenerateCommand>();
        var absolutePath = Path.Combine(_tempDirectory.FullName, "devcert.pfx");
        var args = new[] { "--output", absolutePath, "--publisher", "TestPublisher" };

        // Act
        var parseResult = command.Parse(args);

        // Assert
        Assert.IsEmpty(parseResult.Errors,
            $"Absolute path should be accepted. Errors: {string.Join("; ", parseResult.Errors)}");
    }

    [TestMethod]
    public void OutputOption_AcceptsDotRelativePath()
    {
        // Arrange
        var command = GetRequiredService<CertGenerateCommand>();
        var args = new[] { "--output", @".\certs\devcert.pfx", "--publisher", "TestPublisher" };

        // Act
        var parseResult = command.Parse(args);

        // Assert
        Assert.IsEmpty(parseResult.Errors,
            $"Dot-relative path should be accepted. Errors: {string.Join("; ", parseResult.Errors)}");
    }

    [TestMethod]
    public void OutputOption_AcceptsParentRelativePath()
    {
        // Arrange
        var command = GetRequiredService<CertGenerateCommand>();
        var args = new[] { "--output", @"..\certs\devcert.pfx", "--publisher", "TestPublisher" };

        // Act
        var parseResult = command.Parse(args);

        // Assert
        Assert.IsEmpty(parseResult.Errors,
            $"Parent-relative path should be accepted. Errors: {string.Join("; ", parseResult.Errors)}");
    }

    [TestMethod]
    public void OutputOption_RejectsIllegalCharacters()
    {
        // Arrange
        var command = GetRequiredService<CertGenerateCommand>();
        var args = new[] { "--output", "dev|cert.pfx", "--publisher", "TestPublisher" };

        // Act
        var parseResult = command.Parse(args);

        // Assert
        Assert.IsNotEmpty(parseResult.Errors,
            "Path with illegal characters (|) should be rejected");
    }

    // ── Parse-level tests: --export-cer and --json ──────────────────────

    [TestMethod]
    public void Parse_AcceptsExportCerOption()
    {
        var command = GetRequiredService<CertGenerateCommand>();
        var args = new[] { "--publisher", "CN=Test", "--export-cer" };

        var parseResult = command.Parse(args);

        Assert.IsEmpty(parseResult.Errors,
            $"--export-cer should be accepted. Errors: {string.Join("; ", parseResult.Errors)}");
    }

    [TestMethod]
    public void Parse_AcceptsJsonOption()
    {
        var command = GetRequiredService<CertGenerateCommand>();
        var args = new[] { "--publisher", "CN=Test", "--json" };

        var parseResult = command.Parse(args);

        Assert.IsEmpty(parseResult.Errors,
            $"--json should be accepted. Errors: {string.Join("; ", parseResult.Errors)}");
    }

    // ── Invocation tests: --export-cer ──────────────────────────────────

    [TestMethod]
    public async Task ExportCer_GeneratesBothPfxAndCerFiles()
    {
        var command = GetRequiredService<CertGenerateCommand>();
        var pfxPath = Path.Combine(_tempDirectory.FullName, "export-test.pfx");
        var cerPath = Path.ChangeExtension(pfxPath, ".cer");
        var args = new[]
        {
            "--publisher", "CN=ExportCerTest",
            "--output", pfxPath,
            "--password", "testpw",
            "--export-cer"
        };

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, args);

        Assert.AreEqual(0, exitCode, "cert generate --export-cer should succeed");
        Assert.IsTrue(File.Exists(pfxPath), "PFX file should be created");
        Assert.IsTrue(File.Exists(cerPath), "CER file should be created alongside PFX");
    }

    [TestMethod]
    public async Task ExportCer_CerFileContainsPublicKeyOnly()
    {
        var command = GetRequiredService<CertGenerateCommand>();
        var pfxPath = Path.Combine(_tempDirectory.FullName, "cer-public-test.pfx");
        var cerPath = Path.ChangeExtension(pfxPath, ".cer");
        var args = new[]
        {
            "--publisher", "CN=CerPublicKeyTest",
            "--output", pfxPath,
            "--password", "testpw",
            "--export-cer"
        };

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, args);
        Assert.AreEqual(0, exitCode);

        // Load the .cer file and verify it has no private key
        using var cert = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificateFromFile(cerPath);
        Assert.IsFalse(cert.HasPrivateKey, "CER file should contain only the public key");
    }

    [TestMethod]
    public async Task WithoutExportCer_OnlyPfxIsGenerated()
    {
        var command = GetRequiredService<CertGenerateCommand>();
        var pfxPath = Path.Combine(_tempDirectory.FullName, "no-cer-test.pfx");
        var cerPath = Path.ChangeExtension(pfxPath, ".cer");
        var args = new[]
        {
            "--publisher", "CN=NoCerTest",
            "--output", pfxPath,
            "--password", "testpw"
        };

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, args);

        Assert.AreEqual(0, exitCode, "cert generate should succeed");
        Assert.IsTrue(File.Exists(pfxPath), "PFX file should be created");
        Assert.IsFalse(File.Exists(cerPath), "CER file should NOT be created without --export-cer");
    }

    // ── --json parse tests ──────────────────────────────────────────────
    // JSON invocation tests are in CertGenerateCommandJsonTests below
    // (requires LogLevel.None to match production --json behavior).

    // ── --json rejection on commands that don't support it ──────────────

    [TestMethod]
    public void JsonOption_RejectedOnSignCommand()
    {
        // SignCommand does not opt in to --json; passing it should produce a parse error
        var command = GetRequiredService<SignCommand>();
        var args = new[] { "file.exe", "cert.pfx", "--json" };

        var parseResult = command.Parse(args);

        Assert.IsNotEmpty(parseResult.Errors,
            "--json should be rejected on commands that do not opt in (e.g., sign)");
    }

    // ── --if-exists handling (non-JSON) ─────────────────────────────────

    [TestMethod]
    public async Task FileAlreadyExists_DefaultErrorMode_NonJson_ReturnsError()
    {
        var command = GetRequiredService<CertGenerateCommand>();
        var pfxPath = Path.Combine(_tempDirectory.FullName, "existing.pfx");
        await File.WriteAllTextAsync(pfxPath, "placeholder");

        // Default --if-exists is Error; without --json the error is logged to stderr.
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["--publisher", "CN=ExistsTest", "--output", pfxPath, "--password", "testpw"]);

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(ConsoleStdErr.ToString(), "already exists");
    }

    [TestMethod]
    public async Task FileAlreadyExists_SkipMode_ReturnsZeroWithoutOverwriting()
    {
        var command = GetRequiredService<CertGenerateCommand>();
        var pfxPath = Path.Combine(_tempDirectory.FullName, "existing.pfx");
        await File.WriteAllTextAsync(pfxPath, "placeholder");

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["--publisher", "CN=SkipTest", "--output", pfxPath, "--if-exists", "Skip"]);

        Assert.AreEqual(0, exitCode, "Skip mode should succeed without regenerating");
        Assert.AreEqual("placeholder", await File.ReadAllTextAsync(pfxPath, TestContext.CancellationToken),
            "Skip mode must leave the existing certificate file untouched");
    }

    [TestMethod]
    public async Task FileAlreadyExists_OverwriteMode_RegeneratesCertificate()
    {
        var command = GetRequiredService<CertGenerateCommand>();
        var pfxPath = Path.Combine(_tempDirectory.FullName, "existing.pfx");
        await File.WriteAllTextAsync(pfxPath, "placeholder");

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["--publisher", "CN=OverwriteTest", "--output", pfxPath, "--if-exists", "Overwrite"]);

        Assert.AreEqual(0, exitCode, "Overwrite mode should regenerate the certificate");
        Assert.AreNotEqual("placeholder", await File.ReadAllTextAsync(pfxPath, TestContext.CancellationToken),
            "Overwrite mode must replace the existing file with a real certificate");
    }
}

/// <summary>
/// JSON invocation tests for CertGenerateCommand. Uses LogLevel.None to match
/// production --json behavior (no log output, only structured JSON on stdout).
/// </summary>
[TestClass]
public class CertGenerateCommandJsonTests() : BaseCommandTests(logLevel: LogLevel.None)
{
    [TestMethod]
    public async Task JsonOutput_IsValidAndContainsExpectedFields()
    {
        var command = GetRequiredService<CertGenerateCommand>();
        var pfxPath = Path.Combine(_tempDirectory.FullName, "json-test.pfx");
        var args = new[]
        {
            "--publisher", "CN=JsonOutputTest",
            "--output", pfxPath,
            "--password", "jsonpw",
            "--json"
        };

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, args);
        Assert.AreEqual(0, exitCode, "cert generate --json should succeed");

        var output = TestAnsiConsole.Output.Trim();
        var jsonDoc = JsonDocument.Parse(output);
        var root = jsonDoc.RootElement;

        Assert.IsTrue(root.TryGetProperty("certificatePath", out var certPathProp), "JSON should contain 'certificatePath'");
        StringAssert.EndsWith(certPathProp.GetString()!, ".pfx");
        Assert.IsTrue(File.Exists(certPathProp.GetString()!), "certificatePath should point to an existing file");

        Assert.IsTrue(root.TryGetProperty("password", out var passwordProp), "JSON should contain 'password'");
        Assert.AreEqual("jsonpw", passwordProp.GetString());

        Assert.IsTrue(root.TryGetProperty("publisher", out _), "JSON should contain 'publisher'");
        Assert.IsTrue(root.TryGetProperty("subjectName", out _), "JSON should contain 'subjectName'");
    }

    [TestMethod]
    public async Task JsonOutput_WithExportCer_IncludesPublicCertificatePath()
    {
        var command = GetRequiredService<CertGenerateCommand>();
        var pfxPath = Path.Combine(_tempDirectory.FullName, "json-cer-test.pfx");
        var args = new[]
        {
            "--publisher", "CN=JsonCerTest",
            "--output", pfxPath,
            "--password", "testpw",
            "--export-cer",
            "--json"
        };

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, args);
        Assert.AreEqual(0, exitCode, "cert generate --json --export-cer should succeed");

        var output = TestAnsiConsole.Output.Trim();
        var jsonDoc = JsonDocument.Parse(output);
        var root = jsonDoc.RootElement;

        Assert.IsTrue(root.TryGetProperty("publicCertificatePath", out var cerPathProp),
            "JSON should contain 'publicCertificatePath' when --export-cer is used");
        StringAssert.EndsWith(cerPathProp.GetString()!, ".cer");
        Assert.IsTrue(File.Exists(cerPathProp.GetString()!), "publicCertificatePath should point to an existing .cer file");
    }

    [TestMethod]
    public async Task JsonOutput_WithoutExportCer_OmitsPublicCertificatePath()
    {
        var command = GetRequiredService<CertGenerateCommand>();
        var pfxPath = Path.Combine(_tempDirectory.FullName, "json-no-cer-test.pfx");
        var args = new[]
        {
            "--publisher", "CN=JsonNoCerTest",
            "--output", pfxPath,
            "--password", "testpw",
            "--json"
        };

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, args);
        Assert.AreEqual(0, exitCode);

        var output = TestAnsiConsole.Output.Trim();
        var jsonDoc = JsonDocument.Parse(output);
        var root = jsonDoc.RootElement;

        // publicCertificatePath should be omitted (WhenWritingNull) when --export-cer is not used
        Assert.IsFalse(root.TryGetProperty("publicCertificatePath", out _),
            "JSON should NOT contain 'publicCertificatePath' when --export-cer is not used");
    }

    [TestMethod]
    public async Task JsonError_FileAlreadyExistsOutputsJsonError()
    {
        var command = GetRequiredService<CertGenerateCommand>();
        var pfxPath = Path.Combine(_tempDirectory.FullName, "existing.pfx");
        // Create the file first so it already exists
        await File.WriteAllTextAsync(pfxPath, "placeholder");

        var args = new[]
        {
            "--publisher", "CN=AlreadyExistsTest",
            "--output", pfxPath,
            "--password", "testpw",
            "--json"
            // default --if-exists is Error
        };

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, args);
        Assert.AreEqual(1, exitCode, "cert generate --json should fail when file exists");

        var output = TestAnsiConsole.Output.Trim();
        var jsonDoc = JsonDocument.Parse(output);
        var root = jsonDoc.RootElement;

        Assert.IsTrue(root.TryGetProperty("error", out var errorProp), "JSON error output should contain 'error' property");
        StringAssert.Contains(errorProp.GetString(), "already exists");
    }

    [TestMethod]
    public async Task EmptyPassword_Json_OutputsJsonError()
    {
        var command = GetRequiredService<CertGenerateCommand>();
        var pfxPath = Path.Join(_tempDirectory.FullName, "empty-pw-json.pfx");

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["--publisher", "CN=EmptyPwTest", "--output", pfxPath, "--password", "   ", "--json"]);

        Assert.AreEqual(1, exitCode);
        var root = JsonDocument.Parse(TestAnsiConsole.Output.Trim()).RootElement;
        Assert.IsTrue(root.TryGetProperty("error", out var errorProp), "JSON error output should contain 'error' property");
        StringAssert.Contains(errorProp.GetString(), "password cannot be empty");
        Assert.IsFalse(File.Exists(pfxPath));
    }

    [TestMethod]
    public async Task ExplicitManifest_MissingPublisher_Json_OutputsSingleJsonError()
    {
        // The VS Code extension passes --manifest with --json. A manifest that can't yield a publisher
        // must produce exactly one structured error document — not empty stdout, and not two documents
        // (issue #839). JsonDocument.Parse over the full output verifies it is a single JSON document.
        var command = GetRequiredService<CertGenerateCommand>();
        var manifestPath = Path.Join(_tempDirectory.FullName, "NoPublisher.appxmanifest");
        await File.WriteAllTextAsync(manifestPath, """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="FlowHarnessApp" Version="1.0.0.0" />
            </Package>
            """);
        var pfxPath = Path.Join(_tempDirectory.FullName, "no-publisher-json.pfx");

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["--manifest", manifestPath, "--output", pfxPath, "--password", "testpw", "--json"]);

        Assert.AreEqual(1, exitCode);
        var root = JsonDocument.Parse(TestAnsiConsole.Output.Trim()).RootElement;
        Assert.IsTrue(root.TryGetProperty("error", out var errorProp), "JSON error output should contain 'error' property");
        StringAssert.Contains(errorProp.GetString(), "Could not extract the publisher from the manifest");
        Assert.IsFalse(File.Exists(pfxPath));
    }
}
