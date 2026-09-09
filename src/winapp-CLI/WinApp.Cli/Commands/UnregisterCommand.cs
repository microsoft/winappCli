// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal partial class UnregisterCommand : Command, IShortDescription, ITargetAwareCommand
{
    public string ShortDescription => "Unregister a sideloaded development package.";

    public static Option<FileInfo> ManifestOption { get; }
    public static Option<bool> ForceOption { get; }


    static UnregisterCommand()
    {
        ManifestOption = new Option<FileInfo>("--manifest")
        {
            Description = "Path to the Package.appxmanifest (default: auto-detect from current directory)"
        };
        ManifestOption.AcceptExistingOnly();

        ForceOption = new Option<bool>("--force")
        {
            Description = "Skip the install-location directory check and unregister even if the package was registered from a different project tree"
        };

    }

    public UnregisterCommand() : base("unregister", "Unregisters a sideloaded development package. Only removes packages registered in development mode (e.g., via 'winapp run' or 'create-debug-identity').")
    {
        Options.Add(ManifestOption);
        Options.Add(ForceOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public partial class Handler(
        IPackageRegistrationService packageRegistrationService,
        IAppLauncherService appLauncherService,
        ICurrentDirectoryProvider currentDirectoryProvider,
        ExecutionTargetOrchestrator orchestrator,
        GuestApplicationRunner guestApplicationRunner,
        IAnsiConsole ansiConsole,
        ILogger<UnregisterCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var manifest = parseResult.GetValue(ManifestOption);
            var force = parseResult.GetValue(ForceOption);
            var target = ExecutionTargetSelection.Resolve(parseResult);
            var isJson = parseResult.GetValue(WinAppRootCommand.JsonOption);

            // Resolve manifest
            FileInfo resolvedManifest;
            if (manifest != null && manifest.Exists)
            {
                resolvedManifest = manifest;
            }
            else
            {
                resolvedManifest = ManifestHelper.FindManifest(currentDirectoryProvider.GetCurrentDirectory());
                if (!resolvedManifest.Exists)
                {
                    var message = "No manifest found in the current directory. Use --manifest to specify the path.";
                    if (isJson)
                    {
                        PrintJson([], [], message);
                    }
                    else
                    {
                        logger.LogError("{UISymbol} {Message}", UiSymbols.Error, message);
                    }
                    return 1;
                }
            }

            // Parse package name from manifest
            var manifestContent = await File.ReadAllTextAsync(resolvedManifest.FullName, Encoding.UTF8, cancellationToken);
            var identity = MsixService.ParseAppxManifestAsync(manifestContent);
            var packageName = identity.PackageName;

            // A selected target never touches this machine's registrations, and this machine's
            // state never decides what happens on that target.
            if (!target.IsLocal)
            {
                if (force)
                {
                    return TargetOutput.RejectOptions(
                        ansiConsole,
                        isJson,
                        new ExecutionTargetErrorInfo
                        {
                            Code = ExecutionTargetErrorCodes.TargetInvalidArguments,
                            Message =
                                "'--force' is not supported with '--on'. Target packages are removed only when winapp can prove ownership.",
                            UserAction = "Retry without '--force'.",
                        });
                }

                return await UnregisterOnTargetAsync(identity, isJson, cancellationToken);
            }

            // Search for both the exact name and the .debug variant
            var namesToCheck = new[] { packageName, $"{packageName}.debug" };
            var cwd = Path.GetFullPath(currentDirectoryProvider.GetCurrentDirectory());

            var unregistered = new List<string>();
            var skipped = new List<string>();

            foreach (var name in namesToCheck)
            {
                var packages = packageRegistrationService.FindDevPackages(name);

                foreach (var pkg in packages)
                {
                    if (!pkg.IsDevelopmentMode)
                    {
                        if (!isJson)
                        {
                            logger.LogInformation("{UISymbol} {FullName}: installed via MSIX/Store, skipping.", UiSymbols.Note, pkg.FullName);
                        }
                        skipped.Add(pkg.FullName);
                        continue;
                    }

                    // Check install location is under current directory tree
                    if (!force && !string.IsNullOrEmpty(pkg.InstallLocation))
                    {
                        var installPath = Path.GetFullPath(pkg.InstallLocation);
                        if (!installPath.StartsWith(cwd, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!isJson)
                            {
                                logger.LogWarning("{UISymbol} {FullName}: registered from a different project tree ({Location}). Use --force to override.",
                                    UiSymbols.Warning, pkg.FullName, pkg.InstallLocation);
                            }
                            skipped.Add(pkg.FullName);
                            continue;
                        }
                    }

                    // Explicit unregister command — remove package and its data
                    await packageRegistrationService.UnregisterAsync(name, preserveAppData: false, cancellationToken);

                    if (!isJson)
                    {
                        ansiConsole.MarkupLineInterpolated($"{UiSymbols.Check} Unregistered {pkg.FullName}");
                    }
                    unregistered.Add(pkg.FullName);
                }
            }

            if (isJson)
            {
                PrintJson(unregistered, skipped, errorMessage: null);
            }
            else if (unregistered.Count == 0 && skipped.Count == 0)
            {
                logger.LogInformation("{UISymbol} No dev-registered package found for '{PackageName}'.", UiSymbols.Note, packageName);
            }

            return 0;
        }

        private void PrintJson(List<string> unregistered, List<string> skipped, string? errorMessage)
        {
            var result = new UnregisterResult
            {
                Unregistered = unregistered.Count > 0 ? unregistered : null,
                Skipped = skipped.Count > 0 ? skipped : null,
                Error = errorMessage
            };

            var json = JsonSerializer.Serialize(result, UnregisterJsonContext.Default.UnregisterResult);

            // Written straight to the underlying stdout writer rather than through Spectre's
            // word-wrapping layer, which injects CR/LF *inside* JSON string values once a message
            // exceeds the console width and produces a document strict parsers reject. Matches how
            // run/cert/ui emit their machine-readable output.
            ansiConsole.Profile.Out.Writer.WriteLine(json);
        }
    }
}

internal sealed class UnregisterResult
{
    public List<string>? Unregistered { get; set; }
    public List<string>? Skipped { get; set; }
    public string? Error { get; set; }
}

[JsonSerializable(typeof(UnregisterResult))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class UnregisterJsonContext : JsonSerializerContext;
