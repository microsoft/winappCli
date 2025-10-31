// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class InitCommand : Command
{
    public static Argument<DirectoryInfo> BaseDirectoryArgument { get; }
    public static Option<DirectoryInfo> ConfigDirOption { get; }
    public static Option<bool> PrereleaseOption { get; }
    public static Option<bool> IgnoreConfigOption { get; }
    public static Option<bool> NoGitignoreOption { get; }
    public static Option<bool> YesOption { get; }
    public static Option<bool> NoCertOption { get; }
    public static Option<bool> ConfigOnlyOption { get; }

    static InitCommand()
    {
        BaseDirectoryArgument = new Argument<DirectoryInfo>("base-directory")
        {
            Description = "Base/root directory for the winapp workspace, for consumption or installation.",
            Arity = ArgumentArity.ZeroOrOne
        };
        BaseDirectoryArgument.AcceptExistingOnly();
        ConfigDirOption = new Option<DirectoryInfo>("--config-dir")
        {
            Description = "Directory to read/store configuration (default: current directory)"
        };
        ConfigDirOption.AcceptExistingOnly();
        PrereleaseOption = new Option<bool>("--prerelease")
        {
            Description = "Include prerelease packages from NuGet"
        };
        IgnoreConfigOption = new Option<bool>("--ignore-config", "--no-config")
        {
            Description = "Don't use configuration file for version management"
        };
        NoGitignoreOption = new Option<bool>("--no-gitignore")
        {
            Description = "Don't update .gitignore file"
        };
        YesOption = new Option<bool>("--yes", "--no-prompt")
        {
            Description = "Assume yes to all prompts"
        };
        NoCertOption = new Option<bool>("--no-cert")
        {
            Description = "Skip development certificate generation"
        };
        ConfigOnlyOption = new Option<bool>("--config-only")
        {
            Description = "Only handle configuration file operations (create if missing, validate if exists). Skip package installation, certificate generation, and other workspace setup steps."
        };
    }

    public InitCommand() : base("init", "Initializes a directory with required assets (manifest, certs, libraries) for building a modern Windows app. ")
    {
        Arguments.Add(BaseDirectoryArgument);
        Options.Add(ConfigDirOption);
        Options.Add(PrereleaseOption);
        Options.Add(IgnoreConfigOption);
        Options.Add(NoGitignoreOption);
        Options.Add(YesOption);
        Options.Add(NoCertOption);
        Options.Add(ConfigOnlyOption);
    }

    public class Handler(IWorkspaceSetupService workspaceSetupService, ICurrentDirectoryProvider currentDirectoryProvider) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var baseDirectory = parseResult.GetValue(BaseDirectoryArgument);
            var configDir = parseResult.GetValue(ConfigDirOption) ?? currentDirectoryProvider.GetCurrentDirectoryInfo();
            var prerelease = parseResult.GetValue(PrereleaseOption);
            var ignoreConfig = parseResult.GetValue(IgnoreConfigOption);
            var noGitignore = parseResult.GetValue(NoGitignoreOption);
            var assumeYes = parseResult.GetValue(YesOption);
            var noCert = parseResult.GetValue(NoCertOption);
            var configOnly = parseResult.GetValue(ConfigOnlyOption);

            var options = new WorkspaceSetupOptions
            {
                BaseDirectory = baseDirectory ?? currentDirectoryProvider.GetCurrentDirectoryInfo(),
                ConfigDir = configDir,
                IncludeExperimental = prerelease,
                IgnoreConfig = ignoreConfig,
                NoGitignore = noGitignore,
                AssumeYes = assumeYes,
                RequireExistingConfig = false,
                ForceLatestBuildTools = true,
                NoCert = noCert,
                ConfigOnly = configOnly
            };

            return await workspaceSetupService.SetupWorkspaceAsync(options, cancellationToken);
        }
    }
}
