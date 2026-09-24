// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services.Performance;

internal sealed class PerfAnalysisManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string ToolVersion { get; set; } = VersionHelper.GetVersionString();
    public int DecoderVersion { get; set; } = 2;
    public int AnalyzerVersion { get; set; } = 3;
    public required string Fingerprint { get; set; }
    public long TargetRecords { get; set; }
    public int Events { get; set; }
    public int Calls { get; set; }
    public int Elements { get; set; }
    public int DecodeErrors { get; set; }
    public int IncompleteCalls { get; set; }
    public int GcIntervals { get; set; }
    public int IncompleteGcIntervals { get; set; }
    public int OmittedPayloadFields { get; set; }
    public uint ReaderEventsLost { get; set; }
    public double? FirstEventMs { get; set; }
    public double? LastEventMs { get; set; }
    public List<string> IncompleteReasons { get; set; } = [];
    public List<string> CaptureReasons { get; set; } = [];
    public Dictionary<string, int> Families { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> CacheHashes { get; set; } = new(StringComparer.Ordinal);
}

internal sealed record PerfAnalysis(PerfCaptureDocument Capture, PerfAnalysisManifest Manifest, string Directory);

internal static class PerfAnalysisStore
{
    private static readonly string[] CacheFiles = ["events.ndjson", "calls.ndjson", "elements.ndjson", "gc.ndjson"];
    private const long MaximumCacheBytes = 256L * 1024 * 1024;

