// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Xml.Linq;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Decides whether project mode injects an explicit MSBuild <c>Platform</c> (<c>-p:Platform=&lt;arch&gt;</c>).
/// Arch is normally conveyed by the RID alone, but older WindowsAppSDK targets hard-reject the default
/// <c>Platform=AnyCPU</c> (self-contained: "The platform 'AnyCPU' is not supported for Self Contained
/// mode"; packaged: "app host exe cannot be ProcessorArchitecture neutral"). Injecting the Platform fixes
/// those, but a GLOBAL Platform also flows into every <c>ProjectReference</c> and desyncs a
/// no-<c>&lt;Platforms&gt;</c> (implicit-AnyCPU) WinUI library → MSB3030/PRI252. So we inject ONLY when the
/// target and its whole ProjectReference closure declare a <c>&lt;Platforms&gt;</c> that includes the arch —
/// otherwise we fall back to the safe RID-only default.
/// </summary>
internal sealed partial class ProjectRunService
{
    // Guards against pathological reference graphs (cycles are already de-duped by the visited set; this
    // just bounds a maliciously deep or generated chain so the static walk can't run away).
    private const int MaxProjectReferenceClosure = 256;

    /// <summary>
    /// Resolves the explicit <c>Platform</c> to inject for project mode, returning the (possibly updated)
    /// options. Injects <c>-p:Platform=&lt;declared-token&gt;</c> only when it is provably safe:
    /// <list type="number">
    ///   <item>the user did NOT supply their own <c>-p:Platform</c> (that is forwarded as-is), and</item>
    ///   <item>the target project declares a <c>&lt;Platforms&gt;</c> including the target arch, and</item>
    ///   <item>every project in the <c>ProjectReference</c> closure ALSO declares that arch (else a global
    ///   Platform would desync a no-<c>&lt;Platforms&gt;</c> reference → MSB3030/PRI252).</item>
    /// </list>
    /// The exact token from the project's <c>&lt;Platforms&gt;</c> is preserved (e.g. <c>ARM64</c> vs
    /// <c>arm64</c>) so it matches the solution configuration the project defines. Reads are static XML
    /// (no MSBuild round-trip); any ambiguity (unresolvable reference path, missing file, cycle-bounded
    /// overflow) resolves conservatively to "do not inject", preserving today's RID-only behavior.
    /// </summary>
    internal static ProjectRunOptions ResolvePlatformInjection(
        FileInfo csproj,
        ProjectRunOptions options,
        bool requireConcreteRid = false)
    {
        // A user -p:Platform is authoritative and forwarded as-is (WarnOnOverriddenFlags surfaces an
        // arch/Platform mismatch); never override it. It still conveys the architecture, so it counts when
        // deciding whether the RID is redundant below.
        var userPlatform = UserSpecifiesProperty(options.Properties, "Platform");

        // The target must declare a <Platforms> that includes the target arch. Capture the exact declared
        // token so the injected Platform matches the solution config the project defines.
        var token = userPlatform ? null : FindArchPlatformToken(csproj, options.Architecture);

        // Multi-project guard: a global -p:Platform reaches every ProjectReference, so inject only when the
        // whole closure also declares the arch. A no-<Platforms> (implicit-AnyCPU) library is exactly the
        // MSB3030/PRI252 case the RID-only default was chosen to avoid.
        if (token is not null && !ProjectReferenceClosureSupportsArch(csproj, options.Architecture))
        {
            token = null;
        }

        // With an effective Platform the architecture is already conveyed (Platform sets PlatformTarget, so
        // the apphost and compile land on the target arch without a RID). The RID is then redundant — and
        // actively harmful when the closure splits on it: the same project builds both with and without the
        // RID into two output directories, and a packaged app harvests both copies into the MSIX payload,
        // failing with APPX1101 "two or more files with the same destination path". Drop the RID only for
        // that provable case; every other project keeps today's behavior, including a split closure with no
        // effective Platform, where the RID is the only thing conveying the architecture.
        var ridSplit = ProjectReferenceClosureSplitsOnRuntimeIdentifier(csproj);
        if (requireConcreteRid && ridSplit)
        {
            if (userPlatform)
            {
                throw new ProjectRunException(
                    "Native AOT cannot combine an explicit Platform with a project graph that removes RuntimeIdentifier. Remove -p:Platform or stop removing RuntimeIdentifier from ProjectReference.");
            }

            token = null;
        }

        var platformInEffect = userPlatform || token is not null;
        var omitRid = !requireConcreteRid && platformInEffect && ridSplit;

        return options with
        {
            Platform = token ?? options.Platform,
            OmitRuntimeIdentifier = omitRid,
        };
    }

