// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>
/// Journal of what runtime provisioning was in the middle of doing
/// (spec §"Runtime provisioning": "Partial installation is journaled and verified/repaired before
/// application launch").
/// </summary>
/// <remarks>
/// Installing framework packages is the one step here that cannot be undone by deleting a folder: a
/// host that dies between installing the first package and the last leaves a guest that is neither
/// in its previous state nor the intended one. The journal is what makes that recoverable — the next
/// run sees an unfinished record and repairs from scratch instead of trusting a staged copy it never
/// finished applying.
/// </remarks>
internal sealed record RuntimeProvisionState
{
    /// <summary>Schema version. An unknown newer version fails closed.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Monotonic revision, incremented on every commit.</summary>
    public required long Revision { get; init; }

    /// <summary>
    /// Generation this record describes. A record from a previous generation describes a guest that
    /// no longer exists, so it is repaired rather than believed.
    /// </summary>
    public required string TargetEpoch { get; init; }

    /// <summary>Content identity of the requirement set that was being applied.</summary>
    public required string PlanId { get; init; }

    /// <summary>True while installation is in flight, and until the graph has been verified.</summary>
    /// <remarks>
    /// The only question this record answers. Whether the guest currently <em>has</em> the runtime is
    /// deliberately not recorded: <c>sandbox exec</c> lets any caller change package and runtime
    /// state inside the same generation, so a "verified" flag would be a claim about the past that
    /// the launch would then act on. That is re-established by asking the guest, every time.
    /// </remarks>
    public required bool Dirty { get; init; }

    /// <summary>UTC timestamp of the last commit, for diagnostics only.</summary>
    public DateTimeOffset? UpdatedUtc { get; init; }
}

/// <summary>Source-generated serializer context for persisted runtime provisioning state.</summary>
[JsonSerializable(typeof(RuntimeProvisionState))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class RuntimeProvisionStateJsonContext : JsonSerializerContext
{
}

/// <summary>Reads and atomically commits runtime provisioning state.</summary>
internal interface IRuntimeProvisionStateStore
{
    /// <summary>Reads the record, or null when none exists.</summary>
    /// <exception cref="ExecutionTargetException">The record is corrupt or from a newer schema.</exception>
    RuntimeProvisionState? Read(ExecutionTargetRef target);

    /// <summary>Commits state under optimistic concurrency, returning the new revision.</summary>
    /// <exception cref="ExecutionTargetException">Another host process committed first.</exception>
    RuntimeProvisionState Commit(ExecutionTargetRef target, RuntimeProvisionState state, long expectedRevision);
}

/// <summary>
/// File-backed <see cref="IRuntimeProvisionStateStore"/>, using the same atomic replace and
/// monotonic revisions as target and deployment state.
/// </summary>
/// <remarks>
/// Held in its own file beside them rather than inside either. Runtime provisioning is shared by
/// every deployment in a guest, so recording it per deployment would give each one a different
/// answer to the same question; recording it in the target's ownership record would mean a runtime
/// write could corrupt the record that proves which Sandbox winapp owns.
/// </remarks>
internal sealed class RuntimeProvisionStateStore(ITargetStateDirectoryProvider directoryProvider)
    : IRuntimeProvisionStateStore
{
    /// <summary>Schema version this build reads and writes.</summary>
    internal const int CurrentSchemaVersion = 1;

    /// <summary>File name of the record inside the target state root.</summary>
    internal const string StateFileName = "runtime-state.json";

    /// <inheritdoc/>
    public RuntimeProvisionState? Read(ExecutionTargetRef target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var file = GetStateFile(target, create: false);
        if (!File.Exists(file))
        {
            return null;
        }

        RuntimeProvisionState? state;
        try
        {
            using var stream = File.OpenRead(file);
            state = JsonSerializer.Deserialize(stream, RuntimeProvisionStateJsonContext.Default.RuntimeProvisionState);
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
                ExecutionTargetErrorCodes.RuntimeProvisionFailed,
                $"Runtime provisioning state was written by a newer version of winapp (schema {state.SchemaVersion}, this build supports {CurrentSchemaVersion}).",
                userAction: "Update winapp to the newest version, then retry.",
                nextCommand: new ExecutionTargetNextCommand { Command = "winapp update", Advisory = false });
        }

        return state;
    }

    /// <inheritdoc/>
    public RuntimeProvisionState Commit(
        ExecutionTargetRef target,
        RuntimeProvisionState state,
        long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(state);

        var current = Read(target);
        var currentRevision = current?.Revision ?? 0;

        if (currentRevision != expectedRevision)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.RuntimeProvisionFailed,
                "Runtime provisioning state changed while this command was running.",
                userAction: "Retry the command.",
                context: new Dictionary<string, string>
                {
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

        var file = GetStateFile(target, create: true);
        WriteAtomic(file, JsonSerializer.Serialize(committed, RuntimeProvisionStateJsonContext.Default.RuntimeProvisionState));
        return committed;
    }

    private string GetStateFile(ExecutionTargetRef target, bool create) =>
        TargetPathSafety.CombineInsideRoot(
            directoryProvider.GetTargetRoot(target, create).FullName,
            StateFileName);

    private static void WriteAtomic(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);

        var temporary = TargetPathSafety.CombineInsideRoot(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllText(temporary, contents);
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary is harmless and must never mask the original error.
        }
    }

    private static ExecutionTargetException Unreadable(string file, Exception? innerException) =>
        ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.RuntimeProvisionFailed,
            "Runtime provisioning state is unreadable, so winapp cannot tell which runtimes are installed in the guest.",
            userAction: $"Delete '{file}', then retry to provision from scratch.",
            context: new Dictionary<string, string> { ["stateFile"] = file },
            innerException: innerException);
}
