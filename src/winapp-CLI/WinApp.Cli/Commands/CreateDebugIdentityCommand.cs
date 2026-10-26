// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Invocation;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class CreateDebugIdentityCommand : Command, IShortDescription
{
    public string ShortDescription => "Add package identity to an exe for debugging";

    public static Argument<FileInfo> EntryPointArgument { get; }
    public static Option<FileInfo> ManifestOption { get; }
    public static Option<bool> NoInstallOption { get; }
    public static Option<bool> KeepIdentityOption { get; }

    static CreateDebugIdentityCommand()
    {
        EntryPointArgument = new Argument<FileInfo>("entrypoint")
        {
            Description = "Path to the .exe that will need to run with identity, or entrypoint script.",
            Arity = ArgumentArity.ZeroOrOne
        };
        EntryPointArgument.AcceptExistingOnly();
        ManifestOption = new Option<FileInfo>("--manifest")
        {
            Description = "Path to the Package.appxmanifest or appxmanifest.xml"
        };
        ManifestOption.AcceptExistingOnly();
        NoInstallOption = new Option<bool>("--no-install")
        {
            Description = "Do not install the package after creation."
        };
        KeepIdentityOption = new Option<bool>("--keep-identity")
        {
            Description = "Keep the package identity from the manifest as-is, without appending '.debug' to the package name and application ID."
        };
    }

    public CreateDebugIdentityCommand() : base("create-debug-identity", "Enable package identity for debugging without creating full MSIX. Required for testing Windows APIs (push notifications, share target, etc.) during development. Example: winapp create-debug-identity ./myapp.exe. Requires Package.appxmanifest or appxmanifest.xml in current directory or passed via --manifest. Re-run after changing the manifest or Assets/.")
    {
        Arguments.Add(EntryPointArgument);
        Options.Add(ManifestOption);
        Options.Add(NoInstallOption);
        Options.Add(KeepIdentityOption);
    }

    public class Handler(IMsixService msixService, ICurrentDirectoryProvider currentDirectoryProvider, IStatusService statusService, ILogger<CreateDebugIdentityCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var entryPointPath = parseResult.GetValue(EntryPointArgument);
            var manifest = parseResult.GetValue(ManifestOption) ?? ManifestHelper.FindManifest(currentDirectoryProvider.GetCurrentDirectory());
            var noInstall = parseResult.GetValue(NoInstallOption);
            var keepIdentity = parseResult.GetValue(KeepIdentityOption);

            if (entryPointPath != null && !entryPointPath.Exists)
            {
                logger.LogError("EntryPoint/Executable not found: {EntryPointPath}", entryPointPath);
                return 1;
            }

            return await statusService.ExecuteWithStatusAsync("Creating MSIX Debug identity...", async (taskContext, cancellationToken) =>
            {
                try
                {
                    var result = await msixService.AddSparseIdentityAsync(entryPointPath?.ToString(), manifest, noInstall, keepIdentity, taskContext, cancellationToken);

                    taskContext.AddStatusMessage($"{UiSymbols.Package} Package: {result.PackageName}");
                    taskContext.AddStatusMessage($"{UiSymbols.User} Publisher: {result.Publisher}");
                    taskContext.AddStatusMessage($"{UiSymbols.Id} App ID: {result.ApplicationId}");
                }
                catch (Exception error)
                {
                    var baseEx = error.GetBaseException();
                    var message = string.IsNullOrWhiteSpace(baseEx.Message) ? error.Message : baseEx.Message;
                    if (baseEx.HResult != 0)
                    {
                        message += $" (0x{baseEx.HResult:X8})";
                    }
                    return (1, $"{UiSymbols.Error} Failed to add package identity: {message}");
                }

                return (0, "Package identity created successfully.");
            }, cancellationToken);
        }
    }
}
