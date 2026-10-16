// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace WinApp.Cli.Services.Performance;

internal sealed class PerfCaptureWorker : IDisposable
{
    public const string InternalVerb = "__perf-worker";
    private readonly PerfCaptureDocument capture;
    private PerfControlRegistration registration;
    private readonly string registrationPath;
    private readonly IPrivateEtwApi? etwApi;
    private PrivateEtwSession? session;
    private Process? target;
    private long deadline;
    private long lastProbe;
    private long lastResourceProbe;

    private PerfCaptureWorker(PerfControlRegistration registration, string registrationPath, IPrivateEtwApi? etwApi)
    {
        this.registration = registration;
        this.registrationPath = registrationPath;
        this.etwApi = etwApi;
        capture = PerfCaptureDocument.Load(registration.Directory);
        PerfCaptureService.ValidateOwnership(registration, capture);
        if (registration.Worker is not null || capture.Worker is not null || capture.Target is not null || capture.State != "starting")
        {
            throw new InvalidDataException("The worker cannot take ownership of an existing capture.");
        }
        capture.Worker = PerfProcessIdentity.Read(Process.GetCurrentProcess());
        capture.DurationSec = registration.DurationSec;
        capture.MaxSizeMiB = registration.MaxSizeMiB;
        capture.Providers = PerfProviders.All;
        capture.ProviderStates = [];
        capture.Frequency = Stopwatch.Frequency;
        this.registration = registration with { Worker = capture.Worker };
        SaveRegistration();
        deadline = Stopwatch.GetTimestamp() + 30 * Stopwatch.Frequency;
    }

