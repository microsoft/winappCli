// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

namespace WinApp.Cli.Services.Performance;

internal sealed record PerfQueryOptions(string View = "summary", int Limit = 10, int Offset = 0, int MaxBytes = 16384,
    double? FromMs = null, double? ToMs = null, string? FromMarker = null, string? ToMarker = null,
    string? Id = null, string? Type = null, string? Element = null, uint? Thread = null,
    string? Provider = null, string? Event = null, string Sort = "self", int? Depth = null, string? Family = null,
    double? MinFrameMs = null);
internal sealed record PerfRange(double FromMs, double ToMs);
internal sealed record PerfCoverage(bool Complete, string Startup, int DecodeErrors, int IncompleteCalls,
    uint? EventsLost, uint? BuffersLost, Dictionary<string, int> Families, string[] Reasons,
    int CaptureDecodeErrors = 0, int CaptureIncompleteCalls = 0, string[]? CaptureReasons = null);
internal sealed record PerfGcCoverage(string Availability, int RecordedEvents, int DecodeErrors,
    int IncompleteIntervals, bool Complete, string? Error = null);
internal sealed record PerfGcOverlap(double ObservedOverlapMs, string[] IntervalIds, int OmittedIntervals);
internal sealed record PerfActivityRow(string Category, double ObservedMs, double? RangePercent);

internal sealed class PerfParsingResource
{
    public required string Resource { get; set; }
    public double ObservedMs { get; init; }
    public int Count { get; init; }
    public bool Projected { get; set; }
}

internal sealed class PerfHotspotOperation
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public required string Family { get; init; }
    public double DurationMs { get; init; }
    public double? ExclusiveMs { get; init; }
    public string[] Evidence { get; set; } = [];
}

internal sealed class PerfQueryRow
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public string? Name { get; set; }
    public int? Count { get; init; }
    public int? BoundaryOverlaps { get; init; }
    public double? ClippedOverlapMs { get; init; }
    public double? InclusiveMs { get; init; }
    public double? SelfMs { get; init; }
    public double? ExclusiveMs { get; init; }
    public int? Depth { get; init; }
    public int? ChildCount { get; init; }
    public bool DepthLimited { get; init; }
    public string[] AncestorIds { get; init; } = [];
    public double? MeanMs { get; init; }
    public double? MaxMs { get; init; }
    public double? P95Ms { get; init; }
    public PerfElement? Element { get; set; }
    public PerfEvent? Event { get; set; }
    public PerfCall? Call { get; set; }
    public PerfGcInterval? GcInterval { get; set; }
    public PerfGcOverlap? GcOverlap { get; set; }
    public List<PerfHotspotOperation>? DominantOperations { get; set; }
    public string[] Evidence { get; set; } = [];
    public bool Projected { get; set; }
}

internal sealed class PerfQueryResult
{
    public int SchemaVersion { get; init; } = 1;
    public required string CaptureId { get; init; }
    public required string View { get; init; }
    public required PerfRange Range { get; init; }
    public required PerfCoverage Coverage { get; init; }
    public required List<PerfQueryRow> Rows { get; init; }
    public int Returned => Rows.Count;
    public int Total { get; init; }
    public int Offset { get; init; }
    public int? NextOffset { get; set; }
    public bool ByteBudgetLimited { get; set; }
    public string? RootCallId { get; init; }
    public PerfGcCoverage? GcCoverage { get; init; }
    public uint? PrimaryUiThread { get; init; }
    public List<PerfActivityRow>? Activity { get; init; }
    public List<PerfParsingResource>? ParsingResources { get; init; }
    public int? ParsingResourcesTotal { get; init; }
    public int? ParsingResourcesOmitted { get; set; }
    public Dictionary<uint, double> LayoutBusyMsByThread { get; init; } = [];
    public string[] Limitations { get; set; } =
    [
        "Durations and self time are elapsed time, not CPU time. Inclusive totals overlap.",
        "Frames are UI-side phases, not presented frames or FPS. Temporal proximity is not causation.",
        "Missing layout during compositor scrolling is inconclusive. CPU, kernel waits and GPU are not recorded.",
        "Element IDs and addresses are trace-local; parents/source are observed metadata, not a complete visual tree or UIA mapping.",
        "Mean/max/p95 use complete calls wholly inside the range; boundary overlaps are reported separately.",
        "Call trees are instrumented operations, not CPU stacks. Exclusive time subtracts child spans; element self time has separate accounting.",
        "GC overlap is observed runtime suspension, not proven UI blocking or causation. Background collection lifetime is not a pause; partial overlap is a lower bound.",
        "ETL can contain names, paths and other app data. A compact query does not sanitize the raw capture.",
    ];
}

internal static class PerfQuery
{
    private sealed record ActivityInterval(PerfCall Call, string Category, int Depth, string? Resource);
    private sealed record ActivityBoundary(double Time, bool IsStart, ActivityInterval Interval);
    private sealed record ActivityAnalysis(List<PerfActivityRow> Categories, List<PerfParsingResource> Resources);

