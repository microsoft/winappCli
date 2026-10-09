// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.Xml.Linq;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Decides how project mode conveys the target architecture to MSBuild. <c>winapp run</c> builds use a
/// global <c>Platform</c> alone, as Visual Studio does (<see cref="ResolveBuildArchitectureAsync"/>).
/// Publish passes (<c>winapp pack</c>, <c>--aot</c>) and projects that can't honor a Platform-only build use
/// <see cref="ResolvePlatformInjection"/>: the RID conveys the arch, plus an explicit Platform only when the
/// target and its whole ProjectReference closure declare a <c>&lt;Platforms&gt;</c> including the arch.
/// Older WindowsAppSDK targets hard-reject the default <c>Platform=AnyCPU</c> for self-contained and packaged
/// builds, which is why that path still injects a Platform when it is provably safe.
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

        // A lone -p RuntimeIdentifier (ExactRuntimeIdentifier) is an explicit exact-RID request. If this graph
        // would otherwise drop the RID, honoring the request by forcing it back in reintroduces the APPX1101
        // duplicate-output failure, and silently dropping it contradicts the request — so reject explicitly.
        if (omitRid && !string.IsNullOrEmpty(options.ExactRuntimeIdentifier))
        {
            throw new ProjectRunException(
                $"-p RuntimeIdentifier={options.ExactRuntimeIdentifier} cannot be honored for this project: its " +
                "ProjectReference graph removes RuntimeIdentifier when a Platform is in effect, which would drop " +
                "the requested RID. Select the architecture with --arch instead, or stop removing " +
                "RuntimeIdentifier from the ProjectReference.");
        }

        return options with
        {
            Platform = token ?? options.Platform,
            OmitRuntimeIdentifier = omitRid,
        };
    }

    /// <summary>
    /// Resolves how a <c>winapp run</c> build conveys the target architecture. Visual Studio builds with a
    /// global <c>Platform</c> and no global <c>RuntimeIdentifier</c>, and that is the default here: a
    /// global RID reaches every project MSBuild touches, and the MSIX/MRT packaging targets query
    /// references without removing it, so a RID-agnostic library gets built twice (with and without the
    /// RID) and packaging fails with APPX1101/PRI175/PRI252/MSB3030. Before committing, the project and every
    /// project it references are evaluated under that Platform; the existing RID-based resolution
    /// (<see cref="ResolvePlatformInjection"/>) is kept when an evaluation fails, when any project in the graph
    /// sets its own conflicting <c>RuntimeIdentifier</c> or enables <c>EnableDynamicPlatformResolution</c>, for
    /// an explicit exact <c>-p RuntimeIdentifier</c>, for a user <c>-p:Platform</c> that doesn't name the
    /// <c>--arch</c> architecture, and for a cross-architecture build whose graph references an analyzer or
    /// source-generator project.
    /// </summary>
    private async Task<ProjectRunOptions> ResolveBuildArchitectureAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        DirectoryInfo workingDir,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(options.ExactRuntimeIdentifier))
        {
            return ResolvePlatformInjection(csproj, options);
        }

        // A user -p:Platform is authoritative and already forwarded; it can carry the architecture alone only
        // when it names --arch. Otherwise keep the RID so --arch still decides what gets built.
        string? platform;
        if (TryGetUserProperty(options.Properties, "Platform", out var userPlatform))
        {
            if (!string.Equals(RunArchHelper.NormalizeArchitecture(userPlatform), options.Architecture, StringComparison.OrdinalIgnoreCase))
            {
                return ResolvePlatformInjection(csproj, options);
            }

            platform = null;
        }
        else
        {
            // Use the project's own <Platforms> token for the arch (preserving casing such as ARM64), else the
            // canonical arch name.
            platform = FindArchPlatformToken(csproj, options.Architecture) ?? options.Architecture;
        }

        var platformOnly = options with { Platform = platform, OmitRuntimeIdentifier = true };

        var graph = await ProbeReferenceGraphAsync(csproj, platformOnly, workingDir, cancellationToken);
        if (graph is null)
        {
            return ResolvePlatformInjection(csproj, options);
        }

        if (graph.ConflictingRuntimeIdentifierProject is not null || graph.DynamicPlatformResolutionProject is not null)
        {
            logger.LogDebug(
                "{UISymbol} {Project} sets its own RuntimeIdentifier or EnableDynamicPlatformResolution; using the existing RID-based resolution.",
                UiSymbols.Note, graph.ConflictingRuntimeIdentifierProject ?? graph.DynamicPlatformResolutionProject);

            // The existing resolution drops the RID for a reference graph that removes it. Keep that for
            // dynamic platform resolution, but a project's own conflicting RuntimeIdentifier would then win
            // and build the wrong architecture (NETSDK1032), so it must always get the RID.
            var resolved = ResolvePlatformInjection(csproj, options);
            return graph.ConflictingRuntimeIdentifierProject is not null
                ? resolved with { OmitRuntimeIdentifier = false }
                : resolved;
        }

        // A global Platform also reaches project-referenced analyzers and source generators, building them for
        // the target architecture. The compiler runs on the SDK's architecture, so for a cross-architecture
        // build it can't load them (CS8034). Visual Studio avoids this by mapping such projects to Any CPU.
        if (graph.HasBuildOnlyReference
            && !string.Equals(RunArchHelper.ArchitectureFromRid(graph.SdkRuntimeIdentifier), options.Architecture, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug(
                "{UISymbol} A project-referenced analyzer must load in the {SdkRid} compiler; using the existing RID-based resolution for --arch {Arch}.",
                UiSymbols.Note, graph.SdkRuntimeIdentifier, options.Architecture);
            return ResolvePlatformInjection(csproj, options);
        }

        return platformOnly;
    }

    /// <summary>What the Platform-only decision needs from the evaluated <c>ProjectReference</c> graph.</summary>
    /// <param name="SdkRuntimeIdentifier">The .NET SDK's RID (the compiler's architecture).</param>
    /// <param name="ConflictingRuntimeIdentifierProject">The first project that sets a RuntimeIdentifier other than win-&lt;arch&gt;.</param>
    /// <param name="DynamicPlatformResolutionProject">The first project that enables EnableDynamicPlatformResolution.</param>
    /// <param name="HasBuildOnlyReference">True when any project references an analyzer or source-generator project.</param>
    internal sealed record ReferenceGraphProbe(
        string SdkRuntimeIdentifier,
        string? ConflictingRuntimeIdentifierProject,
        string? DynamicPlatformResolutionProject,
        bool HasBuildOnlyReference);

    // Bounds concurrent `dotnet msbuild` evaluations while walking a large reference graph.
    private const int MaxConcurrentGraphEvaluations = 4;

    /// <summary>
    /// Evaluates <paramref name="csproj"/> and every managed project it references (transitively, through
    /// runtime references) under the Platform-only globals, using MSBuild's own evaluation so properties and
    /// <c>ProjectReference</c> items from imports, conditions and <c>$(…)</c> paths are all seen. Returns
    /// <see langword="null"/> when any evaluation fails, so the caller keeps the RID-based resolution.
    /// </summary>
    private async Task<ReferenceGraphProbe?> ProbeReferenceGraphAsync(
        FileInfo csproj,
        ProjectRunOptions platformOnly,
        DirectoryInfo workingDir,
        CancellationToken cancellationToken)
    {
        string? sdkRid = null;
        string? conflictingRid = null;
        string? dynamicPlatformResolution = null;
        var buildOnlyReference = false;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { csproj.FullName };
        var level = new List<FileInfo> { csproj };
        using var throttle = new SemaphoreSlim(MaxConcurrentGraphEvaluations);

        while (level.Count > 0)
        {
            (FileInfo Project, string? Output)[] evaluations;
            try
            {
                evaluations = await Task.WhenAll(level.Select(async project =>
                {
                    await throttle.WaitAsync(cancellationToken);
                    try
                    {
                        return (Project: project, Output: await EvaluateArchitectureProbeAsync(
                            project, platformOnly, includeFramework: project == csproj, workingDir, cancellationToken));
                    }
                    finally
                    {
                        throttle.Release();
                    }
                }));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Awaiting canceled tasks surfaces TaskCanceledException; callers expect the token's own exception.
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }

            var next = new List<FileInfo>();
            foreach (var (project, output) in evaluations)
            {
                if (output is null)
                {
                    return null;
                }

                var props = MsBuildPropertyReader.Parse(output, ArchitectureProbeProperties);
                sdkRid ??= GetProp(props, "NETCoreSdkRuntimeIdentifier");
                if (conflictingRid is null && HasConflictingRuntimeIdentifier(props, platformOnly.Architecture))
                {
                    conflictingRid = project.Name;
                }

                if (dynamicPlatformResolution is null && IsTrue(GetProp(props, "EnableDynamicPlatformResolution")))
                {
                    dynamicPlatformResolution = project.Name;
                }

                foreach (var reference in MsBuildPropertyReader.ParseItemMetadata(output, "ProjectReference"))
                {
                    if (IsBuildOnlyReference(reference))
                    {
                        buildOnlyReference = true;
                        continue;
                    }

                    var fullPath = GetProp(reference, "FullPath");
                    if (string.IsNullOrEmpty(fullPath)
                        || !IsManagedProjectPath(fullPath)
                        || !File.Exists(fullPath)
                        || !visited.Add(fullPath))
                    {
                        continue;
                    }

                    if (visited.Count > MaxProjectReferenceClosure)
                    {
                        return null;
                    }

                    next.Add(new FileInfo(fullPath));
                }
            }

            level = next;
        }

        return new ReferenceGraphProbe(sdkRid ?? string.Empty, conflictingRid, dynamicPlatformResolution, buildOnlyReference);
    }

    private static bool IsBuildOnlyReference(IReadOnlyDictionary<string, string> reference) =>
        string.Equals(GetProp(reference, "OutputItemType"), "Analyzer", StringComparison.OrdinalIgnoreCase)
        || string.Equals(GetProp(reference, "ReferenceOutputAssembly"), "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Runs one evaluate-only probe and returns its stdout, or <see langword="null"/> when dotnet could not run
    /// or the evaluation failed.
    /// </summary>
    private async Task<string?> EvaluateArchitectureProbeAsync(
        FileInfo project,
        ProjectRunOptions platformOnly,
        bool includeFramework,
        DirectoryInfo workingDir,
        CancellationToken cancellationToken)
    {
        var args = BuildArchitectureProbeArguments(project, platformOnly, includeFramework);
        logger.LogDebug("{UISymbol} dotnet {Arguments}", UiSymbols.Note, RedactSecretsForDisplay(args));
        try
        {
            var (exitCode, stdout, _) = await dotNetService.RunDotnetCommandAsync(workingDir, args, cancellationToken);
            if (exitCode == 0)
            {
                return stdout;
            }

            logger.LogDebug("{UISymbol} Evaluating {Project} for a Platform-only build exited {ExitCode}; conveying the architecture with the RID.", UiSymbols.Note, project.Name, exitCode);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogDebug("{UISymbol} Could not evaluate {Project} for a Platform-only build; conveying the architecture with the RID.", UiSymbols.Note, project.Name);
            return null;
        }
    }

    private static readonly string[] ArchitectureProbeProperties = ["RuntimeIdentifier", "EnableDynamicPlatformResolution", "NETCoreSdkRuntimeIdentifier"];

    /// <summary>
    /// Builds the evaluate-only <c>dotnet msbuild</c> arguments that read the properties and project
    /// references deciding whether a Platform-only build is honored, under the same globals the build pass
    /// will use. A referenced project is evaluated without the app's pinned <c>TargetFramework</c>.
    /// </summary>
    internal static string BuildArchitectureProbeArguments(FileInfo csproj, ProjectRunOptions options, bool includeFramework = true)
    {
        var tokens = new List<string> { "msbuild", csproj.FullName };
        foreach (var property in ForwardableProperties(options.Properties))
        {
            tokens.Add($"-p:{property}");
        }

        AppendSolutionProperties(tokens, options);
        tokens.Add($"-p:Configuration={options.Configuration}");
        if (!string.IsNullOrWhiteSpace(options.Platform))
        {
            tokens.Add($"-p:Platform={options.Platform}");
        }

        if (includeFramework && !string.IsNullOrWhiteSpace(options.Framework))
        {
            tokens.Add($"-p:TargetFramework={options.Framework}");
        }

        foreach (var property in ArchitectureProbeProperties)
        {
            tokens.Add($"--getProperty:{property}");
        }

        tokens.Add("--getItem:ProjectReference");
        return WindowsCommandLine.JoinArguments(tokens) ?? string.Empty;
    }

    /// <summary>
    /// True when the project sets its own <c>RuntimeIdentifier</c> other than <c>win-&lt;arch&gt;</c>; only a
    /// global RID overrides it. A hard-coded RID fails a Platform-only build with NETSDK1032, and
    /// <c>win-$(Platform)</c> with an upper-case Platform with NETSDK1083. A RID that is exactly
    /// <c>win-&lt;arch&gt;</c> (e.g. from a <c>win-$(Platform)</c> publish profile) agrees with the Platform.
    /// </summary>
    private static bool HasConflictingRuntimeIdentifier(IReadOnlyDictionary<string, string> properties, string architecture)
    {
        var projectRid = GetProp(properties, "RuntimeIdentifier");
        return !string.IsNullOrEmpty(projectRid)
            && !string.Equals(projectRid, RunArchHelper.ToRuntimeIdentifier(architecture), StringComparison.Ordinal);
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
