// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services.Performance;

internal enum WptXamlToolSource
{
    TrustedWindowsKits,
    EnvironmentOverride,
}

internal enum WptXamlToolUnavailableReason
{
    None,
    OverrideNotFullyQualified,
    OverrideDirectoryNotFound,
    ToolkitNotInstalled,
    MissingWpaExporter,
    MissingPerfXaml,
    MissingPerfcoreIni,
    UntrustedWpaExporter,
    UntrustedPerfXaml,
    UnsupportedPerfXamlVersion,
    PerfcoreUnreadable,
    PerfXamlNotRegistered,
}

internal sealed record WptBinaryVersionInfo(string? FileVersion, string? ProductVersion);

internal sealed record WptXamlToolResolution
{
    public required bool IsAvailable { get; init; }
    public required WptXamlToolSource Source { get; init; }
    public required WptXamlToolUnavailableReason UnavailableReason { get; init; }
    public string? ToolkitDirectory { get; init; }
    public string? WpaExporterPath { get; init; }
    public string? XperfPath { get; init; }
    public string? PerfXamlPath { get; init; }
    public string? PerfcoreIniPath { get; init; }
    public string? ToolVersion { get; init; }
    public string? XperfVersion { get; init; }
    public string? PluginVersion { get; init; }
    public string? Remediation { get; init; }
}

internal interface IWptXamlToolResolver
{
    WptXamlToolResolution Resolve();
}

internal interface IWptXamlToolResolverProbe
{
    string? GetEnvironmentVariable(string name);
    IReadOnlyList<string> GetTrustedToolkitRoots();
    bool DirectoryExists(string path);
    bool FileExists(string path);
    string[] ReadAllLines(string path);
    WptBinaryVersionInfo? TryGetVersionInfo(string path);
    bool IsTrustedMicrosoftSigned(string path, ILogger logger);
}

internal sealed class WptXamlToolResolver : IWptXamlToolResolver
{
    internal const string OverrideVariableName = "WINAPP_WPT_DIR";
    internal const string MinimumSupportedWptVersion = "10.1.26100.1";
    internal static readonly Version MinimumSupportedPerfXamlVersion = new(10, 0, 26100, 1);

    private readonly ILogger _logger;
    private readonly IWptXamlToolResolverProbe _probe;

    public WptXamlToolResolver(ILogger<WptXamlToolResolver> logger)
        : this(logger, new SystemWptXamlToolResolverProbe())
    {
    }

    internal WptXamlToolResolver(ILogger logger, IWptXamlToolResolverProbe probe)
    {
        _logger = logger ?? NullLogger.Instance;
        _probe = probe;
    }

    public WptXamlToolResolution Resolve()
    {
        var overrideValue = _probe.GetEnvironmentVariable(OverrideVariableName);
        if (!string.IsNullOrWhiteSpace(overrideValue))
        {
            var overrideDirectory = NormalizeDirectory(overrideValue);
            if (overrideDirectory is null)
            {
                return Unavailable(
                    WptXamlToolSource.EnvironmentOverride,
                    WptXamlToolUnavailableReason.OverrideNotFullyQualified,
                    remediation: $"Set {OverrideVariableName} to an absolute Windows Performance Toolkit directory, or unset it to use the trusted Windows Kits install roots.");
            }

            var authoritativeToolkitDirectory = ResolveToolkitDirectory(overrideDirectory, _probe);
            if (!_probe.DirectoryExists(authoritativeToolkitDirectory))
            {
                return Unavailable(
                    WptXamlToolSource.EnvironmentOverride,
                    WptXamlToolUnavailableReason.OverrideDirectoryNotFound,
                    toolkitDirectory: authoritativeToolkitDirectory,
                    remediation: $"Install Windows Performance Toolkit {MinimumSupportedWptVersion} or newer, or point {OverrideVariableName} at that directory.");
            }

            return EvaluateCandidate(WptXamlToolSource.EnvironmentOverride, authoritativeToolkitDirectory);
        }

        WptXamlToolResolution? firstFailure = null;
        foreach (var candidate in _probe.GetTrustedToolkitRoots()
            .Select(NormalizeDirectory)
            .Where(path => path is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!_probe.DirectoryExists(candidate!))
            {
                continue;
            }

            var resolution = EvaluateCandidate(WptXamlToolSource.TrustedWindowsKits, candidate!);
            if (resolution.IsAvailable)
            {
                return resolution;
            }

            firstFailure ??= resolution;
        }

        return firstFailure ?? Unavailable(
            WptXamlToolSource.TrustedWindowsKits,
            WptXamlToolUnavailableReason.ToolkitNotInstalled,
            remediation: $"Install Windows Performance Toolkit {MinimumSupportedWptVersion} or newer under Program Files\\Windows Kits\\10\\Windows Performance Toolkit, or set {OverrideVariableName} to that directory.");
    }

