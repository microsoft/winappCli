// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

internal sealed partial class ProjectRunService
{
    private const string NativeAotPrerequisites = "https://aka.ms/nativeaot-prerequisites";

    public async Task<ProjectBuildOutcome> PublishAotAndResolveAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        CancellationToken cancellationToken)
    {
        if (options.NoBuild)
        {
            throw new ProjectRunException("--aot cannot be combined with --no-build.");
        }
        if (options.Architecture is not ("x64" or "arm64"))
        {
            throw new ProjectRunException("--aot supports only x64 and ARM64 Windows targets.");
        }

        WarnOnOverriddenFlags(options);
        var workingDirectory = csproj.Directory
            ?? new DirectoryInfo(Directory.GetCurrentDirectory());
        (options, _, var csWinRTMetadata) =
            await PrepareBuildInputsAsync(
                csproj,
                options,
                workingDirectory,
                cancellationToken,
                aotPublish: true);

        // A build-context pre-restore does not cover publish-conditional dependencies.
        var publish = await RunAotPublishPassAsync(
            csproj,
            options,
            workingDirectory,
            csWinRTMetadata,
            cancellationToken);
        if (publish.ExitCode != 0)
        {
            if (IsMissingVsWhereFailure(publish.Output, publish.Error))
            {
                logger.LogError(
                    "{UISymbol} Install the Windows Native AOT prerequisites: {Url}",
                    UiSymbols.Error,
                    NativeAotPrerequisites);
            }
            return new ProjectBuildOutcome(null, publish.ExitCode);
        }

        var properties = MsBuildPropertyReader.Parse(
            publish.Output,
            RequestedProperties);
        if (!IsTrue(GetProp(properties, "PublishAot")))
        {
            throw new ProjectRunException(BuildPublishAotRequiredMessage(csproj));
        }

        var resolution = CreateAotResolution(csproj, options, properties);
        logger.LogDebug(
            "{UISymbol} Native AOT output: PublishDir={PublishDir}; executable={Executable}; manifest={Manifest}; recipe={Recipe}",
            UiSymbols.Note,
            resolution.TargetDir,
            resolution.RunCommand,
            resolution.AppxManifestPath ?? "(unpackaged)",
            resolution.AppxRecipePath ?? "(unpackaged)");
        if (!options.Json && logger.IsEnabled(LogLevel.Information))
        {
            ansiConsole.MarkupLineInterpolated(
                $"{UiSymbols.Check} Native AOT output: {resolution.RunCommand}");
        }

        return new ProjectBuildOutcome(resolution, 0);
    }

    private async Task<(int ExitCode, string Output, string Error)> RunAotPublishPassAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        DirectoryInfo workingDirectory,
        string? csWinRTMetadata,
        CancellationToken cancellationToken)
    {
        var arguments = BuildAotPublishArguments(
            csproj,
            options,
            ResolveBuildVerbosity(logger, options.Json),
            csWinRTMetadata);
        var display = RedactSecretsForDisplay(
            WindowsCommandLine.JoinArguments(arguments) ?? string.Empty);
        logger.LogDebug("{UISymbol} dotnet {Arguments}", UiSymbols.Note, display);

        Action<string> writeLine;
        if (options.Json || !logger.IsEnabled(LogLevel.Information))
        {
            writeLine = static line => Console.Error.WriteLine(NugetErrorMessage.Redact(line));
        }
        else
        {
            ansiConsole.MarkupLineInterpolated($"{UiSymbols.Wrench} Publishing Native AOT...");
            writeLine = CreateSynchronizedRedactedLineWriter();
        }

        var propertyJsonStarted = false;
        string? pendingOpeningBrace = null;
        var outputLock = new object();
        void WritePublishOutput(string line)
        {
            lock (outputLock)
            {
                if (propertyJsonStarted)
                {
                    return;
                }
                var trimmed = line.Trim();
                if (pendingOpeningBrace is not null)
                {
                    if (trimmed.StartsWith("\"Properties\":", StringComparison.Ordinal))
                    {
                        pendingOpeningBrace = null;
                        propertyJsonStarted = true;
                        return;
                    }
                    writeLine(pendingOpeningBrace);
                    pendingOpeningBrace = null;
                }
                // MSBuild ends stdout with its Properties envelope. A project message beginning
                // with '{' is not enough evidence to hide it or any diagnostics that follow.
                if (trimmed == "{")
                {
                    pendingOpeningBrace = line;
                }
                else if (trimmed.StartsWith('{') &&
                    trimmed[1..].TrimStart().StartsWith("\"Properties\":", StringComparison.Ordinal))
                {
                    propertyJsonStarted = true;
                }
                else
                {
                    writeLine(line);
                }
            }
        }

        try
        {
            return await dotNetService.RunDotnetCommandAsync(
                workingDirectory,
                arguments,
                BuildAotPublishEnvironment(),
                WritePublishOutput,
                writeLine,
                cancellationToken);
        }
        finally
        {
            lock (outputLock)
            {
                if (pendingOpeningBrace is not null)
                {
                    writeLine(pendingOpeningBrace);
                }
            }
        }
    }

    internal static IReadOnlyDictionary<string, string>? BuildAotPublishEnvironment(
        string? inheritedPath = null,
        string? installerDirectory = null)
    {
        inheritedPath ??= Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        installerDirectory ??= Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio",
            "Installer");
        var vsWhere = Path.Join(installerDirectory, "vswhere.exe");
        if (!File.Exists(vsWhere) || PathContainsDirectory(inheritedPath, installerDirectory))
        {
            return null;
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = string.IsNullOrWhiteSpace(inheritedPath)
                ? installerDirectory
                : $"{installerDirectory}{Path.PathSeparator}{inheritedPath}",
        };
    }

    private static bool PathContainsDirectory(string path, string directory)
    {
        string expected;
        try
        {
            expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        foreach (var segment in path.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                if (string.Equals(
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(segment.Trim('"'))),
                        expected,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Ignore malformed inherited PATH entries.
            }
        }
        return false;
    }

    private static bool IsMissingVsWhereFailure(string stdout, string stderr)
    {
        var diagnostics = $"{stdout}{Environment.NewLine}{stderr}";
        return diagnostics.Contains("vswhere.exe", StringComparison.OrdinalIgnoreCase)
            && diagnostics.Contains("findvcvarsall", StringComparison.OrdinalIgnoreCase)
            && diagnostics.Contains("123", StringComparison.Ordinal);
    }

    private ProjectRunResolution CreateAotResolution(
        FileInfo csproj,
        ProjectRunOptions options,
        IReadOnlyDictionary<string, string> properties)
    {
        var outputType = GetProp(properties, "OutputType");
        if (!string.IsNullOrWhiteSpace(outputType) &&
            !ProjectDetectionService.IsExecutableOutputType(outputType))
        {
            throw new ProjectRunException(
                $"'{csproj.Name}' is not runnable (OutputType='{outputType}').");
        }

        var projectDirectoryValue = GetProp(properties, "MSBuildProjectDirectory");
        if (string.IsNullOrWhiteSpace(projectDirectoryValue) ||
            !Path.IsPathFullyQualified(projectDirectoryValue))
        {
            throw new ProjectRunException(
                $"Could not resolve MSBuildProjectDirectory for '{csproj.Name}'.");
        }

        var projectDirectory = Path.GetFullPath(projectDirectoryValue);
        var publishDirectory = ResolveEvaluatedPath(
            properties,
            "PublishDir",
            projectDirectory,
            required: true)!;
        if (!Directory.Exists(publishDirectory))
        {
            throw new ProjectRunException(
                $"The evaluated PublishDir does not exist: '{publishDirectory}'.");
        }

        var targetName = GetProp(properties, "TargetName");
        if (string.IsNullOrWhiteSpace(targetName) ||
            !string.Equals(Path.GetFileName(targetName), targetName, StringComparison.Ordinal))
        {
            throw new ProjectRunException(
                $"Could not resolve a valid TargetName for '{csproj.Name}'.");
        }

        string executable;
        try
        {
            if (targetName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new ArgumentException("TargetName contains invalid file-name characters.");
            }
            executable = Path.GetFullPath($"{targetName}.exe", publishDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ProjectRunException(
                $"The evaluated TargetName '{targetName}' is not a valid executable name.");
        }
        if (!File.Exists(executable))
        {
            throw new ProjectRunException(
                $"Native executable '{executable}' was not produced.");
        }

        var targetDirectory = ResolveEvaluatedPath(
            properties,
            "TargetDir",
            projectDirectory,
            required: false) ?? publishDirectory;
        var packaging = DeterminePackaging(properties, targetDirectory);
        string? manifest = null;
        string? recipe = null;
        if (packaging == ProjectPackaging.Packaged)
        {
            if (IsTrue(GetProp(properties, "EnableMsixTooling")) ||
                !string.IsNullOrWhiteSpace(GetProp(properties, "FinalAppxManifestName")) ||
                !string.IsNullOrWhiteSpace(GetProp(properties, "AppxPackageRecipe")))
            {
                manifest = ResolveEvaluatedFile(
                    properties,
                    "FinalAppxManifestName",
                    projectDirectory);
                recipe = ResolveEvaluatedFile(
                    properties,
                    "AppxPackageRecipe",
                    projectDirectory);
                var nativeBinary = ResolveEvaluatedFile(
                    properties,
                    "NativeBinary",
                    projectDirectory);
                ValidatePackagedNativeEntryPoint(manifest, recipe, nativeBinary);
            }
            else
            {
                var publishedManifest = ManifestHelper.FindManifest(publishDirectory);
                if (!publishedManifest.Exists)
                {
                    throw new ProjectRunException(
                        $"The Native AOT publish did not produce a package manifest in '{publishDirectory}'. " +
                        "Include Package.appxmanifest or appxmanifest.xml in the project's publish output.");
                }
                manifest = publishedManifest.FullName;
            }
        }

        return new ProjectRunResolution(
            csproj,
            publishDirectory,
            executable,
            packaging,
            IsTrue(GetProp(properties, "WindowsAppSDKSelfContained")),
            options.Architecture,
            options.Framework,
            options.NoRestore,
            RunArguments: null,
            OutputType: string.IsNullOrWhiteSpace(outputType) ? null : outputType,
            PreferExecutionAlias: ReadAliasPreference(properties),
            ProjectAssetsFile: ResolveEvaluatedPath(properties, "ProjectAssetsFile", projectDirectory, required: false),
            ProjectAssetsRuntimeIdentifier: GetProp(properties, "RuntimeIdentifier") is { Length: > 0 } rid ? rid : null,
            IsAot: true,
            AppxManifestPath: manifest,
            AppxRecipePath: recipe);
    }

    private static void ValidatePackagedNativeEntryPoint(
        string manifestPath,
        string recipePath,
        string nativeBinaryPath)
    {
        string executable;
        XDocument recipe;
        try
        {
            executable = AppxManifestDocument.Load(manifestPath).ApplicationExecutable
                ?? throw new ProjectRunException(
                    $"The generated manifest '{manifestPath}' has no application executable.");
            recipe = XDocument.Load(recipePath);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new ProjectRunException(
                $"The generated packaged AOT metadata could not be parsed: {ex.Message}");
        }

        XNamespace msbuild = "http://schemas.microsoft.com/developer/msbuild/2003";
        var entryPoint = NormalizePackagePath(executable);
        var sources = recipe
            .Descendants(msbuild + "AppxPackagedFile")
            .Where(entry => string.Equals(
                NormalizePackagePath(entry.Element(msbuild + "PackagePath")?.Value),
                entryPoint,
                StringComparison.OrdinalIgnoreCase))
            .Select(entry => ResolveRecipeSource(
                recipePath,
                entry.Attribute("Include")?.Value))
            .ToArray();

        if (sources.Length == 0)
        {
            throw new ProjectRunException(
                $"The appx recipe does not map the packaged entry point '{executable}'.");
        }

        var expected = Path.GetFullPath(nativeBinaryPath);
        if (!sources.Any(source => string.Equals(
                Path.GetFullPath(source),
                expected,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new ProjectRunException(
                $"The packaged entry point '{executable}' is not sourced from the evaluated NativeBinary '{expected}'.");
        }
    }

    private static string NormalizePackagePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            var root = Path.GetFullPath(".winapp-package-root", Path.GetTempPath());
            var fullPath = Path.GetFullPath(
                path.Replace('/', Path.DirectorySeparatorChar),
                root);
            var relative = Path.GetRelativePath(root, fullPath);
            if (relative == ".." ||
                relative.StartsWith(
                    $"..{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal) ||
                Path.IsPathFullyQualified(relative))
            {
                throw new ArgumentException("Package path escapes its root.");
            }
            return relative;
        }
        catch (Exception ex) when (
            ex is ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            throw new ProjectRunException(
                $"The generated appx recipe contains an invalid PackagePath '{path}'.");
        }
    }

    private static string ResolveRecipeSource(
        string recipePath,
        string? include)
    {
        if (string.IsNullOrWhiteSpace(include))
        {
            throw new ProjectRunException(
                $"The generated appx recipe '{recipePath}' contains an entry without Include.");
        }

        try
        {
            return Path.GetFullPath(
                include,
                Path.GetDirectoryName(recipePath)
                    ?? throw new ArgumentException("Recipe has no parent directory."));
        }
        catch (Exception ex) when (
            ex is ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            throw new ProjectRunException(
                $"The generated appx recipe contains an invalid source path '{include}'.");
        }
    }

    private static string? ResolveEvaluatedPath(
        IReadOnlyDictionary<string, string> properties,
        string name,
        string projectDirectory,
        bool required)
    {
        var value = GetProp(properties, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            if (required)
            {
                throw new ProjectRunException(
                    $"Could not resolve {name} from the Native AOT publish.");
            }
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(value, projectDirectory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ProjectRunException(
                $"The evaluated {name} path is invalid: '{value}'.");
        }
    }

    private static string ResolveEvaluatedFile(
        IReadOnlyDictionary<string, string> properties,
        string name,
        string projectDirectory)
    {
        var path = ResolveEvaluatedPath(properties, name, projectDirectory, required: true)!;
        if (!File.Exists(path))
        {
            throw new ProjectRunException(
                $"The evaluated {name} file was not produced: '{path}'.");
        }
        return path;
    }

    private static string BuildPublishAotRequiredMessage(FileInfo csproj)
    {
        var retry = WindowsCommandLine.JoinArguments(
            ["winapp", "run", csproj.FullName, "--aot", "-p", "PublishAot=true"]);
        return $"Native AOT is not enabled for '{csproj.Name}'. Add <PublishAot>true</PublishAot> inside a <PropertyGroup> in the project, or retry with: {retry}";
    }

}
