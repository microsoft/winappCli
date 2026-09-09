// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>
/// Transfers runtime payloads and their plan into a guest's staging scope
/// (spec §"Runtime provisioning" step 4).
/// </summary>
/// <remarks>
/// Uses only the verified file channel, so every byte the guest ends up installing was hash-checked
/// on arrival. Separated from <see cref="TargetRuntimeService"/> because deciding <em>what</em> a
/// guest needs and getting the bytes there are different responsibilities with different failure
/// modes — and because the transfer is the part that has to stay cheap on a warm rerun.
/// </remarks>
internal static class RuntimeStaging
{
    /// <summary>
    /// The name a package payload is staged under inside the runtime scope.
    /// </summary>
    /// <remarks>
    /// Derived from the identity read out of the payload rather than inherited from the file name it
    /// happened to have in a cache winapp does not own. Two cached copies with the same file name
    /// therefore cannot collide, and the staged name is a value the host controls.
    /// </remarks>
    public static string StagedFileName(RuntimePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var extension = payload.File.Extension.Equals(".appx", StringComparison.OrdinalIgnoreCase)
            ? ".appx"
            : ".msix";

        return TargetPathSafety.EnsureSafeSegment(
            $"{payload.PackageName}_{payload.Version}_{payload.Architecture}{extension}");
    }

    /// <summary>The name a shared framework layout is staged under inside the runtime scope.</summary>
    public static string StagedFileName(RuntimeFrameworkPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return TargetPathSafety.EnsureSafeSegment(
            $"{payload.Name}_{payload.Version}_{payload.Architecture}.zip");
    }

    /// <summary>
    /// Transfers the plan and any payloads the guest does not already have.
    /// </summary>
    /// <param name="target">Prepared target whose channel and epoch the transfer is fenced on.</param>
    /// <param name="scope">Guest staging scope for this plan.</param>
    /// <param name="plan">Plan to publish, naming each staged payload.</param>
    /// <param name="packages">Resolved framework package payloads, in plan order.</param>
    /// <param name="frameworks">Resolved shared framework layouts, keyed by framework name.</param>
    /// <param name="repair">True to discard the scope first, after an unfinished previous pass.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <remarks>
    /// Content is compared by hash against what the guest reports it holds, so a warm rerun of the
    /// same plan transfers nothing — which matters, because a Windows App Runtime inventory and a
    /// .NET layout are tens of megabytes each and every run would otherwise pay for them again.
    /// </remarks>
    public static async Task StageAsync(
        PreparedTarget target,
        GuestPathScope scope,
        RuntimeProvisionPlan plan,
        IReadOnlyList<ResolvedRuntimePackage> packages,
        IReadOnlyDictionary<string, RuntimeFrameworkPayload> frameworks,
        bool repair,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentNullException.ThrowIfNull(frameworks);

        if (repair)
        {
            // A previous pass left the scope in an unknown state. Discarding it is cheaper to reason
            // about than proving which of a partially transferred set is still intact.
            await target.Operations.DeleteScopeAsync(scope, cancellationToken).ConfigureAwait(false);
        }

        var present = repair
            ? []
            : await target.Operations.ListFilesAsync(scope, cancellationToken).ConfigureAwait(false);

        var byPath = present.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);

        // Any verdict left in the scope belongs to a previous pass. Removing it before the guest
        // runs is what stops a guest that failed to start at all from being credited with the report
        // its predecessor wrote for the same plan.
        if (byPath.ContainsKey(RuntimeProvisionReport.FileName))
        {
            await target.Operations
                .DeleteFilesAsync(scope, [RuntimeProvisionReport.FileName], cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var entry in packages)
        {
            if (entry.Payload is { } payload)
            {
                await TransferAsync(
                    target, scope, byPath, payload.File, StagedFileName(payload), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        foreach (var payload in frameworks.Values)
        {
            await TransferAsync(
                target, scope, byPath, payload.Archive, StagedFileName(payload), cancellationToken)
                .ConfigureAwait(false);
        }

        // The plan is written last so a staging area that contains it is one whose payloads all
        // landed. A guest that finds the plan can trust every file it names is present.
        var planBytes = Encoding.UTF8.GetBytes(plan.ToJson());
        await using var planContent = new MemoryStream(planBytes, writable: false);

        await target.Operations.PutFileAsync(
            scope,
            new GuestFileInfo(
                RuntimeProvisionPlan.FileName,
                planBytes.Length,
                DateTimeOffset.UtcNow.UtcTicks,
                Convert.ToHexStringLower(SHA256.HashData(planBytes))),
            planContent,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends one payload, unless the guest already holds identical content.</summary>
    private static async Task TransferAsync(
        PreparedTarget target,
        GuestPathScope scope,
        Dictionary<string, GuestFileInfo> present,
        FileInfo file,
        string stagedName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var info = await DescribeAsync(file, stagedName, cancellationToken).ConfigureAwait(false);

        if (present.TryGetValue(stagedName, out var existing) &&
            string.Equals(existing.Sha256, info.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await using var content = new FileStream(
            file.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 64 * 1024,
            useAsync: true);

        await target.Operations.PutFileAsync(scope, info, content, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Describes a host payload as the guest file it is about to become.</summary>
    private static async Task<GuestFileInfo> DescribeAsync(
        FileInfo file,
        string stagedName,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            file.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 64 * 1024,
            useAsync: true);

        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);

        return new GuestFileInfo(
            stagedName,
            file.Length,
            file.LastWriteTimeUtc.Ticks,
            Convert.ToHexStringLower(hash));
    }
}
