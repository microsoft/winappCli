// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <inheritdoc cref="ILegacyUwpRunService" />
internal sealed class LegacyUwpRunService(
    IProcessRunner processRunner,
    IPackageRegistrationService packageRegistrationService,
    IAnsiConsole ansiConsole,
    ILogger<LegacyUwpRunService> logger) : ILegacyUwpRunService
{
    private const string UwpProjectTypeGuid = "{BC8A1FFA-BEE3-4634-8014-F334798102B3}";

    internal Func<FileInfo?> LocateVisualStudioMsBuild { get; set; } = FindVisualStudioMsBuild;
    internal Func<IReadOnlyList<Version>> LocateWindowsSdkVersions { get; set; } = FindInstalledWindowsSdkVersions;

    /// <inheritdoc />
    public bool IsLegacyUwpProject(FileInfo csproj)
    {
        try
        {
            var doc = XDocument.Load(csproj.FullName);
            var values = doc.Descendants()
                .Where(element => element.Name.LocalName is "TargetPlatformIdentifier" or "OutputType" or "ProjectTypeGuids")
                .Select(element => (element.Name.LocalName, Value: element.Value.Trim()))
                .ToList();

            return values.Any(value =>
                       value.LocalName == "TargetPlatformIdentifier" &&
                       string.Equals(value.Value, "UAP", StringComparison.OrdinalIgnoreCase))
                   || values.Any(value =>
                       value.LocalName == "ProjectTypeGuids" &&
                       value.Value.Contains(UwpProjectTypeGuid, StringComparison.OrdinalIgnoreCase))
                   || values.Any(value =>
                       value.LocalName == "OutputType" &&
                       string.Equals(value.Value, "AppContainerExe", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            logger.LogDebug(ex, "Could not inspect {Project} for classic UWP markers.", csproj.FullName);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<LegacyUwpBuildOutcome> BuildAndPrepareAsync(
        FileInfo csproj,
        LegacyUwpRunOptions options,
        CancellationToken cancellationToken)
    {
        var projectProperties = ReadProjectProperties(csproj);
        Version? targetSdk = null;

        if (!options.NoBuild)
        {
            targetSdk = SelectTargetSdk(
                projectProperties.GetValueOrDefault("TargetPlatformVersion"),
                projectProperties.GetValueOrDefault("TargetPlatformMinVersion"),
                LocateWindowsSdkVersions());
            var msbuild = LocateVisualStudioMsBuild()
                ?? throw new ProjectRunException(
                    "Classic UWP projects require Visual Studio MSBuild with the UWP build tools. " +
                    "Install the Universal Windows Platform development workload in Visual Studio.");

            var arguments = BuildMsBuildArguments(csproj, options, targetSdk);
            var displayArguments = ProjectRunService.RedactSecretsForDisplay(
                string.Join(" ", arguments.Select(QuoteForDisplay)));

            Action<string> writeLine;
            if (options.Json || !logger.IsEnabled(LogLevel.Information))
            {
                if (options.Json)
                {
                    Console.Error.WriteLine($"{msbuild.FullName} {displayArguments}");
                }
                writeLine = static line => Console.Error.WriteLine(line);
            }
            else
            {
                ansiConsole.MarkupLineInterpolated(
                    $"{UiSymbols.Wrench} Building {csproj.Name} ({options.Configuration} | {options.Architecture}) with Visual Studio MSBuild...");
                ansiConsole.MarkupLineInterpolated(
                    $"[dim]   {Markup.Escape(msbuild.FullName)} {Markup.Escape(displayArguments)}[/]");
                var writeLock = new object();
                writeLine = line =>
                {
                    lock (writeLock)
                    {
                        ansiConsole.WriteLine(line);
                    }
                };
            }

            ProcessRunResult result;
            try
            {
                result = await processRunner.RunAsync(
                    new ProcessRunRequest(msbuild.FullName, arguments),
                    writeLine,
                    writeLine,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ProjectRunException(
                    $"Failed to start Visual Studio MSBuild at '{msbuild.FullName}': {ex.Message}");
            }

            if (result.ExitCode != 0)
            {
                logger.LogError(
                    "{UISymbol} Classic UWP build failed for {Project} (exit code {ExitCode}).",
                    UiSymbols.Error,
                    csproj.Name,
                    result.ExitCode);
                return new LegacyUwpBuildOutcome(null, result.ExitCode, targetSdk.ToString());
            }
        }

        var layout = ResolveLayoutDirectory(csproj, options.Configuration, options.Architecture)
            ?? throw new ProjectRunException(
                $"The classic UWP build completed but no loose AppX layout containing AppxManifest.xml was found under " +
                $"'{Path.Combine(csproj.DirectoryName!, "bin")}'. Build the requested {options.Configuration} | {options.Architecture} configuration, or remove --no-build.");

        await EnsureFrameworkDependenciesAsync(layout, csproj, options.Architecture, cancellationToken);
        return new LegacyUwpBuildOutcome(
            layout,
            0,
            targetSdk?.ToString() ?? projectProperties.GetValueOrDefault("TargetPlatformVersion"));
    }

    internal static IReadOnlyList<string> BuildMsBuildArguments(
        FileInfo csproj,
        LegacyUwpRunOptions options,
        Version targetSdk)
    {
        var arguments = new List<string>
        {
            csproj.FullName,
            "-m",
            "-verbosity:minimal",
        };

        if (!options.NoRestore)
        {
            arguments.Add("-restore");
        }

        foreach (var property in options.Properties)
        {
            var name = property[..property.IndexOf('=')];
            if (name.Equals("Configuration", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Platform", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("TargetPlatformVersion", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("AppxPackageSigningEnabled", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("GenerateAppxPackageOnBuild", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            arguments.Add($"-property:{property}");
        }

        arguments.Add($"-property:Configuration={options.Configuration}");
        arguments.Add($"-property:Platform={options.Architecture}");
        arguments.Add($"-property:TargetPlatformVersion={targetSdk}");
        arguments.Add("-property:AppxPackageSigningEnabled=false");
        arguments.Add("-property:GenerateAppxPackageOnBuild=false");
        return arguments;
    }

    internal static Version SelectTargetSdk(
        string? requestedVersion,
        string? minimumVersion,
        IReadOnlyList<Version> installedVersions)
    {
        if (installedVersions.Count == 0)
        {
            throw new ProjectRunException(
                "No installed Universal Windows Platform SDK was found. Install a Windows SDK and the Visual Studio UWP workload.");
        }

        var ordered = installedVersions.Distinct().OrderDescending().ToList();
        var requested = TryParseVersion(requestedVersion);
        if (requested is not null && ordered.Contains(requested))
        {
            return requested;
        }

        var minimum = TryParseVersion(minimumVersion);
        var compatible = ordered.FirstOrDefault(version => minimum is null || version >= minimum);
        if (compatible is null)
        {
            throw new ProjectRunException(
                $"The project requires Windows SDK {minimumVersion}, but the newest installed UWP SDK is {ordered[0]}.");
        }

        return compatible;
    }

    internal static DirectoryInfo? ResolveLayoutDirectory(
        FileInfo csproj,
        string configuration,
        string architecture)
    {
        var bin = new DirectoryInfo(Path.Combine(csproj.DirectoryName!, "bin"));
        if (!bin.Exists)
        {
            return null;
        }

        return bin.EnumerateFiles("AppxManifest.xml", SearchOption.AllDirectories)
            .Where(file => !file.FullName.Split(Path.DirectorySeparatorChar)
                .Any(segment => string.Equals(segment, "AppX", StringComparison.OrdinalIgnoreCase)))
            .Select(file => new
            {
                Directory = file.Directory!,
                Score = ScoreLayout(file.Directory!, configuration, architecture),
                file.LastWriteTimeUtc,
            })
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.LastWriteTimeUtc)
            .Select(candidate => candidate.Directory)
            .FirstOrDefault();
    }

    private async Task EnsureFrameworkDependenciesAsync(
        DirectoryInfo layout,
        FileInfo csproj,
        string architecture,
        CancellationToken cancellationToken)
    {
        var manifest = new FileInfo(Path.Combine(layout.FullName, "AppxManifest.xml"));
        var dependencies = ReadManifestDependencies(manifest);
        if (dependencies.Count == 0)
        {
            return;
        }

        var assets = new FileInfo(Path.Combine(csproj.DirectoryName!, "obj", "project.assets.json"));
        foreach (var dependency in dependencies)
        {
            var installed = packageRegistrationService.GetInstalledVersion(dependency.Name, architecture)
                ?? packageRegistrationService.GetInstalledVersion(dependency.Name, "neutral");
            if (VersionAtLeast(installed, dependency.MinimumVersion))
            {
                continue;
            }

            var package = FindRestoredFrameworkPackage(assets, dependency, architecture);
            if (package is null)
            {
                throw new ProjectRunException(
                    $"UWP framework dependency '{dependency.Name}' {dependency.MinimumVersion} for {architecture} is not installed, " +
                    $"and no matching .appx was found in the restored packages from '{assets.FullName}'. Restore the project or install the dependency manually.");
            }

            try
            {
                await packageRegistrationService.InstallPackageAsync(package.FullName, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ProjectRunException(
                    $"Failed to install UWP framework dependency '{dependency.Name}' from '{package.FullName}': {ex.Message}");
            }
        }
    }

    private static List<FrameworkDependency> ReadManifestDependencies(FileInfo manifest)
    {
        var doc = XDocument.Load(manifest.FullName);
        return doc.Descendants()
            .Where(element => element.Name.LocalName == "PackageDependency")
            .Select(element => new FrameworkDependency(
                element.Attribute("Name")?.Value ?? string.Empty,
                element.Attribute("MinVersion")?.Value ?? "0.0.0.0"))
            .Where(dependency => dependency.Name.Length > 0)
            .ToList();
    }

    private static FileInfo? FindRestoredFrameworkPackage(
        FileInfo assetsFile,
        FrameworkDependency dependency,
        string architecture)
    {
        if (!assetsFile.Exists)
        {
            return null;
        }

        using var assets = JsonDocument.Parse(File.ReadAllBytes(assetsFile.FullName));
        if (!assets.RootElement.TryGetProperty("packageFolders", out var packageFolders) ||
            !assets.RootElement.TryGetProperty("libraries", out var libraries))
        {
            return null;
        }

        var roots = packageFolders.EnumerateObject().Select(property => property.Name).ToList();
        var libraryPaths = libraries.EnumerateObject()
            .Select(property => property.Value.TryGetProperty("path", out var path) ? path.GetString() : null)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToList();

        var matches = new List<(FileInfo File, Version Version, bool ExactArchitecture)>();
        foreach (var root in roots)
        {
            foreach (var libraryPath in libraryPaths)
            {
                var directory = Path.Combine(root, libraryPath.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                foreach (var appxPath in Directory.EnumerateFiles(directory, "*.appx", SearchOption.AllDirectories))
                {
                    if (TryReadPackageIdentity(appxPath, out var identity) &&
                        string.Equals(identity.Name, dependency.Name, StringComparison.OrdinalIgnoreCase) &&
                        VersionAtLeast(identity.Version.ToString(), dependency.MinimumVersion) &&
                        (string.Equals(identity.Architecture, architecture, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(identity.Architecture, "neutral", StringComparison.OrdinalIgnoreCase)))
                    {
                        matches.Add((
                            new FileInfo(appxPath),
                            identity.Version,
                            string.Equals(identity.Architecture, architecture, StringComparison.OrdinalIgnoreCase)));
                    }
                }
            }
        }

        return matches
            .OrderByDescending(match => match.ExactArchitecture)
            .ThenByDescending(match => match.Version)
            .Select(match => match.File)
            .FirstOrDefault();
    }

    private static bool TryReadPackageIdentity(string appxPath, out PackageIdentity identity)
    {
        identity = default;
        try
        {
            using var archive = ZipFile.OpenRead(appxPath);
            var manifestEntry = archive.Entries.FirstOrDefault(entry =>
                string.Equals(entry.FullName, "AppxManifest.xml", StringComparison.OrdinalIgnoreCase));
            if (manifestEntry is null)
            {
                return false;
            }

            using var stream = manifestEntry.Open();
            var doc = XDocument.Load(stream);
            var element = doc.Descendants().FirstOrDefault(item => item.Name.LocalName == "Identity");
            var name = element?.Attribute("Name")?.Value;
            var version = TryParseVersion(element?.Attribute("Version")?.Value);
            var packageArchitecture = element?.Attribute("ProcessorArchitecture")?.Value ?? "neutral";
            if (string.IsNullOrWhiteSpace(name) || version is null)
            {
                return false;
            }

            identity = new PackageIdentity(name, version, packageArchitecture);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Xml.XmlException)
        {
            return false;
        }
    }

    private static Dictionary<string, string> ReadProjectProperties(FileInfo csproj)
    {
        var doc = XDocument.Load(csproj.FullName);
        return doc.Descendants()
            .Where(element => element.Name.LocalName is "TargetPlatformVersion" or "TargetPlatformMinVersion")
            .GroupBy(element => element.Name.LocalName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Value.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    private static FileInfo? FindVisualStudioMsBuild()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        }
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Select(path => Path.Combine(path, "Microsoft Visual Studio"))
        .Where(Directory.Exists)
        .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var visualStudioRoot in roots)
        {
            foreach (var versionDirectory in Directory.EnumerateDirectories(visualStudioRoot)
                         .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var editionDirectory in Directory.EnumerateDirectories(versionDirectory)
                             .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    foreach (var relativePath in new[]
                             {
                                 Path.Combine("MSBuild", "Current", "Bin", "MSBuild.exe"),
                                 Path.Combine("MSBuild", "15.0", "Bin", "MSBuild.exe"),
                             })
                    {
                        var candidate = new FileInfo(Path.Combine(editionDirectory, relativePath));
                        if (candidate.Exists)
                        {
                            return candidate;
                        }
                    }
                }
            }
        }

        return null;
    }

    private static IReadOnlyList<Version> FindInstalledWindowsSdkVersions()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows Kits\Installed Roots");
            if (key?.GetValue("KitsRoot10") is string registryRoot)
            {
                roots.Add(registryRoot);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // The standard installation path below remains available.
        }

        roots.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Windows Kits",
            "10"));

        var versions = new List<Version>();
        foreach (var root in roots)
        {
            var platforms = Path.Combine(root, "Platforms", "UAP");
            if (!Directory.Exists(platforms))
            {
                continue;
            }

            foreach (var directory in Directory.EnumerateDirectories(platforms))
            {
                if (Version.TryParse(Path.GetFileName(directory), out var version))
                {
                    versions.Add(version);
                }
            }
        }

        return versions;
    }

    private static int ScoreLayout(DirectoryInfo directory, string configuration, string architecture)
    {
        var segments = directory.FullName.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var score = segments.Count(segment => string.Equals(segment, configuration, StringComparison.OrdinalIgnoreCase)) * 2;
        score += segments.Count(segment => string.Equals(segment, architecture, StringComparison.OrdinalIgnoreCase)) * 2;
        score += directory.EnumerateFiles("*.build.appxrecipe", SearchOption.TopDirectoryOnly).Any() ? 1 : 0;
        return score;
    }

    private static Version? TryParseVersion(string? value) =>
        Version.TryParse(value, out var version) ? version : null;

    private static bool VersionAtLeast(string? actual, string minimum)
    {
        var actualVersion = TryParseVersion(actual);
        var minimumVersion = TryParseVersion(minimum);
        return actualVersion is not null && (minimumVersion is null || actualVersion >= minimumVersion);
    }

    private static string QuoteForDisplay(string value) =>
        value.Any(char.IsWhiteSpace) ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"" : value;

    private sealed record FrameworkDependency(string Name, string MinimumVersion);
    private readonly record struct PackageIdentity(string Name, Version Version, string Architecture);
}
