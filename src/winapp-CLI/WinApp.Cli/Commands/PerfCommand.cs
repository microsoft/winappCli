// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services.Performance;

namespace WinApp.Cli.Commands;

internal sealed class PerfCommand : Command, IShortDescription
{
    public string ShortDescription => "Record and query WinUI operation timings and managed GC suspension context";

    public PerfCommand(PerfCaptureService service, IUiTargetResolver targetResolver, IAnsiConsole console)
        : base("perf", "Record PID-scoped WinUI 3 ETW without elevation, then query compact offline performance evidence. Raw ETL can contain app data; elapsed timings are not CPU/GPU measurements.")
    {
        Options.Add(WinAppRootCommand.JsonOption);
        SetAction(parse =>
        {
            EmitError(parse.GetValue(WinAppRootCommand.JsonOption), "invalid_arguments",
                "Choose a perf operation: start, status, mark, stop, or analyze.");
            return 1;
        });
        var start = new Command("start", "Start a bounded private ETW worker and return after provider/control readiness.");
        var app = new Option<string>("--app", "-a") { Description = "Target WinUI 3 app (process name, window title, or PID).", Required = true };
        var output = new Option<string>("--output") { Description = "Empty capture directory to create.", Required = true };
        var duration = new Option<int>("--duration-sec") { Description = "Capture duration: 1-300 seconds.", DefaultValueFactory = _ => 30 };
        var size = new Option<int>("--max-size-mib") { Description = "Maximum raw ETL size: 1-1024 MiB.", DefaultValueFactory = _ => 128 };
        start.Options.Add(app);
        start.Options.Add(output);
        start.Options.Add(duration);
        start.Options.Add(size);
        Configure(start);
        start.SetAction(async (parse, token) => await ExecuteAsync(parse, async () =>
        {
            var resolved = await targetResolver.ResolveAsync(parse.GetRequiredValue(app), null, token);
            using var target = Process.GetProcessById(resolved.ProcessId);
            var identity = PerfProcessIdentity.Read(target);
            var registration = await service.PrepareAsync(parse.GetRequiredValue(output), parse.GetValue(duration), parse.GetValue(size), token);
            var capture = await PerfCaptureService.BindAsync(registration, identity, "attached; startup not recorded", false, token);
            PrintCapture(capture, parse.GetValue(WinAppRootCommand.JsonOption), console);
            return 0;
        }));
        Subcommands.Add(start);

        foreach (var operation in new[] { "status", "mark", "stop" })
        {
            var command = new Command(operation, operation switch
            {
                "status" => "Read current or final capture status. Readiness is not proof of decoded coverage.",
                "mark" => "Record a uniquely named marker using the worker's QPC clock.",
                _ => "Finalize an owned capture without closing the app; repeated stops are safe.",
            });
            var id = new Argument<string>("capture-id") { Description = "ID returned by perf start." };
            var name = new Option<string>("--name") { Description = "Unique marker name (1-128 characters).", Required = true };
            command.Arguments.Add(id);
            if (operation == "mark")
            {
                command.Options.Add(name);
            }
            Configure(command);
            command.SetAction(async (parse, token) => await ExecuteAsync(parse, async () =>
            {
                var capture = await service.ControlAsync(parse.GetRequiredValue(id), operation,
                    operation == "mark" ? parse.GetRequiredValue(name) : null, token);
                PrintCapture(capture, parse.GetValue(WinAppRootCommand.JsonOption), console);
                if (capture.State == "failed")
                {
                    EmitError(parse.GetValue(WinAppRootCommand.JsonOption), "capture_incomplete", capture.Error ?? "Capture failed.", true);
                    return 1;
                }
                return 0;
            }));
            Subcommands.Add(command);
        }

        var analyze = new Command("analyze", "Query a finalized winapp capture directory. ETL stays authoritative; derived NDJSON is cached locally. Partial evidence is returned with nonzero exit status.");
        var directory = new Argument<string>("directory") { Description = "Capture directory containing capture.json and its ETL files." };
        var view = new Option<string>("--view") { Description = "summary, elements, element, frames, hotspots, events, calls, call, or gc.", DefaultValueFactory = _ => "summary" };
        var limit = new Option<int>("--limit") { Description = "Rows per page, 1-100.", DefaultValueFactory = _ => 10 };
        var offset = new Option<int>("--offset") { Description = "Zero-based row offset.", DefaultValueFactory = _ => 0 };
        var maxBytes = new Option<int>("--max-bytes") { Description = "Whole JSON response byte budget, 4096-1048576.", DefaultValueFactory = _ => 16384 };
        var from = new Option<double?>("--from-ms") { Description = "Range start relative to capture readiness." };
        var to = new Option<double?>("--to-ms") { Description = "Range end relative to capture readiness." };
        var fromMarker = new Option<string?>("--from-marker") { Description = "Range start at a recorded marker." };
        var toMarker = new Option<string?>("--to-marker") { Description = "Range end at a recorded marker." };
        var elementId = new Option<string?>("--id") { Description = "Trace-local ID for --view element or --view call; optional interval ID for --view gc." };
        var type = new Option<string?>("--type") { Description = "Observed type substring for elements/element views." };
        var elementFilter = new Option<string?>("--element") { Description = "Trace-local element ID for the events view." };
        var thread = new Option<uint?>("--thread") { Description = "Restrict to an ETW thread ID." };
        var provider = new Option<string?>("--provider") { Description = "Provider GUID for the events view." };
        var eventFilter = new Option<string?>("--event") { Description = "Exact event name or evidence ID for the events view." };
        var sort = new Option<string>("--sort") { Description = "Ranking: self, inclusive, or count for summary/elements; duration for gc.", DefaultValueFactory = _ => "self" };
        var depth = new Option<int?>("--depth") { Description = "Expansion depth, 0-4. Defaults: call 2, element 0. Call trees show instrumented operations, not CPU stacks." };
        var family = new Option<string?>("--family") { Description = "Exact operation family for --view calls, for example layout, frames, input, or initialization." };
        var minFrameMs = new Option<double?>("--min-frame-ms") { Description = "Minimum complete Frame duration for --view hotspots. Default: 16.67 ms." };
        analyze.Arguments.Add(directory);
        foreach (var option in new Option[] { view, limit, offset, maxBytes, from, to, fromMarker, toMarker,
            elementId, type, elementFilter, thread, provider, eventFilter, sort, depth, family, minFrameMs })
        {
            analyze.Options.Add(option);
        }
        Configure(analyze);
        analyze.SetAction(async (parse, token) => await ExecuteAsync(parse, () =>
        {
            var options = new PerfQueryOptions(parse.GetRequiredValue(view), parse.GetValue(limit), parse.GetValue(offset),
                parse.GetValue(maxBytes), parse.GetValue(from), parse.GetValue(to), parse.GetValue(fromMarker),
                parse.GetValue(toMarker), parse.GetValue(elementId), parse.GetValue(type), parse.GetValue(elementFilter),
                parse.GetValue(thread), parse.GetValue(provider), parse.GetValue(eventFilter), parse.GetRequiredValue(sort),
                parse.GetValue(depth), parse.GetValue(family), parse.GetValue(minFrameMs));
            PerfQuery.Validate(options);
            if (parse.GetResult(sort) is { Implicit: false } &&
                !(options.View is "summary" or "elements" or "element" && options.Sort is "self" or "inclusive" or "count") &&
                !(options.View == "gc" && options.Sort == "duration"))
            {
                throw new ArgumentException("--sort accepts self/inclusive/count for summary and element rankings, or duration for GC. Calls, frames, and hotspots are longest-first; events and unsorted GC intervals are chronological.");
            }
            var analysis = PerfAnalysisStore.Open(parse.GetRequiredValue(directory), token);
            var result = PerfQuery.Execute(analysis, options);
            var json = parse.GetValue(WinAppRootCommand.JsonOption);
            if (json)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(result, PerfJsonContext.Default.PerfQueryResult));
            }
            else
            {
                console.WriteLine($"Capture {result.CaptureId}: {result.View}, {result.Range.FromMs:F2}-{result.Range.ToMs:F2} ms");
                console.WriteLine($"{"Operation / element",-42} {"Elapsed ms",9} {"Exclusive",10} {"Elem self",9}");
                foreach (var row in result.Rows)
                {
                    var indent = new string(' ', (row.Depth ?? 0) * 2);
                    var label = indent + row.Id + " " +
                        (row.Name ?? row.Event?.Name ?? row.Element?.Type);
                    if (label.Length > 42)
                    {
                        label = label[..39] + "...";
                    }
                    console.WriteLine($"{label,-42} {Timing(row.InclusiveMs),9} {Timing(row.ExclusiveMs),10} {Timing(row.SelfMs),9}");
                    if (row.Call is { } call)
                    {
                        console.WriteLine($"{indent}  thread {call.Thread}, {call.Status}, parent {call.ParentCallId ?? "none"}, {row.ChildCount?.ToString() ?? "unknown"} children");
                        if (row.ClippedOverlapMs != call.DurationMs)
                        {
                            console.WriteLine($"{indent}  overlap with query: {Timing(row.ClippedOverlapMs)} ms");
                        }
                    }
                    if (row.Element is { } element)
                    {
                        console.WriteLine($"{indent}  {element.Id} {element.Type ?? "type not observed"} {element.Source}:{element.Line}");
                    }
                    if (row.Count is { } count)
                    {
                        console.WriteLine($"  count={count}; mean={Timing(row.MeanMs)}; max={Timing(row.MaxMs)}; p95={Timing(row.P95Ms)} ms");
                    }
                    if (row.GcInterval is { } gc)
                    {
                        console.WriteLine($"  CLR instance={gc.ClrInstanceId}; status={gc.Status}; fully suspended={Timing(gc.FullySuspendedMs)} ms; clipped overlap={Timing(row.ClippedOverlapMs)} ms");
                    }
                    if (row.GcOverlap is { } overlap)
                    {
                        console.WriteLine($"  observed GC suspension overlap={overlap.ObservedOverlapMs:F3} ms; intervals={string.Join(", ", overlap.IntervalIds)}; omitted={overlap.OmittedIntervals}");
                    }
                    foreach (var operation in row.DominantOperations ?? [])
                    {
                        console.WriteLine($"{indent}  {operation.Id} {operation.Name} ({operation.Family}): {operation.DurationMs:F3} ms elapsed, {Timing(operation.ExclusiveMs)} ms exclusive");
                        if (operation.Evidence.Length > 0)
                        {
                            console.WriteLine(indent + "    evidence: " + string.Join(", ", operation.Evidence));
                        }
                    }
                    if (row.Evidence.Length > 0)
                    {
                        console.WriteLine(indent + "  evidence: " + string.Join(", ", row.Evidence));
                    }
                    if (row.DepthLimited)
                    {
                        console.WriteLine($"  More children: query --view call --id {row.Id} --depth 2.");
                    }
                }
                console.WriteLine($"GC coverage: {result.GcCoverage?.Availability}; selected-range complete={result.GcCoverage?.Complete}. {result.GcCoverage?.Error}");
                foreach (var reason in result.Coverage.Reasons)
                {
                    console.WriteLine("Coverage: " + reason);
                }
                console.WriteLine($"Returned {result.Returned}/{result.Total}; next offset: {result.NextOffset?.ToString() ?? "none"}.");
                foreach (var limitation in result.Limitations)
                {
                    console.WriteLine(limitation);
                }
            }
            if (!result.Coverage.Complete)
            {
                EmitError(json, "partial_data", "Partial evidence returned. See coverage.reasons and the capture/cache manifests.", true);
                return Task.FromResult(1);
            }
            return Task.FromResult(0);
        }));
        Subcommands.Add(analyze);
    }

    private static void Configure(Command command)
    {
        command.Options.Add(WinAppRootCommand.JsonOption);
        command.Options.Add(WinAppRootCommand.VerboseOption);
        command.Options.Add(WinAppRootCommand.QuietOption);
    }

    private static async Task<int> ExecuteAsync(ParseResult parse, Func<Task<int>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            EmitError(parse.GetValue(WinAppRootCommand.JsonOption),
                ex is ArgumentException ? "invalid_arguments" : ex is OperationCanceledException ? "cancelled" : "performance_error",
                ex.Message);
            return 1;
        }
    }

    internal static void EmitError(bool json, string code, string message, bool partial = false)
    {
        if (json)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new PerfCommandError(code, message, partial),
                PerfJsonContext.Default.PerfCommandError));
        }
        else
        {
            Console.Error.WriteLine(message);
        }
    }

    private static void PrintCapture(PerfCaptureDocument capture, bool json, IAnsiConsole console)
    {
        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(capture, PerfJsonContext.Default.PerfCaptureDocument));
        }
        else
        {
            console.WriteLine($"{capture.Id}: {capture.State} - {capture.Directory}");
            console.WriteLine($"Status/stop: winapp perf status {capture.Id} / winapp perf stop {capture.Id}");
            foreach (var warning in capture.Warnings)
            {
                console.WriteLine(warning);
            }
            foreach (var state in capture.ProviderStates.Where(p => p.State == "unavailable"))
            {
                console.WriteLine($"Optional provider {state.Id} unavailable: {state.Error}");
            }
        }
    }

    private static string Timing(double? value) => value?.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";

    internal static bool IsDescendant(ParseResult parse)
    {
        for (Command? command = parse.CommandResult.Command; command is not null;
            command = command.Parents.OfType<Command>().FirstOrDefault())
        {
            if (command.Name == "perf")
            {
                return true;
            }
        }
        return false;
    }
}
