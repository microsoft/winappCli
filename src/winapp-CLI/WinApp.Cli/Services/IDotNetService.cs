// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Service for detecting and working with .NET projects
/// </summary>
internal interface IDotNetService
{
    /// <summary>
    /// Finds all .csproj files in the specified directory (non-recursive)
    /// </summary>
    /// <returns>A list of .csproj files found, empty if none</returns>
    IReadOnlyList<FileInfo> FindCsproj(DirectoryInfo directory);

    /// <summary>
    /// Gets the TargetFramework value from a .csproj file.
    /// If the project uses <TargetFrameworks> (plural/multi-targeting), returns the first TFM.
    /// </summary>
    string? GetTargetFramework(FileInfo csprojPath);

    /// <summary>
    /// Checks whether a .csproj file uses <TargetFrameworks> (plural) for multi-targeting.
    /// </summary>
    bool IsMultiTargeted(FileInfo csprojPath);

    /// <summary>
    /// Checks whether the TargetFramework includes a Windows TFM that supports WinAppSDK
    /// (e.g. net8.0-windows10.0.19041.0 or later)
    /// </summary>
    bool IsTargetFrameworkSupported(string targetFramework);

    /// <summary>
    /// Returns the recommended TargetFramework for WinAppSDK projects.
    /// If <paramref name="currentTargetFramework"/> is provided and has a supported .NET version,
    /// it preserves that version and only adds/updates the Windows SDK version.
    /// </summary>
    /// <param name="currentTargetFramework">The current TargetFramework from the project, or null if not set.</param>
    string GetRecommendedTargetFramework(string? currentTargetFramework = null);

    /// <summary>
    /// Updates the TargetFramework in a .csproj file
    /// </summary>
    void SetTargetFramework(FileInfo csprojPath, string newTargetFramework);

