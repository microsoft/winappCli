// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Reflection;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;

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
                "The running winapp executable could not be identified. Run 'winapp ui accessibility' from a complete winapp installation.");
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
                $"The native winapp executable was not found at '{paths.Winapp}'. Reinstall the complete winapp package.",
                paths.Winapp);
        }
        if (!File.Exists(paths.Worker))
        {
            throw new FileNotFoundException(
                $"The bundled accessibility worker is missing at '{paths.Worker}'. Reinstall or re-extract the complete winapp package, " +
                "keeping the accessibility folder next to winapp.exe. A separate dotnet installation or sample checkout is not required.",
                paths.Worker);
        }
        try
        {
            // Node AbortSignal can terminate winapp without running managed cancellation handlers.
            // Contain the caller before spawning so the worker and native grandchildren cannot outlive it.
            GuestJobObject.EnsureCallerLifetimeContainment();
        }
        catch (ExecutionTargetException ex)
        {
            throw new InvalidOperationException("Could not contain the accessibility worker process tree. " + ex.Message, ex);
        }

        var workerArguments = new List<string>(arguments);
        if (arguments[0] is not ("assessment-template" or "assess"))
        {
            workerArguments.Add("--winapp");
            workerArguments.Add(paths.Winapp);
        }
        // The worker orchestrates coordinated native UI children. A parent turn/barrier would
        // deadlock those children; inherit WINAPP_UI_WORKFLOW_ID unchanged instead.
        var result = await _processRunner.RunAsync(
            new ProcessRunRequest(paths.Worker, workerArguments),
            onOutputLine: output.WriteLine,
            onErrorLine: error.WriteLine,
            cancellationToken: cancellationToken);
        return result.ExitCode;
    }
}
