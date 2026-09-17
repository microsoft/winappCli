// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services.Controls;
using WinApp.Cli.Telemetry.Events;

namespace WinApp.Cli.Commands;

/// <summary>
/// <c>winapp find-ui</c> — lexical search over WinUI controls and samples.
/// WinUI-only by design: the corpus is the WinUI 3 Gallery, the Windows
/// Community Toolkit gallery, and the microsoft-ui-reactor ReactorGallery
/// (plus a few curated core patterns). It does not cover WPF, WinForms, or
/// other UI frameworks.
/// </summary>
internal sealed class FindUiCommand : Command, IShortDescription
{
    public string ShortDescription => "Agent-first: search WinUI controls & samples for a working example";

    public static Argument<string?> QueryArgument { get; }
    public static Option<string[]> IdOption { get; }
    public static Option<bool> ListOption { get; }
    public static Option<string?> SourceOption { get; }
    public static Option<int> MaxOption { get; }
    public static Option<bool> RefreshOption { get; }

    static FindUiCommand()
    {
        QueryArgument = new Argument<string?>("query")
        {
            Description = "What you're looking for, e.g. \"tabbed layout\" or \"color picker\". Matched lexically against WinUI control names, sample headers, and tags.",
            Arity = ArgumentArity.ZeroOrOne
        };

        IdOption = new Option<string[]>("--id")
        {
            Description = "Fetch the code (Gallery/Toolkit return XAML and/or C#; Reactor is C#-only) plus prerequisite notes for one or more scenario ids from a prior search (e.g. gallery-tabview-1).",
            Arity = ArgumentArity.OneOrMore,
            AllowMultipleArgumentsPerToken = true
        };

        ListOption = new Option<bool>("--list")
        {
            Description = "List every discoverable control/sample id instead of searching. Covers Gallery, Toolkit, and core; the opt-in Reactor source is excluded (search it with --source reactor)."
        };

        SourceOption = new Option<string?>("--source")
        {
            Description = "Restrict results to a single source: gallery (WinUI 3 Gallery), toolkit (Windows Community Toolkit), reactor (microsoft-ui-reactor, C#-only declarative WinUI), or core (curated patterns). Reactor is opt-in — it is excluded from a normal search, so pass --source reactor to search it (only do this for a Reactor/MVU project; its C#-only samples don't paste into a standard XAML app)."
        };

        MaxOption = new Option<int>("--max")
        {
            Description = "Maximum number of matched controls to return. Applies to search only; ignored with --list and --id.",
            DefaultValueFactory = _ => 3
        };

        RefreshOption = new Option<bool>("--refresh")
        {
            Description = "Bypass the local cache and re-fetch the WinUI corpus from GitHub."
        };
    }

