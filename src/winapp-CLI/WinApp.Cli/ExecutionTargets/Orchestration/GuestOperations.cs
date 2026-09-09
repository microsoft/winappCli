// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>How a frame's plaintext should be interpreted.</summary>
internal enum GuestPayloadKind : byte
{
    /// <summary>A <see cref="GuestMessage"/> serialized as UTF-8 JSON.</summary>
    Json = 1,

    /// <summary>A chunk of one operation's standard stream.</summary>
    Stream = 2,
}

/// <summary>Which standard stream a chunk belongs to.</summary>
internal enum GuestStreamId : byte
{
    /// <summary>Data flowing host to guest.</summary>
    StandardInput = 0,

    /// <summary>Data flowing guest to host.</summary>
    StandardOutput = 1,

    /// <summary>Data flowing guest to host.</summary>
    StandardError = 2,
}

/// <summary>Message types carried in <see cref="GuestMessage.Type"/>.</summary>
internal static class GuestMessageTypes
{
    /// <summary>Host asks the guest to describe itself.</summary>
    public const string CapabilitiesRequest = "capabilities-request";

    /// <summary>Guest reports architecture and what it supports.</summary>
    public const string CapabilitiesResponse = "capabilities-response";

    /// <summary>Host asks the guest to start a process.</summary>
    public const string ExecRequest = "exec-request";

    /// <summary>Guest reports the process started, with its ID.</summary>
    public const string ExecStarted = "exec-started";

    /// <summary>Host signals no more standard input for an operation.</summary>
    public const string StdinClosed = "stdin-closed";

    /// <summary>Host asks the guest to cancel an operation.</summary>
    public const string CancelRequest = "cancel-request";

    /// <summary>Guest reports the process exited, with its code.</summary>
    public const string ExecCompleted = "exec-completed";

    /// <summary>Host asks the guest to enumerate a managed root.</summary>
    public const string ListFilesRequest = "list-files-request";

    /// <summary>Guest returns the actual contents of a managed root.</summary>
    public const string ListFilesResponse = "list-files-response";

    /// <summary>Host announces a file it is about to stream into a managed root.</summary>
    public const string PutFileRequest = "put-file-request";

    /// <summary>Host asks the guest to stream a file out of a managed root.</summary>
    public const string GetFileRequest = "get-file-request";

    /// <summary>Host asks the guest to delete paths from a managed root.</summary>
    public const string DeleteFilesRequest = "delete-files-request";

    /// <summary>Host asks the guest to discard an entire managed scope.</summary>
    public const string RemoveScopeRequest = "remove-scope-request";

    /// <summary>Guest reports a file operation finished and verified.</summary>
    public const string FileCompleted = "file-completed";

    /// <summary>Guest reports a structured failure for an operation.</summary>
    public const string OperationFailed = "operation-failed";

    /// <summary>
    /// Host asks the guest to stop every running process of a package before a redeploy mutates
    /// the layout it was registered from.
    /// </summary>
    public const string StopPackageRequest = "stop-package-request";

    /// <summary>Host asks the guest which package is actually registered for an identity.</summary>
    public const string QueryPackageRequest = "query-package-request";

    /// <summary>Guest reports the package actually registered for an identity, or confirmed absence.</summary>
    public const string QueryPackageResponse = "query-package-response";

    /// <summary>Host asks the guest to unregister one exact package full name.</summary>
    public const string UnregisterPackageRequest = "unregister-package-request";

    /// <summary>
    /// Host asks the guest to stop one specific tracked process, identified by PID and start time,
    /// before a redeploy mutates the files it may still have open.
    /// </summary>
    public const string StopProcessRequest = "stop-process-request";

    /// <summary>Host asks whether one PID/start-time identity is alive right now.</summary>
    public const string QueryProcessRequest = "query-process-request";

    /// <summary>Guest reports whether the exact process identity is alive right now.</summary>
    public const string QueryProcessResponse = "query-process-response";
}

/// <summary>Managed guest roots a file operation may address.</summary>
/// <remarks>
/// A closed set rather than a free path is what keeps guest-provided values from selecting arbitrary
/// destinations. The host names a root and a relative path; the guest resolves both and proves the
/// result stays inside that root.
/// </remarks>
internal static class GuestRootNames
{
    /// <summary>Per-deployment application layout.</summary>
    public const string Deployment = "deployment";

