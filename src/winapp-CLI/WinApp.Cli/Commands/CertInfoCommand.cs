// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class CertInfoCommand : Command, IShortDescription
{
    public string ShortDescription => "Display certificate details.";
    public static Argument<FileInfo> CertPathArgument { get; }
    public static Option<string> PasswordOption { get; }

    static CertInfoCommand()
    {
        CertPathArgument = new Argument<FileInfo>("cert-path")
        {
            Description = "Path to the certificate file (PFX or CER)"
        };
        CertPathArgument.AcceptExistingOnly();
        PasswordOption = new Option<string>("--password")
        {
            Description = "Password for the PFX file (ignored for a public CER)",
            DefaultValueFactory = (argumentResult) => "password",
        };
    }

    public CertInfoCommand()
        : base("info", "Display certificate details (subject, thumbprint, expiry). Useful for verifying a certificate matches your manifest before signing.")
    {
        Arguments.Add(CertPathArgument);
        Options.Add(PasswordOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public class Handler(IAnsiConsole ansiConsole, ILogger<CertInfoCommand> logger) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var certPath = parseResult.GetRequiredValue(CertPathArgument);
            var password = parseResult.GetRequiredValue(PasswordOption);
            var json = parseResult.GetRequiredValue(WinAppRootCommand.JsonOption);

            certPath.Refresh();
            if (!certPath.Exists)
            {
                if (json)
                {
                    return Task.FromResult(JsonErrorOutput.Write(ansiConsole, $"Certificate file not found: {certPath}"));
                }
                logger.LogError("Certificate file not found: {CertPath}", certPath);
                return Task.FromResult(1);
            }

            try
            {
                using var cert = CertificateService.LoadCertificate(
                    certPath, password, X509KeyStorageFlags.Exportable);

                if (json)
                {
                    var jsonOutput = new CertInfoJsonOutput
                    {
                        Subject = cert.Subject,
                        Issuer = cert.Issuer,
                        Thumbprint = cert.Thumbprint,
                        SerialNumber = cert.SerialNumber,
                        NotBefore = cert.NotBefore.ToString("yyyy-MM-dd HH:mm:ss"),
                        NotAfter = cert.NotAfter.ToString("yyyy-MM-dd HH:mm:ss"),
                        HasPrivateKey = cert.HasPrivateKey,
                    };
                    ansiConsole.Profile.Out.Writer.WriteLine(JsonSerializer.Serialize(jsonOutput, WinAppJsonContext.Default.CertInfoJsonOutput));
                }
                else
                {
                    ansiConsole.WriteLine($"Subject:         {cert.Subject}");
                    ansiConsole.WriteLine($"Issuer:          {cert.Issuer}");
                    ansiConsole.WriteLine($"Thumbprint:      {cert.Thumbprint}");
                    ansiConsole.WriteLine($"Serial Number:   {cert.SerialNumber}");
                    ansiConsole.WriteLine($"Not Before:      {cert.NotBefore:yyyy-MM-dd HH:mm:ss}");
                    ansiConsole.WriteLine($"Not After:       {cert.NotAfter:yyyy-MM-dd HH:mm:ss}");
                    ansiConsole.WriteLine($"Has Private Key: {cert.HasPrivateKey}");
                }

                return Task.FromResult(0);
            }
            catch (Exception ex)
            {
                if (json)
                {
                    return Task.FromResult(JsonErrorOutput.Write(ansiConsole, $"Failed to read certificate: {ex.Message}"));
                }
                logger.LogError("Failed to read certificate: {Message}", ex.Message);
                return Task.FromResult(1);
            }
        }
    }
}
