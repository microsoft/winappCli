// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

internal sealed partial class ProjectRunService
{
    private sealed record ExistingOutputProjectTarget(FileInfo Project, FileInfo? Solution);

    /// <inheritdoc />
    public async Task<RunInputResolution> ResolveExistingOutputAsync(
        FileSystemInfo input,
        string? projectSelector,
        ExistingProjectOutputQuery query,
        CancellationToken cancellationToken)
    {
        var targets = await ResolveExistingOutputTargetsAsync(
            input,
            projectSelector,
            cancellationToken);
        if (targets is null)
        {
            return await ResolveInputAsync(
                input,
                cancellationToken,
                projectSelector,
                classificationInputs: null);
        }

        var candidates = new List<ExistingProjectOutputCandidate>();
        foreach (var target in targets)
        {
            candidates.AddRange(await DiscoverProjectExistingOutputsAsync(
                target.Project,
                query with { Solution = target.Solution },
                cancellationToken));
        }

        var filters = FormatExistingOutputFilters(query.Configuration, query.Architecture);
        if (candidates.Count == 0)
        {
            throw new ProjectRunException(
                $"No runnable existing output was found for '{input.Name}'{filters}. " +
                "Build it first, or rerun 'winapp perf record' with --build.");
        }

        if (candidates.Count > 1)
        {
            var list = string.Join(
                Environment.NewLine,
                candidates.Select(candidate =>
                    $"  {candidate.Resolution.Csproj.Name} | {candidate.Configuration} | " +
                    $"{candidate.Architecture} | {candidate.Resolution.TargetDir}"));
            throw new ProjectRunException(
                $"Several runnable outputs were found for '{input.Name}':{Environment.NewLine}{list}{Environment.NewLine}" +
                "Rerun with --project, --configuration and/or --arch to select one.");
        }

        var selected = candidates[0];
        var selectedTarget = targets.Single(target =>
            string.Equals(
                target.Project.FullName,
                selected.Resolution.Csproj.FullName,
                StringComparison.OrdinalIgnoreCase));
        return new RunInputResolution(
            WinAppRunMode.Project,
            selected.Resolution.Csproj,
            selected.Resolution.Csproj.Directory
                ?? new DirectoryInfo(Directory.GetCurrentDirectory()),
            selectedTarget.Solution,
            "only runnable existing output",
            ExistingOutput: selected);
    }

    private async Task<List<ExistingOutputProjectTarget>?> ResolveExistingOutputTargetsAsync(
        FileSystemInfo input,
        string? projectSelector,
        CancellationToken cancellationToken)
    {
        if (input is FileInfo file)
        {
            if (string.Equals(file.Extension, ".csproj", StringComparison.OrdinalIgnoreCase))
            {
                var projectDir = file.Directory ?? new DirectoryInfo(Directory.GetCurrentDirectory());
                if (!string.IsNullOrWhiteSpace(projectSelector)
                    && MatchProjectSelector([file], projectSelector, projectDir) is null)
                {
                    throw new ProjectRunException(
                        $"--project '{projectSelector}' does not match the specified project '{file.Name}'. Omit --project when passing a .csproj directly.");
                }

                return [new(file, FindOwningSolution(file))];
            }

            if (!IsSolutionFile(file))
            {
                return null;
            }

            var solutionDir = file.Directory ?? new DirectoryInfo(Directory.GetCurrentDirectory());
            var solutionProjects = await GetSolutionProjectsAsync(file, solutionDir, cancellationToken);
            if (solutionProjects.Count == 0)
            {
                throw new ProjectRunException(
                    $"No .csproj projects were found in '{file.Name}'. 'winapp run' needs a runnable C# project in the solution.");
            }

            return SelectExistingOutputTargets(solutionProjects, file, solutionDir, projectSelector);
        }

        var directory = (DirectoryInfo)input;
        var solutions = CollapseSolutionMigrationPairs(
            SafeEnumerateFiles(directory, "*.sln", "*.slnx"));
        if (solutions.Count > 1)
        {
            var names = string.Join(
                ", ",
                solutions.Select(solution => solution.Name)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
            throw new ProjectRunException(
                $"Multiple solution files found in '{directory.FullName}' ({names}). Specify which one to run, e.g. 'winapp run {solutions[0].Name}'.");
        }

        if (solutions.Count == 1)
        {
            var solutionProjects = await GetSolutionProjectsAsync(
                solutions[0],
                directory,
                cancellationToken);
            if (solutionProjects.Count == 0)
            {
                return null;
            }

            return SelectExistingOutputTargets(
                solutionProjects,
                solutions[0],
                directory,
                projectSelector);
        }

        var projects = SafeEnumerateFiles(directory, "*.csproj");
        if (projects.Count == 0)
        {
            return null;
        }

        return SelectExistingOutputTargets(
            projects,
            solution: null,
            directory,
            projectSelector);
    }

    private static List<ExistingOutputProjectTarget> SelectExistingOutputTargets(
        List<FileInfo> projects,
        FileInfo? solution,
        DirectoryInfo selectionRoot,
        string? projectSelector)
    {
        if (string.IsNullOrWhiteSpace(projectSelector))
        {
            return projects.Select(project => new ExistingOutputProjectTarget(project, solution)).ToList();
        }

        var selected = MatchProjectSelector(projects, projectSelector, selectionRoot);
        if (selected is null)
        {
            var available = FormatProjectNameList(projects.Select(project => project.Name));
            var source = solution?.Name ?? selectionRoot.FullName;
            throw new ProjectRunException(
                $"--project '{projectSelector}' did not match a single project in '{source}'. Available: {available}.");
        }

        return [new(selected, solution)];
    }

    private static string FormatExistingOutputFilters(
        string? configuration,
        string? architecture)
    {
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuration))
        {
            filters.Add($"configuration '{configuration}'");
        }
        if (!string.IsNullOrWhiteSpace(architecture))
        {
            filters.Add($"architecture '{architecture}'");
        }

