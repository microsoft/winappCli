// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class CertGenerateCommand : Command, IShortDescription
{
    public string ShortDescription => "Create a self-signed certificate for local testing";

    public static Option<string> PublisherOption { get; }
    public static Option<FileInfo> ManifestOption { get; }
    public static Option<FileInfo> OutputOption { get; }
    public static Option<string> PasswordOption { get; }
    public static Option<int> ValidDaysOption { get; }
    public static Option<bool> InstallOption { get; }
    public static Option<IfExists> IfExistsOption { get; }
    public static Option<bool> ExportCerOption { get; }

    static CertGenerateCommand()
    {
        PublisherOption = new Option<string>("--publisher")
        {
            Description = "Publisher distinguished name (DN) for the generated certificate (e.g., CN=MyCompany or OU=Team, O=Corp, C=US). If not specified, will be inferred from manifest. Bare names are auto-wrapped as CN=<name>."
        };
        ManifestOption = new Option<FileInfo>("--manifest")
        {
            Description = "Path to Package.appxmanifest or appxmanifest.xml file to extract publisher information from"
        };
        ManifestOption.AcceptExistingOnly();
        ManifestOption.AcceptLegalFilePathsOnly();
        OutputOption = new Option<FileInfo>("--output")
        {
            Description = "Output path for the generated PFX file"
        };
        OutputOption.AcceptLegalFilePathsOnly();
        PasswordOption = new Option<string>("--password")
        {
            Description = "Password for the generated PFX file",
            DefaultValueFactory = (argumentResult) => "password",
        };
        ValidDaysOption = new Option<int>("--valid-days")
        {
            Description = "Number of days the certificate is valid",
            DefaultValueFactory = (argumentResult) => 365,
        };
        InstallOption = new Option<bool>("--install")
        {
            Description = "Install the certificate to the local machine store after generation"
        };
        IfExistsOption = new Option<IfExists>("--if-exists")
        {
            Description = "Behavior when output file exists: 'error' (fail, default), 'skip' (keep existing), or 'overwrite' (replace)",
            DefaultValueFactory = (argumentResult) => IfExists.Error,
        };
        ExportCerOption = new Option<bool>("--export-cer")
        {
            Description = "Export a .cer file (public key only) alongside the .pfx"
        };
    }

    public CertGenerateCommand()
        : base("generate", "Create a self-signed certificate for local testing only. Publisher must match the manifest (auto-inferred if --manifest provided or Package.appxmanifest is in working directory). Output: devcert.pfx (default password: 'password'). For production, obtain a certificate from a trusted CA. Use 'cert install' to trust on this machine.")
    {
        Options.Add(PublisherOption);
        Options.Add(ManifestOption);
        Options.Add(OutputOption);
        Options.Add(PasswordOption);
        Options.Add(ValidDaysOption);
        Options.Add(InstallOption);
        Options.Add(IfExistsOption);
        Options.Add(ExportCerOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public class Handler(ICertificateService certificateService, ICurrentDirectoryProvider currentDirectoryProvider, IStatusService statusService, IAnsiConsole ansiConsole, ILogger<CertGenerateCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var publisher = parseResult.GetValue(PublisherOption);
            var manifestPath = parseResult.GetValue(ManifestOption);
            var output = parseResult.GetValue(OutputOption) ?? new FileInfo(Path.Combine(currentDirectoryProvider.GetCurrentDirectory(), CertificateService.DefaultCertFileName));
            var password = parseResult.GetRequiredValue(PasswordOption);
            var validDays = parseResult.GetRequiredValue(ValidDaysOption);
            var install = parseResult.GetRequiredValue(InstallOption);
            var ifExists = parseResult.GetRequiredValue(IfExistsOption);
            var exportCer = parseResult.GetRequiredValue(ExportCerOption);
            var json = parseResult.GetRequiredValue(WinAppRootCommand.JsonOption);

            // The npm wrapper now forwards an explicitly empty --password rather than dropping it, and a
            // native user can pass one directly. An empty or whitespace password would produce a PFX with
            // no effective protection, so reject it here. Omitting --password still uses the dev default.
            if (string.IsNullOrWhiteSpace(password))
            {
                const string message = "Certificate password cannot be empty. Omit --password to use the development default, or supply a non-empty password.";
                if (json)
                {
                    return JsonErrorOutput.Write(ansiConsole, message);
                }
                logger.LogError("{UISymbol} {Message}", UiSymbols.Error, message);
                return 1;
            }

            // Check if certificate file already exists
            if (output.Exists)
            {
                if (ifExists == IfExists.Error)
                {
                    if (json)
                    {
                        return JsonErrorOutput.Write(ansiConsole, $"Certificate file already exists: {output}");
                    }
                    logger.LogError("{UISymbol} Certificate file already exists: {Output}{NewLine}Please specify a different output path or remove the existing file.", UiSymbols.Error, output, System.Environment.NewLine);
                    return 1;
                }
                else if (ifExists == IfExists.Skip)
                {
                    logger.LogInformation("{UISymbol} Certificate file already exists: {Output}", UiSymbols.Warning, output);
                    return 0;
                }
                else if (ifExists == IfExists.Overwrite)
                {
                    logger.LogInformation("{UISymbol} Overwriting existing certificate file: {Output}", UiSymbols.Warning, output);
                }
            }

            CertificateService.CertificateResult? certResult = null;

            var returnCode = await statusService.ExecuteWithStatusAsync("Generating development certificate...", async (taskContext, ct) =>
            {
                certResult = await GenerateCertAsync(taskContext, ct);
                return (0, "Development certificate generated successfully.");
            }, cancellationToken);

            if (returnCode == 0 && json && certResult != null)
            {
                var jsonOutput = new CertGenerateJsonOutput
                {
                    CertificatePath = certResult.CertificatePath.FullName,
                    Password = certResult.Password,
                    Publisher = certResult.Publisher,
                    SubjectName = certResult.SubjectName,
                    PublicCertificatePath = certResult.PublicCertificatePath?.FullName,
                };
                ansiConsole.Profile.Out.Writer.WriteLine(JsonSerializer.Serialize(jsonOutput, WinAppJsonContext.Default.CertGenerateJsonOutput));
            }

            return returnCode;

            Task<CertificateService.CertificateResult> GenerateCertAsync(TaskContext taskContext, CancellationToken ct) =>
                certificateService.GenerateDevCertificateWithInferenceAsync(
                    outputPath: output,
                    taskContext: taskContext,
                    explicitPublisher: publisher,
                    manifestPath: manifestPath,
                    password: password,
                    validDays: validDays,
                    updateGitignore: true,
                    install: install,
                    exportCer: exportCer,
                    cancellationToken: ct);
        }
    }
}
