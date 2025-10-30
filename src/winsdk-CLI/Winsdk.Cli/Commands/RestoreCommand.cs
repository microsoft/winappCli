// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using Winsdk.Cli.Services;

namespace Winsdk.Cli.Commands;

internal class RestoreCommand : Command
{
    public static Argument<DirectoryInfo> BaseDirectoryArgument { get; }
    public static Option<DirectoryInfo> ConfigDirOption { get; }
    static RestoreCommand()
    {
        BaseDirectoryArgument = new Argument<DirectoryInfo>("base-directory")
        {
            Description = "Base/root directory for the winsdk workspace",
            Arity = ArgumentArity.ZeroOrOne
        };
        BaseDirectoryArgument.AcceptExistingOnly();

        ConfigDirOption = new Option<DirectoryInfo>("--config-dir")
        {
            Description = "Directory to read configuration from (default: current directory)"
        };
        ConfigDirOption.AcceptExistingOnly();
    }

    public RestoreCommand() : base("restore", "Restore packages from winsdk.yaml and ensure workspace is ready")
    {
        Arguments.Add(BaseDirectoryArgument);
        Options.Add(ConfigDirOption);
    }

    public class Handler(IWorkspaceSetupService workspaceSetupService, ICurrentDirectoryProvider currentDirectoryProvider) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var baseDirectory = parseResult.GetValue(BaseDirectoryArgument) ?? currentDirectoryProvider.GetCurrentDirectoryInfo();
            var configDir = parseResult.GetValue(ConfigDirOption) ?? currentDirectoryProvider.GetCurrentDirectoryInfo();

            var options = new WorkspaceSetupOptions
            {
                BaseDirectory = baseDirectory,
                ConfigDir = configDir,
                RequireExistingConfig = true,
                ForceLatestBuildTools = false // Will be determined from config
            };

            return await workspaceSetupService.SetupWorkspaceAsync(options, cancellationToken);
        }
    }
}
