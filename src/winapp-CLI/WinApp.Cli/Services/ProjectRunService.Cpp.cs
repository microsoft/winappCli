// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// C++ (<c>.vcxproj</c>) project mode: builds with the Visual Studio <c>MSBuild.exe</c> (the .NET SDK has
/// no C++ project system) and resolves the same <see cref="ProjectRunResolution"/> the <c>.csproj</c> path
/// produces, so launch, registration and runtime provisioning are shared.
/// </summary>
internal sealed partial class ProjectRunService
{
    /// <summary>Properties read from the evaluate pass after the build.</summary>
    private static readonly string[] CppRequestedProperties =
    [
        "OutDir",
        "TargetPath",
        "ConfigurationType",
        "AppxPackage",
        "WindowsPackageType",
        "WindowsAppSDKSelfContained",
        "AppxPackageRecipe",
        "FinalAppxManifestName",
        "Configuration",
        "Platform",
    ];

    /// <summary>True for a C++ project (<c>.vcxproj</c>), which <c>winapp run</c> builds with MSBuild instead of <c>dotnet</c>.</summary>
    internal static bool IsCppProject(FileInfo project) =>
        string.Equals(project.Extension, ".vcxproj", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when a <c>.vcxproj</c> builds an executable (<c>ConfigurationType=Application</c>), read from the
    /// project XML. Used to pick runnable candidates without spawning MSBuild.
    /// </summary>
    internal static bool IsCppApplicationProject(FileInfo project)
    {
        try
        {
            return XDocument.Load(project.FullName)
                .Descendants()
                .Any(e => e.Name.LocalName == "ConfigurationType"
                    && string.Equals(e.Value.Trim(), "Application", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return false;
        }
    }

    /// <summary>The Visual Studio C++ <c>Platform</c> for a winapp architecture.</summary>
    internal static string ToCppPlatform(string architecture) => architecture.ToLowerInvariant() switch
    {
        "x86" => "Win32",
        "arm64" => "ARM64",
        _ => "x64",
    };

    private async Task<ProjectBuildOutcome> BuildAndResolveCppAsync(
        FileInfo project,
        ProjectRunOptions options,
        CancellationToken cancellationToken)
    {
        var msbuild = await msBuildService.LocateCppMSBuildAsync(options.Architecture, cancellationToken);
        var properties = BuildCppPropertyTokens(options);

        if (!options.NoBuild)
        {
            var buildExit = await RunCppBuildPassAsync(msbuild, project, options, properties, cancellationToken);
            if (buildExit != 0)
            {
                logger.LogError("{UISymbol} Build failed for {Project} (exit code {ExitCode}).", UiSymbols.Error, project.Name, buildExit);
                return new ProjectBuildOutcome(null, buildExit);
            }
        }

        List<string> evaluateArgs = [project.FullName, "-nologo", .. properties, .. CppRequestedProperties.Select(p => $"-getProperty:{p}")];
        logger.LogDebug("{UISymbol} {Command}", UiSymbols.Note, RedactSecretsForDisplay(WindowsCommandLine.JoinArguments([msbuild, .. evaluateArgs]) ?? string.Empty));
        var evaluation = await msBuildService.RunAsync(msbuild, evaluateArgs, onLine: null, cancellationToken);
        if (evaluation.ExitCode != 0)
        {
            logger.LogError("{UISymbol} Property evaluation failed for {Project} (exit code {ExitCode}).", UiSymbols.Error, project.Name, evaluation.ExitCode);
            var diagnostics = string.Join(Environment.NewLine,
                new[] { evaluation.StandardOutput, evaluation.StandardError }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.TrimEnd()));
            if (!string.IsNullOrWhiteSpace(diagnostics))
            {
                if (options.Json)
                {
                    Console.Error.WriteLine(diagnostics);
                }
                else
                {
                    ansiConsole.WriteLine(diagnostics);
                }
            }

            return new ProjectBuildOutcome(null, evaluation.ExitCode);
        }

        var props = MsBuildPropertyReader.Parse(evaluation.StandardOutput, CppRequestedProperties);
        return new ProjectBuildOutcome(CreateCppResolution(project, options, props), 0);
    }

    /// <summary>
    /// The global properties shared by the build and evaluate passes: the user's <c>-p</c> first, then the
    /// solution context and Configuration/Platform. MSBuild is last-wins, so a user <c>-p:Platform</c> or
    /// <c>Solution*</c> is honored by not emitting winapp's own; Configuration always follows <c>-c</c>.
    /// </summary>
    internal static List<string> BuildCppPropertyTokens(ProjectRunOptions options)
    {
        var tokens = ForwardableProperties(options.Properties).Select(p => $"-p:{p}").ToList();
        AppendSolutionProperties(tokens, options);
        tokens.Add($"-p:Configuration={EscapeMsBuildPropertyValue(options.Configuration)}");
        if (!UserSpecifiesProperty(options.Properties, "Platform"))
        {
            tokens.Add($"-p:Platform={ToCppPlatform(options.Architecture)}");
        }

        return tokens;
    }

    /// <summary>
    /// Builds the C++ project, streaming MSBuild's output like the <c>.csproj</c> build pass. Restores
    /// <c>packages.config</c> NuGet packages first unless <c>--no-restore</c>, as Visual Studio does.
    /// </summary>
    private async Task<int> RunCppBuildPassAsync(
        string msbuild,
        FileInfo project,
        ProjectRunOptions options,
        IReadOnlyList<string> properties,
        CancellationToken cancellationToken)
    {
        var verbosity = ResolveBuildVerbosity(logger, options.Json);
        List<string> arguments = [project.FullName, "-nologo", "-nodeReuse:false", $"-verbosity:{verbosity}"];
        if (!options.NoRestore)
        {
            arguments.AddRange(["-restore", "-p:RestorePackagesConfig=true"]);
        }
        arguments.Add("-t:Build");
        arguments.AddRange(properties);

        var display = RedactSecretsForDisplay(WindowsCommandLine.JoinArguments([msbuild, .. arguments]) ?? string.Empty);
        var failures = new CppBuildFailureCollector();
        Action<string> writeLine;
        var interactive = !options.Json && logger.IsEnabled(LogLevel.Information);
        if (interactive)
        {
            ansiConsole.MarkupLineInterpolated($"{UiSymbols.Wrench} Building {project.Name} ({options.Configuration} | {ToCppPlatform(options.Architecture)})...");
            ansiConsole.MarkupLineInterpolated($"[dim]   {display}[/]");
            writeLine = CreateSynchronizedRedactedLineWriter();
        }
        else
        {
            // --json/--quiet keep stdout clean: the invocation (json only) and build output go to stderr.
            if (options.Json)
            {
                Console.Error.WriteLine(display);
            }
            writeLine = static line => Console.Error.WriteLine(NugetErrorMessage.Redact(line));
        }

        var stopwatch = Stopwatch.StartNew();
        var result = await msBuildService.RunAsync(
            msbuild,
            arguments,
            line =>
            {
                failures.Observe(line);
                writeLine(line);
            },
            cancellationToken);

        if (result.ExitCode != 0 && failures.Hint is { } hint)
        {
            throw new ProjectRunException($"Build failed for {project.Name}. {hint}");
        }

        if (result.ExitCode == 0 && interactive)
        {
            PrintBuildSucceeded(project, options, stopwatch.Elapsed);
        }

        return result.ExitCode;
    }

    /// <summary>
    /// Maps the evaluated C++ properties to the shared resolution: packaged when the project builds an
    /// MSIX layout (<c>AppxPackage</c> / <c>WindowsPackageType=MSIX</c>), otherwise unpackaged and launched
    /// from <c>TargetPath</c>.
    /// </summary>
    internal static ProjectRunResolution CreateCppResolution(
        FileInfo project,
        ProjectRunOptions options,
        IReadOnlyDictionary<string, string> props)
    {
        var configurationType = GetProp(props, "ConfigurationType");
        if (!string.Equals(configurationType, "Application", StringComparison.OrdinalIgnoreCase))
        {
            throw new ProjectRunException(
                $"'{project.Name}' is not runnable (ConfigurationType='{configurationType}'). winapp runs C++ projects that build an application (ConfigurationType=Application).");
        }

        var projectDirectory = project.DirectoryName ?? Directory.GetCurrentDirectory();
        var outDir = GetProp(props, "OutDir");
        if (string.IsNullOrEmpty(outDir))
        {
            throw new ProjectRunException(
                $"Could not resolve the build output directory (OutDir) for '{project.Name}'. Ensure the project builds successfully.");
        }
        var targetDir = Path.GetFullPath(outDir, projectDirectory);

        var windowsPackageType = GetProp(props, "WindowsPackageType");
        var packaged = !string.Equals(windowsPackageType, "None", StringComparison.OrdinalIgnoreCase)
            && (IsTrue(GetProp(props, "AppxPackage")) || string.Equals(windowsPackageType, "MSIX", StringComparison.OrdinalIgnoreCase));

        var targetPath = GetProp(props, "TargetPath");
        var executable = string.IsNullOrEmpty(targetPath) ? null : Path.GetFullPath(targetPath, projectDirectory);
        if (!packaged && (executable is null || !File.Exists(executable)))
        {
            var reason = options.NoBuild
                ? "Remove --no-build so the project is built first, or build it for this configuration and platform."
                : "The build did not produce it.";
            throw new ProjectRunException(
                $"'{project.Name}' resolves to an unpackaged app but its executable ({executable ?? "TargetPath"}) was not found. {reason}");
        }

        var outputType = executable is not null && File.Exists(executable)
            ? PeHelper.IsConsoleSubsystem(executable) switch { true => "Exe", false => "WinExe", null => null }
            : null;

        return new ProjectRunResolution(
            project,
            targetDir,
            packaged ? null : executable,
            packaged ? ProjectPackaging.Packaged : ProjectPackaging.Unpackaged,
            IsTrue(GetProp(props, "WindowsAppSDKSelfContained")),
            options.Architecture,
            NoRestore: options.NoRestore,
            OutputType: outputType,
            AppxManifestPath: packaged ? ResolveEvaluatedFileIfPresent(props, "FinalAppxManifestName", projectDirectory) : null,
            AppxRecipePath: packaged ? ResolveEvaluatedFileIfPresent(props, "AppxPackageRecipe", projectDirectory) : null,
            Configuration: GetProp(props, "Configuration") is { Length: > 0 } configuration ? configuration : options.Configuration,
            Platform: GetProp(props, "Platform") is { Length: > 0 } platform ? platform : ToCppPlatform(options.Architecture));
    }

    /// <summary>
    /// Rejects building a .NET project whose <c>ProjectReference</c> closure contains a C++ project: the
    /// .NET SDK can't load it (MSB4278), so say what works instead of letting the build fail.
    /// </summary>
    /// <param name="packaging">
    /// <c>winapp package</c> publishes with dotnet even under <c>--no-build</c>, and publish still loads
    /// project references, so its advice is to package the MSBuild output folder instead.
    /// </param>
    private static void ThrowIfReferencesCppProject(FileInfo project, bool packaging = false)
    {
        if (FindCppProjectReference(project) is { } cpp)
        {
            var next = packaging
                ? "then package its build output folder: winapp package <output folder>."
                : "then re-run this command with --no-build.";
            throw new ProjectRunException(
                $"'{project.Name}' references the C++ project '{cpp.Name}', which dotnet can't build. " +
                "Build it with Visual Studio or MSBuild.exe (Visual Studio or Build Tools 2022 17.8+ with the \"Desktop development with C++\" workload), " +
                next);
        }
    }

    /// <summary>
    /// Finds a <c>.vcxproj</c> in the transitive <c>ProjectReference</c> closure of <paramref name="start"/>,
    /// read statically. Unlike the platform walk, build-only references count: a native DLL is usually
    /// referenced with <c>ReferenceOutputAssembly=false</c> and must still be built. Conditional references
    /// are skipped — a common way to keep a native reference Visual Studio-only — so this never blocks a
    /// build dotnet would have completed.
    /// </summary>
    internal static FileInfo? FindCppProjectReference(FileInfo start)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start.FullName };
        var queue = new Queue<FileInfo>([start]);
        while (queue.Count > 0 && visited.Count <= MaxProjectReferenceClosure)
        {
            var current = queue.Dequeue();
            XDocument doc;
            try
            {
                doc = XDocument.Load(current.FullName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                continue;
            }

            var includes = doc.Descendants()
                .Where(e => e.Name.LocalName == "ProjectReference"
                    && string.IsNullOrWhiteSpace((string?)e.Attribute("Condition"))
                    && string.IsNullOrWhiteSpace((string?)e.Parent?.Attribute("Condition")))
                .SelectMany(e => (e.Attribute("Include")?.Value ?? string.Empty)
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            foreach (var include in includes)
            {
                if (!TryResolveReferencePath(current, include, out var reference))
                {
                    continue;
                }

                if (IsCppProject(reference))
                {
                    return reference;
                }

                if (visited.Add(reference.FullName))
                {
                    queue.Enqueue(reference);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Watches MSBuild output for the errors that mean a prerequisite is missing, so the failure can say
    /// what to install instead of leaving the user with a raw MSBuild code.
    /// </summary>
    internal sealed class CppBuildFailureCollector
    {
        private int _missingToolset;
        private int _missingWindowsSdk;

        public void Observe(string line)
        {
            if (line.Contains("error MSB8020", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Exchange(ref _missingToolset, 1);
            }
            else if (line.Contains("error MSB8036", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Exchange(ref _missingWindowsSdk, 1);
            }
        }

        public string? Hint
        {
            get
            {
                var hints = new List<string>();
                if (Volatile.Read(ref _missingToolset) == 1)
                {
                    hints.Add("The C++ build tools (platform toolset) this project targets are not installed (MSB8020). " +
                        "Add them in the Visual Studio Installer (WinUI 3 C++ apps need the \"C++ WinUI app development tools\"), " +
                        "or build with an installed toolset via -p:PlatformToolset=<version>.");
                }
                if (Volatile.Read(ref _missingWindowsSdk) == 1)
                {
                    hints.Add("The Windows SDK version this project targets is not installed (MSB8036). " +
                        "Install it with the Visual Studio Installer or winget (e.g. winget install Microsoft.WindowsSDK.10.0.26100), " +
                        "or build against an installed one with -p:WindowsTargetPlatformVersion=<version>.");
                }

                return hints.Count == 0 ? null : string.Join(" ", hints);
            }
        }
    }
}
