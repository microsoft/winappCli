// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>The compatible runtime constraints one resolved application imposes.</summary>
/// <param name="Architecture">Architecture the application was built for.</param>
/// <param name="Packages">Framework MSIX package constraints, from the application's manifest.</param>
/// <param name="Frameworks">Shared .NET framework constraints, from its runtime configuration.</param>
internal sealed record RuntimeRequirements(
    string Architecture,
    IReadOnlyList<RuntimePackageRequirement> Packages,
    IReadOnlyList<RuntimeFrameworkRequirement> Frameworks,
    string? WindowsAppRuntimeVersion = null)
{
    /// <summary>Nothing to provision or verify.</summary>
    public bool IsEmpty =>
        Packages.Count == 0 &&
        Frameworks.Count == 0 &&
        WindowsAppRuntimeVersion is null;

    /// <summary>
    /// Stable content identity of this requirement set.
    /// </summary>
    /// <remarks>
    /// Scoping guest staging by content rather than by deployment means two applications that need
    /// the same runtime share one staged copy, and a rerun that changed nothing transfers nothing.
    /// </remarks>
    public string PlanId
    {
        get
        {
            var builder = new StringBuilder(Architecture);

            foreach (var package in Packages.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append('|').Append(package.Name).Append('@').Append(package.MinVersion)
                    .Append('/').Append(package.Architecture)
                    .Append('/').Append(package.Publisher ?? string.Empty);
            }

            foreach (var framework in Frameworks.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append("|fx:").Append(framework.Name).Append('@').Append(framework.MinVersion)
                    .Append('/').Append(framework.Architecture).Append('/').Append(framework.RollToHighestVersion);
                foreach (var policy in framework.Policies.OrderBy(policy => policy.Version, StringComparer.Ordinal)
                    .ThenBy(policy => policy.RollForward, StringComparer.Ordinal).ThenBy(policy => policy.ApplyPatches))
                {
                    builder.Append('/').Append(policy.Version).Append(':').Append(policy.RollForward)
                        .Append(':').Append(policy.ApplyPatches);
                }
            }

            builder.Append("|wasdk:").Append(WindowsAppRuntimeVersion ?? string.Empty);

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
            return Convert.ToHexStringLower(hash)[..32];
        }
    }
}

/// <summary>
/// Derives runtime requirements from the resolved project and build artifacts
/// (spec §"Runtime provisioning" step 1).
/// </summary>
/// <remarks>
/// Reads what the build already produced rather than re-evaluating the project: the manifest in the
/// materialized layout carries packaged framework dependencies, <c>.deps.json</c> carries the
/// Windows App SDK version used by an unpackaged build, and <c>.runtimeconfig.json</c> carries the
/// shared .NET frameworks and resolution policies the apphost will demand at startup.
/// Absent artifacts impose no requirements; unsupported runtime configurations fail explicitly.
/// </remarks>
internal static class RuntimeRequirementDiscovery
{
    /// <summary>
    /// Frameworks that are part of the application rather than shared, and so are never verified.
    /// </summary>
    /// <remarks>
    /// A self-contained publish lists its frameworks under <c>includedFrameworks</c>, which means the
    /// payload ships beside the apphost. Treating those as guest requirements would fail exactly the
    /// applications that need nothing at all.
    /// </remarks>
    internal const string SelfContainedMarker = "includedFrameworks";

