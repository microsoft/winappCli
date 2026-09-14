// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

internal sealed partial class ProjectRunService
{
    /// <inheritdoc />
    public async Task<NativeMsixPublishOutcome> PublishNativeMsixAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        DirectoryInfo packageDir,
        CancellationToken cancellationToken)
    {
        var workingDir = csproj.Directory ?? new DirectoryInfo(Directory.GetCurrentDirectory());

        // Resolve the same effective TFM / shim / platform / publish-profile the run and generic-publish
        // passes use, so the MSIX targets evaluate against the project's intended configuration.
        (options, _, var csWinRTMetadata) =
            await PrepareBuildInputsAsync(csproj, options, workingDir, cancellationToken);

        var verbosity = ResolveBuildVerbosity(logger, options.Json);
        var arguments = BuildNativeMsixPublishArguments(csproj, options, packageDir, verbosity, csWinRTMetadata);
        var argString = WindowsCommandLine.JoinArguments(arguments) ?? string.Empty;
        var display = RedactSecretsForDisplay(argString);

        // --getProperty suppresses MSBuild's normal console output, so print the status up front (the SDK's
        // native packaging can take a while, especially with Native AOT codegen) and keep the invocation
        // discoverable at debug.
        if (!options.Json && logger.IsEnabled(LogLevel.Information))
        {
            ansiConsole.MarkupLineInterpolated(
                $"{UiSymbols.Package} Packaging {csproj.Name} ({options.Configuration} | {options.Architecture})...");
            ansiConsole.MarkupLineInterpolated($"[dim]   dotnet {Markup.Escape(display)}[/]");
        }
        logger.LogDebug("{UISymbol} dotnet {Arguments}", UiSymbols.Note, display);

        var (exitCode, stdout, stderr) = await dotNetService.RunDotnetCommandAsync(workingDir, argString, cancellationToken);

        if (exitCode != 0)
        {
            // The structured-output invocation hides diagnostics, so surface dotnet's captured output on
            // failure (redacting any authenticated NuGet source URLs) before propagating the exit code.
            foreach (var line in SplitDiagnosticLines(stderr).Concat(SplitDiagnosticLines(stdout)))
            {
                Console.Error.WriteLine(NugetErrorMessage.Redact(line));
            }
            logger.LogError(
                "{UISymbol} Native MSIX packaging failed for {Project} (exit code {ExitCode}).",
                UiSymbols.Error, csproj.Name, exitCode);
            return new NativeMsixPublishOutcome(null, exitCode);
        }

        var properties = MsBuildPropertyReader.Parse(stdout, ["AppxPackageOutput"]);
        var appxPackageOutput = GetProp(properties, "AppxPackageOutput");
        if (string.IsNullOrWhiteSpace(appxPackageOutput))
        {
            throw new ProjectRunException(
                $"'{csproj.Name}' resolves to a packaged (MSIX) app but native packaging produced no artifact " +
                "(AppxPackageOutput was empty). Ensure the Windows App SDK MSIX tooling is installed for the project.");
        }

        var resolved = Path.GetFullPath(appxPackageOutput, workingDir.FullName);

        // The package must be the one the SDK produced under winapp's own AppxPackageDir staging location.
        // A project target that redirects AppxPackageOutput elsewhere (including a network path) must not
        // steer winapp's delivery/signing at an arbitrary or remote file.
        if (PathSafety.IsNetworkPath(resolved) || !PathSafety.IsUnder(resolved, packageDir.FullName))
        {
            throw new ProjectRunException(
                $"Native MSIX packaging for '{csproj.Name}' reported a package outside winapp's staging " +
                $"directory: {resolved}. winapp only delivers the package the SDK produced under its own output location.");
        }

        if (!File.Exists(resolved))
        {
            throw new ProjectRunException(
                $"Native MSIX packaging for '{csproj.Name}' reported a package that does not exist: {resolved}.");
        }

        return new NativeMsixPublishOutcome(new FileInfo(resolved), 0);
    }

    private static IEnumerable<string> SplitDiagnosticLines(string? output) =>
        string.IsNullOrEmpty(output)
            ? []
            : output.Split('\n').Select(line => line.TrimEnd('\r'));
}
