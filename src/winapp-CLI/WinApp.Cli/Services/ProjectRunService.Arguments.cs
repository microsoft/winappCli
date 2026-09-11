// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Pure MSBuild argument and property-token construction for <see cref="ProjectRunService"/>'s restore,
/// build, and evaluate passes. Extracted into its own partial to keep the primary partial under the
/// file-size limits; every method here is static and side-effect-free.
/// </summary>
internal sealed partial class ProjectRunService
{
    /// <summary>
    /// Builds the arguments for the pre-build <c>dotnet restore</c>. Mirrors the build pass's effective
    /// RID / Configuration / user <c>-p</c> / solution properties so the same graph resolves into
    /// <c>project.assets.json</c> that the subsequent <c>--no-restore</c> build consumes. Dedicated-flag
    /// user <c>-p</c> are filtered (see <see cref="ForwardableProperties"/>) so a conflicting
    /// <c>-p:RuntimeIdentifier</c> can't restore a different RID's assets than the build needs. The optional
    /// <paramref name="verbosity"/> is used by quiet mode; otherwise dotnet keeps its default verbosity.
    /// </summary>
    internal static string BuildRestorePassArguments(
        FileInfo csproj,
        ProjectRunOptions options,
        string? verbosity = null)
    {
        var rid = RunArchHelper.ToRuntimeIdentifier(options.Architecture);
        var isSolution = IsSolutionFile(csproj);
        var tokens = new List<string>
        {
            "restore",
            csproj.FullName,
        };

        if (!options.OmitRuntimeIdentifier)
        {
            tokens.Add("-r");
            tokens.Add(rid);
        }

        // 'dotnet restore' has no -c switch; Configuration flows as a property so config-conditional
        // <PackageReference> lands in project.assets.json before the --no-restore build consumes it.
        tokens.Add($"-p:Configuration={options.Configuration}");

        // Mirror the build pass's injected Platform for project restores so platform-conditional
        // PackageReferences resolve consistently. A solution-scoped restore must omit it: unlike a
        // project, MSBuild treats Platform as a requested solution configuration, and configuration-free
        // .slnx files reject it with MSB4126.
        if (!isSolution && !string.IsNullOrWhiteSpace(options.Platform))
        {
            tokens.Add($"-p:Platform={options.Platform}");
        }

        AppendInferredPublishProfile(tokens, csproj, options);

        // Drop dedicated-flag user -p (RID/Configuration/TFM) so the restored graph can't diverge from
        // what the --no-restore build resolves; WarnOnOverriddenFlags surfaces the conflict. Platform is
        // also omitted for solution restores because MSBuild interprets it as a solution configuration.
        foreach (var property in ForwardableProperties(options.Properties))
        {
            if (isSolution && property.StartsWith("Platform=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            tokens.Add($"-p:{property}");
        }

        AppendSolutionProperties(tokens, options);

        if (!string.IsNullOrWhiteSpace(verbosity))
        {
            tokens.Add("-v");
            tokens.Add(verbosity);
        }

        return WindowsCommandLine.JoinArguments(tokens) ?? string.Empty;
    }

    /// <summary>
    /// Builds the arguments for the streaming BUILD pass (a plain <c>dotnet build</c> that streams its
    /// console log). Omits <c>--getProperty</c> (which suppresses that log). Architecture is normally
    /// conveyed by the RID (<c>-r win-&lt;arch&gt;</c>) alone; an explicit <c>-p:Platform</c> is injected
    /// ONLY when <see cref="ProjectRunOptions.Platform"/> was resolved (the target and its whole
    /// <c>ProjectReference</c> closure declare a <c>&lt;Platforms&gt;</c> including the arch — see
    /// <c>ResolvePlatformInjection</c>), which older WindowsAppSDK targets require but a
    /// no-<c>&lt;Platforms&gt;</c> reference would break (MSB3030/PRI252). <c>EnableDynamicPlatformResolution</c>
    /// is never injected. A user-supplied <c>-p:Platform</c> still flows through (and suppresses injection).
    /// </summary>
    internal static string BuildBuildPassArguments(FileInfo csproj, ProjectRunOptions options, string verbosity, string? csWinRTMetadataFolder = null, bool nativeTerminal = false)
    {
        var rid = RunArchHelper.ToRuntimeIdentifier(options.Architecture);

        var tokens = new List<string>
        {
            "build",
            csproj.FullName,
            "-c",
            options.Configuration,
        };

        if (!options.OmitRuntimeIdentifier)
        {
            tokens.Add("-r");
            tokens.Add(rid);
        }

        if (options.NoRestore)
        {
            tokens.Add("--no-restore");
        }

        if (!string.IsNullOrWhiteSpace(options.Framework))
        {
            tokens.Add("-f");
            tokens.Add(options.Framework);
        }

        tokens.Add("-v");
        tokens.Add(verbosity);

        // Terminal-logger regime depends on how winapp launches dotnet. Redirected (streaming/json/quiet):
        // pin -tl:off for clean append-only output. Native TTY: omit the token so dotnet's -tl:auto enables
        // the live display. Never -tl:on. Build pass only; the evaluate pass is untouched.
        if (!nativeTerminal)
        {
            tokens.Add("-tl:off");
        }

        // Drop dedicated-switch dupes (-c/-r/-f) so the build and evaluate passes can't resolve a
        // different Configuration/RID/TFM; a user -p:Platform / EDPR still flows through and is respected.
        foreach (var property in ForwardableProperties(options.Properties))
        {
            tokens.Add($"-p:{property}");
        }

        // Inject the resolved Platform (guard-gated in ResolvePlatformInjection) so WindowsAppSDK
        // self-contained / packaged builds don't fail on the default Platform=AnyCPU. Null = RID-only.
        if (!string.IsNullOrWhiteSpace(options.Platform))
        {
            tokens.Add($"-p:Platform={options.Platform}");
        }

        AppendInferredPublishProfile(tokens, csproj, options);

        AppendSolutionProperties(tokens, options);

        // SHIM (temporary): inject the resolved ref-pack winmd folder so cswinrt.exe finds contract winmds
        // without a registered Windows SDK. See CsWinRTMetadataShimService.
        if (!string.IsNullOrEmpty(csWinRTMetadataFolder))
        {
            tokens.Add($"-p:CsWinRTWindowsMetadata={csWinRTMetadataFolder}");
        }

        return WindowsCommandLine.JoinArguments(tokens) ?? string.Empty;
    }

    /// <summary>
    /// Builds the arguments for the EVALUATE pass: a fast, side-effect-free <c>dotnet msbuild
    /// --getProperty</c> returning resolved output paths as JSON. Fed the SAME effective build inputs as
    /// the build pass (including a resolved <c>-p:Platform</c>, when any) so its <c>TargetDir</c>/
    /// <c>RunCommand</c> match what was built. <c>dotnet msbuild</c> rejects <c>-c</c>/<c>-r</c> (MSB1001),
    /// so Configuration/RID/TFM/Platform go as <c>-p:</c> emitted LAST (MSBuild last-wins beats a
    /// conflicting user <c>-p</c>). <paramref name="includeRuntimeIdentifier"/> and
    /// <paramref name="includePlatform"/> and <paramref name="includePublishProfile"/> are
    /// <see langword="false"/> only for the <c>--no-build</c>
    /// output-discovery fallback (see <c>BuildAndResolveAsync</c>): an app previously built by Visual Studio
    /// or a plain <c>dotnet build</c> injects NEITHER a RID nor a Platform, so its output sits at
    /// <c>bin\&lt;cfg&gt;\&lt;tfm&gt;\</c> — which only resolves when both are omitted.
    /// </summary>
    internal static string BuildEvaluateArguments(
        FileInfo csproj,
        ProjectRunOptions options,
        string? csWinRTMetadataFolder = null,
        bool includeRuntimeIdentifier = true,
        bool includePlatform = true,
        bool includePublishProfile = true)
    {
        var rid = RunArchHelper.ToRuntimeIdentifier(options.Architecture);

        var tokens = new List<string>
        {
            "msbuild",
            csproj.FullName,
        };

        // Drop dedicated-switch dupes (same filter as the build pass) so the two passes stay in lock-step;
        // the dedicated -p: equivalents are emitted below. A user -p:Platform / EDPR flows through.
        foreach (var property in ForwardableProperties(options.Properties))
        {
            tokens.Add($"-p:{property}");
        }

        AppendSolutionProperties(tokens, options);

        tokens.Add($"-p:Configuration={options.Configuration}");
        if (includeRuntimeIdentifier && !options.OmitRuntimeIdentifier)
        {
            tokens.Add($"-p:RuntimeIdentifier={rid}");
        }
        if (!string.IsNullOrWhiteSpace(options.Framework))
        {
            tokens.Add($"-p:TargetFramework={options.Framework}");
        }

        // Same resolved Platform the build pass injected (or none) so the evaluate reads TargetDir/RunCommand
        // from the SAME bin\<Platform>\… the build wrote. Emitted last so it beats a stray user -p.
        if (includePlatform && !string.IsNullOrWhiteSpace(options.Platform))
        {
            tokens.Add($"-p:Platform={options.Platform}");
        }

        AppendInferredPublishProfile(tokens, csproj, options, includePublishProfile);

        // SHIM (temporary): keep the evaluate pass's inputs identical to the build pass.
        if (!string.IsNullOrEmpty(csWinRTMetadataFolder))
        {
            tokens.Add($"-p:CsWinRTWindowsMetadata={csWinRTMetadataFolder}");
        }

        foreach (var name in RequestedProperties)
        {
            tokens.Add($"--getProperty:{name}");
        }

        return WindowsCommandLine.JoinArguments(tokens) ?? string.Empty;
    }

    /// <summary>
    /// Builds the arguments for the single-file BUILD pass: <c>dotnet build &lt;file&gt;.cs</c>.
    /// <para>
    /// No <c>-p:Platform</c> is ever injected — a file-based app accepts <c>Platform</c> but ignores it
    /// for RID selection. A <c>-r win-&lt;arch&gt;</c> IS injected when
    /// <see cref="SingleFileRunOptions.InjectedRuntimeIdentifier"/> is set, which is what lets a plain
    /// <c>winapp run app.cs</c> build a self-contained Windows App SDK app instead of failing as
    /// <c>AnyCPU</c>. That is safe because <see cref="BuildSingleFileEvaluateArguments"/> emits the SAME
    /// set from the same options, so the evaluate reads back the RID-qualified directory this pass wrote
    /// rather than the two disagreeing about where the app is.
    /// </para>
    /// </summary>
    internal static string BuildSingleFileBuildPassArguments(
        FileInfo singleFile,
        SingleFileRunOptions options,
        string verbosity,
        bool nativeTerminal = false)
    {
        var tokens = new List<string>
        {
            "build",
            singleFile.FullName,
            "-c",
            options.Configuration,
        };

        AppendSingleFileRuntimeIdentifier(tokens, options);

        if (options.NoRestore)
        {
            tokens.Add("--no-restore");
        }

        tokens.Add("-v");
        tokens.Add(verbosity);

        // Same terminal-logger regime as the .csproj build pass: pin -tl:off when winapp redirects the
        // output, omit it on a real TTY so dotnet's native live display renders.
        if (!nativeTerminal)
        {
            tokens.Add("-tl:off");
        }

        // Reserve Configuration, which winapp owns via -c, plus RuntimeIdentifier whenever a RID is being
        // injected — MSBuild is last-wins and these -p tokens are emitted AFTER -r, so forwarding a
        // conflicting RuntimeIdentifier would silently override the architecture winapp resolved.
        // TargetFramework is deliberately NOT reserved: single-file mode rejects --framework, so -p is the
        // only way to express it, and reusing project mode's wider filter would drop it from both passes
        // and silently ignore what the user asked for.
        foreach (var property in SingleFileForwardableProperties(options.Properties, options.InjectedRuntimeIdentifier is not null))
        {
            tokens.Add($"-p:{property}");
        }

        return WindowsCommandLine.JoinArguments(tokens) ?? string.Empty;
    }

    /// <summary>
    /// Builds the arguments for the single-file EVALUATE pass.
    /// <para>
    /// This uses <c>dotnet build … --getProperty:…</c> rather than <c>dotnet msbuild</c> — which the
    /// <c>.csproj</c> evaluate pass uses — because MSBuild has no <c>.cs</c> project loader and rejects a
    /// file-based app with <c>MSB4025: The project file could not be loaded</c>. The virtual-project
    /// synthesis only exists inside the <c>dotnet build</c>/<c>dotnet run</c> CLI path. Passing
    /// <c>--getProperty</c> makes the invocation evaluate WITHOUT building, so this stays cheap.
    /// </para>
    /// Fed the SAME Configuration, injected RID, and user <c>-p</c> as the build pass so the properties it
    /// reads describe the output that was actually written.
    /// </summary>
    internal static string BuildSingleFileEvaluateArguments(FileInfo singleFile, SingleFileRunOptions options, bool includeRuntimeIdentifier = true)
    {
        var tokens = new List<string>
        {
            "build",
            singleFile.FullName,
            "-c",
            options.Configuration,
        };

        if (includeRuntimeIdentifier)
        {
            AppendSingleFileRuntimeIdentifier(tokens, options);
        }

        // Same reservation as the build pass, so both passes agree on the RID.
        foreach (var property in SingleFileForwardableProperties(options.Properties, includeRuntimeIdentifier && options.InjectedRuntimeIdentifier is not null))
        {
            tokens.Add($"-p:{property}");
        }

        foreach (var name in SingleFileRequestedProperties)
        {
            tokens.Add($"--getProperty:{name}");
        }

        return WindowsCommandLine.JoinArguments(tokens) ?? string.Empty;
    }

    /// <summary>
    /// Builds a cheap, side-effect-free probe that reads ONE evaluated property from a file-based app.
    /// Deliberately omits the injected RuntimeIdentifier, since the probe exists to discover whether the
    /// app declares one of its own.
    /// </summary>
    internal static string BuildSingleFileProbeArguments(FileInfo singleFile, SingleFileRunOptions options, string propertyName)
    {
        var tokens = new List<string>
        {
            "build",
            singleFile.FullName,
            "-c",
            options.Configuration,
        };

        // No ridInjected filter here on purpose: the probe omits -r entirely, so a user
        // -p:RuntimeIdentifier is exactly what it needs to see to answer "does the app declare one?".
        foreach (var property in SingleFileForwardableProperties(options.Properties))
        {
            tokens.Add($"-p:{property}");
        }

        tokens.Add($"--getProperty:{propertyName}");

        return WindowsCommandLine.JoinArguments(tokens) ?? string.Empty;
    }

    /// <summary>
    /// Conveys the target architecture to a single-file pass as <c>-r win-&lt;arch&gt;</c>, matching what
    /// project mode injects. Both single-file passes call this with the same options, so the evaluate
    /// reads back the same RID-qualified output directory the build wrote. No-op when the app declares
    /// its own <c>RuntimeIdentifier</c> (see <c>ResolveSingleFileRuntimeIdentifierAsync</c>).
    /// </summary>
    private static void AppendSingleFileRuntimeIdentifier(List<string> tokens, SingleFileRunOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.InjectedRuntimeIdentifier))
        {
            return;
        }

        tokens.Add("-r");
        tokens.Add(options.InjectedRuntimeIdentifier);
    }

