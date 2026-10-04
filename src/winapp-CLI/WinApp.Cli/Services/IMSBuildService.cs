// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// Locates and runs the Visual Studio (full framework) <c>MSBuild.exe</c>, which C++ projects
/// (<c>.vcxproj</c>) need because the .NET SDK has no C++ project system.
/// </summary>
internal interface IMSBuildService
{
    /// <summary>
    /// Finds <c>MSBuild.exe</c> in the newest Visual Studio or Build Tools instance that has the MSVC
    /// build tools for <paramref name="architecture"/>.
    /// </summary>
    /// <exception cref="ProjectRunException">No such instance exists; the message says what to install.</exception>
    Task<string> LocateCppMSBuildAsync(string architecture, CancellationToken cancellationToken);

    /// <summary>Runs <c>MSBuild.exe</c>, forwarding each stdout/stderr line to <paramref name="onLine"/> as it arrives.</summary>
    Task<ProcessRunResult> RunAsync(
        string msbuildPath,
        IReadOnlyList<string> arguments,
        Action<string>? onLine,
        CancellationToken cancellationToken);
}
