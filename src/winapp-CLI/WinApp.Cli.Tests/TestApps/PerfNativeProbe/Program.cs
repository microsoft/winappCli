// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

extern alias winappcli;

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using WinApp.Cli.Services;
using WinApp.Cli.Services.Performance;
using Native = winappcli::Windows.Win32.PInvoke;
using Etw = winappcli::Windows.Win32.System.Diagnostics.Etw;
using NativeError = winappcli::Windows.Win32.Foundation.WIN32_ERROR;

internal static class Program
{
    private static readonly Guid Xaml = new("531A35AB-63CE-4BCF-AA98-F88C7A89E455");

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == PerfCaptureWorker.InternalVerb)
            {
                return await PerfCaptureWorker.RunAsync(args,
                    args.Length == 2 ? new LoggingEtwApi(Path.ChangeExtension(args[1], ".native.log")) : null);
            }
            if (args.Length == 3 && args[0] is "worker" or "stop" or "launch")
            {
                var directories = new WinappDirectoryService(new CurrentDirectoryProvider(Environment.CurrentDirectory));
                directories.SetCacheDirectoryForTesting(new DirectoryInfo(Path.GetFullPath(args[2]) + ".control"));
                var service = new PerfCaptureService(directories,
                    new AppLauncherService(NullLogger<AppLauncherService>.Instance));
                PerfCaptureDocument controlledCapture;
                if (args[0] is "worker" or "launch")
                {
                    var registration = await service.PrepareAsync(args[2], args[0] == "launch" ? 4 : 30, 32, CancellationToken.None);
                    var targetPid = args[0] == "launch"
                        ? checked((int)new AppLauncherService(NullLogger<AppLauncherService>.Instance).LaunchByAumid(args[1]))
                        : int.Parse(args[1]);
                    using var target = Process.GetProcessById(targetPid);
                    controlledCapture = await PerfCaptureService.BindAsync(registration, PerfProcessIdentity.Read(target),
                        "attached; startup not recorded", false, CancellationToken.None);
                }
                else
                {
                    controlledCapture = await service.ControlAsync(args[1], "stop", null, CancellationToken.None);
                }
                Console.WriteLine(JsonSerializer.Serialize(controlledCapture, PerfJsonContext.Default.PerfCaptureDocument));
                return controlledCapture.State is "recording" or "completed" ? 0 : 1;
            }
            if (args.Length != 3 || args[0] is not ("capture" or "fixture" or "gc"))
            {
                throw new ArgumentException("Usage: capture|worker <pid> <empty-directory> | fixture 0 <empty-directory> | launch <aumid> <empty-directory> | stop <capture-id> <capture-directory>");
            }
            var pid = args[0] == "fixture" ? Environment.ProcessId : int.Parse(args[1]);
            var directory = Directory.CreateDirectory(Path.GetFullPath(args[2]));
            if (directory.EnumerateFileSystemInfos().Any())
            {
                throw new IOException("The probe requires an empty output directory.");
            }
            var provider = args[0] == "fixture" ? Guid.NewGuid() :
                args[0] == "gc" ? new Guid("e13c0d23-ccbc-4e12-931b-d9cc2eee27e4") : Xaml;
            using var process = Process.GetProcessById(pid);
            var capture = new PerfCaptureDocument
            {
                Id = Guid.NewGuid().ToString("N"), Directory = directory.FullName,
                SessionName = "WinApp-Perf-Probe-" + Guid.NewGuid().ToString("N"), SessionId = Guid.NewGuid(),
                Target = PerfProcessIdentity.Read(process), StartupCoverage = "Native test fixture; not WinUI startup coverage",
                Providers = args[0] == "gc" ? [PerfProviders.All.Single(p => p.Id == PerfProviders.Clr)] : [],
            };
            using (var session = new PrivateEtwSession(capture.SessionName,
                capture.SessionId, pid, Path.Join(directory.FullName, "trace.etl"), 16))
            {
                var filtered = session.Enable(provider, args[0] == "gc" ? 1UL : ulong.MaxValue,
                    args[0] == "gc" ? (byte)4 : (byte)5, args[0] == "gc" ? [1, 2, 3, 7, 8, 9] : null);
                capture.ProviderStates.Add(new(provider, "enabled", filtered));
                capture.ReadyQpc = Stopwatch.GetTimestamp();
                File.WriteAllText(Path.Join(directory.FullName, "ready"), capture.ReadyQpc.ToString());
                if (args[0] == "fixture")
                {
                    Emit(provider);
                }
                else
                {
                    Thread.Sleep(TimeSpan.FromSeconds(15));
                }
                session.Stop();
                capture.StopQpc = Stopwatch.GetTimestamp();
                capture.EventsLost = session.EventsLost;
                capture.BuffersLost = session.BuffersLost;
                if (!session.Stopped || session.EventsLost != 0 || session.BuffersLost != 0)
                {
                    throw new InvalidDataException("Capture did not stop with known zero loss.");
                }
            }
            if (args[0] == "gc")
            {
                capture.TraceFiles = directory.GetFiles("trace.etl*").Select(f => f.Name).ToArray();
                capture.State = "completed";
                capture.Save();
                var analysis = PerfAnalysisStore.Open(directory.FullName, CancellationToken.None);
                var gc = PerfQuery.Execute(analysis, new(View: "gc", Limit: 100));
                if (analysis.Manifest.DecodeErrors != 0 ||
                    !gc.Rows.Any(r => r.GcInterval is { IsGcSuspension: true, DurationMs: not null }))
                {
                    throw new InvalidDataException("The native CLR payloads did not produce usable GC suspension analysis.");
                }
            }
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var samples = new Dictionary<string, string>(StringComparer.Ordinal);
            var total = 0;
            var decoded = 0;
            var errors = 0;
            foreach (var file in directory.GetFiles("trace.etl*"))
            {
                var clock = PerfEtwReader.Read(file.FullName, pid, [provider], e =>
                {
                    total++;
                    if (e.TraceLogging && e.DecodeError is null)
                    {
                        decoded++;
                    }
                    if (e.DecodeError is not null)
                    {
                        errors++;
                    }
                    var key = $"{e.EventId}/{e.Version}/{e.Opcode}/{e.Payload.Length} {e.EventName} {e.DecodeError}";
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                    if (args[0] == "gc")
                    {
                        samples.TryAdd(key, Convert.ToHexString(e.Payload.AsSpan(0, Math.Min(64, e.Payload.Length))));
                    }
                    if (counts.Count > 2048)
                    {
                        throw new InvalidDataException("Too many descriptors for this probe.");
                    }
                    if (args[0] == "fixture" &&
                        (e.EventId != 42 || !e.Payload.AsSpan().SequenceEqual(new byte[] { 42, 0, 0, 0 })))
                    {
                        throw new InvalidDataException("Native fixture payload did not round-trip.");
                    }
                });
                if (clock.Frequency != Stopwatch.Frequency || clock.EventsLost != 0)
                {
                    throw new InvalidDataException("Unexpected ETL clock or reader loss.");
                }
            }
            var report = new ProbeReport(total, decoded, errors, counts.OrderByDescending(p => p.Value)
                .Take(30).ToDictionary(), samples);
            Console.WriteLine(JsonSerializer.Serialize(report, ProbeJsonContext.Default.ProbeReport));
            File.WriteAllText(Path.Join(directory.FullName, "done"), "");
            return total > 0 && (args[0] is "fixture" or "gc" || decoded > 0) ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static unsafe void Emit(Guid provider)
    {
        Etw.REGHANDLE registration;
        Check(Native.EventRegister(in provider, null, null, &registration));
        try
        {
            var descriptor = new Etw.EVENT_DESCRIPTOR { Id = 42, Level = 4 };
            uint value = 42;
            var data = new Etw.EVENT_DATA_DESCRIPTOR { Ptr = (ulong)&value, Size = sizeof(uint) };
            Check(Native.EventWrite(registration, &descriptor, 1, &data));
        }
        finally
        {
            Check(Native.EventUnregister(registration));
        }
    }

    private static void Check(uint status)
    {
        if (status != 0)
        {
            throw new System.ComponentModel.Win32Exception((int)status);
        }
    }
}

