// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Invocation;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class PackageCommand : Command
{
    public static Argument<DirectoryInfo> InputFolderArgument { get; }
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

    static PackageCommand()
    {
        InputFolderArgument = new Argument<DirectoryInfo>("input-folder")
        {
            Description = "Input folder with package layout",
            Arity = ArgumentArity.ExactlyOne
        };
        InputFolderArgument.AcceptExistingOnly();
        OutputOption = new Option<FileInfo>("--output")
        {
            Description = "Output msix file name for the generated package (defaults to <name>.msix)",
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
            Description = "Publisher name for certificate generation"
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
    }

    public PackageCommand()
        : base("package", "Create an MSIX package from a prepared package directory")
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
    }

    public class Handler(IMsixService msixService, ILogger<PackageCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var inputFolder = parseResult.GetRequiredValue(InputFolderArgument);
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
            try
            {
                // Auto-sign if certificate is provided or if generate-cert is specified
                var autoSign = certPath != null || generateCert;

                var result = await msixService.CreateMsixPackageAsync(inputFolder, output, name, skipPri, autoSign, certPath, certPassword, generateCert, installCert, publisher, manifestPath, selfContained, cancellationToken);

                logger.LogInformation("{UISymbol} MSIX package created successfully!", UiSymbols.Check);

                logger.LogInformation("{UISymbol} Package: {PackagePath}", UiSymbols.Package, result.MsixPath);
                if (result.Signed)
                {
                    logger.LogInformation("{UISymbol} Package has been signed", UiSymbols.Lock);
                }

                return 0;
            }
            catch (Exception ex)
            {
                logger.LogError("{UISymbol} Failed to create MSIX package: {ErrorMessage}", UiSymbols.Error, ex.Message);
                logger.LogDebug("Stack Trace: {StackTrace}", ex.StackTrace);
                return 1;
            }
        }
    }
}
