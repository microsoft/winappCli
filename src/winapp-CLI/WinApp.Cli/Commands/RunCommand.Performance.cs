// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WinApp.Cli.Services;
using WinApp.Cli.Services.Performance;

namespace WinApp.Cli.Commands;

internal partial class RunCommand
{
    internal static bool IsProfileInvocation(ParseResult parse) =>
        parse.CommandResult.Command.Name == "run" && parse.GetResult(ProfileOption) is not null;

    internal static void EmitProfileParseError(string message) =>
        Console.Out.WriteLine(JsonSerializer.Serialize(new RunCommandResult { Error = message },
            RunCommandJsonContext.Default.RunCommandResult));

    public static Option<string?> ProfileOption { get; } = new("--profile")
    {
        Description = "Record WinUI 3 performance ETW to an empty directory. Enables providers before a new process reaches its executable entry point; earlier DLL and TLS initialization is not recorded.",
    };
    public static Option<int> ProfileDurationOption { get; } = new("--profile-duration-sec")
    {
        Description = "With --profile: trace for 1-300 seconds; stopping the trace does not stop the app.",
        DefaultValueFactory = _ => 30,
    };
    public static Option<int> ProfileSizeOption { get; } = new("--profile-max-size-mib")
    {
        Description = "With --profile: maximum raw ETL size, 1-1024 MiB.",
        DefaultValueFactory = _ => 512,
    };

    public partial class Handler
    {
        private PerfRunCapture? profileRun;

        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            profileRun = null;
            var directory = parseResult.GetValue(ProfileOption);
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            if (directory is null)
            {
                if (parseResult.GetResult(ProfileDurationOption) is { Implicit: false } ||
                    parseResult.GetResult(ProfileSizeOption) is { Implicit: false })
                {
                    return Fail("--profile-duration-sec and --profile-max-size-mib require --profile.", json);
                }
                return await InvokeCoreAsync(parseResult, cancellationToken);
            }
            if (!ExecutionTargetSelection.Resolve(parseResult).IsLocal)
            {
                return Fail("--profile only records on this machine. Remove --on sandbox or --profile.", json);
            }
            if (PerfStartupGate.HostArchitectureError(RuntimeInformation.OSArchitecture,
                RuntimeInformation.ProcessArchitecture) is { } architectureError)
            {
                return Fail(architectureError, json);
            }
            var duration = parseResult.GetValue(ProfileDurationOption);
            var size = parseResult.GetValue(ProfileSizeOption);
            if (duration is < 1 or > 300 || size is < 1 or > 1024 || parseResult.GetValue(NoLaunchOption))
            {
                return Fail("--profile requires launching the app, a duration of 1-300 seconds and a size of 1-1024 MiB.", json);
            }
            if (perfCaptureService is null)
            {
                return Fail("The performance capture service is unavailable.", json);
            }
            try
            {
                profileRun = new(perfCaptureService, directory, duration, size, parseResult.GetValue(DebugOutputOption));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return Fail("Invalid profile directory: " + ex.Message, json);
            }
            using var gatedCancellation = new CancellationTokenSource();
            var cancelTask = Task.CompletedTask;
            using var registration = cancellationToken.Register(() => cancelTask = CancelAfterCaptureAsync());
            var exit = 1;
            try
            {
                exit = await InvokeCoreAsync(parseResult, gatedCancellation.Token);
            }
            finally
            {
                registration.Dispose();
                await cancelTask;
                if (!parseResult.GetValue(DetachOption) || exit != 0 || profileRun.Error is not null)
                {
                    await profileRun.StopAsync();
                }
            }
            if (profileRun.Error is { } error)
            {
                PerfCommand.EmitError(json, "profile_incomplete", error, true);
                return exit == 0 ? 1 : exit;
            }
            return exit;

            async Task CancelAfterCaptureAsync()
            {
                await profileRun.StopAsync();
                await gatedCancellation.CancelAsync();
            }
        }

        private async Task PrepareProfileAsync(CancellationToken token)
        {
            if (profileRun is not null)
            {
                await profileRun.PrepareAsync(token);
            }
        }

        private async Task<ILaunchedProcess> LaunchExecutableWithProfileAsync(string executable, string? arguments,
            string? workingDirectory, LaunchStdioMode stdio, CancellationToken token)
        {
            await PrepareProfileAsync(token);
            if (profileRun is null)
            {
                return appLauncherService.LaunchExecutable(executable, arguments, workingDirectory, stdio);
            }
            var launchedAfter = DateTime.UtcNow;
            return await appLauncherService.LaunchExecutableForProfilingAsync(executable, arguments,
                workingDirectory, stdio, pid => BindProfileAsync(pid, launchedAfter, token, true), token);
        }

        private async Task<uint> LaunchAumidWithProfileAsync(string aumid, string? arguments,
            string? packageFullName, CancellationToken token)
        {
            if (profileRun is null)
            {
                return appLauncherService.LaunchByAumid(aumid, arguments);
            }
            packageFullName ??= appLauncherService.GetRegisteredPackageOrThrow(aumid.Split('!')[0])?.FullName;
            if (string.IsNullOrEmpty(packageFullName))
            {
                throw new InvalidOperationException("Startup recording requires the activated package's full name.");
            }
            await PrepareProfileAsync(token);
            using var debugging = appLauncherService.EnablePackageDebugging(packageFullName,
                profileRun.StartupDebuggerCommand(packageFullName));
            var launchedAfter = DateTime.UtcNow;
            var pid = appLauncherService.LaunchByAumid(aumid, arguments);
            await BindProfileAsync(pid, launchedAfter, token);
            return pid;
        }

