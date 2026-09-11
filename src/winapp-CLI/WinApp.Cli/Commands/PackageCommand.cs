// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.CommandLine;
using System.CommandLine.Invocation;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;
using WinApp.Cli.Telemetry.Events;

namespace WinApp.Cli.Commands;

internal partial class PackageCommand : Command, IShortDescription
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

    // Project-mode options (mirrors winapp run; inert unless the input is a .csproj).
    public static Option<string> ConfigurationOption { get; }
    public static Option<string?> ArchOption { get; }
    public static Option<string?> RuntimeOption { get; }
    public static Option<string?> FrameworkOption { get; }
    public static Option<bool> NoBuildOption { get; }
    public static Option<bool> NoRestoreOption { get; }
    public static Option<string[]> PropertyOption { get; }

    static PackageCommand()
    {
        InputFolderArgument = new Argument<DirectoryInfo[]>("input-folder")
        {
            Description = "A single .csproj to build and package (project mode), one or more input folders with package layout, or a single sparse appxmanifest.xml file (an identity-only package with AllowExternalContent). Pass multiple folders to create an MSIX bundle (e.g., winapp pack ./publish/x64 ./publish/arm64).",
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

        ConfigurationOption = new Option<string>("--configuration")
        {
            Description = "Project mode: build configuration (e.g., Debug, Release). Ignored for folder/bundle/manifest inputs. Default: Debug.",
            DefaultValueFactory = _ => "Debug",
        };
        ConfigurationOption.Aliases.Add("-c");

        ArchOption = new Option<string?>("--arch")
        {
            Description = "Project mode: target architecture (x64, arm64, or x86). Ignored for folder/bundle/manifest inputs. Default: the current process architecture."
        };

        RuntimeOption = new Option<string?>("--runtime")
        {
            Description = "Project mode: target .NET runtime identifier (RID), e.g. win-x64. Uses only the RID's architecture, rejects non-Windows RIDs, and overrides --arch. Ignored for folder/bundle/manifest inputs."
        };
        RuntimeOption.Aliases.Add("-r");

        FrameworkOption = new Option<string?>("--framework")
        {
            Description = "Project mode: target framework moniker for multi-targeted projects (e.g. net10.0-windows10.0.26100.0). Ignored for folder/bundle/manifest inputs."
        };
        FrameworkOption.Aliases.Add("-f");

        NoBuildOption = new Option<bool>("--no-build")
        {
            Description = "Project mode: skip building and package the existing build output (still evaluates output properties). Ignored for folder/bundle/manifest inputs."
        };

        NoRestoreOption = new Option<bool>("--no-restore")
        {
            Description = "Project mode: skip restoring the project before building. Ignored for folder/bundle/manifest inputs."
        };

        PropertyOption = new Option<string[]>("--property")
        {
            Description = "Project mode: MSBuild property as Name=Value, forwarded to both build and evaluation. Repeatable (e.g. -p Configuration=Release). Ignored for folder/bundle/manifest inputs.",
            Arity = ArgumentArity.ZeroOrMore,
            AllowMultipleArgumentsPerToken = false,
        };
        PropertyOption.Aliases.Add("-p");
    }

    public PackageCommand()
        : base("package", "Create an MSIX installer from a built app folder or directly from a .csproj. Pass a package-layout folder (run after building your app; a manifest must be in the current directory, passed as --manifest, or in the input folder), or pass a .csproj to build and package it in one step (e.g. winapp package ./MyApp.csproj -c Release). Use --cert devcert.pfx to sign for testing.")
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
        Options.Add(ConfigurationOption);
        Options.Add(ArchOption);
        Options.Add(RuntimeOption);
        Options.Add(FrameworkOption);
        Options.Add(NoBuildOption);
        Options.Add(NoRestoreOption);
        Options.Add(PropertyOption);
    }

    public partial class Handler(
        IMsixService msixService,
        IStatusService statusService,
        IProjectRunService projectRunService,
        IProjectContextDetector projectContextDetector,
        ICurrentDirectoryProvider currentDirectoryProvider,
        IAnsiConsole ansiConsole,
        ILogger<PackageCommand> logger) : AsynchronousCommandLineAction
    {
        /// <summary>
        /// Heuristic for whether a non-existent input path was intended as a manifest file
        /// (rather than an input folder), so a missing path can be reported with the right error.
        /// </summary>
        private static bool LooksLikeManifestPath(string name)
            => name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".appxmanifest", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Validates the <c>--output</c> extension against the packaging mode. Returns an actionable
        /// error message, or null when the extension is acceptable. Shared by folder/bundle packaging
        /// and project mode so a single <c>.csproj</c> rejects a <c>.msixbundle</c> <c>--output</c>
        /// before building.
        /// </summary>
        internal static string? ValidateOutputExtension(FileInfo? output, bool isBundle)
        {
            if (output == null)
            {
                return null;
            }

            var ext = Path.GetExtension(output.Name);
            if (isBundle && string.Equals(ext, ".msix", StringComparison.OrdinalIgnoreCase))
            {
                return $"{UiSymbols.Error} Cannot use .msix extension for --output when creating a bundle from multiple folders. Use .msixbundle or omit the extension.";
            }

            if (!isBundle && string.Equals(ext, ".msixbundle", StringComparison.OrdinalIgnoreCase))
            {
                return $"{UiSymbols.Error} Cannot use .msixbundle extension for --output when creating a single package. Use .msix or omit the extension.";
            }

            return null;
        }

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

            // Project mode: a single explicit .csproj input (not an existing directory that merely
            // happens to be named "*.csproj") builds the project and packages its output. Runs before
            // the sparse/missing-directory checks below so a non-existent .csproj is reported as a
            // missing project, not a missing input folder. Everything else keeps its existing routing.
            if (inputFolders.Length == 1
                && inputFolders[0].Name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                && !Directory.Exists(inputFolders[0].FullName))
            {
                return await RunProjectModeAsync(parseResult, new FileInfo(inputFolders[0].FullName), cancellationToken);
            }

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

            // Validate --output extension against the packaging mode (shared with project mode).
            var outputExtensionError = ValidateOutputExtension(output, isBundle: inputFolders.Length > 1);
            if (outputExtensionError != null)
            {
                return await statusService.ExecuteWithStatusAsync("Validating input...", (taskContext, _) =>
                {
                    return Task.FromResult((1, outputExtensionError));
                }, cancellationToken);
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

                        var result = await msixService.CreateMsixPackageAsync(inputFolder, output, taskContext, name, skipPri, autoSign, certPath, certPassword, generateCert, installCert, publisher, manifestPath, selfContained, executable, cancellationToken: cancellationToken);

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
