// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Spectre.Console.Testing;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Direct service-level tests for <see cref="CertificateService"/>. These exercise the
/// real certificate-generation flow plus the install / sign / publisher-inference paths,
/// injecting the OS-boundary seams (machine-store add/check, AppxPackaging event-log read)
/// so success and error branches that would otherwise require administrator privileges or a
/// real signing failure are covered without touching real elevation or SDK tools.
/// </summary>
[TestClass]
public class CertificateServiceTests : BaseCommandTests
{
    // ── helpers ─────────────────────────────────────────────────────────

    private (CertificateService svc, ConfigurableBuildToolsService bt, FakeGitignoreService gi) NewService(string? cwd = null)
    {
        var bt = new ConfigurableBuildToolsService();
        var gi = new FakeGitignoreService();
        var dir = cwd ?? _tempDirectory.FullName;
        var svc = new CertificateService(bt, gi, new CurrentDirectoryProvider(dir));
        // Load the install PFX in-memory only so no test ever persists a machine key container.
        // Production keeps the default MachineKeySet|PersistKeySet (see InstallKeyStorageFlags).
        svc.InstallKeyStorageFlags = X509KeyStorageFlags.EphemeralKeySet;
        return (svc, bt, gi);
    }

    private static TaskContext MakeContext(LogLevel minLevel, out CapturingLogger<TaskContext> logger)
    {
        var console = new TestConsole();
        logger = new CapturingLogger<TaskContext> { MinLevel = minLevel };
        return new TaskContext(new GroupableTask("cert-test", null), null, console, logger, new Lock());
    }

