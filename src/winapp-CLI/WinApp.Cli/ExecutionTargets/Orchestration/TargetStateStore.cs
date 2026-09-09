// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>Reads and atomically commits <see cref="TargetState"/>.</summary>
internal interface ITargetStateStore
{
    /// <summary>
    /// Reads the persisted record, or <see langword="null"/> when none exists.
    /// </summary>
    /// <exception cref="ExecutionTargetException">
    /// The record is corrupt or was written by a newer schema. Both fail closed.
    /// </exception>
    TargetState? Read(ExecutionTargetRef target);

    /// <summary>
    /// Commits <paramref name="state"/> when the on-disk revision still matches
    /// <paramref name="expectedRevision"/>, returning the committed record with its new revision.
    /// </summary>
    /// <param name="expectedRevision">
    /// Revision the caller read, or 0 when the caller expects no existing record.
    /// </param>
    /// <exception cref="ExecutionTargetException">
    /// Another host process committed first, or the existing record cannot be read.
    /// </exception>
    TargetState Commit(ExecutionTargetRef target, TargetState state, long expectedRevision);

    /// <summary>Removes the persisted record. Succeeds when none exists.</summary>
    void Clear(ExecutionTargetRef target);
}

/// <summary>
/// File-backed <see cref="ITargetStateStore"/> using atomic replace and monotonic revisions
/// (spec §"Host coordination and state": "per-deployment atomic state files with revisions").
/// </summary>
/// <remarks>
/// Every commit writes a sibling temporary file and then replaces the target in one filesystem
/// operation, so a crash mid-write leaves the previous committed record intact rather than a
/// truncated file. There is no partially written state to repair.
/// </remarks>
internal sealed class TargetStateStore(ITargetStateDirectoryProvider directoryProvider) : ITargetStateStore
{
    /// <summary>Schema version this build reads and writes.</summary>
    internal const int CurrentSchemaVersion = 1;

    /// <summary>File name of the ownership record inside the target state root.</summary>
    internal const string StateFileName = "target-state.json";

    /// <inheritdoc/>
    public TargetState? Read(ExecutionTargetRef target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var file = GetStateFile(target, create: false);
        if (!File.Exists(file))
        {
            return null;
        }

        TargetState? state;
        try
        {
            using var stream = File.OpenRead(file);
            state = JsonSerializer.Deserialize(stream, TargetStateJsonContext.Default.TargetState);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw Unreadable(target, file, ex);
        }

        if (state is null)
        {
            throw Unreadable(target, file, innerException: null);
        }

        // A newer host wrote this. Guessing at unknown fields could mis-fence epochs or adopt an
        // instance we do not understand, so refuse instead.
        if (state.SchemaVersion > CurrentSchemaVersion)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.TargetAmbiguous,
                $"Windows Sandbox state was written by a newer version of winapp (schema {state.SchemaVersion}, this build supports {CurrentSchemaVersion}).",
                userAction: "Update winapp to the newest version, then retry.",
                context: new Dictionary<string, string>
                {
                    ["stateSchemaVersion"] = state.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["supportedSchemaVersion"] = CurrentSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
                nextCommand: new ExecutionTargetNextCommand { Command = "winapp update", Advisory = false });
        }

        // The record names the target it was written for, and it must be the one being asked about.
        // The state key already separates targets, so a mismatch means the folder was reused, moved,
        // or hand-edited -- and acting on it would let one target's ownership record fence another
        // target's epochs and adopt its instance. Refusing costs a clear error; trusting it costs a
        // command running against a guest that belongs to something else.
        if (!target.Matches(state.TargetKind, state.TargetId))
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.TargetAmbiguous,
                $"The state recorded at '{file}' belongs to a different execution target.",
                userAction: $"Delete '{file}' if no managed target is running, then retry.",
                context: new Dictionary<string, string>
                {
                    ["requestedTarget"] = target.Selector,
                    ["recordedKind"] = state.TargetKind ?? string.Empty,
                    ["recordedId"] = state.TargetId ?? string.Empty,
                    ["stateFile"] = file,
                });
        }

        return state;
    }

    /// <inheritdoc/>
    public TargetState Commit(ExecutionTargetRef target, TargetState state, long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(state);

        var current = Read(target);
        var currentRevision = current?.Revision ?? 0;
        if (currentRevision != expectedRevision)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.TargetAmbiguous,
                "Windows Sandbox state changed while this command was running.",
                userAction: "Retry the command.",
                context: new Dictionary<string, string>
                {
                    ["expectedRevision"] = expectedRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["actualRevision"] = currentRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        // Every persisted field is listed explicitly rather than copied with `with`, so the
        // committed record cannot inherit a revision or schema version from the caller. That does
        // mean a new field must be added here as well as to the record — omitting it silently
        // discards the caller's value on every commit.
        var committed = new TargetState
        {
            SchemaVersion = CurrentSchemaVersion,
            Revision = currentRevision + 1,
            TargetKind = target.Kind,
            TargetId = target.Id,
            InstanceId = state.InstanceId,
            BootNonce = state.BootNonce,
            PendingInstanceId = state.PendingInstanceId,
            PendingStartedUtc = state.PendingStartedUtc,
            InstanceOrigin = state.InstanceOrigin,
            BootstrappedEpoch = state.BootstrappedEpoch,
            AgentVersion = state.AgentVersion,
            AgentBinaryHash = state.AgentBinaryHash,
            GuestAddress = state.GuestAddress,
            ClientWindowHandle = state.ClientWindowHandle,
            ClientProcessId = state.ClientProcessId,
            ClientProcessStartTicksUtc = state.ClientProcessStartTicksUtc,
            ClientOwnedByWinapp = state.ClientOwnedByWinapp,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };

        var file = GetStateFile(target, create: true);
        WriteAtomic(file, JsonSerializer.Serialize(committed, TargetStateJsonContext.Default.TargetState));
        return committed;
    }

    /// <inheritdoc/>
    public void Clear(ExecutionTargetRef target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var file = GetStateFile(target, create: false);
        if (File.Exists(file))
        {
            File.Delete(file);
        }
    }

    private string GetStateFile(ExecutionTargetRef target, bool create) =>
        TargetPathSafety.CombineInsideRoot(
            directoryProvider.GetTargetRoot(target, create).FullName,
            StateFileName);

    /// <summary>
    /// Writes <paramref name="contents"/> so readers observe either the previous file or the new
    /// one, never a partial write. The temporary file is a sibling so the replace stays on one
    /// volume and is therefore atomic.
    /// </summary>
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
        catch (IOException)
        {
            // Best effort: a leftover temporary file is harmless and is overwritten by name on the
            // next attempt. Failing cleanup must never mask the original error.
        }
        catch (UnauthorizedAccessException)
        {
            // Same reasoning as above.
        }
    }

    private static ExecutionTargetException Unreadable(
        ExecutionTargetRef target,
        string file,
        Exception? innerException) =>
        ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.TargetAmbiguous,
            "Windows Sandbox state is unreadable, so winapp cannot prove which Sandbox it owns.",
            userAction: $"Delete '{file}' if no managed Sandbox is running, then retry.",
            context: new Dictionary<string, string>
            {
                ["targetId"] = target.Id,
                ["stateFile"] = file,
            },
            innerException: innerException);
}
