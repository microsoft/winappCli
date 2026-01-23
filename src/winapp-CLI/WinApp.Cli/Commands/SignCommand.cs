// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
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

    public SignCommand() : base("sign", "Code-sign a package or executable with a certificate. Use a development certificate for testing, or a trusted certificate for distribution.")
    {
        Arguments.Add(FilePathArgument);
        Arguments.Add(CertPathArgument);
        Options.Add(PasswordOption);
        Options.Add(TimestampOption);
    }

    public class Handler(ICertificateService certificateService, IStatusService statusService) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var filePath = parseResult.GetRequiredValue(FilePathArgument);
            var certPath = parseResult.GetRequiredValue(CertPathArgument);
            var password = parseResult.GetValue(PasswordOption);
            var timestamp = parseResult.GetValue(TimestampOption);

            return await statusService.ExecuteWithStatusAsync($"Signing file: {filePath}", async (taskContext, cancellationToken) =>
            {
                try
                {
                    await certificateService.SignFileAsync(filePath, certPath, taskContext, password, timestamp, cancellationToken);

                    return (0, "Signed file: {filePath}");
                }
                catch (InvalidOperationException error)
                {
                    return (1, $"Failed to sign file: {error.Message}");
                }
                catch (Exception error)
                {
                    return (1, $"Failed to sign file: {error.Message}");
                }
            }, cancellationToken);
        }
    }
}
