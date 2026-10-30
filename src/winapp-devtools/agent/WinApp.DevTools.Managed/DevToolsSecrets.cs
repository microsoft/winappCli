// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.DevTools.Managed;

/// <summary>
/// What DevTools treats as a secret, the same rule as the native engine and the CLI: a property (or a Reactor prop,
/// hook or context) whose name ends in "Password". Its value is shown as <see cref="Redacted"/> and never written.
/// </summary>
internal static class DevToolsSecrets
{
    internal const string Redacted = "<redacted>";

    internal static bool IsSecretName(string? name) =>
        name is not null && name.EndsWith("Password", StringComparison.OrdinalIgnoreCase);
}
