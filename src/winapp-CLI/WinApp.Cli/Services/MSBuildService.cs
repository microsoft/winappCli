// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;

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
    public async Task<string> LocateCppMSBuildAsync(string architecture, CancellationToken cancellationToken)
    {
        if (!File.Exists(VsWherePath))
        {
            throw new ProjectRunException(BuildMissingToolchainMessage(architecture, installedProduct: null));
        }

        var msbuild = (await RunVsWhereAsync(
                ["-latest", "-prerelease", "-products", "*", "-version", MinimumVersion, "-requires", VcToolsComponent(architecture), "-find", @"MSBuild\**\Bin\MSBuild.exe"],
                cancellationToken))
            .FirstOrDefault(File.Exists);
        if (msbuild is not null)
        {
            return msbuild;
        }

        // Name the installed product when there is one, so the user fixes that install instead of adding another.
        var installed = (await RunVsWhereAsync(
                ["-latest", "-prerelease", "-products", "*", "-property", "displayName"],
                cancellationToken))
            .FirstOrDefault();
        throw new ProjectRunException(BuildMissingToolchainMessage(architecture, installed));
    }

    /// <inheritdoc />
    public Task<ProcessRunResult> RunAsync(
        string msbuildPath,
        IReadOnlyList<string> arguments,
        Action<string>? onLine,
        CancellationToken cancellationToken)
        => processRunner.RunAsync(new ProcessRunRequest(msbuildPath, arguments), onLine, onLine, cancellationToken);

    private async Task<List<string>> RunVsWhereAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ProcessRunResult result;
        try
        {
            result = await processRunner.RunAsync(new ProcessRunRequest(VsWherePath, arguments), cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return [];
        }

        return result.ExitCode != 0
            ? []
            : result.StandardOutput
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
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
}