    /// <summary>Per-operation staging for artifacts a command produced.</summary>
    public const string Artifacts = "artifacts";

    /// <summary>Staging for runtime payloads awaiting installation.</summary>
    public const string Runtimes = "runtimes";

    /// <summary>Free-form working area for <c>target push</c> and <c>sandbox exec</c>.</summary>
    public const string Work = "work";

    /// <summary>
    /// Folder name a root maps to under the guest's managed root.
    /// </summary>
    /// <remarks>
    /// Defined once because both halves depend on it: the guest resolves incoming file operations
    /// through it, and the host composes the absolute guest path of a deployed folder through it.
    /// Two copies would be two things to keep in agreement, and a disagreement would put files
    /// somewhere the launch could not find them.
    /// </remarks>
    /// <exception cref="ExecutionTargetException">The name is not a managed root.</exception>
    public static string FolderFor(string root) => root switch
    {
        Deployment => "deployments",
        Artifacts => "artifacts",
        Runtimes => "runtimes",
        Work => "work",
        _ => throw ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.TargetAmbiguous,
            $"'{root}' is not a managed guest location."),
    };
}

/// <summary>Where a file operation applies.</summary>
/// <param name="Root">One of <see cref="GuestRootNames"/>.</param>
/// <param name="Scope">
/// Sub-identity within the root, such as a deployment ID or operation ID. Null addresses the root
/// itself.
/// </param>
internal sealed record GuestPathScope(string Root, string? Scope);

/// <summary>One file in a managed root.</summary>
/// <param name="RelativePath">Path relative to the resolved root, using backslash separators.</param>
/// <param name="Size">Length in bytes.</param>
/// <param name="LastWriteUtcTicks">Last write time, preserved so guest timestamps stay useful.</param>
/// <param name="Sha256">Lowercase hex content hash.</param>
internal sealed record GuestFileInfo(
    string RelativePath,
    long Size,
    long LastWriteUtcTicks,
    string Sha256);

/// <summary>The package registration the guest OS currently reports for an identity.</summary>
/// <param name="FullName">The effective package full name.</param>
/// <param name="IsDevelopmentMode">Whether Windows reports a development-mode registration.</param>
/// <param name="RegisteredLocation">The location Windows recorded for the registration.</param>
internal sealed record GuestPackageRegistration(
    string FullName,
    bool IsDevelopmentMode,
    string? RegisteredLocation);

/// <summary>A request to start one guest process.</summary>
/// <remarks>
/// The executable and its arguments are separate values, never one interpolated string. That is
/// what preserves argument boundaries end to end and removes any possibility of shell injection —
/// the spec calls for structured executable and argument arrays specifically.
/// </remarks>
internal sealed class GuestExecRequest
{
    /// <summary>Executable to launch inside the guest.</summary>
    /// <remarks>
    /// Ignored when <see cref="UseGuestWinapp"/> is set, which is the only way to name the guest's
    /// own winapp binary: the host does not know where the agent installed itself, and letting it
    /// send a path that the guest then executes as winapp would make the agent's own identity
    /// host-selectable.
    /// </remarks>
    public string? Executable { get; init; }

    /// <summary>
    /// Runs the guest's own winapp binary instead of <see cref="Executable"/>.
    /// </summary>
    /// <remarks>
    /// The agent implements no application semantics of its own: <c>run</c>, <c>unregister</c>,
    /// debugging, and UI automation are all the ordinary guest winapp commands, started this way.
    /// </remarks>
    public bool UseGuestWinapp { get; init; }

    /// <summary>Arguments, each preserved as its own value.</summary>
    public required List<string> Arguments { get; init; }

    /// <summary>Working directory, or null for the agent's default.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Extra environment variables for the child, used to carry the forwarded Cooperative UI Turns
    /// owner context.
    /// </summary>
    public Dictionary<string, string>? Environment { get; init; }

    /// <summary>
    /// Whether this operation will inject real input or capture the screen.
    /// </summary>
    /// <remarks>
    /// Carried per request rather than assumed from the connection, because readiness is not a
    /// property of the channel: the user can close the Sandbox window at any moment, including
    /// between the capability handshake and this command. The guest re-verifies immediately before
    /// starting a request that declares this, and refuses rather than starting a process that would
    /// report input it never delivered.
    /// </remarks>
    public bool RequiresRealInput { get; init; }

