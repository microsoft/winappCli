// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;

namespace WinApp.Cli.Commands;

internal class ManifestCommand : Command
{
    public ManifestCommand(ManifestGenerateCommand manifestGenerateCommand, ManifestUpdateAssetsCommand manifestUpdateAssetsCommand)
        : base("manifest", "AppxManifest.xml management")
    {
        Subcommands.Add(manifestGenerateCommand);
        Subcommands.Add(manifestUpdateAssetsCommand);
    }
}
