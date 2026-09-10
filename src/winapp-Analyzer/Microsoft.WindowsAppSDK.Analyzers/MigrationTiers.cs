// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Immutable;

namespace Microsoft.WindowsAppSDK.Analyzers;

/// <summary>
/// Migration-severity tiers carried on a diagnostic's <see cref="Microsoft.CodeAnalysis.Diagnostic.Properties"/>
/// bag. This is orthogonal to the Roslyn <c>DiagnosticSeverity</c> (all migration rules ship at
/// Warning): it lets the out-of-process analyze driver map a finding to the JSON contract's
/// <c>severity</c> field.
///
/// <para><b>startup-crash</b> marks APIs that THROW at runtime if left unaddressed (e.g. view-scoped
/// <c>GetForCurrentView</c> family, <c>DisplayRequest</c>) — these crash the page to a blank window
/// and must never be resolved with a keep-comment.</para>
/// </summary>
internal static class MigrationTiers
{
    /// <summary>Property key on <c>Diagnostic.Properties</c>.</summary>
    public const string PropertyKey = "MigrationTier";

    /// <summary>Runtime-crash tier value (maps to contract <c>severity: startup-crash</c>).</summary>
    public const string StartupCrash = "startup-crash";

    /// <summary>
    /// Sensitive-family tier value (maps to contract <c>severity: sensitive</c>). Drives the
    /// skill's SEQUENTIAL pacing. Carried on <c>WUI1010</c> feature hints for the sensitive
    /// families only (media-capture/speech/audio/sensors/…), NOT on ordinary namespace-rename
    /// hints — so a plain <c>Windows.UI.Xaml</c> hint does not force sequential processing.
    /// </summary>
    public const string Sensitive = "sensitive";

    /// <summary>Ready-made properties bag for a startup-crash finding.</summary>
    public static readonly ImmutableDictionary<string, string?> StartupCrashProperties =
        ImmutableDictionary<string, string?>.Empty.Add(PropertyKey, StartupCrash);

    /// <summary>Ready-made properties bag for a sensitive-family finding.</summary>
    public static readonly ImmutableDictionary<string, string?> SensitiveProperties =
        ImmutableDictionary<string, string?>.Empty.Add(PropertyKey, Sensitive);
}
