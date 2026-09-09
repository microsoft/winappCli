// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// Restores a .NET project that <c>winapp init</c> configured, whose SDK package versions live as
/// <c>PackageReference</c> entries in the <c>.csproj</c> rather than in a <c>winapp.yaml</c>.
/// </summary>
internal interface IDotNetProjectRestoreService
{
    /// <summary>
    /// Restores the .NET project found under <paramref name="baseDirectory"/> by running
    /// <c>dotnet restore</c> for it. Returns 0 on success, or 1 when the restore fails or when the
    /// directory contains several projects — that is ambiguous and reported rather than guessed at, since
    /// restore runs non-interactively and has no project-selection option.
    /// </summary>
    /// <param name="baseDirectory">Directory to search for .csproj files.</param>
    /// <param name="configDir">
    /// The configuration directory selected for this run. It cannot be forwarded to <c>dotnet restore</c>
    /// without discarding the user/machine nuget.config levels, so it is only used to warn when it would not
    /// be part of the hierarchy dotnet discovers for the project.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<int> RestoreAsync(DirectoryInfo baseDirectory, DirectoryInfo configDir, CancellationToken cancellationToken = default);
}
