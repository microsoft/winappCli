// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// The pre-build restore passes and how their output reaches the user. Restore output always flows
/// through winapp, never inherited stdio, so authenticated NuGet source URLs can be redacted first.
/// </summary>
internal sealed partial class ProjectRunService
{
    /// <summary>How a restore step shows its dotnet output.</summary>
    private enum RestoreOutputMode
    {
        /// <summary><c>--json</c>/<c>--quiet</c>: every line goes to stderr so stdout stays clean.</summary>
        Redirected,

        /// <summary>Agents, CI, redirected output and <c>--verbose</c>: every line streams live as plain text.</summary>
        Streamed,

        /// <summary>
        /// Interactive terminal: a spinner while dotnet runs, then one result line plus any warnings, or
        /// the full output and command of a failed restore.
        /// </summary>
        Summarized,
    }

    /// <summary>One <c>dotnet restore</c> invocation within a <see cref="RestoreStep"/>.</summary>
    private sealed class RestoreInvocation(string arguments)
    {
        public string Arguments { get; } = arguments;

        public int ExitCode { get; set; }

        /// <summary>Redacted output of this invocation.</summary>
        public List<string> Lines { get; } = [];

        /// <summary>
        /// Whether a failure of this invocation is reported with its output and command. Cleared for a
        /// solution-level restore whose projects are then restored, and reported, one by one.
        /// </summary>
        public bool ReportFailure { get; set; } = true;
    }

    /// <summary>
    /// One user-visible restore step, which may run several <c>dotnet restore</c> invocations (the solution,
    /// a per-project fallback, the target).
    /// </summary>
    private sealed class RestoreStep(ProjectRunService service, string subject, ProjectRunOptions options, DirectoryInfo workingDirectory, RestoreOutputMode mode)
    {
        public string Subject { get; } = subject;

        public ProjectRunOptions Options { get; } = options;

        public DirectoryInfo WorkingDirectory { get; } = workingDirectory;

        public RestoreOutputMode Mode { get; } = mode;

        public List<RestoreInvocation> Invocations { get; } = [];

        /// <summary>
        /// Warnings buffered while a spinner owns the console, each with the number of invocations that had
        /// run when it was raised, so the summary keeps them in order with the failures they follow.
        /// </summary>
        public List<(int AfterInvocations, string Message)> PendingWarnings { get; } = [];

        public bool Announced { get; set; }

        public void Warn(string message)
        {
            if (Mode == RestoreOutputMode.Summarized)
            {
                PendingWarnings.Add((Invocations.Count, message));
                return;
            }

            service.WriteRestoreFallbackWarning(Options, message);
        }
    }

    /// <summary>Matches an MSBuild/NuGet diagnostic line that reports a warning (e.g. <c>… : warning NU1901: …</c>).</summary>
    [GeneratedRegex(@"(^|[\s:])warning( [A-Za-z]+\d+)?\s*:", RegexOptions.CultureInvariant)]
    private static partial Regex RestoreWarningLineRegex();