    private static FileInfo CreatePfx(string dir, string fileName, string subject, string password)
    {
        var path = Path.Combine(dir, Path.GetFileName(fileName));
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(2));
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx, password));
        return new FileInfo(path);
    }

    private static FileInfo CreateCer(string dir, string fileName, string subject)
    {
        var path = Path.Combine(dir, Path.GetFileName(fileName));
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(2));
        // Export the public certificate only (DER), matching `cert generate --export-cer` output.
        File.WriteAllBytes(path, cert.Export(X509ContentType.Cert));
        return new FileInfo(path);
    }

    private static FileInfo CreateManifest(string dir, string fileName, string publisher)
    {
        var path = Path.Combine(dir, Path.GetFileName(fileName));
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="Test.App" Publisher="{publisher}" Version="1.0.0.0" />
              <Applications>
                <Application Id="App" />
              </Applications>
            </Package>
            """;
        File.WriteAllText(path, xml);
        return new FileInfo(path);
    }

    // ── GenerateDevCertificateAsync ─────────────────────────────────────

    [TestMethod]
    public async Task GenerateDevCertificateAsync_Success_WritesPfxAndCer()
    {
        var (svc, _, _) = NewService();
        var pfx = new FileInfo(Path.Combine(_tempDirectory.FullName, "gen", "cert.pfx"));

        var result = await svc.GenerateDevCertificateAsync(
            "CN=GenDirectTest", pfx, TestTaskContext, password: "pw", validDays: 5, exportCer: true,
            cancellationToken: TestContext.CancellationToken);

        Assert.IsTrue(File.Exists(pfx.FullName), "PFX should be written");
        Assert.IsNotNull(result.PublicCertificatePath);
        Assert.IsTrue(File.Exists(result.PublicCertificatePath!.FullName), "CER should be written");
        Assert.AreEqual("pw", result.Password);
        StringAssert.Contains(result.SubjectName, "GenDirectTest");
        Assert.IsFalse(result.UpdatedGitignore);
    }

    [TestMethod]
    public async Task GenerateDevCertificateAsync_WriteFailure_ThrowsInvalidOperation()
    {
        var (svc, _, _) = NewService();
        // Point the output at the temp directory itself: writing bytes to a directory fails.
        var badOutput = new DirectoryInfo(_tempDirectory.FullName);
        var outputAsFile = new FileInfo(badOutput.FullName);

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            svc.GenerateDevCertificateAsync("CN=WriteFail", outputAsFile, TestTaskContext,
                cancellationToken: TestContext.CancellationToken));

        StringAssert.Contains(ex.Message, "Failed to generate development certificate");
    }

    // ── InstallCertificate ──────────────────────────────────────────────

    [TestMethod]
    public void InstallCertificate_FileNotFound_Throws()
    {
        var (svc, _, _) = NewService();
        var missing = new FileInfo(Path.Combine(_tempDirectory.FullName, "does-not-exist.pfx"));

        Assert.ThrowsExactly<FileNotFoundException>(() =>
            svc.InstallCertificate(missing, "pw", force: false, TestTaskContext));
    }

    [TestMethod]
    public void InstallCertificate_AlreadyInstalled_ReturnsFalse()
    {
        var (svc, _, _) = NewService();
        var pfx = CreatePfx(_tempDirectory.FullName, "already.pfx", "CN=Already", "pw");
        svc.IsCertificateInstalledImpl = _ => true;

        var result = svc.InstallCertificate(pfx, "pw", force: false, TestTaskContext);

        Assert.IsFalse(result, "Already-installed certificate should return false");
    }

    [TestMethod]
    public void InstallCertificate_NotInstalled_InstallsAndReturnsTrue()
    {
        var (svc, _, _) = NewService();
        var pfx = CreatePfx(_tempDirectory.FullName, "install.pfx", "CN=Install", "pw");
        svc.IsCertificateInstalledImpl = _ => false;
        var added = false;
        svc.AddCertificateToStoreImpl = _ => added = true;

        var result = svc.InstallCertificate(pfx, "pw", force: false, TestTaskContext);

        Assert.IsTrue(result, "Fresh install should return true");
        Assert.IsTrue(added, "Certificate should have been added to the store");
    }

    [TestMethod]
    public void InstallCertificate_PublicCer_InstallsAndReturnsTrue()
    {
        var (svc, _, _) = NewService();
        // A public-only .cer (as produced by `cert generate --export-cer`) has no private key
        // and is not PKCS#12, so it must load via the certificate-only fallback path.
        var cer = CreateCer(_tempDirectory.FullName, "public.cer", "CN=PublicCer");
        svc.IsCertificateInstalledImpl = _ => false;
        var addedCount = 0;
        bool? hadPrivateKey = null;
        svc.AddCertificateToStoreImpl = c =>
        {
            addedCount++;
            // Inspect the certificate inside the callback, before InstallCertificate disposes it.
            hadPrivateKey = c.HasPrivateKey;
        };

        var result = svc.InstallCertificate(cer, "unused-password", force: false, TestTaskContext);

        Assert.IsTrue(result, "Installing a public .cer should return true");
        Assert.AreEqual(1, addedCount, "Certificate should have been added to the store");
        Assert.IsFalse(hadPrivateKey, "A public .cer carries no private key");
    }

    [TestMethod]
    public void InstallCertificate_PublicCer_AlreadyInstalled_ReturnsFalse()
    {
        var (svc, _, _) = NewService();
        var cer = CreateCer(_tempDirectory.FullName, "already-public.cer", "CN=AlreadyPublicCer");
        svc.IsCertificateInstalledImpl = _ => true;

        var result = svc.InstallCertificate(cer, "unused-password", force: false, TestTaskContext);

        Assert.IsFalse(result, "Already-installed public .cer should return false");
    }

    [TestMethod]
    public void InstallCertificate_WrongPfxPassword_SurfacesPasswordError()
    {
        var (svc, _, _) = NewService();
        var pfx = CreatePfx(_tempDirectory.FullName, "wrongpw.pfx", "CN=WrongPw", "correct-pw");
        svc.AddCertificateToStoreImpl = _ => { };

        // A genuine PFX with the wrong password must surface the PFX loader's password error,
        // not be misreported as an unreadable certificate file.
        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            svc.InstallCertificate(pfx, "wrong-pw", force: true, TestTaskContext));

        StringAssert.Contains(ex.Message, "password");
    }

    [TestMethod]
    public void InstallCertificate_Force_SkipsInstalledCheck()
    {
        var (svc, _, _) = NewService();
        var pfx = CreatePfx(_tempDirectory.FullName, "force.pfx", "CN=Force", "pw");
        // If the check were consulted it would throw; force must skip it entirely.
        svc.IsCertificateInstalledImpl = _ => throw new InvalidOperationException("check must not run when force=true");
        svc.AddCertificateToStoreImpl = _ => { };

        var result = svc.InstallCertificate(pfx, "pw", force: true, TestTaskContext);

        Assert.IsTrue(result);
    }

    [TestMethod]
    public void InstallCertificate_CheckThrows_ContinuesAndInstalls()
    {
        var (svc, _, _) = NewService();
        var pfx = CreatePfx(_tempDirectory.FullName, "checkthrow.pfx", "CN=CheckThrow", "pw");
        svc.IsCertificateInstalledImpl = _ => throw new CryptographicException("boom during check");
        svc.AddCertificateToStoreImpl = _ => { };

        // A failing "already installed?" check must not abort installation.
        var result = svc.InstallCertificate(pfx, "pw", force: false, TestTaskContext);

        Assert.IsTrue(result);
    }

    [TestMethod]
    public void InstallCertificate_AccessDenied_ThrowsAdminError()
    {
        var (svc, _, _) = NewService();
        var pfx = CreatePfx(_tempDirectory.FullName, "denied.pfx", "CN=Denied", "pw");
        svc.IsCertificateInstalledImpl = _ => false;
        svc.AddCertificateToStoreImpl = _ => throw new CryptographicException("Access is denied.");

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            svc.InstallCertificate(pfx, "pw", force: false, TestTaskContext));

        StringAssert.Contains(ex.Message, "Administrator");
    }

    // NOTE: There is deliberately no un-seamed "real default" install test. The admin-error
    // path is already covered deterministically by InstallCertificate_AccessDenied_ThrowsAdminError
    // (via the AddCertificateToStoreImpl seam). Exercising the real DefaultIsCertificateInstalled
    // read + DefaultAddCertificateToStore write requires the real InstallCertificate flow, which
    // persists a machine key container and — on an elevated runner — would add a *trusted*
    // certificate to LocalMachine\TrustedPeople. That machine-state mutation must never happen in
    // a unit test, so those LocalMachine cert-store operations are the documented admin/OS-boundary
    // ceiling for this service.

    // ── SignFileAsync ───────────────────────────────────────────────────

    [TestMethod]
    public async Task SignFileAsync_Success_InvokesSigntool()
    {
        var (svc, bt, _) = NewService();
        var file = new FileInfo(Path.Combine(_tempDirectory.FullName, "app.exe"));
        await File.WriteAllTextAsync(file.FullName, "MZ");
        var cert = CreatePfx(_tempDirectory.FullName, "sign.pfx", "CN=Sign", "pw");

        await svc.SignFileAsync(file, cert, TestTaskContext, password: "pw",
            cancellationToken: TestContext.CancellationToken);

        Assert.HasCount(1, bt.Invocations);
        Assert.AreEqual("signtool.exe", bt.Invocations[0].Tool);
        StringAssert.Contains(bt.Invocations[0].Arguments, "sign /f");
        StringAssert.Contains(bt.Invocations[0].Arguments, "/fd SHA256");
    }

    [TestMethod]
    public async Task SignFileAsync_WithTimestamp_AddsTimestampArgs()
    {
        var (svc, bt, _) = NewService();
        var file = new FileInfo(Path.Combine(_tempDirectory.FullName, "app2.exe"));
        await File.WriteAllTextAsync(file.FullName, "MZ");
        var cert = CreatePfx(_tempDirectory.FullName, "sign2.pfx", "CN=Sign2", "pw");

        await svc.SignFileAsync(file, cert, TestTaskContext, password: "pw",
            timestampUrl: "http://timestamp.example/rfc3161",
            cancellationToken: TestContext.CancellationToken);

        StringAssert.Contains(bt.Invocations[0].Arguments, "/tr http://timestamp.example/rfc3161");
        StringAssert.Contains(bt.Invocations[0].Arguments, "/td SHA256");
    }

    [TestMethod]
    public async Task SignFileAsync_InvalidTimestampUrl_Throws()
    {
        var (svc, bt, _) = NewService();
        var file = new FileInfo(Path.Combine(_tempDirectory.FullName, "app-ts.exe"));
        await File.WriteAllTextAsync(file.FullName, "MZ");
        var cert = CreatePfx(_tempDirectory.FullName, "sign-ts.pfx", "CN=SignTs", "pw");

        // A non-absolute / non-http(s) timestamp URL (e.g. from a project's
        // AppxPackageSigningTimestampServerUrl) must be rejected before signtool runs.
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => svc.SignFileAsync(
            file, cert, TestTaskContext, password: "pw", timestampUrl: "ftp://ts.example",
            cancellationToken: TestContext.CancellationToken));
        Assert.HasCount(0, bt.Invocations);
    }

    [TestMethod]
    public async Task SignFileAsync_TimestampUrlWithInjectedSwitch_Rejected()
    {
        var (svc, bt, _) = NewService();
        var file = new FileInfo(Path.Combine(_tempDirectory.FullName, "app-inj.exe"));
        await File.WriteAllTextAsync(file.FullName, "MZ");
        var cert = CreatePfx(_tempDirectory.FullName, "sign-inj.pfx", "CN=SignInj", "pw");

        // An attempt to smuggle an extra signtool switch through the timestamp URL is not a valid absolute
        // URL, so it is rejected before reaching signtool (defense in depth on top of argument escaping).
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => svc.SignFileAsync(
            file, cert, TestTaskContext, password: "pw", timestampUrl: "http://ts\" /debug /tr \"http://evil",
            cancellationToken: TestContext.CancellationToken));
        Assert.HasCount(0, bt.Invocations);
    }

    [TestMethod]
    public async Task SignFileAsync_FileMissing_Throws()
    {
        var (svc, _, _) = NewService();
        var missing = new FileInfo(Path.Combine(_tempDirectory.FullName, "nope.exe"));
        var cert = CreatePfx(_tempDirectory.FullName, "sign3.pfx", "CN=Sign3", "pw");

        var ex = await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
            svc.SignFileAsync(missing, cert, TestTaskContext, cancellationToken: TestContext.CancellationToken));
        StringAssert.Contains(ex.Message, "File not found");
    }

    [TestMethod]
    public async Task SignFileAsync_CertMissing_Throws()
    {
        var (svc, _, _) = NewService();
        var file = new FileInfo(Path.Combine(_tempDirectory.FullName, "app4.exe"));
        await File.WriteAllTextAsync(file.FullName, "MZ");
        var missingCert = new FileInfo(Path.Combine(_tempDirectory.FullName, "nocert.pfx"));

        var ex = await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
            svc.SignFileAsync(file, missingCert, TestTaskContext, cancellationToken: TestContext.CancellationToken));
        StringAssert.Contains(ex.Message, "Certificate file not found");
    }

    [TestMethod]
    public async Task SignFileAsync_GenericToolFailure_WrapsError()
    {
        var (svc, bt, _) = NewService();
        var file = new FileInfo(Path.Combine(_tempDirectory.FullName, "app5.exe"));
        await File.WriteAllTextAsync(file.FullName, "MZ");
        var cert = CreatePfx(_tempDirectory.FullName, "sign5.pfx", "CN=Sign5", "pw");
        // InvalidBuildToolException without a 0x800 stdout → the AppxPackaging filter is
        // skipped and the generic catch wraps it.
        bt.RunBuildToolHandler = (_, _, _) =>
            throw new BuildToolsService.InvalidBuildToolException(4321, "some non-appx failure", "", "tool failed");

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            svc.SignFileAsync(file, cert, TestTaskContext, cancellationToken: TestContext.CancellationToken));
        StringAssert.Contains(ex.Message, "Failed to sign file");
    }

    [TestMethod]
    public async Task SignFileAsync_AppxError_NonVerbose_StripsErrorCode()
    {
        var (svc, bt, _) = NewService();
        var file = new FileInfo(Path.Combine(_tempDirectory.FullName, "app6.exe"));
        await File.WriteAllTextAsync(file.FullName, "MZ");
        var cert = CreatePfx(_tempDirectory.FullName, "sign6.pfx", "CN=Sign6", "pw");
        var ctx = MakeContext(LogLevel.Information, out _); // non-verbose

        bt.RunBuildToolHandler = (_, _, _) =>
            throw new BuildToolsService.InvalidBuildToolException(4321, "signtool 0x80080204 error", "", "signtool failed");
        svc.ReadAppxPackagingSignErrorAsync = (_, _) =>
            Task.FromResult<string?>("error 0x80080204: certificate publisher mismatch");

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            svc.SignFileAsync(file, cert, ctx, cancellationToken: TestContext.CancellationToken));

        StringAssert.Contains(ex.Message, "certificate publisher mismatch");
        Assert.IsFalse(ex.Message.Contains("0x80080204"), "non-verbose output should strip the raw error code");
    }

    [TestMethod]
    public async Task SignFileAsync_AppxError_Verbose_KeepsErrorCode()
    {
        var (svc, bt, _) = NewService();
        var file = new FileInfo(Path.Combine(_tempDirectory.FullName, "app7.exe"));
        await File.WriteAllTextAsync(file.FullName, "MZ");
        var cert = CreatePfx(_tempDirectory.FullName, "sign7.pfx", "CN=Sign7", "pw");
        var ctx = MakeContext(LogLevel.Debug, out _); // verbose

        bt.RunBuildToolHandler = (_, _, _) =>
            throw new BuildToolsService.InvalidBuildToolException(4321, "signtool 0x80080204 error", "", "signtool failed");
        svc.ReadAppxPackagingSignErrorAsync = (_, _) =>
            Task.FromResult<string?>("error 0x80080204: certificate publisher mismatch");

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            svc.SignFileAsync(file, cert, ctx, cancellationToken: TestContext.CancellationToken));

        StringAssert.Contains(ex.Message, "0x80080204");
    }

    [TestMethod]
    public async Task SignFileAsync_AppxError_NoEventRecord_RethrowsOriginal()
    {
        var (svc, bt, _) = NewService();
        var file = new FileInfo(Path.Combine(_tempDirectory.FullName, "app8.exe"));
        await File.WriteAllTextAsync(file.FullName, "MZ");
        var cert = CreatePfx(_tempDirectory.FullName, "sign8.pfx", "CN=Sign8", "pw");

        bt.RunBuildToolHandler = (_, _, _) =>
            throw new BuildToolsService.InvalidBuildToolException(4321, "signtool 0x80080204 error", "", "signtool failed");
        // No matching event found → original build-tool exception is rethrown unchanged.
        svc.ReadAppxPackagingSignErrorAsync = (_, _) => Task.FromResult<string?>(null);

        await Assert.ThrowsExactlyAsync<BuildToolsService.InvalidBuildToolException>(() =>
            svc.SignFileAsync(file, cert, TestTaskContext, cancellationToken: TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task SignFileAsync_AppxError_RealEventLogSeam_TimesOutAndRethrows()
    {
        // Exercises the real DefaultReadAppxPackagingSignErrorAsync poll loop against the
        // AppxPackaging operational log for a process id that produced no event → after the
        // timeout it returns null and the original exception is rethrown.
        var (svc, bt, _) = NewService();
        var file = new FileInfo(Path.Combine(_tempDirectory.FullName, "app9.exe"));
        await File.WriteAllTextAsync(file.FullName, "MZ");
        var cert = CreatePfx(_tempDirectory.FullName, "sign9.pfx", "CN=Sign9", "pw");

        bt.RunBuildToolHandler = (_, _, _) =>
            throw new BuildToolsService.InvalidBuildToolException(999999, "signtool 0x80080204 error", "", "signtool failed");

        await Assert.ThrowsExactlyAsync<BuildToolsService.InvalidBuildToolException>(() =>
            svc.SignFileAsync(file, cert, TestTaskContext, cancellationToken: TestContext.CancellationToken));
    }

    // ── GenerateDevCertificateWithInferenceAsync ────────────────────────

    [TestMethod]
    public async Task WithInference_ExplicitPublisher_NoInstall_UpdatesGitignore()
    {
        var (svc, _, gi) = NewService();
        var pfx = new FileInfo(Path.Combine(_tempDirectory.FullName, "inf-explicit.pfx"));

        var result = await svc.GenerateDevCertificateWithInferenceAsync(
            pfx, TestTaskContext, explicitPublisher: "CN=ExplicitPub", password: "custompw",
            cancellationToken: TestContext.CancellationToken);

        StringAssert.Contains(result.SubjectName, "ExplicitPub");
        Assert.IsTrue(result.UpdatedGitignore, "gitignore should have been updated");
        Assert.HasCount(1, gi.CertificateRequests);
    }

    [TestMethod]
    public async Task WithInference_NoGitignore_ExportCer_SetsPublicPath()
    {
        var (svc, _, gi) = NewService();
        var pfx = new FileInfo(Path.Combine(_tempDirectory.FullName, "inf-cer.pfx"));

        var result = await svc.GenerateDevCertificateWithInferenceAsync(
            pfx, TestTaskContext, explicitPublisher: "CN=ExportInf", updateGitignore: false, exportCer: true,
            cancellationToken: TestContext.CancellationToken);

        Assert.IsNotNull(result.PublicCertificatePath);
        Assert.IsFalse(result.UpdatedGitignore);
        Assert.IsEmpty(gi.CertificateRequests);
    }

    [TestMethod]
    public async Task WithInference_ExistingOutput_DefaultPassword_Succeeds()
    {
        var (svc, _, _) = NewService();
        var pfx = new FileInfo(Path.Combine(_tempDirectory.FullName, "inf-exists.pfx"));
        await File.WriteAllTextAsync(pfx.FullName, "old"); // pre-existing → hits the "already exists" branch

        var result = await svc.GenerateDevCertificateWithInferenceAsync(
            pfx, TestTaskContext, explicitPublisher: "CN=ExistsInf", // default password "password"
            cancellationToken: TestContext.CancellationToken);

        Assert.IsTrue(File.Exists(result.CertificatePath.FullName));
        Assert.AreEqual("password", result.Password);
    }

    [TestMethod]
    public async Task WithInference_Install_Success_ReportsInstalled()
    {
        var (svc, _, _) = NewService();
        var pfx = new FileInfo(Path.Combine(_tempDirectory.FullName, "inf-install.pfx"));
        svc.IsCertificateInstalledImpl = _ => false;
        svc.AddCertificateToStoreImpl = _ => { };

        var result = await svc.GenerateDevCertificateWithInferenceAsync(
            pfx, TestTaskContext, explicitPublisher: "CN=InstInf", install: true,
            cancellationToken: TestContext.CancellationToken);

        Assert.IsTrue(File.Exists(result.CertificatePath.FullName));
    }

    [TestMethod]
    public async Task WithInference_Install_AlreadyInstalled_ReportsAlready()
    {
        var (svc, _, _) = NewService();
        var pfx = new FileInfo(Path.Combine(_tempDirectory.FullName, "inf-already.pfx"));
        svc.IsCertificateInstalledImpl = _ => true; // InstallCertificate returns false

        var result = await svc.GenerateDevCertificateWithInferenceAsync(
            pfx, TestTaskContext, explicitPublisher: "CN=AlreadyInf", install: true,
            cancellationToken: TestContext.CancellationToken);

        Assert.IsTrue(File.Exists(result.CertificatePath.FullName));
    }

    [TestMethod]
    public async Task WithInference_GenerationFails_LogsAndRethrows()
    {
        var (svc, _, _) = NewService();
        // Output points at the temp directory → generation write fails inside the try.
        var outputAsFile = new FileInfo(_tempDirectory.FullName);
        var ctx = MakeContext(LogLevel.Information, out var logger);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            svc.GenerateDevCertificateWithInferenceAsync(
                outputAsFile, ctx, explicitPublisher: "CN=FailInf",
                cancellationToken: TestContext.CancellationToken));

        Assert.IsTrue(logger.Has(LogLevel.Error, "Failed to generate development certificate"));
    }

    // ── InferPublisher (via WithInference) ──────────────────────────────

    [TestMethod]
    public async Task WithInference_ManifestPath_UsesManifestPublisher()
    {
        var (svc, _, _) = NewService();
        var manifest = CreateManifest(_tempDirectory.FullName, "FromManifest.appxmanifest", "CN=ManifestPub");
        var pfx = new FileInfo(Path.Combine(_tempDirectory.FullName, "inf-manifest.pfx"));

        var result = await svc.GenerateDevCertificateWithInferenceAsync(
            pfx, TestTaskContext, manifestPath: manifest, updateGitignore: false,
            cancellationToken: TestContext.CancellationToken);

        StringAssert.Contains(result.SubjectName, "ManifestPub");
    }

    [TestMethod]
    public async Task WithInference_ManifestPathInvalid_FallsBackToDefault()
    {
        // manifestPath exists but is malformed → parse throws → falls through. Use an isolated
        // empty cwd so no project manifest is discovered and the default publisher is used.
        var isolated = _tempDirectory.CreateSubdirectory("isolated-" + Guid.NewGuid().ToString("N"));
        var (svc, _, _) = NewService(cwd: isolated.FullName);
        var badManifest = new FileInfo(Path.Combine(isolated.FullName, "Bad.appxmanifest"));
        await File.WriteAllTextAsync(badManifest.FullName, "<not-a-package/>");
        var pfx = new FileInfo(Path.Combine(isolated.FullName, "inf-bad.pfx"));

        var result = await svc.GenerateDevCertificateWithInferenceAsync(
            pfx, TestTaskContext, manifestPath: badManifest, updateGitignore: false,
            cancellationToken: TestContext.CancellationToken);

        var expected = PublisherDnHelper.Normalize(SystemDefaultsHelper.GetDefaultPublisherCN());
        Assert.AreEqual(expected, result.SubjectName);
    }

    [TestMethod]
    public async Task WithInference_ProjectManifest_UsesDiscoveredPublisher()
    {
        var projectDir = _tempDirectory.CreateSubdirectory("proj-" + Guid.NewGuid().ToString("N"));
        CreateManifest(projectDir.FullName, "Package.appxmanifest", "CN=ProjPub");
        var (svc, _, _) = NewService(cwd: projectDir.FullName);
        var pfx = new FileInfo(Path.Combine(projectDir.FullName, "inf-proj.pfx"));

        var result = await svc.GenerateDevCertificateWithInferenceAsync(
            pfx, TestTaskContext, updateGitignore: false,
            cancellationToken: TestContext.CancellationToken);

        StringAssert.Contains(result.SubjectName, "ProjPub");
    }

    [TestMethod]
    public async Task WithInference_ProjectManifestInvalid_FallsBackToDefault()
    {
        var projectDir = _tempDirectory.CreateSubdirectory("projbad-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(Path.Combine(projectDir.FullName, "Package.appxmanifest"), "<broken/>");
        var (svc, _, _) = NewService(cwd: projectDir.FullName);
        var pfx = new FileInfo(Path.Combine(projectDir.FullName, "inf-projbad.pfx"));

        var result = await svc.GenerateDevCertificateWithInferenceAsync(
            pfx, TestTaskContext, updateGitignore: false,
            cancellationToken: TestContext.CancellationToken);

        var expected = PublisherDnHelper.Normalize(SystemDefaultsHelper.GetDefaultPublisherCN());
        Assert.AreEqual(expected, result.SubjectName);
    }

    // ── ExtractPublisherFromCertificate (static) ────────────────────────

    [TestMethod]
    public void ExtractPublisher_ValidCert_ReturnsSubject()
    {
        var pfx = CreatePfx(_tempDirectory.FullName, "extract.pfx", "CN=ExtractMe", "pw");

        var subject = CertificateService.ExtractPublisherFromCertificate(pfx, "pw");

        StringAssert.Contains(subject, "CN=ExtractMe");
    }

    [TestMethod]
    public void ExtractPublisher_MissingFile_Throws()
    {
        var missing = new FileInfo(Path.Combine(_tempDirectory.FullName, "missing-extract.pfx"));

        Assert.ThrowsExactly<FileNotFoundException>(() =>
            CertificateService.ExtractPublisherFromCertificate(missing, "pw"));
    }

    [TestMethod]
    public void ExtractPublisher_WrongPassword_ThrowsInvalidOperation()
    {
        var pfx = CreatePfx(_tempDirectory.FullName, "extract-wrongpw.pfx", "CN=WrongPw", "pw");

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            CertificateService.ExtractPublisherFromCertificate(pfx, "not-the-password"));
        StringAssert.Contains(ex.Message, "Failed to extract publisher");
    }

    [TestMethod]
    public void ExtractPublisher_EmptySubject_ThrowsInvalidOperation()
    {
        var path = Path.Combine(_tempDirectory.FullName, "extract-empty.pfx");
        using (var rsa = RSA.Create(2048))
        {
            var req = new CertificateRequest("", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));
            File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx, "pw"));
        }

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            CertificateService.ExtractPublisherFromCertificate(new FileInfo(path), "pw"));
        StringAssert.Contains(ex.Message, "no subject");
    }

    // ── ValidatePublisherMatchAsync (static) ────────────────────────────

    [TestMethod]
    public async Task ValidatePublisherMatch_Matching_DoesNotThrow()
    {
        var pfx = CreatePfx(_tempDirectory.FullName, "match.pfx", "CN=MatchPub", "pw");
        var manifest = CreateManifest(_tempDirectory.FullName, "match.appxmanifest", "CN=MatchPub");

        await CertificateService.ValidatePublisherMatchAsync(pfx, "pw", manifest, TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task ValidatePublisherMatch_Mismatch_Throws()
    {
        var pfx = CreatePfx(_tempDirectory.FullName, "mismatch.pfx", "CN=CertPub", "pw");
        var manifest = CreateManifest(_tempDirectory.FullName, "mismatch.appxmanifest", "CN=OtherPub");

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            CertificateService.ValidatePublisherMatchAsync(pfx, "pw", manifest, TestContext.CancellationToken));
        StringAssert.Contains(ex.Message, "does not match");
    }

    [TestMethod]
    public async Task ValidatePublisherMatch_ExtractFails_WrapsError()
    {
        var missingCert = new FileInfo(Path.Combine(_tempDirectory.FullName, "validate-missing.pfx"));
        var manifest = CreateManifest(_tempDirectory.FullName, "validate.appxmanifest", "CN=Whatever");

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            CertificateService.ValidatePublisherMatchAsync(missingCert, "pw", manifest, TestContext.CancellationToken));
        StringAssert.Contains(ex.Message, "Failed to validate publisher match");
    }
}
