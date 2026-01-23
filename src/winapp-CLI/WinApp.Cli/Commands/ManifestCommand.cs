// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;

namespace WinApp.Cli.Commands;

internal class ManifestCommand : Command
{
    public ManifestCommand(ManifestGenerateCommand manifestGenerateCommand, ManifestUpdateAssetsCommand manifestUpdateAssetsCommand)
        : base("manifest", "Create and modify appxmanifest.xml files for package identity and MSIX packaging. Use 'manifest generate' to create a new manifest, or 'manifest update-assets' to regenerate app icons from a source image.")
    {
        Subcommands.Add(manifestGenerateCommand);
        Subcommands.Add(manifestUpdateAssetsCommand);
    }
}
