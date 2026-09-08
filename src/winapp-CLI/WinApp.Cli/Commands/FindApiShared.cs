// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;
using WinApp.Cli.Services.ApiSearch;
using WinApp.Cli.Telemetry.Events;

namespace WinApp.Cli.Commands;

/// <summary>
/// Shared plumbing for the <c>winapp find-api</c> verb family: the common
/// <c>--project-dir</c>/<c>--project</c> scope options, the outcome→exit-code +
/// text/JSON emit path (with usage telemetry), and the console renderers that
/// reproduce the standalone tool's output. Rendering uses
/// <see cref="IAnsiConsole.WriteLine(string)"/> for content lines so type names,
/// signatures, and generics with angle brackets stay literal and copy-pasteable.
/// </summary>
internal static class FindApiShared
{
    /// <summary>
    /// The scope options are declared once and shared by <c>find-api</c> and every verb
    /// under it — the pattern the CLI already uses for <c>--verbose</c> and <c>--quiet</c>.
    /// A separate instance per verb only binds after the verb name, so
    /// <c>winapp find-api --project App members Button</c> would bind <c>--project</c> to
    /// nothing and answer from the wrong scope instead of scoping the lookup.
    /// </summary>
    public static Option<string?> ProjectDirOption { get; } = new("--project-dir")
    {
        Description = "Project directory to query (defaults to the current directory). Used to locate the indexed project.",
    };

    public static Option<string?> ProjectOption { get; } = new("--project")
    {
        Description = "Project name to query (matches the .csproj/.vcxproj name), or 'sdk' to query the machine-wide Windows SDK scope instead of a project.",
    };

    public static ApiRequestScope ReadScope(ParseResult parseResult) =>
        new(parseResult.GetValue(ProjectDirOption), parseResult.GetValue(ProjectOption));

    /// <summary>
    /// Adds the two scope options to a command, together with the validator that keeps
    /// them from being combined. They name the scope two different ways — a directory to
    /// look in, and a project (or the machine-wide SDK) to answer from — so passing both
    /// leaves one silently ignored and the answer coming from a scope the caller did not
    /// ask for. Registering them through one call keeps a new verb from picking up the
    /// options without the check.
    /// </summary>
    public static void AddScopeOptions(Command command)
    {
        command.Options.Add(ProjectDirOption);
        command.Options.Add(ProjectOption);
        command.Validators.Add(result =>
        {
            if (result.GetResult(ProjectDirOption) is not null && result.GetResult(ProjectOption) is not null)
            {
                result.AddError(
                    "--project-dir and --project cannot be combined: --project-dir picks the directory to read a project from, "
                    + "and --project picks an already-indexed project by name (or 'sdk'). Pass whichever one names the scope you want.");
            }
        });
    }

    /// <summary>
    /// Rejects a search query typed in front of a verb. <c>find-api</c> on its own takes a
    /// free-form query, so <c>winapp find-api NavigationView members Button</c> binds
    /// <c>NavigationView</c> to the parent's query argument and then drops it when the verb
    /// runs — answering half the question with no indication the rest was ignored. The
    /// validator is attached to the verb rather than the parent because only the innermost
    /// command's validators run.
    /// </summary>
    public static void RejectStrayQuery(Command verb, Argument<string[]> queryArgument) =>
        verb.Validators.Add(result =>
        {
            if (result.Parent is CommandResult parent
                && parent.GetResult(queryArgument) is { Tokens.Count: > 0 } stray)
            {
                string dropped = string.Join(" ", stray.Tokens.Select(token => token.Value));
                result.AddError(
                    $"'{dropped}' cannot be combined with the '{verb.Name}' verb. "
                    + $"Search on its own (winapp find-api \"{dropped}\"), or pass it to the verb as a subject.");
            }
        });

