// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;

namespace WinApp.Cli.Commands;

internal class CertCommand : Command
{
    public CertCommand(CertGenerateCommand certGenerateCommand, CertInstallCommand certInstallCommand)
        : base("cert", "Manage development certificates for code signing. Use 'cert generate' to create a self-signed certificate for testing, or 'cert install' (requires elevation) to trust an existing certificate on this machine.")
    {
        Subcommands.Add(certGenerateCommand);
        Subcommands.Add(certInstallCommand);
    }
}
