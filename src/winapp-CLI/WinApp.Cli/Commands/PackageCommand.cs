// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;
using WinApp.Cli.Telemetry.Events;

namespace WinApp.Cli.Commands;

internal class PackageCommand : Command, IShortDescription
{
    public string ShortDescription => "Create MSIX package or bundle";

    public static Argument<DirectoryInfo[]> InputFolderArgument { get; }
    public static Option<FileInfo> OutputOption { get; }
    public static Option<string?> NameOption { get; }
    public static Option<bool> SkipPriOption { get; }
    public static Option<FileInfo> CertOption { get; }
    public static Option<string> CertPasswordOption { get; }
    public static Option<bool> GenerateCertOption { get; }
    public static Option<bool> InstallCertOption { get; }
    public static Option<string?> PublisherOption { get; }
    public static Option<FileInfo> ManifestOption { get; }
    public static Option<bool> SelfContainedOption { get; }
    public static Option<string?> ExecutableOption { get; }

    static PackageCommand()
    {
        InputFolderArgument = new Argument<DirectoryInfo[]>("input-folder")
        {
            Description = "One or more input folders with package layout, or a single sparse appxmanifest.xml file (an identity-only package with AllowExternalContent). Pass multiple folders to create an MSIX bundle (e.g., winapp pack ./publish/x64 ./publish/arm64).",
            Arity = ArgumentArity.OneOrMore
        };
        OutputOption = new Option<FileInfo>("--output")
        {
            Description = "Output file name for the generated package (.msix) or bundle (.msixbundle). Defaults to <name>_<version>_<arch>.msix for single packages, or <name>_<version>_<arch1>_<arch2>.msixbundle for bundles.",
        };

        NameOption = new Option<string?>("--name")
        {
            Description = "Package name (default: from manifest)"
        };
        SkipPriOption = new Option<bool>("--skip-pri")
        {
            Description = "Skip PRI file generation"
        };
        CertOption = new Option<FileInfo>("--cert")
        {
            Description = "Path to signing certificate (will auto-sign if provided)"
        };
        CertOption.AcceptExistingOnly();
        CertPasswordOption = new Option<string>("--cert-password")
        {
            Description = "Certificate password (default: password)",
            DefaultValueFactory = (argumentResult) => "password"
        };
        GenerateCertOption = new Option<bool>("--generate-cert")
        {
            Description = "Generate a new development certificate"
        };
        InstallCertOption = new Option<bool>("--install-cert")
        {
            Description = "Install certificate to machine"
        };
        PublisherOption = new Option<string?>("--publisher")
        {
            Description = "Publisher distinguished name (DN) for certificate generation (e.g., CN=MyCompany). Bare names are auto-wrapped as CN=<name>."
        };
        ManifestOption = new Option<FileInfo>("--manifest")
        {
            Description = "Path to AppX manifest file (default: auto-detect from input folder or current directory)"
        };
        ManifestOption.AcceptExistingOnly();
        SelfContainedOption = new Option<bool>("--self-contained")
        {
            Description = "Bundle Windows App SDK runtime for self-contained deployment"
        };
        ExecutableOption = new Option<string?>("--executable")
        {
            Description = "Path to the executable relative to the input folder."
        };
        ExecutableOption.Aliases.Add("--exe");
    }

    public PackageCommand()
        : base("package", "Create MSIX installer from your built app. Run after building your app. A manifest (Package.appxmanifest or appxmanifest.xml) is required for packaging - it must be in current working directory, passed as --manifest or be in the input folder. Use --cert devcert.pfx to sign for testing. Example: winapp package ./dist --manifest Package.appxmanifest --cert ./devcert.pfx")
    {
        Aliases.Add("pack");
        Arguments.Add(InputFolderArgument);
        Options.Add(OutputOption);
        Options.Add(NameOption);
        Options.Add(SkipPriOption);
        Options.Add(CertOption);
        Options.Add(CertPasswordOption);
        Options.Add(GenerateCertOption);
        Options.Add(InstallCertOption);
        Options.Add(PublisherOption);
        Options.Add(ManifestOption);
        Options.Add(SelfContainedOption);
        Options.Add(ExecutableOption);
    }

