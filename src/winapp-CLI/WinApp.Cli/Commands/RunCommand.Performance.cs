// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
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
        Description = "Record WinUI 3 performance ETW to an empty directory. By default it attaches after the app starts, so early startup events may be missed; use --profile-mode elevated to record startup.",
    };
    public static Option<string> ProfileModeOption { get; } = new Option<string>("--profile-mode")
    {
        Description = "With --profile: 'attach' (default) records after the app starts, without elevation. 'elevated' asks for administrator approval (UAC) for the recorder only, and starts recording before launch so startup is captured.",
        DefaultValueFactory = _ => PerfProfileModes.Attach,
    }.AcceptOnlyFromAmong(PerfProfileModes.Attach, PerfProfileModes.Elevated);
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
                    parseResult.GetResult(ProfileSizeOption) is { Implicit: false } ||
                    parseResult.GetResult(ProfileModeOption) is { Implicit: false })
                {
                    return Fail("--profile-duration-sec, --profile-max-size-mib and --profile-mode require --profile.", json);
                }
                return await InvokeCoreAsync(parseResult, cancellationToken);
            }
            if (!ExecutionTargetSelection.Resolve(parseResult).IsLocal)
            {
                return Fail("--profile only records on this machine. Remove --on sandbox or --profile.", json);
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
                profileRun = new(perfCaptureService, directory, duration, size, parseResult.GetValue(ProfileModeOption)!,
                    parseResult.GetValue(DebugOutputOption));
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

        /// <param name="scope">The app about to launch, so an elevated capture records only that app.</param>
        private async Task PrepareProfileAsync(PerfEtwScope scope, CancellationToken token)
        {
            if (profileRun is not null)
            {
                await profileRun.PrepareAsync(scope, token);
            }
        }

        /// <summary>Scopes an elevated capture to the current user's registration of the package.</summary>
        private Task PreparePackageProfileAsync(string? packageFamilyName, CancellationToken token)
        {
            if (profileRun is not { Elevated: true })
            {
                return PrepareProfileAsync(new(), token);
            }
            var fullName = string.IsNullOrWhiteSpace(packageFamilyName)
                ? null
                : appLauncherService.GetRegisteredPackageOrThrow(packageFamilyName)?.FullName;
            return PrepareProfileAsync(new(PackageFullName: fullName), token);
        }

        private async Task BindProfileAsync(uint pid, DateTime launchedAfter, CancellationToken token)
        {
            if (profileRun is null)
            {
                return;
            }
            await profileRun.BindAsync(pid, launchedAfter, token);
            if (profileRun.Error is null)
            {
                if (profileRun.Elevated)
                {
                    logger.LogInformation("Performance capture {CaptureId}: {Directory}. Recording from launch.",
                        profileRun.CaptureId, profileRun.Directory);
                }
                else
                {
                    logger.LogInformation("Performance capture {CaptureId}: {Directory}. Early startup may be missing; use --profile-mode elevated to record it.",
                        profileRun.CaptureId, profileRun.Directory);
                }
                if (profileRun.Debugger)
                {
                    logger.LogWarning("Debugger pauses perturb performance timings.");
                }
            }
            else
            {
                logger.LogError("Performance capture failed: {Error}", profileRun.Error);
            }
        }
    }
}

internal sealed record RunProfileResult(string? CaptureId, string Directory, string State, string StartupCoverage, string? Error,
    string[]? Warnings = null);

internal sealed class PerfRunCapture(PerfCaptureService service, string directory, int duration, int size, string mode,
    bool debugger)
{
    private PerfControlRegistration? registration;
    private PerfCaptureDocument? capture;
    private Task? stopping;
    private readonly object gate = new();
    public string Directory { get; } = Path.GetFullPath(directory);
    public string? CaptureId => registration?.Id;
    public bool Debugger { get; } = debugger;
    public bool Elevated { get; } = mode == PerfProfileModes.Elevated;
    public string? Error { get; private set; }
    public RunProfileResult Result => new(CaptureId, Directory, Error is not null ? "failed" : capture?.State ?? "starting",
        capture?.StartupCoverage ?? "unknown", Error,
        capture is null ? null : [.. capture.Warnings, .. capture.ProviderStates.Where(p => p.Error is not null).Select(p => p.Error!)]);

    public async Task PrepareAsync(PerfEtwScope scope, CancellationToken token)
    {
        if (Elevated && scope.IsEmpty)
        {
            throw new InvalidOperationException(
                "Cannot identify the app for elevated performance recording. Use --profile-mode attach to record after launch.");
        }
        registration = await service.PrepareAsync(Directory, duration, size, mode, token);
        if (Elevated)
        {
            capture = await PerfCaptureService.ArmAsync(registration, scope, token);
        }
    }

    public async Task BindAsync(uint pid, DateTime launchedAfter, CancellationToken token)
    {
        try
        {
            using var target = Process.GetProcessById(checked((int)pid));
            var identity = PerfProcessIdentity.Read(target);
            capture = await PerfCaptureService.BindAsync(registration!, identity,
                identity.CreationUtc < launchedAfter ? "attached-to-existing; startup not recorded" :
                Elevated ? "recorded from launch (elevated session)" : "post-launch attachment; early startup may be missing",
                Debugger, token);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
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
