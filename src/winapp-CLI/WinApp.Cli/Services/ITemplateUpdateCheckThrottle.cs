// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// Throttles the WinUI template-pack staleness check ('dotnet new update --check-only', a NuGet feed
/// round-trip) so it runs at most once per day. Without this, 'winapp new' pays the feed latency on
/// every invocation — listing templates and then immediately scaffolding would wait twice.
/// </summary>
internal interface ITemplateUpdateCheckThrottle
{
    /// <summary>
    /// Returns <see langword="true"/> when a check for <paramref name="installedVersion"/> ran within
    /// the last day, letting the caller skip the network check and reuse <paramref name="latestVersion"/>
    /// (the newest version seen then, or <see langword="null"/> when the pack was up-to-date). Returns
    /// <see langword="false"/> when no recent check exists for that installed version, so a fresh check
    /// is due.
    /// </summary>
    bool TryGetRecentLatest(string installedVersion, out string? latestVersion);

    /// <summary>Whether an automatic check can persist its throttle without relocating bookkeeping.</summary>
    bool CanCheckForUpdates();

    /// <summary>
    /// Records that a staleness check just ran for <paramref name="installedVersion"/>, remembering the
    /// newest available version (<paramref name="latestVersion"/>, or <see langword="null"/>/empty when
    /// the pack was up-to-date) so subsequent runs within the day can reuse it.
    /// </summary>
    void Record(string installedVersion, string? latestVersion);
}