    /// <summary>
    /// The user <c>-p</c> properties a single-file pass forwards.
    /// </summary>
    /// <remarks>
    /// <c>Configuration</c> is always filtered because <c>-c</c> already sets it.
    /// <para>
    /// <c>RuntimeIdentifier</c> is filtered ONLY when a RID is being injected. MSBuild is last-wins and
    /// the forwarded <c>-p</c> is emitted after <c>-r</c>, so leaving it in would let
    /// <c>--arch x64 -p RuntimeIdentifier=win-arm64</c> build arm64 while winapp provisions an x64
    /// Windows App Runtime — a silently mismatched app. This mirrors project mode's dedicated-flag
    /// precedence. When NO RID is injected the property is forwarded untouched, because that is exactly
    /// the case where the user owns the choice (see <c>ResolveSingleFileRuntimeIdentifierAsync</c>).
    /// </para>
    /// </remarks>
    /// <summary>
    /// The MSBuild property names a single <c>-p</c> token sets, trimmed.
    /// </summary>
    /// <remarks>
    /// Shared so every decision keyed on "does the user set property X?" parses the token identically.
    /// They previously diverged: the RID-injection check used a raw <c>StartsWith</c> while this filter
    /// trimmed, so <c>-p " RuntimeIdentifier=win-arm64"</c> was invisible to the first (winapp injected
    /// the host RID over it) and visible to the second (which then dropped the user's value) — the app
    /// built for the wrong architecture.
    /// </remarks>
    internal static IEnumerable<string> PropertyNames(string property) =>
        property.Split(';').Select(segment => segment.Split('=', 2)[0].Trim());

