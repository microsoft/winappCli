// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class CertInstallCommand : Command
{
    public static Argument<FileInfo> CertPathArgument { get; }
    public static Option<string> PasswordOption { get; }
    public static Option<bool> ForceOption { get; }

    static CertInstallCommand()
    {
        CertPathArgument = new Argument<FileInfo>("cert-path")
        {
            Description = "Path to the certificate file (PFX or CER)"
        };
        CertPathArgument.AcceptExistingOnly();
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
        : base("install", "Add a certificate to the Trusted People store so Windows trusts packages signed with it. Required before installing MSIX packages signed with development certificates. Needs administrator privileges.")
    {
        Arguments.Add(CertPathArgument);
        Options.Add(PasswordOption);
        Options.Add(ForceOption);
    }

    public class Handler(ICertificateService certificateService, IStatusService statusService) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var certPath = parseResult.GetRequiredValue(CertPathArgument);
            var password = parseResult.GetRequiredValue(PasswordOption);
            var force = parseResult.GetRequiredValue(ForceOption);

            return await statusService.ExecuteWithStatusAsync("Installing certificate...", (taskContext, cancellationToken) =>
            {
                try
                {
                    var result = certificateService.InstallCertificate(certPath, password, force, taskContext);
                    var message = !result
                        ? "Certificate is already installed."
                        : "Certificate installed successfully!";

                    return Task.FromResult((0, message));
                }
                catch (Exception error)
                {
                    return Task.FromResult((1, $"{UiSymbols.Error} Failed to install certificate: {error.Message}"));
                }
            }, cancellationToken);
        }
    }
}