    /// <summary>
    /// Return after process creation while the guest agent continues owning the process job.
    /// </summary>
    /// <remarks>
    /// Used for direct unpackaged launches. Packaged launches keep using guest <c>winapp run
    /// --detach</c>, so their existing semantics remain authoritative.
    /// </remarks>
    public bool Detach { get; init; }
}

/// <summary>One control message on the guest channel.</summary>
/// <remarks>
/// A single envelope with optional payload members keeps serialization source-generated and
/// AOT-safe, which polymorphic message hierarchies are not.
/// </remarks>
internal sealed class GuestMessage
{
    /// <summary>One of <see cref="GuestMessageTypes"/>.</summary>
    public required string Type { get; init; }

    /// <summary>Operation this message belongs to, when it is operation-scoped.</summary>
    public string? OperationId { get; init; }

    /// <summary>
    /// Generation the sender believes it is talking to. Mutation requests carrying a stale epoch are
    /// rejected rather than applied to a recreated guest.
    /// </summary>
    public string? TargetEpoch { get; init; }

    /// <summary>Present on <see cref="GuestMessageTypes.ExecRequest"/>.</summary>
    public GuestExecRequest? Exec { get; init; }

    /// <summary>Present on <see cref="GuestMessageTypes.ExecStarted"/>.</summary>
    public int? ProcessId { get; init; }

    /// <summary>Present on <see cref="GuestMessageTypes.ExecStarted"/>, for liveness checks.</summary>
    public long? ProcessStartTicksUtc { get; init; }

    /// <summary>Present on <see cref="GuestMessageTypes.QueryProcessResponse"/>.</summary>
    public bool? ProcessRunning { get; init; }

    /// <summary>Present on <see cref="GuestMessageTypes.ExecCompleted"/>.</summary>
    public int? ExitCode { get; init; }

    /// <summary>Present on <see cref="GuestMessageTypes.CapabilitiesResponse"/>.</summary>
    public ExecutionTargetCapabilities? Capabilities { get; init; }

    /// <summary>Managed root and scope a file operation applies to.</summary>
    public GuestPathScope? Scope { get; init; }

    /// <summary>Present on <see cref="GuestMessageTypes.PutFileRequest"/>.</summary>
    public GuestFileInfo? File { get; init; }

    /// <summary>
    /// Present on <see cref="GuestMessageTypes.ListFilesResponse"/>, and the paths to remove on
    /// <see cref="GuestMessageTypes.DeleteFilesRequest"/>.
    /// </summary>
    public List<GuestFileInfo>? Files { get; init; }

    /// <summary>Relative paths for delete and get requests.</summary>
    public List<string>? Paths { get; init; }

    /// <summary>Present on <see cref="GuestMessageTypes.OperationFailed"/>.</summary>
    public ExecutionTargetErrorInfo? Error { get; init; }

    /// <summary>
    /// Present on <see cref="GuestMessageTypes.StopPackageRequest"/>. The package family name a
    /// deployment registered, resolved to the guest's actual current full name before anything is
    /// terminated.
    /// </summary>
    public string? PackageFamilyName { get; init; }

    /// <summary>Package identity name for a registration query.</summary>
    public string? PackageName { get; init; }

    /// <summary>Package publisher for a registration query.</summary>
    public string? PackagePublisher { get; init; }

    /// <summary>
    /// Whether a registration query found a package. Present on
    /// <see cref="GuestMessageTypes.QueryPackageResponse"/>.
    /// </summary>
    public bool? PackageRegistered { get; init; }

    /// <summary>Actual package returned by <see cref="GuestMessageTypes.QueryPackageResponse"/>.</summary>
    public GuestPackageRegistration? RegisteredPackage { get; init; }

    /// <summary>Exact package full name for <see cref="GuestMessageTypes.UnregisterPackageRequest"/>.</summary>
    public string? PackageFullName { get; init; }

    /// <summary>
    /// Present on <see cref="GuestMessageTypes.StopPackageRequest"/>. The guest location the
    /// requesting deployment's own registration is expected to be installed from.
    /// </summary>
    /// <remarks>
    /// Two deployments built from different source paths can share the same package identity, and
    /// resolving a family name to a full name proves only that <em>something</em> is registered
    /// under it — not that it is <em>this</em> deployment's registration. The guest verifies the
    /// currently registered package's own install location against this value before terminating
    /// anything, so a family name collision with a different, legitimately running deployment
    /// refuses instead of stopping the wrong application.
    /// </remarks>
    public string? ExpectedRegisteredLocation { get; init; }
}