    /// <summary>
    /// Render an <see cref="ApiQueryResult{T}"/>: a clean error envelope for a
    /// non-Ok outcome, otherwise JSON (source-gen) or the text renderer, mapping to
    /// an exit code and emitting one bounded usage event.
    /// </summary>
    public static int Emit<T>(
        IAnsiConsole console,
        bool json,
        string verb,
        ApiQueryResult<T> result,
        JsonTypeInfo<T> jsonType,
        Action<T> renderText,
        Func<T, (int Exit, int Count, bool Found)> summarize)
        where T : class
    {
        if (!result.IsOk)
        {
            FindApiUsageEvent.Log(verb, json, resultCount: 0, found: false);
            return Fail(console, json, result.Message ?? "The find-api query failed.");
        }

        T data = result.Data!;
        (int exit, int count, bool found) = summarize(data);
        FindApiUsageEvent.Log(verb, json, count, found);

        if (json)
        {
            console.Profile.Out.Writer.WriteLine(JsonSerializer.Serialize(data, jsonType));
        }
        else
        {
            WriteScopeNote(console, data);
            renderText(data);
        }
        return exit;
    }

    /// <summary>
    /// In text mode, state which index answered before rendering the payload.
    ///
    /// This used to fire only for the machine-wide SDK scope. It now fires for every
    /// scoped verb because benchmark analysis found <c>--json</c> was used on ~1% of
    /// calls — so the project attribution carried in the JSON payload was, in practice,
    /// unobservable. An answer sourced from the wrong index is indistinguishable from a
    /// correct one without it, and project names are not unique across directories,
    /// which is why the directory is printed alongside the name.
    /// </summary>
    private static void WriteScopeNote(IAnsiConsole console, object data)
    {
        if (data is not IApiScopedOutput scoped)
        {
            return;
        }

        if (scoped.Scope == ApiScopeNames.Sdk)
        {
            console.MarkupLine("[yellow]Note:[/] no project found here \u2014 showing Windows SDK + Windows App SDK APIs only (project NuGet packages are not included).");
            console.WriteLine();
            WriteCaveats(console, scoped);
            return;
        }

        if (!string.IsNullOrEmpty(scoped.ProjectName))
        {
            string where = string.IsNullOrEmpty(scoped.ProjectDir) ? "" : $" ({scoped.ProjectDir})";
            console.MarkupLineInterpolated($"[grey]Project: {scoped.ProjectName}{where}[/]");
            console.WriteLine();
        }

        WriteCaveats(console, scoped);
    }

    /// <summary>
    /// State what the answering index could not cover, above the answer it qualifies.
    /// </summary>
    /// <remarks>
    /// The index records these when it is built; the answer is usually served from the
    /// cache much later, so without this the caveat is only ever seen by whoever happened
    /// to run the refresh. Printed before the result rather than after, because a reader
    /// who has already seen "found" has stopped reading.
    /// </remarks>
    private static void WriteCaveats(IAnsiConsole console, IApiScopedOutput scoped)
    {
        if (scoped.Caveats is not { Count: > 0 } caveats)
        {
            return;
        }
        foreach (string caveat in caveats)
        {
            console.MarkupLineInterpolated($"[yellow]\u26a0 {caveat}[/]");
        }
        console.WriteLine();
    }

    /// <summary>
    /// Render several subjects answered by one invocation. Emits a single batch envelope
    /// in <c>--json</c> and a separator-delimited sequence in text, with the scope header
    /// printed once rather than per subject.
    ///
    /// Exit code is 0 only when every subject resolved and was found: a batch that
    /// silently succeeded on 7 of 8 lookups would be worse than no batching at all,
    /// because the caller's whole reason for batching is to stop checking individually.
    /// </summary>
    public static int EmitBatch<T, TBatch>(
        IAnsiConsole console,
        bool json,
        string verb,
        List<(string Subject, ApiQueryResult<T> Result)> results,
        Func<List<T>, List<ApiBatchError>, TBatch> buildBatch,
        JsonTypeInfo<TBatch> batchJsonType,
        Action<T> renderText,
        Func<T, (int Count, bool Found)> summarize,
        bool separateItems = true)
        where T : class
        where TBatch : class
    {
        var ok = new List<T>();
        var errors = new List<ApiBatchError>();
        int totalCount = 0;
        bool allFound = true;

        foreach ((string subject, ApiQueryResult<T> result) in results)
        {
            if (result.IsOk && result.Data is not null)
            {
                ok.Add(result.Data);
                (int count, bool found) = summarize(result.Data);
                totalCount += count;
                if (!found)
                {
                    allFound = false;
                }
            }
            else
            {
                errors.Add(new ApiBatchError
                {
                    Subject = subject,
                    Message = result.Message ?? $"'{subject}' could not be resolved.",
                });
                allFound = false;
            }
        }

        FindApiUsageEvent.Log(verb, json, totalCount, allFound);

        if (json)
        {
            console.Profile.Out.Writer.WriteLine(JsonSerializer.Serialize(buildBatch(ok, errors), batchJsonType));
            return allFound ? 0 : 1;
        }

        if (ok.Count > 0)
        {
            WriteScopeNote(console, ok[0]);
        }

        for (int i = 0; i < ok.Count; i++)
        {
            if (i > 0 && separateItems)
            {
                console.WriteLine();
            }
            renderText(ok[i]);
        }

        foreach (ApiBatchError error in errors)
        {
            console.WriteLine($"{UiSymbols.Error} {error.Message}");
        }

        return allFound ? 0 : 1;
    }

