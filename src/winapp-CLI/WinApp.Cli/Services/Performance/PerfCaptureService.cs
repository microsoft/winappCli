// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services.Performance;

internal sealed class PerfCaptureService(IWinappDirectoryService directories, IAppLauncherService launcher)
{
    private string RegistryRoot => Path.Join(directories.GetGlobalWinappDirectory().FullName, "perf-control");

    public async Task<PerfControlRegistration> PrepareAsync(string directory, int durationSec, int maxSizeMiB,
        CancellationToken token)
    {
        if (durationSec is < 1 or > 300 || maxSizeMiB is < 1 or > 1024)
        {
            throw new ArgumentException("Capture duration must be 1-300 seconds and maximum size 1-1024 MiB.");
        }
        var output = new DirectoryInfo(Path.GetFullPath(directory));
        output.Create();
        if (output.EnumerateFileSystemInfos().Any())
        {
            throw new IOException("The capture output directory must be empty.");
        }
        // CreateNew claims the directory before publishing any mutable capture state.
        using (new FileStream(Path.Join(output.FullName, ".capture-owner"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
        }
        var id = Guid.NewGuid().ToString("N");
        var registry = new DirectoryInfo(Path.Join(RegistryRoot, id));
        using var identity = WindowsIdentity.GetCurrent();
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(identity.User!);
        acl.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        registry.Create(acl);
        var registration = new PerfControlRegistration(id, output.FullName,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), Guid.NewGuid(), durationSec, maxSizeMiB);
        var registrationPath = Path.Join(registry.FullName, "control.json");
        AtomicFile.WriteAllText(registrationPath, JsonSerializer.Serialize(registration, PerfJsonContext.Default.PerfControlRegistration));
        var capture = new PerfCaptureDocument
        {
            Id = id,
            Directory = output.FullName,
            SessionId = registration.SessionId,
            SessionName = "WinApp-Perf-" + id,
            DurationSec = durationSec,
            MaxSizeMiB = maxSizeMiB,
        };
        capture.Save();
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot identify the current winapp executable.");
        var arguments = new List<string>();
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add(Path.Join(AppContext.BaseDirectory, Assembly.GetEntryAssembly()!.GetName().Name + ".dll"));
        }
        arguments.Add(PerfCaptureWorker.InternalVerb);
        arguments.Add(registrationPath);
        try
        {
            using var worker = launcher.LaunchExecutable(executable, string.Join(' ', arguments.Select(WindowsCommandLine.EscapeArgument)),
                stdioMode: LaunchStdioMode.Suppress);
            await PerfControlChannel.SendAsync(registration, new(registration.Credential, "status"), token);
            return registration;
        }
        catch (Exception ex)
        {
            capture.State = "failed";
            capture.StopReason = "readiness-failed";
            capture.Error = $"Performance worker readiness failed: {ex.Message}. Any unbound worker has a 30-second shutdown deadline.";
            capture.Save();
            throw new InvalidOperationException($"{capture.Error} Capture details: {output.FullName}", ex);
        }
    }

    public static Task<PerfCaptureDocument> BindAsync(PerfControlRegistration registration, PerfProcessIdentity target,
        string startupCoverage, bool debugger, CancellationToken token) =>
        PerfControlChannel.SendAsync(registration, new(registration.Credential, "bind", target,
            StartupCoverage: startupCoverage, DebuggerAttached: debugger), token);

    public async Task<PerfCaptureDocument> ControlAsync(string id, string operation, string? name, CancellationToken token)
    {
        if (!Guid.TryParseExact(id, "N", out _))
        {
            throw new ArgumentException("A capture ID must be the 32-character ID returned by perf start.");
        }
        var registration = ReadRegistration(Path.Join(RegistryRoot, id, "control.json"));
        var capture = PerfCaptureDocument.Load(registration.Directory);
        ValidateOwnership(registration, capture);
        capture.Worker = registration.Worker;
        capture.Target = registration.Target;
        if (capture.Finalized && operation == "mark")
        {
            throw new InvalidOperationException("Markers require an active capture. This recording has already ended.");
        }
        if (capture.Finalized && (operation == "status" ||
            (operation == "stop" && (capture.StopQpc is not null || registration.Worker is null))))
        {
            return capture;
        }
        if (capture.Worker is not null)
        {
            try
            {
                using var worker = capture.Worker.Open();
                if (worker.HasExited)
                {
                    throw new InvalidOperationException("The capture worker has exited.");
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                if (ReadFinalCapture(registration, operation) is { } finalized)
                {
                    return finalized;
                }
                capture.State = "failed";
                capture.Error = "Worker unavailable; ETW finalization and loss are unknown. " + ex.Message;
                capture.EventsLost = null;
                capture.BuffersLost = null;
                if (operation == "stop" && capture.Target is { } identity)
                {
                    using var target = identity.Open();
                    var stopped = PrivateEtwSession.StopOwned(capture.SessionName, capture.SessionId, target.Id,
                        Path.Join(capture.Directory, "trace.etl"));
                    capture.StopQpc = Stopwatch.GetTimestamp();
                    capture.StoppedUtc = DateTime.UtcNow;
                    capture.StopReason = stopped ? "orphan-recovered" : "session-unavailable";
                    capture.TraceFiles = Directory.GetFiles(capture.Directory, "trace.etl*")
                        .Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToArray();
                }
                capture.Save();
                return capture;
            }
        }
        try
        {
            return await PerfControlChannel.SendAsync(registration, new(registration.Credential, operation, Name: name), token);
        }
        catch (Exception ex) when ((ex is IOException or OperationCanceledException) && !token.IsCancellationRequested)
        {
            // Finalization can close the pipe between the initial state read and the control reply.
            if (ReadFinalCapture(registration, operation) is { } finalized)
            {
                return finalized;
            }
            throw;
        }
    }

    internal static PerfCaptureDocument? ReadFinalCapture(PerfControlRegistration registration, string operation)
    {
        if (operation is not ("status" or "stop" or "mark"))
        {
            return null;
        }
        var latest = PerfCaptureDocument.Load(registration.Directory);
        ValidateOwnership(registration, latest);
        latest.Worker = registration.Worker;
        latest.Target = registration.Target;
        if (latest.Finalized && operation == "mark")
        {
            throw new InvalidOperationException("Markers require an active capture. This recording has already ended.");
        }
        return latest.Finalized && latest.StopQpc is not null ? latest : null;
    }

    internal static PerfControlRegistration ReadRegistration(string path)
    {
        if (new FileInfo(path).Length > 16384)
        {
            throw new InvalidDataException("Performance control registration exceeds its size limit.");
        }
        var registration = JsonSerializer.Deserialize(File.ReadAllText(path), PerfJsonContext.Default.PerfControlRegistration)
            ?? throw new InvalidDataException("Empty performance control registration.");
        if (!Guid.TryParseExact(registration.Id, "N", out _) || registration.Credential.Length != 64 ||
            registration.SessionId == Guid.Empty || registration.DurationSec is < 1 or > 300 ||
            registration.MaxSizeMiB is < 1 or > 1024)
        {
            throw new InvalidDataException("Invalid performance control registration.");
        }
        return registration;
    }

    internal static void ValidateOwnership(PerfControlRegistration registration, PerfCaptureDocument capture)
    {
        if (capture.Id != registration.Id || capture.SessionId != registration.SessionId ||
            capture.SessionName != "WinApp-Perf-" + registration.Id ||
            !string.Equals(capture.Directory, registration.Directory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Capture ownership does not match the private control registry. No ETW session was changed.");
        }
    }
}
