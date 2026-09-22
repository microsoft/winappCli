// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using WinApp.Cli.Services.Performance;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class WptXamlToolResolverTests
{
    private static readonly string[] CaptureKeywords =
    [
        "CpuConfig",
        "ProcessThread",
        "Loader",
        "SampledProfile",
        "CSwitch",
        "ReadyThread",
        "HardFaults",
        "FileIO",
        "FileIOInit",
    ];
    private static readonly string[] CaptureStacks = ["SampledProfile", "ReadyThread"];

    [TestMethod]
    public void Resolve_ValidOverrideReturnsTrustedResolutionAndVersions()
    {
        var toolkit = @"C:\Wpt";
        var probe = new FakeProbe
        {
            ToolkitRoots = [@"C:\Program Files\Windows Kits\10\Windows Performance Toolkit"],
        };
        probe.Environment[WptXamlToolResolver.OverrideVariableName] = toolkit;
        probe.AddToolkit(toolkit);

        var result = new WptXamlToolResolver(NullLogger.Instance, probe).Resolve();

        Assert.IsTrue(result.IsAvailable);
        Assert.AreEqual(WptXamlToolSource.EnvironmentOverride, result.Source);
        Assert.AreEqual(WptXamlToolUnavailableReason.None, result.UnavailableReason);
        Assert.AreEqual(Path.GetFullPath(toolkit), result.ToolkitDirectory);
        Assert.AreEqual(Path.Join(Path.GetFullPath(toolkit), "wpaexporter.exe"), result.WpaExporterPath);
        Assert.AreEqual(Path.Join(Path.GetFullPath(toolkit), "xperf.exe"), result.XperfPath);
        Assert.AreEqual("11.7.395.48728", result.ToolVersion);
        Assert.AreEqual("10.0.26100.8249", result.PluginVersion);
        CollectionAssert.AreEqual(
            new[] { WptXamlToolResolver.OverrideVariableName },
            probe.EnvironmentRequests);
    }

    [TestMethod]
    public void Resolve_PathOnlyDoesNotCountAsAnInstallLocation()
    {
        var probe = new FakeProbe();
        probe.Environment["PATH"] = @"C:\SidecarTools";
        probe.AddToolkit(@"C:\SidecarTools");
        probe.ToolkitRoots = [];

        var result = new WptXamlToolResolver(NullLogger.Instance, probe).Resolve();

        Assert.IsFalse(result.IsAvailable);
        Assert.AreEqual(WptXamlToolUnavailableReason.ToolkitNotInstalled, result.UnavailableReason);
        Assert.IsNull(result.ToolkitDirectory);
        CollectionAssert.AreEqual(
            new[] { WptXamlToolResolver.OverrideVariableName },
            probe.EnvironmentRequests);
    }

    [TestMethod]
    public void Resolve_OverrideIsAuthoritativeAndDoesNotFallBackToTrustedRoots()
    {
        var probe = new FakeProbe
        {
            ToolkitRoots = [@"C:\Program Files\Windows Kits\10\Windows Performance Toolkit"],
        };
        probe.Environment[WptXamlToolResolver.OverrideVariableName] = @"relative\wpt";
        probe.AddToolkit(@"C:\Program Files\Windows Kits\10\Windows Performance Toolkit");

        var result = new WptXamlToolResolver(NullLogger.Instance, probe).Resolve();

        Assert.IsFalse(result.IsAvailable);
        Assert.AreEqual(WptXamlToolUnavailableReason.OverrideNotFullyQualified, result.UnavailableReason);
        Assert.IsNull(result.ToolkitDirectory);
    }

    [TestMethod]
    public void Resolve_RejectsOlderPerfXamlOrInactiveRegistration()
    {
        var toolkit = @"C:\Program Files (x86)\Windows Kits\10\Windows Performance Toolkit";
        var probe = new FakeProbe
        {
            ToolkitRoots = [toolkit],
        };
        probe.AddToolkit(
            toolkit,
            pluginVersion: new("10.0.22621.1", "10.0.22621.1"),
            perfcoreLines:
            [
                "; perf_xaml.dll",
                "perf_frames.dll",
            ]);

        var versionResult = new WptXamlToolResolver(NullLogger.Instance, probe).Resolve();

        Assert.IsFalse(versionResult.IsAvailable);
        Assert.AreEqual(WptXamlToolUnavailableReason.UnsupportedPerfXamlVersion, versionResult.UnavailableReason);
        Assert.AreEqual("10.0.22621.1", versionResult.PluginVersion);

        probe.AddToolkit(
            toolkit,
            pluginVersion: new("10.0.26100.8249", "10.0.26100.8249"),
            perfcoreLines:
            [
                "; perf_xaml.dll",
                "Dlls=perf_frames.dll;perf_nt.dll",
            ]);

        var registrationResult = new WptXamlToolResolver(NullLogger.Instance, probe).Resolve();

        Assert.IsFalse(registrationResult.IsAvailable);
        Assert.AreEqual(WptXamlToolUnavailableReason.PerfXamlNotRegistered, registrationResult.UnavailableReason);
    }

    [TestMethod]
    public void EmbeddedProfile_MatchesValidatedContentAndExpansionContract()
    {
        var xml = WptXamlProfileResources.ReadAllXamlInfoText();
        var normalized = xml.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
        Assert.AreEqual(WptXamlProfileResources.AllXamlInfoNormalizedSha256, hash);

        var document = XDocument.Parse(xml);
        XNamespace ns = "http://tempuri.org/SerializableElement.xsd";
        var preset = document.Descendants(ns + "Preset").Single();

        Assert.AreEqual("All Xaml Info", (string?)preset.Attribute("Name"));
        Assert.AreEqual("6", (string?)preset.Attribute("KeyColumnCount"));
        Assert.AreEqual("[Series Depth]:<=6", (string?)preset.Attribute("InitialExpansionQuery"));
        Assert.AreEqual(string.Empty, (string?)preset.Attribute("InitialFilterQuery"));
    }

    [TestMethod]
    public void EmbeddedCaptureProfile_HasBoundedKernelAndXamlProviderSet()
    {
        using var stream = WptXamlProfileResources.OpenCaptureProfile();
        var document = XDocument.Load(stream);
        var systemProvider = document.Descendants("SystemProvider").Single();
        var keywords = systemProvider
            .Descendants("Keyword")
            .Select(element => (string?)element.Attribute("Value"))
            .ToArray();
        CollectionAssert.AreEquivalent(
            CaptureKeywords,
            keywords);
        CollectionAssert.AreEquivalent(
            CaptureStacks,
            systemProvider
                .Descendants("Stack")
                .Select(element => (string?)element.Attribute("Value"))
                .ToArray());
        Assert.IsTrue(document
            .Descendants("EventProvider")
            .Any(element =>
                (string?)element.Attribute("Name") == "Microsoft-Windows-XAML"));
        Assert.IsFalse(keywords.Contains("DiskIO", StringComparer.Ordinal));
    }

    private sealed class FakeProbe : IWptXamlToolResolverProbe
    {
        public Dictionary<string, string?> Environment { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> EnvironmentRequests { get; } = [];

        public IReadOnlyList<string> ToolkitRoots { get; set; } = [];

        private HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);

        private HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        private Dictionary<string, WptBinaryVersionInfo> Versions { get; } = new(StringComparer.OrdinalIgnoreCase);

        private Dictionary<string, string[]> TextFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

        private HashSet<string> TrustedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? GetEnvironmentVariable(string name)
        {
            EnvironmentRequests.Add(name);
            return Environment.TryGetValue(name, out var value) ? value : null;
        }

        public IReadOnlyList<string> GetTrustedToolkitRoots() => ToolkitRoots;

        public bool DirectoryExists(string path) => Directories.Contains(path);

        public bool FileExists(string path) => Files.Contains(path);

        public string[] ReadAllLines(string path) => TextFiles[path];

        public WptBinaryVersionInfo? TryGetVersionInfo(string path) =>
            Versions.TryGetValue(path, out var value) ? value : null;

        public bool IsTrustedMicrosoftSigned(string path, Microsoft.Extensions.Logging.ILogger logger) =>
            TrustedFiles.Contains(path);

        public void AddToolkit(
            string toolkitDirectory,
            WptBinaryVersionInfo? exporterVersion = null,
            WptBinaryVersionInfo? pluginVersion = null,
            string[]? perfcoreLines = null)
        {
            var normalized = Path.GetFullPath(toolkitDirectory);
            Directories.Add(normalized);

            var exporter = Path.Join(normalized, "wpaexporter.exe");
            var plugin = Path.Join(normalized, "perf_xaml.dll");
            var xperf = Path.Join(normalized, "xperf.exe");
            var perfcore = Path.Join(normalized, "perfcore.ini");

            Files.Add(exporter);
            Files.Add(plugin);
            Files.Add(xperf);
            Files.Add(perfcore);

            Versions[exporter] = exporterVersion ?? new("11.7.395.48728", "11.7.395+be585f6f10");
            Versions[plugin] = pluginVersion ?? new("10.0.26100.8249 (WinBuild.160101.0800)", "10.0.26100.8249");
            Versions[xperf] = pluginVersion ?? new("10.0.26100.8249 (WinBuild.160101.0800)", "10.0.26100.8249");
            TextFiles[perfcore] = perfcoreLines ?? ["perf_frames.dll", "perf_xaml.dll"];

            TrustedFiles.Add(exporter);
            TrustedFiles.Add(plugin);
            TrustedFiles.Add(xperf);
        }
    }
}
