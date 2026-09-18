// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal partial class UnregisterCommand : Command, IShortDescription, ITargetAwareCommand
{
    public string ShortDescription => "Unregister a sideloaded development package.";

    public static Argument<FileInfo> InputArgument { get; }
    public static Option<FileInfo> ManifestOption { get; }
    public static Option<bool> ForceOption { get; }
    public static Option<bool> PruneOption { get; }
    public static Option<string[]> PropertyOption { get; }
    public static Option<DirectoryInfo> OutputAppXDirectoryOption { get; }
    public static Option<string> ConfigurationOption { get; }
    public static Option<string> ArchOption { get; }
    public static Option<string> RuntimeOption { get; }


    static UnregisterCommand()
    {
        InputArgument = new Argument<FileInfo>("input")
        {
            Description = "App directory, .csproj, .sln, .slnx, or .cs file whose development package should be unregistered. Resolves the same app as 'winapp run' without building it. Recorded normal and unique identities are discovered automatically. Omit to use the current directory, --manifest, or --output-appx-directory. Cannot be combined with --manifest.",
            Arity = ArgumentArity.ZeroOrOne
        };

        ManifestOption = new Option<FileInfo>("--manifest")
        {
            Description = "Path to the Package.appxmanifest (default: auto-detect from current directory)"
        };

        ForceOption = new Option<bool>("--force")
        {
            Description = "Skip the install-location directory check for legacy registrations without managed ownership metadata. Never bypasses managed ownership or live-registration checks. Legacy candidates are matched by Identity/@Name alone, so a same-named legacy package from another publisher can also be removed with its app data. With --prune, skips the confirmation prompt."
        };

        PruneOption = new Option<bool>("--prune")
        {
            Description = "Remove legacy development-mode registrations whose files are gone. Lists what it found and asks before removing; pass --force to skip the prompt. Managed deployments require an app input or --output-appx-directory instead, so ownership can be verified. Cannot be combined with an input or --manifest."
        };

        PropertyOption = new Option<string[]>("--property", "-p")
        {
            Description = "MSBuild property (Name=Value) used to classify project/solution inputs or resolve a legacy .cs app's identity. Repeatable. For legacy .cs registrations, pass the same identity-affecting properties the run used (e.g. -p WinAppPackageName=...). Managed registrations are selected by recorded app ownership.",
            // ZeroOrMore, not OneOrMore: OneOrMore lets System.CommandLine reject a valueless -p with
            // plain-text help before the handler runs, which breaks the --json contract scripts rely on.
            // The handler detects the missing value itself and reports it in the requested format.
            Arity = ArgumentArity.ZeroOrMore,
            AllowMultipleArgumentsPerToken = false
        };

        OutputAppXDirectoryOption = new Option<DirectoryInfo>("--output-appx-directory")
        {
            Description = "Select the AppX layout to unregister, including when several deployments belong to the same app. A recorded layout can be selected without the original source or manifest. For --on, pass the host layout used by the run."
        };

        ConfigurationOption = new Option<string>("--configuration", "-c")
        {
            Description = "Configuration used to classify project/solution inputs or resolve a legacy .cs app's identity (default: Debug). Pass the same configuration the run used."
        };

        ArchOption = new Option<string>("--arch")
        {
            Description = "Target architecture (x64, arm64, x86) used to classify project/solution inputs or resolve a legacy .cs app's identity (default: the current process architecture). Pass the same architecture the run used."
        };

        RuntimeOption = new Option<string>("--runtime", "-r")
        {
            Description = "Target .NET runtime identifier (e.g. win-x64) used to classify project/solution inputs or resolve a legacy .cs app's identity. Only its architecture is used, and it overrides --arch."
        };

    }

    public UnregisterCommand() : base("unregister", "Unregisters a sideloaded development package. Only removes packages registered in development mode (e.g., via 'winapp run' or 'create-debug-identity').")
    {
        Arguments.Add(InputArgument);
        Options.Add(ManifestOption);
        Options.Add(ForceOption);
        Options.Add(PruneOption);
        Options.Add(PropertyOption);
        Options.Add(OutputAppXDirectoryOption);
        Options.Add(ConfigurationOption);
        Options.Add(ArchOption);
        Options.Add(RuntimeOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public partial class Handler(
        IPackageRegistrationService packageRegistrationService,
        IAppLauncherService appLauncherService,
        IProjectRunService projectRunService,
        ICurrentDirectoryProvider currentDirectoryProvider,
        ExecutionTargetOrchestrator orchestrator,
        GuestApplicationRunner guestApplicationRunner,
        IAnsiConsole ansiConsole,
        ILogger<UnregisterCommand> logger,
        IWinappDirectoryService winappDirectoryService,
        IDeploymentStateStore deploymentStateStore) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            try
            {
                return await InvokeCoreAsync(parseResult, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return FailWith(ex.Message, parseResult.GetValue(WinAppRootCommand.JsonOption));
            }
        }

        private async Task<int> InvokeCoreAsync(ParseResult parseResult, CancellationToken cancellationToken)
        {
            var input = parseResult.GetValue(InputArgument);
            var manifest = parseResult.GetValue(ManifestOption);
            var force = parseResult.GetValue(ForceOption);
            var target = ExecutionTargetSelection.Resolve(parseResult);
            var prune = parseResult.GetValue(PruneOption);
            var properties = parseResult.GetValue(PropertyOption) ?? [];
            var outputAppXDirectory = parseResult.GetValue(OutputAppXDirectoryOption);
            var configuration = parseResult.GetValue(ConfigurationOption);
            var archOption = parseResult.GetValue(ArchOption);
            var runtimeOption = parseResult.GetValue(RuntimeOption);
            var isJson = parseResult.GetValue(WinAppRootCommand.JsonOption);

            // Reject a valueless -p/--property here rather than letting System.CommandLine's arity check
            // do it, which would print plain-text help and bypass the --json envelope. There is one
            // identifier token per '-p' occurrence, so more identifiers than value tokens means at least
            // one '-p' arrived without its argument. Mirrors RunCommand.
            if (parseResult.GetResult(PropertyOption) is OptionResult propertyResult &&
                propertyResult.IdentifierTokenCount > propertyResult.Tokens.Count)
            {
                return FailWith("A --property/-p option was provided without a value. Expected Name=Value (for example: -p WinAppPackageName=com.contoso.app).", isJson);
            }

            if (prune)
            {
                if (!target.IsLocal)
                {
                    return TargetOutput.RejectOptions(
                        ansiConsole,
                        isJson,
                        new ExecutionTargetErrorInfo
                        {
                            Code = ExecutionTargetErrorCodes.TargetInvalidArguments,
                            Message = "'--prune' is not supported with '--on'. It sweeps registrations on the selected machine rather than removing one proven deployment.",
                            UserAction = "Run '--prune' locally without '--on', or name one target package with --manifest.",
                        });
                }

                if (input != null || manifest != null)
                {
                    return FailWith(
                        "--prune sweeps every dev registration whose files are gone, so it cannot be combined with an input or --manifest. Run it on its own.",
                        isJson);
                }

                if (properties.Length > 0 || outputAppXDirectory != null || configuration != null
                    || archOption != null || runtimeOption != null)
                {
                    return FailWith(
                        "--prune sweeps by registration state rather than by identity or layout, so it cannot be combined with --property, --configuration, --arch, --runtime, or --output-appx-directory.",
                        isJson);
                }

                return await PruneOrphanedRegistrationsAsync(force, isJson, cancellationToken);
            }

            if (!target.IsLocal && force)
            {
                return TargetOutput.RejectOptions(
                    ansiConsole,
                    isJson,
                    new ExecutionTargetErrorInfo
                    {
                        Code = ExecutionTargetErrorCodes.TargetInvalidArguments,
                        Message = "'--force' is not supported with '--on'. Target packages are removed only when winapp can prove ownership.",
                        UserAction = "Retry without '--force'.",
                    });
            }

            // An input and --manifest are two different ways to name a package, and they can name
            // DIFFERENT ones. Silently preferring either would let the command remove a registration —
            // and its app data — that the user did not ask for, so the ambiguity is rejected instead.
            if (input != null && manifest != null)
            {
                return FailWith(
                    $"'{input.Name}' and --manifest name the package two different ways, and they can resolve to different packages. Pass one or the other.",
                    isJson);
            }

            // A manifest states its identity; classification inputs only apply to an app input.
            if ((properties.Length > 0 || configuration != null || archOption != null || runtimeOption != null)
                && input == null)
            {
                return FailWith(
                    "--property, --configuration, --arch and --runtime require an app input (directory, .csproj, solution, or .cs file). A manifest already declares its identity.",
                    isJson);
            }

            if (MsBuildPropertyValidator.Validate(properties) is { } propertyError)
            {
                return FailWith(propertyError, isJson);
            }

            // Existence is checked here rather than with AcceptExistingOnly(), which System.CommandLine
            // enforces during parsing — before the handler runs, so it prints the plain-text help page and
            // bypasses the --json contract entirely. A mistyped or already-deleted path is exactly the case
            // cleanup automation has to parse. RunCommand's input and --manifest dropped that validator for
            // the same reason.
            if (input != null && !input.Exists && !Directory.Exists(input.FullName) && outputAppXDirectory == null)
            {
                return FailWith($"'{input.FullName}' does not exist.", isJson);
            }

            if (manifest != null && !manifest.Exists)
            {
                return FailWith($"'{manifest.FullName}' does not exist.", isJson);
            }

            if (!RunCommand.Handler.TryResolveArchitecture(archOption, runtimeOption, out var architecture, out var archError))
            {
                return FailWith(archError!, isJson);
            }

            RunInputResolution? inputResolution = null;
            string? canonicalOwner = null;
            if (input != null || (manifest == null && outputAppXDirectory == null))
            {
                FileSystemInfo appInput = input == null
                    ? new DirectoryInfo(currentDirectoryProvider.GetCurrentDirectory())
                    : Directory.Exists(input.FullName) ? new DirectoryInfo(input.FullName) : input;
                if (appInput is FileInfo source &&
                    source.Extension.ToLowerInvariant() is not (".cs" or ".csproj" or ".sln" or ".slnx"))
                {
                    return FailWith($"'{source.Name}' is not a supported app input. Pass a directory, .csproj, .sln, .slnx, or .cs file, or use --manifest.", isJson);
                }

                if (!appInput.Exists && outputAppXDirectory != null)
                {
                    // A deleted solution cannot identify which project it selected. Layout-only cleanup
                    // does not need that source; a named .cs/.csproj still constrains the recorded owner.
                    if (appInput.Extension.ToLowerInvariant() is ".sln" or ".slnx")
                    {
                        return FailWith("The solution no longer exists. Omit the input and pass --output-appx-directory to select its recorded layout.", isJson);
                    }
                    canonicalOwner = DevelopmentIdentityHelper.CanonicalizePath(appInput.FullName);
                }
                else
                {
                    inputResolution = await projectRunService.ResolveInputAsync(
                        appInput, cancellationToken,
                        classificationInputs: new ProjectClassificationInputs(configuration ?? "Debug", architecture, null, properties));
                    canonicalOwner = DevelopmentIdentityHelper.CanonicalizePath(
                        inputResolution.SingleFile?.FullName ?? inputResolution.Csproj?.FullName ?? inputResolution.ProjectDirectory.FullName);
                }
            }

            var stateRoot = winappDirectoryService.GetGlobalWinappDirectory();
            if (outputAppXDirectory is not null)
            {
                outputAppXDirectory = new DirectoryInfo(DevelopmentIdentityHelper.ResolvePathForIo(outputAppXDirectory.FullName));
            }
            if (target.IsLocal)
            {
                var selected = outputAppXDirectory != null
                    ? DevelopmentRegistrationStore.Read(outputAppXDirectory)
                    : null;
                if (selected == null && outputAppXDirectory != null)
                {
                    selected = DevelopmentRegistrationStore.FindAll(stateRoot)
                        .SingleOrDefault(record => SamePath(record.Identity.LayoutPath, outputAppXDirectory.FullName));
                }
                if (selected != null)
                {
                    if (canonicalOwner != null && !SamePath(selected.Identity.OwnerPath, canonicalOwner))
                    {
                        return FailWith("The selected layout belongs to a different app. Pass its own input or omit the input to select only --output-appx-directory.", isJson);
                    }
                    if (manifest != null && !MatchesManifest(selected.Identity, await ReadManifestIdentityAsync(manifest, cancellationToken)))
                    {
                        return FailWith("The selected layout's recorded identity does not match --manifest.", isJson);
                    }
                    return await RemoveManagedRegistrationAsync(stateRoot, selected, isJson, cancellationToken);
                }

                if (canonicalOwner != null)
                {
                    var records = DevelopmentRegistrationStore.FindByOwner(stateRoot, canonicalOwner)
                        .Where(record => outputAppXDirectory == null || SamePath(record.Identity.LayoutPath, outputAppXDirectory.FullName))
                        .ToList();
                    if (records.Count > 1)
                    {
                        return FailWith(AmbiguousLayouts(records.Select(record => record.Identity.LayoutPath)), isJson);
                    }
                    if (records.Count == 1)
                    {
                        return await RemoveManagedRegistrationAsync(stateRoot, records[0], isJson, cancellationToken);
                    }
                }
            }
            else if (manifest == null)
            {
                return await UnregisterOnTargetAsync(null, canonicalOwner, outputAppXDirectory?.FullName, null, isJson, cancellationToken);
            }

            if (manifest == null && inputResolution == null && outputAppXDirectory != null)
            {
                return ReportNoRegistration(isJson);
            }

            string packageName;
            MsixIdentityResult? targetIdentity = null;
            FileInfo? selectedManifest = null;

            // A registration legitimately belongs to more than one directory: `run` copies an explicit
            // --manifest into the input's own AppX layout, and --output-appx-directory puts that layout
            // somewhere else entirely. Collecting every directory the caller has named — rather than
            // picking one — is what keeps the guard strict without rejecting valid registrations.
            var trustedRoots = new List<string>();

            if (inputResolution?.Mode == WinAppRunMode.SingleFile)
            {
                var identityInputs = new SingleFileIdentityInputs(
                    configuration ?? "Debug",
                    architecture,
                    ArchitectureIsExplicit: !string.IsNullOrWhiteSpace(archOption) || !string.IsNullOrWhiteSpace(runtimeOption),
                    properties);

                SingleFileIdentityResolution resolved;
                try
                {
                    resolved = await projectRunService.ResolveSingleFileIdentityAsync(inputResolution.SingleFile!, identityInputs, cancellationToken);
                }
                catch (ProjectRunException ex)
                {
                    return FailWith(ex.Message, isJson);
                }

                if (resolved.Packaging == ProjectPackaging.Unpackaged)
                {
                    // Nothing was ever registered, so reporting "no package found" would read as a failure
                    // to find something that should exist.
                    if (!isJson)
                    {
                        logger.LogInformation(
                            "{UISymbol} '{File}' is an unpackaged app (WindowsPackageType=None), so it has no registration to remove.",
                            UiSymbols.Note, inputResolution.SingleFile!.Name);
                    }
                    else
                    {
                        PrintJson([], [], errorMessage: null);
                    }

                    return 0;
                }

                packageName = resolved.PackageName;

                // A file-based app's layout lives in the SDK's own %TEMP%\dotnet\runfile\<stem>-<hash>
                // directory, never under the user's working directory. That is strictly more precise than
                // a directory-tree heuristic: it confirms the registration came from THIS .cs rather than
                // a same-named app elsewhere — the collision `winapp run` warns about.
                if (!string.IsNullOrEmpty(resolved.BuildRootDirectory))
                {
                    trustedRoots.Add(resolved.BuildRootDirectory);
                }
            }
            else
            {
                // Resolve manifest
                FileInfo resolvedManifest;
                if (manifest != null && manifest.Exists)
                {
                    resolvedManifest = manifest;
                }
                else
                {
                    resolvedManifest = ManifestHelper.FindManifest(
                        inputResolution?.ProjectDirectory.FullName ?? currentDirectoryProvider.GetCurrentDirectory());
                    if (!resolvedManifest.Exists)
                    {
                        if (input != null)
                        {
                            return ReportNoRegistration(isJson);
                        }
                        return FailWith(
                            "No manifest found and no managed registration was recorded for this app. Pass an app input, --manifest, or --output-appx-directory.",
                            isJson);
                    }
                }

                // Parse package name from manifest
                selectedManifest = resolvedManifest;
                var identity = await ReadManifestIdentityAsync(resolvedManifest, cancellationToken);
                targetIdentity = identity;
                packageName = identity.PackageName;

                // Trust BOTH the manifest's own directory and the current directory. They are the same
                // folder for an auto-detected manifest, and diverge for an explicit --manifest — where
                // either can legitimately hold the registered layout, because `run` copies an explicit
                // manifest into the INPUT's AppX directory rather than registering from the manifest's
                // own folder. Trusting only the manifest directory would refuse to clean up
                // `run . --manifest C:\shared\custom.appxmanifest`, whose layout is under the project.
                if (resolvedManifest.DirectoryName is { Length: > 0 } manifestDirectory)
                {
                    trustedRoots.Add(manifestDirectory);
                }

                trustedRoots.Add(inputResolution?.ProjectDirectory.FullName ?? currentDirectoryProvider.GetCurrentDirectory());
            }

            // --output-appx-directory relocates the registered layout, so the caller has to be able to
            // name it here too; nothing on the package records which run option produced it.
            if (outputAppXDirectory != null)
            {
                trustedRoots.Add(outputAppXDirectory.FullName);
            }

            // A selected target never touches this machine's registrations, and this machine's
            // state never decides what happens on that target.
            if (!target.IsLocal)
            {
                return await UnregisterOnTargetAsync(
                    targetIdentity, canonicalOwner, outputAppXDirectory?.FullName, selectedManifest,
                    isJson,
                    cancellationToken);
            }

            var managedRegistrations = DevelopmentRegistrationStore.FindAll(stateRoot);
            if (selectedManifest != null && targetIdentity != null)
            {
                var manifestDirectory = new DirectoryInfo(
                    DevelopmentIdentityHelper.ResolvePathForIo(selectedManifest.Directory!.FullName));
                var adjacent = DevelopmentRegistrationStore.Read(manifestDirectory);
                var scoped = managedRegistrations
                    .Concat(adjacent == null ? [] : new[] { adjacent })
                    .Where(record => outputAppXDirectory != null
                            ? SamePath(record.Identity.LayoutPath, outputAppXDirectory.FullName)
                            : BelongsToManifest(record.Identity, selectedManifest))
                    .DistinctBy(record => record.Identity.LayoutPath, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var matches = scoped.Where(record => MatchesManifest(record.Identity, targetIdentity)).ToList();
                if (matches.Count > 1)
                {
                    return FailWith(AmbiguousLayouts(matches.Select(record => record.Identity.LayoutPath)), isJson);
                }
                if (matches.Count == 1)
                {
                    return await RemoveManagedRegistrationAsync(stateRoot, matches[0], isJson, cancellationToken);
                }
                if (scoped.Count > 0)
                {
                    return FailWith("The recorded deployment at this app's path does not match --manifest. Select its app input or --output-appx-directory instead.", isJson);
                }
            }

            // Search for both the exact name and the .debug variant
            var namesToCheck = new[] { packageName, $"{packageName}.debug" };

            var unregistered = new List<string>();
            var skipped = new List<string>();

            // Tracked apart from `skipped`: a safety skip is the command working as intended, but a
            // removal Windows refused is a failure the caller must see in the exit code.
            var removalFailed = false;

            foreach (var name in namesToCheck)
            {
                var packages = packageRegistrationService.FindDevPackages(name);

                foreach (var pkg in packages)
                {
                    // Legacy name/.debug matching must never become a back door into an owned run,
                    // including when --force bypasses the legacy directory-tree guard.
                    if (FindManagedRegistration(managedRegistrations, pkg) != null)
                    {
                        skipped.Add(pkg.FullName);
                        removalFailed = true;
                        if (!isJson)
                        {
                            logger.LogError("{UISymbol} {FullName}: this is a managed deployment. Select its app input or --output-appx-directory; --force cannot override ownership.", UiSymbols.Error, pkg.FullName);
                        }
                        continue;
                    }

                    if (!pkg.IsDevelopmentMode)
                    {
                        if (!isJson)
                        {
                            logger.LogInformation("{UISymbol} {FullName}: installed via MSIX/Store, skipping.", UiSymbols.Note, pkg.FullName);
                        }
                        skipped.Add(pkg.FullName);
                        continue;
                    }

                    // Confirm the install location really sits under the tree the caller identified.
                    // Segment-aware on purpose: a plain string prefix treats SIBLING directories as the
                    // same tree, so a package installed at 'C:\apps\counter-old\AppX' would pass a check
                    // rooted at 'C:\apps\counter' and be removed along with its app data.
                    //
                    // An UNKNOWN location (Windows could not resolve it, typically because the files were
                    // deleted) or an unresolved root counts as unverifiable, NOT as verified. Identity
                    // alone is not proof of ownership: a default single-file identity carries a hash of the
                    // file's path, but an explicit WinAppPackageName or an authored manifest's Identity/@Name
                    // is used verbatim, so two apps under different roots can deliberately share one. Skipping
                    // the check there would let `winapp unregister B\counter.cs` delete A's registration and
                    // its application data. `--prune` is the supported way to clear registrations whose files
                    // are gone, and it preserves that data.
                    if (!force)
                    {
                        if (string.IsNullOrEmpty(pkg.InstallLocation) || trustedRoots.Count == 0)
                        {
                            if (!isJson)
                            {
                                logger.LogWarning("{UISymbol} {FullName}: cannot confirm this registration belongs here (its install location is unavailable). Use --force to remove it anyway, or 'winapp unregister --prune' to clear registrations whose files are gone.",
                                    UiSymbols.Warning, pkg.FullName);
                            }
                            skipped.Add(pkg.FullName);
                            continue;
                        }

                        if (!trustedRoots.Any(root => MsixService.IsPathInsideDirectory(pkg.InstallLocation, root)))
                        {
                            if (!isJson)
                            {
                                logger.LogWarning("{UISymbol} {FullName}: registered from a different project tree ({Location}). Use --force to override, or --output-appx-directory to name the layout it was registered from.",
                                    UiSymbols.Warning, pkg.FullName, pkg.InstallLocation);
                            }
                            skipped.Add(pkg.FullName);
                            continue;
                        }
                    }

                    // Remove the package that was just vetted, BY FULL NAME. The name-wide overload
                    // removes every package sharing this identity name, which would defeat the per-package
                    // checks above: a same-named package that this loop deliberately skipped — because it
                    // is Store-installed or lives in another tree — would be deleted anyway, along with
                    // its application data.
                    var removed = await packageRegistrationService.UnregisterByFullNameAsync(pkg.FullName, preserveAppData: false, cancellationToken);
                    if (!removed)
                    {
                        // Windows reports a refused removal as error text rather than an exception, so
                        // reporting it as unregistered would tell the user a still-registered package is gone.
                        logger.LogWarning("{UISymbol} {FullName}: Windows refused to remove this package.", UiSymbols.Warning, pkg.FullName);
                        skipped.Add(pkg.FullName);
                        removalFailed = true;
                        continue;
                    }

                    if (!isJson)
                    {
                        ansiConsole.MarkupLineInterpolated($"{UiSymbols.Check} Unregistered {pkg.FullName}");
                    }
                    unregistered.Add(pkg.FullName);
                }
            }

            if (isJson)
            {
                PrintJson(unregistered, skipped, errorMessage: removalFailed
                    ? "One or more registrations could not be removed. For managed deployments, select the app input or --output-appx-directory; --force cannot override ownership."
                    : null);
            }
            else if (unregistered.Count == 0 && skipped.Count == 0)
            {
                logger.LogInformation("{UISymbol} No dev-registered package found for '{PackageName}'.", UiSymbols.Note, packageName);
            }

            // A package the user explicitly named and that Windows then refused to remove is a failure,
            // even though other packages may have been removed: automation must not read success and
            // carry on as though the registration were gone. A safety skip is NOT a failure — that is the
            // guard doing its job, and it already tells the user to pass --force.
            return removalFailed ? 1 : 0;

        }

        private static bool SamePath(string left, string right) =>
            string.Equals(DevelopmentIdentityHelper.CanonicalizePath(left),
                DevelopmentIdentityHelper.CanonicalizePath(right), StringComparison.OrdinalIgnoreCase);

        private static bool MatchesManifest(DevelopmentIdentity identity, MsixIdentityResult manifest) =>
            string.Equals(identity.Publisher, manifest.Publisher, StringComparison.Ordinal)
            && (string.Equals(identity.OriginalPackageName, manifest.PackageName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(identity.EffectivePackageName, manifest.PackageName, StringComparison.OrdinalIgnoreCase));

        private static bool BelongsToManifest(DevelopmentIdentity identity, FileInfo manifest)
        {
            var owner = identity.OwnerPath;
            var ownerDirectory = Path.GetExtension(owner).ToLowerInvariant() is ".cs" or ".csproj"
                ? Path.GetDirectoryName(owner)!
                : owner;
            return SamePath(manifest.DirectoryName!, ownerDirectory)
                || SamePath(manifest.DirectoryName!, identity.LayoutPath);
        }

        private static string AmbiguousLayouts(IEnumerable<string> layouts) =>
            "More than one managed deployment matches this app. Pass --output-appx-directory to select one layout: "
            + string.Join(", ", layouts.Order(StringComparer.OrdinalIgnoreCase).Select(path => $"'{path}'")) + ".";

        private static async Task<MsixIdentityResult> ReadManifestIdentityAsync(FileInfo manifest, CancellationToken cancellationToken) =>
            MsixService.ParseAppxManifestAsync(await File.ReadAllTextAsync(manifest.FullName, Encoding.UTF8, cancellationToken));

        private static DevelopmentRegistration? FindManagedRegistration(
            IReadOnlyList<DevelopmentRegistration> registrations, DevPackageInfo package)
        {
            var recorded = registrations.FirstOrDefault(record =>
                string.Equals(record.Identity.PackageFullName, package.FullName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(record.Identity.EffectivePackageName, package.Name, StringComparison.OrdinalIgnoreCase));
            return recorded ?? (string.IsNullOrWhiteSpace(package.InstallLocation)
                ? null
                : DevelopmentRegistrationStore.Read(new DirectoryInfo(package.InstallLocation)));
        }

        private async Task<int> RemoveManagedRegistrationAsync(
            DirectoryInfo stateRoot, DevelopmentRegistration registration, bool isJson, CancellationToken cancellationToken)
        {
            var removed = await DevelopmentRegistrationStore.RemoveOwnedAsync(
                packageRegistrationService, stateRoot, registration, false, cancellationToken);
            if (!removed)
            {
                return ReportNoRegistration(isJson);
            }

            if (isJson)
            {
                PrintJson([registration.Identity.PackageFullName!], [], errorMessage: null);
            }
            else
            {
                ansiConsole.MarkupLineInterpolated($"{UiSymbols.Check} Unregistered {registration.Identity.PackageFullName}");
            }
            return 0;
        }

        private int ReportNoRegistration(bool isJson)
        {
            if (isJson)
            {
                PrintJson([], [], errorMessage: null);
            }
            else
            {
                logger.LogInformation("{UISymbol} No managed package registration was found for this app.", UiSymbols.Note);
            }
            return 0;
        }

        private int FailWith(string message, bool isJson)
        {
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

        /// <summary>
        /// Removes every development-mode registration whose files are gone.
        /// </summary>
        /// <remarks>
        /// <para>
        /// These registrations can never launch — Windows keeps the identity and its Start-menu entry,
        /// but activation silently does nothing, so the entry looks broken with no way to tell what it
        /// is. They accumulate whenever a build output or project tree is deleted while the package
        /// stays registered; a file-based app is especially exposed, because its layout lives under
        /// <c>%LOCALAPPDATA%\Temp</c>, which Windows cleans on its own schedule.
        /// </para>
        /// <para>
        /// Deliberately confirms first. "The install location does not resolve" is nearly always a
        /// deleted folder, but it also describes a package registered from a disconnected network share
        /// or removable drive, which would come back when the device does. Listing the candidates lets
        /// the user notice that before anything is removed; <c>--force</c> skips the prompt, and a
        /// non-interactive run requires it rather than silently assuming consent.
        /// </para>
        /// </remarks>
        private async Task<int> PruneOrphanedRegistrationsAsync(bool force, bool isJson, CancellationToken cancellationToken)
        {
            var orphans = packageRegistrationService.FindOrphanedDevPackages();

            if (orphans.Count == 0)
            {
                if (isJson)
                {
                    PrintJson([], [], errorMessage: null);
                }
                else
                {
                    logger.LogInformation("{UISymbol} No dev registrations with missing files.", UiSymbols.Note);
                }

                return 0;
            }

            if (!isJson)
            {
                ansiConsole.MarkupLineInterpolated(
                    $"{UiSymbols.Package} {orphans.Count} dev registration(s) whose files are gone:");
                foreach (var orphan in orphans)
                {
                    ansiConsole.MarkupLineInterpolated($"  [dim]{orphan.FullName}[/]");
                }
            }

            if (!force)
            {
                if (isJson || !ansiConsole.Profile.Capabilities.Interactive)
                {
                    return FailWith(
                        $"--prune found {orphans.Count} dev registration(s) whose files are gone, but cannot prompt for confirmation here. Re-run with --force to remove them.",
                        isJson);
                }

                var confirmed = await ansiConsole.PromptAsync(
                    new ConfirmationPrompt($"Unregister {orphans.Count} package(s)?"), cancellationToken);
                if (!confirmed)
                {
                    logger.LogInformation("{UISymbol} Nothing removed.", UiSymbols.Note);
                    return 0;
                }
            }

            var unregistered = new List<string>();
            var skipped = new List<string>();
            var managedRegistrations = DevelopmentRegistrationStore.FindAll(winappDirectoryService.GetGlobalWinappDirectory());

            foreach (var orphan in orphans)
            {
                try
                {
                    if (FindManagedRegistration(managedRegistrations, orphan) != null)
                    {
                        skipped.Add(orphan.FullName);
                        if (!isJson)
                        {
                            logger.LogError("{UISymbol} {FullName}: this is a managed deployment. Select its app input or --output-appx-directory to verify ownership before removal.", UiSymbols.Error, orphan.FullName);
                        }
                        continue;
                    }

                    // By full name, not identity name: prune targets exactly the registrations it listed,
                    // so a same-named package that IS still installed from a live location is untouched.
                    //
                    // Application data is PRESERVED, unlike the explicit single-package path above.
                    // LocalState lives in %LOCALAPPDATA%\Packages\<family>, not in the install location,
                    // so "the install files are gone" is no evidence at all that the data is unwanted —
                    // and prune is a bulk, often unattended (--force) sweep over packages the user never
                    // named individually. For a file-based app the identity is stable across rebuilds, so
                    // re-running `winapp run counter.cs` reuses this family name and the app finds its
                    // settings again. Deleting a package's own explicitly-named registration is a
                    // deliberate act; deleting data for a package caught in a sweep is not.
                    var removed = await packageRegistrationService.UnregisterByFullNameAsync(orphan.FullName, preserveAppData: true, cancellationToken);
                    if (!removed)
                    {
                        // Windows reports a refused removal as error text rather than an exception, so
                        // counting it as unregistered would hand cleanup automation a false confirmation
                        // while the dead registration is still there.
                        skipped.Add(orphan.FullName);
                        continue;
                    }

                    unregistered.Add(orphan.FullName);

                    if (!isJson)
                    {
                        ansiConsole.MarkupLineInterpolated($"{UiSymbols.Check} Unregistered {orphan.FullName}");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One package that refuses to go must not abandon the rest of the sweep.
                    skipped.Add(orphan.FullName);
                    logger.LogWarning("{UISymbol} {FullName}: {Message}", UiSymbols.Warning, orphan.FullName, ex.Message);
                }
            }

            if (isJson)
            {
                PrintJson(unregistered, skipped, errorMessage: skipped.Count > 0
                    ? "Some registrations could not be removed. Managed deployments require explicit app input or --output-appx-directory selection and live ownership verification."
                    : null);
            }

            // A sweep that could not remove everything it listed must not report success: scripts would
            // carry on as though the stale registrations were gone.
            if (skipped.Count > 0)
            {
                if (!isJson)
                {
                    logger.LogError("{UISymbol} {Skipped} of {Total} registration(s) could not be removed.",
                        UiSymbols.Error, skipped.Count, orphans.Count);
                }

                return 1;
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