    public class Handler(
        IMsixService msixService,
        IStatusService statusService,
        IProjectContextDetector projectContextDetector) : AsynchronousCommandLineAction
    {
        /// <summary>
        /// Heuristic for whether a non-existent input path was intended as a manifest file
        /// (rather than an input folder), so a missing path can be reported with the right error.
        /// </summary>
        private static bool LooksLikeManifestPath(string name)
            => name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".appxmanifest", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Classifies a manifest-file input into sparse / non-sparse / unreadable so the caller can
        /// report a malformed or inaccessible manifest distinctly from a valid non-sparse one
        /// (rather than mislabelling a parse failure as "missing AllowExternalContent").
        /// </summary>
        private enum ManifestInputKind { NotManifestName, Sparse, NotSparse, Unreadable }

        private static async Task<(ManifestInputKind Kind, string? Error)> ClassifyManifestInputAsync(FileInfo file, CancellationToken cancellationToken)
        {
            var name = file.Name;
            var isManifestName = name.EndsWith(".appxmanifest", StringComparison.OrdinalIgnoreCase)
                || name.Equals("appxmanifest.xml", StringComparison.OrdinalIgnoreCase);
            if (!isManifestName)
            {
                return (ManifestInputKind.NotManifestName, null);
            }

            string content;
            try
            {
                content = await File.ReadAllTextAsync(file.FullName, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (ManifestInputKind.Unreadable, ex.Message);
            }

            return MsixService.ClassifySparseManifest(content, out var parseError) switch
            {
                MsixService.SparseManifestKind.Sparse => (ManifestInputKind.Sparse, null),
                MsixService.SparseManifestKind.ParseError => (ManifestInputKind.Unreadable, parseError),
                _ => (ManifestInputKind.NotSparse, null),
            };
        }

        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var inputFolders = parseResult.GetRequiredValue(InputFolderArgument);
            var output = parseResult.GetValue(OutputOption);
            var name = parseResult.GetValue(NameOption);
            var skipPri = parseResult.GetValue(SkipPriOption);
            var certPath = parseResult.GetValue(CertOption);
            var certPassword = parseResult.GetRequiredValue(CertPasswordOption);
            var generateCert = parseResult.GetValue(GenerateCertOption);
            var installCert = parseResult.GetValue(InstallCertOption);
            var publisher = parseResult.GetValue(PublisherOption);
            var manifestPath = parseResult.GetValue(ManifestOption);
            var selfContained = parseResult.GetValue(SelfContainedOption);
            var executable = parseResult.GetValue(ExecutableOption);

            FileInfo? candidateManifest = null;
            var manifestKind = ManifestInputKind.NotManifestName;
            string? manifestError = null;
            if (inputFolders.Length == 1 && File.Exists(inputFolders[0].FullName))
            {
                candidateManifest = new FileInfo(inputFolders[0].FullName);
                (manifestKind, manifestError) = await ClassifyManifestInputAsync(candidateManifest, cancellationToken);
            }

            var contextDirectories = inputFolders
                .Select(input => File.Exists(input.FullName)
                    ? new FileInfo(input.FullName).Directory
                    : input)
                .Where(directory => directory is not null)
                .Cast<DirectoryInfo>()
                .ToList();
            if (manifestPath?.Directory is not null)
            {
                contextDirectories.Insert(0, manifestPath.Directory);
            }

            ProjectContextEvent.Log(
                "package",
                () => projectContextDetector.DetectDirectories(
                        contextDirectories,
                        candidateManifest is not null ? ProjectTargetKind.Manifest : ProjectTargetKind.BuildOutput) with
                {
                    Packaging = manifestKind == ManifestInputKind.Sparse
                        ? ProjectContextPackaging.Sparse
                        : ProjectContextPackaging.Packaged,
                });

            // Sparse identity packaging: when a single manifest FILE is passed (instead of a
            // folder) and it declares AllowExternalContent, build an identity-only .msix from
            // just the manifest — no input folder or app binaries required.
            if (candidateManifest is not null)
            {
                if (manifestKind == ManifestInputKind.Sparse)
                {
                    // Identity-only packaging builds the .msix from just the manifest, so options that
                    // describe an app payload have no effect here. Reject them rather than silently
                    // discarding a scripted override.
                    var inapplicable = new[]
                    {
                        (parseResult.GetResult(NameOption), "--name"),
                        (parseResult.GetResult(ManifestOption), "--manifest"),
                        (parseResult.GetResult(SkipPriOption), "--skip-pri"),
                        (parseResult.GetResult(SelfContainedOption), "--self-contained"),
                        (parseResult.GetResult(ExecutableOption), "--executable"),
                    }
                    .Where(o => o.Item1 is { Implicit: false })
                    .Select(o => o.Item2)
                    .ToList();

                    if (inapplicable.Count > 0)
                    {
                        var optionList = string.Join(", ", inapplicable);
                        return await statusService.ExecuteWithStatusAsync("Validating input...", (taskContext, _) =>
                        {
                            return Task.FromResult((1, $"{UiSymbols.Error} The following option(s) do not apply to sparse identity packaging (manifest-file input): {optionList}. Remove them, or pass an input folder to build a full MSIX."));
                        }, cancellationToken);
                    }

                    return await statusService.ExecuteWithStatusAsync("Creating sparse identity package...", async (taskContext, ct) =>
                    {
                        try
                        {
                            var autoSign = certPath != null || generateCert;
                            var result = await msixService.CreateSparseIdentityPackageAsync(candidateManifest, output, taskContext, autoSign, certPath, certPassword, generateCert, installCert, publisher, ct);

                            taskContext.AddStatusMessage($"{UiSymbols.Package} Identity package: {result.MsixPath}");
                            if (result.Signed)
                            {
                                taskContext.AddStatusMessage($"{UiSymbols.Lock} Package has been signed");
                            }
                            else
                            {
                                taskContext.AddStatusMessage($"{UiSymbols.Warning} Package is unsigned. Windows requires sparse identity packages to be signed before they can be registered — sign it (e.g. pass --generate-cert or --cert <pfx>) and trust the certificate first, otherwise Add-AppxPackage will fail.");
                            }
                            taskContext.AddStatusMessage($"{UiSymbols.Info} Next: winapp embed-identity <exe> — then register in your installer with Add-AppxPackage -Path <msix> -ExternalLocation <install-dir>");

                            return (0, "Sparse identity package creation completed.");
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            taskContext.AddDebugMessage($"Stack Trace: {ex.StackTrace}");
                            return (1, $"{UiSymbols.Error} Failed to create sparse identity package: {ex.GetBaseException().Message}");
                        }
                    }, cancellationToken);
                }

                if (manifestKind == ManifestInputKind.Unreadable)
                {
                    return await statusService.ExecuteWithStatusAsync("Validating input...", (taskContext, _) =>
                    {
                        return Task.FromResult((1, $"{UiSymbols.Error} Manifest file could not be read as valid XML: {manifestError} Fix the manifest, or regenerate it with 'winapp init --exe <exe> --sparse'."));
                    }, cancellationToken);
                }

                return await statusService.ExecuteWithStatusAsync("Validating input...", (taskContext, _) =>
                {
                    return Task.FromResult((1, $"{UiSymbols.Error} Input is a file but not a sparse manifest (missing uap10:AllowExternalContent). Pass an input folder, or generate a sparse manifest with 'winapp init --exe <exe> --sparse'."));
                }, cancellationToken);
            }

            // A path that looks like a manifest file (ends in .xml/.appxmanifest) but doesn't
            // exist should be reported as a missing manifest file — not a missing input folder —
            // since it can never enter the sparse-manifest branch above. Include the init hint.
            var missingManifestFiles = inputFolders
                .Where(d => !d.Exists && LooksLikeManifestPath(d.Name))
                .ToList();
            if (missingManifestFiles.Count > 0)
            {
                var manifestPaths = string.Join(Environment.NewLine, missingManifestFiles.Select(d => $"  {d.FullName}"));
                return await statusService.ExecuteWithStatusAsync("Validating input...", (taskContext, _) =>
                {
                    return Task.FromResult((1, $"{UiSymbols.Error} Manifest file not found:{Environment.NewLine}{manifestPaths}{Environment.NewLine}Generate a sparse manifest with 'winapp init --exe <exe> --sparse'."));
                }, cancellationToken);
            }

            // Validate all input folders exist (report all missing at once)
            var missingDirs = inputFolders.Where(d => !d.Exists).ToList();
            if (missingDirs.Count > 0)
            {
                var missingPaths = string.Join(Environment.NewLine, missingDirs.Select(d => $"  {d.FullName}"));
                return await statusService.ExecuteWithStatusAsync("Validating input...", (taskContext, _) =>
                {
                    return Task.FromResult((1, $"{UiSymbols.Error} Input folder(s) not found:{Environment.NewLine}{missingPaths}"));
                }, cancellationToken);
            }

            // Reject duplicate paths (normalize and compare)
            var normalizedPaths = inputFolders.Select(d => Path.GetFullPath(d.FullName).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).ToList();
            var duplicates = normalizedPaths.GroupBy(p => p, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (duplicates.Count > 0)
            {
                var dupPaths = string.Join(Environment.NewLine, duplicates.Select(d => $"  {d}"));
                return await statusService.ExecuteWithStatusAsync("Validating input...", (taskContext, _) =>
                {
                    return Task.FromResult((1, $"{UiSymbols.Error} Duplicate input folder(s):{Environment.NewLine}{dupPaths}"));
                }, cancellationToken);
            }

            // Validate --output extension for bundle mode
            if (inputFolders.Length > 1 && output != null)
            {
                var ext = Path.GetExtension(output.Name);
                if (string.Equals(ext, ".msix", StringComparison.OrdinalIgnoreCase))
                {
                    return await statusService.ExecuteWithStatusAsync("Validating input...", (taskContext, _) =>
                    {
                        return Task.FromResult((1, $"{UiSymbols.Error} Cannot use .msix extension for --output when creating a bundle from multiple folders. Use .msixbundle or omit the extension."));
                    }, cancellationToken);
                }
            }

            // Validate --output extension for single-package mode
            if (inputFolders.Length == 1 && output != null)
            {
                var ext = Path.GetExtension(output.Name);
                if (string.Equals(ext, ".msixbundle", StringComparison.OrdinalIgnoreCase))
                {
                    return await statusService.ExecuteWithStatusAsync("Validating input...", (taskContext, _) =>
                    {
                        return Task.FromResult((1, $"{UiSymbols.Error} Cannot use .msixbundle extension for --output when creating a single package. Use .msix or omit the extension."));
                    }, cancellationToken);
                }
            }

            if (inputFolders.Length == 1)
            {
                // Single folder: existing behavior unchanged
                var inputFolder = inputFolders[0];
                return await statusService.ExecuteWithStatusAsync("Creating MSIX package...", async (taskContext, cancellationToken) =>
                {
                    try
                    {
                        var autoSign = certPath != null || generateCert;

                        var result = await msixService.CreateMsixPackageAsync(inputFolder, output, taskContext, name, skipPri, autoSign, certPath, certPassword, generateCert, installCert, publisher, manifestPath, selfContained, executable, cancellationToken);

                        taskContext.AddStatusMessage($"{UiSymbols.Package} Package: {result.MsixPath}");
                        if (result.Signed)
                        {
                            taskContext.AddStatusMessage($"{UiSymbols.Lock} Package has been signed");
                        }

                        return (0, "MSIX package creation completed.");
                    }
                    catch (Exception ex)
                    {
                        taskContext.AddDebugMessage($"Stack Trace: {ex.StackTrace}");
                        return (1, $"{UiSymbols.Error} Failed to create MSIX package: {ex.GetBaseException().Message}");
                    }
                }, cancellationToken);
            }
            else
            {
                // Multiple folders: create MSIX bundle
                return await statusService.ExecuteWithStatusAsync("Creating MSIX bundle...", async (taskContext, cancellationToken) =>
                {
                    try
                    {
                        var autoSign = certPath != null || generateCert;

                        var result = await msixService.CreateMsixBundleAsync(inputFolders, output, taskContext, name, skipPri, autoSign, certPath, certPassword, generateCert, installCert, publisher, manifestPath, selfContained, executable, cancellationToken);

                        taskContext.AddStatusMessage($"{UiSymbols.Package} Bundle: {result.BundlePath}");
                        if (result.Signed)
                        {
                            taskContext.AddStatusMessage($"{UiSymbols.Lock} Bundle has been signed");
                        }
                        else
                        {
                            taskContext.AddStatusMessage($"Bundle is unsigned. For Store submission, upload as-is. For sideload, run `winapp sign`.");
                        }

                        return (0, "MSIX bundle creation completed.");
                    }
                    catch (Exception ex)
                    {
                        taskContext.AddDebugMessage($"Stack Trace: {ex.StackTrace}");
                        return (1, $"{UiSymbols.Error} Failed to create MSIX bundle: {ex.GetBaseException().Message}");
                    }
                }, cancellationToken);
            }
        }
    }
}
