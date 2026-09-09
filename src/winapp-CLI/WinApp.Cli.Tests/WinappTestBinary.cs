// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;

namespace WinApp.Cli.Tests;

/// <summary>
/// Locates the published <c>winapp.exe</c> that the gated multiprocess and real-app coordination
/// suites launch as real child processes.
/// </summary>
/// <remarks>
/// <para>
/// The canonical build publishes both <c>artifacts/cli/win-x64</c> and <c>artifacts/cli/win-arm64</c>,
/// and CI downloads the whole <c>cli-binaries</c> artifact, so BOTH runtime identifiers are on disk at
/// once. Probing them in a fixed order therefore picks by luck rather than by architecture: an
/// arm64-first walk hands an ARM64 executable to an x64 runner and every
/// <see cref="System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo)"/> fails. That is
/// invisible on an arm64 dev box, where the wrong-order lookup happens to be right.
/// </para>
/// <para>
/// Resolution is therefore driven by the host architecture and is fail-closed: only the matching RID is
/// ever returned, never a different one. Shared by both suites so the rule cannot drift between them.
/// </para>
/// </remarks>
internal static class WinappTestBinary
{
    /// <summary>How far up from the test output directory to look for the artifacts folder.</summary>
    private const int MaxParentLevels = 8;

    /// <summary>
    /// The publish RID for <paramref name="architecture"/>. Pure, so the choice itself is unit-testable
    /// without a published binary or a second machine.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">
    /// The canonical build publishes only x64 and arm64, so any other architecture has no binary to run
    /// and must fail loudly rather than silently falling back to one that cannot execute.
    /// </exception>
    internal static string RidFor(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "win-x64",
        Architecture.Arm64 => "win-arm64",
        _ => throw new PlatformNotSupportedException(
            $"No winapp.exe is published for {architecture}; the gated UI coordination suites need win-x64 or win-arm64."),
    };

    /// <summary>
    /// The RID this machine can launch. Uses the OS architecture rather than the test process's own:
    /// an arm64 Windows host runs arm64 natively (and x64 only under emulation), while an x64 host
    /// cannot run arm64 at all, so the OS architecture is the one that is always executable.
    /// </summary>
    internal static string CurrentRid => RidFor(RuntimeInformation.OSArchitecture);

    /// <summary>
    /// The published <c>winapp.exe</c> for this host, or <see langword="null"/> when it is not present.
    /// </summary>
    /// <param name="foundOtherRids">
    /// RIDs that were found but are not runnable here — the signature of an architecture mismatch rather
    /// than a missing build.
    /// </param>
    internal static string? TryFind(out IReadOnlyList<string> foundOtherRids)
    {
        var requiredRid = CurrentRid;
        var otherRids = new List<string>();
        var root = AppContext.BaseDirectory;

        for (var i = 0; i < MaxParentLevels && root is not null; i++)
        {
            var cliRoot = Path.Combine(root, "artifacts", "cli");

            var candidate = Path.Combine(cliRoot, requiredRid, "winapp.exe");
            if (File.Exists(candidate))
            {
                foundOtherRids = [];
                return candidate;
            }

            // Only recorded for diagnostics — a non-matching RID is never returned.
            if (Directory.Exists(cliRoot))
            {
                foreach (var rid in new[] { "win-x64", "win-arm64" })
                {
                    if (rid != requiredRid && File.Exists(Path.Combine(cliRoot, rid, "winapp.exe")))
                    {
                        otherRids.Add(rid);
                    }
                }
            }

            root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar));
        }

        // Last resort for a binary copied next to the test assembly, which is built for this host.
        var sideBySide = Path.Combine(AppContext.BaseDirectory, "winapp.exe");
        if (File.Exists(sideBySide))
        {
            foundOtherRids = [];
            return sideBySide;
        }

        foundOtherRids = otherRids;
        return null;
    }

    /// <summary>
    /// The published <c>winapp.exe</c> for this host, or a precise failure explaining which one is
    /// missing.
    /// </summary>
    /// <remarks>
    /// A build that produced the <em>other</em> architecture is a hard configuration error and fails the
    /// test, because silently skipping would report the suite as "not run" when the real problem is that
    /// it can never run here. No build at all stays inconclusive, which is the ordinary local-dev state;
    /// the CI step additionally fails the job on any skip, so neither case can pass unnoticed.
    /// </remarks>
    internal static string Resolve()
    {
        var path = TryFind(out var otherRids);
        if (path is not null)
        {
            return path;
        }

        if (otherRids.Count > 0)
        {
            Assert.Fail(
                $"winapp.exe was published for {string.Join(", ", otherRids)} but not for {CurrentRid}, "
                + $"which is the only architecture this {RuntimeInformation.OSArchitecture} host can launch. "
                + "Publish the matching runtime identifier (scripts\\build-cli.ps1 builds both).");
        }

        throw new AssertInconclusiveException(
            $"winapp.exe was not found. Run scripts\\build-cli.ps1 first so artifacts\\cli\\{CurrentRid}\\winapp.exe exists.");
    }
}