    private WptXamlToolResolution EvaluateCandidate(WptXamlToolSource source, string toolkitDirectory)
    {
        var exporterPath = Path.Join(toolkitDirectory, "wpaexporter.exe");
        var xperfPath = Path.Join(toolkitDirectory, "xperf.exe");
        var perfXamlPath = Path.Join(toolkitDirectory, "perf_xaml.dll");
        var perfcoreIniPath = Path.Join(toolkitDirectory, "perfcore.ini");

        if (!_probe.FileExists(exporterPath))
        {
            return Unavailable(
                source,
                WptXamlToolUnavailableReason.MissingWpaExporter,
                toolkitDirectory,
                exporterPath,
                perfXamlPath,
                perfcoreIniPath,
                remediation: $"Repair or reinstall Windows Performance Toolkit {MinimumSupportedWptVersion} or newer so wpaexporter.exe is present beside perf_xaml.dll.");
        }

        if (!_probe.FileExists(perfXamlPath))
        {
            return Unavailable(
                source,
                WptXamlToolUnavailableReason.MissingPerfXaml,
                toolkitDirectory,
                exporterPath,
                perfXamlPath,
                perfcoreIniPath,
                ToolVersion: SelectDisplayVersion(_probe.TryGetVersionInfo(exporterPath)),
                remediation: $"Repair or reinstall Windows Performance Toolkit {MinimumSupportedWptVersion} or newer so perf_xaml.dll is present beside wpaexporter.exe.");
        }

        if (!_probe.FileExists(perfcoreIniPath))
        {
            return Unavailable(
                source,
                WptXamlToolUnavailableReason.MissingPerfcoreIni,
                toolkitDirectory,
                exporterPath,
                perfXamlPath,
                perfcoreIniPath,
                ToolVersion: SelectDisplayVersion(_probe.TryGetVersionInfo(exporterPath)),
                PluginVersion: SelectDisplayVersion(_probe.TryGetVersionInfo(perfXamlPath)),
                remediation: "Repair the Windows Performance Toolkit installation so perfcore.ini is restored beside the analysis binaries.");
        }

        var toolVersion = SelectDisplayVersion(_probe.TryGetVersionInfo(exporterPath));
        var xperfVersion = SelectDisplayVersion(_probe.TryGetVersionInfo(xperfPath));
        var pluginVersionInfo = _probe.TryGetVersionInfo(perfXamlPath);
        var pluginVersion = SelectDisplayVersion(pluginVersionInfo);

        if (!_probe.IsTrustedMicrosoftSigned(exporterPath, _logger))
        {
            return Unavailable(
                source,
                WptXamlToolUnavailableReason.UntrustedWpaExporter,
                toolkitDirectory,
                exporterPath,
                perfXamlPath,
                perfcoreIniPath,
                toolVersion,
                pluginVersion,
                "Use the Microsoft-signed Windows Performance Toolkit installation from Windows Kits; do not copy wpaexporter.exe into another directory.");
        }

        if (!_probe.IsTrustedMicrosoftSigned(perfXamlPath, _logger))
        {
            return Unavailable(
                source,
                WptXamlToolUnavailableReason.UntrustedPerfXaml,
                toolkitDirectory,
                exporterPath,
                perfXamlPath,
                perfcoreIniPath,
                toolVersion,
                pluginVersion,
                "Use the Microsoft-signed Windows Performance Toolkit installation from Windows Kits; do not replace perf_xaml.dll with a side-loaded copy.");
        }

        var trustedXperfPath = _probe.FileExists(xperfPath)
            && _probe.IsTrustedMicrosoftSigned(xperfPath, _logger)
                ? xperfPath
                : null;

        // The public support contract is WPA/WPT 10.1.26100.1+. The locally inspectable component
        // that follows the Windows build line is perf_xaml.dll (10.0.26100.* on current kits);
        // wpaexporter.exe uses an independent 11.x product/file version, so the DLL is the safest
        // build-floor proxy while the exporter's own version is still reported for diagnostics.
        if (!MeetsMinimumPerfXamlVersion(pluginVersionInfo))
        {
            return Unavailable(
                source,
                WptXamlToolUnavailableReason.UnsupportedPerfXamlVersion,
                toolkitDirectory,
                exporterPath,
                perfXamlPath,
                perfcoreIniPath,
                toolVersion,
                pluginVersion,
                $"Install Windows Performance Toolkit {MinimumSupportedWptVersion} or newer; older perf_xaml.dll builds do not meet the validated XAML analysis floor.");
        }

        try
        {
            if (!HasActivePerfXamlRegistration(_probe.ReadAllLines(perfcoreIniPath)))
            {
                return Unavailable(
                    source,
                    WptXamlToolUnavailableReason.PerfXamlNotRegistered,
                    toolkitDirectory,
                    exporterPath,
                    perfXamlPath,
                    perfcoreIniPath,
                    toolVersion,
                    pluginVersion,
                    "Add the co-located perf_xaml.dll entry to perfcore.ini and restart WPA/WPAExporter. Winapp intentionally does not modify perfcore.ini for you.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Failed to read {PerfcoreIniPath}.", perfcoreIniPath);
            return Unavailable(
                source,
                WptXamlToolUnavailableReason.PerfcoreUnreadable,
                toolkitDirectory,
                exporterPath,
                perfXamlPath,
                perfcoreIniPath,
                toolVersion,
                pluginVersion,
                "Repair the Windows Performance Toolkit installation and ensure perfcore.ini is readable.");
        }

        return new()
        {
            IsAvailable = true,
            Source = source,
            UnavailableReason = WptXamlToolUnavailableReason.None,
            ToolkitDirectory = toolkitDirectory,
            WpaExporterPath = exporterPath,
            XperfPath = trustedXperfPath,
            PerfXamlPath = perfXamlPath,
            PerfcoreIniPath = perfcoreIniPath,
            ToolVersion = toolVersion,
            XperfVersion = trustedXperfPath is null ? null : xperfVersion,
            PluginVersion = pluginVersion,
        };
    }

    internal static bool HasActivePerfXamlRegistration(IEnumerable<string> lines)
    {
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0
                || line.StartsWith(';')
                || line.StartsWith('#')
                || line.StartsWith("//", StringComparison.Ordinal)
                || (line.StartsWith('[') && line.EndsWith(']')))
            {
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex >= 0)
            {
                var key = line[..separatorIndex].Trim();
                var value = line[(separatorIndex + 1)..].Trim();
                if (IsPerfXamlToken(key))
                {
                    return !IsDisabledValue(value);
                }

                if (ContainsPerfXamlToken(value))
                {
                    return true;
                }

                continue;
            }

            if (ContainsPerfXamlToken(line))
            {
                return true;
            }
        }

