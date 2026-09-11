// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WinApp.Cli.ExecutionTargets.Abstractions;

/// <summary>
/// Provider-neutral reference to one Windows execution target.
/// </summary>
/// <remarks>
/// A reference is a provider family (<see cref="Kind"/>) plus that provider's own identity for one
/// target within it (<see cref="Id"/>). <c>--on sandbox</c> resolves to <c>sandbox</c>/<c>default</c>;
/// a future provider resolves values such as <c>hyperv:winui-test</c> without changing anything above
/// the provider boundary, so nothing outside <c>ExecutionTargets/WindowsSandbox</c> may branch on
/// <see cref="Kind"/> to reach Windows Sandbox APIs.
/// <para>
/// Kind matching is ASCII case-insensitive and is normalised to lower case here, because a kind is a
/// name this build reserves. An ID is provider-defined and is kept exactly as the provider produced
/// it: a provider whose identities are case-sensitive (a Hyper-V VM name, a desktop name) must not
/// have two distinct targets folded into one.
/// </para>
/// </remarks>
internal sealed record ExecutionTargetRef
{
    /// <summary>The host itself. Commands run here when no target is selected.</summary>
    public const string LocalKind = "local";

    /// <summary>The managed Windows Sandbox provider.</summary>
    public const string SandboxKind = "sandbox";

    /// <summary>ID every single-instance provider uses for its one target.</summary>
    public const string DefaultId = "default";

    /// <summary>
    /// How much of <see cref="StateKey"/> is readable text before the hash. Bounded so the key stays
    /// usable as both a directory name and a kernel object name, whose lengths are capped.
    /// </summary>
    private const int MaxReadableLength = 48;

    /// <summary>Creates a reference from a provider kind and that provider's target identity.</summary>
    /// <param name="kind">Target family, for example <c>sandbox</c>. Normalised to lower case.</param>
    /// <param name="id">Stable target identity within that family, kept verbatim.</param>
    public ExecutionTargetRef(string kind, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        Kind = kind.ToLowerInvariant();
        Id = id;
    }

    /// <summary>Target family, for example <c>sandbox</c>. Always lower case.</summary>
    public string Kind { get; }

    /// <summary>Stable target identity within that family, in the provider's own casing.</summary>
    public string Id { get; }

    /// <summary>The local machine.</summary>
    public static ExecutionTargetRef Local { get; } = new(LocalKind, DefaultId);

    /// <summary>Whether this reference names the host itself rather than a separate target.</summary>
    public bool IsLocal => Kind == LocalKind;

    /// <summary>How a user would write this target after <c>--on</c>.</summary>
    /// <remarks>
    /// A provider whose only target is its default is written as the bare kind, which is what the
    /// user typed and what error messages and copy-paste hints should echo back.
    /// </remarks>
    public string Selector => Id == DefaultId ? Kind : $"{Kind}:{Id}";

    /// <summary>
    /// Filesystem- and kernel-object-safe key for this target's state root and named locks.
    /// </summary>
    /// <remarks>
    /// Two targets must never share a key, because the key is what separates one target's ownership
    /// record, mutation lock, and connection lock from another's. Sanitising alone cannot promise
    /// that: <c>α</c> and <c>β</c> both sanitise away entirely, and a provider with case-sensitive
    /// IDs would have <c>Build</c> and <c>build</c> collide. So the key is a readable, sanitised
    /// prefix for a human reading the folder, plus a hash over the exact <c>(kind, id)</c> pair that
    /// carries the distinctness. The pair is hashed with a delimiter, so <c>("a-b", "c")</c> and
    /// <c>("a", "b-c")</c> also stay distinct.
    /// </remarks>
    public string StateKey => $"{Readable()}-{Fingerprint()}";

    /// <inheritdoc/>
    public override string ToString() => Selector;

    /// <summary>
    /// True when this reference names the same target as <paramref name="kind"/> and
    /// <paramref name="id"/>.
    /// </summary>
    /// <remarks>
    /// Used to prove a persisted record belongs to the target being asked about. Kind compares
    /// case-insensitively and ID exactly, matching how each is defined.
    /// </remarks>
    public bool Matches(string? kind, string? id) =>
        kind is not null &&
        id is not null &&
        string.Equals(Kind, kind, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Id, id, StringComparison.Ordinal);

