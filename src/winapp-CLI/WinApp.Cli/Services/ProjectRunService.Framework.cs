// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.Xml.Linq;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Effective-target-framework resolution for a multi-targeted project: pins a single TFM so every
/// downstream pass (build, evaluate, packaging, runtime provisioning) agrees, preferring a cheap static
/// read and falling back to an authoritative MSBuild evaluate only when the TFM list can't be resolved
/// statically.
/// </summary>
internal sealed partial class ProjectRunService
{
    /// <summary>
    /// Resolves an <em>effective</em> single TFM for a multi-targeted project so every downstream pass
    /// pins the SAME one; with no <c>--framework</c> it pins the FIRST declared TFM. Tries a cheap static
    /// read first, falling back to an authoritative <c>dotnet msbuild --getProperty:TargetFrameworks</c>
    /// evaluate when the list is imported/conditional/expression-based (without which a plural-TFMs
    /// project builds but its evaluate hits the empty cross-targeting outer node → throw after a
    /// successful build). No-op when single-targeted, <c>--framework</c> is set, or no TFM is determinable.
    /// </summary>
    private async Task<ProjectRunOptions> ResolveEffectiveFrameworkAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        DirectoryInfo workingDir,
        CancellationToken cancellationToken)
        => (await ResolveFrameworksAsync(csproj, options, workingDir, cancellationToken)).Options;

    /// <summary>
    /// <see cref="ResolveEffectiveFrameworkAsync"/> plus the single TFM the CsWinRT metadata shim uses to
    /// check the targeted Windows SDK and steer ref-pack selection. That TFM also covers a single-targeted
    /// project, whose <c>--framework</c> stays null, and is NEVER used as a build <c>-p:TargetFramework</c>.
    /// It reuses the effective-framework evaluate, so it never costs an extra MSBuild call.
    /// </summary>
    private async Task<(ProjectRunOptions Options, string? ShimFramework)> ResolveFrameworksAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        DirectoryInfo workingDir,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(options.Framework))
        {
            return (options, options.Framework);
        }

        // No --framework, but the user forwarded an explicit -p:TargetFramework=X. TFM is a
        // DedicatedFlagProperty, so ForwardableProperties would strip it from every pass and silently build
        // the default; promote it so it flows through the -f path and beats the multi-target auto-pin. The
        // command layer normally resolves this up front (ResolveExplicitFramework) so the early return
        // above already fired; this is a service-level fallback for direct callers/tests.
        if (TryGetUserProperty(options.Properties, "TargetFramework", out var userFramework))
        {
            return (options with { Framework = userFramework }, userFramework);
        }

        var staticFirst = dotNetService.GetTargetFramework(csproj);
        var staticFirstIsConcrete = !string.IsNullOrWhiteSpace(staticFirst)
            && !staticFirst.Contains("$(", StringComparison.Ordinal);

        if (dotNetService.IsMultiTargeted(csproj))
        {
            // Inline <TargetFrameworks>: pin the first statically ONLY when concrete AND unconditional. The
            // static read is first-textual-match, so a conditional/per-Configuration declaration could pin
            // the wrong group's TFM; fall through to the authoritative MSBuild evaluate in that case.
            if (staticFirstIsConcrete && HasUnconditionalInlineDeclaration(csproj, "TargetFrameworks"))
            {
                LogEffectiveFrameworkPinned(csproj, staticFirst!);
                return (options with { Framework = staticFirst }, staticFirst);
            }
        }
        else if (staticFirstIsConcrete && HasUnconditionalInlineDeclaration(csproj, "TargetFramework"))
        {
            // Inline single concrete unconditional <TargetFramework>: genuinely single-targeted — no pinning
            // needed. A per-Configuration declaration falls through so the evaluate reports the effective one.
            return (options, staticFirst);
        }

        // The TFM isn't statically resolvable (imported / conditional / expression). Authoritatively
        // evaluate it with MSBuild (same globals) and pin the FIRST of a multi-target list. A non-empty
        // TargetFramework wins over TargetFrameworks (the SDK builds it, not the list), so never pin then.
        var (evaluatedSingle, evaluatedFirst) = await ResolveEvaluatedFrameworksAsync(csproj, options, workingDir, cancellationToken);
        if (!string.IsNullOrWhiteSpace(evaluatedSingle) || string.IsNullOrWhiteSpace(evaluatedFirst))
        {
            return (options, evaluatedSingle);
        }

        LogEffectiveFrameworkPinned(csproj, evaluatedFirst);
        return (options with { Framework = evaluatedFirst }, evaluatedFirst);
    }

    private void LogEffectiveFrameworkPinned(FileInfo csproj, string tfm) =>
        logger.LogDebug(
            "{UISymbol} '{Project}' is multi-targeted; defaulting to first target framework '{Tfm}'. Pass --framework to choose another.",
            UiSymbols.Note, csproj.Name, tfm);

    /// <summary>
    /// True when <paramref name="elementName"/> (<c>TargetFramework</c> or <c>TargetFrameworks</c>) is declared
    /// inline in a form the cheap static read can trust: exactly ONE such element with no <c>Condition</c> on it
    /// or any ancestor. A conditional or duplicated declaration makes the first-textual-match read unreliable,
    /// forcing the caller to the authoritative evaluate. Returns <see langword="false"/> on unreadable/invalid XML.
    /// </summary>
    private static bool HasUnconditionalInlineDeclaration(FileInfo csproj, string elementName)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(csproj.FullName);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            return false;
        }

        // SDK-style project files are namespace-less; match on local name to stay namespace-agnostic.
        var declarations = doc.Descendants()
            .Where(e => e.Name.LocalName == elementName)
            .ToList();

        if (declarations.Count != 1)
        {
            return false;
        }

        return !declarations[0].AncestorsAndSelf()
            .Any(a => !string.IsNullOrWhiteSpace(a.Attribute("Condition")?.Value));
    }

    /// <summary>
    /// MSBuild properties queried to discover a project's effective TFM list. Requests TWO properties (not
    /// just <c>TargetFrameworks</c>) so the SDK emits the JSON envelope rather than a raw scalar — a single
    /// <c>--getProperty</c> would capture any warning/diagnostic preamble as the value. See
    /// <see cref="MsBuildPropertyReader"/>.
    /// </summary>
    private static readonly string[] FrameworkDiscoveryProperties = ["TargetFramework", "TargetFrameworks"];

    /// <summary>
    /// Authoritatively resolves a project's evaluated <c>TargetFramework</c> and the FIRST entry of its
    /// <c>TargetFrameworks</c> via an evaluate-only MSBuild pass using the same globals as the real passes.
    /// Either is <c>null</c> when empty or when the evaluate can't run (SDK-less / pre-restore /
    /// cancelled-start).
    /// </summary>
    private async Task<(string? TargetFramework, string? FirstOfTargetFrameworks)> ResolveEvaluatedFrameworksAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        DirectoryInfo workingDir,
        CancellationToken cancellationToken)
    {
        var args = BuildFrameworkDiscoveryArguments(csproj, options);
        logger.LogDebug("{UISymbol} dotnet {Arguments}", UiSymbols.Note, RedactSecretsForDisplay(args));

        int exitCode;
        string stdout;
        try
        {
            (exitCode, stdout, _) = await dotNetService.RunDotnetCommandAsync(workingDir, args, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Starting/communicating with dotnet failed → indeterminate; let the normal passes surface it.
            return (null, null);
        }

        if (exitCode != 0)
        {
            return (null, null);
        }

        var props = MsBuildPropertyReader.Parse(stdout, FrameworkDiscoveryProperties);
        var single = GetProp(props, "TargetFramework");
        var first = GetProp(props, "TargetFrameworks")?
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return (string.IsNullOrWhiteSpace(single) ? null : single, string.IsNullOrWhiteSpace(first) ? null : first);
    }

    /// <summary>
    /// Builds the evaluate-only <c>dotnet msbuild --getProperty:...</c> arguments for discovering a
    /// project's framework properties. Mirrors the property section of <see cref="BuildEvaluateArguments"/>
    /// but omits <c>-p:TargetFramework</c> (that's what we're discovering) and the RID: this reads the OUTER
    /// cross-targeting <c>&lt;TargetFrameworks&gt;</c> node, which the RID does not select, and pinning one on
    /// a bare cross-targeting evaluation can trip RID-graph resolution. Defaults to both <c>TargetFramework</c>
    /// and <c>TargetFrameworks</c> (two properties force the JSON envelope — see <see cref="FrameworkDiscoveryProperties"/>).
    /// </summary>
    internal static string BuildFrameworkDiscoveryArguments(FileInfo csproj, ProjectRunOptions options, params string[] getProperties)
    {
        var properties = getProperties.Length > 0 ? getProperties : FrameworkDiscoveryProperties;

        var tokens = new List<string>
        {
            "msbuild",
            csproj.FullName,
        };

        foreach (var property in ForwardableProperties(options.Properties))
        {
            tokens.Add($"-p:{property}");
        }

        AppendSolutionProperties(tokens, options);

        tokens.Add($"-p:Configuration={options.Configuration}");
        foreach (var property in properties)
        {
            tokens.Add($"--getProperty:{property}");
        }

        return WindowsCommandLine.JoinArguments(tokens) ?? string.Empty;
    }

    /// <summary>
    /// Properties read by <see cref="BuildCsWinRTMetadataProbeArguments"/>. Two properties force the JSON
    /// envelope (see <see cref="FrameworkDiscoveryProperties"/>).
    /// </summary>
    private static readonly string[] CsWinRTMetadataProbeProperties = ["CsWinRTWindowsMetadata", "TargetFramework"];

    /// <summary>
    /// SHIM (temporary): evaluate-only arguments that read the project's own <c>CsWinRTWindowsMetadata</c>
    /// under the run's Configuration and effective framework (the CsWinRT targets that default it are
    /// imported per framework).
    /// </summary>
    internal static string BuildCsWinRTMetadataProbeArguments(FileInfo csproj, ProjectRunOptions options)
    {
        var tokens = new List<string>
        {
            "msbuild",
            csproj.FullName,
        };

        foreach (var property in ForwardableProperties(options.Properties))
        {
            tokens.Add($"-p:{property}");
        }

        AppendSolutionProperties(tokens, options);

        tokens.Add($"-p:Configuration={options.Configuration}");
        if (!string.IsNullOrWhiteSpace(options.Framework))
        {
            tokens.Add($"-p:TargetFramework={options.Framework}");
        }

        foreach (var property in CsWinRTMetadataProbeProperties)
        {
            tokens.Add($"--getProperty:{property}");
        }

        return WindowsCommandLine.JoinArguments(tokens) ?? string.Empty;
    }

    /// <summary>
    /// SHIM (temporary): a cheap text check for whether the project file, or a <c>Directory.Build.props</c> /
    /// <c>Directory.Build.targets</c> in its directory or any parent, mentions <c>CsWinRTWindowsMetadata</c>.
    /// That is where a project pins its own value; a pin in some other imported file goes unnoticed.
    /// </summary>
    internal static bool ProjectMentionsCsWinRTMetadata(FileInfo csproj)
    {
        if (FileMentionsCsWinRTMetadata(csproj.FullName))
        {
            return true;
        }

        for (var directory = csproj.Directory; directory is not null; directory = directory.Parent)
        {
            if (FileMentionsCsWinRTMetadata(Path.Join(directory.FullName, "Directory.Build.props"))
                || FileMentionsCsWinRTMetadata(Path.Join(directory.FullName, "Directory.Build.targets")))
            {
                return true;
            }
        }

        return false;
    }

    private static bool FileMentionsCsWinRTMetadata(string path)
    {
        try
        {
            return File.Exists(path)
                && File.ReadAllText(path).Contains("CsWinRTWindowsMetadata", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
