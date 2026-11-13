// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public class RunArchHelperTests
{
    private static readonly string[] ExpectedArchitectures = ["x64", "arm64", "x86"];

    [TestMethod]
    [DataRow("x64", "x64")]
    [DataRow("X64", "x64")]
    [DataRow("amd64", "x64")]
    [DataRow("x86_64", "x64")]
    [DataRow("arm64", "arm64")]
    [DataRow("ARM64", "arm64")]
    [DataRow("aarch64", "arm64")]
    [DataRow("x86", "x86")]
    [DataRow("win32", "x86")]
    [DataRow("  x64  ", "x64")]
    public void NormalizeArchitecture_KnownValues_Normalized(string input, string expected)
    {
        Assert.AreEqual(expected, RunArchHelper.NormalizeArchitecture(input));
    }

    [TestMethod]
    [DataRow("mips")]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow(null)]
    public void NormalizeArchitecture_UnknownOrEmpty_ReturnsNull(string? input)
    {
        Assert.IsNull(RunArchHelper.NormalizeArchitecture(input));
    }

    [TestMethod]
    [DataRow("x64", "win-x64")]
    [DataRow("arm64", "win-arm64")]
    [DataRow("x86", "win-x86")]
    public void ToRuntimeIdentifier_MapsToWinRid(string arch, string expected)
    {
        Assert.AreEqual(expected, RunArchHelper.ToRuntimeIdentifier(arch));
    }

    [TestMethod]
    [DataRow("win-x64", "x64")]
    [DataRow("win10-arm64", "arm64")]
    [DataRow("win10.0.19041-x64", "x64")]
    [DataRow("win-x86", "x86")]
    [DataRow("win-arm64", "arm64")]
    [DataRow("x64", "x64")]
    [DataRow("arm64", "arm64")]
    [DataRow("x86", "x86")]
    public void ArchitectureFromRid_ExtractsArch(string rid, string expected)
    {
        Assert.AreEqual(expected, RunArchHelper.ArchitectureFromRid(rid));
    }

    [TestMethod]
    [DataRow("win-loongarch64")]
    [DataRow("linux-x64")]
    [DataRow("osx-arm64")]
    [DataRow("linux-musl-x64")]
    [DataRow("windows-x64")]
    [DataRow("winter-x64")]
    [DataRow("winrt-arm64")]
    [DataRow("")]
    [DataRow(null)]
    public void ArchitectureFromRid_Unrecognized_ReturnsNull(string? rid)
    {
        Assert.IsNull(RunArchHelper.ArchitectureFromRid(rid));
    }

    [TestMethod]
    public void SupportedArchitectures_ContainsExpected()
    {
        Assert.AreSequenceEqual(ExpectedArchitectures, RunArchHelper.SupportedArchitectures.ToArray(), SequenceOrder.InAnyOrder);
    }

    [TestMethod]
    public void DefaultArchitecture_IsSupported()
    {
        // The default is the current process arch; on any supported host it must be a known value.
        Assert.Contains(RunArchHelper.DefaultArchitecture(), RunArchHelper.SupportedArchitectures.ToArray());
    }
}