    private static IEnumerable<string> SingleFileForwardableProperties(IReadOnlyList<string> properties, bool ridInjected = false) =>
        properties.Where(p => !PropertyNames(p)
            .Any(name => name.Equals("Configuration", StringComparison.OrdinalIgnoreCase)
                || (ridInjected && name.Equals("RuntimeIdentifier", StringComparison.OrdinalIgnoreCase))));


    /// <summary>
    /// Appends the <c>Solution*</c> MSBuild properties a solution build normally sets — most importantly
    /// <c>$(SolutionDir)</c> — when the target was resolved from a solution, so projects referencing them
    /// build as they do under <c>dotnet build &lt;sln&gt;</c> / VS. No-op for a bare <c>.csproj</c>.
    /// </summary>
    private static void AppendSolutionProperties(List<string> tokens, ProjectRunOptions options)
    {
        if (options.Solution is not { } solution)
        {
            return;
        }

        // MSBuild is last-wins and user -p is emitted first, so skip any Solution* the user set explicitly
        // (an explicit -p:SolutionDir=… always wins).
        foreach (var token in BuildSolutionPropertyTokens(solution))
        {
            if (UserSpecifiesProperty(options.Properties, SolutionPropertyName(token)))
            {
                continue;
            }

            tokens.Add(token);
        }
    }