        private async Task BindProfileAsync(uint pid, DateTime launchedAfter, CancellationToken token, bool entryPoint = false)
        {
            if (profileRun is null)
            {
                return;
            }
            await profileRun.BindAsync(pid, launchedAfter, token, entryPoint);
            if (profileRun.Error is null)
            {
                logger.LogInformation("Performance capture {CaptureId}: {Directory}. {StartupCoverage}.",
                    profileRun.CaptureId, profileRun.Directory, profileRun.Result.StartupCoverage);
                if (profileRun.Debugger)
                {
                    logger.LogWarning("Debugger pauses perturb performance timings.");
                }
            }
            else
            {
                logger.LogError("Performance capture failed: {Error}", profileRun.Error);
                throw new InvalidOperationException(profileRun.Error);
            }
        }
    }
}

internal sealed record RunProfileResult(string? CaptureId, string Directory, string State, string StartupCoverage, string? Error,
    string[]? Warnings = null);

internal sealed class PerfRunCapture(PerfCaptureService service, string directory, int duration, int size, bool debugger)
{
    private PerfControlRegistration? registration;
    private PerfCaptureDocument? capture;
    private Task? stopping;
    private readonly object gate = new();
    public string Directory { get; } = Path.GetFullPath(directory);
    public string? CaptureId => registration?.Id;
    public bool Debugger { get; } = debugger;
    public string? Error { get; private set; }
    public RunProfileResult Result => new(CaptureId, Directory, Error is not null ? "failed" : capture?.State ?? "starting",
        capture?.StartupCoverage ?? "unknown", Error,
        capture is null ? null : [.. capture.Warnings, .. capture.ProviderStates.Where(p => p.Error is not null).Select(p => p.Error!)]);

    public async Task PrepareAsync(CancellationToken token)
    {
        registration = await service.PrepareAsync(Directory, duration, size, token);
    }

    public string StartupDebuggerCommand(string packageFullName) =>
        PerfStartupHelper.Command(service.RegistrationPath(registration!.Id), packageFullName, Debugger);

    public async Task BindAsync(uint pid, DateTime launchedAfter, CancellationToken token, bool entryPoint = false)
    {
        try
        {
            if (!entryPoint)
            {
                var existing = await StartupStatusAsync(token);
                if (existing.Target is { } bound && bound.Pid == pid &&
                    bound.CreationUtc >= launchedAfter && existing.StartupCoverage == PerfStartupGate.Coverage &&
                    existing.State is "recording" or "completed" or "failed")
                {
                    capture = existing;
                    if (existing.State == "failed") { Error = existing.Error ?? "Startup recording failed."; }
                    return;
                }
            }
            using var target = Process.GetProcessById(checked((int)pid));
            var identity = PerfProcessIdentity.Read(target);
            if (!entryPoint && identity.CreationUtc >= launchedAfter)
            {
                var until = Stopwatch.GetTimestamp() + 30 * Stopwatch.Frequency;
                do
                {
                    token.ThrowIfCancellationRequested();
                    var started = await StartupStatusAsync(token);
                    if (started.Target == identity && started.StartupCoverage == PerfStartupGate.Coverage &&
                        started.State is "recording" or "completed")
                    {
                        capture = started;
                        return;
                    }
                    if (started.State == "failed")
                    {
                        throw new InvalidOperationException(started.Error ?? "Startup recording failed.");
                    }
                    await Task.Delay(50, token);
                } while (Stopwatch.GetTimestamp() < until);
                throw new TimeoutException("The startup helper did not prepare recording before the application entry point.");
            }
            capture = await PerfCaptureService.BindAsync(registration!, identity,
                entryPoint ? PerfStartupGate.Coverage : "attached-to-existing; startup not recorded",
                Debugger, token);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    private async Task<PerfCaptureDocument> StartupStatusAsync(CancellationToken token)
    {
        try
        {
            return await PerfControlChannel.SendAsync(registration!,
                new(registration!.Credential, "status"), token);
        }
        catch (Exception ex) when ((ex is IOException or OperationCanceledException) && !token.IsCancellationRequested)
        {
            // Once the worker closes its pipe, its finalized manifest is no longer being updated.
            var latestRegistration = PerfCaptureService.ReadRegistration(service.RegistrationPath(registration!.Id));
            var finalized = PerfCaptureService.ReadFinalCapture(latestRegistration, "status");
            if (finalized is null) { throw; }
            return finalized;
        }
    }

    public Task StopAsync()
    {
        lock (gate)
        {
            if (registration is null)
            {
                return Task.CompletedTask;
            }
            return stopping ??= StopCoreAsync();
        }
    }

    private async Task StopCoreAsync()
    {
        if (registration is null)
        {
            return;
        }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            capture = await service.ControlAsync(registration.Id, "stop", null, timeout.Token);
            if (capture.State != "completed")
            {
                Error ??= capture.Error ?? "The requested performance capture is incomplete.";
            }
        }
        catch (Exception ex)
        {
            Error ??= "Could not finalize the performance capture: " + ex.Message;
        }
    }
}
