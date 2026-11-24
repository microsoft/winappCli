// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>Retained observations from a rejected foreground check, not a later foreground query.</summary>
/// <remarks>
/// The comparison and desktop classification use separate foreground reads. Process IDs, and the root
/// when it was not compared, are queried afterward at <paramref name="MetadataAtUtc"/>. These observations
/// are not atomic; a zero process ID means the window's process could not be determined.
/// </remarks>
/// <param name="ComparedAtUtc">UTC time immediately before the foreground comparison.</param>
/// <param name="TargetHwnd">Window the check was asked to protect.</param>
/// <param name="RootHwnd">Target's top-level root, or zero when unavailable.</param>
/// <param name="RootWasCompared">Whether the root came from the comparison rather than the metadata read.</param>
/// <param name="ForegroundHwnd">Foreground window observed by the comparison, or zero.</param>
/// <param name="ClassifiedAtUtc">UTC time immediately before the desktop-classification foreground read.</param>
/// <param name="ClassificationForegroundHwnd">Foreground window observed by the second read, or zero.</param>
/// <param name="MetadataAtUtc">UTC time immediately before querying diagnostic window ownership.</param>
/// <param name="TargetProcessId">Process owning the target at the metadata read.</param>
/// <param name="RootProcessId">Process owning the root at the metadata read.</param>
/// <param name="ForegroundProcessId">Process owning the comparison's foreground handle at the metadata read.</param>
/// <param name="ClassificationForegroundProcessId">Process owning the second read's handle at the metadata read.</param>
public sealed record ForegroundCheckSnapshot(
    DateTimeOffset ComparedAtUtc,
    long TargetHwnd,
    long RootHwnd,
    bool RootWasCompared,
    long ForegroundHwnd,
    DateTimeOffset ClassifiedAtUtc,
    long ClassificationForegroundHwnd,
    DateTimeOffset MetadataAtUtc,
    uint TargetProcessId,
    uint RootProcessId,
    uint ForegroundProcessId,
    uint ClassificationForegroundProcessId);