    /// <summary>
    /// Builds the <c>-p:</c> property tokens used to classify runnable candidates so the evaluate reads
    /// <c>OutputType</c>/test markers under the SAME globals the build uses. Mirrors the property section
    /// of <see cref="BuildEvaluateArguments"/>: forwardable user <c>-p</c>, then <c>Solution*</c> props
    /// (skipping any the user set), then Configuration/RID/TFM LAST. Null <paramref name="inputs"/> emits
    /// solution props only (classification against MSBuild defaults).
    /// </summary>
    private static IReadOnlyList<string> BuildClassificationPropertyTokens(
        ProjectClassificationInputs? inputs,
        FileInfo? solution)
    {
        if (inputs is null)
        {
            return solution is null ? [] : BuildSolutionPropertyTokens(solution);
        }

        var tokens = new List<string>();

        foreach (var property in ForwardableProperties(inputs.Properties))
        {
            tokens.Add($"-p:{property}");
        }

        if (solution is not null)
        {
            foreach (var token in BuildSolutionPropertyTokens(solution))
            {
                if (UserSpecifiesProperty(inputs.Properties, SolutionPropertyName(token)))
                {
                    continue;
                }

                tokens.Add(token);
            }
        }

        tokens.Add($"-p:Configuration={inputs.Configuration}");
        tokens.Add($"-p:RuntimeIdentifier={RunArchHelper.ToRuntimeIdentifier(inputs.Architecture)}");
        if (!string.IsNullOrWhiteSpace(inputs.Framework))
        {
            tokens.Add($"-p:TargetFramework={inputs.Framework}");
        }

        return tokens;
    }

