// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Xml.Linq;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Reads the item metadata on a <c>&lt;ProjectReference&gt;</c> that decides whether the
/// referenced project contributes an assembly the referencing project can actually use.
/// </summary>
/// <remarks>
/// Both the run-platform decision and the API index need the same answer — one to avoid
/// desyncing a generator's platform, the other to avoid reporting an analyzer's types as
/// callable API — and two copies of these rules would drift apart silently.
/// </remarks>
internal static class ProjectReferenceMetadata
{
    /// <summary>
    /// <see langword="true"/> when a <c>&lt;ProjectReference&gt;</c> is build-time-only — an
    /// analyzer / source generator (<c>OutputItemType="Analyzer"</c>) or one whose output
    /// assembly is deliberately not referenced (<c>ReferenceOutputAssembly="false"</c>).
    /// </summary>
    internal static bool IsBuildOnly(XElement reference)
    {
        if (string.Equals(Read(reference, "OutputItemType"), "Analyzer", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(Read(reference, "ReferenceOutputAssembly"), "false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads MSBuild item metadata authored either as an attribute or as a child element on
    /// the item, namespace-agnostic; <see langword="null"/> when absent.
    /// </summary>
    internal static string? Read(XElement item, string name)
    {
        var attribute = item.Attribute(name)?.Value;
        if (!string.IsNullOrWhiteSpace(attribute))
        {
            return attribute.Trim();
        }

        var child = item.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(child?.Value) ? null : child.Value.Trim();
    }
}
