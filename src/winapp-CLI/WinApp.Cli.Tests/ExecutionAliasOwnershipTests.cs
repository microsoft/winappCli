// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public class ExecutionAliasOwnershipTests
{
    public TestContext TestContext { get; set; } = null!;

    private static readonly ExecutionAliasResolver.AliasTarget Expected =
        new("Contoso_publisher", "Contoso_publisher!App", @"C:\layout\app.exe");

    [TestMethod]
    [DataRow(0u)]
    [DataRow(3u)]
    [DataRow(17u)]
    [DataRow(uint.MaxValue)]
    public void Parse_DoesNotTreatLeadingDwordAsStringCount(uint version)
    {
        var target = ExecutionAliasResolver.ParseAliasTarget(Buffer(version,
            $"{Expected.PackageFamilyName}\0{Expected.ApplicationUserModelId}\0{Expected.TargetExecutable}\0"));
        Assert.AreEqual(Expected, target);
    }

    [TestMethod]
    [DataRow("Contoso_publisher\0")]
    [DataRow("Contoso_publisher\0unfinished")]
    [DataRow("Contoso_publisher\0Contoso_publisher!App\0unfinished")]
    public void Parse_OwnerOnlyRemainsUsableButCannotAuthorizeInspector(string fields)
    {
        var target = ExecutionAliasResolver.ParseAliasTarget(Buffer(3, fields));
        Assert.IsNotNull(target);
        Assert.AreEqual(Expected.PackageFamilyName, target.PackageFamilyName);
        Assert.IsFalse(target.MatchesApplication(Expected));
    }

    [TestMethod]
    [DataRow("short")]
    [DataRow("tag")]
    [DataRow("overrun")]
    [DataRow("odd")]
    [DataRow("empty")]
    [DataRow("unterminated")]
    public void Parse_MalformedOwnerIsRejected(string defect)
    {
        var raw = Buffer(3, "Contoso_publisher\0");
        switch (defect)
        {
            case "short": raw = raw[..7]; break;
            case "tag": raw[0] = 0; break;
            case "overrun": raw[4] = 255; break;
            case "odd": raw[4]--; break;
            case "empty": raw = Buffer(3, "\0"); break;
            case "unterminated": raw = Buffer(3, "Contoso_publisher"); break;
        }
        Assert.IsNull(ExecutionAliasResolver.ParseAliasTarget(raw));
    }

    [TestMethod]
    public void Parse_DoesNotReadPastDeclaredPayload()
    {
        var raw = Buffer(3, "Contoso_publisher\0");
        var combined = raw.Concat(Encoding.Unicode.GetBytes("Contoso_publisher!App\0C:\\layout\\app.exe\0")).ToArray();
        Assert.IsNull(ExecutionAliasResolver.ParseAliasTarget(combined)!.ApplicationUserModelId);
    }

    [TestMethod]
    public void Read_RejectsRelativeMissingAndOrdinaryFiles()
    {
        Assert.IsNull(ExecutionAliasResolver.TryReadAliasTarget("alias.exe"));
        Assert.IsNull(ExecutionAliasResolver.TryReadAliasTarget(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe")));
        var file = Path.GetTempFileName();
        try
        {
            Assert.IsNull(ExecutionAliasResolver.TryReadAliasTarget(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [TestMethod]
    public void Read_GenuineProxies_PreservesOwnerAndReadsApplication()
    {
        var directory = ExecutionAliasResolver.GetDefaultWindowsAppsDirectory();
        if (!Directory.Exists(directory))
        {
            Assert.Inconclusive("This host has no Windows App Execution Alias directory; genuine-proxy evidence remains unverified.");
        }
        var verified = 0;
        var richer = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*.exe"))
        {
            var target = ExecutionAliasResolver.TryReadAliasTarget(file);
            if (target is null)
            {
                continue;
            }
            Assert.IsTrue(ExecutionAliasResolver.TryGetAliasPackageFamilyName(file, out var owner));
            Assert.AreEqual(target.PackageFamilyName, owner);
            verified++;
            if (target.ApplicationUserModelId is { } aumid && target.TargetExecutable is { } exe)
            {
                StringAssert.StartsWith(aumid, owner + "!");
                Assert.IsTrue(Path.IsPathFullyQualified(exe));
                richer++;
            }
        }
        TestContext.WriteLine($"Read-only APPEXECLINK probe: {verified} readable owners, {richer} complete application targets.");
        Assert.IsGreaterThan(0, verified, "No genuine owner evidence available on this host.");
        Assert.IsGreaterThan(0, richer, "No genuine application-target evidence available on this host.");
    }

    [TestMethod]
    public void Select_PublicDefaultFirst_AndEightStableSafeCandidates()
    {
        var candidates = Enumerable.Range(0, 8).Select(i => ExecutionAliasResolver.BuildInspectorAliasName(Expected.PackageFamilyName, i)).ToArray();
        Assert.AreEqual(ExecutionAliasResolver.BuildDefaultAliasName(Expected.PackageFamilyName), candidates[0]);
        Assert.AreEqual(8, candidates.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.IsTrue(candidates.All(ExecutionAliasResolver.IsSafeAliasName));
        Assert.AreEqual(candidates[0], ExecutionAliasResolver.SelectInspectorAlias(Expected, exists: _ => false));
        for (var i = 0; i < candidates.Length; i++)
        {
            Assert.AreEqual(candidates[i], ExecutionAliasResolver.BuildInspectorAliasName(Expected.PackageFamilyName, i));
        }
    }

    [TestMethod]
    [DataRow("unreadable")]
    [DataRow("foreign")]
    [DataRow("wrong-app")]
    [DataRow("wrong-exe")]
    public void Select_SkipsUnverifiableOrWrongApplication(string defect)
    {
        var other = defect switch
        {
            "unreadable" => null,
            "foreign" => Expected with { PackageFamilyName = "Other_publisher" },
            "wrong-app" => Expected with { ApplicationUserModelId = "Contoso_publisher!Other" },
            _ => Expected with { TargetExecutable = @"C:\other\app.exe" },
        };
        var probes = 0;
        var selected = ExecutionAliasResolver.SelectInspectorAlias(Expected,
            exists: _ => ++probes == 1, readTarget: _ => other);
        Assert.AreEqual(ExecutionAliasResolver.BuildInspectorAliasName(Expected.PackageFamilyName, 1), selected);
        Assert.AreEqual(8, probes, "Check all bounded candidates for a healthy target before choosing a free name.");
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(7)]
    public void Select_HealthyAlternativeWinsOverEarlierFreeCandidate(int attempt)
    {
        var healthy = ExecutionAliasResolver.BuildInspectorAliasName(Expected.PackageFamilyName, attempt);
        var probes = 0;
        var selected = ExecutionAliasResolver.SelectInspectorAlias(Expected,
            exists: path => { probes++; return Path.GetFileName(path) == healthy; },
            readTarget: _ => Expected);
        Assert.AreEqual(healthy, selected, "A healthy exact-target alias must not be renamed when an earlier candidate becomes free.");
        Assert.IsLessThanOrEqualTo(8, probes);
    }

    [TestMethod]
    public void Select_ReusesExpectedTarget_AndStopsAfterEightRefusals()
    {
        Assert.AreEqual(ExecutionAliasResolver.BuildDefaultAliasName(Expected.PackageFamilyName),
            ExecutionAliasResolver.SelectInspectorAlias(Expected, exists: _ => true, readTarget: _ => Expected));
        var reads = 0;
        Assert.IsNull(ExecutionAliasResolver.SelectInspectorAlias(Expected,
            exists: _ => true, readTarget: _ => { reads++; return null; }));
        Assert.AreEqual(8, reads);
    }

    private static byte[] Buffer(uint version, string fields)
    {
        var payload = Encoding.Unicode.GetBytes(fields);
        var raw = new byte[12 + payload.Length];
        BitConverter.GetBytes(0x8000001Bu).CopyTo(raw, 0);
        BitConverter.GetBytes(checked((ushort)(4 + payload.Length))).CopyTo(raw, 4);
        BitConverter.GetBytes(version).CopyTo(raw, 8);
        payload.CopyTo(raw, 12);
        return raw;
    }
}
