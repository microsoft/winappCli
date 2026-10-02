// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Tools;

namespace WinApp.Cli.Services;

internal partial class PriService
{
    private const string IdentityReindexConfig = """
        <?xml version="1.0" encoding="utf-8"?>
        <resources targetOsVersion="10.0.0" majorVersion="1">
          <index root="\" startIndexAt="original.pri">
            <indexer-config type="PRI" />
          </index>
        </resources>
        """;

    public async Task ReindexIdentityAsync(
        DirectoryInfo layout,
        string originalPackageName,
        string effectivePackageName,
        TaskContext taskContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ValidatePriIdentityName(originalPackageName);
        ValidatePriIdentityName(effectivePackageName);
        cancellationToken.ThrowIfCancellationRequested();

        var sourcePath = Path.Join(layout.FullName, "resources.pri");
        EnsureLocalPriPath(sourcePath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Identity reindexing requires an existing resources.pri.", sourcePath);
        }

        // A sibling is outside the payload but on the same volume for atomic replacement.
        var parent = layout.Parent ?? throw new InvalidOperationException("The PRI layout cannot be a volume root.");
        var work = new DirectoryInfo(Path.Join(parent.FullName, $".winapp-pri-identity-{Guid.NewGuid():N}"));
        EnsureLocalPriPath(work.FullName);
        if (work.Exists)
        {
            throw new IOException($"PRI working directory already exists: {work.FullName}");
        }
        work.Create();

        try
        {
            var input = work.CreateSubdirectory("input");
            var output = work.CreateSubdirectory("output");
            var originalPath = Path.Join(input.FullName, "original.pri");
            var resultPath = Path.Join(output.FullName, "resources.pri");
            var configPath = Path.Join(work.FullName, "reindex.xml");
            var originalDump = Path.Join(work.FullName, "original.xml");
            var resultDump = Path.Join(work.FullName, "result.xml");

            await using (var source = File.OpenRead(sourcePath))
            await using (var copy = new FileStream(originalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await source.CopyToAsync(copy, cancellationToken);
            }
            var originalHash = await HashPriFileAsync(originalPath, cancellationToken);
            await File.WriteAllTextAsync(configPath, IdentityReindexConfig, cancellationToken);

            await DumpIdentityPriAsync(originalPath, originalDump, taskContext, cancellationToken);
            var unsupportedAuthority = string.Equals(originalPackageName, effectivePackageName, StringComparison.OrdinalIgnoreCase)
                ? null : originalPackageName;
            var before = await PriIdentityValidation.ReadAsync(
                originalDump, layout, originalPackageName, unsupportedAuthority, cancellationToken);

            var arguments = $@"new /pr ""{PriToolPath(input.FullName)}"" /cf ""{PriToolPath(configPath)}"" /in ""{effectivePackageName}"" /of ""{PriToolPath(resultPath)}"" /il ""{PriToolPath(Path.Join(work.FullName, "indexlog.xml"))}"" /o";
            await buildToolsService.RunBuildToolAsync(new MakePriTool(), arguments, taskContext, cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            EnsureLocalPriPath(resultPath);
            var outputEntries = output.GetFileSystemInfos();
            if (outputEntries.Length != 1 || outputEntries[0] is not FileInfo result
                || !string.Equals(result.FullName, resultPath, StringComparison.OrdinalIgnoreCase)
                || result.Length == 0)
            {
                throw new InvalidDataException("MakePri did not produce exactly one nonempty resources.pri; split resource output is unsupported.");
            }

            await DumpIdentityPriAsync(resultPath, resultDump, taskContext, cancellationToken);
            var after = await PriIdentityValidation.ReadAsync(
                resultDump, layout, effectivePackageName, unsupportedAuthority, cancellationToken);
            PriIdentityValidation.EnsureEquivalent(before, after);

            EnsureLocalPriPath(sourcePath);
            var stagedHash = await HashPriFileAsync(sourcePath, cancellationToken);
            if (!originalHash.AsSpan().SequenceEqual(stagedHash))
            {
                throw new InvalidDataException("The staged resources.pri changed during identity reindexing.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Replace(resultPath, sourcePath, destinationBackupFileName: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Cannot safely reindex resources.pri from '{originalPackageName}' to '{effectivePackageName}': {ex.Message}", ex);
        }
        finally
        {
            try
            {
                // Never follow a directory that has been redirected since it was created.
                EnsureLocalPriPath(work.FullName);
                work.Delete(recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                taskContext.AddDebugMessage($"Could not remove PRI working directory '{work.FullName}': {ex.Message}");
            }
        }
    }

    private async Task DumpIdentityPriAsync(string input, string output, TaskContext taskContext, CancellationToken cancellationToken)
    {
        var arguments = $@"dump /if ""{PriToolPath(input)}"" /of ""{PriToolPath(output)}"" /dt detailed /o";
        await buildToolsService.RunBuildToolAsync(new MakePriTool(), arguments, taskContext, cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureLocalPriPath(output);
    }

    private static string PriToolPath(string path) => LongPathHelper.EnsureExtendedLengthPrefix(path);

    private static async Task<byte[]> HashPriFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await SHA256.HashDataAsync(stream, cancellationToken);
    }

    private static void EnsureLocalPriPath(string path)
    {
        if (PathSafety.HasReparsePointOnPath(path, Path.GetPathRoot(path)!))
        {
            throw new IOException($"PRI identity reindexing requires a local path without symbolic links or junctions: {path}");
        }
    }

    private static void ValidatePriIdentityName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length is < 3 or > 50 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-'))
        {
            throw new ArgumentException("A PRI package name must contain 3–50 ASCII letters, digits, periods, or hyphens.", nameof(name));
        }
    }
}