    public static async Task<int> RunAsync(string[] args, IPrivateEtwApi? etwApi = null)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Performance worker requires a private control registration.");
            return 1;
        }
        try
        {
            var registration = PerfCaptureService.ReadRegistration(args[1]);
            using var identity = WindowsIdentity.GetCurrent();
            var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            using var directories = elevated || registration.Mode == PerfProfileModes.Elevated
                ? PerfCaptureDirectoryLease.Open(Path.GetDirectoryName(Path.GetFullPath(args[1]))!, registration.Directory)
                : null;
            using var worker = new PerfCaptureWorker(registration, args[1], etwApi);
            return await worker.RunAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Performance worker failed: {ex.Message}");
            return 1;
        }
    }

    private async Task<int> RunAsync()
    {
        try
        {
            if (capture.Mode == PerfProfileModes.Elevated)
            {
                StopStaleElevatedSessions();
            }
            using var pipe = CreatePipe(capture.Id);
            capture.Save();
            using var lifetime = new CancellationTokenSource();
            var connection = pipe.WaitForConnectionAsync(lifetime.Token);
            while (!capture.Finalized)
            {
                await Task.WhenAny(connection, Task.Delay(100));
                if (target?.HasExited == true)
                {
                    capture.TargetExited = true;
                    Finish("target-exited");
                }
                else if (Stopwatch.GetTimestamp() >= deadline)
                {
                    Finish(session is null ? "binding-timeout" : "duration");
                }
                else if (session is not null && Directory.EnumerateFiles(capture.Directory, "trace.etl*")
                    .Sum(p => new FileInfo(p).Length) >= capture.MaxSizeMiB * 1024L * 1024)
                {
                    Finish("size-limit");
                }
                if (capture.Finalized)
                {
                    break;
                }
                ProbeRuntime();
                ProbeResources();
                if (!connection.IsCompleted)
                {
                    continue;
                }
                await connection;
                using var requestTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    var request = await PerfControlChannel.ReadAsync(pipe, PerfJsonContext.Default.PerfControlRequest,
                        4096, requestTimeout.Token);
                    PerfControlResponse response;
                    try
                    {
                        response = Handle(request);
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
                    {
                        if (request.Operation is "arm" or "bind" && capture.ReadyQpc is null)
                        {
                            capture.Error = ex.Message;
                            Finish("binding-failed");
                        }
                        response = new(capture, ex.Message);
                    }
                    using var responseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await PerfControlChannel.WriteAsync(pipe, response, PerfJsonContext.Default.PerfControlResponse,
                        responseTimeout.Token);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or InvalidDataException)
                {
                    // A disconnected or malformed controller must not extend the capture deadline.
                    capture.LastControlError = "Control request failed: " + ex.Message;
                    capture.Save();
                }
                if (pipe.IsConnected)
                {
                    pipe.Disconnect();
                }
                connection = pipe.WaitForConnectionAsync(lifetime.Token);
            }
            lifetime.Cancel();
            try
            {
                await connection;
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                // The worker owns this pending accept and cancels it before disposing the pipe.
            }
            return capture.State == "completed" ? 0 : 1;
        }
        catch (Exception ex)
        {
            capture.Error = ex.Message;
            try
            {
                Finish("worker-error");
            }
            catch (Exception stopError)
            {
                capture.State = "failed";
                capture.Error += "; stop failed: " + stopError.Message;
                capture.Save();
            }
            return 1;
        }
    }

    private PerfControlResponse Handle(PerfControlRequest request)
    {
        if (request.Credential is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(request.Credential),
            Encoding.UTF8.GetBytes(registration.Credential)))
        {
            return new(null, "The capture control credential is invalid.");
        }
        switch (request.Operation)
        {
            case "status":
                return new(capture);
            case "arm":
                // An elevated capture starts recording before launch, scoped to the app that is about to start.
                if (capture.Mode != PerfProfileModes.Elevated || session is not null || request.Scope is null ||
                    request.Scope.IsPrivate)
                {
                    throw new InvalidOperationException("Only an elevated capture can start recording before launch.");
                }
                BeginRecording(request.Scope, null);
                capture.Save();
                return new(capture);
            case "bind":
                var armed = capture.Mode == PerfProfileModes.Elevated;
                if (capture.Target is not null || request.Target is null || armed != (session is not null))
                {
                    throw new InvalidOperationException(armed
                        ? "An elevated capture must start recording before it binds to exactly one target process."
                        : "A capture can bind to exactly one target process.");
                }
                target = request.Target.Open();
                capture.Target = request.Target;
                registration = registration with { Target = request.Target };
                SaveRegistration();
                capture.StartupCoverage = request.StartupCoverage ?? capture.StartupCoverage;
                capture.DebuggerAttached = request.DebuggerAttached;
                if (!armed)
                {
                    BeginRecording(new(ProcessId: target.Id), target);
                }
                capture.ProcessResources = new();
                ProbeResources(force: true);
                capture.Save();
                return new(capture);
            case "mark":
                if (capture.State != "recording")
                {
                    throw new InvalidOperationException("Markers require an active capture.");
                }
                if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 128 ||
                    capture.Markers.Any(m => m.Name == request.Name) || capture.Markers.Count >= 256)
                {
                    throw new ArgumentException("Markers require unique nonempty names of at most 128 characters; maximum 256 markers.");
                }
                capture.Markers.Add(new(request.Name, Stopwatch.GetTimestamp()));
                capture.Save();
                return new(capture);
            case "stop":
                capture.TargetExited = target?.HasExited == true;
                Finish(capture.TargetExited ? "target-exited" : "requested");
                return new(capture);
            default:
                throw new ArgumentException("Unknown performance control operation.");
        }
    }

    private void ProbeRuntime()
    {
        if (target is null || target.HasExited ||
            Stopwatch.GetTimestamp() - lastProbe < Stopwatch.Frequency)
        {
            return;
        }
        lastProbe = Stopwatch.GetTimestamp();
        try
        {
            target.Refresh();
            foreach (ProcessModule module in target.Modules)
            {
                if (capture.Runtime is null &&
                    string.Equals(module.ModuleName, "Microsoft.UI.Xaml.dll", StringComparison.OrdinalIgnoreCase))
                {
                    var architecture = PeHelper.DetectPeArchitecture(module.FileName);
                    capture.Runtime = new(module.FileName, module.FileVersionInfo.FileVersion, module.FileVersionInfo.ProductVersion, architecture);
                    capture.RuntimeProbeError = architecture is null ? "The loaded WinUI module architecture could not be identified." : null;
                    capture.Save();
                }
                if ((module.ModuleName.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase) ||
                    module.ModuleName.Equals("clr.dll", StringComparison.OrdinalIgnoreCase)) &&
                    !capture.ManagedRuntimes.Any(r => r.Path.Equals(module.FileName, StringComparison.OrdinalIgnoreCase)))
                {
                    capture.ManagedRuntimes.Add(new(module.FileName, module.FileVersionInfo.FileVersion,
                        module.FileVersionInfo.ProductVersion, PeHelper.DetectPeArchitecture(module.FileName)));
                    capture.Save();
                }
            }
            if (capture.Runtime is null)
            {
                capture.RuntimeProbeError = "Microsoft.UI.Xaml.dll has not been observed in the target process.";
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            capture.RuntimeProbeError = ex.Message;
        }
    }

    private void ProbeResources(bool force = false)
    {
        if (target is null || capture.ProcessResources is not { } resources ||
            !force && Stopwatch.GetTimestamp() - lastResourceProbe < Stopwatch.Frequency)
        {
            return;
        }
        lastResourceProbe = Stopwatch.GetTimestamp();
        try
        {
            if (resources.Samples.Count >= PerfProcessResources.MaximumSamples)
            {
                throw new InvalidOperationException("Process resource sample limit reached.");
            }
            resources.Samples.Add(PerfProcessResources.Read(target));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            resources.FailedSamples++;
            resources.LastError = ex.Message.Length > 512 ? ex.Message[..512] : ex.Message;
        }
        capture.Save();
    }

    internal static string? UnfilteredProvidersWarning(PerfCaptureDocument capture)
    {
        // Private ETW sessions accept only scope filters, so Windows can reject event-ID filtering
        // and the provider records all of its events.
        var names = capture.ProviderStates
            .Where(s => s is { State: "enabled", EventIdFilterApplied: false })
            .Select(s => capture.Providers.FirstOrDefault(p => p.Id == s.Id)?.Name ?? s.Id.ToString())
            .ToArray();
        return names.Length == 0 ? null :
            $"Windows did not apply event filtering for {string.Join(", ", names)}, so unused events are also recorded. " +
            "UI-heavy scenarios can reach the size limit quickly; if recording stops with size-limit, " +
            "raise --max-size-mib (or --profile-max-size-mib) or shorten the scenario.";
    }

    internal static PerfProviderState EnableProvider(PrivateEtwSession session, PerfProvider provider)
    {
        try
        {
            var filtered = session.Enable(provider.Id,
                ulong.Parse(provider.Keywords, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                provider.Level, provider.EventIds);
            return new(provider.Id, "enabled", provider.EventIds is null ? null : filtered);
        }
        catch (Win32Exception ex) when (provider.Optional)
        {
            return new(provider.Id, "unavailable", null, ex.Message);
        }
    }

    private void BeginRecording(PerfEtwScope scope, Process? process)
    {
        var activeSession = StartSession(scope, process);
        foreach (var provider in capture.Providers)
        {
            capture.ProviderStates.Add(EnableProvider(activeSession, provider));
        }
        if (UnfilteredProvidersWarning(capture) is { } unfiltered)
        {
            capture.Warnings.Add(unfiltered);
        }
        capture.ReadyQpc = Stopwatch.GetTimestamp();
        capture.ReadyUtc = DateTime.UtcNow;
        deadline = capture.ReadyQpc.Value + capture.DurationSec * Stopwatch.Frequency;
        capture.State = "recording";
    }

    private PrivateEtwSession StartSession(PerfEtwScope scope, Process? process)
    {
        var until = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
        while (true)
        {
            session = new(capture.SessionName, capture.SessionId, scope,
                Path.Join(capture.Directory, "trace.etl"), capture.MaxSizeMiB, etwApi);
            if (session.CanEnable)
            {
                return session;
            }
            // StartTrace may succeed without a usable handle during early process
            // initialization. Finalize that request before retrying the same PID scope.
            session.Dispose();
            session = null;
            if (process is null || process.HasExited || Stopwatch.GetTimestamp() >= until)
            {
                throw new PerfEtwTargetNotReadyException();
            }
            Thread.Sleep(50);
        }
    }

    /// <summary>
    /// Creates the control pipe so only the current user can open it. The owner is set explicitly because an
    /// elevated worker would otherwise create a pipe owned by Administrators, which a non-elevated controller rejects.
    /// </summary>
    private static NamedPipeServerStream CreatePipe(string id)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(identity.User!);
        security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(PerfControlChannel.PipeName(id), PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    /// <summary>
    /// An elevated session is owned by the system, so it keeps recording if its worker is killed. Stop sessions
    /// left behind by earlier elevated workers that are no longer running; only an elevated caller can.
    /// </summary>
    private void StopStaleElevatedSessions()
    {
        var root = Path.GetDirectoryName(Path.GetDirectoryName(registrationPath));
        if (root is null || !Directory.Exists(root))
        {
            return;
        }
        foreach (var path in Directory.EnumerateFiles(root, "control.json", SearchOption.AllDirectories))
        {
            try
            {
                var other = PerfCaptureService.ReadRegistration(path);
                if (other.Id == registration.Id || other.Mode != PerfProfileModes.Elevated || other.Worker is null ||
                    IsRunning(other.Worker))
                {
                    continue;
                }
                PrivateEtwSession.StopOwned("WinApp-Perf-" + other.Id, other.SessionId, null,
                    Path.Join(other.Directory, "trace.etl"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                JsonException or InvalidOperationException or Win32Exception)
            {
                // A session that cannot be verified as this capture's is left alone.
            }
        }

        static bool IsRunning(PerfProcessIdentity worker)
        {
            try
            {
                using var process = worker.Open();
                return !process.HasExited;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return false;
            }
        }
    }

    private void Finish(string reason)
    {
        if (target?.HasExited == false)
        {
            ProbeResources(force: true);
        }
        ProbeRuntime();
        capture.State = "stopping";
        capture.StopReason = reason;
        capture.Save();
        try
        {
            session?.Stop();
        }
        catch (Win32Exception) when (session is { Stopped: false } && TargetExitsSoon())
        {
            // A crashing app can stay frozen for error reporting; its private session ends with the process.
            capture.TargetExited = true;
            capture.StopReason = "target-exited";
        }
        capture.StopQpc = Stopwatch.GetTimestamp();
        capture.StoppedUtc = DateTime.UtcNow;
        capture.EventsLost = session?.EventsLost;
        capture.BuffersLost = session?.BuffersLost;
        capture.TraceFiles = Directory.GetFiles(capture.Directory, "trace.etl*")
            .Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToArray();
        SetFinalState(capture, session?.Stopped == true);
        capture.Save();
    }

    private bool TargetExitsSoon() =>
        capture.Mode == PerfProfileModes.Attach && target is not null && target.WaitForExit(TimeSpan.FromSeconds(30));

    internal static void SetFinalState(PerfCaptureDocument capture, bool sessionStopped)
    {
        var appClosed = capture.StopReason == "target-exited" && capture.TargetExited && capture.ReadyQpc is not null;
        var normalStop = sessionStopped && capture.StopReason is "duration" or "requested" &&
            capture.Runtime is not null && capture.EventsLost == 0 && capture.BuffersLost == 0;
        capture.State = capture.Error is null && (appClosed || normalStop) ? "completed" : "failed";
        if (appClosed && capture.State == "completed")
        {
            capture.Warnings.Add("Recording ended when the app exited. Its final buffered events and loss statistics may be unavailable; retained events can still be analyzed.");
        }
        if (capture.State == "failed")
        {
            capture.Error ??= $"Capture stopped with {capture.StopReason}; runtime or final ETW statistics may be unavailable.";
        }
    }

    public void Dispose()
    {
        target?.Dispose();
        session?.Dispose();
    }

    private void SaveRegistration() => Helpers.AtomicFile.WriteAllText(registrationPath,
        JsonSerializer.Serialize(registration, PerfJsonContext.Default.PerfControlRegistration));
}
