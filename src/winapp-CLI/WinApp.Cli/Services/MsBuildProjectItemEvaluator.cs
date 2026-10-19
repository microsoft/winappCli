// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Text.Json;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services;

internal sealed class MsBuildProjectItemEvaluator(
    IDotNetService dotNetService) : IMsBuildProjectItemEvaluator
{
    internal static readonly string[] ItemKinds =
    [
        "Compile",
        "EmbeddedResource",
        "Page",
        "ApplicationDefinition",
        "PRIResource",
        "Content",
        "None"
    ];

    internal const string ProjectAssetsFileProperty = "ProjectAssetsFile";

    public async Task<MsBuildProjectItemEvaluation> EvaluateAsync(
        FileInfo project,
        CancellationToken cancellationToken = default)
    {
        var arguments = new List<string>
        {
            "msbuild",
            project.FullName
        };
        arguments.AddRange(ItemKinds.Select(kind => $"--getItem:{kind}"));
        arguments.Add($"--getProperty:{ProjectAssetsFileProperty}");

        try
        {
            var (exitCode, output, error) =
                await dotNetService.RunDotnetCommandAsync(
                    project.Directory!,
                    arguments,
                    cancellationToken: cancellationToken);
            if (exitCode != 0)
            {
                return MsBuildProjectItemEvaluation.Failure(
                    string.IsNullOrWhiteSpace(error)
                        ? $"dotnet msbuild exited with code {exitCode}."
                        : error.Trim());
            }

            if (!MsBuildPropertyReader.TryParseItemFacts(
                    output,
                    out var groups))
            {
                return MsBuildProjectItemEvaluation.Failure(
                    "dotnet msbuild returned malformed item JSON.");
            }

            var items = new List<MsBuildProjectItem>();
            foreach (var kind in ItemKinds)
            {
                if (!groups.TryGetValue(kind, out var facts))
                {
                    return MsBuildProjectItemEvaluation.Failure(
                        $"dotnet msbuild omitted the requested {kind} item group.");
                }

                foreach (var fact in facts)
                {
                    if (!Path.IsPathFullyQualified(fact.FullPath)
                        || !Path.IsPathFullyQualified(
                            fact.DefiningProjectFullPath))
                    {
                        return MsBuildProjectItemEvaluation.Failure(
                            $"dotnet msbuild returned an unqualified path for {kind} item '{fact.Identity}'.");
                    }

                    items.Add(new MsBuildProjectItem(
                        kind,
                        fact.Identity,
                        fact.FullPath,
                        fact.DefiningProjectFullPath,
                        fact.Metadata));
                }
            }

            var properties = MsBuildPropertyReader.Parse(
                output,
                [ProjectAssetsFileProperty]);
            var restoredPackageDirectories = new List<string>();
            if (properties.TryGetValue(
                    ProjectAssetsFileProperty,
                    out var assetsFile)
                && !string.IsNullOrWhiteSpace(assetsFile))
            {
                if (!Path.IsPathFullyQualified(assetsFile))
                {
                    return MsBuildProjectItemEvaluation.Failure(
                        "dotnet msbuild returned an unqualified ProjectAssetsFile path.");
                }
                if (File.Exists(assetsFile)
                    && !TryReadRestoredPackageDirectories(
                        assetsFile,
                        out restoredPackageDirectories,
                        out var assetsError))
                {
                    return MsBuildProjectItemEvaluation.Failure(
                        $"The evaluated project assets file is invalid: {assetsError}");
                }
            }

            return MsBuildProjectItemEvaluation.Success(
                items,
                restoredPackageDirectories);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or Win32Exception)
        {
            return MsBuildProjectItemEvaluation.Failure(
                $"dotnet msbuild evaluation failed: {exception.Message}");
        }
    }

    private static bool TryReadRestoredPackageDirectories(
        string assetsFile,
        out List<string> packageDirectories,
        out string error)
    {
        packageDirectories = [];
        error = string.Empty;
        try
        {
            using var assets = JsonDocument.Parse(
                File.ReadAllBytes(assetsFile));
            if (!assets.RootElement.TryGetProperty(
                    "packageFolders",
                    out var packageFolders)
                || packageFolders.ValueKind != JsonValueKind.Object
                || !assets.RootElement.TryGetProperty(
                    "libraries",
                    out var libraries)
                || libraries.ValueKind != JsonValueKind.Object)
            {
                error =
                    "packageFolders or libraries is missing.";
                return false;
            }

            var roots = new List<string>();
            foreach (var folder in packageFolders.EnumerateObject())
            {
                if (!Path.IsPathFullyQualified(folder.Name))
                {
                    error =
                        $"package folder '{folder.Name}' is not fully qualified.";
                    return false;
                }
                roots.Add(Path.GetFullPath(folder.Name));
            }

            foreach (var library in libraries.EnumerateObject())
            {
                if (!library.Value.TryGetProperty(
                        "type",
                        out var type)
                    || type.ValueKind != JsonValueKind.String
                    || !string.Equals(
                        type.GetString(),
                        "package",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!library.Value.TryGetProperty(
                        "path",
                        out var path)
                    || path.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(path.GetString()))
                {
                    error =
                        $"package library '{library.Name}' has no path.";
                    return false;
                }

                var rawPath = path.GetString()!;
                var segments = rawPath.Split(
                    ['/', '\\'],
                    StringSplitOptions.RemoveEmptyEntries);
                if (Path.IsPathRooted(rawPath)
                    || segments.Length == 0
                    || segments.Any(segment =>
                        segment is "." or ".."
                        || segment.EndsWith('.')
                        || segment.EndsWith(' ')))
                {
                    error =
                        $"package library '{library.Name}' has an unsafe path.";
                    return false;
                }
                var relativePath = string.Join(
                    Path.DirectorySeparatorChar,
                    segments);
                foreach (var root in roots)
                {
                    var candidate = Path.GetFullPath(
                        Path.Combine(root, relativePath));
                    var relativeCandidate = Path.GetRelativePath(
                        root,
                        candidate);
                    if (Path.IsPathRooted(relativeCandidate)
                        || relativeCandidate == ".."
                        || relativeCandidate.StartsWith(
                            $"..{Path.DirectorySeparatorChar}",
                            StringComparison.Ordinal))
                    {
                        error =
                            $"package library '{library.Name}' escapes its package folder.";
                        return false;
                    }
                    if (Directory.Exists(candidate))
                    {
                        packageDirectories.Add(candidate);
                    }
                }
            }

            packageDirectories = packageDirectories
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return true;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            error = exception.Message;
            return false;
        }
    }
}