    public FindUiCommand()
        : base("find-ui", "Agent-first: built primarily for AI coding agents to pull a real WinUI sample into the editor instead of inventing markup (pair it with --json); it works just as well typed by hand. Search WinUI controls and samples for a working code example. WinUI-only: covers the WinUI 3 Gallery and the Windows Community Toolkit by default (plus the microsoft-ui-reactor ReactorGallery as an opt-in source via --source reactor); not WPF/WinForms. A corpus is baked into the CLI, so this works offline and behind proxies; when GitHub is reachable it refreshes to the latest samples and caches them per-user.")
    {
        Arguments.Add(QueryArgument);
        Options.Add(IdOption);
        Options.Add(ListOption);
        Options.Add(SourceOption);
        Options.Add(MaxOption);
        Options.Add(RefreshOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public class Handler(
        IControlsSearchService searchService,
        IAnsiConsole console,
        ILogger<FindUiCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var query = parseResult.GetValue(QueryArgument);
            var ids = parseResult.GetValue(IdOption) ?? [];
            var list = parseResult.GetValue(ListOption);
            var source = parseResult.GetValue(SourceOption);
            var max = parseResult.GetValue(MaxOption);
            var refresh = parseResult.GetValue(RefreshOption);

            if (source is not null && !ProviderRegistry.IsValidSourceFilter(source))
            {
                var valid = string.Join(", ", ProviderRegistry.SourceFilterValues);
                return Fail(json, $"--source must be one of: {valid} (got: {source})");
            }

            // --source only affects search. Reject it with --list/--id rather than
            // silently ignoring it, so scripted callers don't get a false sense of filtering.
            if (source is not null && (list || ids.Length > 0))
            {
                return Fail(json, "--source only applies to search; it can't be combined with --list or --id.");
            }

            if (max < 1)
            {
                return Fail(json, "--max must be at least 1.");
            }

            // Exactly one mode: search (query), fetch (--id), or browse (--list).
            var modes = 0;
            if (!string.IsNullOrWhiteSpace(query)) { modes++; }
            if (ids.Length > 0) { modes++; }
            if (list) { modes++; }

            if (modes == 0)
            {
                return Fail(json, "Provide a search query (e.g. winapp find-ui \"tabbed layout\"), or use --id <id> to fetch code, or --list to browse.");
            }
            if (modes > 1)
            {
                return Fail(json, "Choose one of: a search query, --id <id>, or --list — they can't be combined.");
            }

            SearchEngine engine;
            // The embedded core patterns need no network. A request that a core-only
            // corpus can satisfy — browse (--list), an explicit --source core search,
            // or fetching only core-prefixed ids — should still work offline, so tell
            // the service a core-only engine is acceptable when the network corpus is
            // unavailable. A normal search or a gallery/toolkit/reactor --id still
            // surfaces the friendly "connect and run once" error on a cold offline cache.
            var allowCoreOnly = list
                || string.Equals(source, "core", StringComparison.OrdinalIgnoreCase)
                || (ids.Length > 0 && ids.All(id => ProviderRegistry.ForScenarioId(id) is null));

            // A request satisfiable by the embedded core patterns ALONE — an explicit
            // "--source core" search or an all-core --id set — needs no network at all.
            // Signal the service to skip the gallery/toolkit/reactor providers entirely
            // so there's no wasted fetch and no misleading "fetching…" notice. (--list is
            // deliberately excluded: it lists every source, so it still wants the network
            // corpus when online and only falls back to core via allowCoreOnly offline.)
            var coreOnly = string.Equals(source, "core", StringComparison.OrdinalIgnoreCase)
                || (ids.Length > 0 && ids.All(id => ProviderRegistry.ForScenarioId(id) is null));

            // Reactor is opt-in: its C#-only declarative samples can't paste into a
            // standard XAML app, so they must never surface in a default search.
            // Only load/fetch Reactor when the caller explicitly asks for it — a
            // "--source reactor" search or a "reactor-*" id fetch.
            var includeReactor = ProviderRegistry.IsReactorSource(source)
                || ids.Any(ProviderRegistry.IsReactorScenarioId);
            try
            {
                engine = await searchService.GetEngineAsync(refresh, allowCoreOnly, coreOnly, includeReactor, BuildFetchNotice(json), cancellationToken).ConfigureAwait(false);
            }
            catch (ControlsDataUnavailableException ex)
            {
                return Fail(json, ex.Message);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "find-ui failed to load the WinUI corpus");
                return Fail(json, $"Failed to load the WinUI corpus: {ex.Message}");
            }

            NoteEmbeddedCorpus(json);

            if (list)
            {
                return EmitList(engine, json);
            }

            if (ids.Length > 0)
            {
                return EmitCode(engine, ids, includeReactor, json);
            }

            return EmitSearch(engine, query!, max, source, includeReactor, json);
        }

        /// <summary>
        /// The <c>corpus</c> value for JSON output, or null when nothing upstream-derived
        /// was loaded (a core-patterns-only result).
        /// </summary>
        private string? CorpusLabel() => searchService.LoadedOrigin switch
        {
            CorpusOrigin.Network => "network",
            CorpusOrigin.Cache => "cache",
            CorpusOrigin.Embedded => "embedded",
            _ => null
        };

        /// <summary>
        /// Tell the user when results came from the corpus baked into the CLI. Without this
        /// the offline path is silent and indistinguishable from a live fetch — and the
        /// "Fetching WinUI controls from GitHub..." notice has usually already been shown by
        /// the attempt that failed, which would otherwise imply the data is current.
        /// <para>
        /// The notice states <i>what</i> was served, never <i>why</i>. A failed fetch is only
        /// one of the ways the embedded corpus wins: a still-fresh cache that predates the
        /// bake is served from the snapshot too (see <c>CachedProviderBase.PreferNewerOf</c>),
        /// with no network attempt at all, so claiming GitHub was unreachable would be a lie
        /// on that path.
        /// </para>
        /// Written to <b>stderr</b> on the same terms as <see cref="BuildFetchNotice"/>: a
        /// user redirecting stdout to a file keeps the results clean and still sees the
        /// staleness warning on the terminal. Under <c>--json</c> the provenance is carried
        /// by the <c>corpus</c> field instead.
        /// </summary>
        private void NoteEmbeddedCorpus(bool json)
        {
            if (searchService.LoadedOrigin != CorpusOrigin.Embedded)
            {
                return;
            }

            // --json/--quiet drop info-level output; skip the notice entirely.
            if (json || !logger.IsEnabled(LogLevel.Information))
            {
                return;
            }

            AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) })
                .MarkupLine(
                    "[grey]Using the WinUI corpus built into the CLI. "
                    + "These samples may lag upstream; run with --refresh to fetch the latest.[/]");
        }

        private int EmitSearch(SearchEngine engine, string query, int max, string? source, bool includeReactor, bool json)
        {
            // H4 guard: a --source filter naming a source that never loaded (a failed/cold
            // fetch masked by another warm source) must surface the friendly "run online
            // once" error, not a confident — and wrong — "no match". Distinguishing the two
            // is exactly what SearchEngine.HasSource is for.
            if (source is not null && !engine.HasSource(source))
            {
                return Fail(json, SourceUnavailableMessage(source));
            }

            var groups = engine.SearchGrouped(query, maxControls: max, maxScenariosPerControl: 3, sourceFilter: source);

            // Usage telemetry: mode + registry-validated source + match count. The
            // free-form query is never captured. Emitted for both output formats.
            FindUiUsageEvent.LogSearch(source, includeReactor, json, groups.Count);

            if (json)
            {
                var result = new FindUiSearchJsonOutput
                {
                    Query = query,
                    MatchCount = groups.Count,
                    Corpus = CorpusLabel(),
                    Matches = groups.Select(g => new FindUiMatchJson
                    {
                        Source = g.Source,
                        Control = g.ControlName,
                        Score = Math.Round(g.Score, 4),
                        Description = g.ControlDescription,
                        Scenarios = g.Scenarios.Select(s => new FindUiScenarioJson { Id = s.Id, Header = s.Header }).ToList()
                    }).ToList()
                };
                console.Profile.Out.Writer.WriteLine(JsonSerializer.Serialize(result, WinAppJsonContext.Default.FindUiSearchJsonOutput));
                return groups.Count > 0 ? 0 : 1;
            }

            if (groups.Count == 0)
            {
                logger.LogInformation("No WinUI controls matched \"{Query}\".", query);
                // A default search excludes the opt-in Reactor source. If the user
                // hasn't already scoped to a source, nudge them toward it so a
                // Reactor-only match isn't silently invisible.
                if (source is null)
                {
                    logger.LogInformation(
                        "Reactor samples are excluded by default; add --source reactor to search them (Reactor/MVU projects only).");
                }
                return 1;
            }

            foreach (var g in groups)
            {
                var desc = string.IsNullOrWhiteSpace(g.ControlDescription) ? "" : $" [grey]— {Markup.Escape(g.ControlDescription!)}[/]";
                var control = string.IsNullOrEmpty(g.ControlName) ? "(core pattern)" : g.ControlName;
                console.MarkupLine($"[bold cyan]{Markup.Escape(control)}[/] [grey][[{g.Source}]][/]{desc}");
                foreach (var s in g.Scenarios)
                {
                    console.MarkupLine($"  [green]{Markup.Escape(s.Id)}[/]  {Markup.Escape(s.Header)}");
                }
            }
            console.MarkupLine("[grey]Fetch full code with:[/] winapp find-ui --id <id>");
            return 0;
        }

        private int EmitCode(SearchEngine engine, string[] ids, bool includeReactor, bool json)
        {
            // H4 guard: if any requested id targets a real provider source (its "gallery-"/
            // "toolkit-"/"reactor-" prefix) that never loaded, report the friendly "run online
            // once" error naming that source rather than a misleading "Pattern not found" —
            // the id may well exist upstream; we just failed to fetch its corpus.
            foreach (var unavailable in ids
                .Select(id => ProviderRegistry.ForScenarioId(id)?.Id)
                .Where(sourceId => sourceId is not null && !engine.HasSource(sourceId))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                return Fail(json, SourceUnavailableMessage(unavailable!));
            }

            var entries = new List<FindUiCodeEntryJson>(ids.Length);
            // Telemetry uses the corpus-canonical id (e.g. "gallery-tabview-1"), never
            // the caller's raw --id token — the fallback resolver accepts bare control
            // ids ("gallery-gridview"), so echoing the request would both misreport the
            // fetched scenario and leak an unvalidated user string. Unresolved ids are
            // reflected in the count only (RequestedIdCount - resolved), never emitted.
            var resolvedCanonicalIds = new List<string>(ids.Length);
            foreach (var id in ids)
            {
                var (formatted, found, canonicalId) = engine.GetPattern(id);
                entries.Add(new FindUiCodeEntryJson { Id = id, Found = found, Content = formatted });
                if (found && canonicalId is not null)
                {
                    resolvedCanonicalIds.Add(canonicalId);
                }
            }

            FindUiUsageEvent.LogFetch(includeReactor, json, resolvedCanonicalIds, ids.Length);

            if (json)
            {
                var result = new FindUiCodeJsonOutput { Corpus = CorpusLabel(), Results = entries };
                console.Profile.Out.Writer.WriteLine(JsonSerializer.Serialize(result, WinAppJsonContext.Default.FindUiCodeJsonOutput));
                return entries.All(e => e.Found) ? 0 : 1;
            }

            for (int i = 0; i < entries.Count; i++)
            {
                if (i > 0)
                {
                    console.WriteLine();
                }

                var entry = entries[i];
                if (!entry.Found)
                {
                    // Not-found message (e.g. "Pattern 'x' not found.") — flag it clearly.
                    console.MarkupLine($"[red]{Markup.Escape(entry.Content)}[/]");
                }
                else
                {
                    WriteScenarioContent(entry.Content);
                }
            }
            return entries.All(e => e.Found) ? 0 : 1;
        }

        /// <summary>
        /// Render a formatted scenario to the console with light color to break up the
        /// wall of code (headings, metadata labels, and code-fence markers), while
        /// keeping code bodies byte-for-byte verbatim. Structural lines go through
        /// <see cref="IAnsiConsole"/> markup (escaped, so brackets in headers are safe);
        /// code and free text are written straight to the underlying output writer so
        /// they are neither markup-parsed nor word-wrapped to the console width — a
        /// snippet must survive intact even when output is redirected or the terminal is
        /// narrow. When color is available the structural styling still applies; the JSON
        /// <c>content</c> field shares the same verbatim sink and is never touched.
        /// </summary>
        private void WriteScenarioContent(string content)
        {
            foreach (var rawLine in content.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');

                // "## Control: Header [Source]" — the scenario heading.
                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    console.MarkupLine($"[bold cyan]{Markup.Escape(line)}[/]");
                }
                // Metadata labels: **Setup:**, **Namespace:**, **XAML:**, **C#:**, **Important:**
                else if (TryFormatLabelLine(line, out var labelMarkup))
                {
                    console.MarkupLine(labelMarkup);
                }
                // Code-fence markers (```xml / ```csharp / ```) — dim so they quietly delimit blocks.
                else if (line.StartsWith("```", StringComparison.Ordinal))
                {
                    console.MarkupLine($"[grey]{Markup.Escape(line)}[/]");
                }
                else
                {
                    // Code and free text: write straight to the underlying writer so the
                    // line is emitted verbatim. Routing through IAnsiConsole (WriteLine/
                    // Write) renders the string as a Spectre `Text`, which word-wraps to
                    // the console width — with no wide TTY (piping, CI, a narrow terminal)
                    // that reflows a snippet and can split a break mid-token (e.g.
                    // `</DataT` + `emplate>`), producing invalid XAML. The raw writer is the
                    // same sink the `--json` path uses, so both stay byte-for-byte faithful.
                    console.Profile.Out.Writer.WriteLine(line);
                }
            }
        }

        /// <summary>
        /// If <paramref name="line"/> opens with a bold markdown label such as
        /// <c>**Setup:**</c>, produce console markup that tints just the label and leaves
        /// any trailing content (e.g. <c>NuGet `pkg`</c>) in the default color. Returns
        /// false for non-label lines. Both segments are escaped so backticks/brackets are literal.
        /// </summary>
        private static bool TryFormatLabelLine(string line, out string markup)
        {
            markup = "";
            if (!line.StartsWith("**", StringComparison.Ordinal))
            {
                return false;
            }
            var close = line.IndexOf(":**", StringComparison.Ordinal);
            if (close < 0)
            {
                return false;
            }
            var label = line[..(close + 3)];   // includes the trailing ":**"
            var rest = line[(close + 3)..];    // may be empty (standalone label line)
            markup = $"[yellow]{Markup.Escape(label)}[/]{Markup.Escape(rest)}";
            return true;
        }

        /// <summary>
        /// Build the one-time "fetching…" notice callback handed to the search service.
        /// The service invokes it (once) only when a provider actually starts a network
        /// fetch — a warm cache never triggers it. The notice is written to a dedicated
        /// <b>stderr</b> console so stdout (and <c>--json</c> payloads in particular) stay
        /// clean, and it is suppressed entirely when info-level output is off
        /// (<c>--json</c> / <c>--quiet</c>). Returns null in those suppressed modes so no
        /// stderr console is even created.
        /// </summary>
        private Action<string>? BuildFetchNotice(bool json)
        {
            // --json/--quiet drop info-level output; skip the notice (and its stderr console).
            if (json || !logger.IsEnabled(LogLevel.Information))
            {
                return null;
            }

            IAnsiConsole? stderrConsole = null;
            var shown = false;
            // Providers load sequentially in ControlsSearchService, so this callback is
            // never invoked concurrently; a plain latch is sufficient.
            return _ =>
            {
                if (shown)
                {
                    return;
                }
                shown = true;
                stderrConsole ??= AnsiConsole.Create(
                    new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });
                stderrConsole.MarkupLine("[grey]Fetching WinUI controls from GitHub...[/]");
            };
        }

        private int EmitList(SearchEngine engine, bool json)
        {
            var items = engine.ListAll().Select(x => new FindUiScenarioJson { Id = x.id, Header = x.scenario }).ToList();

            // Usage telemetry: mode + item count. No per-id detail (a browse isn't a
            // targeted lookup) and no free-form input to capture.
            FindUiUsageEvent.LogList(json, items.Count);

            if (json)
            {
                var result = new FindUiListJsonOutput { Count = items.Count, Corpus = CorpusLabel(), Items = items };
                console.Profile.Out.Writer.WriteLine(JsonSerializer.Serialize(result, WinAppJsonContext.Default.FindUiListJsonOutput));
                return items.Count > 0 ? 0 : 1;
            }

            foreach (var item in items)
            {
                console.MarkupLine($"[green]{Markup.Escape(item.Id)}[/]  {Markup.Escape(item.Header)}");
            }
            logger.LogInformation("{Count} WinUI controls/samples available.", items.Count);
            return items.Count > 0 ? 0 : 1;
        }

        private int Fail(bool json, string message)
        {
            if (json)
            {
                return JsonErrorOutput.Write(console, message);
            }
            logger.LogError("{Symbol} {Message}", UiSymbols.Error, message);
            return 1;
        }

        /// <summary>
        /// Message for a filtered request (<c>--source X</c> or an <c>X-*</c> <c>--id</c>) whose
        /// source loaded no scenarios. With a corpus baked into the binary this should now only
        /// happen if that snapshot is missing or was produced by a different
        /// <see cref="CacheVersion"/> — so the guidance is still to refresh, but the message no
        /// longer claims the data was never fetched.
        /// </summary>
        private static string SourceUnavailableMessage(string source) =>
            $"No '{source}' control data could be loaded. find-ui normally serves this source from " +
            "the corpus baked into the CLI or a per-user cache; if both are unavailable, run the " +
            $"command with --refresh while online to repopulate the '{source}' data.";
    }
}
