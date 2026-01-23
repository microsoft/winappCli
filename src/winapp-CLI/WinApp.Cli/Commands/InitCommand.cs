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
    public static Option<SdkInstallMode?> SetupSdksOption { get; }
    public static Option<bool> IgnoreConfigOption { get; }
    public static Option<bool> NoGitignoreOption { get; }
    public static Option<bool> UseDefaults { get; }
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
        SetupSdksOption = new Option<SdkInstallMode?>("--setup-sdks")
        {
            Description = "SDK installation mode: 'stable' (default), 'preview', 'experimental', or 'none' (skip SDK installation)",
            HelpName = "stable|preview|experimental|none"
        };
        IgnoreConfigOption = new Option<bool>("--ignore-config", "--no-config")
        {
            Description = "Don't use configuration file for version management"
        };
        NoGitignoreOption = new Option<bool>("--no-gitignore")
        {
            Description = "Don't update .gitignore file"
        };
        UseDefaults = new Option<bool>("--use-defaults", "--no-prompt")
        {
            Description = "Do not prompt, and use default of all prompts"
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

    public InitCommand() : base("init", "Set up a new Windows app project. Interactive by default, use --use-defaults to skip prompts. Creates appxmanifest.xml with default assets, generates a devcert.pfx development certificate, creates winapp.yaml for version management, and downloads Windows SDK and Windows App SDK packages and generates projections. This is the recommended way to start - it combines what 'manifest generate' and 'cert generate' do individually. For existing projects, use 'restore' to reinstall packages from winapp.yaml.")
    {
        Arguments.Add(BaseDirectoryArgument);
        Options.Add(ConfigDirOption);
        Options.Add(SetupSdksOption);
        Options.Add(IgnoreConfigOption);
        Options.Add(NoGitignoreOption);
        Options.Add(UseDefaults);
        Options.Add(NoCertOption);
        Options.Add(ConfigOnlyOption);
    }

    public class Handler(IWorkspaceSetupService workspaceSetupService, ICurrentDirectoryProvider currentDirectoryProvider) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var baseDirectory = parseResult.GetValue(BaseDirectoryArgument);
            var configDir = parseResult.GetValue(ConfigDirOption) ?? currentDirectoryProvider.GetCurrentDirectoryInfo();
            var setupSdks = parseResult.GetValue(SetupSdksOption);
            var ignoreConfig = parseResult.GetValue(IgnoreConfigOption);
            var noGitignore = parseResult.GetValue(NoGitignoreOption);
            var useDefaults = parseResult.GetValue(UseDefaults);
            var noCert = parseResult.GetValue(NoCertOption);
            var configOnly = parseResult.GetValue(ConfigOnlyOption);

            var options = new WorkspaceSetupOptions
            {
                BaseDirectory = baseDirectory ?? currentDirectoryProvider.GetCurrentDirectoryInfo(),
                ConfigDir = configDir,
                SdkInstallMode = setupSdks,
                IgnoreConfig = ignoreConfig,
                NoGitignore = noGitignore,
                UseDefaults = useDefaults,
                RequireExistingConfig = false,
                ForceLatestBuildTools = true,
                NoCert = noCert,
                ConfigOnly = configOnly
            };

            return await workspaceSetupService.SetupWorkspaceAsync(options, cancellationToken);
        }
    }
}
