// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using Microsoft.Extensions.Logging;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Resolves an architecture-specific publish profile when required by the effective build, without forcing a
/// global MSBuild Platform onto the project-reference graph.
/// </summary>
internal sealed partial class ProjectRunService
{
    /// <summary>
    /// Resolves a required architecture-specific profile while preserving the established RID-only behavior
    /// for projects that already build successfully. A profile is required when MSIX tooling makes a trimmed,
    /// framework-dependent build run the SDK's publish-time optimization validation. Plain SDK builds retain
    /// their configured deployment mode because they do not hit that validation during <c>dotnet build</c>.
    /// NativeAOT and already-self-contained projects also retain RID-only behavior. The profile sets Platform
    /// locally in the app, so AnyCPU references keep their own Platform.
    /// </summary>
    private async Task<ProjectRunOptions> ResolveRequiredPublishProfileAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        DirectoryInfo workingDirectory,
        string? csWinRTMetadataFolder,
        CancellationToken cancellationToken)
    {
        if (!CanInferPublishProfile(options))
        {
            return options;
        }

        var currentProperties = await TryEvaluatePublishProfilePropertiesAsync(
            csproj,
            options,
            workingDirectory,
            csWinRTMetadataFolder,
            cancellationToken);

        if (currentProperties is null
            || !RequiresSelfContainedProfile(currentProperties)
            || HasAuthoritativeProfileSelector(currentProperties))
        {
            return options;
        }

        var platform = FindArchPlatformToken(csproj, options.Architecture) ?? options.Architecture;
        var platformProperties = await TryEvaluatePublishProfilePropertiesAsync(
            csproj,
            options with { Platform = platform },
            workingDirectory,
            csWinRTMetadataFolder,
            cancellationToken);

        if (platformProperties is null
            || !TryGetImportedPublishProfileFile(csproj, platformProperties, out var profile))
        {
            return options;
        }

        var candidateOptions = options with { PublishProfile = Path.GetFileName(profile.FullName) };
        var candidateProperties = await TryEvaluatePublishProfilePropertiesAsync(
            csproj,
            candidateOptions,
            workingDirectory,
            csWinRTMetadataFolder,
            cancellationToken);

        return candidateProperties is null
            ? options
            : ResolvePublishProfileFallback(
                csproj,
                options,
                currentProperties,
                platformProperties,
                candidateProperties);
    }

    private async Task<IReadOnlyDictionary<string, string>?> TryEvaluatePublishProfilePropertiesAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        DirectoryInfo workingDirectory,
        string? csWinRTMetadataFolder,
        CancellationToken cancellationToken)
    {
        var arguments = BuildEvaluateArguments(csproj, options, csWinRTMetadataFolder);
        logger.LogDebug("{UISymbol} dotnet {Arguments}", UiSymbols.Note, RedactSecretsForDisplay(arguments));

        int exitCode;
        string output;
        try
        {
            (exitCode, output, _) = await dotNetService.RunDotnetCommandAsync(
                workingDirectory,
                arguments,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Win32Exception)
        {
            logger.LogDebug(
                "{UISymbol} Could not start publish-profile requirement evaluation; keeping RID-only inputs.",
                UiSymbols.Note);
            return null;
        }
        catch (InvalidOperationException)
        {
            logger.LogDebug(
                "{UISymbol} Could not evaluate whether an architecture-specific publish profile is required; keeping RID-only inputs.",
                UiSymbols.Note);
            return null;
        }

        if (exitCode != 0)
        {
            logger.LogDebug(
                "{UISymbol} Publish-profile requirement evaluation exited {ExitCode}; keeping RID-only inputs.",
                UiSymbols.Note,
                exitCode);
            return null;
        }

        return MsBuildPropertyReader.Parse(output, RequestedProperties);
    }

    internal static ProjectRunOptions ResolvePublishProfileFallback(
        FileInfo csproj,
        ProjectRunOptions options,
        IReadOnlyDictionary<string, string> currentProperties,
        IReadOnlyDictionary<string, string> platformProperties,
        IReadOnlyDictionary<string, string> candidateProperties)
    {
        if (!CanInferPublishProfile(options)
            || !RequiresSelfContainedProfile(currentProperties)
            || HasAuthoritativeProfileSelector(currentProperties)
            || HasAuthoritativeProfileSelector(platformProperties))
        {
            return options;
        }

        var currentProfile = GetProp(currentProperties, "PublishProfile");
        var candidateProfile = GetProp(platformProperties, "PublishProfile");
        var currentFramework = GetProp(currentProperties, "TargetFramework");
        var candidateFramework = GetProp(candidateProperties, "TargetFramework");
        var candidatePlatform = RunArchHelper.NormalizeArchitecture(GetProp(candidateProperties, "Platform"));
        var candidateRuntimeArchitecture = RunArchHelper.ArchitectureFromRid(
            GetProp(candidateProperties, "RuntimeIdentifier"));
        if (string.IsNullOrWhiteSpace(candidateProfile)
            || string.Equals(currentProfile, candidateProfile, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(currentFramework)
            || !string.Equals(currentFramework, candidateFramework, StringComparison.OrdinalIgnoreCase)
            || !IsTrue(GetProp(candidateProperties, "PublishProfileImported"))
            || !IsTrue(GetProp(candidateProperties, "SelfContained"))
            || !IsTrue(GetProp(candidateProperties, "PublishTrimmed"))
            || IsTrue(GetProp(candidateProperties, "PublishAot"))
            || !string.Equals(candidatePlatform, options.Architecture, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                candidateRuntimeArchitecture,
                options.Architecture,
                StringComparison.OrdinalIgnoreCase)
            || !TryGetImportedPublishProfileFile(csproj, candidateProperties, out var profile))
        {
            return options;
        }

        return options with { PublishProfile = Path.GetFileName(profile.FullName) };
    }

    private static bool CanInferPublishProfile(ProjectRunOptions options) =>
        string.IsNullOrWhiteSpace(options.PublishProfile)
        && string.IsNullOrWhiteSpace(options.Platform)
        && !UserSpecifiesProperty(options.Properties, "Platform")
        && !UserSpecifiesProperty(options.Properties, "SelfContained")
        && !UserSpecifiesProperty(options.Properties, "PublishProfile")
        && !UserSpecifiesProperty(options.Properties, "PublishProfileName")
        && !UserSpecifiesProperty(options.Properties, "PublishProfileFullPath")
        && !UserSpecifiesProperty(options.Properties, "WebPublishProfileFile");

    private static bool RequiresSelfContainedProfile(IReadOnlyDictionary<string, string> properties) =>
        IsTrue(GetProp(properties, "EnableMsixTooling"))
        && IsTrue(GetProp(properties, "PublishTrimmed"))
        && !IsTrue(GetProp(properties, "PublishAot"))
        && !IsTrue(GetProp(properties, "SelfContained"));

    private static bool HasAuthoritativeProfileSelector(IReadOnlyDictionary<string, string> properties)
    {
        var publishProfile = GetProp(properties, "PublishProfile");
        var publishProfileName = GetProp(properties, "PublishProfileName");
        var publishProfileFullPath = GetProp(properties, "PublishProfileFullPath");
        var webPublishProfileFile = GetProp(properties, "WebPublishProfileFile");

        if (string.IsNullOrWhiteSpace(publishProfile))
        {
            return !string.IsNullOrWhiteSpace(publishProfileName)
                || !string.IsNullOrWhiteSpace(publishProfileFullPath)
                || !string.IsNullOrWhiteSpace(webPublishProfileFile);
        }

        try
        {
            var expectedName = Path.GetFileNameWithoutExtension(publishProfile);
            if (!string.Equals(publishProfileName, expectedName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var profileRoot = GetProp(properties, "_PublishProfileRootFolder");
            if (string.IsNullOrWhiteSpace(profileRoot))
            {
                return !string.IsNullOrWhiteSpace(publishProfileFullPath)
                    || !string.IsNullOrWhiteSpace(webPublishProfileFile);
            }

            var expectedFullPath = Path.GetFullPath(Path.Join(profileRoot, expectedName + ".pubxml"));
            if (!PathsEqual(publishProfileFullPath, expectedFullPath))
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(webPublishProfileFile)
                && !PathsEqual(webPublishProfileFile, publishProfileFullPath);
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (NotSupportedException)
        {
            return true;
        }
    }

    private static bool TryGetImportedPublishProfileFile(
        FileInfo project,
        IReadOnlyDictionary<string, string> properties,
        out FileInfo profile)
    {
        profile = null!;
        var projectDirectory = project.Directory;
        var publishProfileFullPath = GetProp(properties, "PublishProfileFullPath");
        var webPublishProfileFile = GetProp(properties, "WebPublishProfileFile");
        if (projectDirectory is null
            || string.IsNullOrWhiteSpace(publishProfileFullPath)
            || !PathsEqual(publishProfileFullPath, webPublishProfileFile))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(publishProfileFullPath);
            var projectRoot = Path.GetFullPath(projectDirectory.FullName)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            profile = new FileInfo(fullPath);
            return profile.Exists;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsTrue(string value) =>
        value.Equals("true", StringComparison.OrdinalIgnoreCase);

    private static bool PathsEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }
}