    /// <summary>
    /// Returns the exact token from the project's declared <c>&lt;Platforms&gt;</c> that matches
    /// <paramref name="architecture"/> (case-insensitive, canonicalized), preserving its original casing;
    /// <see langword="null"/> when the project declares no <c>&lt;Platforms&gt;</c> or none matches.
    /// </summary>
    private static string? FindArchPlatformToken(FileInfo project, string architecture)
    {
        foreach (var declared in ReadDeclaredPlatformTokens(project))
        {
            if (string.Equals(RunArchHelper.NormalizeArchitecture(declared), architecture, StringComparison.OrdinalIgnoreCase))
            {
                return declared;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the union of every <c>&lt;Platforms&gt;</c> token declared in the project XML (a semicolon list
    /// per element, unioned across elements so a project that splits Debug/Release lists still resolves).
    /// Returns an empty list when the file is missing/unreadable or declares no <c>&lt;Platforms&gt;</c> —
    /// the empty case is the no-<c>&lt;Platforms&gt;</c> (implicit-AnyCPU) reference the guard must reject.
    /// </summary>
    private static List<string> ReadDeclaredPlatformTokens(FileInfo project)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(project.FullName);
        }
        catch
        {
            return [];
        }

        // SDK-style projects have no default namespace; match by local name so the read is namespace-agnostic.
        var tokens = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in doc.Descendants().Where(e => e.Name.LocalName == "Platforms"))
        {
            foreach (var token in element.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (seen.Add(token))
                {
                    tokens.Add(token);
                }
            }
        }

        return tokens;
    }

    /// <summary>
    /// Walks the transitive <c>ProjectReference</c> closure of <paramref name="start"/> (via static XML,
    /// no MSBuild) and returns <see langword="true"/> only when EVERY referenced <em>runtime</em> project
    /// declares a <c>&lt;Platforms&gt;</c> that includes <paramref name="architecture"/>. Build-only
    /// references (analyzers / source generators — see <c>IsBuildOnlyReference</c>) are excluded from the
    /// walk. Any runtime reference that lacks the arch — including one with no <c>&lt;Platforms&gt;</c> at
    /// all, an unresolvable <c>Include</c> (property/wildcard expansion), or a missing file — returns
    /// <see langword="false"/> so injection falls back to the safe RID-only default. Cycles are de-duped and
    /// the walk is depth-bounded.
    /// </summary>
    private static bool ProjectReferenceClosureSupportsArch(FileInfo start, string architecture)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start.FullName };
        var queue = new Queue<FileInfo>();
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var include in ReadProjectReferenceIncludes(current))
            {
                // An unresolvable Include (MSBuild property or glob) can't be statically verified — be
                // conservative and skip injection rather than force a Platform onto an unknown project.
                if (include.Contains("$(", StringComparison.Ordinal) || include.Contains('*', StringComparison.Ordinal))
                {
                    return false;
                }

                var referenceDir = current.Directory?.FullName ?? Directory.GetCurrentDirectory();
                FileInfo reference;
                try
                {
                    reference = new FileInfo(Path.GetFullPath(Path.Combine(referenceDir, include)));
                }
                catch
                {
                    return false;
                }

                if (!reference.Exists)
                {
                    return false;
                }

                // A referenced project that doesn't declare the arch (or declares no <Platforms>) is the
                // exact case a global Platform would break.
                if (FindArchPlatformToken(reference, architecture) is null)
                {
                    return false;
                }

                if (visited.Add(reference.FullName))
                {
                    if (visited.Count > MaxProjectReferenceClosure)
                    {
                        return false;
                    }

                    queue.Enqueue(reference);
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Reads the <c>Include</c> of every <em>runtime</em> <c>&lt;ProjectReference&gt;</c> in the project XML
    /// (splitting a semicolon list), namespace-agnostic. Build-only references — analyzers / source
    /// generators, marked <c>OutputItemType="Analyzer"</c> or <c>ReferenceOutputAssembly="false"</c> — are
    /// skipped: they emit no arch-specific output and no PRI, so they can neither be desynced by a global
    /// <c>Platform</c> nor trigger MSB3030/PRI252, and a common netstandard2.0 generator (no
    /// <c>&lt;Platforms&gt;</c>) must not veto injection for the app that consumes it. Returns an empty list
    /// when the file is missing/unreadable.
    /// </summary>
    private static List<string> ReadProjectReferenceIncludes(FileInfo project)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(project.FullName);
        }
        catch
        {
            return [];
        }

        var includes = new List<string>();
        foreach (var element in doc.Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
        {
            var include = element.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(include))
            {
                continue;
            }

            if (ProjectReferenceMetadata.IsBuildOnly(element))
            {
                continue;
            }

            foreach (var segment in include.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                includes.Add(segment);
            }
        }

        return includes;
    }

    /// <summary>
    /// Resolves a <c>ProjectReference</c> <c>Include</c> to an existing file relative to the referencing
    /// project. Returns <see langword="false"/> for an unresolvable include (an MSBuild macro or glob), a
    /// malformed path, or a file that doesn't exist — callers treat that as "can't reason about it".
    /// </summary>
    private static bool TryResolveReferencePath(FileInfo referencingProject, string include, out FileInfo resolved)
    {
        resolved = null!;
        if (include.Contains("$(", StringComparison.Ordinal) || include.Contains('*', StringComparison.Ordinal))
        {
            return false;
        }

        var referenceDir = referencingProject.Directory?.FullName ?? Directory.GetCurrentDirectory();
        try
        {
            resolved = new FileInfo(Path.GetFullPath(Path.Combine(referenceDir, include)));
        }
        catch
        {
            return false;
        }

        return resolved.Exists;
    }

    /// <summary>
    /// Reports whether injecting a <c>RuntimeIdentifier</c> would make some project in the
    /// <c>ProjectReference</c> closure build TWICE — once carrying the RID and once without it.
    /// </summary>
    /// <remarks>
    /// MSBuild builds a project once per distinct set of global properties, and an edge carrying
    /// <c>GlobalPropertiesToRemove</c>/<c>UndefineProperties</c> that lists <c>RuntimeIdentifier</c> drops
    /// the RID for that subtree. When the same project is reachable both with and without the RID it is
    /// built into two different output directories (<c>bin\…\&lt;tfm&gt;\</c> and
    /// <c>bin\…\&lt;tfm&gt;\win-&lt;arch&gt;\</c>). For a packaged app both copies are harvested into the
    /// MSIX payload, which fails the build with <c>APPX1101: Payload contains two or more files with the
    /// same destination path</c>. Detecting it statically lets the caller convey the architecture with
    /// <c>Platform</c> alone instead.
    /// </remarks>
    internal static bool ProjectReferenceClosureSplitsOnRuntimeIdentifier(FileInfo start)
    {
        // Walk (project, carriesRid) states; a project seen in both states is built twice.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ridStates = new Dictionary<string, HashSet<bool>>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(FileInfo Project, bool CarriesRid)>();
        queue.Enqueue((start, true));

        var visited = 0;
        while (queue.Count > 0 && visited < MaxProjectReferenceClosure)
        {
            var (current, carriesRid) = queue.Dequeue();
            visited++;

            if (!seen.Add($"{current.FullName}|{carriesRid}"))
            {
                continue;
            }

            if (!ridStates.TryGetValue(current.FullName, out var states))
            {
                states = [];
                ridStates[current.FullName] = states;
            }

            states.Add(carriesRid);
            if (states.Count > 1)
            {
                return true;
            }

            foreach (var (include, stripsRid) in ReadProjectReferenceRidEdges(current))
            {
                if (!TryResolveReferencePath(current, include, out var referenced))
                {
                    // Unresolvable path (an MSBuild macro, or a missing file): stay conservative and
                    // report no split, preserving today's RID behavior.
                    continue;
                }

                queue.Enqueue((referenced, carriesRid && !stripsRid));
            }
        }

        return false;
    }

    /// <summary>
    /// Enumerates runtime-relevant <c>ProjectReference</c> includes paired with whether the edge drops
    /// <c>RuntimeIdentifier</c> from the referenced project's global properties.
    /// </summary>
    private static List<(string Include, bool StripsRid)> ReadProjectReferenceRidEdges(FileInfo project)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(project.FullName);
        }
        catch
        {
            return [];
        }

        var edges = new List<(string, bool)>();
        foreach (var element in doc.Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
        {
            var include = element.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(include) || ProjectReferenceMetadata.IsBuildOnly(element))
            {
                continue;
            }

            // GlobalPropertiesToRemove and UndefineProperties are equivalent spellings.
            var removed = $"{ProjectReferenceMetadata.Read(element, "GlobalPropertiesToRemove")};{ProjectReferenceMetadata.Read(element, "UndefineProperties")}";
            var stripsRid = removed
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(p => string.Equals(p, "RuntimeIdentifier", StringComparison.OrdinalIgnoreCase));

            foreach (var segment in include.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                edges.Add((segment, stripsRid));
            }
        }

        return edges;
    }
}