    /// <summary>
    /// Split the positional subjects of a batched verb, trimming blanks. Returns an empty
    /// list when nothing usable was supplied so the caller can emit its own usage error.
    /// </summary>
    public static List<string> ReadSubjects(IEnumerable<string>? raw) =>
        raw?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList() ?? [];

    /// <summary>
    /// Emit an unwrapped payload (verbs like <c>projects</c>/<c>refresh</c> that
    /// always succeed once reached) as JSON or text, with one usage event.
    /// </summary>
    public static int EmitRaw<T>(
        IAnsiConsole console,
        bool json,
        string verb,
        T data,
        JsonTypeInfo<T> jsonType,
        Action<T> renderText,
        Func<T, (int Exit, int Count, bool Found)> summarize)
        where T : class
    {
        (int exit, int count, bool found) = summarize(data);
        FindApiUsageEvent.Log(verb, json, count, found);
        if (json)
        {
            console.Profile.Out.Writer.WriteLine(JsonSerializer.Serialize(data, jsonType));
        }
        else
        {
            renderText(data);
        }
        return exit;
    }

    public static int Fail(IAnsiConsole console, bool json, string message)
    {
        if (json)
        {
            return JsonErrorOutput.Write(console, message);
        }
        console.MarkupLineInterpolated($"[red]{UiSymbols.Error} {message}[/]");
        return 1;
    }

    // ---- text renderers (mirror the standalone tool's plain output) ----

    /// <summary>
    /// Drops the cache-file diagnostics from a search payload unless the caller asked
    /// for them. Text output already hid these at default verbosity; JSON did not, where
    /// they measured ~21% of the payload. Stripping here rather than in the query engine
    /// keeps the engine free of presentation concerns.
    /// </summary>
    public static void ApplyVerbosity(ApiSearchOutput output, bool verbose)
    {
        if (verbose)
        {
            return;
        }
        foreach (ApiNamespaceHit hit in output.Results)
        {
            hit.Files = null;
        }
    }

    public static void RenderSearch(IAnsiConsole console, ApiSearchOutput output, bool verbose)
    {
        if (output.Ambiguous is { Count: > 0 })
        {
            foreach (ApiAmbiguityGroup group in output.Ambiguous)
            {
                console.WriteLine($"\u26a0\ufe0f AMBIGUOUS \u2014 '{group.Name}' found in multiple namespaces:");
                console.WriteLine();
                foreach (ApiAmbiguityCandidate candidate in group.Candidates)
                {
                    console.WriteLine($"  [{candidate.Score}] {candidate.FullName} ({candidate.Kind})");
                    if (candidate.EnumValues is { Count: > 0 })
                    {
                        string preview = string.Join(", ", candidate.EnumValues.Take(6));
                        if (candidate.EnumValues.Count > 6)
                        {
                            preview += ", ...";
                        }
                        console.WriteLine($"        Values: {preview}");
                    }
                    if (!string.IsNullOrEmpty(candidate.Description))
                    {
                        console.WriteLine($"        {candidate.Description}");
                    }
                }
                console.WriteLine();
                console.WriteLine("  Use the fully-qualified name to avoid CS0104.");
                console.WriteLine();
            }
        }

        if (output.Results.Count == 0)
        {
            console.WriteLine($"No results found for: {output.Query}");
            if (!string.IsNullOrEmpty(output.Note))
            {
                console.WriteLine(output.Note);
            }
            return;
        }

        foreach (ApiNamespaceHit ns in output.Results)
        {
            console.WriteLine($"[{ns.Score}] {ns.Namespace}");
            if (verbose && ns.Files is not null)
            {
                // The on-disk cache path is a debugging aid, not an answer. It is long,
                // absolute, and user-specific, so at default verbosity it was crowding
                // out the API facts the caller actually asked for.
                foreach (string file in ns.Files)
                {
                    console.WriteLine($"    File: {file}");
                }
            }
            foreach (ApiTypeHit match in ns.Matches)
            {
                console.WriteLine($"    {match.Display}");
            }
            console.WriteLine();
        }
    }