    public static PerfAnalysis Open(string directory, CancellationToken token)
    {
        var capture = PerfCaptureDocument.Load(directory);
        if (!capture.Finalized || capture.Target is null || capture.ReadyQpc is null || capture.TraceFiles.Length == 0)
        {
            throw new InvalidDataException("Stop the capture before analysis. It must contain a target, QPC calibration, and ETL files.");
        }
        if (capture.TraceFiles.Distinct(StringComparer.OrdinalIgnoreCase).Count() != capture.TraceFiles.Length)
        {
            throw new InvalidDataException("The capture repeats an ETL file.");
        }
        using var exclusive = new FileStream(Path.Join(capture.Directory, ".analysis.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var fingerprint = Fingerprint(capture, token);
        var cache = Path.Join(capture.Directory, "analysis");
        var manifestPath = Path.Join(cache, "manifest.json");
        if (File.Exists(manifestPath))
        {
            CheckRegularFile(manifestPath, 262144);
            var existing = JsonSerializer.Deserialize(File.ReadAllText(manifestPath), PerfJsonContext.Default.PerfAnalysisManifest)
                ?? throw new InvalidDataException("The analysis cache manifest is empty.");
            if (existing.SchemaVersion == 1 && existing.DecoderVersion == 2 && existing.AnalyzerVersion == 3 &&
                existing.Fingerprint == fingerprint)
            {
                foreach (var file in CacheFiles)
                {
                    var path = Path.Join(cache, file);
                    CheckRegularFile(path, MaximumCacheBytes);
                    if (!existing.CacheHashes.TryGetValue(file, out var hash) || Hash(path) != hash)
                    {
                        throw new InvalidDataException("Analysis cache content has changed. Remove only the analysis cache directory and retry.");
                    }
                }
                return new(capture, existing, cache);
            }
        }
        var staging = cache + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(staging);
        try
        {
            var manifest = Decode(capture, staging, fingerprint, token);
            foreach (var file in CacheFiles)
            {
                manifest.CacheHashes.Add(file, Hash(Path.Join(staging, file)));
            }
            File.WriteAllText(Path.Join(staging, "manifest.json"),
                JsonSerializer.Serialize(manifest, PerfJsonContext.Default.PerfAnalysisManifest));
            if (Directory.Exists(cache))
            {
                // Never delete files that were not created by this cache format.
                EnsureCacheOnly(cache);
                var old = cache + "." + Guid.NewGuid().ToString("N") + ".old";
                Directory.Move(cache, old);
                try
                {
                    Directory.Move(staging, cache);
                }
                catch
                {
                    Directory.Move(old, cache);
                    throw;
                }
                DeleteCache(old);
            }
            else
            {
                Directory.Move(staging, cache);
            }
            return new(capture, manifest, cache);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                DeleteCache(staging);
            }
        }
    }

    private static PerfAnalysisManifest Decode(PerfCaptureDocument capture, string staging, string fingerprint,
        CancellationToken token)
    {
        var manifest = new PerfAnalysisManifest { Fingerprint = fingerprint };
        var events = new List<PerfEvent>();
        long retainedBytes = 0;
        try
        {
            foreach (var file in capture.TraceFiles)
            {
                var sourceRecord = 0;
                var clock = PerfEtwReader.Read(Path.Join(capture.Directory, file), capture.Target!.Pid,
                    capture.Providers.Select(p => p.Id), raw =>
                    {
                        sourceRecord++;
                        if (++manifest.TargetRecords >= 5_000_000)
                        {
                            throw new PerfAnalysisLimitException("Five-million target-record limit reached.");
                        }
                        var e = raw.Provider == PerfProviders.Clr
                            ? ClrGcEventDecoder.Decode(raw, "v" + manifest.TargetRecords, capture.ReadyQpc!.Value, capture.Frequency)
                            : WinUiEventDecoder.Decode(raw, "v" + manifest.TargetRecords, capture.ReadyQpc!.Value, capture.Frequency);
                        if (e is null)
                        {
                            return;
                        }
                        e = e with { SourceFile = file, TargetRecord = sourceRecord };
                        retainedBytes += JsonSerializer.SerializeToUtf8Bytes(e, PerfJsonContext.Default.PerfEvent).Length;
                        if (events.Count >= 1_000_000 || retainedBytes > 128L * 1024 * 1024)
                        {
                            throw new PerfAnalysisLimitException("Selected-event or 128-MiB retained-evidence limit reached.");
                        }
                        events.Add(e);
                    }, token);
                if (clock.Frequency != capture.Frequency)
                {
                    throw new InvalidDataException("ETL clock frequency does not match capture calibration.");
                }
                manifest.ReaderEventsLost = checked(manifest.ReaderEventsLost + clock.EventsLost);
            }
        }
        catch (PerfAnalysisLimitException ex)
        {
            manifest.IncompleteReasons.Add(ex.Message);
        }
        var ambiguous = capture.TraceFiles.Length > 1
            ? events.GroupBy(e => (e.Qpc, Thread: e.Family == "gc" ? 0 : e.Thread))
                .Where(g => g.Select(e => e.SourceFile).Distinct().Skip(1).Any())
                .SelectMany(g => g.Where(e => e.Phase is "begin" or "end" || e.Family == "gc").Select(e => e.Id))
                .ToHashSet(StringComparer.Ordinal)
            : [];
        using var eventWriter = new StreamWriter(Path.Join(staging, "events.ndjson"), false, new UTF8Encoding(false));
        using var callWriter = new StreamWriter(Path.Join(staging, "calls.ndjson"), false, new UTF8Encoding(false));
        using var gcWriter = new StreamWriter(Path.Join(staging, "gc.ndjson"), false, new UTF8Encoding(false));
        var gcAnalyzer = new PerfGcAnalyzer(interval =>
        {
            gcWriter.WriteLine(JsonSerializer.Serialize(interval, PerfJsonContext.Default.PerfGcInterval));
            manifest.GcIntervals++;
        });
        var analyzer = new PerfAnalyzer(call =>
        {
            callWriter.WriteLine(JsonSerializer.Serialize(call, PerfJsonContext.Default.PerfCall));
            manifest.Calls++;
        });
        try
        {
            foreach (var original in events.OrderBy(e => e.Qpc))
            {
                var e = ambiguous.Contains(original.Id)
                    ? original with { DecodeError = "Unresolved ordering across ETL files.", Phase = "unknown" }
                    : original;
                token.ThrowIfCancellationRequested();
                string? elementId = null;
                if (e.Family == "gc")
                {
                    gcAnalyzer.Accept(e);
                }
                else
                {
                    elementId = analyzer.Accept(e);
                }
                eventWriter.WriteLine(JsonSerializer.Serialize(e with { ElementId = elementId }, PerfJsonContext.Default.PerfEvent));
                manifest.Events++;
                manifest.FirstEventMs ??= e.TimeMs;
                manifest.LastEventMs = e.TimeMs;
                if (e.DecodeError is not null)
                {
                    manifest.DecodeErrors++;
                }
                else
                {
                    manifest.Families[e.Family] = manifest.Families.GetValueOrDefault(e.Family) + 1;
                }
                manifest.OmittedPayloadFields += e.OmittedFields;
            }
        }
        catch (PerfAnalysisLimitException ex)
        {
            manifest.IncompleteReasons.Add(ex.Message);
        }
        analyzer.Complete();
        gcAnalyzer.Complete();
        manifest.IncompleteGcIntervals = gcAnalyzer.IncompleteIntervals;
        using (var elementWriter = new StreamWriter(Path.Join(staging, "elements.ndjson"), false, new UTF8Encoding(false)))
        {
            foreach (var element in analyzer.Elements)
            {
                elementWriter.WriteLine(JsonSerializer.Serialize(element, PerfJsonContext.Default.PerfElement));
            }
        }
        manifest.Elements = analyzer.Elements.Count;
        manifest.IncompleteCalls = analyzer.IncompleteCalls;
        if (capture.State != "completed")
        {
            manifest.IncompleteReasons.Add(capture.Error ?? "The capture did not complete normally.");
        }
        if (capture.EventsLost != 0 || capture.BuffersLost != 0 || manifest.ReaderEventsLost != 0)
        {
            manifest.IncompleteReasons.Add("ETW loss is nonzero or unknown.");
        }
        if (capture.TargetExited)
        {
            manifest.IncompleteReasons.Add("The app exited before trace finalization; its final buffered events may be missing.");
        }
        if (capture.Runtime is null && capture.Providers.Any(p => p.Id == PerfProviders.Xaml))
        {
            manifest.IncompleteReasons.Add("WinUI runtime metadata was not observed; runtime provenance is incomplete.");
        }
        manifest.CaptureReasons.AddRange(manifest.IncompleteReasons);
        if (manifest.DecodeErrors > 0)
        {
            manifest.IncompleteReasons.Add("Selected events have unsupported descriptors or malformed payloads.");
        }
        if (manifest.IncompleteCalls > 0)
        {
            manifest.IncompleteReasons.Add("Some scope boundaries are missing or inconsistent; their durations are unavailable.");
        }
        if (manifest.IncompleteGcIntervals > 0)
        {
            manifest.IncompleteReasons.Add("Some CLR collection/suspension boundaries are incomplete.");
        }
        return manifest;
    }

    public static IEnumerable<T> Read<T>(PerfAnalysis analysis, string file, JsonTypeInfo<T> type)
    {
        if (!CacheFiles.Contains(file, StringComparer.Ordinal))
        {
            throw new ArgumentException("Unknown cache artifact.");
        }
        var path = Path.Join(analysis.Directory, file);
        CheckRegularFile(path, MaximumCacheBytes);
        using var reader = new StreamReader(path, new UTF8Encoding(false, true));
        var chars = new char[65536];
        var count = 0;
        while (!reader.EndOfStream)
        {
            var length = 0;
            int ch;
            while ((ch = reader.Read()) >= 0 && ch != '\n')
            {
                if (length == chars.Length)
                {
                    throw new InvalidDataException("Oversized analysis cache record.");
                }
                chars[length++] = (char)ch;
            }
            if (++count > 1_000_000)
            {
                throw new InvalidDataException("Too many analysis cache records.");
            }
            yield return JsonSerializer.Deserialize(chars.AsSpan(0, length), type)
                ?? throw new InvalidDataException("Empty analysis cache record.");
        }
    }

    private static string Fingerprint(PerfCaptureDocument capture, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes("decoder:2;analyzer:3;" + VersionHelper.GetVersionString()));
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(capture, PerfJsonContext.Default.PerfCaptureDocument));
        long total = 0;
        foreach (var file in capture.TraceFiles)
        {
            token.ThrowIfCancellationRequested();
            var path = Path.Join(capture.Directory, file);
            CheckRegularFile(path, 1024L * 1024 * 1024);
            total += new FileInfo(path).Length;
            if (total > 1024L * 1024 * 1024 + 2 * 1024 * 1024)
            {
                throw new InvalidDataException("Capture ETL files exceed the one-GiB input limit.");
            }
            hash.AppendData(Encoding.UTF8.GetBytes(file));
            hash.AppendData(Convert.FromHexString(Hash(path)));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    private static void CheckRegularFile(string path, long maxBytes)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length > maxBytes ||
            (file.Attributes & FileAttributes.ReparsePoint) != 0 ||
            (file.Directory!.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Missing, oversized, or redirected performance artifact.");
        }
    }

    internal static void EnsureCacheOnly(string directory)
    {
        if (PathSafety.IsReparsePoint(directory))
        {
            throw new IOException("The analysis cache directory cannot be a junction, symbolic link, or network path.");
        }
        if (Directory.EnumerateDirectories(directory).Any() ||
            Directory.EnumerateFiles(directory).Any(p => Path.GetFileName(p) != "manifest.json" &&
                !CacheFiles.Contains(Path.GetFileName(p), StringComparer.Ordinal)))
        {
            throw new IOException("The analysis directory contains files not owned by the performance cache.");
        }
    }

    private static void DeleteCache(string directory)
    {
        EnsureCacheOnly(directory);
        foreach (var file in CacheFiles.Append("manifest.json"))
        {
            File.Delete(Path.Join(directory, file));
        }
        Directory.Delete(directory);
    }
}
