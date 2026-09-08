// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using WinApp.Cli.Services;
using WinApp.Cli.Telemetry.Events;

namespace WinApp.Cli.Commands;

internal class RestoreCommand : Command, IShortDescription
{
    public string ShortDescription => "Restore packages and projections for an initialized project";
    public static Argument<DirectoryInfo> BaseDirectoryArgument { get; }
    public static Option<DirectoryInfo> ConfigDirOption { get; }
    static RestoreCommand()
    {
        BaseDirectoryArgument = new Argument<DirectoryInfo>("base-directory")
        {
            Description = "Base/root directory for the winapp workspace",
            Arity = ArgumentArity.ZeroOrOne
        };
        BaseDirectoryArgument.AcceptExistingOnly();

        ConfigDirOption = new Option<DirectoryInfo>("--config-dir")
        {
            Description = "Directory to read configuration from (default: base-directory)"
        };
        ConfigDirOption.AcceptExistingOnly();
    }

    public RestoreCommand() : base("restore", "Use after cloning a repo or when .winapp/ folder is missing. Reinstalls SDK packages without changing versions, reading them from winapp.yaml or, for a .NET project initialized by 'init', from the .csproj via 'dotnet restore'. Requires a project already initialized by 'init'. To check for newer SDK versions, use 'update' instead.")
    {
        Arguments.Add(BaseDirectoryArgument);
        Options.Add(ConfigDirOption);
    }

    public class Handler(
        IWorkspaceSetupService workspaceSetupService,
        ICurrentDirectoryProvider currentDirectoryProvider,
        IProjectContextDetector projectContextDetector) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var baseDirectory = parseResult.GetValue(BaseDirectoryArgument) ?? currentDirectoryProvider.GetCurrentDirectoryInfo();

            // When --config-dir is not given, read the configuration from the directory being restored, so
            // `winapp restore ./my-project` finds ./my-project/winapp.yaml. This mirrors `init`, which already
            // co-locates winapp.yaml with the selected directory; without it the two commands disagreed and
            // `winapp restore ./my-project` silently reported "nothing to restore" while looking in the
            // current directory. With no base directory both still default to the current directory.
            var configDir = parseResult.GetValue(ConfigDirOption) ?? baseDirectory;

            ProjectContextEvent.Log(
                "restore",
                () => projectContextDetector.DetectDirectories(
                    [baseDirectory, configDir],
                    ProjectTargetKind.Workspace));

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
