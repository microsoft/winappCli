// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;

namespace WinApp.Cli.Commands;

internal class CertCommand : Command
{
    public CertCommand(CertGenerateCommand certGenerateCommand, CertInstallCommand certInstallCommand)
        : base("cert", "Generate or install development certificates")
    {
        Subcommands.Add(certGenerateCommand);
        Subcommands.Add(certInstallCommand);
    }
}