    public static void RenderMembers(IAnsiConsole console, ApiMembersOutput output)
    {
        string deprecatedPrefix = output.Deprecated is not null ? "\U0001f6ab " : "";
        console.WriteLine($"{deprecatedPrefix}{output.Kind} {output.FullName}");
        if (output.Deprecated is not null)
        {
            console.WriteLine($"  {output.Deprecated}");
        }
        if (!string.IsNullOrEmpty(output.Description))
        {
            console.WriteLine($"  {output.Description}");
        }
        if (!string.IsNullOrEmpty(output.BaseType))
        {
            console.WriteLine($"  Extends: {output.BaseType}");
        }
        WriteAlsoMatched(console, output.AlsoMatched, "  ");
        if (output.Filter is not null)
        {
            int shown = output.Properties.Count + output.Events.Count + output.Methods.Count
                + (output.Fields?.Count ?? 0);
            int total = (output.TotalProperties ?? 0) + (output.TotalEvents ?? 0) + (output.TotalMethods ?? 0)
                + (output.TotalFields ?? 0);

            console.WriteLine($"  Filter: '{output.Filter}' \u2014 showing {shown} of {total} members");
        }
        console.WriteLine();

        WriteMemberGroup(console, "Properties:", output.Properties, MemberKind.Property);
        WriteMemberGroup(console, "Fields:", output.Fields ?? [], MemberKind.Field);
        WriteMemberGroup(console, "Events:", output.Events, MemberKind.Event);
        WriteMemberGroup(console, "Methods:", output.Methods, MemberKind.Method);

        WriteInheritedGroups(console, output);

        WriteTrimNote(console, output);

        if (output.Properties.Count == 0 && output.Events.Count == 0 && output.Methods.Count == 0
            && output.Fields is not { Count: > 0 })

        {
            // Distinguish a filter miss from a type with no members. Rendering nothing at
            // all reads as "this type does not exist", which is the wrong conclusion to
            // hand an agent that is deciding whether to use the API.
            console.WriteLine(output.Filter is not null ? "  (no members match the filter)" : "  (no declared members)");
            console.WriteLine();
        }

        if (output.GetForCurrentViewWarning)
        {
            console.WriteLine("  \u26a0\ufe0f GetForCurrentView() requires a CoreWindow (UWP). Desktop WinUI 3 apps");
            console.WriteLine("     may need COM interop (e.g., IInitializeWithWindow, IDataTransferManagerInterop).");
            console.WriteLine();
        }
    }

    /// <summary>
    /// Lists inherited members by name under the base type that declares them. An
    /// unfiltered listing summarizes them this way instead of inlining their signatures,
    /// which for a WinUI control is almost the entire payload.
    /// </summary>
    private static void WriteInheritedGroups(IAnsiConsole console, ApiMembersOutput output)
    {
        if (output.Inherited is not { Count: > 0 })
        {
            return;
        }

        int total = output.Inherited.Sum(g =>
            (g.Properties?.Count ?? 0) + (g.Events?.Count ?? 0) + (g.Methods?.Count ?? 0) + (g.Fields?.Count ?? 0));
        console.WriteLine($"  Inherited ({total} member(s) from {output.Inherited.Count} base type(s), names only):");
        foreach (ApiInheritedMemberGroup group in output.Inherited)
        {
            console.WriteLine($"    {group.DeclaringType}");
            WriteNames(console, "Properties", group.Properties);
            WriteNames(console, "Fields", group.Fields);
            WriteNames(console, "Events", group.Events);
            WriteNames(console, "Methods", group.Methods);
        }
        console.WriteLine();

        static void WriteNames(IAnsiConsole console, string heading, List<string>? names)
        {
            if (names is not { Count: > 0 })
            {
                return;
            }
            console.WriteLine($"      {heading}: {string.Join(", ", names)}");
        }
    }

