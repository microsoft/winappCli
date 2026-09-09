// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;

namespace WinApp.Cli.Services.InteractiveDesktop;

/// <summary>
/// One resolved logical UI workflow owner. Only <see cref="Key"/> is ever persisted; the raw
/// <c>WINAPP_UI_WORKFLOW_ID</c> never leaves this process.
/// </summary>
/// <param name="Kind">How the owner was resolved.</param>
/// <param name="Key">Lowercase hex SHA-256 of the domain-separated owner payload.</param>
internal sealed record UiOwnerIdentity(UiOwnerKind Kind, string Key)
{
    /// <summary>
    /// Whether this owner may hold the desktop across separate <c>winapp.exe</c> invocations.
    /// </summary>
    /// <remarks>
    /// This is the whole two-tier rule in one place. Collision arbitration is unconditional — every
    /// mutation takes a turn — but <em>continuity</em> between commands is opt-in: only a workflow that
    /// named itself keeps a post-command idle grace. An anonymous command releases the desktop the
    /// instant it finishes, so a one-shot can never strand the desktop waiting for a follow-up that is
    /// never coming.
    /// </remarks>
    public bool HasContinuity => Kind == UiOwnerKind.Workflow;
}

/// <summary>Resolves the logical workflow owner for the current command.</summary>
internal interface IUiOwnerResolver
{
    /// <summary>
    /// Resolves the owner from <c>WINAPP_UI_WORKFLOW_ID</c>, or mints a unique anonymous one-command
    /// owner when the variable is absent.
    /// </summary>
    /// <exception cref="UiCoordinationException">
    /// <c>WINAPP_UI_WORKFLOW_ID</c> is present but invalid. Thrown before any UI side effect.
    /// </exception>
    UiOwnerIdentity Resolve();
}

/// <inheritdoc cref="IUiOwnerResolver"/>
internal sealed class UiOwnerResolver : IUiOwnerResolver
{
    /// <summary>
    /// Environment variable naming one logical UI workflow — not an agent, not an app, and not a
    /// process. Setting the same value across several <c>winapp.exe</c> invocations is what makes them
    /// one cooperating workflow.
    /// </summary>
    internal const string WorkflowIdVariable = "WINAPP_UI_WORKFLOW_ID";

    /// <summary>
    /// Maximum accepted length in UTF-16 code units (<c>string.Length</c>). The value is an opaque
    /// grouping token, so a bound keeps a pathological value from bloating every state write.
    /// </summary>
    internal const int MaxWorkflowIdLength = 256;

    private const string WorkflowDomain = "winapp-ui-workflow-v1\0";
    private const string AnonymousDomain = "winapp-ui-anonymous-v1\0";

    public UiOwnerIdentity Resolve()
    {
        var raw = Environment.GetEnvironmentVariable(WorkflowIdVariable);

        // Deliberately no process-ancestry fallback. Deriving an owner from the parent process silently
        // grouped unrelated commands that merely shared a shell, and just as silently split commands of
        // one workflow that did not — continuity you cannot see is worse than none, so a workflow that
        // wants it now asks for it by name.
        return raw is null
            ? new UiOwnerIdentity(UiOwnerKind.Anonymous, ComputeAnonymousKey())
            : ResolveWorkflow(raw);
    }

    private static UiOwnerIdentity ResolveWorkflow(string raw)
    {
        // An explicitly-set-but-blank value is a scripting mistake (an unset variable expanded to ""),
        // not a request for an empty workflow. Failing here is far cheaper than silently merging every
        // workflow that made the same mistake into one shared owner.
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new UiCoordinationException(
                UiCoordinationErrorCodes.InvalidWorkflowId,
                $"{WorkflowIdVariable} is set but empty or whitespace.",
                $"Set {WorkflowIdVariable} to a non-empty value identifying one logical UI workflow, for example a GUID, or unset it to run this command as a standalone one-shot.");
        }

        if (raw.Length > MaxWorkflowIdLength)
        {
            throw new UiCoordinationException(
                UiCoordinationErrorCodes.InvalidWorkflowId,
                $"{WorkflowIdVariable} is longer than {MaxWorkflowIdLength} characters.",
                $"Set {WorkflowIdVariable} to a short opaque value such as a GUID.");
        }

        return ResolveWorkflowKey(raw);
    }

    private static UiOwnerIdentity ResolveWorkflowKey(string raw)
    {
        try
        {
            return new UiOwnerIdentity(UiOwnerKind.Workflow, ComputeWorkflowKey(raw));
        }
        catch (EncoderFallbackException)
        {
            // An unpaired surrogate is not text. Encoding it with the usual replacement behaviour turns
            // every such value into U+FFFD, so "\uD800", "\uD801" and a literal "\uFFFD" would all hash
            // to one key and three unrelated workflows would silently share an owner — and the desktop.
            throw new UiCoordinationException(
                UiCoordinationErrorCodes.InvalidWorkflowId,
                $"{WorkflowIdVariable} is not valid text: it contains an unpaired UTF-16 surrogate.",
                $"Set {WorkflowIdVariable} to a plain text value identifying one logical UI workflow, for example a GUID.");
        }
    }

    /// <summary>
    /// <c>SHA-256("winapp-ui-workflow-v1\0" + raw value)</c>. Hashing means a workflow id that happens
    /// to contain a path, ticket number or user name never reaches disk, and the domain prefix keeps a
    /// named workflow from ever colliding with an anonymous owner.
    /// </summary>
    /// <remarks>
    /// The encoding is strict on purpose. <see cref="Encoding.UTF8"/> substitutes U+FFFD for anything
    /// it cannot encode, which would map distinct ill-formed ids onto one key; throwing instead makes
    /// that collision structurally impossible rather than merely unlikely.
    /// </remarks>
    /// <exception cref="EncoderFallbackException">
    /// <paramref name="rawWorkflowId"/> is not well-formed UTF-16.
    /// </exception>
    internal static string ComputeWorkflowKey(string rawWorkflowId)
        => Hash(s_strictUtf8.GetBytes(WorkflowDomain + rawWorkflowId));

    /// <summary>UTF-8 that refuses to encode ill-formed UTF-16 rather than substituting U+FFFD.</summary>
    private static readonly UTF8Encoding s_strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// A fresh key per call. Two no-ID commands are therefore different owners even when they come from
    /// the same shell, which is exactly what makes each one a self-contained one-shot.
    /// </summary>
    private static string ComputeAnonymousKey()
        => Hash(Encoding.UTF8.GetBytes(AnonymousDomain + Guid.NewGuid().ToString("N")));

    private static string Hash(byte[] payload)
        => Convert.ToHexStringLower(SHA256.HashData(payload));
}
