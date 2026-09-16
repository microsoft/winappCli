// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;

namespace WinApp.Cli.Services.Performance;

internal sealed class PerfGcInterval
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public ushort ClrInstanceId { get; init; }
    public uint? Count { get; init; }
    public uint? Generation { get; init; }
    public uint? Reason { get; init; }
    public string? CollectionType { get; init; }
    public double? StartMs { get; init; }
    public double? EndMs { get; set; }
    public double? SuspendCompleteMs { get; set; }
    public double? RestartBeginMs { get; set; }
    public string Status { get; set; } = "complete";
    public List<string> Evidence { get; init; } = [];
    public bool IsGcSuspension => Kind == "suspension" && Reason is 1 or 6;
    public double? DurationMs => Status == "complete" && StartMs is { } start && EndMs is { } end ? end - start : null;
    public double? FullySuspendedMs => Status == "complete" && SuspendCompleteMs is { } start &&
        RestartBeginMs is { } end ? end - start : null;
}

internal sealed class PerfGcAnalyzer(Action<PerfGcInterval> emit)
{
    private readonly Dictionary<(ushort Instance, uint Count), PerfGcInterval> collections = [];
    private readonly Dictionary<ushort, PerfGcInterval> suspensions = [];
    private int sequence;
    public int IncompleteIntervals { get; private set; }

    public void Accept(PerfEvent e)
    {
        if (e.DecodeError is not null)
        {
            foreach (var interval in collections.Values.Concat(suspensions.Values))
            {
                interval.Status = "unsupported-event";
            }
            return;
        }
        var instance = checked((ushort)Number(e, "ClrInstanceID")!.Value);
        if (e.Name == "GCStart")
        {
            var key = (instance, Number(e, "Count")!.Value);
            if (collections.Remove(key, out var previous))
            {
                Finish(previous, "missing-end");
            }
            collections.Add(key, New(e, instance, "collection"));
        }
        else if (e.Name == "GCEnd")
        {
            var key = (instance, Number(e, "Count")!.Value);
            if (!collections.Remove(key, out var collection))
            {
                collection = New(e, instance, "collection", missingStart: true);
            }
            collection.Evidence.Add(e.Id);
            collection.EndMs = e.TimeMs;
            if (collection.Generation != Number(e, "Depth"))
            {
                collection.Status = "inconsistent-generation";
            }
            Finish(collection);
        }
        else if (e.Name == "GCSuspendEEBegin")
        {
            if (suspensions.Remove(instance, out var previous))
            {
                Finish(previous, "missing-restart");
            }
            suspensions.Add(instance, New(e, instance, "suspension"));
        }
        else
        {
            if (!suspensions.TryGetValue(instance, out var pause))
            {
                pause = New(e, instance, "suspension", missingStart: true);
                suspensions.Add(instance, pause);
            }
            if (pause.Evidence.Count < 8)
            {
                pause.Evidence.Add(e.Id);
            }
            switch (e.Name)
            {
                case "GCSuspendEEEnd":
                    if (pause.SuspendCompleteMs is not null || pause.RestartBeginMs is not null)
                    {
                        pause.Status = "inconsistent-boundaries";
                    }
                    pause.SuspendCompleteMs = e.TimeMs;
                    break;
                case "GCRestartEEBegin":
                    if (pause.SuspendCompleteMs is null || pause.RestartBeginMs is not null)
                    {
                        pause.Status = "inconsistent-boundaries";
                    }
                    pause.RestartBeginMs = e.TimeMs;
                    break;
                case "GCRestartEEEnd":
                    if (pause.SuspendCompleteMs is null || pause.RestartBeginMs is null)
                    {
                        pause.Status = "inconsistent-boundaries";
                    }
                    pause.EndMs = e.TimeMs;
                    suspensions.Remove(instance);
                    Finish(pause);
                    break;
            }
        }
        if (collections.Count > 4096 || suspensions.Count > 64)
        {
            throw new PerfAnalysisLimitException("Too many active CLR collections or runtime instances.");
        }
    }

    public void Complete()
    {
        foreach (var interval in collections.Values.Concat(suspensions.Values))
        {
            Finish(interval, "missing-end");
        }
        collections.Clear();
        suspensions.Clear();
    }

    private PerfGcInterval New(PerfEvent e, ushort instance, string kind, bool missingStart = false) => new()
    {
        Id = "gc" + ++sequence, Kind = kind, ClrInstanceId = instance,
        Count = Number(e, "Count"), Generation = Number(e, "Depth"), Reason = Number(e, "Reason"),
        CollectionType = Number(e, "Type") switch
        {
            0 => "blocking", 1 => "background", 2 => "blocking-during-background",
            null => null, var value => "unknown:" + value.Value.ToString(CultureInfo.InvariantCulture),
        },
        StartMs = missingStart ? null : e.TimeMs,
        Status = missingStart ? "missing-start" : "complete",
        Evidence = missingStart ? [] : [e.Id],
    };

    private void Finish(PerfGcInterval interval, string? incomplete = null)
    {
        if (incomplete is not null)
        {
            interval.Status = incomplete;
        }
        if (interval.StartMs > interval.EndMs ||
            interval.SuspendCompleteMs < interval.StartMs ||
            interval.RestartBeginMs < interval.SuspendCompleteMs ||
            interval.EndMs < interval.RestartBeginMs)
        {
            interval.Status = "inconsistent-boundaries";
        }
        if (interval.Status != "complete")
        {
            IncompleteIntervals++;
        }
        emit(interval);
    }

    private static uint? Number(PerfEvent e, string name) => e.Fields.TryGetValue(name, out var value)
        ? uint.Parse(value, CultureInfo.InvariantCulture) : null;
}