    /// <summary>
    /// States what an unfiltered listing left out. Without this the trim is invisible,
    /// and a caller who does not see <c>BackgroundProperty</c> could reasonably conclude
    /// the type has no such identifier rather than that it was hidden.
    /// </summary>
    private static void WriteTrimNote(IAnsiConsole console, ApiMembersOutput output)
    {
        if (output.HiddenDependencyProperties is not > 0
            && output.DescriptionsOmitted is not true
            && output.Inherited is not { Count: > 0 })
        {
            return;
        }

        var parts = new List<string>();
        if (output.HiddenDependencyProperties is > 0)
        {
            parts.Add($"{output.HiddenDependencyProperties} dependency-property identifier(s)");
        }
        if (output.DescriptionsOmitted is true)
        {
            parts.Add("member descriptions");
        }
        if (output.Inherited is { Count: > 0 })
        {
            parts.Add("inherited member signatures");
        }

        console.WriteLine($"  Omitted: {string.Join(", ", parts)}.");
        console.WriteLine("  Use --filter <text> to search the full surface, or --all to list it in full.");
        console.WriteLine();
    }

    private static void WriteMemberGroup(IAnsiConsole console, string heading, List<ApiMemberOutput> members, MemberKind kind)
    {
        if (members.Count == 0)
        {
            return;
        }
        console.WriteLine($"  {heading}");
        foreach (ApiMemberOutput member in members)
        {
            WriteMember(console, member, "    ", kind);
        }
        console.WriteLine();
    }

    private static void WriteMember(IAnsiConsole console, ApiMemberOutput member, string indent, MemberKind? kind = null)
    {
        string deprecatedPrefix = member.Deprecated is not null ? "\U0001f6ab " : "";
        string desc = member.Description is not null ? $" \u2014 {member.Description}" : "";
        string suffix = member is { Inherited: true, DeclaringType: not null } ? $"  (from {member.DeclaringType})" : "";
        // An event's signature is 'event <HandlerType> <Name>', which reads worse than
        // the bare name. The kind comes from the group being rendered, because an
        // unfiltered listing omits the per-member kind as redundant.
        bool isEvent = kind == MemberKind.Event || member.Kind == nameof(MemberKind.Event);
        string text = isEvent ? member.Name : member.Signature;
        console.WriteLine($"{indent}{deprecatedPrefix}{text}{desc}{suffix}");
        if (member.Deprecated is not null)
        {
            console.WriteLine($"{indent}  \u21b3 Deprecated: {member.Deprecated}");
        }
    }

    public static void RenderCheckProperty(IAnsiConsole console, ApiCheckPropertyOutput output)
    {
        WriteAlsoMatched(console, output.AlsoMatched, "");
        if (output.Found && output.Match is not null)
        {
            string inherited = output.Match is { Inherited: true, DeclaringType: not null } ? $"  (from {output.Match.DeclaringType})" : "";
            string desc = output.Match.Description is not null ? $" \u2014 {output.Match.Description}" : "";
            console.WriteLine($"{FoundMarker(output)} {output.Type}.{output.Match.Name}{ReadOnlyNote(output)}");
            console.WriteLine($"   {output.Match.Signature}{inherited}{desc}");
            return;
        }
        if (output is { Attached: true, AttachedInfo: not null })
        {
            console.WriteLine($"\u2705 {output.Type}.{output.Property} (attached)");
            console.WriteLine($"   {output.AttachedInfo}");
            return;
        }

        console.WriteLine($"\u274c {output.Type} does not have property '{output.Property}'");
        if (output.Warning is not null)
        {
            console.WriteLine($"   \u26a0 {output.Warning}");
        }
        console.WriteLine();
        if (output.SimilarOnType is { Count: > 0 })
        {
            console.WriteLine($"  Similar {output.Type} properties:");
            foreach (ApiMemberOutput member in output.SimilarOnType)
            {
                string desc = member.Description is not null ? $" \u2014 {member.Description}" : "";
                console.WriteLine($"    {member.Signature}{desc}");
            }
            console.WriteLine();
        }
        if (output.TypesWithProperty is { Count: > 0 })
        {
            console.WriteLine($"  Types that have a '{output.Property}' property:");
            foreach (ApiCrossTypeMember member in output.TypesWithProperty)
            {
                string desc = member.Description is not null ? $" \u2014 {member.Description}" : "";
                console.WriteLine($"    {member.TypeName}.{member.Signature}{desc}");
            }
            console.WriteLine();
        }
        if (output.TypesWithSimilar is { Count: > 0 })
        {
            console.WriteLine("  Types with a similar property:");
            foreach (ApiCrossTypeMember member in output.TypesWithSimilar)
            {
                string desc = member.Description is not null ? $" \u2014 {member.Description}" : "";
                console.WriteLine($"    {member.TypeName}.{member.Signature}{desc}");
            }
            console.WriteLine();
        }
    }

