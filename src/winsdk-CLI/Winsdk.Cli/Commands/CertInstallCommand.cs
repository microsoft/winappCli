// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Invocation;
using Winsdk.Cli.Helpers;
using Winsdk.Cli.Services;

namespace Winsdk.Cli.Commands;

internal class CertInstallCommand : Command
{
    public static Argument<string> CertPathArgument { get; }
    public static Option<string> PasswordOption { get; }
    public static Option<bool> ForceOption { get; }

    static CertInstallCommand()
    {
        CertPathArgument = new Argument<string>("cert-path")
        {
            Description = "Path to the certificate file (PFX or CER)"
        };
        PasswordOption = new Option<string>("--password")
        {
            Description = "Password for the PFX file",
            DefaultValueFactory = (argumentResult) => "password",
        };
        ForceOption = new Option<bool>("--force")
        {
            Description = "Force installation even if the certificate already exists",
            DefaultValueFactory = (argumentResult) => false,
        };
    }

    public CertInstallCommand()
        : base("install", "Install a certificate to the local machine store")
    {
        Arguments.Add(CertPathArgument);
        Options.Add(PasswordOption);
        Options.Add(ForceOption);
    }

    public class Handler(ICertificateService certificateService, ILogger<CertInstallCommand> logger) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var certPath = parseResult.GetRequiredValue(CertPathArgument);
            var password = parseResult.GetRequiredValue(PasswordOption);
            var force = parseResult.GetRequiredValue(ForceOption);

            try
            {
                var result = certificateService.InstallCertificate(certPath, password, force);
                if (!result)
                {
                    logger.LogInformation("{UISymbol} Certificate is already installed", UiSymbols.Info);
                }
                else
                {
                    logger.LogInformation("{UISymbol} Certificate installed successfully!", UiSymbols.Check);
                }

                return Task.FromResult(0);
            }
            catch (Exception error)
            {
                logger.LogError("{UISymbol} Failed to install certificate: {ErrorMessage}", UiSymbols.Error, error.Message);
                return Task.FromResult(1);
            }
        }
    }
}
