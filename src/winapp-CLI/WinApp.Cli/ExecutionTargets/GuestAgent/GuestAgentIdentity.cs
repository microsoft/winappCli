// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.ExecutionTargets.GuestAgent;

/// <summary>
/// Identity of one winapp binary acting as a guest agent (spec §"Agent versioning and upgrades").
/// </summary>
/// <remarks>
/// Version and binary hash are carried together deliberately. The version alone cannot distinguish
/// two builds of the same version — a locally built host and a released guest, for instance — and
/// the hash alone carries no ordering, so neither is sufficient to decide whether an update is an
/// upgrade or a downgrade.
/// </remarks>
/// <param name="Version">Stamped winapp version, the single version source for all comparisons.</param>
/// <param name="BinaryHash">Lowercase hex SHA-256 of the agent binary.</param>
/// <param name="Architecture">Processor architecture the binary targets, for example <c>arm64</c>.</param>
/// <param name="ProtocolMinimum">Oldest protocol revision this binary can speak.</param>
/// <param name="ProtocolMaximum">Newest protocol revision this binary can speak.</param>
internal sealed record GuestAgentIdentity(
    string Version,
    string BinaryHash,
    string Architecture,
    int ProtocolMinimum,
    int ProtocolMaximum)
{
    /// <summary>Describes the winapp binary at <paramref name="binaryPath"/>.</summary>
    public static async Task<GuestAgentIdentity> ForBinaryAsync(
        string binaryPath,
        string version,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binaryPath);

        return new GuestAgentIdentity(
            version,
            await ComputeBinaryHashAsync(binaryPath, cancellationToken).ConfigureAwait(false),
            CurrentArchitecture,
            GuestProtocol.MinimumVersion,
            GuestProtocol.CurrentVersion);
    }

    /// <summary>Describes the running winapp process as an agent binary.</summary>
    public static Task<GuestAgentIdentity> ForCurrentProcessAsync(CancellationToken cancellationToken)
    {
        var path = Environment.ProcessPath
            ?? throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.AgentUpgradeFailed,
                "winapp could not determine its own executable path.",
                userAction: "Reinstall winapp, then retry.");

        return ForBinaryAsync(path, VersionHelper.GetVersionString(), cancellationToken);
    }

    /// <summary>Architecture of the running process, in the spec's lowercase spelling.</summary>
    internal static string CurrentArchitecture => RuntimeInformation.ProcessArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => "x64",
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        System.Runtime.InteropServices.Architecture.X86 => "x86",
        var other => other.ToString().ToLowerInvariant(),
    };

    /// <summary>Lowercase hex SHA-256 of a file's contents.</summary>
    internal static async Task<string> ComputeBinaryHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 64 * 1024,
            useAsync: true);

        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

/// <summary>
/// What a running guest agent publishes about itself (spec §"Guest winapp agent mode").
/// </summary>
/// <remarks>
/// Written by the agent and read by the host through the bootstrap-result folder, so the host can
/// tell a healthy agent from one that started but refused to serve — and can report the agent's own
/// diagnostics when it never came up at all, rather than reducing that to a generic transport error.
/// </remarks>
internal sealed record GuestAgentHeartbeat
{
    /// <summary>Schema version of this record, so a newer agent stays readable or fails closed.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Stamped winapp version of the running agent.</summary>
    public required string Version { get; init; }

    /// <summary>SHA-256 of the running agent binary.</summary>
    public required string BinaryHash { get; init; }

    /// <summary>Architecture the agent is running as.</summary>
    public required string Architecture { get; init; }

    /// <summary>Oldest protocol revision the agent can speak.</summary>
    public required int ProtocolMinimum { get; init; }

    /// <summary>Newest protocol revision the agent can speak.</summary>
    public required int ProtocolMaximum { get; init; }

    /// <summary>
    /// Whether the agent passed its interactive-session checks. A heartbeat is published even when
    /// this is false so the host can report exactly why, rather than timing out on silence.
    /// </summary>
    public required bool Ready { get; init; }

    /// <summary>Why the agent is not ready, when <see cref="Ready"/> is false.</summary>
    public string? NotReadyReason { get; init; }

    /// <summary>Target generation the agent believes it serves.</summary>
    public required string TargetEpoch { get; init; }

    /// <summary>TCP port the agent is listening on, for the host to connect to.</summary>
    public required int Port { get; init; }

    /// <summary>UTC timestamp of this publication, used to detect a stalled agent.</summary>
    public required DateTimeOffset PublishedUtc { get; init; }

    /// <summary>Current schema version emitted by this build.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Heartbeat older than this is treated as stale rather than live.</summary>
    public static TimeSpan MaximumAge => TimeSpan.FromSeconds(30);

    /// <summary>Whether this heartbeat is recent enough to prove the agent is alive.</summary>
    public bool IsFresh(DateTimeOffset nowUtc) => nowUtc - PublishedUtc <= MaximumAge;

    /// <summary>Builds the heartbeat for an agent with <paramref name="identity"/>.</summary>
    public static GuestAgentHeartbeat Create(
        GuestAgentIdentity identity,
        GuestReadinessFailure readiness,
        ExecutionTargetEpoch epoch,
        int port,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(identity);

        return new GuestAgentHeartbeat
        {
            SchemaVersion = CurrentSchemaVersion,
            Version = identity.Version,
            BinaryHash = identity.BinaryHash,
            Architecture = identity.Architecture,
            ProtocolMinimum = identity.ProtocolMinimum,
            ProtocolMaximum = identity.ProtocolMaximum,
            Ready = readiness == GuestReadinessFailure.None,
            NotReadyReason = readiness == GuestReadinessFailure.None ? null : readiness.ToString(),
            TargetEpoch = epoch.Value,
            Port = port,
            PublishedUtc = nowUtc,
        };
    }

    /// <summary>Serializes this heartbeat.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, GuestAgentJsonContext.Default.GuestAgentHeartbeat);

    /// <summary>
    /// Parses a heartbeat, returning null for anything malformed or from an unknown schema.
    /// </summary>
    /// <remarks>
    /// The heartbeat arrives through the guest-writable bootstrap-result folder, which the spec
    /// treats as untrusted input. Parsing is therefore total: bad content produces "no usable
    /// heartbeat", never an exception out of the readiness path.
    /// </remarks>
    public static GuestAgentHeartbeat? TryParse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var heartbeat = JsonSerializer.Deserialize(json, GuestAgentJsonContext.Default.GuestAgentHeartbeat);

            // A newer schema is refused rather than partially interpreted: fields this build does
            // not know about could change the meaning of the ones it does.
            return heartbeat?.SchemaVersion == CurrentSchemaVersion ? heartbeat : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Source-generated serializer context for agent heartbeat payloads.</summary>
[JsonSerializable(typeof(GuestAgentHeartbeat))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class GuestAgentJsonContext : JsonSerializerContext
{
}