/// <summary>Source-generated serializer context for guest control messages.</summary>
[JsonSerializable(typeof(GuestMessage))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class GuestMessageJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Encodes and decodes frame plaintext: either a JSON control message or a raw stream chunk.
/// </summary>
/// <remarks>
/// Stream chunks use a compact binary header rather than base64 inside JSON, so forwarding a
/// process's output does not inflate every byte by a third or force large payloads through a JSON
/// writer.
/// <para>
/// Decoding is total: malformed input returns false rather than throwing. The peer is authenticated
/// by this point, but a compromised or buggy guest must still not be able to crash the host.
/// </para>
/// </remarks>
internal static class GuestPayloadCodec
{
    private const int StreamHeaderSize = 1 + 16 + 1;

    /// <summary>Serializes a control message.</summary>
    public static byte[] EncodeJson(GuestMessage message)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, GuestMessageJsonContext.Default.GuestMessage);
        var payload = new byte[json.Length + 1];
        payload[0] = (byte)GuestPayloadKind.Json;
        json.CopyTo(payload.AsSpan(1));
        return payload;
    }

    /// <summary>Frames a chunk of one operation's stream.</summary>
    public static byte[] EncodeStream(Guid operationId, GuestStreamId stream, ReadOnlySpan<byte> data)
    {
        var payload = new byte[StreamHeaderSize + data.Length];
        payload[0] = (byte)GuestPayloadKind.Stream;
        operationId.TryWriteBytes(payload.AsSpan(1, 16));
        payload[17] = (byte)stream;
        data.CopyTo(payload.AsSpan(StreamHeaderSize));
        return payload;
    }

    /// <summary>Reads the payload kind, if the payload is long enough to have one.</summary>
    public static bool TryGetKind(ReadOnlySpan<byte> payload, out GuestPayloadKind kind)
    {
        kind = default;
        if (payload.Length < 1)
        {
            return false;
        }

        var value = payload[0];
        if (value is not ((byte)GuestPayloadKind.Json or (byte)GuestPayloadKind.Stream))
        {
            return false;
        }

        kind = (GuestPayloadKind)value;
        return true;
    }

    /// <summary>Deserializes a control message, returning null when the payload is malformed.</summary>
    public static GuestMessage? TryDecodeJson(ReadOnlySpan<byte> payload)
    {
        if (!TryGetKind(payload, out var kind) || kind != GuestPayloadKind.Json)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(payload[1..], GuestMessageJsonContext.Default.GuestMessage);
        }
        catch (JsonException)
        {
            // A peer that sends malformed JSON is reported as a protocol failure by the caller, not
            // allowed to throw out of the receive loop.
            return null;
        }
    }

    /// <summary>Decodes a stream chunk.</summary>
    public static bool TryDecodeStream(
        ReadOnlyMemory<byte> payload,
        out Guid operationId,
        out GuestStreamId stream,
        out ReadOnlyMemory<byte> data)
    {
        operationId = Guid.Empty;
        stream = default;
        data = default;

        var span = payload.Span;
        if (!TryGetKind(span, out var kind) || kind != GuestPayloadKind.Stream)
        {
            return false;
        }

        if (payload.Length < StreamHeaderSize)
        {
            return false;
        }

        operationId = new Guid(span.Slice(1, 16));

        var streamValue = span[17];
        if (streamValue > (byte)GuestStreamId.StandardError)
        {
            return false;
        }

        stream = (GuestStreamId)streamValue;
        data = payload[StreamHeaderSize..];
        return true;
    }

    /// <summary>Largest stream chunk that fits in one frame.</summary>
    /// <remarks>
    /// Bulk data is split to this size so one operation's output cannot exceed the frame limit and
    /// stall the channel.
    /// </remarks>
    public static int MaxStreamChunkSize => GuestFrameCodec.MaxPlaintextBytes - StreamHeaderSize;

    /// <summary>Reads a big-endian operation sequence, used by fencing checks.</summary>
    internal static bool TryReadUInt32(ReadOnlySpan<byte> value, out uint result)
    {
        if (value.Length < sizeof(uint))
        {
            result = 0;
            return false;
        }

        result = BinaryPrimitives.ReadUInt32BigEndian(value);
        return true;
    }
}