    /// <summary>The trailing <c> [project]</c> MSBuild appends to a diagnostic to name the entry project.</summary>
    [GeneratedRegex(@"\s+\[[^\[\]]+\]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex DiagnosticProjectSuffixRegex();

    /// <summary>Matches an MSBuild/NuGet diagnostic line that reports an error, capturing its code.</summary>
    [GeneratedRegex(@"(^|[\s:])error( (?<code>[A-Za-z]+\d+))?\s*:", RegexOptions.CultureInvariant)]
    private static partial Regex RestoreErrorLineRegex();

    /// <summary>
    /// True when the build pass will hand the console to dotnet's native terminal logger: a real
    /// interactive terminal with normal output (not <c>--json</c>/<c>--quiet</c>, agents, CI or redirection).
    /// </summary>
    private bool UsesNativeTerminalBuild(ProjectRunOptions options) =>
        !options.Json
        && logger.IsEnabled(LogLevel.Information)
        && (NativeTerminalGateOverrideForTests?.Invoke() ?? ProgressDisplay.ShouldUseLiveSpinner(ansiConsole, logger));

    private RestoreOutputMode ResolveRestoreOutputMode(ProjectRunOptions options)
    {
        if (options.Json || !logger.IsEnabled(LogLevel.Information))
        {
            return RestoreOutputMode.Redirected;
        }

        // --verbose keeps the plain live stream so winapp's own traces stay interleaved with dotnet's output.
        return UsesNativeTerminalBuild(options) && !logger.IsEnabled(LogLevel.Debug)
            ? RestoreOutputMode.Summarized
            : RestoreOutputMode.Streamed;
    }

    /// <summary>Names what a restore step covers, e.g. <c>FluentStore.App and 19 solution projects</c>.</summary>
    private static string DescribeRestoreSubject(FileInfo target, bool includesTarget, int siblingCount)
    {
        var projects = siblingCount == 1 ? "1 solution project" : $"{siblingCount} solution projects";
        var app = Path.GetFileNameWithoutExtension(target.Name);
        if (!includesTarget)
        {
            return projects;
        }

        return siblingCount == 0 ? app : $"{app} and {projects}";
    }

    /// <summary>
    /// Runs <paramref name="body"/> as one restore step. On an interactive terminal its restores run behind
    /// a spinner and are summarized afterwards; otherwise their output streams live (stderr for
    /// <c>--json</c>/<c>--quiet</c>).
    /// </summary>
    private async Task<T> RunRestoreStepAsync<T>(
        string subject,
        ProjectRunOptions options,
        DirectoryInfo workingDirectory,
        Func<RestoreStep, Task<T>> body,
        CancellationToken cancellationToken)
    {
        var step = new RestoreStep(this, subject, options, workingDirectory, ResolveRestoreOutputMode(options));
        if (step.Mode != RestoreOutputMode.Summarized)
        {
            return await body(step);
        }

        var stopwatch = Stopwatch.StartNew();
        // Status text is markup; a project name such as App[1] must not be read as a style.
        var status = $"Restoring {Markup.Escape(subject)}...";
        try
        {
            return await ansiConsole.Status()
                .AutoRefresh(true)
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("blue"))
                .StartAsync(status, async context =>
                {
                    var work = body(step);
                    while (!cancellationToken.IsCancellationRequested
                        && await Task.WhenAny(work, Task.Delay(1000, cancellationToken)) != work)
                    {
                        context.Status($"{status} {stopwatch.Elapsed.TotalSeconds:0}s");
                    }

                    return await work;
                });
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                PrintRestoreSummary(step, stopwatch.Elapsed);
            }
        }
    }

    /// <summary>Runs one <c>dotnet restore</c> inside <paramref name="step"/>, displayed per its output mode.</summary>
    private async Task<RestoreInvocation> RunRestoreAsync(RestoreStep step, string arguments, CancellationToken cancellationToken)
    {
        var invocation = new RestoreInvocation(arguments);
        step.Invocations.Add(invocation);
        var display = RedactSecretsForDisplay(arguments);

        // Every mode keeps the redacted lines: a failed solution restore's errors decide whether retrying
        // projects one by one can help, and a summarized step prints them afterwards.
        var gate = new object();
        Action<string> Capture(Action<string>? forward) => line =>
        {
            var redacted = NugetErrorMessage.Redact(line);
            lock (gate)
            {
                invocation.Lines.Add(redacted);
                forward?.Invoke(redacted);
            }
        };

        switch (step.Mode)
        {
            case RestoreOutputMode.Redirected:
                // --json keeps the injected arguments discoverable on stderr; --quiet suppresses them.
                if (step.Options.Json)
                {
                    Console.Error.WriteLine($"dotnet {display}");
                }

                var toStderr = Capture(static line => Console.Error.WriteLine(line));
                invocation.ExitCode = await dotNetService.RunDotnetStreamingAsync(
                    step.WorkingDirectory, arguments, toStderr, toStderr, cancellationToken: cancellationToken);
                break;

            case RestoreOutputMode.Streamed:
                if (!step.Announced)
                {
                    ansiConsole.MarkupLineInterpolated($"{UiSymbols.Sync} Restoring {step.Subject}...");
                    step.Announced = true;
                }

                var verbose = logger.IsEnabled(LogLevel.Debug);
                if (verbose)
                {
                    ansiConsole.MarkupLineInterpolated($"[dim]   dotnet {display}[/]");
                }

                // Write unwrapped, like the subprocess would, so long paths stay intact.
                var writer = ansiConsole.Profile.Out.Writer;
                var live = Capture(writer.WriteLine);
                invocation.ExitCode = await dotNetService.RunDotnetStreamingAsync(
                    step.WorkingDirectory, arguments, live, live, cancellationToken: cancellationToken);
                if (invocation.ExitCode != 0 && !verbose)
                {
                    PrintFailedCommand($"dotnet {display}");
                }

                break;

            case RestoreOutputMode.Summarized:
                var buffered = Capture(null);
                invocation.ExitCode = await dotNetService.RunDotnetStreamingAsync(
                    step.WorkingDirectory, arguments, buffered, buffered, cancellationToken: cancellationToken);
                break;
        }

        return invocation;
    }

