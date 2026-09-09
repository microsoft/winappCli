// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Compression;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>An official framework package payload on the host, ready to be staged.</summary>
/// <param name="File">Host path of the <c>.msix</c> or <c>.appx</c>.</param>
/// <param name="PackageName">Identity name read from inside the payload, not from its file name.</param>
/// <param name="Version">Identity version read from inside the payload.</param>
/// <param name="Architecture">Identity architecture read from inside the payload.</param>
/// <param name="Publisher">Identity publisher read from inside the payload.</param>
internal sealed record RuntimePayload(
    FileInfo File,
    string PackageName,
    string Version,
    string Architecture,
    string? Publisher);

/// <summary>
/// Reads the real identity of a framework package payload, from the manifest inside it.
/// </summary>
/// <remarks>
/// Never from a file name, a folder name, or a runtime's <c>msix.inventory</c>. All three are known
/// to disagree with what the package actually contains — the Windows App Runtime's DDLM and
/// Singleton are the standing example, where the recorded name and version differ from the manifest
/// — and identity is the entire basis for deciding whether a requirement is met and whether an
/// installed package satisfies it.
/// </remarks>
internal static class RuntimePayloadIdentity
{
    /// <summary>Reads a payload's identity, or null when it is not a readable package.</summary>
    /// <remarks>
    /// Total rather than throwing: a payload that cannot be read cannot satisfy anything, and
    /// skipping it lets a sibling cached copy still be found. Failing the whole run on one corrupt
    /// file in a cache winapp does not own would be a worse answer.
    /// </remarks>
    public static RuntimePayload? TryRead(FileInfo payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        try
        {
            using var archive = ZipFile.OpenRead(payload.FullName);

            var entry = archive.GetEntry("AppxManifest.xml");
            if (entry is null)
            {
                return null;
            }

            using var stream = entry.Open();
            var manifest = AppxManifestDocument.Load(stream);

            if (manifest.IdentityName is not { Length: > 0 } name ||
                manifest.IdentityVersion is not { Length: > 0 } version)
            {
                return null;
            }

            return new RuntimePayload(
                payload,
                name,
                version,
                NormalizeArchitecture(manifest.IdentityProcessorArchitecture),
                manifest.IdentityPublisher);
        }
        catch (Exception ex) when (ex is IOException
                                    or InvalidDataException
                                    or UnauthorizedAccessException
                                    or System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// Canonicalizes a manifest's processor architecture.
    /// </summary>
    /// <remarks>
    /// An absent or unrecognized value is treated as neutral, which is what the manifest schema
    /// means by omitting it. Everything else maps to the same canonical tokens the rest of winapp
    /// uses, so a payload's architecture and a requirement's architecture are always comparable.
    /// </remarks>
    internal static string NormalizeArchitecture(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value, RuntimePackageRequirement.NeutralArchitecture, StringComparison.OrdinalIgnoreCase))
        {
            return RuntimePackageRequirement.NeutralArchitecture;
        }

        return RunArchHelper.NormalizeArchitecture(value) ?? "unknown";
    }

    /// <summary>Whether <paramref name="payload"/> can satisfy <paramref name="requirement"/>.</summary>
    /// <remarks>
    /// Same question the guest will ask of an installed package, asked here of a candidate file, so
    /// the host never stages something it already knows cannot satisfy the constraint.
    /// </remarks>
    public static bool Satisfies(RuntimePayload payload, RuntimePackageRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(requirement);

        return string.Equals(payload.PackageName, requirement.Name, StringComparison.OrdinalIgnoreCase)
            && requirement.AcceptsArchitecture(payload.Architecture)
            && requirement.AcceptsPublisher(payload.Publisher)
            && RuntimeRequirementDiscovery.ComparableVersion(payload.Version)
                >= RuntimeRequirementDiscovery.ComparableVersion(requirement.MinVersion);
    }
}
