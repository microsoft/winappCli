// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services.Performance;

internal sealed record PerfProcessIdentity(int Pid, DateTime CreationUtc)
{
    public static PerfProcessIdentity Read(Process process) => new(process.Id, process.StartTime.ToUniversalTime());

    public Process Open()
    {
        var process = Process.GetProcessById(Pid);
        if (Read(process) != this)
        {
            process.Dispose();
            throw new InvalidOperationException("The process ID has been reused by another process.");
        }
        return process;
    }
}

internal sealed record PerfMarker(string Name, [property: JsonNumberHandling(JsonNumberHandling.WriteAsString |
    JsonNumberHandling.AllowReadingFromString)] long Qpc);

internal sealed record PerfProvider(Guid Id, string Name, string Keywords, byte Level, ushort[]? EventIds = null,
    bool Optional = false);
internal sealed record PerfProviderState(Guid Id, string State, bool? EventIdFilterApplied, string? Error = null);
internal sealed record PerfRuntime(string Path, string? FileVersion, string? ProductVersion, string? Architecture);

internal sealed class PerfCaptureDocument
{
    public int SchemaVersion { get; set; } = 1;
    public required string Id { get; set; }
    public required string Directory { get; set; }
    public Guid SessionId { get; set; }
    public required string SessionName { get; set; }
    public string State { get; set; } = "starting";
    public string? Error { get; set; }
    public List<string> Warnings { get; set; } = [];
    public string? LastControlError { get; set; }
    public string? StopReason { get; set; }
    public string StartupCoverage { get; set; } = "attached; startup not recorded";
    public bool DebuggerAttached { get; set; }
    public int DurationSec { get; set; } = 30;
    public int MaxSizeMiB { get; set; } = 128;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ReadyUtc { get; set; }
    public DateTime? StoppedUtc { get; set; }
    public PerfProcessIdentity? Target { get; set; }
    public PerfProcessIdentity? Worker { get; set; }
    public PerfRuntime? Runtime { get; set; }
    public List<PerfRuntime> ManagedRuntimes { get; set; } = [];
    public string? RuntimeProbeError { get; set; }
    public string WindowsVersion { get; set; } = Environment.OSVersion.VersionString;
    public string CollectorVersion { get; set; } = VersionHelper.GetVersionString();
    [JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
    public long Frequency { get; set; } = Stopwatch.Frequency;
    [JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
    public long? ReadyQpc { get; set; }
    [JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
    public long? StopQpc { get; set; }
    public uint? EventsLost { get; set; }
    public uint? BuffersLost { get; set; }
    public bool TargetExited { get; set; }
    public List<PerfProviderState> ProviderStates { get; set; } = [];
    public PerfProvider[] Providers { get; set; } = PerfProviders.All;
    public List<PerfMarker> Markers { get; set; } = [];
    public string[] TraceFiles { get; set; } = [];
    [JsonIgnore]
    public bool Finalized => State is "completed" or "failed";

    public void Save() => AtomicFile.WriteAllText(Path.Join(Directory, "capture.json"),
        JsonSerializer.Serialize(this, PerfJsonContext.Default.PerfCaptureDocument));

    public static PerfCaptureDocument Load(string directory)
    {
        var file = new FileInfo(Path.Join(Path.GetFullPath(directory), "capture.json"));
        if (!file.Exists || file.Length > 262144)
        {
            throw new InvalidDataException("A capture.json file of at most 256 KiB is required.");
        }
        var capture = JsonSerializer.Deserialize(File.ReadAllText(file.FullName), PerfJsonContext.Default.PerfCaptureDocument)
            ?? throw new InvalidDataException("Empty capture manifest.");
        if (capture.SchemaVersion != 1 || !Guid.TryParseExact(capture.Id, "N", out _) ||
            capture.DurationSec is < 1 or > 300 || capture.MaxSizeMiB is < 1 or > 1024 ||
            capture.Frequency <= 0 || capture.Markers.Count > 256 || capture.TraceFiles.Length > 32)
        {
            throw new InvalidDataException("Unsupported or invalid capture manifest.");
        }
        foreach (var path in capture.TraceFiles)
        {
            if (string.IsNullOrEmpty(path) || Path.GetFileName(path) != path ||
                path.Contains(':') || !path.StartsWith("trace.etl", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Invalid ETL artifact path.");
            }
        }
        capture.Directory = file.DirectoryName!;
        return capture;
    }
}

internal static class PerfProviders
{
    public static readonly Guid Xaml = new("531A35AB-63CE-4BCF-AA98-F88C7A89E455");
    public static readonly Guid Diagnostics = new("59E7A714-73A4-4147-B47E-0957048C75C4");
    public static readonly Guid Operational = new("2DC72F6E-E4D1-5F58-3245-09A4243799DD");
    public static readonly Guid Controls = new("F55F7011-988D-4674-A724-E01B39DC7AF6");
    public static readonly Guid Clr = new("e13c0d23-ccbc-4e12-931b-d9cc2eee27e4");

    public static readonly PerfProvider[] All =
    [
        new(Xaml, "Microsoft-Windows-XAML", "fffffffffff094c5", 5),
        new(Operational, "Microsoft.UI.Xaml", "ffffffffffffffff", 5),
        new(Controls, "Microsoft.UI.Xaml.Controls.Perf", "ffffffffffffffff", 4),
        new(Diagnostics, "Microsoft-Windows-XAML-Diagnostics", "ffffffffffffffff", 5,
            [1, 2, 26, 27, 59, 60, 61, 62, 64, 65, 66, 67, 83]),
        new(Clr, "Microsoft-Windows-DotNETRuntime", "1", 4, [1, 2, 3, 7, 8, 9], Optional: true),
    ];
}

internal sealed record PerfControlRequest(string Credential, string Operation,
    PerfProcessIdentity? Target = null, string? Name = null, string? StartupCoverage = null, bool DebuggerAttached = false);
internal sealed record PerfControlResponse(PerfCaptureDocument? Capture, string? Error = null);
internal sealed record PerfControlRegistration(string Id, string Directory, string Credential,
    Guid SessionId, int DurationSec, int MaxSizeMiB, PerfProcessIdentity? Worker = null, PerfProcessIdentity? Target = null);
internal sealed record PerfCommandError(string Code, string Message, bool PartialOutput);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PerfCaptureDocument))]
[JsonSerializable(typeof(PerfControlRequest))]
[JsonSerializable(typeof(PerfControlResponse))]
[JsonSerializable(typeof(PerfControlRegistration))]
[JsonSerializable(typeof(PerfEvent))]
[JsonSerializable(typeof(PerfCall))]
[JsonSerializable(typeof(PerfGcInterval))]
[JsonSerializable(typeof(PerfElement))]
[JsonSerializable(typeof(PerfAnalysisManifest))]
[JsonSerializable(typeof(PerfQueryResult))]
[JsonSerializable(typeof(PerfCommandError))]
internal sealed partial class PerfJsonContext : JsonSerializerContext;