    /// <summary>
    /// Lower-case ASCII rendering of the pair, for a human looking at the state folder.
    /// </summary>
    /// <remarks>
    /// Every character that is not an ASCII letter, digit, or underscore becomes a dash so the value
    /// is safe as both a directory name and a kernel object name. Runs collapse, and the result is
    /// truncated, so this alone is not unique — <see cref="Fingerprint"/> is what makes the key
    /// unique.
    /// </remarks>
    private string Readable()
    {
        var buffer = new StringBuilder(MaxReadableLength);
        Append(buffer, Kind);

        if (buffer.Length < MaxReadableLength)
        {
            buffer.Append('-');
            Append(buffer, Id);
        }

        while (buffer.Length > 0 && buffer[^1] == '-')
        {
            buffer.Length--;
        }

        return buffer.Length == 0 ? "target" : buffer.ToString();

        static void Append(StringBuilder buffer, string value)
        {
            var lastWasDash = buffer.Length > 0 && buffer[^1] == '-';
            foreach (var c in value)
            {
                if (buffer.Length >= MaxReadableLength)
                {
                    return;
                }

                if (char.IsAsciiLetterOrDigit(c) || c is '_')
                {
                    buffer.Append(char.ToLowerInvariant(c));
                    lastWasDash = false;
                    continue;
                }

                if (!lastWasDash && buffer.Length > 0)
                {
                    buffer.Append('-');
                    lastWasDash = true;
                }
            }
        }
    }

    /// <summary>
    /// Hex digest over the exact <c>(kind, id)</c> pair.
    /// </summary>
    /// <remarks>
    /// SHA-256 truncated to 8 bytes. This is a uniqueness device, not a security boundary — the
    /// inputs are locally chosen target names, and the only property required is that two different
    /// pairs practically never produce the same key. The NUL delimiter keeps the pair unambiguous,
    /// and hashing the raw UTF-8 preserves both Unicode and case.
    /// </remarks>
    private string Fingerprint()
    {
        var bytes = Encoding.UTF8.GetBytes($"{Kind}\u0000{Id}");
        var digest = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(digest.AsSpan(0, 8));
    }
}

/// <summary>
/// Opaque generation identity for one live target instance (spec §"Output and JSON":
/// <c>executionTarget.epoch</c>).
/// </summary>
/// <remarks>
/// Guest process IDs and window handles are only meaningful within the epoch that produced them.
/// Every request, result, and state commit carries the epoch so a value captured before the target
/// was recreated is rejected instead of silently resolving against a different guest.
/// </remarks>
/// <param name="Value">Opaque token. Callers must treat it as unstructured.</param>
internal readonly record struct ExecutionTargetEpoch(string Value)
{
    /// <summary>Sentinel meaning "no target instance has been observed yet".</summary>
    public static ExecutionTargetEpoch None => new(string.Empty);

    /// <summary>True when this epoch does not identify a target instance.</summary>
    public bool IsNone => string.IsNullOrEmpty(Value);

    /// <summary>
    /// Builds an epoch from a provider instance identity and that instance's random boot nonce.
    /// Including the nonce means a provider that reuses instance IDs still produces a new epoch for
    /// a new boot, which is what makes stale-handle rejection reliable.
    /// </summary>
    public static ExecutionTargetEpoch Create(string instanceId, string bootNonce) =>
        new($"{instanceId}:{bootNonce}");

    /// <inheritdoc/>
    public override string ToString() => Value;
}

/// <summary>
/// The target scope a process ID, window handle, or other acquired result belongs to.
/// </summary>
/// <remarks>
/// Emitted alongside any identifier that is only meaningful inside one target incarnation. A PID on
/// its own carries no scope, so a caller that copies one out of a result cannot tell whether it
/// names a host process or a guest one; carrying kind, ID, and epoch with it removes the guess.
/// </remarks>
internal sealed class ExecutionTargetScope
{
    /// <summary>Target family, for example <c>sandbox</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Provider-defined identity within that family.</summary>
    public required string Id { get; init; }

    /// <summary>
    /// Opaque generation of the target instance that produced the result, omitted for local results.
    /// </summary>
    public string? Epoch { get; init; }

    /// <summary>Builds a scope from a reference and the epoch that produced the result.</summary>
    public static ExecutionTargetScope For(ExecutionTargetRef target, ExecutionTargetEpoch epoch)
    {
        ArgumentNullException.ThrowIfNull(target);

        return new ExecutionTargetScope
        {
            Kind = target.Kind,
            Id = target.Id,
            Epoch = epoch.IsNone ? null : epoch.Value,
        };
    }

    /// <summary>Builds the local scope, which has no epoch.</summary>
    public static ExecutionTargetScope ForLocal() => new()
    {
        Kind = ExecutionTargetRef.Local.Kind,
        Id = ExecutionTargetRef.Local.Id,
    };

    /// <summary>
    /// How a user should re-select this target on a following command, for example <c>sandbox</c>.
    /// </summary>
    /// <remarks>
    /// Empty for local, where no selector is needed. Used so output that reports a target-scoped PID
    /// can show the selector next to it instead of printing a bare number that means nothing on its
    /// own.
    /// </remarks>
    public string SelectorHint => string.Equals(Kind, ExecutionTargetRef.LocalKind, StringComparison.Ordinal)
        ? string.Empty
        : string.Equals(Id, ExecutionTargetRef.DefaultId, StringComparison.Ordinal)
            ? Kind
            : string.Create(CultureInfo.InvariantCulture, $"{Kind}:{Id}");
}
