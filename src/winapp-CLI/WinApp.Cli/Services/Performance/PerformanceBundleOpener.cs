// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace WinApp.Cli.Services.Performance;

internal sealed record PerformanceOpenManifest
{
    public string? SchemaVersion { get; init; }
    public IReadOnlyList<PerformanceArtifact>? Artifacts { get; init; }
    public WprCollectorResult? Wpr { get; init; }
    public ManagedCollectorsResult? Managed { get; init; }
}

internal sealed record PerformanceOpenResult
{
    public required string Status { get; init; }
    public required string Bundle { get; init; }
    public string? Viewer { get; init; }
    public string? Artifact { get; init; }
    public string? Error { get; init; }
    public string? Handoff { get; init; }
}

internal interface IPerformanceViewerResolver
{
    string? ResolveWpa();
}

internal sealed class PerformanceViewerResolver : IPerformanceViewerResolver
{
    public string? ResolveWpa()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        }
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => Path.Join(value!, "Windows Kits", "10", "Windows Performance Toolkit"));
        return SafeExecutableResolver.Resolve(
            "wpa.exe",
            Environment.GetEnvironmentVariable("PATH"),
            candidates);
    }
}

internal interface IPerformanceViewerLauncher
{
    void Launch(ProcessStartInfo startInfo);
}

internal sealed class PerformanceViewerLauncher : IPerformanceViewerLauncher
{
    public void Launch(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to launch '{startInfo.FileName}'.");
    }
}

internal interface IPerformanceBundleOpener
{
    PerformanceOpenResult Open(string bundlePath, string viewer);
}

