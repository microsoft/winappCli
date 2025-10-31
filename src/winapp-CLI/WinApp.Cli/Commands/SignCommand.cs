// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Invocation;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class SignCommand : Command
{
    public static Argument<FileInfo> FilePathArgument { get; }
    public static Argument<FileInfo> CertPathArgument { get; }
    public static Option<string> PasswordOption { get; }
    public static Option<string> TimestampOption { get; }

    static SignCommand()
    {
        FilePathArgument = new Argument<FileInfo>("file-path")
        {
            Description = "Path to the file/package to sign"
        };
        FilePathArgument.AcceptExistingOnly();
        CertPathArgument = new Argument<FileInfo>("cert-path")
        {
            Description = "Path to the certificate file (PFX format)"
        };
        CertPathArgument.AcceptExistingOnly();
        PasswordOption = new Option<string>("--password")
        {
            Description = "Certificate password",
            DefaultValueFactory = (argumentResult) => "password"
        };
        TimestampOption = new Option<string>("--timestamp")
        {
            Description = "Timestamp server URL"
        };
    }

    public SignCommand() : base("sign", "Sign a file/package with a certificate")
    {
        Arguments.Add(FilePathArgument);
        Arguments.Add(CertPathArgument);
        Options.Add(PasswordOption);
        Options.Add(TimestampOption);
    }

    public class Handler(ICertificateService certificateService, ILogger<RestoreCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var filePath = parseResult.GetRequiredValue(FilePathArgument);
            var certPath = parseResult.GetRequiredValue(CertPathArgument);
            var password = parseResult.GetValue(PasswordOption);
            var timestamp = parseResult.GetValue(TimestampOption);

            try
            {
                await certificateService.SignFileAsync(filePath, certPath, password, timestamp, cancellationToken);

                logger.LogInformation("{UISymbol} Signed file: {FilePath}", UiSymbols.Lock, filePath);
                return 0;
            }
            catch (InvalidOperationException error)
            {
                logger.LogError("{ErrorMessage}", error.Message);
                return 1;
            }
            catch (Exception error)
            {
                logger.LogError("{UISymbol} Failed to sign file: {ErrorMessage}", UiSymbols.Error, error.Message);
                return 1;
            }
        }
    }
}
