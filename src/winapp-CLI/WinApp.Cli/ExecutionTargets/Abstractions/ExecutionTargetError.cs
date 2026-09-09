// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinApp.Cli.ExecutionTargets.Abstractions;

/// <summary>
/// One recovery command offered alongside a failure (spec §"Failure model": <c>nextCommand</c>).
/// </summary>
/// <remarks>
/// A command whose consequences require user judgement — stopping a Sandbox that may hold someone
/// else's work, for example — is marked <see cref="Advisory"/> and is never executed automatically
/// nor presented as a safe default.
/// </remarks>
internal sealed class ExecutionTargetNextCommand
{
    /// <summary>The exact command line a user can copy and run.</summary>
    public required string Command { get; init; }

    /// <summary>True when the command needs a human decision before it is safe to run.</summary>
    public required bool Advisory { get; init; }
}

/// <summary>
/// Structured failure detail shared by every execution-target error (spec §"Failure model").
/// </summary>
/// <remarks>
/// Sandbox failures <em>extend</em> the invoking command's existing JSON error contract; routing a
/// command through Sandbox never moves its payload between stdout and stderr. Optional members stay
/// null when they add nothing, and null members are omitted from serialized output.
/// </remarks>
internal sealed class ExecutionTargetErrorInfo
{
    /// <summary>Stable code from <see cref="ExecutionTargetErrorCodes"/>.</summary>
    public required string Code { get; init; }

    /// <summary>Human-readable summary. Never contains secrets, arguments, or guest payloads.</summary>
    public required string Message { get; init; }

    /// <summary>Structured, non-sensitive detail such as an instance ID or a package family name.</summary>
    public Dictionary<string, string>? Context { get; init; }

    /// <summary>The exact action the user should take.</summary>
    public string? UserAction { get; init; }

    /// <summary>A single recovery command, advisory when it requires user judgement.</summary>
    public ExecutionTargetNextCommand? NextCommand { get; init; }

    /// <summary>Accepted alternatives when the failure was caused by an unusable value.</summary>
    public List<string>? ValidValues { get; init; }

    /// <summary>A working invocation the user can copy.</summary>
    public string? Example { get; init; }

    /// <summary>
    /// The original failure when winapp automatically recovered from it without a destructive
    /// change. Present only on results that ultimately succeeded or degraded gracefully.
    /// </summary>
    public ExecutionTargetErrorInfo? RecoveredFrom { get; init; }
}

/// <summary>
/// The serialized envelope: <c>{ "error": { ... } }</c>.
/// </summary>
/// <remarks>
/// This is additive. Existing envelopes such as <c>UiErrorResult</c> keep their own shape so
/// released output and their snapshot tests are unaffected.
/// </remarks>
internal sealed class ExecutionTargetErrorResult
{
    /// <summary>The failure detail.</summary>
    public required ExecutionTargetErrorInfo Error { get; init; }
}

/// <summary>
/// Source-generated serializer context for the execution-target error envelope. Reflection-based
/// serialization is unavailable under NativeAOT, so every serialized type must be declared here.
/// </summary>
[JsonSerializable(typeof(ExecutionTargetErrorResult))]
[JsonSerializable(typeof(ExecutionTargetErrorInfo))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class ExecutionTargetErrorJsonContext : JsonSerializerContext
{
}

/// <summary>Serialization helpers for <see cref="ExecutionTargetErrorInfo"/>.</summary>
internal static class ExecutionTargetErrorSerializer
{
    /// <summary>Serializes <paramref name="error"/> as the <c>{ "error": ... }</c> envelope.</summary>
    public static string Serialize(ExecutionTargetErrorInfo error) =>
        JsonSerializer.Serialize(
            new ExecutionTargetErrorResult { Error = error },
            ExecutionTargetErrorJsonContext.Default.ExecutionTargetErrorResult);
}
