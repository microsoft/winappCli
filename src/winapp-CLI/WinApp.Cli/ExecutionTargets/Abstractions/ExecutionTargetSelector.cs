// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Immutable;

namespace WinApp.Cli.ExecutionTargets.Abstractions;

/// <summary>
/// Parses and validates the value a user writes after <c>--on</c>, or as the selector of a
/// <c>winapp target</c> verb.
/// </summary>
/// <remarks>
/// Validation is strict and happens before anything else interprets the surrounding command line.
/// That ordering is the point: <c>winapp target push .\setup.ps1 C:\Setup\setup.ps1</c> is missing
/// its selector, and a lenient parser would silently accept <c>.\setup.ps1</c> as the target and
/// then treat the real source as the destination. Because only a registered provider kind is ever
/// accepted, that command fails naming the problem instead.
/// <para>
/// Kinds are the closed set this build can actually run against. A kind that is planned but not
/// implemented is rejected the same way a typo is — there is nothing to route it to, and reserving
/// the name in the parser would only produce a worse error later.
/// </para>
/// </remarks>
internal static class ExecutionTargetSelector
{
    /// <summary>Separator between a provider kind and that provider's target ID.</summary>
    public const char IdSeparator = ':';

    /// <summary>
    /// Kinds a user may select. <c>local</c> is included so <c>--on local</c> is an explicit way to
    /// say "this machine", which is also the default when <c>--on</c> is omitted.
    /// </summary>
    public static ImmutableArray<string> SupportedKinds { get; } =
    [
        ExecutionTargetRef.LocalKind,
        ExecutionTargetRef.SandboxKind,
    ];

    /// <summary>Kinds other than <c>local</c>, for help text and error suggestions.</summary>
    public static ImmutableArray<string> SelectableRemoteKinds { get; } =
    [
        ExecutionTargetRef.SandboxKind,
    ];

    /// <summary>Parses <paramref name="selector"/> into a validated reference.</summary>
    /// <exception cref="ExecutionTargetException">
    /// The selector is empty, malformed, names an unknown kind, or names an ID the provider for that
    /// kind does not have.
    /// </exception>
    public static ExecutionTargetRef Parse(string? selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            throw Invalid(
                selector,
                "No execution target was given.",
                $"Name one of: {string.Join(", ", SupportedKinds)}.");
        }

        var trimmed = selector.Trim();
        var separator = trimmed.IndexOf(IdSeparator, StringComparison.Ordinal);
        var kind = separator < 0 ? trimmed : trimmed[..separator];
        var id = separator < 0 ? ExecutionTargetRef.DefaultId : trimmed[(separator + 1)..];

        if (kind.Length == 0)
        {
            throw Invalid(
                trimmed,
                $"'{trimmed}' does not name an execution target.",
                $"Write the target kind before '{IdSeparator}', for example 'sandbox'.");
        }

        var matched = SupportedKinds.FirstOrDefault(
            candidate => string.Equals(candidate, kind, StringComparison.OrdinalIgnoreCase));

        if (matched is null)
        {
            throw Invalid(
                trimmed,
                $"'{kind}' is not an execution target this version of winapp can run against.",
                $"Use one of: {string.Join(", ", SupportedKinds)}.");
        }

        if (id.Length == 0)
        {
            throw Invalid(
                trimmed,
                $"'{trimmed}' names the '{matched}' target kind but no target within it.",
                $"Write '{matched}' on its own to use its default target.");
        }

        // Both providers this build ships are single-instance, so any ID other than the default
        // names something that does not exist. Refusing it here, rather than letting it become a
        // second state root and a second lock, is what keeps a typo from quietly creating an
        // unreachable target.
        if (!string.Equals(id, ExecutionTargetRef.DefaultId, StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid(
                trimmed,
                $"The '{matched}' target has a single instance, so '{id}' does not name one of its targets.",
                $"Write '{matched}' on its own.");
        }

        return new ExecutionTargetRef(matched, ExecutionTargetRef.DefaultId);
    }

    /// <summary>Parses without throwing, for validation paths that report their own errors.</summary>
    public static bool TryParse(string? selector, out ExecutionTargetRef? target, out ExecutionTargetErrorInfo? error)
    {
        try
        {
            target = Parse(selector);
            error = null;
            return true;
        }
        catch (ExecutionTargetException ex)
        {
            target = null;
            error = ex.Error;
            return false;
        }
    }

    private static ExecutionTargetException Invalid(string? selector, string message, string userAction) =>
        ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.TargetInvalid,
            message,
            userAction: userAction,
            example: "winapp run . --on sandbox",
            context: selector is null
                ? null
                : new Dictionary<string, string> { ["selector"] = selector });
}
