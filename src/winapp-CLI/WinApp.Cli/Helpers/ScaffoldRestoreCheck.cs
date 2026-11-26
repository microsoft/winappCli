// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.RegularExpressions;
using WinApp.Cli.Services;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Finds package-restore failures that <c>dotnet new</c> reports but does not fail on. The WinUI templates
/// write <c>Version="*"</c> placeholders, then run post actions that pin each package and restore. When
/// those post actions fail (for example, a NuGet feed is missing part of the newest Windows App SDK),
/// <c>dotnet new</c> still exits 0 and the project is left unbuildable.
/// </summary>
/// <remarks>
/// The primary signal is the <c>logs</c> array that restore writes into <c>obj\project.assets.json</c>:
/// its codes, levels and package ids don't depend on the console language. Output text ("Post action
/// failed") is only a fallback for failures that leave no assets file behind.
/// </remarks>
internal static partial class ScaffoldRestoreCheck
{
    internal sealed record RestoreError(string? Code, string? Package, string Message);

    internal sealed class Problem
    {
        public List<RestoreError> Errors { get; } = [];

        /// <summary>The project restore ran for, from the assets file. Null when only output text was available.</summary>
        public string? ProjectPath { get; set; }

        /// <summary>
        /// The Windows App SDK version restore tried, when a part of it (e.g. <c>Microsoft.WindowsAppSDK.AI</c>)
        /// couldn't be found. Null when the failure isn't a missing Windows App SDK package.
        /// </summary>
        public string? MissingWindowsAppSdkVersion { get; set; }

        /// <summary>True when a missing or unresolvable package is the Windows App SDK or one of its parts.</summary>
        public bool WindowsAppSdkPackageMissing { get; set; }

        /// <summary>Direct package references still at the template's <c>Version="*"</c> placeholder.</summary>
        public List<string> FloatingPackages { get; } = [];
    }

