// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Xml.Linq;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

internal partial class MsixService
{
    internal Func<string, FileInfo?> ResolveAliasProxy { get; set; } =
        alias => ExecutionAliasResolver.ResolveAliasPath(alias);

    internal Func<string, string?> ReadAliasOwner { get; set; } =
        path => ExecutionAliasResolver.TryGetAliasPackageFamilyName(path, out var owner) ? owner : null;

    private async Task<MsixIdentityResult> BuildOwnedLooseLayoutAsync(
        FileInfo manifest, DirectoryInfo input, DirectoryInfo layout, TaskContext taskContext,
        bool register, LayoutReconciliation reconciliation, bool clean, string? executable,
        string? runtimeArch, FileInfo? projectFile, string? framework, bool noRestore, bool selfContained,
        bool ensureExecutionAlias, PackageGraphSource? packageGraph, DevelopmentIdentityOptions? options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (register && !devModeService.IsEnabled())
        {
            throw new InvalidOperationException("Developer Mode is not enabled on this machine. Please enable Developer Mode and try again.");
        }
        options ??= new DevelopmentIdentityOptions(projectFile?.FullName ?? input.FullName, false);
        if (options.UniqueIdentity)
        {
            input = new DirectoryInfo(DevelopmentIdentityHelper.ResolvePathForIo(input.FullName));
            layout = new DirectoryInfo(DevelopmentIdentityHelper.ResolvePathForIo(layout.FullName));
            manifest = new FileInfo(DevelopmentIdentityHelper.ResolvePathForIo(manifest.FullName));
        }
        EnsureLayoutPathHasNoReparsePoint(layout);
        EnsureLayoutPathHasNoReparsePoint(input);
        if (IsPathInsideDirectory(input.FullName, layout.FullName) ||
            IsPathInsideDirectory(manifest.FullName, layout.FullName))
        {
            throw new InvalidOperationException("The output AppX directory must not contain the input or source manifest. Use a separate --output-appx-directory.");
        }

        var stateRoot = winappDirectoryService.GetGlobalWinappDirectory();
        var prior = DevelopmentRegistrationStore.Read(layout);
        var pending = DevelopmentRegistrationStore.ReadPending(layout);
        if (prior is not null && !DevelopmentRegistrationStore.SamePath(prior.Identity.OwnerPath, options.OwnerPath))
        {
            throw new InvalidOperationException($"The layout '{layout.FullName}' is owned by '{prior.Identity.OwnerPath}'. Use a separate --output-appx-directory.");
        }
        if (pending is not null && !DevelopmentRegistrationStore.SamePath(pending.Candidate.Identity.OwnerPath, options.OwnerPath))
        {
            throw new InvalidOperationException($"The layout '{layout.FullName}' has an interrupted registration for another owner. Use a separate output directory.");
        }
        var staging = new DirectoryInfo(Path.Combine(layout.Parent!.FullName, "." + layout.Name + ".winapp-stage-" + Guid.NewGuid().ToString("N")));
        try
        {
            // Build only the candidate. The materializer cannot provision or register on the host.
            await BuildLooseLayoutAsync(manifest, input, staging, taskContext,
                LayoutReconciliation.Exact, executable, projectFile, framework, noRestore,
                selfContained, packageGraph, layout, options.UniqueIdentity, stateRoot, cancellationToken);
            var stagedManifest = ResolveLayoutRegistrationManifest(staging);
            var document = AppxManifestDocument.Load(stagedManifest.FullName);
            var identity = DevelopmentIdentityHelper.Create(document, options.OwnerPath, layout.FullName, options.UniqueIdentity);
            if (options.UniqueIdentity)
            {
                document.ApplyDevelopmentIdentity(identity);
            }
            if (ensureExecutionAlias && document.GetExecutionAliases().Count == 0)
            {
                if (TryAddDefaultExecutionAlias(document, taskContext))
                {
                    var originalAlias = ExecutionAliasResolver.BuildDefaultAliasName(
                        DevelopmentIdentityHelper.ComputeFamilyName(identity.OriginalPackageName, identity.Publisher))
                        ?? throw new InvalidOperationException("Cannot derive a safe execution alias for the original package identity.");
                    var aliases = new Dictionary<string, string>(identity.Aliases, StringComparer.OrdinalIgnoreCase)
                    {
                        [originalAlias] = document.GetExecutionAliases().Single(),
                    };
                    identity = identity with { Aliases = aliases };
                }
                else if (options.UniqueIdentity)
                {
                    throw new InvalidOperationException("Cannot stage a safe execution alias for the unique identity. Declare an execution alias in the manifest before retrying.");
                }
            }
            document.Save(stagedManifest.FullName);
            if (options.UniqueIdentity)
            {
                if (File.Exists(Path.Combine(staging.FullName, "resources.pri")))
                {
                    await priService.ReindexIdentityAsync(staging, identity.OriginalPackageName, identity.EffectivePackageName, taskContext, cancellationToken);
                }
                else
                {
                    // Missing PRI is safe only for a raw image-only resource graph. Never replace
                    // compiled/localized resources with the warning-only image fallback.
                    if (document.Document.Descendants().Attributes().Any(attribute => attribute.Value.Contains("ms-resource:", StringComparison.OrdinalIgnoreCase)) ||
                        document.Document.Descendants().Any(element => !element.HasElements && element.Value.Contains("ms-resource:", StringComparison.OrdinalIgnoreCase)) ||
                        staging.EnumerateFiles("*", SearchOption.AllDirectories).Any(file =>
                            file.Extension.Equals(".xbf", StringComparison.OrdinalIgnoreCase) ||
                            file.Extension.Equals(".resw", StringComparison.OrdinalIgnoreCase) ||
                            file.Extension.Equals(".pri", StringComparison.OrdinalIgnoreCase)) ||
                        document.Document.Descendants(AppxManifestDocument.BuildNs + "Item").Any(item =>
                            string.Equals(item.Attribute("Name")?.Value, "makepri.exe", StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new InvalidOperationException("Unique identity requires the original resources.pri for compiled or localized resources. Rebuild the app before retrying.");
                    }
                    var assets = MrtAssetHelper.GetExpandedManifestReferencedFiles(stagedManifest, taskContext);
                    await priService.CreatePriConfigAsync(staging, taskContext, assets.Select(asset => asset.RelativePath), cancellationToken: cancellationToken);
                    await priService.GeneratePriFileAsync(staging, taskContext, cancellationToken: cancellationToken);
                    if (!File.Exists(Path.Combine(staging.FullName, "resources.pri")))
                    {
                        throw new InvalidOperationException("Image resource generation did not produce resources.pri. The original layout was not changed.");
                    }
                }
            }
            var expectedFullName = DevelopmentIdentityHelper.ComputeFullName(document);
            var candidate = new DevelopmentRegistration
            {
                Identity = identity with
                {
                    PackageFullName = expectedFullName,
                    Revision = checked(Math.Max(prior?.Identity.Revision ?? 0, pending?.Candidate.Identity.Revision ?? 0) + 1),
                },
                ManifestHash = DevelopmentRegistrationStore.HashManifest(staging),
            };
            ValidateCandidateDestinations(staging, layout, reconciliation, cancellationToken);
            var families = new[] { identity.PackageFamilyName, prior?.Identity.PackageFamilyName, pending?.Candidate.Identity.PackageFamilyName, pending?.Prior?.Identity.PackageFamilyName }
                .OfType<string>();
            using var familyLease = LayoutLease.AcquireFamilies(families, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (!register)
            {
                // A target must never rewrite the backing files of a live host registration.
                if (packageRegistrationService.FindPackagesAtLocation(layout.FullName).Count != 0 || pending is not null)
                {
                    throw new InvalidOperationException($"The output '{layout.FullName}' is used by a host registration or an interrupted registration. Use a separate --output-appx-directory for the target.");
                }
                PublishCandidate(staging, layout, reconciliation, taskContext, cancellationToken);
                return IdentityResult(identity);
            }

            if (options.UniqueIdentity)
            {
                VerifyEffectiveAliasesAreAvailable(document, identity.PackageFamilyName, cancellationToken);
            }
            if (pending is not null)
            {
                RecoverPendingRegistration(stateRoot, layout, pending, cancellationToken);
                prior = DevelopmentRegistrationStore.Read(layout);
            }
            var installed = ValidateRegistrationSelection(layout, candidate, prior);
            if (prior is not null)
            {
                var previousLive = DevelopmentRegistrationStore.FindExact(packageRegistrationService, prior.Identity);
                if (previousLive is not null)
                {
                    DevelopmentRegistrationStore.VerifyLive(previousLive, prior.Identity);
                    DevelopmentRegistrationStore.VerifyManifest(prior);
                }
            }
            if (prior is null && installed is not null)
            {
                // First adoption is allowed only for an exact, unambiguous existing development
                // identity, proved against its old manifest before anything is overwritten.
                var oldDocument = AppxManifestDocument.Load(Path.Combine(layout.FullName, "appxmanifest.xml"));
                if (!string.Equals(DevelopmentIdentityHelper.ComputeFullName(oldDocument), installed.FullName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("The existing layout does not prove ownership of its live package. Use a separate output directory.");
                }
                prior = candidate with { ManifestHash = DevelopmentRegistrationStore.HashManifest(layout), Identity = candidate.Identity with { Revision = 1 } };
                candidate = candidate with { Identity = candidate.Identity with { Revision = 2 } };
            }
            var skip = !clean && installed is not null && prior is not null &&
                string.Equals(prior.Identity.PackageFullName, expectedFullName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(prior.ManifestHash, candidate.ManifestHash, StringComparison.Ordinal);

            // Provisioning is local-only and follows the collision checks.
            if (!selfContained)
            {
                var packageList = await ResolveDotNetPackageListAsync(projectFile, framework, noRestore, packageGraph, cancellationToken);
                await EnsureWindowsAppRuntimeInstalledAsync(packageList, runtimeArch, taskContext, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (options.UniqueIdentity)
            {
                VerifyEffectiveAliasesAreAvailable(document, identity.PackageFamilyName, cancellationToken);
            }
            DevelopmentRegistrationStore.Begin(stateRoot, layout, new PendingDevelopmentRegistration
            {
                Prior = prior,
                Candidate = candidate,
            });

            if (!skip)
            {
                if (prior is not null)
                {
                    var preservePriorData = !clean || !string.Equals(
                        prior.Identity.PackageFamilyName, candidate.Identity.PackageFamilyName, StringComparison.OrdinalIgnoreCase);
                    await DevelopmentRegistrationStore.RemoveExactAsync(packageRegistrationService, prior.Identity, preservePriorData, cancellationToken);
                }
                else if (installed is not null)
                {
                    await DevelopmentRegistrationStore.RemoveExactAsync(packageRegistrationService, candidate.Identity, !clean, cancellationToken);
                }
            }
            PublishCandidate(staging, layout, reconciliation, taskContext, cancellationToken);
            DevelopmentRegistrationStore.VerifyManifest(candidate);
            if (!skip)
            {
                await RegisterLooseLayoutPackageAsync(new FileInfo(Path.Combine(layout.FullName, "appxmanifest.xml")), taskContext, cancellationToken);
                if (clean && installed is null)
                {
                    // A previously unregistered family may still have preserved application data.
                    // Make it observable before clearing only that family's data.
                    var cleanTarget = DevelopmentRegistrationStore.FindExact(packageRegistrationService, candidate.Identity)
                        ?? throw new InvalidOperationException("Windows did not report the package to clean. The pending registration journal has been retained.");
                    DevelopmentRegistrationStore.VerifyLive(cleanTarget, candidate.Identity);
                    await DevelopmentRegistrationStore.RemoveExactAsync(packageRegistrationService, candidate.Identity, preserveData: false, cancellationToken);
                    await RegisterLooseLayoutPackageAsync(new FileInfo(Path.Combine(layout.FullName, "appxmanifest.xml")), taskContext, cancellationToken);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var observed = DevelopmentRegistrationStore.FindExact(packageRegistrationService, candidate.Identity)
                ?? throw new InvalidOperationException("Windows did not report the exact registered package. The pending ownership journal has been retained; rerun to recover.");
            DevelopmentRegistrationStore.VerifyLive(observed, candidate.Identity);
            var liveAtLayout = packageRegistrationService.FindPackagesAtLocation(layout.FullName);
            if (liveAtLayout.Count != 1 || !string.Equals(liveAtLayout[0].FullName, observed.FullName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Another package references this layout. The pending ownership journal has been retained; use a separate output directory.");
            }
            var committed = candidate with { Identity = candidate.Identity with
            {
                PackageFullName = observed.FullName,
                PackageFamilyName = observed.PackageFamilyName!,
                LayoutPath = DevelopmentIdentityHelper.CanonicalizePath(observed.InstallLocation!),
            }};
            DevelopmentRegistrationStore.Commit(stateRoot, layout, committed);
            if (skip)
            {
                taskContext.AddDebugMessage("Package manifest and registration are unchanged; preserving capability consent.");
            }
            return IdentityResult(committed.Identity);
        }
        finally
        {
            // Only our randomly named candidate is disposable, never the user's layout.
            if (Directory.Exists(staging.FullName))
            {
                EnsureLayoutPathHasNoReparsePoint(staging);
                staging.Delete(recursive: true);
            }
        }
    }

    private static MsixIdentityResult IdentityResult(DevelopmentIdentity identity) =>
        new(identity.EffectivePackageName, identity.Publisher, identity.ApplicationId) { Identity = identity };

    private void VerifyEffectiveAliasesAreAvailable(AppxManifestDocument document, string expectedFamily, CancellationToken cancellationToken)
    {
        foreach (var alias in document.GetExecutionAliases())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var proxy = ResolveAliasProxy(alias)
                ?? throw new InvalidOperationException($"Execution alias '{alias}' cannot be safely resolved. Correct the manifest before retrying.");
            try
            {
                _ = File.GetAttributes(proxy.FullName);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"Cannot determine whether execution alias '{alias}' is available. No package was changed. Check access to '{proxy.FullName}' before retrying.", ex);
            }

            var owner = ReadAliasOwner(proxy.FullName);
            if (!string.Equals(owner, expectedFamily, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Execution alias '{alias}' is " +
                    (string.IsNullOrWhiteSpace(owner) ? "present but its package owner cannot be verified." : $"owned by package family '{owner}', not '{expectedFamily}'.") +
                    " No package was changed. Resolve the alias conflict or choose a different authored alias before retrying.");
            }
        }
    }

    private DevPackageInfo? ValidateRegistrationSelection(DirectoryInfo layout, DevelopmentRegistration candidate, DevelopmentRegistration? prior)
    {
        var identity = candidate.Identity;
        var named = packageRegistrationService.FindDevPackages(identity.EffectivePackageName);
        // Publisher strings are case-sensitive Windows identity inputs. A same-name package with
        // another publisher is not a package we may remove or silently adopt.
        foreach (var package in named)
        {
            if (prior is not null && string.Equals(package.FullName, prior.Identity.PackageFullName, StringComparison.OrdinalIgnoreCase))
            {
                DevelopmentRegistrationStore.VerifyLive(package, prior.Identity);
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(package.InstallLocation) &&
                    !DevelopmentRegistrationStore.SamePath(package.InstallLocation, layout.FullName))
                {
                    throw new InvalidOperationException($"Package '{package.FullName}' is already registered at '{package.InstallLocation}'. " +
                        (identity.Mode == "Original" ? "Use --unique-identity to run side by side." : "Unregister that owned layout first, or run from a different project/worktree."));
                }
                DevelopmentRegistrationStore.VerifyLive(package, identity);
            }
        }
        if (named.Count > 1)
        {
            throw new InvalidOperationException("More than one live package matches this identity. No package was changed.");
        }
        foreach (var package in packageRegistrationService.FindPackagesAtLocation(layout.FullName))
        {
            if (prior is not null && string.Equals(package.FullName, prior.Identity.PackageFullName, StringComparison.OrdinalIgnoreCase))
            {
                DevelopmentRegistrationStore.VerifyLive(package, prior.Identity);
            }
            else
            {
                DevelopmentRegistrationStore.VerifyLive(package, identity);
            }
        }
        return named.SingleOrDefault();
    }

    private void RecoverPendingRegistration(DirectoryInfo stateRoot, DirectoryInfo layout, PendingDevelopmentRegistration pending, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRegistrationSelection(layout, pending.Candidate, pending.Prior);
        var candidateLive = DevelopmentRegistrationStore.FindExact(packageRegistrationService, pending.Candidate.Identity);
        var priorLive = pending.Prior is null ? null : DevelopmentRegistrationStore.FindExact(packageRegistrationService, pending.Prior.Identity);
        var actualHash = File.Exists(Path.Combine(layout.FullName, "appxmanifest.xml"))
            ? DevelopmentRegistrationStore.HashManifest(layout) : null;
        if (candidateLive is not null && actualHash == pending.Candidate.ManifestHash)
        {
            DevelopmentRegistrationStore.VerifyLive(candidateLive, pending.Candidate.Identity);
            if (priorLive is not null && !string.Equals(priorLive.FullName, candidateLive.FullName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Both the prior and candidate package remain registered. No files were changed; inspect the pending ownership journal.");
            }
            DevelopmentRegistrationStore.Commit(stateRoot, layout, pending.Candidate);
            return;
        }
        if (priorLive is not null && pending.Prior is not null && actualHash == pending.Prior.ManifestHash)
        {
            DevelopmentRegistrationStore.VerifyLive(priorLive, pending.Prior.Identity);
            DevelopmentRegistrationStore.Commit(stateRoot, layout, pending.Prior);
            return;
        }
        if (candidateLive is null && priorLive is null && packageRegistrationService.FindPackagesAtLocation(layout.FullName).Count == 0)
        {
            DevelopmentRegistrationStore.Remove(stateRoot, layout);
            return;
        }
        throw new InvalidOperationException($"The interrupted registration at '{layout.FullName}' cannot be recovered safely. Restore its manifest or inspect the pending ownership journal; no package was removed.");
    }

    private static void PublishCandidate(DirectoryInfo candidate, DirectoryInfo layout, LayoutReconciliation reconciliation, TaskContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureLayoutPathHasNoReparsePoint(layout);
        var files = EnumerateInputFilesForLayout(candidate, layout, cancellationToken: cancellationToken);
        layout.Create();
        var desired = CopyRecipeEntries(files, layout, enforceRealPaths: true, out _, out _, cancellationToken);
        EnsureDestinationIsInsideLayout(layout, Path.Combine(layout.FullName, "appxmanifest.xml"));
        AtomicFile.Copy(Path.Combine(candidate.FullName, "appxmanifest.xml"), Path.Combine(layout.FullName, "appxmanifest.xml"));
        cancellationToken.ThrowIfCancellationRequested();
        if (reconciliation == LayoutReconciliation.Exact)
        {
            var (_, unremovable) = PruneLayout(layout, desired, context);
            ThrowIfLayoutStillHoldsStaleContent(layout, unremovable);
        }
        RemoveCompetingLayoutManifests(layout, context);
    }

    private static void ValidateCandidateDestinations(DirectoryInfo candidate, DirectoryInfo layout, LayoutReconciliation reconciliation, CancellationToken cancellationToken)
    {
        foreach (var file in EnumerateInputFilesForLayout(candidate, layout, cancellationToken: cancellationToken))
        {
            EnsureDestinationIsInsideLayout(layout, Path.Combine(layout.FullName, file.PackagePath));
        }
        if (reconciliation == LayoutReconciliation.Exact && Directory.Exists(layout.FullName))
        {
            _ = EnumerateInputFilesForLayout(layout, candidate, cancellationToken: cancellationToken);
        }
    }
}
