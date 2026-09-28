// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Invocation;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class ManifestGenerateCommand : Command, IShortDescription
{
    public string ShortDescription => "Create Package.appxmanifest and required image assets";

    public static Argument<DirectoryInfo> DirectoryArgument { get; }
    public static Option<string> PackageNameOption { get; }
    public static Option<string> PublisherNameOption { get; }
    public static Option<string> VersionOption { get; }
    public static Option<string> DescriptionOption { get; }
    public static Option<FileInfo> ExecutableOption { get; }
    public static Option<ManifestTemplates> TemplateOption { get; }
    public static Option<FileInfo> LogoPathOption { get; }

    static ManifestGenerateCommand()
    {
        DirectoryArgument = new Argument<DirectoryInfo>("directory")
        {
            Description = "Directory to generate manifest in",
            Arity = ArgumentArity.ZeroOrOne
        };
        DirectoryArgument.AcceptExistingOnly();

        PackageNameOption = new Option<string>("--package-name")
        {
            Description = "Package name (default: folder name)"
        };

        PublisherNameOption = new Option<string>("--publisher-name")
        {
            Description = "Publisher distinguished name (DN) (default: CN=<current user>). Accepts an X.500 DN with single-valued, comma-separated components (multi-valued '+' RDNs, ';' separators, and backslashes are not supported); bare names are auto-wrapped as CN=<name>."
        };

        VersionOption = new Option<string>("--version")
        {
            Description = "App version in Major.Minor.Build.Revision format (e.g., 1.0.0.0).",
            DefaultValueFactory = (argumentResult) => "1.0.0.0"
        };

        DescriptionOption = new Option<string>("--description")
        {
            Description = "Human-readable app description shown during installation and in Windows Settings",
            DefaultValueFactory = (argumentResult) => SystemDefaultsHelper.GetDefaultDescription(),
        };

        ExecutableOption = new Option<FileInfo>("--executable", "--entrypoint")
        {
            Description = "Path to the application's executable. Default: <package-name>.exe"
        };
        ExecutableOption.AcceptExistingOnly();

        TemplateOption = new Option<ManifestTemplates>("--template")
        {
            Description = "Manifest template type: 'packaged' (full MSIX app, default) or 'sparse' (desktop app with package identity for Windows APIs)",
            DefaultValueFactory = (argumentResult) => ManifestTemplates.Packaged
        };

        LogoPathOption = new Option<FileInfo>("--logo-path")
        {
            Description = "Path to logo image file"
        };
    }

    public ManifestGenerateCommand() : base("generate", "Create Package.appxmanifest without full project setup. Use when you only need a manifest and image assets (no SDKs, no certificate). For full setup, use 'init' instead. Templates: 'packaged' (full MSIX), 'sparse' (desktop app needing Windows APIs).")
    {
        Arguments.Add(DirectoryArgument);
        Options.Add(PackageNameOption);
        Options.Add(PublisherNameOption);
        Options.Add(VersionOption);
        Options.Add(DescriptionOption);
        Options.Add(ExecutableOption);
        Options.Add(TemplateOption);
        Options.Add(LogoPathOption);
        Options.Add(CertGenerateCommand.IfExistsOption);
    }

    public class Handler(IManifestService manifestService, ICurrentDirectoryProvider currentDirectoryProvider, IStatusService statusService, ILogger<ManifestGenerateCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var directory = parseResult.GetValue(DirectoryArgument) ?? currentDirectoryProvider.GetCurrentDirectoryInfo();
            var packageName = parseResult.GetValue(PackageNameOption);
            var publisherName = parseResult.GetValue(PublisherNameOption);
            var version = parseResult.GetRequiredValue(VersionOption);
            var description = parseResult.GetRequiredValue(DescriptionOption);
            var executable = parseResult.GetValue(ExecutableOption);
            var template = parseResult.GetValue(TemplateOption);
            var logoPath = parseResult.GetValue(LogoPathOption);
            var ifExists = parseResult.GetRequiredValue(CertGenerateCommand.IfExistsOption);

            // Check if manifest already exists
            var manifestPath = MsixService.FindProjectManifest(currentDirectoryProvider, directory);
            if (manifestPath?.Exists == true)
            {
                if (ifExists == IfExists.Error)
                {
                    logger.LogError("{UISymbol} Manifest file already exists: {Output}{NewLine}Please specify a different output path or remove the existing file.", UiSymbols.Error, manifestPath, System.Environment.NewLine);
                    return 1;
                }
                else if (ifExists == IfExists.Skip)
                {
                    logger.LogInformation("{UISymbol} Manifest file already exists: {Output}", UiSymbols.Warning, manifestPath);
                    return 0;
                }
                else if (ifExists == IfExists.Overwrite)
                {
                    logger.LogInformation("{UISymbol} Overwriting existing manifest file: {Output}", UiSymbols.Warning, manifestPath);
                }
            }

            var manifestGenerationInfo = await manifestService.PromptForManifestInfoAsync(directory, packageName, publisherName, version, description, executable?.ToString(), true, cancellationToken);

            return await statusService.ExecuteWithStatusAsync("Generating manifest", async (taskContext, cancellationToken) =>
            {
                try
                {
                    await manifestService.GenerateManifestAsync(
                        directory,
                        manifestGenerationInfo,
                        template,
                        logoPath,
                        executable?.ToString(),
                        taskContext,
                        cancellationToken);

                    return (0, $"Manifest generated successfully in: {directory}");
                }
                catch (Exception ex)
                {
                    taskContext.AddDebugMessage($"Stack Trace: {ex.StackTrace}");
                    return (1, $"{UiSymbols.Error} Error generating manifest: {ex.GetBaseException().Message}");
                }
            }, cancellationToken);
        }
    }
}
