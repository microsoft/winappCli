// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;
using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace WinApp.Cli.Services;

internal partial class CertificateService(
    IBuildToolsService buildToolsService,
    IGitignoreService gitignoreService,
    ICurrentDirectoryProvider currentDirectoryProvider,
    ILogger<CertificateService> logger) : ICertificateService
{
    public const string DefaultCertFileName = "devcert.pfx";

    public record CertificateResult(
        FileInfo CertificatePath,
        string Password,
        string Publisher,
        string SubjectName
    );

    public async Task<CertificateResult> GenerateDevCertificateAsync(
        string publisher,
        FileInfo outputPath,
        string password = "password",
        int validDays = 365,
        CancellationToken cancellationToken = default)
    {
        // Ensure output directory exists
        outputPath.Directory?.Create();

        // Clean up the publisher name to ensure proper CN format
        // Remove any existing CN= prefix and clean up quotes
        var cleanPublisher = publisher.Replace("CN=", "").Replace("\"", "").Replace("'", "");

        // Ensure we have a proper CN format
        var subjectName = $"CN={cleanPublisher}";

        try
        {
            // 1) Create a persisted CNG key in MS Software KSP with AllowExport
            var creationParams = new CngKeyCreationParameters
            {
                Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
                ExportPolicy = CngExportPolicies.AllowExport,
                KeyCreationOptions = CngKeyCreationOptions.None,
                KeyUsage = CngKeyUsages.Signing
            };
            // Set length = 2048
            creationParams.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(2048), CngPropertyOptions.None));

            using var cngKey = CngKey.Create(CngAlgorithm.Rsa, $"MSIXDev-{Guid.NewGuid()}", creationParams);
            using var rsa = new RSACng(cngKey);

            // 2) Build req to mirror PS flags
            var req = new CertificateRequest(subjectName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: false));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                [new Oid("1.3.6.1.5.5.7.3.3")], critical: false));
            // BasicConstraints like PS default (non-CA, non-critical)
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));

            var notBefore = DateTimeOffset.UtcNow;
            var notAfter = DateTimeOffset.UtcNow.AddDays(validDays);
            using var cert = req.CreateSelfSigned(notBefore, notAfter);
            cert.FriendlyName = "MSIX Dev Certificate";

            using (var store = new X509Store(StoreName.My, StoreLocation.CurrentUser))
            {
                store.Open(OpenFlags.ReadWrite);
                store.Add(cert);
            }

            var pfx = cert.Export(X509ContentType.Pfx, password);
            await File.WriteAllBytesAsync(outputPath.FullName, pfx, cancellationToken);

            logger.LogDebug("Certificate generated: {OutputPath}", outputPath);

            return new CertificateResult(
                CertificatePath: outputPath,
                Password: password,
                Publisher: cleanPublisher,
                SubjectName: subjectName
            );
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"Failed to generate development certificate: {error.Message}", error);
        }
    }

    public bool InstallCertificate(FileInfo certPath, string password, bool force)
    {
        certPath.Refresh();
        if (!certPath.Exists)
        {
            throw new FileNotFoundException($"Certificate file not found: {certPath}");
        }

        logger.LogDebug("Installing development certificate: {CertPath}", certPath);

        try
        {
            // Check if certificate is already installed (unless force is true)
            if (!force)
            {
                try
                {
                    // Load the certificate to get its thumbprint/subject for comparison
                    using var certToCheck = X509CertificateLoader.LoadPkcs12FromFile(
                        certPath.FullName,
                        password,
                        X509KeyStorageFlags.Exportable);

                    // Check if this certificate is already in the TrustedPeople store
                    using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
                    store.Open(OpenFlags.ReadOnly);

                    var existingCerts = store.Certificates.Find(
                        X509FindType.FindByThumbprint,
                        certToCheck.Thumbprint,
                        validOnly: false);

                    if (existingCerts.Count > 0)
                    {
                        logger.LogDebug("Certificate appears to already be installed");
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    // Continue with installation if check fails
                    logger.LogDebug("Could not check existing certificates: {Message}", ex.Message);
                }
            }

            // Install to TrustedPeople store (required for MSIX sideloading)
            // Load the certificate from the PFX file
            using var cert = X509CertificateLoader.LoadPkcs12FromFile(
                certPath.FullName,
                password,
                X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);

            // Install to LocalMachine\TrustedPeople store (requires elevation)
            try
            {
                using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
                store.Open(OpenFlags.ReadWrite);
                store.Add(cert);
            }
            catch (CryptographicException ex) when (ex.Message.Contains("Access is denied"))
            {
                throw new InvalidOperationException(
                    "Failed to install certificate: Administrator privileges are required to install certificates to the LocalMachine store. " +
                    "Please run this command as an administrator.", ex);
            }

            logger.LogDebug("Certificate installed successfully to TrustedPeople store");

            return true;
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"Failed to install development certificate: {error.Message}", error);
        }
    }

    /// <summary>
    /// Signs a file with a certificate.
    /// This method can be used to sign any file, including but not limited to MSIX packages.
    /// </summary>
    /// <param name="filePath">Path to the file to sign</param>
    /// <param name="certificatePath">Path to the .pfx certificate file</param>
    /// <param name="password">Certificate password</param>
    /// <param name="timestampUrl">Timestamp server URL (optional)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task SignFileAsync(FileInfo filePath, FileInfo certificatePath, string? password = "password", string? timestampUrl = null, CancellationToken cancellationToken = default)
    {
        filePath.Refresh();
        if (!filePath.Exists)
        {
            throw new FileNotFoundException($"File not found: {filePath}");
        }

        certificatePath.Refresh();
        if (!certificatePath.Exists)
        {
            throw new FileNotFoundException($"Certificate file not found: {certificatePath}");
        }

        var arguments = $@"sign /f ""{certificatePath}"" /p ""{password}"" /fd SHA256";

        if (!string.IsNullOrWhiteSpace(timestampUrl))
        {
            arguments += $@" /tr ""{timestampUrl}"" /td SHA256";
        }

        arguments += $@" ""{filePath}""";

        logger.LogDebug("Signing file: {FilePath}", filePath);

        try
        {
            await buildToolsService.RunBuildToolAsync("signtool.exe", arguments, cancellationToken: cancellationToken);

            logger.LogDebug("File signed successfully");
        }
        catch (BuildToolsService.InvalidBuildToolException ex)
            when (ex.Stdout.Contains("0x800"))
        {
            var query = new EventLogQuery(
                "Microsoft-Windows-AppxPackaging/Operational",
                PathType.LogName,
                $"*[System[Level=2 and Execution[@ProcessID={ex.ProcessId}]]]");

            EventRecord? record = null;
            var timeout = TimeSpan.FromSeconds(5);
            var pollingInterval = TimeSpan.FromMilliseconds(500);
            var startTime = DateTime.UtcNow;

            while (record == null && (DateTime.UtcNow - startTime) < timeout && !cancellationToken.IsCancellationRequested)
            {
                using var reader = new EventLogReader(query);
                record = reader.ReadEvent();

                if (record != null)
                {
                    throw new InvalidOperationException($"Failed to sign file: {record.FormatDescription()}", ex);
                }
                
                await Task.Delay(pollingInterval, cancellationToken);
            }

            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to sign file: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Generates a development certificate with automatic publisher inference, console output, and installation.
    /// This method combines publisher inference, certificate generation, gitignore management, console messaging, and optional installation.
    /// </summary>
    /// <param name="outputPath">Path where the certificate should be generated</param>
    /// <param name="explicitPublisher">Explicit publisher to use (optional)</param>
    /// <param name="manifestPath">Specific manifest path to extract publisher from (optional)</param>
    /// <param name="password">Certificate password</param>
    /// <param name="validDays">Certificate validity period</param>
    /// <param name="skipIfExists">Skip generation if certificate already exists</param>
    /// <param name="updateGitignore">Whether to update .gitignore</param>
    /// <param name="install">Whether to install the certificate after generation</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Certificate generation result, or null if skipped</returns>
    public async Task<CertificateResult?> GenerateDevCertificateWithInferenceAsync(
        FileInfo outputPath,
        string? explicitPublisher = null,
        FileInfo? manifestPath = null,
        string password = "password",
        int validDays = 365,
        bool skipIfExists = true,
        bool updateGitignore = true,
        bool install = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Skip if certificate already exists and skipIfExists is true
            outputPath.Refresh();
            if (skipIfExists && outputPath.Exists)
            {
                logger.LogInformation("{UISymbol} Development certificate already exists: {OutputPath}", UiSymbols.Note, outputPath);
                return null;
            }

            // Start generation message
            logger.LogInformation("{UISymbol} Generating development certificate...", UiSymbols.Gear);

            // Get default publisher from system defaults
            var defaultPublisher = SystemDefaultsHelper.GetDefaultPublisherCN();

            // Infer publisher using the specified hierarchy
            string publisher = await InferPublisherAsync(explicitPublisher, manifestPath, defaultPublisher, cancellationToken);

            logger.LogInformation("Certificate publisher: {Publisher}", publisher);

            // Generate the certificate
            var result = await GenerateDevCertificateAsync(
                publisher,
                outputPath,
                password,
                validDays,
                cancellationToken);

            // Success message
            logger.LogInformation("{UISymbol} Development certificate generated → {CertificatePath}", UiSymbols.Check, result.CertificatePath);

            // Add certificate to .gitignore
            if (updateGitignore)
            {
                var baseDirectory = outputPath.Directory ?? new DirectoryInfo(currentDirectoryProvider.GetCurrentDirectory());
                var certFileName = result.CertificatePath.Name;
                gitignoreService.AddCertificateToGitignore(baseDirectory, certFileName);
            }

            // Display password information
            if (password == "password")
            {
                logger.LogInformation("{UISymbol} Using default password", UiSymbols.Note);
            }

            // Install certificate if requested
            if (install)
            {
                logger.LogDebug("Installing certificate...");

                var installResult = InstallCertificate(result.CertificatePath, password, false);
                if (installResult)
                {
                    logger.LogInformation("{UISymbol} Certificate installed successfully!", UiSymbols.Check);
                }
                else
                {
                    logger.LogInformation("{UISymbol} Certificate was already installed", UiSymbols.Info);
                }
            }
            else
            {
                logger.LogInformation("{UISymbol} Use 'winapp cert install' to install the certificate for development", UiSymbols.Note);
            }

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError("{UISymbol} Failed to generate development certificate: {Message}", UiSymbols.Error, ex.Message);
            logger.LogDebug(ex, "Certificate generation failed with exception");
            throw; // Re-throw for callers that want to handle the error differently
        }
    }

    /// <summary>
    /// Extracts the publisher name from a certificate file
    /// </summary>
    /// <param name="certificatePath">Path to the certificate file (.pfx)</param>
    /// <param name="password">Certificate password</param>
    /// <returns>Publisher name (without CN= prefix)</returns>
    /// <exception cref="FileNotFoundException">Certificate file not found</exception>
    /// <exception cref="InvalidOperationException">Certificate cannot be loaded or has no subject</exception>
    public static string ExtractPublisherFromCertificate(FileInfo certificatePath, string password)
    {
        certificatePath.Refresh();
        if (!certificatePath.Exists)
        {
            throw new FileNotFoundException($"Certificate file not found: {certificatePath}");
        }

        try
        {
            using var cert = X509CertificateLoader.LoadPkcs12FromFile(
                certificatePath.FullName, password, X509KeyStorageFlags.Exportable);

            var subject = cert.Subject;
            if (string.IsNullOrWhiteSpace(subject))
            {
                throw new InvalidOperationException("Certificate has no subject information");
            }

            // Extract CN from the subject (format: "CN=Publisher, O=Organization, ...")
            var cnMatch = CnFieldRegex().Match(subject);
            if (!cnMatch.Success)
            {
                throw new InvalidOperationException($"Certificate subject does not contain CN field: {subject}");
            }

            var publisher = cnMatch.Groups[1].Value.Trim();

            // Remove any quotes that might be present
            publisher = publisher.Trim('"', '\'');

            return publisher;
        }
        catch (Exception ex) when (!(ex is FileNotFoundException || ex is InvalidOperationException))
        {
            throw new InvalidOperationException($"Failed to extract publisher from certificate: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Validates that the publisher in the certificate matches the publisher in the AppX manifest
    /// </summary>
    /// <param name="certificatePath">Path to the certificate file</param>
    /// <param name="password">Certificate password</param>
    /// <param name="manifestPath">Path to the AppX manifest file</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <exception cref="InvalidOperationException">Publishers don't match or validation failed</exception>
    public static async Task ValidatePublisherMatchAsync(FileInfo certificatePath, string password, FileInfo manifestPath, CancellationToken cancellationToken = default)
    {
        try
        {
            // Extract publisher from certificate
            var certPublisher = ExtractPublisherFromCertificate(certificatePath, password);

            // Extract publisher from manifest
            var manifestIdentity = await MsixService.ParseAppxManifestFromPathAsync(manifestPath, cancellationToken);
            var manifestPublisher = manifestIdentity.Publisher;

            // Normalize both publishers for comparison (remove CN= prefix and quotes)
            var normalizedCertPublisher = ManifestTemplateService.StripCnPrefix(certPublisher);
            var normalizedManifestPublisher = ManifestTemplateService.StripCnPrefix(manifestPublisher);

            // Compare publishers (case-insensitive)
            if (!string.Equals(normalizedCertPublisher, normalizedManifestPublisher, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Error: Publisher in {manifestPath} (CN={normalizedManifestPublisher}) does not match the publisher in the certificate {certificatePath} (CN={normalizedCertPublisher}).");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Failed to validate publisher match: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Infers the publisher name using the specified hierarchy:
    /// 1. If explicit publisher is provided, use that
    /// 2. If manifest path is provided, extract publisher from that manifest
    /// 3. If appxmanifest.xml is found in project (.winapp directory), use that
    /// 4. Use the system default publisher (from SystemDefaultsService.GetDefaultPublisherCN())
    /// </summary>
    private async Task<string> InferPublisherAsync(
        string? explicitPublisher,
        FileInfo? manifestPath,
        string defaultPublisher,
        CancellationToken cancellationToken)
    {
        // 1. If explicit publisher is provided, use that
        if (!string.IsNullOrWhiteSpace(explicitPublisher))
        {
            return explicitPublisher;
        }

        // 2. If manifest path is provided, extract publisher from that manifest
        if (manifestPath != null)
        {
            try
            {
                logger.LogInformation("Certificate publisher inferred from: {ManifestPath}", manifestPath);

                var identityInfo = await MsixService.ParseAppxManifestFromPathAsync(manifestPath, cancellationToken);
                return identityInfo.Publisher;
            }
            catch (Exception ex)
            {
                logger.LogDebug("Could not extract publisher from manifest: {Message}", ex.Message);
            }
        }

        // 3. If appxmanifest.xml is found in the current project, use that
        var projectManifestPath = MsixService.FindProjectManifest(currentDirectoryProvider);
        if (projectManifestPath != null)
        {
            try
            {
                logger.LogInformation("Certificate publisher inferred from: {ProjectManifestPath}", projectManifestPath);

                var identityInfo = await MsixService.ParseAppxManifestFromPathAsync(projectManifestPath, cancellationToken);
                return identityInfo.Publisher;
            }
            catch (Exception ex)
            {
                logger.LogDebug("Could not extract publisher from project manifest: {Message}", ex.Message);
            }
        }

        // 4. Use default publisher
        logger.LogInformation("No manifest found, using default publisher: {DefaultPublisher}", defaultPublisher);
        return defaultPublisher;
    }

    [GeneratedRegex(@"CN=([^,]+)", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex CnFieldRegex();
}
