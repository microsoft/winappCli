// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    string? WindowsAppSdkVersion = null)
{
    /// <summary>Nothing to provision or verify.</summary>
    public bool IsEmpty =>
        Packages.Count == 0 &&
        Frameworks.Count == 0 &&
        WindowsAppSdkVersion is null;

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
                    .Append('/').Append(framework.Architecture);
            }

            builder.Append("|wasdk:").Append(WindowsAppSdkVersion ?? string.Empty);

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
/// shared .NET frameworks the apphost will demand at startup.
/// <para>
/// Discovery is deliberately total: an artifact that is absent or unreadable yields no requirement
/// rather than an error. A native C++ or Rust build has no runtime configuration and an unpackaged
/// app has no manifest, and neither is a reason to refuse to run.
/// </para>
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
    /// <param name="fallbackArchitecture">
    /// Architecture to attribute requirements to when the manifest does not state one, normally the
    /// guest's own reported architecture.
    /// </param>
    public static RuntimeRequirements Discover(DirectoryInfo sourceRoot, string fallbackArchitecture)
    {
        ArgumentNullException.ThrowIfNull(sourceRoot);

        var manifest = FindManifest(sourceRoot);
        var architecture =
            RunArchHelper.NormalizeArchitecture(manifest?.IdentityProcessorArchitecture)
            ?? RunArchHelper.NormalizeArchitecture(fallbackArchitecture)
            ?? RunArchHelper.DefaultArchitecture();

        return new RuntimeRequirements(
            architecture,
            ReadPackageDependencies(manifest, architecture),
            ReadSharedFrameworks(sourceRoot, architecture),
            ReadWindowsAppSdkVersion(sourceRoot));
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
    /// apphost — so the union is taken and de-duplicated at the highest constraint. Taking only the
    /// first would miss a requirement whenever the enumeration order changed.
    /// </remarks>
    private static List<RuntimeFrameworkRequirement> ReadSharedFrameworks(
        DirectoryInfo sourceRoot,
        string architecture)
    {
        var highest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in EnumerateRuntimeConfigs(sourceRoot))
        {
            var document = TryRead(file);
            if (document?.RuntimeOptions is not { } options)
            {
                continue;
            }

            // A self-contained publish carries its frameworks in the payload, so it imposes no guest
            // requirement at all — and must not inherit one from a sibling framework-dependent
            // assembly's configuration either.
            if (options.IncludedFrameworks is { Count: > 0 })
            {
                continue;
            }

            foreach (var framework in Declared(options))
            {
                if (string.IsNullOrWhiteSpace(framework.Name) || string.IsNullOrWhiteSpace(framework.Version))
                {
                    continue;
                }

                if (!highest.TryGetValue(framework.Name, out var existing) ||
                    ComparableVersion(framework.Version) > ComparableVersion(existing))
                {
                    highest[framework.Name] = framework.Version;
                }
            }
        }

        AddImpliedFrameworks(highest);

        return
        [
            .. highest
                .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                .Select(entry => new RuntimeFrameworkRequirement
                {
                    Name = entry.Key,
                    MinVersion = entry.Value,
                    Architecture = architecture,
                }),
        ];
    }

    /// <summary>
    /// Adds the frameworks a declared one silently depends on.
    /// </summary>
    /// <remarks>
    /// A WPF or WinForms application's runtime configuration names only
    /// <c>Microsoft.WindowsDesktop.App</c>, but that framework is itself layered on
    /// <c>Microsoft.NETCore.App</c> and cannot load without it. Provisioning only what was written
    /// down would install a desktop framework into a guest with no runtime underneath it and then
    /// report the graph satisfied — the failure would surface as an unexplained startup error.
    /// <para>
    /// The implied version is the declared one: the two ship as a matched pair, and the resolver's
    /// roll-forward then picks the same servicing band for both.
    /// </para>
    /// </remarks>
    private static void AddImpliedFrameworks(Dictionary<string, string> highest)
    {
        foreach (var (dependent, implied) in ImpliedFrameworks)
        {
            if (!highest.TryGetValue(dependent, out var version))
            {
                continue;
            }

            if (!highest.TryGetValue(implied, out var existing) ||
                ComparableVersion(version) > ComparableVersion(existing))
            {
                highest[implied] = version;
            }
        }
    }

    /// <summary>Frameworks that cannot load without another shared framework beneath them.</summary>
    private static readonly (string Dependent, string Implied)[] ImpliedFrameworks =
    [
        ("Microsoft.WindowsDesktop.App", "Microsoft.NETCore.App"),
        ("Microsoft.AspNetCore.App", "Microsoft.NETCore.App"),
    ];

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
    /// Reads the Windows App SDK version restored into an unpackaged build.
    /// </summary>
    /// <remarks>
    /// An unpackaged app has no PackageDependency manifest entry, but its <c>.deps.json</c> records
    /// the exact <c>Microsoft.WindowsAppSDK</c> package the bootstrapper was built against. That is
    /// the authoritative key for selecting the matching Framework/DDLM/Main/Singleton inventory.
    /// </remarks>
    private static string? ReadWindowsAppSdkVersion(DirectoryInfo sourceRoot)
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
                    const string Prefix = "Microsoft.WindowsAppSDK/";
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
            return null;
        }
    }

    /// <summary>
    /// Compares framework versions, tolerating the prerelease suffixes runtime configurations carry.
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