    /// <summary>
    /// Prints a summarized restore step: the output and command of each reported failure, any buffered
    /// warnings, and — when nothing failed — one result line followed by the restore's warnings, once each.
    /// </summary>
    private void PrintRestoreSummary(RestoreStep step, TimeSpan elapsed)
    {
        if (step.Invocations.Count == 0)
        {
            return;
        }

        var writer = ansiConsole.Profile.Out.Writer;
        var failed = false;
        for (var i = 0; i <= step.Invocations.Count; i++)
        {
            foreach (var (_, message) in step.PendingWarnings.Where(w => w.AfterInvocations == i))
            {
                WriteRestoreFallbackWarning(step.Options, message);
            }

            if (i == step.Invocations.Count)
            {
                break;
            }

            var invocation = step.Invocations[i];
            if (invocation.ExitCode == 0 || !invocation.ReportFailure)
            {
                continue;
            }

            failed = true;
            foreach (var line in invocation.Lines)
            {
                writer.WriteLine(line);
            }

            PrintFailedCommand($"dotnet {RedactSecretsForDisplay(invocation.Arguments)}");
        }

        if (failed)
        {
            return;
        }

        var warnings = ExtractRestoreWarnings(step.Invocations.Where(i => i.ExitCode == 0).SelectMany(i => i.Lines));
        var seconds = elapsed.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        if (warnings.Count == 0)
        {
            ansiConsole.MarkupLineInterpolated($"{UiSymbols.Check} Restored {step.Subject} in {seconds}s");
            return;
        }

        ansiConsole.MarkupLineInterpolated(
            $"{UiSymbols.Check} Restored {step.Subject} with {warnings.Count} warning(s) in {seconds}s");
        var ansi = ansiConsole.Profile.Capabilities.Ansi;
        foreach (var warning in warnings)
        {
            // Unwrapped, like dotnet's own terminal logger, so long paths stay copyable.
            writer.WriteLine(ansi ? $"    \u001b[33m{warning}\u001b[0m" : $"    {warning}");
        }
    }

    /// <summary>
    /// The distinct warnings in restore output. NuGet repeats a warning once per entry project, differing
    /// only in the trailing <c>[project]</c>, so that suffix is dropped before de-duplicating.
    /// </summary>
    internal static List<string> ExtractRestoreWarnings(IEnumerable<string> lines)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var warnings = new List<string>();
        foreach (var line in lines)
        {
            if (!RestoreWarningLineRegex().IsMatch(line))
            {
                continue;
            }

            var warning = DiagnosticProjectSuffixRegex().Replace(line.Trim(), string.Empty);
            if (seen.Add(warning))
            {
                warnings.Add(warning);
            }
        }