    /// <summary>True when the user passed a <c>-p Name=Value</c> for <paramref name="name"/> (case-insensitive).</summary>
    private static bool UserSpecifiesProperty(IReadOnlyList<string> properties, string name) =>
        properties.SelectMany(PropertySegments)
            .Any(segment => PropertyName(segment).Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads the effective value of the user's <c>-p Name=Value</c> for <paramref name="name"/>
    /// (case-insensitive). MSBuild is last-wins, so this returns the LAST non-empty match; an empty value
    /// (<c>-p:TargetFramework=</c>) is treated as "not specified" and doesn't hide a later valid one.
    /// Returns <see langword="true"/> only when a non-empty value was found.
    /// </summary>
    private static bool TryGetUserProperty(IReadOnlyList<string> properties, string name, out string value)
    {
        value = string.Empty;
        var found = false;
        foreach (var property in properties)
        {
            foreach (var segment in PropertySegments(property))
            {
                var equals = segment.IndexOf('=');
                if (equals > 0 && segment[..equals].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    var candidate = segment[(equals + 1)..].Trim();
                    if (candidate.Length > 0)
                    {
                        value = candidate;
                        found = true;
                    }
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Resolves the <em>explicit</em> effective target framework shared by the classification and build
    /// passes so they never evaluate a different TFM. Precedence: <c>--framework</c> wins; else a bare
    /// <c>-p:TargetFramework</c> is promoted (last-wins, empty ignored); else <see langword="null"/>
    /// (leaving the multi-target first-TFM auto-pin to build resolution). Pure function of the args, so it
    /// runs BEFORE input/classification and is threaded into both.
    /// </summary>
    internal static string? ResolveExplicitFramework(string? frameworkOption, IReadOnlyList<string> properties)
    {
        if (!string.IsNullOrWhiteSpace(frameworkOption))
        {
            return frameworkOption.Trim();
        }

        return TryGetUserProperty(properties, "TargetFramework", out var userFramework) ? userFramework : null;
    }

    /// <summary>
    /// User <c>Name=Value</c> properties with dedicated <c>-c</c>/<c>-r</c>/<c>-f</c> dupes removed (see
    /// <see cref="DedicatedFlagProperties"/>), so the dedicated switch is the single source of
    /// Configuration/RID/TFM in both the build and evaluate passes.
    /// </summary>
    private static IEnumerable<string> ForwardableProperties(IReadOnlyList<string> properties) =>
        properties.Where(property => !IsDedicatedFlagProperty(property));

    /// <summary>
    /// True when a <c>Name=Value</c> property names a dedicated-switch property (case-insensitive). Splits
    /// on both MSBuild property separators and matches ANY packed segment, so a smuggled
    /// <c>RuntimeIdentifier</c>/<c>Configuration</c>/<c>TargetFramework</c> in a packed <c>-p</c> can never
    /// override the switch winapp sets.
    /// </summary>
    private static bool IsDedicatedFlagProperty(string property) =>
        PropertySegments(property)
            .Select(PropertyName)
            .Any(name => DedicatedFlagProperties.Any(
                dedicated => name.Equals(dedicated, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Enumerates the properties packed into one <c>-p</c> value. MSBuild accepts both separators; literal
    /// separator characters in a value must be percent-escaped before they reach this boundary.
    /// </summary>
    private static IEnumerable<string> PropertySegments(string property) =>
        property.Split([';', ','], StringSplitOptions.RemoveEmptyEntries);

    private static string PropertyName(string segment)
    {
        var equals = segment.IndexOf('=');
        return (equals > 0 ? segment[..equals] : segment).Trim();
    }

    /// <summary>Extracts the property name from a <c>-p:Name=Value</c> token (e.g. <c>SolutionDir</c>).</summary>
    private static string SolutionPropertyName(string token)
    {
        var start = token.StartsWith("-p:", StringComparison.Ordinal) ? 3 : 0;
        var equals = token.IndexOf('=', start);
        return equals > start ? token[start..equals] : token[start..];
    }

    /// <summary>
    /// Builds the <c>-p:Solution*</c> tokens a solution build sets — most importantly <c>$(SolutionDir)</c>
    /// (trailing separator, per MSBuild convention). Shared by the build, evaluate, and classification
    /// passes so all three see the same solution-defined properties.
    /// </summary>
    private static IReadOnlyList<string> BuildSolutionPropertyTokens(FileInfo solution)
    {
        var solutionDir = solution.Directory?.FullName ?? Directory.GetCurrentDirectory();
        // $(SolutionDir) convention is a trailing separator; EscapeArgument round-trips it under quoting.
        if (!solutionDir.EndsWith(Path.DirectorySeparatorChar) && !solutionDir.EndsWith(Path.AltDirectorySeparatorChar))
        {
            solutionDir += Path.DirectorySeparatorChar;
        }

        var solutionName = Path.GetFileNameWithoutExtension(solution.Name);

        // MSBuild-escape each value: an unescaped ';' in a legal path reads as a property separator and a
        // literal '%' could be mis-decoded. This is a separate layer from command-line quoting.
        return
        [
            $"-p:SolutionDir={EscapeMsBuildPropertyValue(solutionDir)}",
            $"-p:SolutionPath={EscapeMsBuildPropertyValue(solution.FullName)}",
            $"-p:SolutionName={EscapeMsBuildPropertyValue(solutionName)}",
            $"-p:SolutionFileName={EscapeMsBuildPropertyValue(solution.Name)}",
            $"-p:SolutionExt={EscapeMsBuildPropertyValue(solution.Extension)}",
        ];
    }

    /// <summary>
    /// Adds an inferred profile and scopes its import to the selected app. The .NET SDK honors
    /// <c>ProjectToOverrideProjectExtensionsPath</c> by setting <c>PublishProfileImported=false</c> in every
    /// referenced project whose <c>MSBuildProjectFullPath</c> differs, so the global property cannot activate
    /// a same-named profile elsewhere in the project graph.
    /// </summary>
    private static void AppendInferredPublishProfile(
        List<string> tokens,
        FileInfo project,
        ProjectRunOptions options,
        bool include = true)
    {
        if (!include || string.IsNullOrWhiteSpace(options.PublishProfile))
        {
            return;
        }

        tokens.Add($"-p:PublishProfile={EscapeMsBuildPropertyValue(options.PublishProfile)}");
        tokens.Add(
            $"-p:ProjectToOverrideProjectExtensionsPath={EscapeMsBuildPropertyValue(project.FullName)}");
    }

    /// <summary>
    /// Percent-escapes the characters MSBuild treats specially in a <c>-p:Name=Value</c> property value —
    /// <c>;</c>/<c>,</c> (property separators) and <c>%</c> (escape lead-in, escaped first to stay
    /// idempotent-safe).
    /// Other special chars are inert here and left as-is so paths stay readable in logs.
    /// </summary>
    private static string EscapeMsBuildPropertyValue(string value) =>
        value.Replace("%", "%25", StringComparison.Ordinal)
             .Replace(";", "%3B", StringComparison.Ordinal)
             .Replace(",", "%2C", StringComparison.Ordinal);

    /// <summary>Name fragments that mark a <c>-p:Name=Value</c> property whose value must not be echoed.</summary>
    private static readonly string[] SecretPropertyNameFragments =
        ["password", "pwd", "secret", "token", "apikey", "accesskey", "credential", "connectionstring"];

    /// <summary>
    /// Masks secret-like <c>-p:Name=Value</c> properties and credentials embedded in URI user-info/query
    /// strings for DISPLAY only — the real command passed to dotnet is never altered. Property redaction
    /// runs at the token level so a quote inside a value can't leave part of the secret unmasked.
    /// </summary>
    internal static string RedactSecretsForDisplay(string commandLine)
    {
        if (string.IsNullOrEmpty(commandLine))
        {
            return commandLine;
        }

        var displayLine = commandLine;
        if (commandLine.Contains("-p:", StringComparison.Ordinal))
        {
            var tokens = WindowsCommandLine.SplitArguments(commandLine);
            var anyChanged = false;
            var redacted = new List<string>(tokens.Count);

            foreach (var token in tokens)
            {
                if (token.StartsWith("-p:", StringComparison.Ordinal))
                {
                    var body = RedactPropertySegments(token[3..], out var changed);
                    if (changed)
                    {
                        anyChanged = true;
                        redacted.Add("-p:" + body);
                        continue;
                    }
                }

                redacted.Add(token);
            }

            if (anyChanged)
            {
                displayLine = WindowsCommandLine.JoinArguments(redacted) ?? commandLine;
            }
        }

        return NugetErrorMessage.Redact(displayLine);
    }

    /// <summary>
    /// Masks a secret-looking value in a single <c>Name=Value</c> MSBuild property, using the same policy
    /// as <see cref="RedactSecretsForDisplay"/>.
    /// </summary>
    /// <remarks>
    /// For a property echoed on its own rather than inside a command line — a <c>-p</c> value repeated back
    /// in guidance, say — where the <c>-p:</c> token prefix that drives the command-line form is absent.
    /// </remarks>
    internal static string RedactSecretPropertyForDisplay(string property) =>
        RedactPropertySegments(property, out _);

    private static string RedactPropertySegments(string body, out bool changed)
    {
        changed = false;
        var result = new StringBuilder(body.Length);
        var segmentStart = 0;
        var secretContinuation = false;
        for (int i = 0; i <= body.Length; i++)
        {
            if (i < body.Length && body[i] is not (';' or ','))
            {
                continue;
            }

            var segment = body[segmentStart..i];
            var equals = segment.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0 && IsSecretPropertyName(segment[..equals]))
            {
                result.Append(segment[..equals]).Append("=***");
                changed = true;
                secretContinuation = true;
            }
            else if (equals <= 0 && secretContinuation)
            {
                result.Append("***");
                changed = true;
            }
            else
            {
                result.Append(segment);
                secretContinuation = false;
            }

            if (i < body.Length)
            {
                result.Append(body[i]);
            }

            segmentStart = i + 1;
        }

        return changed ? result.ToString() : body;
    }

    private static bool IsSecretPropertyName(string name) =>
        SecretPropertyNameFragments.Any(fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}
