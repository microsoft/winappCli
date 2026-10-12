// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Text.Json;

namespace WinApp.Cli.Services;

/// <inheritdoc cref="IMSBuildService" />
internal sealed class MSBuildService(IProcessRunner processRunner) : IMSBuildService
{
    /// <summary>MSBuild 17.8 (Visual Studio 2022 17.8) is the first with <c>-getProperty</c>, which the evaluate pass needs.</summary>
    private const string MinimumVersion = "[17.8,";

    /// <summary>
    /// The documented vswhere location. Read through the environment so it can be redirected; the
    /// Visual Studio Installer always installs it here, even for Build Tools-only machines.
    /// </summary>
    internal string VsWherePath { get; init; } = Path.Join(
        Environment.GetEnvironmentVariable("ProgramFiles(x86)") is { Length: > 0 } programFilesX86
            ? programFilesX86
            : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        "Microsoft Visual Studio",
        "Installer",
        "vswhere.exe");

    /// <inheritdoc />
    public async Task<string> LocateCppMSBuildAsync(string architecture, bool requiresWindowsStoreAppType, CancellationToken cancellationToken)
    {
        if (!File.Exists(VsWherePath))
        {
            throw new ProjectRunException(BuildMissingToolchainMessage(architecture, installedProduct: null));
        }

        // Every install with the C++ tools, newest first. The newest isn't always usable: a C++-only Build Tools
        // installed next to Visual Studio can't build a WinUI ("Windows Store" application type) project.
        var installs = ParseInstances(await RunVsWhereAsync(
            ["-prerelease", "-products", "*", "-version", MinimumVersion, "-requires", VcToolsComponent(architecture), "-sort", "-format", "json", "-utf8"],
            cancellationToken));
        foreach (var (path, _) in installs)
        {
            var msbuild = Path.Join(path, "MSBuild", "Current", "Bin", "MSBuild.exe");
            if (File.Exists(msbuild) && (!requiresWindowsStoreAppType || SupportsWindowsStoreAppType(path)))
            {
                return msbuild;
            }
        }

        if (installs.Count > 0)
        {
            throw new ProjectRunException(BuildMissingWinUiToolsMessage(installs[0].DisplayName));
        }

        // Name the installed product when there is one, so the user fixes that install instead of adding another.
        var installed = ParseInstances(await RunVsWhereAsync(
                ["-latest", "-prerelease", "-products", "*", "-format", "json", "-utf8"],
                cancellationToken))
            .Select(i => i.DisplayName)
            .FirstOrDefault();
        throw new ProjectRunException(BuildMissingToolchainMessage(architecture, installed));
    }

    /// <summary>
    /// True when the install can build a "Windows Store" application type project (WinUI 3 / UWP C++): the
    /// C++ MSBuild targets include that application type. Without it MSBuild fails with "The BaseOutputPath/
    /// OutputPath property is not set".
    /// </summary>
    internal static bool SupportsWindowsStoreAppType(string installationPath)
    {
        var vcTargets = Path.Join(installationPath, "MSBuild", "Microsoft", "VC");
        try
        {
            return Directory.Exists(vcTargets)
                && Directory.EnumerateDirectories(vcTargets, "v*")
                    .Any(toolset => Directory.Exists(Path.Join(toolset, "Application Type", "Windows Store")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static List<(string Path, string DisplayName)> ParseInstances(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind != JsonValueKind.Array
                ? []
                : document.RootElement.EnumerateArray()
                    .Select(i => (
                        Path: i.TryGetProperty("installationPath", out var p) ? p.GetString() ?? string.Empty : string.Empty,
                        DisplayName: i.TryGetProperty("displayName", out var d) ? d.GetString() ?? string.Empty : string.Empty))
                    .Where(i => i.Path.Length > 0)
                    .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <inheritdoc />
    public Task<ProcessRunResult> RunAsync(
        string msbuildPath,
        IReadOnlyList<string> arguments,
        Action<string>? onLine,
        CancellationToken cancellationToken)
        => processRunner.RunAsync(new ProcessRunRequest(msbuildPath, arguments), onLine, onLine, cancellationToken);

    private async Task<string> RunVsWhereAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ProcessRunResult result;
        try
        {
            result = await processRunner.RunAsync(new ProcessRunRequest(VsWherePath, arguments), cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return string.Empty;
        }

        return result.ExitCode != 0 ? string.Empty : result.StandardOutput;
    }

    /// <summary>The Visual Studio component that carries the MSVC compiler for the target architecture.</summary>
    internal static string VcToolsComponent(string architecture) =>
        string.Equals(architecture, "arm64", StringComparison.OrdinalIgnoreCase)
            ? "Microsoft.VisualStudio.Component.VC.Tools.ARM64"
            : "Microsoft.VisualStudio.Component.VC.Tools.x86.x64";

    internal static string BuildMissingToolchainMessage(string architecture, string? installedProduct)
    {
        var arm64 = string.Equals(architecture, "arm64", StringComparison.OrdinalIgnoreCase);
        var winget = "winget install Microsoft.VisualStudio.BuildTools --override \"--wait --passive " +
            "--add Microsoft.VisualStudio.Workload.VCTools --add Microsoft.VisualStudio.ComponentGroup.UWP.VC.BuildTools " +
            (arm64 ? "--add Microsoft.VisualStudio.Component.VC.Tools.ARM64 " : string.Empty) +
            "--includeRecommended\"";
        var problem = installedProduct is null
            ? "no Visual Studio or Build Tools for Visual Studio installation was found."
            : $"{installedProduct} does not have them installed or is older than version 17.8.";
        var addTools = installedProduct is null
            ? "Install Visual Studio with the \"Desktop development with C++\" workload"
            : $"In the Visual Studio Installer, modify {installedProduct} to add the \"Desktop development with C++\" workload";

        return $"Building a C++ project (.vcxproj) needs Visual Studio or Build Tools for Visual Studio 2022 version 17.8 or later with the MSVC C++ build tools for {architecture}, but {problem}" + Environment.NewLine +
            $"  - {addTools}" +
            (arm64 ? " and the \"MSVC ARM64/ARM64EC build tools\" component" : string.Empty) +
            ". WinUI 3 apps also need the \"WinUI application development\" workload with \"C++ WinUI app development tools\"." + Environment.NewLine +
            $"  - Or install Build Tools for Visual Studio: {winget}";
    }

    internal static string BuildMissingWinUiToolsMessage(string installedProduct) =>
        $"This is a WinUI 3 C++ project (ApplicationType 'Windows Store'), which needs the C++ WinUI app development tools, but {installedProduct} does not have them." + Environment.NewLine +
        $"  - In the Visual Studio Installer, modify {installedProduct} to add \"C++ WinUI app development tools\" (in Build Tools: \"C++ Universal Windows Platform build tools\")." + Environment.NewLine +
        "  - Or install Build Tools for Visual Studio: winget install Microsoft.VisualStudio.BuildTools --override \"--wait --passive " +
        "--add Microsoft.VisualStudio.Workload.VCTools --add Microsoft.VisualStudio.ComponentGroup.UWP.VC.BuildTools --includeRecommended\"";
}