        return warnings;
    }

    /// <summary>Shows the exact command of a failed step so the failure is reproducible.</summary>
    private void PrintFailedCommand(string command) =>
        ansiConsole.MarkupLineInterpolated($"[dim]   Command: {command}[/]");

    /// <summary>
    /// Restores the owning solution's managed sibling projects before the target build so build-dependency
    /// siblings that aren't <c>ProjectReference</c>s still have a <c>project.assets.json</c> (NETSDK1004
    /// parity with VS / <c>dotnet build &lt;sln&gt;</c>). When every listed project is managed and on disk,
    /// a single <c>dotnet restore &lt;sln&gt;</c> covers the whole graph and this returns
    /// <see langword="true"/>. Otherwise (a native or missing project, which fail a whole-solution restore,
    /// or an inferred publish profile that belongs only to the target), one restore runs over a temporary
    /// solution filter listing just the managed siblings on disk. Only when that fails are siblings
    /// restored one by one. Build-mode restores are best-effort; package preparation stops on a failed
    /// dependency restore.
    /// </summary>
    private async Task<bool> RestoreSolutionSiblingsAsync(
        RestoreStep step,
        SolutionRestorePlan plan,
        ProjectRunOptions options,
        bool publish,
        CancellationToken cancellationToken)
    {
        if (options.Solution is not { } solution || plan.ManagedSiblings.Count == 0)
        {
            return false;
        }

        var verbosity = ResolveRestoreVerbosity(logger, options.Json);
        RestoreInvocation solutionRestore;
        var restoresWholeSolution = plan.CanRestoreWholeSolution && string.IsNullOrWhiteSpace(options.PublishProfile);
        if (restoresWholeSolution)
        {
            // Closest to VS: one restore over the whole solution pulls the target and every sibling.
            logger.LogDebug("{UISymbol} Restoring solution before build for build-dependency parity.", UiSymbols.Note);
            solutionRestore = await RunRestoreAsync(step, BuildRestorePassArguments(solution, options, verbosity), cancellationToken);
            if (solutionRestore.ExitCode == 0)
            {
                return true;
            }
        }
        else
        {
            // An inferred PublishProfile belongs only to the selected app, so siblings restore without it
            // and the target restores separately under the profile.
            var siblingOptions = options with { PublishProfile = null };
            var filter = WriteSolutionFilter(solution, plan.ManagedSiblingEntries);
            logger.LogDebug(
                "{UISymbol} Restoring {Count} solution projects through solution filter {Filter} for build-dependency parity.",
                UiSymbols.Note, plan.ManagedSiblings.Count, filter.FullName);
            try
            {
                solutionRestore = await RunRestoreAsync(
                    step, BuildRestorePassArguments(filter, siblingOptions, verbosity), cancellationToken);
            }
            finally
            {
                TryDeleteFile(filter.FullName);
            }

            if (solutionRestore.ExitCode == 0)
            {
                return false;
            }
        }

        // Don't defer to the target-only build restore (that leaves non-ProjectReference managed siblings
        // unrestored — the NETSDK1004 case this prevents); restore each sibling instead. NuGet restores every
        // project it can and reports package failures per project, so when its errors are the only ones,
        // the other projects are already restored and retrying one by one would only repeat the errors.
        var scope = restoresWholeSolution ? "Solution restore" : "Restore of the solution's projects";
        if (FailedOnlyWithPackageErrors(solutionRestore.Lines))
        {
            // Package preparation still restores the target's exact publish graph next, and that restore
            // decides: an error here may come from a framework or platform the publish doesn't use.
            step.Warn(
                $"{UiSymbols.Warning} {scope} failed (exit code {solutionRestore.ExitCode}); continuing with the build, which will report any unresolved dependency errors.");
            return false;
        }

        step.Warn(
            $"{UiSymbols.Warning} {scope} failed (exit code {solutionRestore.ExitCode}); retrying {plan.ManagedSiblings.Count} project(s) individually.");

        // Each failing project reports its own output, which may differ from the solution-level failure
        // (that one may have stopped before reaching it), so the solution-level output isn't repeated.
        solutionRestore.ReportFailure = false;
        await RestoreSiblingsIndividuallyAsync(step, plan.ManagedSiblings, options, publish, cancellationToken);
        return false;
    }

    /// <summary>
    /// True when restore output reports at least one error and every error is a NuGet package error
    /// (<c>NUxxxx</c>) rather than one that stops restore altogether, such as a project that fails to load.
    /// </summary>
    internal static bool FailedOnlyWithPackageErrors(IEnumerable<string> lines)
    {
        var any = false;
        foreach (var match in lines.Select(line => RestoreErrorLineRegex().Match(line)))
        {
            if (!match.Success)
            {
                continue;
            }

            if (!match.Groups["code"].Value.StartsWith("NU", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            any = true;
        }

        return any;
    }

    /// <summary>
    /// Restores each managed sibling individually (skipping the target, which restores separately). Used
    /// only when a solution-level restore fails. Package preparation stops at the first failure.
    /// </summary>
    private async Task RestoreSiblingsIndividuallyAsync(
        RestoreStep step,
        IReadOnlyList<FileInfo> siblings,
        ProjectRunOptions options,
        bool publish,
        CancellationToken cancellationToken)
    {
        var siblingOptions = options with { PublishProfile = null };
        var verbosity = ResolveRestoreVerbosity(logger, siblingOptions.Json);
        var failed = new List<string>();
        foreach (var sibling in siblings)
        {
            logger.LogDebug("{UISymbol} Restoring solution sibling {Sibling} before build for build-dependency parity.", UiSymbols.Note, sibling.Name);
            var restore = await RunRestoreAsync(step, BuildRestorePassArguments(sibling, siblingOptions, verbosity), cancellationToken);
            if (restore.ExitCode == 0)
            {
                continue;
            }

            if (publish)
            {
                throw new ProjectRunException($"Publish restore failed for '{sibling.Name}' (exit code {restore.ExitCode}).");
            }

            failed.Add(sibling.Name);
        }

        if (failed.Count > 0)
        {
            step.Warn(
                $"{UiSymbols.Warning} Could not restore {failed.Count} solution project(s) ({string.Join(", ", failed)}); continuing with the build, which will report any unresolved dependency errors.");
        }
    }

    /// <summary>
    /// Writes a temporary solution filter (<c>.slnf</c>) that selects <paramref name="entries"/> from
    /// <paramref name="solution"/>, so one <c>dotnet restore</c> covers exactly those projects without
    /// tripping over the solution's missing or native ones. Entries must be spelled as the solution lists them.
    /// </summary>
    internal static FileInfo WriteSolutionFilter(FileInfo solution, IReadOnlyList<string> entries)
    {
        var path = Path.Join(
            Path.GetTempPath(),
            $"winapp-restore-{Path.GetFileNameWithoutExtension(solution.Name)}-{Guid.NewGuid():N}.slnf");
        using (var stream = File.Create(path))
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("solution");
            writer.WriteString("path", solution.FullName);
            writer.WriteStartArray("projects");
            foreach (var entry in entries)
            {
                writer.WriteStringValue(entry);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return new FileInfo(path);
    }

    /// <summary>
    /// Notes, once, that the solution lists projects that aren't on disk (commonly an uninitialized git
    /// submodule). Restoring them would only fail, so they are skipped.
    /// </summary>
    private void ReportMissingSolutionProjects(ProjectRunOptions options, FileInfo solution, IReadOnlyList<string> missing)
    {
        logger.LogDebug(
            "{UISymbol} Projects listed in {Solution} but not on disk: {Projects}",
            UiSymbols.Note, solution.Name, string.Join(", ", missing));
        if (options.Json || !logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        var projects = missing.Count == 1 ? "1 project" : $"{missing.Count} projects";
        var example = missing.Count == 1 ? missing[0] : $"{missing[0]} and {missing.Count - 1} more";
        logger.LogInformation(
            "{UISymbol} Skipping {Projects} listed in {Solution} that aren't on disk ({Example}).",
            UiSymbols.Info, projects, solution.Name, example);
    }

    private static bool HasEffectivePlatform(ProjectRunOptions options) =>
        !string.IsNullOrWhiteSpace(options.Platform)
        || UserSpecifiesProperty(options.Properties, "Platform");

    private void WriteRestoreFallbackWarning(ProjectRunOptions options, string message)
    {
        if (options.Json || !logger.IsEnabled(LogLevel.Warning))
        {
            return;
        }

        if (!logger.IsEnabled(LogLevel.Information))
        {
            Console.Error.WriteLine(message);
            return;
        }

        logger.LogWarning("{Message}", message);
    }

    /// <summary>
    /// Runs a single restore as its own step. Output always flows through winapp so authenticated NuGet
    /// source URLs are redacted before they reach the terminal or CI logs.
    /// </summary>
    internal async Task<int> RunRestoreCommandAsync(
        string arguments,
        string subject,
        ProjectRunOptions options,
        DirectoryInfo workingDir,
        CancellationToken cancellationToken)
    {
        var restore = await RunRestoreStepAsync(
            subject,
            options,
            workingDir,
            step => RunRestoreAsync(step, arguments, cancellationToken),
            cancellationToken);
        return restore.ExitCode;
    }

    private static string? ResolveRestoreVerbosity(ILogger logger, bool json) =>
        !json && !logger.IsEnabled(LogLevel.Information) ? "quiet" : null;
}