    /// <summary>
    /// Reads <paramref name="sourceRoot"/> — a materialized layout or a build output folder — and
    /// returns what it needs at runtime.
    /// </summary>
    /// <param name="sourceRoot">Host folder about to be deployed into the guest.</param>
    /// <param name="applicationArchitecture">
    /// Resolved build architecture, used when the manifest does not state one.
    /// </param>
    /// <param name="windowsAppRuntimeVersion">Exact restored Runtime package version for native builds.</param>
    public static RuntimeRequirements Discover(
        DirectoryInfo sourceRoot,
        string? applicationArchitecture,
        string? windowsAppRuntimeVersion = null)
    {
        ArgumentNullException.ThrowIfNull(sourceRoot);

        var manifest = FindManifest(sourceRoot);
        var architecture =
            RunArchHelper.NormalizeArchitecture(manifest?.IdentityProcessorArchitecture)
            ?? RunArchHelper.NormalizeArchitecture(applicationArchitecture)
            ?? ReadExecutableArchitecture(sourceRoot, manifest);

        var requirementArchitecture = architecture ?? RuntimePackageRequirement.NeutralArchitecture;
        var requirements = new RuntimeRequirements(
            requirementArchitecture,
            ReadPackageDependencies(manifest, requirementArchitecture),
            ReadSharedFrameworks(sourceRoot, requirementArchitecture),
            ReadWindowsAppRuntimeVersion(sourceRoot) ?? windowsAppRuntimeVersion);

        if (architecture is null && !requirements.IsEmpty)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.RuntimeProvisionFailed,
                "The application's architecture could not be determined for shared runtime provisioning.",
                "Run the project with an explicit architecture or use a layout whose manifest declares its executable and architecture.");
        }

        return requirements;
    }

    private static string? ReadExecutableArchitecture(DirectoryInfo sourceRoot, AppxManifestDocument? manifest)
    {
        if (manifest?.ApplicationExecutable is not { Length: > 0 } executable)
        {
            return null;
        }

        return RunArchHelper.NormalizeArchitecture(PeHelper.DetectPeArchitecture(
            TargetPathSafety.CombineInsideRoot(sourceRoot.FullName, executable)));
    }

    /// <summary>Loads the layout's manifest, or null when there is none to read.</summary>
    private static AppxManifestDocument? FindManifest(DirectoryInfo sourceRoot)
    {
        var manifest = MsixService.FindManifestInDirectory(sourceRoot);
        if (manifest is null)
        {
            return null;
        }

        try
        {
            return AppxManifestDocument.Load(manifest.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            // An unreadable manifest is the registration's problem to report, with far better
            // context than a runtime-discovery failure could give.
            return null;
        }
    }

    /// <summary>
    /// Turns the manifest's <c>PackageDependency</c> entries into compatible constraints.
    /// </summary>
    /// <remarks>
    /// Every declared dependency is carried, not just the Windows App Runtime: the VC runtime is
    /// declared the same way and is just as required. Nothing here decides which ones a payload
    /// exists for — that is resolution's job, and keeping the two separate is what stops discovery
    /// from quietly dropping a requirement it cannot fulfil.
    /// </remarks>
    private static List<RuntimePackageRequirement> ReadPackageDependencies(
        AppxManifestDocument? manifest,
        string architecture)
    {
        var dependencies = manifest?.GetDependenciesElement();
        if (dependencies is null)
        {
            return [];
        }

        var requirements = new List<RuntimePackageRequirement>();

        foreach (var element in dependencies.Elements(AppxManifestDocument.DefaultNs + "PackageDependency"))
        {
            var name = element.Attribute("Name")?.Value;
            var minVersion = element.Attribute("MinVersion")?.Value;
            var publisher = element.Attribute("Publisher")?.Value;

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(minVersion))
            {
                continue;
            }

            requirements.Add(new RuntimePackageRequirement
            {
                Name = name.Trim(),
                MinVersion = minVersion.Trim(),
                Architecture = architecture,

                // Carried through because Windows resolves a framework dependency on (name,
                // publisher): a same-named package from anyone else is a different package, and a
                // check that ignored this would report a satisfied graph that registration rejects.
                Publisher = string.IsNullOrWhiteSpace(publisher) ? null : publisher.Trim(),
            });
        }

        return requirements;
    }

    /// <summary>
    /// Reads the shared .NET frameworks the build's runtime configuration asks for.
    /// </summary>
    /// <remarks>
    /// A folder can contain several <c>.runtimeconfig.json</c> files — one per assembly with an
    /// apphost — so all references are retained, including policies on lower requested versions.
    /// </remarks>
    private static List<RuntimeFrameworkRequirement> ReadSharedFrameworks(
        DirectoryInfo sourceRoot,
        string architecture)
    {
        var requirements = new List<RuntimeFrameworkRequirement>();

        foreach (var file in EnumerateRuntimeConfigs(sourceRoot))
        {
            requirements.AddRange(ReadFrameworkConfig(file, architecture));
        }

        return [.. requirements.GroupBy(requirement => requirement.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(RuntimeFrameworkRequirement.Combine)];
    }

    /// <summary>Reads framework references from an app or a selected shared framework.</summary>
    internal static List<RuntimeFrameworkRequirement> ReadFrameworkConfig(string path, string architecture)
    {
        var options = TryRead(path)?.RuntimeOptions;
        if (options is null || options.IncludedFrameworks is { Count: > 0 })
        {
            return [];
        }

        var declared = Declared(options).ToList();
        var hasRollForward = options.RollForward is not null || declared.Any(framework => framework.RollForward is not null);
        var hasLegacyPolicy = options.ApplyPatches is not null || options.RollForwardOnNoCandidateFx is not null ||
            declared.Any(framework => framework.ApplyPatches is not null || framework.RollForwardOnNoCandidateFx is not null);
        if (hasRollForward && hasLegacyPolicy)
        {
            throw UnsupportedConfig(path, "rollForward cannot be combined with applyPatches or rollForwardOnNoCandidateFx");
        }

        var requirements = new List<RuntimeFrameworkRequirement>();
        foreach (var framework in declared)
        {
            if (string.IsNullOrWhiteSpace(framework.Name) ||
                !Version.TryParse(framework.Version, out var version) || version.Build < 0 || version.Revision >= 0)
            {
                throw UnsupportedConfig(path, "framework references must specify a name and a stable major.minor.patch version");
            }

            var rollForward = framework.RollForward ?? options.RollForward;
            var legacyRollForward = framework.RollForwardOnNoCandidateFx ?? options.RollForwardOnNoCandidateFx;
            var applyPatches = framework.ApplyPatches ?? options.ApplyPatches;
            rollForward ??= legacyRollForward switch
            {
                null or 1 => "Minor",
                0 => "LatestPatch",
                2 => "Major",
                _ => throw UnsupportedConfig(path, "rollForwardOnNoCandidateFx must be 0, 1, or 2"),
            };
            var normalized = ((string[])["Disable", "LatestPatch", "Minor", "Major", "LatestMinor", "LatestMajor"])
                .FirstOrDefault(value => value.Equals(rollForward, StringComparison.OrdinalIgnoreCase))
                ?? throw UnsupportedConfig(path, $"unknown rollForward value '{rollForward}'");

            requirements.Add(new RuntimeFrameworkRequirement
            {
                Name = framework.Name,
                MinVersion = version.ToString(),
                Architecture = architecture,
                RollForward = normalized,
                ApplyPatches = applyPatches ?? true,
            });
        }

        return requirements;
    }

    private static ExecutionTargetException UnsupportedConfig(string path, string detail) =>
        ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.RuntimeProvisionFailed,
            $"Cannot provision the runtime configuration '{Path.GetFileName(path)}': {detail}.",
            "Use a supported stable runtime configuration or publish the app self-contained.");

    private static IEnumerable<RuntimeConfigFramework> Declared(RuntimeConfigOptions options)
    {
        if (options.Framework is { } single)
        {
            yield return single;
        }

        foreach (var framework in options.Frameworks ?? [])
        {
            yield return framework;
        }
    }

    private static IEnumerable<string> EnumerateRuntimeConfigs(DirectoryInfo sourceRoot)
    {
        try
        {
            return sourceRoot.Exists
                ? Directory.EnumerateFiles(sourceRoot.FullName, "*.runtimeconfig.json", SearchOption.TopDirectoryOnly)
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Reads the Windows App SDK Runtime package version restored into an unpackaged build.
    /// </summary>
    /// <remarks>
    /// An unpackaged app has no PackageDependency manifest entry, but its <c>.deps.json</c> records
    /// the exact <c>Microsoft.WindowsAppSDK.Runtime</c> package. That is
    /// the authoritative key for selecting the matching Framework/DDLM/Main/Singleton inventory.
    /// </remarks>
    private static string? ReadWindowsAppRuntimeVersion(DirectoryInfo sourceRoot)
    {
        string? highest = null;

        foreach (var file in EnumerateFiles(sourceRoot, "*.deps.json"))
        {
            try
            {
                using var stream = File.OpenRead(file);
                using var document = JsonDocument.Parse(stream);
                if (!document.RootElement.TryGetProperty("libraries", out var libraries) ||
                    libraries.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var library in libraries.EnumerateObject())
                {
                    const string Prefix = "Microsoft.WindowsAppSDK.Runtime/";
                    if (!library.Name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var version = library.Name[Prefix.Length..];
                    if (highest is null ||
                        NuGetVersionHelper.Compare(version, highest) is > 0)
                    {
                        highest = version;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Another deps file in the output can still carry the requirement.
            }
        }

        return highest;
    }

    private static IEnumerable<string> EnumerateFiles(DirectoryInfo sourceRoot, string pattern)
    {
        try
        {
            return sourceRoot.Exists
                ? Directory.EnumerateFiles(sourceRoot.FullName, pattern, SearchOption.TopDirectoryOnly)
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static RuntimeConfigDocument? TryRead(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, RuntimeConfigJsonContext.Default.RuntimeConfigDocument);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw UnsupportedConfig(path, $"the file could not be read ({ex.Message})");
        }
    }

    /// <summary>
    /// Compares numeric version components for runtime and package identities.
    /// </summary>
    /// <remarks>
    /// <c>Version.TryParse</c> rejects <c>8.0.0-preview.1</c>, and an unparsed version would silently
    /// lose to every other candidate. Comparing the numeric prefix keeps the ordering meaningful
    /// without pulling a full semantic-version implementation into path that only picks a maximum.
    /// </remarks>
    internal static Version ComparableVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return new Version(0, 0);
        }

        var span = version.AsSpan().Trim();
        var cut = span.IndexOfAny('-', '+');
        if (cut >= 0)
        {
            span = span[..cut];
        }

        return Version.TryParse(span, out var parsed) ? parsed : new Version(0, 0);
    }
}

/// <summary>Minimal shape of a <c>.runtimeconfig.json</c>: only what requirements are read from.</summary>
internal sealed class RuntimeConfigDocument
{
    /// <summary>The single <c>runtimeOptions</c> object.</summary>
    public RuntimeConfigOptions? RuntimeOptions { get; init; }
}

/// <summary>The framework references inside <c>runtimeOptions</c>.</summary>
internal sealed class RuntimeConfigOptions
{
    public string? RollForward { get; init; }
    public int? RollForwardOnNoCandidateFx { get; init; }
    public bool? ApplyPatches { get; init; }

    /// <summary>Single framework reference, used when exactly one is declared.</summary>
    public RuntimeConfigFramework? Framework { get; init; }

    /// <summary>Multiple framework references, used when more than one is declared.</summary>
    public List<RuntimeConfigFramework>? Frameworks { get; init; }

    /// <summary>Frameworks published inside the application, which impose no guest requirement.</summary>
    public List<RuntimeConfigFramework>? IncludedFrameworks { get; init; }
}

/// <summary>One framework reference.</summary>
internal sealed class RuntimeConfigFramework
{
    public string? RollForward { get; init; }
    public int? RollForwardOnNoCandidateFx { get; init; }
    public bool? ApplyPatches { get; init; }

    /// <summary>Framework name, for example <c>Microsoft.NETCore.App</c>.</summary>
    public string? Name { get; init; }

    /// <summary>Lowest framework version the build was resolved against.</summary>
    public string? Version { get; init; }
}

/// <summary>Source-generated serializer context for .NET runtime configuration files.</summary>
[JsonSerializable(typeof(RuntimeConfigDocument))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class RuntimeConfigJsonContext : JsonSerializerContext
{
}
