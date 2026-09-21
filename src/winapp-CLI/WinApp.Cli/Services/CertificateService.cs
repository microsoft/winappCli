// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics.Eventing.Reader;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Tools;

namespace WinApp.Cli.Services;

internal partial class CertificateService(
    IBuildToolsService buildToolsService,
    IGitignoreService gitignoreService,
    ICurrentDirectoryProvider currentDirectoryProvider) : ICertificateService
{
    public const string DefaultCertFileName = "devcert.pfx";

    // Test seams for OS/certificate-store boundaries. Each defaults to the real
    // production implementation; tests inject fakes to exercise success/error paths
    // that require administrator privileges or a matching machine-store certificate.
    internal Func<X509Certificate2, bool> IsCertificateInstalledImpl { get; set; } = DefaultIsCertificateInstalled;
    internal Action<X509Certificate2> AddCertificateToStoreImpl { get; set; } = DefaultAddCertificateToStore;
    internal Func<int, CancellationToken, Task<string?>> ReadAppxPackagingSignErrorAsync { get; set; } = DefaultReadAppxPackagingSignErrorAsync;

    // Key-storage flags used when loading the PFX for the machine-store install. Defaults to the
    // real production combination: MachineKeySet|PersistKeySet persists the private key in the
    // machine key container so the installed certificate stays usable after the process exits.
    // Seamed so unit tests load with EphemeralKeySet (in-memory only) and never leave a persisted
    // key container behind on the host; production always uses the persisting default.
    internal X509KeyStorageFlags InstallKeyStorageFlags { get; set; } =
        X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet;

    public record CertificateResult(
        FileInfo CertificatePath,
        string Password,
        string Publisher,
        string SubjectName,
        bool UpdatedGitignore,
        FileInfo? PublicCertificatePath = null
    );

    public async Task<CertificateResult> GenerateDevCertificateAsync(
        string publisher,
        FileInfo outputPath,
        TaskContext taskContext,
        string password = "password",
        int validDays = 365,
        bool exportCer = false,
        CancellationToken cancellationToken = default)
    {
        // Ensure output directory exists
        outputPath.Directory?.Create();

        // Normalize the publisher to a valid X.500 distinguished name.
        var subjectName = PublisherDnHelper.Normalize(publisher);

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

            taskContext.AddDebugMessage($"Certificate generated: {outputPath}");

            // Export public certificate (.cer) if requested
            FileInfo? publicCertPath = null;
            if (exportCer)
            {
                var cerPath = Path.ChangeExtension(outputPath.FullName, ".cer");
                var cerBytes = cert.Export(X509ContentType.Cert);
                await File.WriteAllBytesAsync(cerPath, cerBytes, cancellationToken);
                publicCertPath = new FileInfo(cerPath);
                taskContext.AddDebugMessage($"Public certificate exported: {cerPath}");
            }

            outputPath.Refresh();

            var publisherDisplay = PublisherDnHelper.GetDisplayName(subjectName);

            return new CertificateResult(
                CertificatePath: outputPath,
                Password: password,
                Publisher: publisherDisplay,
                SubjectName: subjectName,
                UpdatedGitignore: false,
                PublicCertificatePath: publicCertPath
            );
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"Failed to generate development certificate: {error.Message}", error);
        }
    }

    public bool InstallCertificate(FileInfo certPath, string password, bool force, TaskContext taskContext)
    {
        certPath.Refresh();
        if (!certPath.Exists)
        {
            throw new FileNotFoundException($"Certificate file not found: {certPath}");
        }

        taskContext.AddDebugMessage($"Installing development certificate: {certPath}");

        try
        {
            // Check if certificate is already installed (unless force is true)
            if (!force)
            {
                try
                {
                    // Load the certificate to get its thumbprint/subject for comparison
                    using var certToCheck = LoadCertificate(
                        certPath,
                        password,
                        X509KeyStorageFlags.Exportable);

                    // Check if this certificate is already in the TrustedPeople store
                    if (IsCertificateInstalledImpl(certToCheck))
                    {
                        taskContext.AddDebugMessage("Certificate appears to already be installed");
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    // Continue with installation if check fails
                    taskContext.AddDebugMessage($"Could not check existing certificates: {ex.Message}");
                }
            }

            // Install to TrustedPeople store (required for MSIX sideloading).
            // A PFX carries a private key; a public-only .cer (e.g. one produced by
            // `cert generate --export-cer`) is loaded as a certificate-only object. Either is
            // valid to trust — the TrustedPeople store only needs the public certificate. The
            // key-storage flags are seamed so unit tests load a PFX with EphemeralKeySet (no
            // persisted key container); production uses the default MachineKeySet|PersistKeySet
            // so an installed PFX stays usable.
            using var cert = LoadCertificate(
                certPath,
                password,
                InstallKeyStorageFlags);

            // Install to LocalMachine\TrustedPeople store (requires elevation)
            try
            {
                AddCertificateToStoreImpl(cert);
            }
            catch (CryptographicException ex) when (ex.Message.Contains("Access is denied"))
            {
                throw new InvalidOperationException(
                    "Failed to install certificate: Administrator privileges are required to install certificates to the LocalMachine store. " +
                    "Please run this command as an administrator.", ex);
            }

            taskContext.AddDebugMessage("Certificate installed successfully to TrustedPeople store");

            return true;
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"Failed to install development certificate: {error.Message}", error);
        }
    }

    /// <summary>
    /// Loads a certificate from either a PKCS#12 (.pfx) file or a public-only DER/PEM (.cer) file.
    /// The format is detected up front with <see cref="X509Certificate2.GetCertContentType(string)"/>
    /// — which classifies PFX and certificate files without needing the password — so a PKCS#12
    /// file always loads through the PFX path. A public-only .cer (e.g. one produced by
    /// `cert generate --export-cer`) loads as a certificate-only object. Detecting rather than
    /// catch-and-fallback keeps a genuine PFX error (such as a wrong password) as the error the
    /// user sees instead of masking it with a certificate-decoding failure. <paramref name="pfxKeyStorageFlags"/>
    /// applies only to the PFX path; a .cer carries no private key, so the flags and password are
    /// ignored for it.
    /// </summary>
    internal static X509Certificate2 LoadCertificate(
        FileInfo certPath,
        string password,
        X509KeyStorageFlags pfxKeyStorageFlags)
    {
        return X509Certificate2.GetCertContentType(certPath.FullName) == X509ContentType.Pfx
            ? X509CertificateLoader.LoadPkcs12FromFile(certPath.FullName, password, pfxKeyStorageFlags)
            : X509CertificateLoader.LoadCertificateFromFile(certPath.FullName);
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
    public async Task SignFileAsync(FileInfo filePath, FileInfo certificatePath, TaskContext taskContext, string? password = "password", string? timestampUrl = null, CancellationToken cancellationToken = default)
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

        var tokens = new List<string>
        {
            "sign",
            "/f", certificatePath.FullName,
            "/p", password ?? "password",
            "/fd", "SHA256",
        };

        if (!string.IsNullOrWhiteSpace(timestampUrl))
        {
            // The timestamp URL can originate from a project's AppxPackageSigningTimestampServerUrl, so
            // validate it as an absolute http/https URL before it reaches signtool. JoinArguments below then
            // encodes every token so an embedded quote in any value cannot inject an extra signtool switch.
            if (!Uri.TryCreate(timestampUrl, UriKind.Absolute, out var timestampUri)
                || (timestampUri.Scheme != Uri.UriSchemeHttp && timestampUri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException(
                    $"Invalid timestamp server URL '{timestampUrl}'. It must be an absolute http or https URL.");
            }

            tokens.Add("/tr");
            tokens.Add(timestampUrl);
            tokens.Add("/td");
            tokens.Add("SHA256");
        }

        tokens.Add(filePath.FullName);

        var arguments = WindowsCommandLine.JoinArguments(tokens) ?? string.Empty;

        taskContext.AddDebugMessage($"Signing file: {filePath}");

        try
        {
            await buildToolsService.RunBuildToolAsync(new GenericTool("signtool.exe"), arguments, taskContext, cancellationToken: cancellationToken);

            taskContext.AddDebugMessage("File signed successfully");
        }
        catch (BuildToolsService.InvalidBuildToolException ex)
            when (ex.Stdout.Contains("0x800"))
        {
            var description = await ReadAppxPackagingSignErrorAsync(ex.ProcessId, cancellationToken);

            if (description != null)
            {
                // Keep raw error code in verbose mode; simplify for non-verbose output.
                if (!taskContext.IsVerboseEnabled)
                {
                    description = EventLogHexErrorRegex().Replace(description, "");
                }

                throw new InvalidOperationException($"Failed to sign file: {description}", ex);
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
    /// <param name="taskContext">Task context for status messages and prompts</param>
    /// <param name="explicitPublisher">Explicit publisher to use (optional)</param>
    /// <param name="manifestPath">Specific manifest path to extract publisher from (optional)</param>
    /// <param name="password">Certificate password</param>
    /// <param name="validDays">Certificate validity period</param>
    /// <param name="updateGitignore">Whether to update .gitignore</param>
    /// <param name="install">Whether to install the certificate after generation</param>
    /// <param name="exportCer">Whether to export a .cer file (public key only) alongside the .pfx</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Certificate generation result, or null if skipped</returns>
    public async Task<CertificateResult> GenerateDevCertificateWithInferenceAsync(
        FileInfo outputPath,
        TaskContext taskContext,
        string? explicitPublisher = null,
        FileInfo? manifestPath = null,
        string password = "password",
        int validDays = 365,
        bool updateGitignore = true,
        bool install = false,
        bool exportCer = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Check if certificate already exists
            outputPath.Refresh();
            if (outputPath.Exists)
            {
                taskContext.AddDebugMessage($"{UiSymbols.Check} Development certificate already exists: {outputPath}");
            }

            // Start generation message
            taskContext.AddStatusMessage($"{UiSymbols.Info} Generating development certificate...");

            // Get default publisher from system defaults
            var defaultPublisher = SystemDefaultsHelper.GetDefaultPublisherCN();

            // Infer publisher using the specified hierarchy
            string publisher = await InferPublisherAsync(explicitPublisher, manifestPath, defaultPublisher, taskContext, cancellationToken);

            taskContext.AddStatusMessage($"Certificate publisher: {publisher}");

            // Generate the certificate
            var result = await GenerateDevCertificateAsync(
                publisher,
                outputPath,
                taskContext,
                password,
                validDays,
                exportCer,
                cancellationToken);

            // Success message
            taskContext.AddStatusMessage($"{UiSymbols.Check} Development certificate generated → {result.CertificatePath}");

            if (result.PublicCertificatePath is not null)
            {
                taskContext.AddStatusMessage($"{UiSymbols.Check} Public certificate exported → {result.PublicCertificatePath}");
            }

            // Add certificate to .gitignore
            if (updateGitignore)
            {
                var baseDirectory = outputPath.Directory ?? new DirectoryInfo(currentDirectoryProvider.GetCurrentDirectory());
                var certFileName = result.CertificatePath.Name;

                result = result with
                {
                    UpdatedGitignore = await gitignoreService.AddCertificateToGitignoreAsync(baseDirectory, certFileName, taskContext, cancellationToken)
                };
            }

            if (password == "password")
            {
                taskContext.AddStatusMessage(
                    $"{UiSymbols.Warning} Protected with the default password ('password'), which is public. " +
                    "Treat this certificate as development-only: anyone who obtains the .pfx can sign as you. " +
                    "Pass --password to choose your own, and use a CA-issued certificate or Azure Trusted Signing to ship.");
            }

            // Install certificate if requested
            if (install)
            {
                taskContext.AddDebugMessage("Installing certificate...");

                var installResult = InstallCertificate(result.CertificatePath, password, false, taskContext);
                if (installResult)
                {
                    taskContext.AddStatusMessage($"{UiSymbols.Check} Certificate installed successfully!");
                }
                else
                {
                    taskContext.AddStatusMessage($"{UiSymbols.Info} Certificate was already installed");
                }
            }
            else
            {
                taskContext.AddStatusMessage($"{UiSymbols.Note} Use 'winapp cert install' to install the certificate for development");
            }

            return result;
        }
        catch (Exception ex)
        {
            taskContext.StatusError($"{UiSymbols.Error} Failed to generate development certificate: {ex.Message}");
            taskContext.AddDebugMessage("Certificate generation failed with exception: " + ex.ToString());
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

            // Return the full subject DN (e.g., "CN=Publisher, L=City, S=State, C=Country")
            return subject;
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
            // Extract full subject DN from certificate
            var certPublisher = ExtractPublisherFromCertificate(certificatePath, password);

            // Extract publisher from manifest
            var manifestIdentity = await MsixService.ParseAppxManifestFromPathAsync(manifestPath, cancellationToken);
            var manifestPublisher = manifestIdentity.Publisher;

            // Compare as X.500 distinguished names for semantic equality
            // This handles differences in spacing, ordering normalization, etc.
            var certDn = new X500DistinguishedName(certPublisher);
            var manifestDn = new X500DistinguishedName(manifestPublisher);

            if (!certDn.RawData.AsSpan().SequenceEqual(manifestDn.RawData.AsSpan()))
            {
                throw new InvalidOperationException(
                    $"Publisher in {manifestPath} ({manifestPublisher}) does not match the publisher in the certificate {certificatePath} ({certPublisher}). " +
                    $"Regenerate the certificate with 'winapp cert generate --manifest \"{manifestPath.FullName}\"' or update the manifest Identity Publisher to match the certificate subject.");
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
    /// 3. If a project manifest is found by searching the current directory and parent directories (preferring Package.appxmanifest, then appxmanifest.xml), use that
    /// 4. Use the system default publisher (from SystemDefaultsService.GetDefaultPublisherCN())
    /// </summary>
    private async Task<string> InferPublisherAsync(
        string? explicitPublisher,
        FileInfo? manifestPath,
        string defaultPublisher,
        TaskContext taskContext,
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
                taskContext.AddStatusMessage($"Certificate publisher inferred from: {manifestPath}");

                var identityInfo = await MsixService.ParseAppxManifestFromPathAsync(manifestPath, cancellationToken);
                return identityInfo.Publisher;
            }
            catch (Exception ex)
            {
                taskContext.AddDebugMessage($"Could not extract publisher from manifest: {ex.Message}");
            }
        }

        // 3. If Package.appxmanifest is found in the current project, use that
        var projectManifestPath = MsixService.FindProjectManifest(currentDirectoryProvider);
        if (projectManifestPath != null)
        {
            try
            {
                taskContext.AddStatusMessage($"Certificate publisher inferred from: {projectManifestPath}");

                var identityInfo = await MsixService.ParseAppxManifestFromPathAsync(projectManifestPath, cancellationToken);
                return identityInfo.Publisher;
            }
            catch (Exception ex)
            {
                taskContext.AddDebugMessage($"Could not extract publisher from project manifest: {ex.Message}");
            }
        }

        // 4. Use default publisher
        taskContext.AddStatusMessage($"No manifest found, using default publisher: {defaultPublisher}");
        return defaultPublisher;
    }

    /// <summary>
    /// Default production implementation of the "is this certificate already installed" check.
    /// Opens the LocalMachine TrustedPeople store read-only and looks for a matching thumbprint.
    /// </summary>
    private static bool DefaultIsCertificateInstalled(X509Certificate2 certToCheck)
    {
        using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);

        var existingCerts = store.Certificates.Find(
            X509FindType.FindByThumbprint,
            certToCheck.Thumbprint,
            validOnly: false);

        return existingCerts.Count > 0;
    }

    /// <summary>
    /// Default production implementation that installs a certificate into the LocalMachine
    /// TrustedPeople store. Requires administrator privileges.
    /// </summary>
    private static void DefaultAddCertificateToStore(X509Certificate2 cert)
    {
        using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        store.Add(cert);
    }

    /// <summary>
    /// Default production implementation that polls the AppxPackaging operational event log for a
    /// signing error emitted by the given signtool process, returning its formatted description
    /// (or null if none is found within the timeout).
    /// </summary>
    private static async Task<string?> DefaultReadAppxPackagingSignErrorAsync(int processId, CancellationToken cancellationToken)
    {
        var query = new EventLogQuery(
            "Microsoft-Windows-AppxPackaging/Operational",
            PathType.LogName,
            $"*[System[Level=2 and Execution[@ProcessID={processId}]]]");

        var timeout = TimeSpan.FromSeconds(5);
        var pollingInterval = TimeSpan.FromMilliseconds(500);
        var startTime = DateTime.UtcNow;

        while ((DateTime.UtcNow - startTime) < timeout && !cancellationToken.IsCancellationRequested)
        {
            using var reader = new EventLogReader(query);
            var record = reader.ReadEvent();

            if (record != null)
            {
                return record.FormatDescription() ?? string.Empty;
            }

            await Task.Delay(pollingInterval, cancellationToken);
        }

        return null;
    }

    [GeneratedRegex(@"^error\s+0x[0-9A-Fa-f]+:\s*", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex EventLogHexErrorRegex();
}