        return false;
    }

    internal static string? NormalizeDirectory(string? value)
    {
        var trimmed = value?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(trimmed) || !Path.IsPathFullyQualified(trimmed))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    private static WptXamlToolResolution Unavailable(
        WptXamlToolSource source,
        WptXamlToolUnavailableReason reason,
        string? toolkitDirectory = null,
        string? wpaExporterPath = null,
        string? perfXamlPath = null,
        string? perfcoreIniPath = null,
        string? ToolVersion = null,
        string? PluginVersion = null,
        string? remediation = null)
    {
        return new()
        {
            IsAvailable = false,
            Source = source,
            UnavailableReason = reason,
            ToolkitDirectory = toolkitDirectory,
            WpaExporterPath = wpaExporterPath,
            PerfXamlPath = perfXamlPath,
            PerfcoreIniPath = perfcoreIniPath,
            ToolVersion = ToolVersion,
            PluginVersion = PluginVersion,
            Remediation = remediation,
        };
    }

    private static string ResolveToolkitDirectory(string normalizedDirectory, IWptXamlToolResolverProbe probe)
    {
        if (string.Equals(
            Path.GetFileName(normalizedDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            "Windows Performance Toolkit",
            StringComparison.OrdinalIgnoreCase))
        {
            return normalizedDirectory;
        }

        var nested = Path.Join(normalizedDirectory, "Windows Performance Toolkit");
        return probe.DirectoryExists(nested) ? nested : normalizedDirectory;
    }

    private static bool MeetsMinimumPerfXamlVersion(WptBinaryVersionInfo? versionInfo)
    {
        foreach (var raw in new[] { versionInfo?.ProductVersion, versionInfo?.FileVersion })
        {
            var version = TryParseVersion(raw);
            if (version is not null && version >= MinimumSupportedPerfXamlVersion)
            {
                return true;
            }
        }

        return false;
    }

    private static string? SelectDisplayVersion(WptBinaryVersionInfo? versionInfo)
    {
        var product = TryParseVersion(versionInfo?.ProductVersion);
        var file = TryParseVersion(versionInfo?.FileVersion);
        if (file is not null && HasAtLeastFourComponents(versionInfo?.FileVersion))
        {
            return file.ToString(4);
        }

        if (product is not null)
        {
            return product.ToString(4);
        }

        if (file is not null)
        {
            return file.ToString(4);
        }

        return string.IsNullOrWhiteSpace(versionInfo?.ProductVersion)
            ? versionInfo?.FileVersion
            : versionInfo.ProductVersion;
    }

    private static Version? TryParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var span = value.AsSpan().Trim();
        var length = 0;
        while (length < span.Length && (char.IsDigit(span[length]) || span[length] == '.'))
        {
            length++;
        }

        if (length == 0 || !Version.TryParse(span[..length], out var parsed))
        {
            return null;
        }

        return new Version(
            Math.Max(parsed.Major, 0),
            Math.Max(parsed.Minor, 0),
            Math.Max(parsed.Build, 0),
            Math.Max(parsed.Revision, 0));
    }

    private static bool ContainsPerfXamlToken(string value)
    {
        foreach (var token in value.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IsPerfXamlToken(token))
            {
                return true;
            }
        }

        return IsPerfXamlToken(value);
    }

    private static bool IsPerfXamlToken(string value)
    {
        var candidate = value.Trim().Trim('"');
        if (candidate.Length == 0)
        {
            return false;
        }

        var fileName = Path.GetFileName(candidate);
        return fileName.Equals("perf_xaml.dll", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDisabledValue(string value) =>
        value.Equals("0", StringComparison.OrdinalIgnoreCase)
        || value.Equals("false", StringComparison.OrdinalIgnoreCase)
        || value.Equals("off", StringComparison.OrdinalIgnoreCase)
        || value.Equals("disabled", StringComparison.OrdinalIgnoreCase)
        || value.Equals("no", StringComparison.OrdinalIgnoreCase);

    private static bool HasAtLeastFourComponents(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var separatorCount = 0;
        foreach (var character in value)
        {
            if (character == '.')
            {
                separatorCount++;
            }
            else if (!char.IsDigit(character))
            {
                break;
            }
        }

        return separatorCount >= 3;
    }

    private sealed class SystemWptXamlToolResolverProbe : IWptXamlToolResolverProbe
    {
        public string? GetEnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name);

        public IReadOnlyList<string> GetTrustedToolkitRoots()
        {
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddKitRoot(roots, Environment.GetEnvironmentVariable("ProgramFiles(x86)"));
            AddKitRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
            AddKitRoot(roots, Environment.GetEnvironmentVariable("ProgramFiles"));
            AddKitRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            return [.. roots];
        }

        public bool DirectoryExists(string path) => Directory.Exists(path);

        public bool FileExists(string path) => File.Exists(path);

        public string[] ReadAllLines(string path) => File.ReadAllLines(path);

        public WptBinaryVersionInfo? TryGetVersionInfo(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                var info = FileVersionInfo.GetVersionInfo(path);
                return new(info.FileVersion, info.ProductVersion);
            }
            catch (Exception ex) when (ex is FileNotFoundException or Win32Exception)
            {
                return null;
            }
        }

        public bool IsTrustedMicrosoftSigned(string path, ILogger logger) =>
            AuthenticodeVerifier.IsTrustedMicrosoftSigned(path, logger);

        private static void AddKitRoot(HashSet<string> roots, string? programFiles)
        {
            var normalized = NormalizeDirectory(programFiles);
            if (normalized is not null)
            {
                roots.Add(Path.Join(normalized, "Windows Kits", "10", "Windows Performance Toolkit"));
            }
        }
    }
}

internal static class WptXamlProfileResources
{
    internal const string AllXamlInfoResourceName = "xaml-frame-analysis.wpaProfile";
    internal const string CaptureProfileResourceName = "winapp-performance.wprp";
    internal const string AllXamlInfoNormalizedSha256 = "e4a7a1230ab1d128390ca8635712dc53678b9c2a94ddda9d0a25ebe27bb91c7f";

    public static Stream OpenAllXamlInfo()
    {
        return typeof(WptXamlProfileResources).Assembly.GetManifestResourceStream(AllXamlInfoResourceName)
            ?? throw new InvalidOperationException($"Embedded WPA profile '{AllXamlInfoResourceName}' is missing.");
    }

    public static Stream OpenCaptureProfile()
    {
        return typeof(WptXamlProfileResources).Assembly.GetManifestResourceStream(CaptureProfileResourceName)
            ?? throw new InvalidOperationException($"Embedded WPR profile '{CaptureProfileResourceName}' is missing.");
    }

    public static string ReadAllXamlInfoText()
    {
        using var stream = OpenAllXamlInfo();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
