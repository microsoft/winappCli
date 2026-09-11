// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>
/// What winapp registered in the guest for one deployment (spec §"Package ownership").
/// </summary>
/// <remarks>
/// Registration and unregistration act only on a package whose recorded location matches this
/// deployment. Matching by name alone would let winapp remove a package a user installed themselves
/// that happens to share an identity — which is exactly the "never adopts or removes an external
/// guest package" rule.
/// </remarks>
internal sealed record PackageOwnership
{
    /// <summary>Original package name, preserved rather than rewritten.</summary>
    public required string PackageName { get; init; }

    /// <summary>Original publisher, preserved rather than rewritten.</summary>
    public required string Publisher { get; init; }

    /// <summary>
    /// Effective package full name as registered in the guest, when the guest reported one.
    /// </summary>
    /// <remarks>
    /// Null when the host deployed and launched through guest winapp without asking it to report
    /// back. That is not a weakening of the "never removes an external package" rule: the operative
    /// key is <see cref="RegisteredLocation"/>, a folder only winapp writes, and the guest's own
    /// unregister independently refuses any registration that is not a development-mode package
    /// rooted there. Recording a full name the host had computed rather than observed would be a
    /// value that merely looked like proof.
    /// </remarks>
    public string? PackageFullName { get; init; }

    /// <summary>Effective package family name.</summary>
    public required string PackageFamilyName { get; init; }

    /// <summary>Guest location the package was registered from.</summary>
    public required string RegisteredLocation { get; init; }

    /// <summary>Application user model ID used to launch it.</summary>
    public string? Aumid { get; init; }

