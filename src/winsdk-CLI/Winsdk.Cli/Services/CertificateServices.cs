namespace Winsdk.Cli.Services;

internal class CertificateServices
{
    private readonly BuildToolsService _buildToolsService;
    private readonly PowerShellService _powerShellService;

    public const string DefaultCertFileName = "devcert.pfx";

    public CertificateServices(BuildToolsService buildToolsService)
    {
        _buildToolsService = buildToolsService;
        _powerShellService = new PowerShellService();
    }
    public record CertificateResult(
        string CertificatePath,
        string Password,
        string Publisher,
        string SubjectName
    );

    public async Task<CertificateResult> GenerateDevCertificateAsync(
        string publisher,
        string outputPath,
        string password = "password",
        int validDays = 365,
        bool verbose = true,
        CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathRooted(outputPath))
        {
            outputPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), outputPath));
        }
        // Ensure output directory exists
        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        // Clean up the publisher name to ensure proper CN format
        // Remove any existing CN= prefix and clean up quotes
        var cleanPublisher = publisher.Replace("CN=", "").Replace("\"", "").Replace("'", "");

        // Ensure we have a proper CN format
        var subjectName = $"CN={cleanPublisher}";

        var command = $"New-SelfSignedCertificate -Type Custom -Subject '{subjectName}' -KeyUsage DigitalSignature -FriendlyName 'MSIX Dev Certificate' -CertStoreLocation 'Cert:\\CurrentUser\\My' -TextExtension @('2.5.29.37={{text}}1.3.6.1.5.5.7.3.3', '2.5.29.19={{text}}') -NotAfter (Get-Date).AddDays({validDays}) | Export-PfxCertificate -FilePath '{outputPath}' -Password (ConvertTo-SecureString -String '{password}' -Force -AsPlainText)";

        if (verbose)
        {
            Console.WriteLine($"Generating development certificate for publisher: {cleanPublisher}");
            Console.WriteLine($"Certificate subject: {subjectName}");
        }

        try
        {
            var (exitCode, _) = await _powerShellService.RunCommandAsync(command, verbose: verbose, cancellationToken: cancellationToken);

            if (exitCode != 0)
            {
                throw new InvalidOperationException($"PowerShell command failed with exit code {exitCode}");
            }

            if (verbose)
            {
                Console.WriteLine($"Certificate generated: {outputPath}");
            }

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

    internal async Task<bool> InstallCertificateAsync(string certPath, string password, bool force, bool verbose, CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathRooted(certPath))
        {
            certPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), certPath));
        }

        if (!File.Exists(certPath))
        {
            throw new FileNotFoundException($"Certificate file not found: {certPath}");
        }

        if (verbose)
        {
            Console.WriteLine($"Installing development certificate: {certPath}");
        }

        try
        {
            // Check if certificate is already installed (unless force is true)
            if (!force)
            {
                var certName = Path.GetFileNameWithoutExtension(certPath);
                var checkCommand = $"Get-ChildItem -Path 'Cert:\\LocalMachine\\TrustedPeople' | Where-Object {{ $_.Subject -like '*{certName}*' }}";

                try
                {
                    var (_, result) = await _powerShellService.RunCommandAsync(checkCommand, verbose: false, cancellationToken: cancellationToken);

                    if (!string.IsNullOrWhiteSpace(result))
                    {
                        if (verbose)
                        {
                            Console.WriteLine("Certificate appears to already be installed");
                        }
                        return false;
                    }
                }
                catch
                {
                    // Continue with installation if check fails
                }
            }

            // Install to TrustedPeople store (required for MSIX sideloading)
            // Create the PowerShell command directly
            var absoluteCertPath = Path.GetFullPath(certPath);
            var installCommand = $"Import-PfxCertificate -FilePath '{absoluteCertPath}' -CertStoreLocation 'Cert:\\LocalMachine\\TrustedPeople' -Password (ConvertTo-SecureString -String '{password}' -Force -AsPlainText)";

            await _powerShellService.RunCommandAsync(installCommand, elevated: true, verbose: verbose, cancellationToken: cancellationToken);

            if (verbose)
            {
                Console.WriteLine("Certificate installed successfully to TrustedPeople store");
            }

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
    /// <param name="verbose">Enable verbose logging</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task SignFileAsync(string filePath, string certificatePath, string? password = "password", string? timestampUrl = null, bool verbose = true, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"File not found: {filePath}");

        if (!File.Exists(certificatePath))
            throw new FileNotFoundException($"Certificate file not found: {certificatePath}");

        var arguments = $@"sign /f ""{certificatePath}"" /p ""{password}"" /fd SHA256";

        if (!string.IsNullOrWhiteSpace(timestampUrl))
        {
            arguments += $@" /tr ""{timestampUrl}"" /td SHA256";
        }

        arguments += $@" ""{filePath}""";

        if (verbose)
        {
            Console.WriteLine($"Signing file: {filePath}");
        }

        try
        {
            await _buildToolsService.RunBuildToolAsync("signtool.exe", arguments, verbose, cancellationToken: cancellationToken);

            if (verbose)
            {
                Console.WriteLine("File signed successfully");
            }
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
    /// <param name="quiet">Suppress most console output (errors and final results still shown)</param>
    /// <param name="verbose">Enable verbose output</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Certificate generation result, or null if skipped</returns>
    public async Task<CertificateResult?> GenerateDevCertificateWithInferenceAsync(
        string outputPath,
        string? explicitPublisher = null,
        string? manifestPath = null,
        string password = "password",
        int validDays = 365,
        bool skipIfExists = true,
        bool updateGitignore = true,
        bool install = false,
        bool quiet = false,
        bool verbose = true,
        CancellationToken cancellationToken = default)
    {
        var msixService = new MsixService(_buildToolsService);

        try
        {
            // Skip if certificate already exists and skipIfExists is true
            if (skipIfExists && File.Exists(outputPath))
            {
                if (!quiet)
                {
                    Console.WriteLine($"{UiSymbols.Note} Development certificate already exists: {outputPath}");
                }
                return null;
            }

            // Start generation message
            if (!quiet)
            {
                Console.WriteLine($"{UiSymbols.Gear} Generating development certificate...");
            }

            // Get default publisher from system defaults
            var systemDefaults = new SystemDefaultsService();
            var defaultPublisher = systemDefaults.GetDefaultPublisherCN();

            // Infer publisher using the specified hierarchy
            string publisher = await InferPublisherAsync(explicitPublisher, manifestPath, defaultPublisher, msixService, verbose, cancellationToken);

            if (verbose)
            {
                Console.WriteLine($"Generating development certificate for publisher: {publisher}");
            }

            // Generate the certificate
            var result = await GenerateDevCertificateAsync(
                publisher, 
                outputPath, 
                password, 
                validDays, 
                verbose, 
                cancellationToken);

            // Success message
            Console.WriteLine($"{UiSymbols.Check} Development certificate generated → {result.CertificatePath}");

            // Add certificate to .gitignore
            if (updateGitignore)
            {
                var baseDirectory = Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory();
                var certFileName = Path.GetFileName(result.CertificatePath);
                GitignoreService.AddCertificateToGitignore(baseDirectory, certFileName, verbose);
            }

            // Display password information
            if (!quiet)
            {
                Console.WriteLine($"{UiSymbols.Note} Certificate password: `{password}`");
            }

            // Install certificate if requested
            if (install)
            {
                if (verbose)
                {
                    Console.WriteLine("Installing certificate...");
                }
                
                var installResult = await InstallCertificateAsync(result.CertificatePath, password, false, verbose, cancellationToken);
                if (installResult)
                {
                    Console.WriteLine("✅ Certificate installed successfully!");
                }
                else
                {
                    Console.WriteLine("ℹ️ Certificate was already installed");
                }
            }
            else if (!quiet)
            {
                Console.WriteLine($"{UiSymbols.Note} Use 'winsdk cert install' to install the certificate for development");
            }

            return result;
        }
        catch (Exception ex)
        {
            if (verbose)
            {
                Console.WriteLine($"{UiSymbols.Note} Failed to generate development certificate: {ex.Message}");
            }
            else if (!quiet)
            {
                Console.WriteLine($"{UiSymbols.Note} Failed to generate development certificate (use --verbose for details)");
            }
            throw; // Re-throw for callers that want to handle the error differently
        }
    }

    /// <summary>
    /// Infers the publisher name using the specified hierarchy:
    /// 1. If explicit publisher is provided, use that
    /// 2. If manifest path is provided, extract publisher from that manifest
    /// 3. If appxmanifest.xml is found in project (.winsdk directory), use that
    /// 4. Use the system default publisher (from SystemDefaultsService.GetDefaultPublisherCN())
    /// </summary>
    private async Task<string> InferPublisherAsync(
        string? explicitPublisher, 
        string? manifestPath, 
        string defaultPublisher,
        MsixService msixService,
        bool verbose, 
        CancellationToken cancellationToken)
    {
        // 1. If explicit publisher is provided, use that
        if (!string.IsNullOrWhiteSpace(explicitPublisher))
        {
            if (verbose)
            {
                Console.WriteLine($"Using explicit publisher: {explicitPublisher}");
            }
            return explicitPublisher;
        }

        // 2. If manifest path is provided, extract publisher from that manifest
        if (!string.IsNullOrWhiteSpace(manifestPath))
        {
            try
            {
                if (verbose)
                {
                    Console.WriteLine($"Extracting publisher from manifest: {manifestPath}");
                }
                
                var identityInfo = await msixService.ParseAppxManifestAsync(manifestPath, cancellationToken);
                return identityInfo.Publisher;
            }
            catch (Exception ex)
            {
                if (verbose)
                {
                    Console.WriteLine($"Could not extract publisher from manifest: {ex.Message}");
                }
            }
        }

        // 3. If appxmanifest.xml is found in the current project, use that
        var projectManifestPath = MsixService.FindProjectManifest();
        if (projectManifestPath != null)
        {
            try
            {
                if (verbose)
                {
                    Console.WriteLine($"Found project manifest: {projectManifestPath}");
                }
                
                var identityInfo = await msixService.ParseAppxManifestAsync(projectManifestPath, cancellationToken);
                return identityInfo.Publisher;
            }
            catch (Exception ex)
            {
                if (verbose)
                {
                    Console.WriteLine($"Could not extract publisher from project manifest: {ex.Message}");
                }
            }
        }

        // 4. Use default publisher
        if (verbose)
        {
            Console.WriteLine($"No manifest found, using default publisher: {defaultPublisher}");
        }
        return defaultPublisher;
    }
}