    /// <summary>
    /// Batch rendering for <c>check-property</c>: a confirmation collapses to one line,
    /// while a miss keeps the full suggestion block.
    ///
    /// Measured usage skews ~91% confirmations, and a confirmation carries no actionable
    /// information beyond "yes" — printing its signature, description, and declaring type
    /// is paid for on every subsequent turn of the conversation. A miss is the opposite:
    /// it is the only outcome the caller has to do something about, so it keeps the
    /// near-miss suggestions that let them fix it without a follow-up <c>members</c> call.
    /// </summary>
    public static void RenderCheckPropertyCompact(IAnsiConsole console, ApiCheckPropertyOutput output)
    {
        if (output is { Found: true, Match: not null })
        {
            WriteAlsoMatched(console, output.AlsoMatched, "");
            string inherited = output.Match is { Inherited: true, DeclaringType: not null } ? $"  (from {output.Match.DeclaringType})" : "";
            console.WriteLine($"{FoundMarker(output)} {output.Type}.{output.Match.Name}{inherited}{ReadOnlyNote(output)}");
            return;
        }

        if (output is { Attached: true, AttachedInfo: not null })
        {
            WriteAlsoMatched(console, output.AlsoMatched, "");
            console.WriteLine($"\u2705 {output.Type}.{output.Property} (attached)");
            return;
        }

        RenderCheckProperty(console, output);
    }

    /// <summary>
    /// Name the same-named types the short-name lookup passed over. The
    /// <c>Microsoft.*</c> preference in <c>ResolveType</c> is a guess about which type
    /// the caller meant, and the pair is not always interchangeable — 
    /// <c>Windows.System.DispatcherQueue</c> has no <c>EnsureSystemDispatcherQueue</c>,
    /// which the <c>Microsoft.UI.Dispatching</c> one does. Stating the choice is what
    /// keeps a confident answer from being about the wrong type.
    /// </summary>
    private static void WriteAlsoMatched(IAnsiConsole console, List<string>? alsoMatched, string indent)
    {
        if (alsoMatched is not { Count: > 0 })
        {
            return;
        }
        console.WriteLine(
            $"{indent}\u26a0 That name also matches {string.Join(", ", alsoMatched)}. " +
            "Answering for the Microsoft.* type; use the full name for the other.");
    }

    /// <summary>
    /// A read-only property exists, so the check is not a failure — but reporting it with
    /// the same unqualified tick as a settable one invites the caller to assign it, which
    /// fails at compile time (WMC0050 in XAML). Mark it distinctly instead.
    /// </summary>
    private static string FoundMarker(ApiCheckPropertyOutput output) =>
        output.Writable == false ? "\u26a0\ufe0f" : "\u2705";

    private static string ReadOnlyNote(ApiCheckPropertyOutput output) =>
        output.Writable == false ? "  \u2014 read-only, cannot be assigned" : "";

    public static void RenderTypes(IAnsiConsole console, ApiTypesOutput output)
    {
        foreach (ApiTypeSummary type in output.Types)
        {
            string baseType = string.IsNullOrEmpty(type.BaseType) ? "" : $" : {type.BaseType}";
            console.WriteLine($"{type.Kind} {type.FullName}{baseType}");
        }
    }

