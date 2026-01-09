// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;
using WinApp.Cli.ConsoleTasks;

namespace WinApp.Cli.Services;

internal sealed class CppWinrtService(ILogger<CppWinrtService> logger) : ICppWinrtService
{
    public FileInfo? FindCppWinrtExe(DirectoryInfo packagesDir, IDictionary<string, string> usedVersions)
    {
        var pkgName = "Microsoft.Windows.CppWinRT";
        if (!usedVersions.TryGetValue(pkgName, out var v))
        {
            return null;
        }

        var baseDir = Path.Combine(packagesDir.FullName, $"{pkgName}.{v}");
        var exe = new FileInfo(Path.Combine(baseDir, "bin", "cppwinrt.exe"));
        return exe.Exists ? exe : null;
    }

    public async Task RunWithRspAsync(FileInfo cppwinrtExe, IEnumerable<FileInfo> winmdInputs, DirectoryInfo outputDir, DirectoryInfo workingDirectory, TaskContext taskContext, CancellationToken cancellationToken = default)
    {
        outputDir.Create();
        var rspPath = new FileInfo(Path.Combine(outputDir.FullName, ".cppwinrt.rsp"));

        var sb = new StringBuilder();
        foreach (var winmd in winmdInputs)
        {
            sb.AppendLine($"-input \"{winmd}\"");
        }
        sb.AppendLine("-optimize");
        sb.AppendLine($"-output \"{outputDir}\"");
        if (logger.IsEnabled(LogLevel.Debug))
        {
            sb.AppendLine("-verbose");
        }

        await File.WriteAllTextAsync(rspPath.FullName, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);

        taskContext.AddDebugMessage($"cppwinrt: {cppwinrtExe} @{rspPath}");

        var psi = new ProcessStartInfo
        {
            FileName = cppwinrtExe.FullName,
            Arguments = $"@{rspPath}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory.FullName
        };

        using var p = Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await p.StandardError.ReadToEndAsync(cancellationToken);
        await p.WaitForExitAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(stdout))
        {
            taskContext.AddDebugMessage(stdout);
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            taskContext.AddDebugMessage(stderr);
        }

        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException("cppwinrt execution failed");
        }
    }
}