    /// <summary>
    /// Whether <paramref name="candidate"/> is the exact package this record owns.
    /// </summary>
    /// <remarks>
    /// The registered location must always match: it is the folder this deployment wrote, so a
    /// package registered from anywhere else is someone else's. The full name is compared as well
    /// whenever one was recorded, because the location alone would accept a different package
    /// registered from a path this deployment happens to have used.
    /// </remarks>
    public bool Owns(string packageFullName, string registeredLocation) =>
        TargetPathSafety.PathsEqual(RegisteredLocation, registeredLocation) &&
        (PackageFullName is null ||
            string.Equals(PackageFullName, packageFullName, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Persisted state for one deployment inside one target generation
/// (spec §"Deployment model", §"Host coordination and state").
/// </summary>
/// <remarks>
/// Held separately from target ownership so a deployment write never has to rewrite — or risk
/// corrupting — the record that proves which Sandbox winapp owns.
/// </remarks>
internal sealed record DeploymentState
{
    /// <summary>Schema version. Migrations are monotonic; an unknown newer version fails closed.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Monotonic revision, incremented on every commit.</summary>
    public required long Revision { get; init; }

    /// <summary>Internal deployment identity this record belongs to.</summary>
    public required string DeploymentId { get; init; }

    /// <summary>
    /// Target generation this state was written for. State from a previous generation describes a
    /// guest that no longer exists, so it is discarded rather than reconciled against.
    /// </summary>
    public required string TargetEpoch { get; init; }

    /// <summary>
    /// True while the guest layout is mid-change. A dirty deployment never launches and never
    /// reports healthy; the next run performs a complete desired-state reconciliation.
    /// </summary>
    public required bool Dirty { get; init; }

    /// <summary>Package this deployment registered, when it registered one.</summary>
    public PackageOwnership? Package { get; init; }

    /// <summary>
    /// Whether this deployment has ever held a package registration in this target generation.
    /// </summary>
    /// <remarks>
    /// Kept after unregister so diagnostics can describe the retained layout without retaining an
    /// obsolete ownership claim.
    /// </remarks>
    public bool WasPackaged { get; init; }

    /// <summary>
    /// Process ID of the guest operation winapp launched, valid only within the epoch.
    /// </summary>
    /// <remarks>
    /// For an unpackaged direct launch this is the application. For a packaged launch this is the
    /// guest winapp launcher that owns the operation, not necessarily the application's UI process.
    /// </remarks>
    [JsonPropertyName("processId")]
    public int? TrackedOperationProcessId { get; init; }

    /// <summary>UTC ticks when the tracked operation started, so a reused PID is detected.</summary>
    [JsonPropertyName("processStartTicksUtc")]
    public long? TrackedOperationProcessStartTicksUtc { get; init; }

    /// <summary>UTC timestamp of the last commit, for diagnostics only.</summary>
    public DateTimeOffset? UpdatedUtc { get; init; }

    /// <summary>Whether this state describes <paramref name="epoch"/>.</summary>
    public bool IsForEpoch(ExecutionTargetEpoch epoch) =>
        string.Equals(TargetEpoch, epoch.Value, StringComparison.Ordinal);
}

/// <summary>Source-generated serializer context for persisted deployment state.</summary>
[JsonSerializable(typeof(DeploymentState))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class DeploymentStateJsonContext : JsonSerializerContext
{
}

/// <summary>Reads and atomically commits per-deployment state.</summary>
internal interface IDeploymentStateStore
{
    /// <summary>Reads a deployment's state, or null when none exists.</summary>
    /// <exception cref="ExecutionTargetException">The record is corrupt or from a newer schema.</exception>
    DeploymentState? Read(ExecutionTargetRef target, string deploymentId);

    /// <summary>Commits state under optimistic concurrency, returning the new revision.</summary>
    /// <exception cref="ExecutionTargetException">Another host process committed first.</exception>
    DeploymentState Commit(
        ExecutionTargetRef target,
        DeploymentState state,
        long expectedRevision);

    /// <summary>Removes a deployment's state. Succeeds when none exists.</summary>
    void Clear(ExecutionTargetRef target, string deploymentId);

    /// <summary>
    /// Every deployment recorded for a target.
    /// </summary>
    /// <remarks>
    /// Needed by commands that identify a deployment by what it registered rather than by an input
    /// path — <c>unregister --on sandbox</c> is given a manifest, not the folder a run was launched
    /// from, so it has to find the deployment that owns that identity.
    /// </remarks>
    /// <exception cref="ExecutionTargetException">A record is corrupt or from a newer schema.</exception>
    IReadOnlyList<DeploymentState> List(ExecutionTargetRef target);
}

/// <summary>
/// File-backed <see cref="IDeploymentStateStore"/> using atomic replace and monotonic revisions.
/// </summary>
/// <remarks>
/// Authoritative host state deliberately lives outside guest-writable staging: a guest that could
/// edit this could convince the host it owns a package it does not, or that a dirty deployment is
/// clean.
/// </remarks>
internal sealed class DeploymentStateStore(ITargetStateDirectoryProvider directoryProvider) : IDeploymentStateStore
{
    /// <summary>Schema version this build reads and writes.</summary>
    internal const int CurrentSchemaVersion = 1;

    /// <summary>Folder holding per-deployment records inside the target state root.</summary>
    internal const string DeploymentsFolder = "deployments";

    /// <inheritdoc/>
    public DeploymentState? Read(ExecutionTargetRef target, string deploymentId)
    {
        var file = GetStateFile(target, deploymentId, create: false);
        if (!File.Exists(file))
        {
            return null;
        }

        DeploymentState? state;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            state = JsonSerializer.Deserialize(stream, DeploymentStateJsonContext.Default.DeploymentState);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw Unreadable(file, ex);
        }

        if (state is null)
        {
            throw Unreadable(file, innerException: null);
        }

        if (state.SchemaVersion > CurrentSchemaVersion)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.TargetAmbiguous,
                $"Deployment state was written by a newer version of winapp (schema {state.SchemaVersion}, this build supports {CurrentSchemaVersion}).",
                userAction: "Update your winapp installation to the newest version, then retry.");
        }

        return state;
    }

    /// <inheritdoc/>
    public DeploymentState Commit(ExecutionTargetRef target, DeploymentState state, long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(state);

        var file = GetStateFile(target, state.DeploymentId, create: true);
        using var commitLease = AcquireCommitLease(file);
        var current = Read(target, state.DeploymentId);
        var currentRevision = current?.Revision ?? 0;

        if (currentRevision != expectedRevision)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.TargetAmbiguous,
                "This deployment's state changed while the command was running.",
                userAction: "Retry the command.",
                context: new Dictionary<string, string>
                {
                    ["deploymentId"] = state.DeploymentId,
                    ["expectedRevision"] = expectedRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["actualRevision"] = currentRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        var committed = state with
        {
            SchemaVersion = CurrentSchemaVersion,
            Revision = currentRevision + 1,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };

        AtomicFile.WriteAllText(file, JsonSerializer.Serialize(committed, DeploymentStateJsonContext.Default.DeploymentState));
        return committed;
    }

    /// <inheritdoc/>
    public void Clear(ExecutionTargetRef target, string deploymentId)
    {
        var file = GetStateFile(target, deploymentId, create: false);
        if (!Directory.Exists(Path.GetDirectoryName(file)))
        {
            return;
        }

        using var commitLease = AcquireCommitLease(file);
        if (File.Exists(file))
        {
            File.Delete(file);
        }
    }

    // Process-start publication runs outside the guest mutation lease. Serialize the revision check
    // and replacement together so a delayed start cannot overwrite a newer deployment's ownership.
    private static FileStream AcquireCommitLease(string stateFile)
    {
        var deadline = Environment.TickCount64 + 5_000;
        while (true)
        {
            try
            {
                return new FileStream(stateFile + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
            {
                if (Environment.TickCount64 >= deadline)
                {
                    throw ExecutionTargetException.Create(
                        ExecutionTargetErrorCodes.TargetAmbiguous,
                        "Another command is updating this deployment's state.",
                        userAction: "Retry the command.",
                        innerException: ex);
                }
                Thread.Sleep(10);
            }
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<DeploymentState> List(ExecutionTargetRef target)
    {
        var directory = TargetPathSafety.CombineInsideRoot(
            directoryProvider.GetTargetRoot(target, create: false).FullName, DeploymentsFolder);

        if (!Directory.Exists(directory))
        {
            return [];
        }

        // Read rather than a bespoke parse, so a corrupt or newer-schema record fails here exactly
        // as it would on the path that owns it. Skipping one silently would let a command report
        // "not deployed" for something that is in fact deployed.
        return
        [
            .. Directory.EnumerateFiles(directory, "*.json")
                .Select(file => Read(target, Path.GetFileNameWithoutExtension(file)))
                .OfType<DeploymentState>(),
        ];
    }

    private string GetStateFile(ExecutionTargetRef target, string deploymentId, bool create)
    {
        var root = directoryProvider.GetTargetRoot(target, create).FullName;
        var file = TargetPathSafety.CombineInsideRoot(root, DeploymentsFolder, $"{deploymentId}.json");

        if (create)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        }

        return file;
    }

    private static ExecutionTargetException Unreadable(string file, Exception? innerException) =>
        ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.DeploymentDirty,
            "Deployment state is unreadable, so winapp cannot tell what is deployed in the guest.",
            userAction: $"Delete '{file}', then retry to redeploy from scratch.",
            context: new Dictionary<string, string> { ["stateFile"] = file },
            innerException: innerException);
}