    private sealed class Aggregate(string id, string kind, string name, PerfElement? element = null)
    {
        public string Id { get; } = id;
        public string Kind { get; } = kind;
        public string Name { get; } = name;
        public PerfElement? Element { get; } = element;
        public List<double> Durations { get; } = [];
        public List<string> Evidence { get; } = [];
        public double Inclusive { get; set; }
        public double Self { get; set; }
        public int Overlaps { get; set; }
        public double Clipped { get; set; }

        public PerfQueryRow Row()
        {
            Durations.Sort();
            return new()
            {
                Id = Id, Kind = Kind, Name = Name, Element = Element, Count = Durations.Count,
                InclusiveMs = Inclusive, SelfMs = Kind == "element" ? Self : null,
                ExclusiveMs = Kind == "phase" ? Self : null, BoundaryOverlaps = Overlaps, ClippedOverlapMs = Clipped,
                MeanMs = Durations.Count > 0 ? Inclusive / Durations.Count : null,
                MaxMs = Durations.Count > 0 ? Durations[^1] : null,
                P95Ms = Durations.Count > 0 ? Durations[(int)Math.Ceiling(Durations.Count * .95) - 1] : null,
                Evidence = Evidence.ToArray(),
            };
        }
    }

    public static PerfQueryResult Execute(PerfAnalysis analysis, PerfQueryOptions options)
    {
        Validate(options);
        var manifest = analysis.Manifest;
        var hasRequestedEvidence = options.View switch
        {
            "events" => manifest.Events > 0,
            "gc" => manifest.Events > 0,
            _ => manifest.Families.Any(p => p.Key is not ("metadata" or "unknown" or "gc") && p.Value > 0),
        };
        if (!hasRequestedEvidence)
        {
            throw new InvalidDataException("No usable requested performance evidence was recorded. Exercise the UI while recording and retry.");
        }
        var calls = PerfAnalysisStore.Read(analysis, "calls.ndjson", PerfJsonContext.Default.PerfCall).ToList();
        var gc = PerfAnalysisStore.Read(analysis, "gc.ndjson", PerfJsonContext.Default.PerfGcInterval).ToList();
        var root = options.View == "call" ? calls.SingleOrDefault(c => c.Id == options.Id)
            ?? throw new ArgumentException("The call ID was not observed in this capture.") : null;
        var selectedGc = options.View == "gc" && options.Id is not null
            ? gc.SingleOrDefault(g => g.Id == options.Id) ?? throw new ArgumentException("The GC interval ID was not observed in this capture.") : null;
        var from = Marker(analysis, options.FromMarker) ?? options.FromMs ?? root?.StartMs ??
            selectedGc?.StartMs ?? Math.Min(0, manifest.FirstEventMs!.Value);
        var to = Marker(analysis, options.ToMarker) ?? options.ToMs ??
            root?.EndMs ?? selectedGc?.EndMs ??
            (analysis.Capture.StopQpc is { } stopped ?
                (stopped - analysis.Capture.ReadyQpc!.Value) * 1000.0 / analysis.Capture.Frequency : manifest.LastEventMs!.Value);
        if (!double.IsFinite(from) || !double.IsFinite(to) || from > to)
        {
            throw new ArgumentException("The time range must be finite and ordered.");
        }
        var range = new PerfRange(from, to);
        if (root is not null && (!Overlaps(root.StartMs, root.EndMs, range) ||
            options.Thread is { } rootThread && root.Thread != rootThread))
        {
            throw new ArgumentException("The requested call does not overlap the selected range/thread.");
        }
        var selectedThread = options.Thread ?? root?.Thread;
        uint? primaryUiThread = null;
        if (options.View is "summary" or "parsing")
        {
            primaryUiThread = SelectPrimaryUiThread(calls, range, options.Thread);
            if (options.View == "parsing")
            {
                selectedThread = primaryUiThread;
            }
        }
        var elements = PerfAnalysisStore.Read(analysis, "elements.ndjson", PerfJsonContext.Default.PerfElement)
            .ToDictionary(e => e.Id, StringComparer.Ordinal);
        var rows = new List<PerfQueryRow>();
        var busy = new Dictionary<uint, List<(double Start, double End)>>();
        var families = new Dictionary<string, int>(StringComparer.Ordinal);
        var decodeErrors = 0;
        var gcErrors = 0;
        var gcRecorded = 0;
        var gcDecoded = 0;
        var parsingEvents = new Dictionary<string, PerfEvent>(StringComparer.Ordinal);
        foreach (var e in PerfAnalysisStore.Read(analysis, "events.ndjson", PerfJsonContext.Default.PerfEvent))
        {
            if (e.Family == "parsing" && e.Phase == "begin" &&
                (primaryUiThread is null || e.Thread == primaryUiThread))
            {
                parsingEvents[e.Id] = e;
            }
            if (e.Family == "gc")
            {
                gcRecorded++;
                if (e.DecodeError is null)
                {
                    gcDecoded++;
                }
                else if (e.TimeMs >= from && e.TimeMs <= to)
                {
                    gcErrors++;
                }
            }
            if (e.TimeMs < from || e.TimeMs > to || selectedThread is { } tid && e.Thread != tid ||
                options.View == "gc" && e.Family != "gc" ||
                options.View is not ("gc" or "events") && e.Family == "gc" ||
                options.Family is not null && e.Family != options.Family ||
                options.Element is not null && e.ElementId != options.Element ||
                options.Event is not null && e.Name != options.Event && e.Id != options.Event ||
                options.Provider is not null && e.Provider != Guid.Parse(options.Provider))
            {
                continue;
            }
            if (e.DecodeError is not null)
            {
                decodeErrors++;
            }
            else
            {
                families[e.Family] = families.GetValueOrDefault(e.Family) + 1;
            }
            if (options.View == "events")
            {
                rows.Add(new() { Id = e.Id, Kind = "event", Event = e });
            }
        }
        var gcIncomplete = gc.Count(g => g.Status != "complete" && Overlaps(g.StartMs, g.EndMs, range));
        var gcState = analysis.Capture.ProviderStates.FirstOrDefault(p => p.Id == PerfProviders.Clr);
        var gcAvailability = gcRecorded > 0 ? gcDecoded > 0 ? "observed" : "unsupported" :
            !analysis.Capture.Providers.Any(p => p.Id == PerfProviders.Clr) ? "not-recorded" :
            gcState?.State == "unavailable" ? "unavailable" : "not-observed";
        var gcCoverage = new PerfGcCoverage(gcAvailability, gcRecorded, gcErrors, gcIncomplete,
            gcAvailability == "observed" && gcErrors == 0 && gcIncomplete == 0 && manifest.CaptureReasons.Count == 0,
            Clip(gcState?.Error, 256));
        var activity = options.View is "summary" or "parsing"
            ? AnalyzeActivity(calls, parsingEvents, primaryUiThread!.Value, range)
            : null;
        if (options.View == "gc")
        {
            if (gcRecorded == 0)
            {
                throw new InvalidDataException($"GC evidence is {gcAvailability}. Missing events do not establish that no GC occurred. {gcState?.Error}");
            }
            var selectedIntervals = gc.Where(g => Overlaps(g.StartMs, g.EndMs, range) &&
                (selectedGc is null || g.Id == selectedGc.Id));
            var orderedIntervals = options.Sort == "duration"
                ? selectedIntervals.OrderByDescending(g => g.DurationMs.HasValue)
                    .ThenByDescending(g => g.DurationMs)
                    .ThenBy(g => g.StartMs ?? g.EndMs ?? double.MaxValue)
                    .ThenBy(g => g.Id, StringComparer.Ordinal)
                : selectedIntervals.OrderBy(g => g.StartMs ?? g.EndMs).ThenBy(g => g.Id, StringComparer.Ordinal);
            rows.AddRange(orderedIntervals
                .Select(g => new PerfQueryRow
                {
                    Id = g.Id, Kind = g.Kind, Name = GcName(g), GcInterval = g,
                    InclusiveMs = g.DurationMs, Evidence = g.Evidence.ToArray(),
                    ClippedOverlapMs = g.DurationMs is null ? null : ClipOverlap(g.StartMs!.Value, g.EndMs!.Value, range),
                }));
        }
        else if (options.View == "hotspots")
        {
            rows = HotspotRows(calls, elements, options, range);
        }
        else if (options.View == "parsing")
        {
            rows.AddRange(activity!.Resources.Select((resource, index) => new PerfQueryRow
            {
                Id = "resource:" + (index + 1),
                Kind = "parsing-resource",
                Name = resource.Resource,
                Count = resource.Count,
                InclusiveMs = resource.ObservedMs,
            }));
        }
        else if (options.View is "calls" or "call")
        {
            rows = CallRows(calls, elements, options, range, root);
        }
        else if (options.View != "events")
        {
            var aggregates = new Dictionary<string, Aggregate>(StringComparer.Ordinal);
            var selectedElements = options.View == "element" ? Descendants(elements, options.Id!, options.Depth ?? 0, range) : null;
            foreach (var call in calls)
            {
                if (selectedThread is { } thread && call.Thread != thread)
                {
                    continue;
                }
                if (!Overlaps(call.StartMs, call.EndMs, range))
                {
                    continue;
                }
                if (options.View == "frames")
                {
                    if (call.Family == "frames")
                    {
                        rows.Add(CallRow(call, elements, range, "frame-phase"));
                    }
                    continue;
                }
                if (call.Family == "layout" && call.DurationMs is not null)
                {
                    if (!busy.TryGetValue(call.Thread, out var intervals))
                    {
                        if (busy.Count >= 128)
                        {
                            throw new PerfAnalysisLimitException("The query exceeds 128 layout threads; narrow it with --thread.");
                        }
                        intervals = [];
                        busy.Add(call.Thread, intervals);
                    }
                    intervals.Add((Math.Max(from, call.StartMs!.Value), Math.Min(to, call.EndMs!.Value)));
                }
                if (options.View == "summary")
                {
                    Add(aggregates, "phase:" + call.Name, "phase", call.Name, null, call, range);
                }
                if (call.ElementId is not null && PerfAnalyzer.IsElementOperation(call.Name) &&
                    elements.TryGetValue(call.ElementId, out var element) &&
                    (options.Type is null || element.Type?.Contains(options.Type, StringComparison.OrdinalIgnoreCase) == true) &&
                    (selectedElements is null || selectedElements.Contains(element.Id)))
                {
                    // Override wrappers explain the element call, but must not count as a second element operation.
                    if (call.Name.EndsWith("Override", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    Add(aggregates, element.Id, "element", element.Type ?? "(type not observed)", element, call, range);
                }
            }
            var aggregateRows = aggregates.Values.Select(a => a.Row());
            var sorted = options.Sort switch
            {
                "inclusive" => aggregateRows.OrderByDescending(r => r.InclusiveMs).ThenBy(r => r.Id, StringComparer.Ordinal),
                "count" => aggregateRows.OrderByDescending(r => r.Count).ThenBy(r => r.Id, StringComparer.Ordinal),
                _ => aggregateRows.OrderByDescending(r => r.ExclusiveMs ?? r.SelfMs).ThenBy(r => r.Id, StringComparer.Ordinal),
            };
            if (options.View == "summary")
            {
                rows.AddRange(sorted);
            }
            else if (options.View == "frames")
            {
                rows = rows.OrderByDescending(r => r.Call!.DurationMs).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();
            }
            else
            {
                rows.AddRange(sorted);
                if (options.View == "element" && rows.All(r => r.Id != options.Id))
                {
                    rows.Insert(0, new() { Id = options.Id!, Kind = "element", Element = elements[options.Id!] });
                }
            }
        }
        var incomplete = options.View is "gc" or "events" ? 0 : calls.Count(c => c.Status != "complete" &&
            Overlaps(c.StartMs, c.EndMs, range) && (selectedThread is null || c.Thread == selectedThread) &&
            (options.Family is null || c.Family == options.Family));
        var reasons = manifest.CaptureReasons.ToList();
        var recordedEnd = analysis.Capture.StopQpc is { } stop && analysis.Capture.ReadyQpc is { } ready
            ? (stop - ready) * 1000.0 / analysis.Capture.Frequency : manifest.LastEventMs;
        if (from < Math.Min(0, manifest.FirstEventMs ?? 0) || to > recordedEnd)
        {
            reasons.Add("The selected time range extends beyond the recording. Choose a range within the recording.");
        }
        if (decodeErrors > 0)
        {
            reasons.Add("Some events in the selected time range could not be decoded because their format is unsupported or their data is invalid.");
        }
        if (incomplete > 0)
        {
            reasons.Add("Some UI operations have missing or inconsistent start/end events and may overlap the selected time range. Their durations are unavailable.");
        }
        if (options.View == "gc" && gcIncomplete > 0)
        {
            reasons.Add("Some managed garbage-collection or runtime-pause events have missing or inconsistent start/end events in the selected time range.");
        }
        var page = rows.Skip(options.Offset).Take(options.Limit).ToList();
        var gcOverlapReliable = analysis.Capture.EventsLost == 0 &&
            analysis.Capture.BuffersLost == 0 &&
            manifest.ReaderEventsLost == 0;
        if (gcAvailability == "observed" && gcOverlapReliable)
        {
            page = page.Select(row => WithGcOverlap(row, gc, range)).ToList();
        }
        var result = new PerfQueryResult
        {
            CaptureId = analysis.Capture.Id, View = options.View, Range = range, Offset = options.Offset,
            Total = rows.Count, Rows = page, RootCallId = root?.Id, GcCoverage = gcCoverage,
            NextOffset = (long)options.Offset + options.Limit < rows.Count ? options.Offset + options.Limit : null,
            PrimaryUiThread = primaryUiThread,
            Activity = options.View == "summary" ? activity!.Categories : null,
            ParsingResources = options.View == "summary" ? activity!.Resources.Take(10).ToList() : null,
            ParsingResourcesTotal = options.View == "summary" ? activity!.Resources.Count : null,
            ParsingResourcesOmitted = options.View == "summary" ? Math.Max(0, activity!.Resources.Count - 10) : null,
            LayoutBusyMsByThread = busy.ToDictionary(p => p.Key, p => PerfAnalyzer.Union(p.Value)),
            Coverage = new(reasons.Count == 0, Clip(analysis.Capture.StartupCoverage, 160)!,
                decodeErrors, incomplete, analysis.Capture.EventsLost, analysis.Capture.BuffersLost,
                families, reasons.Take(8).Select(r => Clip(r, 256)!).ToArray(),
                manifest.DecodeErrors, manifest.IncompleteCalls,
                manifest.IncompleteReasons.Take(8).Select(r => Clip(r, 256)!).ToArray()),
        };
        Fit(result, options.MaxBytes);
        return result;
    }

    private static bool Overlaps(double? start, double? end, PerfRange range) =>
        (end is null || end >= range.FromMs) && (start is null || start <= range.ToMs);

    private static double ClipOverlap(double start, double end, PerfRange range) =>
        Math.Max(0, Math.Min(end, range.ToMs) - Math.Max(start, range.FromMs));

    private static uint SelectPrimaryUiThread(List<PerfCall> calls, PerfRange range, uint? requested)
    {
        var complete = calls.Where(call => call.Status == "complete" && call.StartMs is not null && call.EndMs is not null &&
            Intersects(call, range)).ToList();
        if (requested is { } thread)
        {
            if (!complete.Any(call => call.Thread == thread &&
                (call.Name == "Frame" && call.Family == "frames" || Category(call) is not null)))
            {
                throw new ArgumentException($"Thread {thread} has no complete WinUI activity in the selected range.");
            }
            return thread;
        }
        var frameThread = complete.Where(call => call.Name == "Frame" && call.Family == "frames")
            .GroupBy(call => call.Thread)
            .Select(group => new
            {
                Thread = group.Key,
                Count = group.Count(),
                Busy = PerfAnalyzer.Union(group.Select(call =>
                    (Math.Max(range.FromMs, call.StartMs!.Value), Math.Min(range.ToMs, call.EndMs!.Value)))),
            })
            .OrderByDescending(candidate => candidate.Count)
            .ThenByDescending(candidate => candidate.Busy)
            .ThenBy(candidate => candidate.Thread)
            .FirstOrDefault();
        if (frameThread is not null)
        {
            return frameThread.Thread;
        }
        var activityThread = complete.Where(call => Category(call) is not null and not "Image decode/load")
            .GroupBy(call => call.Thread)
            .Select(group => new
            {
                Thread = group.Key,
                Busy = PerfAnalyzer.Union(group.Select(call =>
                    (Math.Max(range.FromMs, call.StartMs!.Value), Math.Min(range.ToMs, call.EndMs!.Value)))),
            })
            .OrderByDescending(candidate => candidate.Busy)
            .ThenBy(candidate => candidate.Thread)
            .FirstOrDefault();
        return activityThread?.Thread ??
            throw new InvalidDataException("No complete WinUI activity identifies a primary UI thread in the selected range.");
    }

    private static bool Intersects(PerfCall call, PerfRange range) => range.FromMs == range.ToMs
        ? call.StartMs <= range.FromMs && call.EndMs >= range.ToMs
        : ClipOverlap(call.StartMs!.Value, call.EndMs!.Value, range) > 0;

    private static ActivityAnalysis AnalyzeActivity(List<PerfCall> calls,
        Dictionary<string, PerfEvent> parsingEvents, uint thread, PerfRange range)
    {
        var callsById = calls.ToDictionary(call => call.Id, StringComparer.Ordinal);
        var depths = new Dictionary<string, int>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        int Depth(PerfCall call)
        {
            if (depths.TryGetValue(call.Id, out var known))
            {
                return known;
            }
            if (!visiting.Add(call.Id))
            {
                throw new InvalidDataException("The operation tree contains a cycle.");
            }
            var depth = call.ParentCallId is { } parent && callsById.TryGetValue(parent, out var parentCall)
                ? Depth(parentCall) + 1
                : 0;
            visiting.Remove(call.Id);
            depths.Add(call.Id, depth);
            return depth;
        }

        var intervals = calls.Where(call => call.Thread == thread && call.Status == "complete" &&
                call.StartMs is not null && call.EndMs is not null && Category(call) is not null &&
                ClipOverlap(call.StartMs.Value, call.EndMs.Value, range) > 0)
            .Select(call => new ActivityInterval(call, Category(call)!, Depth(call),
                Category(call) == "Parsing" ? ParsingResource(call, parsingEvents) : null))
            .ToList();
        var boundaries = intervals.SelectMany(interval => new[]
            {
                new ActivityBoundary(Math.Max(range.FromMs, interval.Call.StartMs!.Value), true, interval),
                new ActivityBoundary(Math.Min(range.ToMs, interval.Call.EndMs!.Value), false, interval),
            })
            .OrderBy(boundary => boundary.Time)
            .ThenBy(boundary => boundary.IsStart)
            .ToList();
        var comparer = Comparer<ActivityInterval>.Create((left, right) =>
        {
            var compared = left.Depth.CompareTo(right.Depth);
            if (compared == 0)
            {
                compared = CategoryPriority(left.Category).CompareTo(CategoryPriority(right.Category));
            }
            return compared != 0 ? compared : StringComparer.Ordinal.Compare(left.Call.Id, right.Call.Id);
        });
        var active = new SortedSet<ActivityInterval>(comparer);
        var totals = ActivityCategories.ToDictionary(category => category, _ => 0d, StringComparer.Ordinal);
        var resourceDurations = new Dictionary<string, double>(StringComparer.Ordinal);
        var resourceCalls = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var previous = range.FromMs;
        for (var index = 0; index < boundaries.Count;)
        {
            var time = boundaries[index].Time;
            if (time > previous && active.Max is { } owner)
            {
                var duration = time - previous;
                totals[owner.Category] += duration;
                if (owner.Category == "Parsing")
                {
                    var resource = owner.Resource!;
                    resourceDurations[resource] = resourceDurations.GetValueOrDefault(resource) + duration;
                    if (!resourceCalls.TryGetValue(resource, out var owners))
                    {
                        owners = new(StringComparer.Ordinal);
                        resourceCalls.Add(resource, owners);
                    }
                    owners.Add(owner.Call.Id);
                }
            }
            var end = index;
            while (end < boundaries.Count && boundaries[end].Time == time)
            {
                end++;
            }
            for (var current = index; current < end; current++)
            {
                if (!boundaries[current].IsStart)
                {
                    active.Remove(boundaries[current].Interval);
                }
            }
            for (var current = index; current < end; current++)
            {
                if (boundaries[current].IsStart)
                {
                    active.Add(boundaries[current].Interval);
                }
            }
            previous = time;
            index = end;
        }
        var rangeDuration = range.ToMs - range.FromMs;
        var classified = totals.Values.Sum();
        var tolerance = Math.Max(0.000001, rangeDuration * 0.000000001);
        if (classified > rangeDuration + tolerance)
        {
            throw new InvalidDataException("Observed activity exceeds the selected range.");
        }
        totals["Unclassified"] = Math.Max(0, rangeDuration - classified);
        var categories = ActivityCategories.Append("Unclassified")
            .Select(category => new PerfActivityRow(category, totals[category],
                rangeDuration > 0 ? totals[category] * 100 / rangeDuration : null))
            .ToList();
        var resources = resourceDurations.Select(pair => new PerfParsingResource
            {
                Resource = pair.Key,
                ObservedMs = pair.Value,
                Count = resourceCalls[pair.Key].Count,
            })
            .OrderByDescending(resource => resource.ObservedMs)
            .ThenByDescending(resource => resource.Count)
            .ThenBy(resource => resource.Resource, StringComparer.Ordinal)
            .ToList();
        return new(categories, resources);
    }

    private static readonly string[] ActivityCategories =
        ["Parsing", "Layout", "Render", "App callbacks", "Other observed XAML", "Image decode/load"];

    private static string? Category(PerfCall call) => call.Family switch
    {
        "parsing" => "Parsing",
        "layout" => "Layout",
        "images" => "Image decode/load",
        "input" when call.Name == "EventCallback" => "App callbacks",
        "frames" when call.Name is "RenderWalk" or "SubmitFrame" => "Render",
        "frames" when call.Name == "Frame" => null,
        "gc" or "metadata" or "unknown" => null,
        _ => "Other observed XAML",
    };

    private static int CategoryPriority(string category) => category switch
    {
        "Parsing" => 6,
        "Layout" => 5,
        "Render" => 4,
        "Image decode/load" => 3,
        "App callbacks" => 2,
        _ => 1,
    };

    private static string ParsingResource(PerfCall call, Dictionary<string, PerfEvent> parsingEvents)
    {
        if (call.BeginEvent is { } begin && parsingEvents.TryGetValue(begin, out var e))
        {
            foreach (var field in new[] { "URI", "Uri", "ComponentName" })
            {
                if (e.Fields.TryGetValue(field, out var resource) && !string.IsNullOrWhiteSpace(resource))
                {
                    return resource;
                }
            }
        }
        return "(resource not recorded)";
    }

    private static PerfQueryRow CallRow(PerfCall call, Dictionary<string, PerfElement> elements, PerfRange range,
        string kind = "call", int? depth = null, int? children = null, bool depthLimited = false, string[]? ancestors = null) => new()
    {
        Id = call.Id, Kind = kind, Name = call.Name, Call = call, InclusiveMs = call.DurationMs,
        ExclusiveMs = call.ExclusiveMs, SelfMs = call.SelfMs, Depth = depth, ChildCount = children, DepthLimited = depthLimited,
        AncestorIds = ancestors ?? [],
        Element = call.ElementId is { } id ? elements.GetValueOrDefault(id) : null,
        Evidence = new[] { call.BeginEvent, call.EndEvent }.OfType<string>().ToArray(),
        ClippedOverlapMs = call.DurationMs is null ? null : ClipOverlap(call.StartMs!.Value, call.EndMs!.Value, range),
    };

    private static List<PerfQueryRow> CallRows(List<PerfCall> calls, Dictionary<string, PerfElement> elements,
        PerfQueryOptions options, PerfRange range, PerfCall? root)
    {
        var selected = calls.Where(c => Overlaps(c.StartMs, c.EndMs, range) &&
            (options.Thread is null || c.Thread == options.Thread) &&
            (options.Family is null || c.Family == options.Family)).ToList();
        var children = selected.Where(c => c.ParentCallId is not null)
            .ToLookup(c => c.ParentCallId!, StringComparer.Ordinal);
        if (root is null)
        {
            return selected.OrderByDescending(c => c.DurationMs).ThenBy(c => c.StartMs ?? c.EndMs)
                .ThenBy(c => c.Id, StringComparer.Ordinal)
                .Select(c => CallRow(c, elements, range, children: children[c.Id].Count())).ToList();
        }
        var rows = new List<PerfQueryRow>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var maximumDepth = options.Depth ?? 2;
        void Visit(PerfCall call, string[] ancestors)
        {
            if (!visited.Add(call.Id))
            {
                throw new InvalidDataException("The operation tree contains a repeated ID or cycle.");
            }
            var direct = children[call.Id].OrderBy(c => c.StartMs ?? c.EndMs).ThenBy(c => c.Id, StringComparer.Ordinal).ToArray();
            rows.Add(CallRow(call, elements, range, depth: ancestors.Length, children: direct.Length,
                depthLimited: ancestors.Length == maximumDepth && direct.Length > 0, ancestors: ancestors));
            if (ancestors.Length < maximumDepth)
            {
                foreach (var child in direct)
                {
                    if (child.Thread != call.Thread || child.StartMs < call.StartMs || child.EndMs > call.EndMs)
                    {
                        throw new InvalidDataException("An operation child violates its parent's thread or interval.");
                    }
                    Visit(child, [.. ancestors, call.Id]);
                }
            }
        }
        Visit(root, []);
        return rows;
    }

    private static List<PerfQueryRow> HotspotRows(List<PerfCall> calls, Dictionary<string, PerfElement> elements,
        PerfQueryOptions options, PerfRange range)
    {
        var minimumDuration = options.MinFrameMs ?? 16.67;
        var children = calls.Where(c => c.ParentCallId is not null)
            .ToLookup(c => c.ParentCallId!, StringComparer.Ordinal);
        return calls.Where(c => c.Family == "frames" && c.Name == "Frame" && c.DurationMs >= minimumDuration &&
                Overlaps(c.StartMs, c.EndMs, range) && (options.Thread is null || c.Thread == options.Thread))
            .OrderByDescending(c => c.DurationMs)
            .ThenBy(c => c.StartMs)
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .Select(frame =>
            {
                var row = CallRow(frame, elements, range, "hotspot", children: children[frame.Id].Count());
                row.DominantOperations = children[frame.Id]
                    .Where(c => c.DurationMs is not null)
                    .OrderByDescending(c => c.DurationMs)
                    .ThenBy(c => c.StartMs)
                    .ThenBy(c => c.Id, StringComparer.Ordinal)
                    .Take(4)
                    .Select(c => new PerfHotspotOperation
                    {
                        Id = c.Id,
                        Name = c.Name,
                        Family = c.Family,
                        DurationMs = c.DurationMs!.Value,
                        ExclusiveMs = c.ExclusiveMs,
                        Evidence = new[] { c.BeginEvent, c.EndEvent }.OfType<string>().ToArray(),
                    }).ToList();
                return row;
            }).ToList();
    }

    private static PerfQueryRow WithGcOverlap(PerfQueryRow row, List<PerfGcInterval> gc, PerfRange range)
    {
        if (row.Call is not { DurationMs: not null, StartMs: { } start, EndMs: { } end })
        {
            return row;
        }
        var window = new PerfRange(Math.Max(start, range.FromMs), Math.Min(end, range.ToMs));
        var overlapping = gc.Where(g => g.IsGcSuspension && g.DurationMs is not null &&
            ClipOverlap(g.StartMs!.Value, g.EndMs!.Value, window) > 0).ToList();
        row.GcOverlap = new(PerfAnalyzer.Union(overlapping.Select(g =>
            (Math.Max(g.StartMs!.Value, window.FromMs), Math.Min(g.EndMs!.Value, window.ToMs))).ToList()),
            overlapping.Take(4).Select(g => g.Id).ToArray(), Math.Max(0, overlapping.Count - 4));
        return row;
    }

    internal static string GcName(PerfGcInterval interval) => interval.Kind == "collection"
        ? $"GC #{interval.Count}, generation {interval.Generation}, {interval.CollectionType ?? "type unknown"}"
        : interval.IsGcSuspension ? $"GC runtime suspension (reason {interval.Reason})"
        : $"Runtime suspension (reason {interval.Reason?.ToString() ?? "unknown"}; not attributed to GC)";

    private static void Add(Dictionary<string, Aggregate> groups, string id, string kind, string name,
        PerfElement? element, PerfCall call, PerfRange range)
    {
        if (!groups.TryGetValue(id, out var aggregate))
        {
            aggregate = new(id, kind, name, element);
            groups.Add(id, aggregate);
        }
        if (aggregate.Evidence.Count < 4 && call.BeginEvent is { } evidence)
        {
            aggregate.Evidence.Add(evidence);
        }
        if (call.DurationMs is null)
        {
            return;
        }
        if (call.StartMs < range.FromMs || call.EndMs > range.ToMs)
        {
            aggregate.Overlaps++;
            aggregate.Clipped += Math.Max(0, Math.Min(range.ToMs, call.EndMs!.Value) - Math.Max(range.FromMs, call.StartMs!.Value));
            return;
        }
        aggregate.Durations.Add(call.DurationMs.Value);
        aggregate.Inclusive += call.DurationMs.Value;
        aggregate.Self += kind == "element" ? call.SelfMs!.Value : call.ExclusiveMs ?? 0;
    }

    public static void Fit(PerfQueryResult result, int maxBytes)
    {
        maxBytes -= System.Text.Encoding.UTF8.GetByteCount(Environment.NewLine);
        while (JsonSerializer.SerializeToUtf8Bytes(result, PerfJsonContext.Default.PerfQueryResult).Length > maxBytes)
        {
            result.ByteBudgetLimited = true;
            if (result.Rows.Count > 1)
            {
                result.Rows.RemoveAt(result.Rows.Count - 1);
                result.NextOffset = result.Offset + result.Rows.Count;
            }
            else if (result.Rows.Count == 1 && !result.Rows[0].Projected)
            {
                var row = result.Rows[0];
                row.Projected = true;
                row.Name = Clip(row.Name, 80);
                row.Evidence = row.Evidence.Take(2).ToArray();
                if (row.Event is { } e)
                {
                    row.Event = e with { Name = Clip(e.Name, 80)!, Fields = [], OmittedFields = e.OmittedFields + e.Fields.Count,
                        DecodeError = Clip(e.DecodeError, 128) };
                }
                if (row.Element is { } element)
                {
                    row.Element = new()
                    {
                        Id = element.Id, ObjectId = element.ObjectId, Type = Clip(element.Type, 80), Name = Clip(element.Name, 80),
                        FirstObservedMs = element.FirstObservedMs, DestroyedMs = element.DestroyedMs,
                        CreationObserved = element.CreationObserved, Evidence = element.Evidence.Take(2).ToList(),
                    };
                }
                if (row.Call is { } call)
                {
                    row.Call = call with { Name = Clip(call.Name, 80)! };
                }
                foreach (var operation in row.DominantOperations ?? [])
                {
                    operation.Name = Clip(operation.Name, 80)!;
                    operation.Evidence = operation.Evidence.Take(2).ToArray();
                }
            }
            else if (result.Rows.Count == 1 && result.Rows[0].DominantOperations is { Count: > 1 } operations)
            {
                operations.RemoveAt(operations.Count - 1);
            }
            else if (result.ParsingResources is { Count: > 1 } resources)
            {
                resources.RemoveAt(resources.Count - 1);
                result.ParsingResourcesOmitted = result.ParsingResourcesTotal - resources.Count;
            }
            else if (result.ParsingResources is [{ Projected: false } resource])
            {
                resource.Resource = Clip(resource.Resource, 80)!;
                resource.Projected = true;
            }
            else if (result.Limitations.Length > 1)
            {
                result.Limitations = ["Projected to fit the byte budget; interpretation limits are in the WinUI performance guide and capture/cache manifests."];
            }
            else
            {
                throw new ArgumentException("The query envelope cannot fit --max-bytes; increase the budget or narrow the query.");
            }
        }
    }

    private static HashSet<string> Descendants(Dictionary<string, PerfElement> elements, string id, int depth, PerfRange range)
    {
        if (!elements.ContainsKey(id))
        {
            throw new ArgumentException("The element ID was not observed in this capture.");
        }
        var result = new HashSet<string>(StringComparer.Ordinal) { id };
        for (var level = 0; level < depth; level++)
        {
            var parents = result.ToHashSet(StringComparer.Ordinal);
            foreach (var element in elements.Values)
            {
                var changes = element.Parents.OrderBy(p => p.TimeMs).ToArray();
                if (element.FirstObservedMs <= range.ToMs &&
                    (element.DestroyedMs is null || element.DestroyedMs > range.FromMs) &&
                    changes.Where((p, i) => p.TimeMs <= range.ToMs &&
                        (i == changes.Length - 1 || changes[i + 1].TimeMs > range.FromMs))
                    .Any(p => p.ParentId is not null && parents.Contains(p.ParentId)))
                {
                    result.Add(element.Id);
                }
            }
        }
        return result;
    }

    private static double? Marker(PerfAnalysis analysis, string? name)
    {
        if (name is null)
        {
            return null;
        }
        var marker = analysis.Capture.Markers.SingleOrDefault(m => m.Name == name)
            ?? throw new ArgumentException($"Marker '{name}' was not recorded.");
        return (marker.Qpc - analysis.Capture.ReadyQpc!.Value) * 1000.0 / analysis.Capture.Frequency;
    }

    internal static void Validate(PerfQueryOptions options)
    {
        if (options.View is not ("summary" or "parsing" or "elements" or "element" or "frames" or "hotspots" or "events" or "calls" or "call" or "gc") ||
            options.Limit is < 1 or > 100 || options.Offset < 0 || options.MaxBytes is < 4096 or > 1048576 ||
            options.Sort is not ("self" or "inclusive" or "count" or "duration") || options.Depth is < 0 or > 4 ||
            options.MinFrameMs is { } minimum && (!double.IsFinite(minimum) || minimum < 0))
        {
            throw new ArgumentException("Invalid query: views summary/parsing/elements/element/frames/hotspots/events/calls/call/gc; limit 1-100; offset >=0; max-bytes 4096-1048576; sort self/inclusive/count/duration; depth 0-4; min-frame-ms finite and >=0.");
        }
        if (options.Id is not null && options.View is not ("element" or "call" or "gc") ||
            options.Depth is not null && options.View is not ("element" or "call") ||
            options.View is "element" or "call" && options.Id is null ||
            options.Type is not null && options.View is not ("elements" or "element") ||
            (options.Provider is not null || options.Event is not null || options.Element is not null) && options.View != "events" ||
            options.Family is not null && options.View != "calls" ||
            options.Thread is not null && options.View == "gc" ||
            options.View is "summary" or "elements" or "element" && options.Sort == "duration" ||
            options.View == "gc" && options.Sort is not ("self" or "duration") ||
            options.View is not ("summary" or "parsing" or "elements" or "element" or "gc") && options.Sort != "self" ||
            options.MinFrameMs is not null && options.View != "hotspots" ||
            options.FromMarker is not null && options.FromMs is not null || options.ToMarker is not null && options.ToMs is not null)
        {
            throw new ArgumentException("Query options do not apply to this view or specify conflicting time boundaries.");
        }
        if (options.Provider is not null && !Guid.TryParse(options.Provider, out _))
        {
            throw new ArgumentException("--provider must be an ETW provider GUID.");
        }
    }

    private static string? Clip(string? text, int length) => text is { Length: > 0 } && text.Length > length ? text[..length] : text;
}
