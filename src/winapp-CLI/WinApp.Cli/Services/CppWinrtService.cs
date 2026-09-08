// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services;

internal sealed class CppWinrtService(ILogger<CppWinrtService> logger) : ICppWinrtService
{
    /// <summary>
    /// Authenticode gate applied to cppwinrt.exe before it is executed. Defaults to the real verifier;
    /// tests override it because their fixtures stand in dummy unsigned files for the real SDK binaries.
    /// Mirrors <see cref="BuildToolsService.SignatureVerifier"/>.
    /// </summary>
    internal static Func<string, ILogger, bool> SignatureVerifier { get; set; } = AuthenticodeVerifier.IsTrustedMicrosoftSigned;

    public FileInfo? FindCppWinrtExe(DirectoryInfo packagesDir, IDictionary<string, string> usedVersions)
    {
        var pkgName = "Microsoft.Windows.CppWinRT";
        if (!usedVersions.TryGetValue(pkgName, out var v))
        {
            return null;
        }

        // NuGet global cache layout: {cache}/lowercase-id/version/
        var baseDir = Path.Combine(packagesDir.FullName, pkgName.ToLowerInvariant(), v);
        var exe = new FileInfo(Path.Combine(baseDir, "bin", "cppwinrt.exe"));
        return exe.Exists ? exe : null;
    }

    public async Task RunWithRspAsync(FileInfo cppwinrtExe, IEnumerable<FileInfo> winmdInputs, DirectoryInfo outputDir, DirectoryInfo workingDirectory, TaskContext taskContext, CancellationToken cancellationToken = default)
    {
        outputDir.Create();

        // cppwinrt.exe is downloaded from the configured NuGet feeds and then executed, so verify it at the
        // point of use — the same gate BuildToolsService applies to every build tool. Without it a custom or
        // compromised feed could supply an unsigned replacement that runs as the invoking user.
        // Deliberately not memoized, for the reason given in BuildToolsService: any cache key cheap enough to
        // compute here is metadata that whoever can replace the file can also reproduce.
        if (!SignatureVerifier(cppwinrtExe.FullName, logger))
        {
            throw new BuildToolSignatureException(
                $"'{cppwinrtExe.Name}' is not validly signed by Microsoft, so it was not run ({cppwinrtExe.FullName}). " +
                "The file on disk is not what Microsoft published, which usually means a corrupt or partial " +
                "download. Delete the package from the NuGet cache and run the command again to re-download it.");
        }

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