    /// <summary>
    /// Adds or updates a NuGet PackageReference using the dotnet CLI.
    /// </summary>
    /// <param name="csprojPath">The project file in which to add or update the package reference.</param>
    /// <param name="packageName">The name of the NuGet package to add or update.</param>
    /// <param name="version">
    /// The specific package version to install. When <see langword="null"/>, the dotnet CLI is invoked
    /// with the <c>--prerelease</c> flag, allowing the latest prerelease version to be selected.
    /// </param>
    /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
    /// <returns>The version that was added or updated.</returns>
    Task<string> AddOrUpdatePackageReferenceAsync(FileInfo csprojPath, string packageName, string? version, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs an arbitrary dotnet CLI command in the given working directory.
    /// </summary>
    Task<(int ExitCode, string Output, string Error)> RunDotnetCommandAsync(
        DirectoryInfo workingDirectory,
        string arguments,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs a dotnet CLI command passing each argument as a discrete token via
    /// <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>. Prefer this overload when any
    /// argument is derived from user input: passing tokens discretely prevents shell/string
    /// splitting, so a value cannot be broken into multiple arguments by embedded whitespace or
    /// quotes.
    /// <para>
    /// This is <b>not</b> a substitute for validating values against the invoked command's option
    /// grammar. A single option-shaped token (e.g. a project name of <c>--force</c> or a version of
    /// <c>-p</c>) is still passed intact to the child process, where dotnet's own parser may
    /// interpret it as a switch rather than a value. Callers must therefore still validate
    /// user-supplied values (as the <c>new</c> command does for project names and template
    /// versions), or place them after a <c>--</c> end-of-options separator where the child command
    /// supports one.
    /// </para>
    /// </summary>
    /// <param name="environmentOverrides">
    /// Optional environment variables to set on the child process (merged over the inherited
    /// environment). Use this to force locale-independent output, e.g. <c>DOTNET_CLI_UI_LANGUAGE=en</c>,
    /// when the caller parses labels that dotnet would otherwise localize.
    /// </param>
    /// <param name="onOutputLine">
    /// Optional callback invoked with each stdout line as it is produced (in addition to the buffered
    /// <c>Output</c> returned on completion). Use this to surface a long-running command's output live —
    /// e.g. echoing <c>dotnet new</c>'s post-creation actions under <c>--verbose</c> — while still
    /// receiving the full captured text. Invoked on a background thread; callers that touch shared state
    /// must synchronize.
    /// </param>
    /// <param name="onErrorLine">Optional callback invoked with each stderr line, mirroring <paramref name="onOutputLine"/>.</param>
    Task<(int ExitCode, string Output, string Error)> RunDotnetCommandAsync(
        DirectoryInfo workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environmentOverrides = null,
        Action<string>? onOutputLine = null,
        Action<string>? onErrorLine = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs a dotnet CLI command in the given working directory, delivering each stdout/stderr line
    /// to the supplied callbacks as it is produced (rather than capturing it all up front). Used for
    /// the project-mode build pass so build progress is visible live. Returns the process exit code.
    /// The callbacks are invoked on background threads; callers that touch shared state must
    /// synchronize. The command's own output is NOT written anywhere unless a callback does so.
    /// </summary>
    Task<int> RunDotnetStreamingAsync(
        DirectoryInfo workingDirectory,
        string arguments,
        Action<string>? onOutputLine,
        Action<string>? onErrorLine,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs a dotnet CLI command with <b>inherited</b> stdio: the child inherits winapp's own console
    /// handles (no redirection, no read pumps), so dotnet sees a real terminal and its native terminal
    /// logger renders the live build (single warnings, live progress) directly. Used for the project-mode
    /// build pass when winapp is attached to a real interactive terminal. Returns the process exit code.
    /// The command's output goes straight to the inherited console — winapp never sees the lines, so this
    /// must not be used for any pass whose output winapp needs to parse. Shares the same kill-on-cancel
    /// tree-kill policy as the streaming/buffered launchers.
    /// </summary>
    Task<int> RunDotnetInheritedAsync(
        DirectoryInfo workingDirectory,
        string arguments,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures the .csproj has a RuntimeIdentifier element with a default that auto-detects
    /// the current platform architecture. Only adds the element if no RuntimeIdentifier or
    /// RuntimeIdentifiers element already exists in the project.
    /// </summary>
    /// <returns>True if the .csproj was modified, false if it already had a RuntimeIdentifier.</returns>
    Task<bool> EnsureRuntimeIdentifierAsync(FileInfo csprojPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the PublishProfile element in a .csproj to include a Condition that checks
    /// whether the publish profile file actually exists, preventing build errors when it doesn't.
    /// Transforms: &ltPublishProfile&gt;win-$(Platform).pubxml&lt;/PublishProfile&gt;
    /// To: &lt;PublishProfile Condition="Exists('Properties\PublishProfiles\win-$(Platform).pubxml')"&gt;win-$(Platform).pubxml&lt;/PublishProfile&gt;
    /// </summary>
    /// <returns>True if the .csproj was modified, false if no matching PublishProfile element was found.</returns>
    Task<bool> UpdatePublishProfileAsync(FileInfo csprojPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether a .csproj file contains a PackageReference for the specified package
    /// by querying the dotnet CLI package list.
    /// </summary>
    Task<bool> HasPackageReferenceAsync(FileInfo csprojPath, string packageName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs `dotnet list package --format json` and returns the parsed result.
    /// </summary>
    /// <param name="projectOrFile">
    /// The project to query. A <c>.cs</c> .NET file-based app is also accepted and is queried through
    /// the SDK 10 <c>dotnet package list --file</c> form.
    /// </param>
    /// <param name="includeTransitive">When true, includes transitive package references in the output.</param>
    /// <param name="noRestore">When true, pass <c>--no-restore</c> so the query doesn't trigger an implicit restore.</param>
    /// <param name="projectAssetsFile">The <c>project.assets.json</c> the build consumed. When it exists,
    /// the graph is read from it, because that is restore's output for the build's actual inputs; the
    /// command itself accepts no <c>-c</c>/<c>-r</c>/<c>-p</c> and so cannot reproduce them.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<DotNetPackageListJson?> GetPackageListAsync(FileInfo projectOrFile, bool includeTransitive = true, bool noRestore = false, FileInfo? projectAssetsFile = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures the .csproj has <c>&lt;EnableMsixTooling&gt;true&lt;/EnableMsixTooling&gt;</c>.
    /// Adds the element with an explanatory XML comment if missing, or updates it to <c>true</c> if set to <c>false</c>.
    /// </summary>
    /// <returns>True if the .csproj was modified, false if it already had the correct setting.</returns>
    Task<bool> EnsureEnableMsixToolingAsync(FileInfo csprojPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes <c>&lt;WindowsPackageType&gt;None&lt;/WindowsPackageType&gt;</c> if found in the .csproj,
    /// since this property prevents the app from running as a packaged application.
    /// </summary>
    /// <returns>True if the .csproj was modified, false if the element was not found.</returns>
    Task<bool> RemoveWindowsPackageTypeNoneAsync(FileInfo csprojPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds XML comments above <c>&lt;PackageReference&gt;</c> elements in the .csproj to describe
    /// what each package provides. Skips packages that already have a comment above them.
    /// </summary>
    /// <param name="csprojPath">The project file to annotate.</param>
    /// <param name="packageComments">A dictionary mapping package names to their descriptive comments.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if any comments were added, false if all packages already had comments or were not found.</returns>
    Task<bool> AnnotatePackageReferencesAsync(FileInfo csprojPath, IReadOnlyDictionary<string, string> packageComments, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures the .csproj contains a <c>&lt;Content Include="Assets\**\*" /&gt;</c> item so that
    /// generated visual assets (StoreLogo, AppList, etc.) are included in the MSIX package layout.
    /// Without this, non-WinUI projects exclude the assets from the .build.appxrecipe.
    /// </summary>
    /// <param name="csprojPath">The project file to update.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the .csproj was modified, false if it already had asset content items.</returns>
    Task<bool> EnsureAssetContentItemsAsync(FileInfo csprojPath, CancellationToken cancellationToken = default);
}
