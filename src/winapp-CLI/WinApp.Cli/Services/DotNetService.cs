// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinApp.Cli.Services;

[JsonSerializable(typeof(DotNetPackageListJson))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class DotNetServiceJsonContext : JsonSerializerContext
{
}

internal class DotNetService : IDotNetService
{
    /// <summary>
    /// Helper method to run dotnet commands
    /// </summary>
    public async Task<(int ExitCode, string Output, string Error)> RunDotnetCommandAsync(
        DirectoryInfo workingDirectory,
        string arguments,
        CancellationToken cancellationToken)
    {
        var processStartInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = arguments,
            WorkingDirectory = workingDirectory.FullName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = processStartInfo };

        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        process.OutputDataReceived += (sender, e) =>
        {
            if (e.Data != null)
            {
                outputBuilder.AppendLine(e.Data);
            }
        };

        process.ErrorDataReceived += (sender, e) =>
        {
            if (e.Data != null)
            {
                errorBuilder.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(cancellationToken);

        var output = outputBuilder.ToString();
        var error = errorBuilder.ToString();

        return (process.ExitCode, output, error);
    }

    public async Task<DotNetPackageListJson?> GetPackageListAsync(DirectoryInfo workingDirectory, CancellationToken cancellationToken)
    {
        var (ExitCode, Output, _) = await RunDotnetCommandAsync(workingDirectory, "package list --include-transitive --format json", cancellationToken);
        if (ExitCode != 0)
        {
            return null;
        }

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Output));
        return await JsonSerializer.DeserializeAsync(stream, DotNetServiceJsonContext.Default.DotNetPackageListJson, cancellationToken);
    }
}
