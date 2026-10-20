// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Reflection;

namespace WinApp.Cli.Services;

internal interface IUiAccessibilityWorkerService
{
    Task<int> RunAsync(IReadOnlyList<string> arguments, TextWriter output, TextWriter error, CancellationToken cancellationToken);
}

internal sealed record UiAccessibilityWorkerPaths(string Winapp, string Worker)
{
    internal static UiAccessibilityWorkerPaths Resolve(string? processPath, string baseDirectory, string? entryAssemblyName)
    {
        if (!string.Equals(entryAssemblyName, "winapp", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(processPath) || !Path.IsPathFullyQualified(processPath))
        {
            throw new InvalidOperationException(
                "The running winapp executable could not be identified. Run 'winapp ui accessibility' from the complete local demo bundle.");
        }

        // Under `dotnet winapp.dll`, ProcessPath is the host, not the native command that the worker needs.
        // Never hand dotnet, a test host or a PATH-resolved executable back to the worker.
        var winapp = string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(Path.Combine(baseDirectory, "winapp.exe"))
            : Path.GetFullPath(processPath);
        return new(winapp, Path.Combine(Path.GetDirectoryName(winapp)!, "accessibility", "winapp-accessibility-worker.exe"));
    }
}

internal sealed class UiAccessibilityWorkerService : IUiAccessibilityWorkerService
{
    private readonly IProcessRunner _processRunner;
    private readonly Func<UiAccessibilityWorkerPaths> _resolvePaths;

    public UiAccessibilityWorkerService(IProcessRunner processRunner)
        : this(processRunner, () => UiAccessibilityWorkerPaths.Resolve(
            Environment.ProcessPath, AppContext.BaseDirectory, Assembly.GetEntryAssembly()?.GetName().Name))
    {
    }

    internal UiAccessibilityWorkerService(IProcessRunner processRunner, Func<UiAccessibilityWorkerPaths> resolvePaths)
    {
        _processRunner = processRunner;
        _resolvePaths = resolvePaths;
    }

    public async Task<int> RunAsync(
        IReadOnlyList<string> arguments, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var paths = _resolvePaths();
        if (!File.Exists(paths.Winapp))
        {
            throw new FileNotFoundException(
                $"The native winapp executable was not found at '{paths.Winapp}'. Run winapp.exe from the complete local accessibility demo bundle.",
                paths.Winapp);
        }
        if (!File.Exists(paths.Worker))
        {
            throw new FileNotFoundException(
                $"The bundled accessibility worker is missing at '{paths.Worker}'. Re-extract the complete local accessibility demo bundle, " +
                "keeping the accessibility folder next to winapp.exe. A separate dotnet installation or sample checkout is not required.",
                paths.Worker);
        }

        var workerArguments = new List<string>(arguments);
        if (arguments[0] is not ("assessment-template" or "assess"))
        {
            workerArguments.Add("--winapp");
            workerArguments.Add(paths.Winapp);
        }
        var result = await _processRunner.RunAsync(
            new ProcessRunRequest(paths.Worker, workerArguments),
            onOutputLine: output.WriteLine,
            onErrorLine: error.WriteLine,
            cancellationToken: cancellationToken);
        return result.ExitCode;
    }
}