    // NuGet codes for "the package (or a version satisfying the range) is not on any configured source".
    private static readonly HashSet<string> PackageNotFoundCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "NU1101", "NU1102", "NU1103",
    };

    // Restore keeps the timestamp of an assets file it didn't rewrite, so a file older than the scaffold
    // (e.g. from a previous project in a --force target) says nothing about this run. Allow a little clock
    // and file-system timestamp slack.
    private static readonly TimeSpan TimestampSlack = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Returns the restore problem a project scaffold left behind, or null when restore succeeded (or there
    /// is no evidence either way).
    /// </summary>
    /// <param name="outputDir">The directory the project was scaffolded into.</param>
    /// <param name="output">Combined stdout and stderr of <c>dotnet new</c>.</param>
    /// <param name="scaffoldStartedUtc">When <c>dotnet new</c> started; older assets files are ignored.</param>
    public static Problem? Detect(DirectoryInfo outputDir, string output, DateTime scaffoldStartedUtc)
    {
        Problem? fromAssets = null;
        foreach (var assetsFile in FindAssetsFiles(outputDir, scaffoldStartedUtc))
        {
            var problem = ReadAssetsFile(assetsFile);
            if (problem is not null)
            {
                fromAssets = problem;
                break;
            }
        }

        if (fromAssets is not null)
        {
            return fromAssets;
        }

        return ReadOutput(output);
    }

    private static IEnumerable<FileInfo> FindAssetsFiles(DirectoryInfo outputDir, DateTime scaffoldStartedUtc)
    {
        IEnumerable<string> paths;
        try
        {
            paths = Directory.EnumerateFiles(outputDir.FullName, "project.assets.json", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = 3,
                IgnoreInaccessible = true,
            }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var path in paths)
        {
            var file = new FileInfo(path);
            if (file.LastWriteTimeUtc >= scaffoldStartedUtc - TimestampSlack)
            {
                yield return file;
            }
        }
    }

    internal static Problem? ReadAssetsFile(FileInfo assetsFile)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(assetsFile.FullName));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind is not JsonValueKind.Object
                || !root.TryGetProperty("logs", out var logs)
                || logs.ValueKind is not JsonValueKind.Array)
            {
                return null;
            }

            var problem = new Problem();
            foreach (var log in logs.EnumerateArray())
            {
                if (log.ValueKind is not JsonValueKind.Object
                    || !string.Equals(GetString(log, "level"), "Error", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var code = GetString(log, "code");
                var package = GetString(log, "libraryId");
                var message = GetString(log, "message") ?? code ?? "Unknown restore error";
                problem.Errors.Add(new RestoreError(code, package, message));

                if (code is not null && package is not null && PackageNotFoundCodes.Contains(code) && IsWindowsAppSdkPackage(package))
                {
                    problem.WindowsAppSdkPackageMissing = true;
                }
            }

            if (problem.Errors.Count == 0)
            {
                return null;
            }

            if (root.TryGetProperty("project", out var project) && project.ValueKind is JsonValueKind.Object)
            {
                if (project.TryGetProperty("restore", out var restore) && restore.ValueKind is JsonValueKind.Object)
                {
                    problem.ProjectPath = GetString(restore, "projectPath");
                }

                if (project.TryGetProperty("frameworks", out var frameworks) && frameworks.ValueKind is JsonValueKind.Object)
                {
                    foreach (var framework in frameworks.EnumerateObject())
                    {
                        if (framework.Value.ValueKind is not JsonValueKind.Object
                            || !framework.Value.TryGetProperty("dependencies", out var dependencies)
                            || dependencies.ValueKind is not JsonValueKind.Object)
                        {
                            continue;
                        }

                        foreach (var dependency in dependencies.EnumerateObject())
                        {
                            // A floating "*" reference is recorded as the range "[*, )".
                            var range = dependency.Value.ValueKind is JsonValueKind.Object ? GetString(dependency.Value, "version") : null;
                            if (range is not null && range.TrimStart('[', '(').StartsWith('*')
                                && !problem.FloatingPackages.Contains(dependency.Name, StringComparer.OrdinalIgnoreCase))
                            {
                                problem.FloatingPackages.Add(dependency.Name);
                            }
                        }
                    }
                }
            }

            if (problem.WindowsAppSdkPackageMissing
                && root.TryGetProperty("libraries", out var libraries) && libraries.ValueKind is JsonValueKind.Object)
            {
                // Restore still records the metapackage version it picked, even though a part it pins is missing.
                var prefix = BuildToolsService.WINAPP_SDK_PACKAGE + "/";
                problem.MissingWindowsAppSdkVersion = libraries.EnumerateObject()
                    .Select(l => l.Name)
                    .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .Select(n => n[prefix.Length..])
                    .FirstOrDefault();
            }

            return problem;
        }
    }

    /// <summary>
    /// Fallback for when restore left no assets file: <c>dotnet new</c> prints "Post action failed." for each
    /// failing step. English-only, since <c>dotnet new</c> localizes its output.
    /// </summary>
    internal static Problem? ReadOutput(string output)
    {
        var lines = output.Split('\n').Select(l => l.TrimEnd('\r').Trim()).ToList();
        if (!lines.Any(l => l.StartsWith("Post action failed", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var problem = new Problem();
        foreach (var line in lines)
        {
            var match = ErrorLineRegex().Match(line);
            if (match.Success)
            {
                var code = match.Groups["code"].Success ? match.Groups["code"].Value : null;
                var message = match.Groups["message"].Value.Trim();
                if (message.Length > 0 && !problem.Errors.Any(e => e.Message == message))
                {
                    problem.Errors.Add(new RestoreError(code, null, message));
                }
            }
        }

        return problem;
    }

    internal static bool IsWindowsAppSdkPackage(string packageId)
        => packageId.Equals(BuildToolsService.WINAPP_SDK_PACKAGE, StringComparison.OrdinalIgnoreCase)
            || packageId.StartsWith(BuildToolsService.WINAPP_SDK_PACKAGE + ".", StringComparison.OrdinalIgnoreCase);

    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.String ? value.GetString() : null;

    // "error: NU1102: Unable to find ..." (from 'dotnet add package') or
    // "C:\x\App.csproj : error NU1102: Unable to find ..." (from 'dotnet restore').
    [GeneratedRegex(@"^(?:.*?\s:\s)?error(?::|\s)\s*(?:(?<code>NU\d{4}):\s*)?(?<message>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorLineRegex();
}
