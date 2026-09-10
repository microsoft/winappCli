// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;

namespace WinApp.Cli.Tests;

/// <summary>
/// Architecture-aware resolution of the published <c>winapp.exe</c> the gated coordination suites
/// launch (<see cref="WinappTestBinary"/>).
/// </summary>
/// <remarks>
/// The canonical build publishes both RIDs and CI downloads both, so a fixed-order probe picks by luck.
/// An arm64-first walk handed an ARM64 binary to the x64 CI runner while looking correct on an arm64 dev
/// box — the failure mode these tests exist to prevent. The RID choice is a pure function precisely so
/// both architectures can be asserted from one machine.
/// </remarks>
[TestClass]
public class WinappTestBinaryTests
{
    private static readonly string[] SupportedRids = ["win-x64", "win-arm64"];

    [TestMethod]
    public void X64HostSelectsTheX64Binary()
    {
        Assert.AreEqual("win-x64", WinappTestBinary.RidFor(Architecture.X64));
    }

    [TestMethod]
    public void Arm64HostSelectsTheArm64Binary()
    {
        Assert.AreEqual("win-arm64", WinappTestBinary.RidFor(Architecture.Arm64));
    }

    [TestMethod]
    public void EachArchitectureSelectsADifferentRid()
    {
        // The whole bug was that both architectures resolved to the same (arm64) binary.
        Assert.AreNotEqual(
            WinappTestBinary.RidFor(Architecture.X64),
            WinappTestBinary.RidFor(Architecture.Arm64));
    }

    [TestMethod]
    public void AnUnpublishedArchitectureFailsRatherThanGuessing()
    {
        // Falling back to a binary that cannot execute here would surface as an opaque Process.Start
        // failure deep inside a coordination test.
        Assert.ThrowsExactly<PlatformNotSupportedException>(() => WinappTestBinary.RidFor(Architecture.X86));
    }

    [TestMethod]
    public void CurrentRidMatchesThisHostAndIsSupported()
    {
        var rid = WinappTestBinary.CurrentRid;

        Assert.AreEqual(WinappTestBinary.RidFor(RuntimeInformation.OSArchitecture), rid);
        CollectionAssert.Contains(SupportedRids, rid);
    }

    [TestMethod]
    public void ResolutionNeverReturnsABinaryForAnotherArchitecture()
    {
        // Guards the fail-closed property directly: whatever is on disk, the path handed to
        // Process.Start is either this host's RID or nothing at all.
        var path = WinappTestBinary.TryFind(out _);
        if (path is null)
        {
            return;
        }

        var directory = Path.GetFileName(Path.GetDirectoryName(path));
        var otherRid = WinappTestBinary.CurrentRid == "win-x64" ? "win-arm64" : "win-x64";
        Assert.AreNotEqual(otherRid, directory,
            $"resolution returned a {otherRid} binary on a {WinappTestBinary.CurrentRid} host");
    }
}
