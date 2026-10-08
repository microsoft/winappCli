// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Tools;

namespace WinApp.Cli.Services;

internal partial class PriService
{
    // Re-indexes an existing PRI as-is: same resources and qualifiers, new package name.
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
        string packageName,
        TaskContext taskContext,
        CancellationToken cancellationToken = default)
    {
        // The name goes on the makepri command line, so it must not carry quotes or switches.
        if (!DevelopmentIdentityHelper.IsValidPackageName(packageName))
        {
            throw new ArgumentException($"'{packageName}' is not a valid package name.", nameof(packageName));
        }

        var priPath = Path.Join(layout.FullName, "resources.pri");
        if (!File.Exists(priPath))
        {
            return;
        }

        // Outside the layout so nothing here is registered as app payload.
        var work = Directory.CreateTempSubdirectory("winapp-pri-");
        try
        {
            var input = work.CreateSubdirectory("input");
            File.Copy(priPath, Path.Join(input.FullName, "original.pri"));
            var config = Path.Join(work.FullName, "reindex.xml");
            await File.WriteAllTextAsync(config, IdentityReindexConfig, cancellationToken);
            var output = Path.Join(work.FullName, "resources.pri");

            var arguments = $@"new /pr ""{ToolPath(input.FullName)}"" /cf ""{ToolPath(config)}"" /in ""{packageName}"" /of ""{ToolPath(output)}"" /o";
            await buildToolsService.RunBuildToolAsync(new MakePriTool(), arguments, taskContext, cancellationToken: cancellationToken);
            if (!File.Exists(output) || new FileInfo(output).Length == 0)
            {
                throw new InvalidOperationException(
                    "MakePri did not produce resources.pri for the unique identity. Rebuild the app, or run without --unique-identity.");
            }

            // Replaces the layout's entry rather than writing through a linked destination.
            AtomicFile.Copy(output, priPath);
            taskContext.AddDebugMessage($"{UiSymbols.Files} Re-indexed resources.pri for {packageName}");
        }
        finally
        {
            try
            {
                work.Delete(recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                taskContext.AddDebugMessage($"Could not remove '{work.FullName}': {ex.Message}");
            }
        }
    }

    private static string ToolPath(string path) => LongPathHelper.EnsureExtendedLengthPrefix(path);
}
