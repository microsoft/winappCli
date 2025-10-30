// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Invocation;
using Winsdk.Cli.Helpers;
using Winsdk.Cli.Models;
using Winsdk.Cli.Services;

namespace Winsdk.Cli.Commands;

internal class ManifestGenerateCommand : Command
{
    public static Argument<DirectoryInfo> DirectoryArgument { get; }
    public static Option<string> PackageNameOption { get; }
    public static Option<string> PublisherNameOption { get; }
    public static Option<string> VersionOption { get; }
    public static Option<string> DescriptionOption { get; }
    public static Option<FileInfo> EntryPointOption { get; }
    public static Option<ManifestTemplates> TemplateOption { get; }
    public static Option<FileInfo> LogoPathOption { get; }
    public static Option<bool> YesOption { get; }

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
            Description = "Publisher CN (default: CN=<current user>)"
        };

        VersionOption = new Option<string>("--version")
        {
            Description = "Version",
            DefaultValueFactory = (argumentResult) => "1.0.0.0"
        };

        DescriptionOption = new Option<string>("--description")
        {
            Description = "Description",
            DefaultValueFactory = (argumentResult) => "My Application"
        };

        EntryPointOption = new Option<FileInfo>("--entrypoint", "--executable")
        {
            Description = "Entry point of the application (e.g., executable path / name, or .py/.js script if template is HostedApp). Default: <package-name>.exe"
        };
        EntryPointOption.AcceptExistingOnly();

        TemplateOption = new Option<ManifestTemplates>("--template")
        {
            Description = "Generate manifest using specified template",
            DefaultValueFactory = (argumentResult) => ManifestTemplates.Packaged
        };

        LogoPathOption = new Option<FileInfo>("--logo-path")
        {
            Description = "Path to logo image file"
        };

        YesOption = new Option<bool>("--yes", "--y")
        {
            Description = "Skip interactive prompts and use default values"
        };
    }

    public ManifestGenerateCommand() : base("generate", "Generate a manifest in directory")
    {
        Arguments.Add(DirectoryArgument);
        Options.Add(PackageNameOption);
        Options.Add(PublisherNameOption);
        Options.Add(VersionOption);
        Options.Add(DescriptionOption);
        Options.Add(EntryPointOption);
        Options.Add(TemplateOption);
        Options.Add(LogoPathOption);
        Options.Add(YesOption);
    }

    public class Handler(IManifestService manifestService, ICurrentDirectoryProvider currentDirectoryProvider, ILogger<ManifestGenerateCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var directory = parseResult.GetValue(DirectoryArgument) ?? currentDirectoryProvider.GetCurrentDirectoryInfo();
            var packageName = parseResult.GetValue(PackageNameOption);
            var publisherName = parseResult.GetValue(PublisherNameOption);
            var version = parseResult.GetRequiredValue(VersionOption);
            var description = parseResult.GetRequiredValue(DescriptionOption);
            var entryPoint = parseResult.GetValue(EntryPointOption);
            var template = parseResult.GetValue(TemplateOption);
            var logoPath = parseResult.GetValue(LogoPathOption);
            var yes = parseResult.GetValue(YesOption);
            try
            {
                await manifestService.GenerateManifestAsync(
                    directory,
                    packageName,
                    publisherName,
                    version,
                    description,
                    entryPoint?.ToString(),
                    template,
                    logoPath,
                    yes,
                    cancellationToken);

                logger.LogInformation("Manifest generated successfully in: {Directory}", directory);

                return 0;
            }
            catch (Exception ex)
            {
                logger.LogError("{UISymbol} Error generating manifest: {ErrorMessage}", UiSymbols.Error, ex.Message);
                logger.LogDebug("Stack Trace: {StackTrace}", ex.StackTrace);
                return 1;
            }
        }
    }
}
