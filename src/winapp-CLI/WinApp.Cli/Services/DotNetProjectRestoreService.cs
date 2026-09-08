// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services;

/// <summary>
/// Restores a .NET project that <c>winapp init</c> configured. For .NET projects init records the SDK package
/// versions as <c>PackageReference</c> entries in the <c>.csproj</c> rather than in a <c>winapp.yaml</c>, so
/// there is nothing for the winapp.yaml-based restore to read and <c>dotnet restore</c> is what actually
/// restores them.
///
/// Split out of <see cref="WorkspaceSetupService"/> so that service is not extended with another distinct
/// responsibility (see the file-size guidance in AGENTS.md), and so this workflow can be tested on its own.
/// </summary>
internal sealed class DotNetProjectRestoreService(
    IDotNetService dotNetService,
    ILogger<DotNetProjectRestoreService> logger) : IDotNetProjectRestoreService
{
    /// <inheritdoc />
    public async Task<int> RestoreAsync(DirectoryInfo baseDirectory, DirectoryInfo configDir, CancellationToken cancellationToken = default)
    {
        var csprojFiles = dotNetService.FindCsproj(baseDirectory);

        // One project restores automatically. Several in the same directory is ambiguous — restoring all of
        // them would let an unrelated project fail `winapp restore` — so list them and hand off rather than
        // guessing or adding a project-selection option. Discovery is top-directory-only, so the common
        // layout (one project per directory) is unaffected, and `winapp restore .\src\App` still works.
        if (csprojFiles.Count > 1)
        {
            logger.LogError(
                "{UISymbol} Found {Count} projects in {Directory}: {Projects}. Restore one of them directly, for example 'dotnet restore {Example}'.",
                UiSymbols.Error,
                csprojFiles.Count,
                baseDirectory.FullName,
                string.Join(", ", csprojFiles.Select(f => f.Name)),
                csprojFiles[0].Name);
            return 1;
        }

        // Nothing to restore. Unreachable through the current caller, which only delegates here after
        // detecting a .csproj, but RestoreAsync is a public contract — report it rather than letting a
        // future caller hit an index-out-of-range on csprojFiles[0] below.
        if (csprojFiles.Count == 0)
        {
            logger.LogError(
                "{UISymbol} No .NET project found in {Directory}. Run 'winapp restore' from a directory containing a .csproj, or pass one as the base directory.",
                UiSymbols.Error,
                baseDirectory.FullName);
            return 1;
        }

        var projectToRestore = csprojFiles[0];

        logger.LogInformation(
            "{UISymbol} .NET project detected with no winapp.yaml — SDK packages are PackageReferences in {Project}. Running 'dotnet restore'.",
            UiSymbols.Note,
            projectToRestore.Name);

        // Let dotnet resolve nuget.config itself, relative to the project. That is the standard hierarchy —
        // the project's directory and its ancestors, merged with the user and machine levels — and it is
        // exactly what the user gets running `dotnet restore` by hand.
        //
        // Deliberately NOT forwarding the selected config directory as `--configfile`: that switch replaces
        // the whole hierarchy with one file, so a source declared there but authenticated through credentials
        // in the user-level config would start failing. Since the config root cannot be honored without that
        // loss, say so instead of silently restoring from feeds the user did not select.
        if (!DirectoryRelationship.IsSameOrAncestor(configDir, projectToRestore.Directory!))
        {
            logger.LogWarning(
                "{UISymbol} The selected configuration directory ({ConfigDir}) does not apply to {Project}: 'dotnet restore' resolves nuget.config relative to the project. Its own nuget.config hierarchy is used instead.",
                UiSymbols.Warning,
                configDir.FullName,
                projectToRestore.Name);
        }

        // Stream dotnet's output rather than inheriting the console, so each line passes through
        // NugetErrorMessage.Redact before it is displayed. NuGet's NU1301 failures quote the full source
        // URL, so a feed authenticated with a signed query string would otherwise print its credential
        // straight to the terminal and into CI logs — the one winapp-invoked path still doing that after
        // this PR's redaction work. The cost is dotnet's terminal logger being disabled because stdout is
        // redirected; restore output is a few lines, so that is a fair trade for not leaking a token.
        // Writes are serialized because the stdout/stderr callbacks arrive on background threads.
        var writeLock = new object();
        void WriteRedacted(string line)
        {
            var redacted = NugetErrorMessage.Redact(line);
            lock (writeLock)
            {
                logger.LogInformation("{Line}", redacted);
            }
        }

        var restoreExitCode = await dotNetService.RunDotnetStreamingAsync(
            projectToRestore.Directory!,
            $"restore \"{projectToRestore.FullName}\"",
            WriteRedacted,
            WriteRedacted,
            cancellationToken);

        if (restoreExitCode != 0)
        {
            logger.LogError(
                "'dotnet restore' failed for {Project} (exit code {ExitCode}).",
                projectToRestore.Name,
                restoreExitCode);
            return 1;
        }

        logger.LogInformation("{UISymbol} Restore completed for {Project}.", UiSymbols.Check, projectToRestore.Name);
        return 0;
    }
}