        return filters.Count == 0
            ? string.Empty
            : $" using {string.Join(" and ", filters)}";
    }

    internal async Task<IReadOnlyList<ExistingProjectOutputCandidate>> DiscoverProjectExistingOutputsAsync(
        FileInfo csproj,
        ExistingProjectOutputQuery query,
        CancellationToken cancellationToken)
    {
        var discoveredDimensions = await DiscoverBuildDimensionsAsync(
            csproj,
            query,
            cancellationToken);
        var configurations = string.IsNullOrWhiteSpace(query.Configuration)
            ? DiscoverConfigurations(csproj, discoveredDimensions.Configurations)
            : [query.Configuration];
        var architectures = string.IsNullOrWhiteSpace(query.Architecture)
            ? DiscoverArchitectures(csproj, discoveredDimensions.Platforms)
            : [query.Architecture];

        var candidates = new List<ExistingProjectOutputCandidate>();
        var seenOutputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var configuration in configurations)
        {
            foreach (var architecture in architectures)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var options = new ProjectRunOptions(
                    configuration,
                    architecture,
                    query.Framework,
                    NoBuild: true,
                    NoRestore: true,
                    query.Properties,
                    query.Json,
                    query.Solution,
                    SuppressDiagnostics: true);

                ProjectBuildOutcome outcome;
                try
                {
                    outcome = await BuildAndResolveAsync(csproj, options, cancellationToken);
                }
                catch (ProjectRunException)
                {
                    continue;
                }

                if (outcome.Resolution is not { } resolution || !IsViableExistingOutput(resolution))
                {
                    continue;
                }

                var actualArchitecture = DetectOutputArchitecture(resolution) ?? architecture;
                if (!string.IsNullOrWhiteSpace(query.Architecture)
                    && !string.Equals(actualArchitecture, query.Architecture, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                resolution = resolution with { Architecture = actualArchitecture };
                var key = BuildOutputKey(resolution);
                if (seenOutputs.Add(key))
                {
                    candidates.Add(new(resolution, configuration, actualArchitecture));
                }
            }
        }

        return candidates;
    }

    private async Task<(IReadOnlyList<string> Configurations, IReadOnlyList<string> Platforms)>
        DiscoverBuildDimensionsAsync(
            FileInfo csproj,
            ExistingProjectOutputQuery query,
            CancellationToken cancellationToken)
    {
        var tokens = new List<string>
        {
            "msbuild",
            csproj.FullName,
        };
        foreach (var property in ForwardableProperties(query.Properties))
        {
            tokens.Add($"-p:{property}");
        }
        if (query.Solution is not null)
        {
            foreach (var token in BuildSolutionPropertyTokens(query.Solution))
            {
                if (!UserSpecifiesProperty(query.Properties, SolutionPropertyName(token)))
                {
                    tokens.Add(token);
                }
            }
        }
        if (!string.IsNullOrWhiteSpace(query.Framework))
        {
            tokens.Add($"-p:TargetFramework={query.Framework}");
        }
        tokens.Add("--getProperty:Configurations");
        tokens.Add("--getProperty:Platforms");

        try
        {
            var workingDirectory = csproj.Directory ?? new DirectoryInfo(Directory.GetCurrentDirectory());
            var arguments = WindowsCommandLine.JoinArguments(tokens) ?? string.Empty;
            logger.LogDebug("{UISymbol} dotnet {Arguments}", UiSymbols.Note, RedactSecretsForDisplay(arguments));
            var (exitCode, output, _) = await dotNetService.RunDotnetCommandAsync(
                workingDirectory,
                arguments,
                cancellationToken);
            if (exitCode != 0)
            {
                return ([], []);
            }

            var properties = MsBuildPropertyReader.Parse(output, ["Configurations", "Platforms"]);
            return (
                SplitListProperty(GetProp(properties, "Configurations")),
                SplitListProperty(GetProp(properties, "Platforms")));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return ([], []);
        }
    }

    private static List<string> DiscoverConfigurations(
        FileInfo csproj,
        IReadOnlyList<string> evaluatedConfigurations)
    {
        var values = evaluatedConfigurations
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var value in ReadDeclaredListProperty(csproj, "Configurations"))
        {
            AddDistinct(values, value);
        }
        AddDistinct(values, "Debug");
        AddDistinct(values, "Release");
        return values;
    }

    private static List<string> DiscoverArchitectures(
        FileInfo csproj,
        IReadOnlyList<string> evaluatedPlatforms)
    {
        var values = evaluatedPlatforms
            .Concat(ReadDeclaredPlatformTokens(csproj))
            .Select(RunArchHelper.NormalizeArchitecture)
            .Where(value => value is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var architecture in RunArchHelper.SupportedArchitectures)
        {
            AddDistinct(values, architecture);
        }

        return values;
    }

    private static string[] SplitListProperty(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static List<string> ReadDeclaredListProperty(FileInfo project, string propertyName)
    {
        try
        {
            var document = XDocument.Load(project.FullName, LoadOptions.None);
            return document
                .Descendants()
                .Where(element => string.Equals(element.Name.LocalName, propertyName, StringComparison.OrdinalIgnoreCase))
                .SelectMany(element => element.Value.Split(
                    ';',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Where(value => !value.Contains("$(", StringComparison.Ordinal))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return [];
        }
    }

    private static void AddDistinct(List<string> values, string value)
    {
        if (!values.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            values.Add(value);
        }
    }

    private static bool IsViableExistingOutput(ProjectRunResolution resolution)
    {
        if (!Directory.Exists(resolution.TargetDir))
        {
            return false;
        }

        return resolution.Packaging == ProjectPackaging.Unpackaged
            ? !string.IsNullOrWhiteSpace(resolution.RunCommand)
              && RunCommandIsLaunchable(resolution.RunCommand)
            : ManifestHelper.FindManifest(resolution.TargetDir).Exists;
    }

    private static string BuildOutputKey(ProjectRunResolution resolution)
    {
        var target = Path.GetFullPath(resolution.TargetDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var command = string.IsNullOrWhiteSpace(resolution.RunCommand)
            ? string.Empty
            : Path.IsPathRooted(resolution.RunCommand)
                ? Path.GetFullPath(resolution.RunCommand)
                : resolution.RunCommand;
        return $"{resolution.Packaging}|{target}|{command}|{resolution.RunArguments}";
    }

    private static string? DetectOutputArchitecture(ProjectRunResolution resolution)
    {
        string? executable = null;
        string? declaredArchitecture = null;
        if (resolution.Packaging == ProjectPackaging.Unpackaged
            && !string.IsNullOrWhiteSpace(resolution.RunCommand)
            && Path.IsPathRooted(resolution.RunCommand)
            && File.Exists(resolution.RunCommand))
        {
            executable = resolution.RunCommand;
        }
        else if (resolution.Packaging == ProjectPackaging.Packaged)
        {
            var manifest = ManifestHelper.FindManifest(resolution.TargetDir);
            if (manifest.Exists)
            {
                try
                {
                    var document = AppxManifestDocument.Load(manifest.FullName);
                    declaredArchitecture = RunArchHelper.NormalizeArchitecture(
                        document.IdentityProcessorArchitecture);
                    var relativeExecutable = document.ApplicationExecutable;
                    if (!string.IsNullOrWhiteSpace(relativeExecutable)
                        && !relativeExecutable.Contains('$'))
                    {
                        var candidate = Path.GetFullPath(
                            relativeExecutable.Replace('/', Path.DirectorySeparatorChar),
                            resolution.TargetDir);
                        if (File.Exists(candidate))
                        {
                            executable = candidate;
                        }
                    }

                    if (executable is null)
                    {
                        var executables = new DirectoryInfo(resolution.TargetDir)
                            .EnumerateFiles("*.exe", SearchOption.TopDirectoryOnly)
                            .Where(file => !MsixService.IsRuntimeToolExecutable(file.Name))
                            .Take(2)
                            .ToList();
                        executable = executables.Count == 1 ? executables[0].FullName : null;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
                {
                }
            }
        }

        return executable is null
            ? declaredArchitecture
            : PeHelper.DetectPeArchitecture(executable) ?? declaredArchitecture;
    }
}