internal sealed unsafe class LoggingEtwApi(string? path) : IPrivateEtwApi
{
    private void Log(string message)
    {
        if (path is null)
        {
            Console.WriteLine(message);
        }
        else
        {
            File.AppendAllText(path, message + Environment.NewLine);
        }
    }

    public NativeError Start(ulong* handle, char* name, Etw.EVENT_TRACE_PROPERTIES* properties)
    {
        var result = NativePrivateEtwApi.Instance.Start(handle, name, properties);
        Log($"Start: {(uint)result}; handle: {*handle:X}; context: {properties->Wnode.Anonymous1.HistoricalContext:X}; mode: {properties->LogFileMode:X}");
        return result;
    }

    public NativeError Enable(ulong handle, Guid* provider, byte level, ulong keywords, Etw.ENABLE_TRACE_PARAMETERS* parameters)
    {
        var result = NativePrivateEtwApi.Instance.Enable(handle, provider, level, keywords, parameters);
        Log($"Enable: {(uint)result}; handle: {handle:X}");
        return result;
    }

    public NativeError Stop(ulong handle, char* name, Etw.EVENT_TRACE_PROPERTIES* properties)
    {
        var result = NativePrivateEtwApi.Instance.Stop(handle, name, properties);
        Log($"Stop: {(uint)result}; context: {properties->Wnode.Anonymous1.HistoricalContext:X}; mode: {properties->LogFileMode:X}");
        return result;
    }
}

internal sealed record ProbeReport(int Events, int TraceLoggingDecoded, int DecodeErrors,
    Dictionary<string, int> Descriptors, Dictionary<string, string> PayloadSamples);

[JsonSerializable(typeof(ProbeReport))]
internal sealed partial class ProbeJsonContext : JsonSerializerContext;