internal sealed class PerformanceBundleOpener(
    IPerformanceViewerResolver viewerResolver,
    IPerformanceViewerLauncher viewerLauncher) : IPerformanceBundleOpener
{
    private const long MaximumManifestBytes = 1024 * 1024;

    public PerformanceOpenResult Open(string bundlePath, string viewer)
    {
        var bundle = Path.GetFullPath(bundlePath);
        if (!Directory.Exists(bundle))
        {
            return Invalid(bundle, "The performance bundle directory does not exist.");
        }

        var manifestPath = Path.Join(bundle, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return Invalid(bundle, "The bundle does not contain manifest.json.");
        }

        PerformanceOpenManifest manifest;
        try
        {
            var info = new FileInfo(manifestPath);
            if (info.Length > MaximumManifestBytes)
            {
                return Invalid(bundle, "manifest.json exceeds the 1 MiB safety limit.");
            }
            using var stream = info.OpenRead();
            manifest = JsonSerializer.Deserialize(
                stream,
                PerformanceJsonContext.Default.PerformanceOpenManifest)
                ?? throw new JsonException("manifest.json contained no JSON value.");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return Invalid(bundle, $"Cannot read manifest.json: {ex.Message}");
        }
        if (manifest.SchemaVersion is not ("0.1" or "0.2"))
        {
            return Invalid(
                bundle,
                $"Unsupported or missing performance bundle schema version '{manifest.SchemaVersion ?? "<missing>"}'.");
        }

        var artifacts = GetArtifacts(manifest);
        var selectedViewer = viewer.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? SelectAutomaticViewer(artifacts)
            : viewer.ToLowerInvariant();
        return selectedViewer switch
        {
            "wpa" => OpenWithWpa(bundle, artifacts),
            "default" => OpenWithDefaultViewer(bundle, artifacts),
            _ => Invalid(bundle, "--with must be 'wpa' or 'default'."),
        };
    }

    private PerformanceOpenResult OpenWithWpa(
        string bundle,
        IReadOnlyList<PerformanceArtifact> artifacts)
    {
        var artifact = artifacts.FirstOrDefault(value =>
            value.Kind.Equals("etl", StringComparison.OrdinalIgnoreCase));
        if (artifact is null)
        {
            return Unavailable(
                bundle,
                "wpa",
                null,
                "This bundle has no ETL artifact.",
                "Record with --with-wpr, or open managed.nettrace manually in PerfView or Visual Studio.");
        }

        if (!TryResolveArtifact(bundle, artifact.Path, out var artifactPath, out var error))
        {
            return Invalid(bundle, error!);
        }

        var wpaPath = viewerResolver.ResolveWpa();
        if (wpaPath is null)
        {
            return Unavailable(
                bundle,
                "wpa",
                artifactPath,
                "Windows Performance Analyzer was not found.",
                $"Install the Windows Performance Toolkit, then run: winapp perf open \"{bundle}\" --with wpa");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = wpaPath,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(artifactPath!);
        return Launch(bundle, "wpa", artifactPath!, startInfo);
    }

    private PerformanceOpenResult OpenWithDefaultViewer(
        string bundle,
        IReadOnlyList<PerformanceArtifact> artifacts)
    {
        var artifact = artifacts.FirstOrDefault(value =>
                value.Kind.Equals("nettrace", StringComparison.OrdinalIgnoreCase))
            ?? artifacts.FirstOrDefault(value =>
                value.Kind.Equals("etl", StringComparison.OrdinalIgnoreCase))
            ?? (artifacts.Count > 0 ? artifacts[0] : null);
        if (artifact is null)
        {
            return Unavailable(
                bundle,
                "default",
                null,
                "This bundle declares no retained artifacts.",
                "Inspect manifest.json and timeline.ndjson directly.");
        }
        if (!TryResolveArtifact(bundle, artifact.Path, out var artifactPath, out var error))
        {
            return Invalid(bundle, error!);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = artifactPath,
            UseShellExecute = true,
        };
        return Launch(bundle, "default", artifactPath!, startInfo);
    }

    private PerformanceOpenResult Launch(
        string bundle,
        string viewer,
        string artifact,
        ProcessStartInfo startInfo)
    {
        try
        {
            viewerLauncher.Launch(startInfo);
            return new()
            {
                Status = "opened",
                Bundle = bundle,
                Viewer = viewer,
                Artifact = artifact,
            };
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            return Unavailable(
                bundle,
                viewer,
                artifact,
                ex.Message,
                $"Open the original artifact directly: {artifact}");
        }
    }

    private static string SelectAutomaticViewer(IReadOnlyList<PerformanceArtifact> artifacts) =>
        artifacts.Any(value => value.Kind.Equals("etl", StringComparison.OrdinalIgnoreCase))
            ? "wpa"
            : "default";

    private static IReadOnlyList<PerformanceArtifact> GetArtifacts(PerformanceOpenManifest manifest)
    {
        if (manifest.Artifacts is { Count: > 0 })
        {
            return manifest.Artifacts;
        }

        var artifacts = new List<PerformanceArtifact>();
        Add(manifest.Wpr?.Artifact, "etl", "wpr", manifest.Wpr?.FileSize, manifest.Wpr?.RecommendedViewer, manifest.Wpr?.LossStatus);
        AddManaged(manifest.Managed?.DotNetTrace, "nettrace");
        AddManaged(manifest.Managed?.DotNetCounters, "json");
        return artifacts;

        void AddManaged(ManagedCollectorResult? result, string kind) =>
            Add(
                result?.Artifact,
                kind,
                result?.Tool ?? "managed",
                result?.FileSize,
                result?.RecommendedViewer,
                result?.LossStatus);

        void Add(
            string? path,
            string kind,
            string collector,
            long? size,
            string? recommendedViewer,
            string? lossStatus)
        {
            if (path is null)
            {
                return;
            }
            artifacts.Add(new()
            {
                Path = path,
                Kind = kind,
                Collector = collector,
                SizeBytes = size ?? 0,
                RecommendedViewer = recommendedViewer,
                LossStatus = lossStatus,
            });
        }
    }

    internal static bool TryResolveArtifact(
        string bundle,
        string relativePath,
        out string? artifactPath,
        out string? error)
    {
        artifactPath = null;
        error = null;
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathFullyQualified(relativePath))
        {
            error = "The manifest contains an invalid rooted or empty artifact path.";
            return false;
        }

        var root = Path.GetFullPath(bundle);
        try
        {
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            {
                error = "The performance bundle root cannot be a reparse point.";
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"Cannot validate the performance bundle root: {ex.Message}";
            return false;
        }
        var candidate = Path.GetFullPath(relativePath, root);
        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            error = $"The artifact path escapes the bundle root: {relativePath}";
            return false;
        }
        if (!File.Exists(candidate))
        {
            error = $"The manifest artifact does not exist: {relativePath}";
            return false;
        }

        var current = root;
        foreach (var segment in Path.GetRelativePath(root, candidate)
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Join(current, segment);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    error = $"The artifact path crosses a reparse point: {relativePath}";
                    return false;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = $"Cannot validate artifact path '{relativePath}': {ex.Message}";
                return false;
            }
        }

        artifactPath = candidate;
        return true;
    }

    private static PerformanceOpenResult Invalid(string bundle, string error) => new()
    {
        Status = "invalid",
        Bundle = bundle,
        Error = error,
    };

    private static PerformanceOpenResult Unavailable(
        string bundle,
        string viewer,
        string? artifact,
        string error,
        string handoff) => new()
        {
            Status = "unavailable",
            Bundle = bundle,
            Viewer = viewer,
            Artifact = artifact,
            Error = error,
            Handoff = handoff,
        };
}