    public static void RenderEnums(IAnsiConsole console, ApiEnumsOutput output)
    {
        console.WriteLine($"Enum {output.FullName}");
        WriteAlsoMatched(console, output.AlsoMatched, "  ");
        if (output.Filter is not null)
        {
            console.WriteLine($"  Filter: '{output.Filter}' \u2014 showing {output.Values.Count} of {output.TotalValues ?? output.Values.Count} values");
        }
        if (output.Values.Count == 0)
        {
            // Say why the list is empty: a filter miss is not the same as an
            // enum with no values, and conflating them invites a wrong conclusion
            // that the API does not exist.
            console.WriteLine(output.Filter is not null ? "  (no values match the filter)" : "  (no values)");
            return;
        }
        foreach (string value in output.Values)
        {
            console.WriteLine($"  {value}");
        }
    }

    public static void RenderNamespaces(IAnsiConsole console, ApiNamespacesOutput output)
    {
        foreach (string ns in output.Namespaces)
        {
            console.WriteLine(ns);
        }
    }

    public static void RenderPackages(IAnsiConsole console, ApiPackagesOutput output)
    {
        console.WriteLine($"Packages for project '{output.ProjectName}' ({output.Packages.Count}):");
        foreach (ApiPackageSummary package in output.Packages)
        {
            string detail = package.Status switch
            {
                "ok" => $"{package.TotalTypes} types, {package.TotalMembers} members",
                // A partially-parsed package still has usable types. Falling through to
                // "(cache missing)" reports indexed data as absent, which sends the
                // caller off to re-index something that is already there.
                "incomplete" => $"{package.TotalTypes} types, {package.TotalMembers} members (partial -- some files could not be parsed)",
                "meta-unreadable" => "(meta unreadable)",
                _ => "(cache missing)",
            };
            console.WriteLine($"  {package.Id}@{package.Version} -- {detail}");
        }
    }

    public static void RenderStats(IAnsiConsole console, ApiStatsOutput output)
    {
        console.WriteLine($"WinMD Index Statistics -- {output.ProjectName}");
        console.WriteLine("======================================");
        console.WriteLine($"  Packages:   {output.Packages}");
        console.WriteLine($"  Namespaces: {output.Namespaces} (may overlap across packages)");
        console.WriteLine($"  Types:      {output.Types}");
        console.WriteLine($"  Members:    {output.Members}");
        console.WriteLine($"  WinMD files: {output.WinMdFiles}");
    }

    public static void RenderProjects(IAnsiConsole console, ApiProjectsOutput output)
    {
        if (output.Projects.Count == 0)
        {
            console.WriteLine("No projects indexed.");
            console.WriteLine("Queries from a directory with no project use the Windows SDK scope automatically.");
            return;
        }
        console.WriteLine($"Indexed projects ({output.Projects.Count}):");
        foreach (ApiProjectSummary project in output.Projects)
        {
            console.WriteLine($"  {project.Name} ({project.PackageCount} package(s))");
        }
        console.WriteLine();
        console.WriteLine("Queries from a directory with no project use the Windows SDK scope ('--project sdk').");
    }

    public static void RenderRefresh(IAnsiConsole console, ApiRefreshOutput output)
    {
        if (output.ProjectsProcessed == 0)
        {
            console.WriteLine("No projects with API metadata were found to index.");
            return;
        }
        if (output.ProjectNames is [ApiCachePaths.SdkScopeName])
        {
            // Not a project index: say what it actually is, so nobody reads
            // "Indexed 1 project(s)" and assumes their project was picked up.
            console.WriteLine($"Indexed {ApiCachePaths.SdkScopeName} metadata (no project in this directory).");
            console.WriteLine($"  Packages parsed: {output.PackagesParsed}, reused from cache: {output.PackagesReused}.");
            return;
        }
        string names = output.ProjectNames.Count > 0 ? $": {string.Join(", ", output.ProjectNames)}" : "";
        console.WriteLine($"Indexed {output.ProjectsProcessed} project(s){names}.");
        console.WriteLine($"  Packages parsed: {output.PackagesParsed}, reused from cache: {output.PackagesReused}.");
    }
}
