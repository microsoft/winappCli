// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;
using Spectre.Console;
using WinApp.Cli.Services.DevTools;
using WinApp.Cli.Services.DevTools.Comments;

namespace WinApp.Cli.Commands;

internal class DevToolsGetSourceCommand : DevToolsLiveCommand, IHelpExamples
{
    public override string ShortDescription => "Read the XAML or C# line an element was declared at";

    public IReadOnlyList<string> Examples { get; } =
    [
        "winapp devtools get-source <selector> -a <app>",
    ];

    public static Argument<string?> SelectorArgument { get; } = new("selector")
    {
        Description = "Element to read: the selector printed in brackets, an x:Name, an AutomationId, or a handle.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    public DevToolsGetSourceCommand()
        : base("get-source", "Find an element's XAML declaration, or the Reactor C# call site that created it, when source information is available.")
    {
        Arguments.Add(SelectorArgument);
    }

    public class Handler(
        IDevToolsTargetResolver resolver,
        IAnsiConsole ansiConsole) : LiveHandler(resolver, ansiConsole)
    {
        protected override Task<int> RunAsync(
            DevToolsTarget target,
            ParseResult parseResult,
            bool json,
            CancellationToken cancellationToken)
        {
            var handle = RequireHandle(
                target,
                parseResult.GetValue(SelectorArgument),
                json,
                "Run `winapp devtools inspect` to list them.",
                out var exitCode, cancellationToken);
            if (handle is null)
            {
                return Task.FromResult(exitCode);
            }

            var response = target.Tap!.RequestSource(handle, cancellationToken);
            if (!response.Ok)
            {
                return Task.FromResult(Fail(json, target.Pid, response.Error!));
            }

            using var doc = response.TryParseResult();
            if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Task.FromResult(Fail(json, target.Pid, "The DevTools agent returned an unreadable source record."));
            }

            var root = doc.RootElement;
            var file = ReadString(root, "fileName");
            var line = ReadInt(root, "lineNumber");
            var column = ReadInt(root, "columnNumber");
            var authoredState = ReadString(root, "authoredState");
            var authoredLine = ReadInt(root, "authoredLineNumber");
            var authoredEndLine = ReadInt(root, "authoredEndLineNumber");
            var authoredColumn = ReadInt(root, "authoredColumnNumber");
            var provenance = ReadString(root, "coordinateProvenance") switch
            {
                "disk-matched-unique-declaration" => "disk-matched",
                "likely-source-line" => "likely",
                _ => "",
            };
            var sourceEvidence = ReadString(root, "sourceEvidence");
            var xaml = ReadString(root, "xaml");

            // ONE verdict for both shapes: no source record is a failure in the human path, and `--json` used
            // to report ok:true with fileName:"" and exit 0 for exactly that.
            var hasSource = file.Length > 0;
            var noSourceToken = "source-unavailable";
            // Without source information for the whole app (DevTools attached after it started) no element has a
            // source, so that is the reason, not this element being framework XAML.
            var xamlUninstrumented = !hasSource &&
                DevToolsAppAuthored.Parse(target.Tap!.RequestAppAuthored(cancellationToken).ResultJson) is { SourceInstrumented: false };
            var noSource = xamlUninstrumented
                    ? "No XAML source is recorded for this element because this app reports no XAML source information. " +
                      DevToolsAppAuthored.RelaunchForSource
                    : "No XAML source is recorded for this element. Framework, template or generated elements " +
                      "may have no app-authored declaration.";
            var confirmed = authoredLine > 0 && authoredColumn > 0 && provenance.Length > 0;
            var projectFile = confirmed && ReadString(root, "authoredFileName") is { Length: > 0 } authoredFile
                ? authoredFile
                : DevToolsFormat.ShortFileName(file) ?? file;
            var endLine = Math.Max(authoredLine, authoredEndLine);
            var path = hasSource ? DevToolsJson.ProjectPath(DevToolsJson.SourceRoot(target.Tap!, cancellationToken), projectFile) : null;

            if (ReadString(root, "kind") == "csharp" && hasSource)
            {
                return Task.FromResult(WriteCSharpSource(target, handle, root, file, line, column, json, cancellationToken));
            }

            if (!hasSource)
            {
                // A Reactor app may hold the answer the agent could not give: say which Reactor fact is missing.
                var describeResponse = target.Tap!.RequestReactorDescribe(cancellationToken);
                var described = describeResponse.Ok ? ReactorDescription.Parse(describeResponse.ResultJson) : null;
                var reactorNode = described is { IsReactor: true } && described.ByHandle.TryGetValue(handle, out var node) ? node : null;
                var missing = described?.ExplainMissingSource();
                if (described is { Present: true, IsReactor: false })
                {
                    noSourceToken = ReactorDescription.UnavailableToken;
                    noSource = described.ExplainUnavailable();
                }
                else if (missing is not null)
                {
                    // Without any C# locations that is the reason, unless this is a XAML element and XAML recorded no
                    // sources either (a late attach): then the XAML reason, which says how to relaunch, applies.
                    if (reactorNode is not null || !xamlUninstrumented)
                    {
                        noSourceToken = ReactorDescription.NoSourceMapToken;
                        noSource = missing;
                    }
                }
                else if (reactorNode is { File: null })
                {
                    // Other elements have call sites, so source mapping is on: Reactor did not stamp this one. Reactor
                    // builds before microsoft/microsoft-ui-reactor#1405 dropped them after a Hot Reload edit.
                    noSource = "Reactor recorded no C# call site for this element. If Hot Reload changed the method that " +
                        "creates it, restart the app to record it (Ctrl+R under `winapp run --watch`), or update Reactor.";
                }
                else if (described is { IsReactor: true })
                {
                    // A Reactor app can have no XAML at all, so missing XAML source information says nothing
                    // about how it was launched: this element just has no authored declaration.
                    noSource = "No XAML declaration or Reactor call site is recorded for this element. Framework, template or " +
                        "generated elements may have no app-authored declaration.";
                }
            }

            if (json)
            {
                WriteJson(DevToolsJson.Serialize(writer =>
                {
                    writer.WriteBoolean("ok", hasSource);
                    writer.WriteNumber("processId", target.Pid);
                    WriteIdentity(writer, target, handle, cancellationToken);
                    WriteWarning(writer);
                    if (hasSource)
                    {
                        writer.WriteString("file", projectFile);
                        if (path is not null)
                        {
                            writer.WriteString("path", path);
                        }
                        if (confirmed)
                        {
                            writer.WriteNumber("line", authoredLine);
                            writer.WriteNumber("endLine", endLine);
                            writer.WriteNumber("column", authoredColumn);
                            writer.WriteString("provenance", provenance);
                        }
                    }

                    if (authoredState.Length > 0)
                    {
                        writer.WriteString("authoredState", authoredState);
                    }
                    if (sourceEvidence.Length > 0) { writer.WriteString("sourceEvidence", sourceEvidence); }

                    if (xaml.Length > 0)
                    {
                        writer.WriteString("xaml", xaml);
                    }
                    else if (hasSource && authoredState.Length > 0 && authoredState != "available")
                    {
                        writer.WriteString("xamlUnavailable", DescribeAuthoredState(authoredState, projectFile));
                    }

                    if (hasSource)
                    {
                        // Where the runtime recorded the element: the end of its start tag, not its declaration.
                        writer.WriteStartObject("runtime");
                        writer.WriteString("file", file);
                        writer.WriteNumber("line", line);
                        writer.WriteNumber("column", column);
                        writer.WriteEndObject();
                    }
                    else
                    {
                        DevToolsJson.WriteError(writer, noSourceToken, noSource);
                    }
                }));
                return Task.FromResult(hasSource ? 0 : 1);
            }

            DevToolsRender.WriteMarkupLine(Console, DevToolsRender.Header(handle, Describe(target, handle, cancellationToken)));
            if (!hasSource)
            {
                Console.MarkupLineInterpolated($"  [yellow]{noSource}[/]");
                return Task.FromResult(1);
            }

            if (confirmed)
            {
                var range = endLine > authoredLine ? $"{authoredLine}-{endLine}" : $"{authoredLine}";
                var tier = provenance == "likely" ? " [yellow](likely source; confirmation required for comments)[/]" : "";
                DevToolsRender.WriteMarkupLine(Console, $"  {Markup.Escape(projectFile)}:{range}{tier}");
                if (sourceEvidence.Length > 0) { DevToolsRender.WriteMarkupLine(Console, $"  [grey]{Markup.Escape(sourceEvidence)}[/]"); }
            }
            else if (xaml.Length > 0)
            {
                DevToolsRender.WriteMarkupLine(Console, $"  {Markup.Escape(projectFile)} [grey](declaration line not confirmed for this build)[/]");
            }
            else
            {
                var reason = authoredState.Length > 0 && authoredState != "available"
                    ? DescribeAuthoredState(authoredState, projectFile)
                    : "The declaration is not confirmed.";
                DevToolsRender.WriteMarkupLine(Console, $"  {Markup.Escape(projectFile)}: [yellow]{Markup.Escape(reason)}[/]");
            }

            if (xaml.Length > 0)
            {
                Console.WriteLine();
                foreach (var xamlLine in xaml.Replace("\r\n", "\n").Split('\n'))
                {
                    DevToolsRender.WriteMarkupLine(Console, $"  [grey]{Markup.Escape(xamlLine)}[/]");
                }
            }

            return Task.FromResult(0);
        }

        // The C# a Microsoft.UI.Reactor element was created by: its own call site, or the nearest Reactor element's.
        private int WriteCSharpSource(DevToolsTarget target, string handle, JsonElement root, string file, int line, int column,
            bool json, CancellationToken cancellationToken)
        {
            var reactor = root.TryGetProperty("reactor", out var r) && r.ValueKind == JsonValueKind.Object ? r : default;
            var element = reactor.ValueKind == JsonValueKind.Object ? ReadString(reactor, "element") : "";
            var component = reactor.ValueKind == JsonValueKind.Object ? ReadString(reactor, "component") : "";
            var exact = reactor.ValueKind != JsonValueKind.Object || !reactor.TryGetProperty("exact", out var e) || e.ValueKind != JsonValueKind.False;
            var code = ReadString(root, "csharp");
            var onDisk = !root.TryGetProperty("onDisk", out var disk) || disk.ValueKind != JsonValueKind.False;
            var compiled = ReadString(root, "compiledFileName");
            var authoredState = ReadString(root, "authoredState");
            var candidates = root.TryGetProperty("candidates", out var listed) && listed.ValueKind == JsonValueKind.Array
                ? listed.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToList()
                : [];
            string? path = onDisk && Path.IsPathFullyQualified(file) ? file : null;
            string? unavailable = null;
            string? changed = null;
            if (onDisk)
            {
                // The app compares the call site with the file on disk: an edit since the build moves the code.
                var lineCount = ReadInt(root, "lineCount");
                var shortName = Path.GetFileName(file.Replace('/', '\\'));
                if (authoredState == "stale")
                {
                    changed = DescribeChangedCSharp(shortName, line, lineCount, element);
                }
                else if (authoredState == "pastEnd")
                {
                    unavailable = $"{shortName} has {lineCount} lines, so it has no line {line}. A #line directive in the " +
                        "source maps this call to a line the file does not have; open the file the directive names to read it.";
                }
            }
            if (!onDisk)
            {
                // The app looked for the file under its project and the projects it references; a launch without a
                // project (a late attach) leaves that to the folder this command runs from.
                var sourceRoot = DevToolsJson.SourceRoot(target.Tap!, cancellationToken);
                var local = candidates.Count == 0 && sourceRoot is null ? ResolveRelativeSource(file, null, out candidates) : null;
                if (local is not null)
                {
                    path = local;
                    if (code.Length == 0 && line > 0)
                    {
                        try
                        {
                            var lines = CSharpSourceText.ReadLines(local, Path.GetDirectoryName(local)!);
                            if (line <= lines.Length)
                            {
                                code = CSharpSourceText.RedactLines(lines, CSharpDeclarations.ForSource(local))[line - 1].Trim();
                            }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                        {
                            // The location still tells the reader where to look.
                        }
                    }
                }
                else
                {
                    unavailable = DescribeMissingCSharp(file, candidates, sourceRoot is not null, component);
                    authoredState = candidates.Count > 1 ? "ambiguous" : "noFile";
                }
            }
            // A string passed to a password parameter is redacted even when the statement never says "password".
            if (code.Length > 0)
            {
                code = CSharpSourceText.Redact(code, CSharpDeclarations.ForSource(
                    path ?? (onDisk ? DevToolsJson.ProjectPath(DevToolsJson.SourceRoot(target.Tap!, cancellationToken), file) : null)));
            }
            if (json)
            {
                WriteJson(DevToolsJson.Serialize(writer =>
                {
                    writer.WriteBoolean("ok", true);
                    writer.WriteNumber("processId", target.Pid);
                    WriteIdentity(writer, target, handle, cancellationToken);
                    WriteWarning(writer);
                    writer.WriteString("kind", "csharp");
                    // The location vocabulary the XAML answer uses: the recorded file, its path here, line and column.
                    writer.WriteString("file", file);
                    if (path is not null) { writer.WriteString("path", path); }
                    writer.WriteNumber("line", line);
                    if (column > 0) { writer.WriteNumber("column", column); }
                    if (compiled.Length > 0) { writer.WriteString("compiledFileName", compiled); }
                    if (authoredState.Length > 0) { writer.WriteString("authoredState", authoredState); }
                    if (candidates.Count > 1)
                    {
                        writer.WriteStartArray("candidates");
                        foreach (var candidate in candidates) { writer.WriteStringValue(candidate); }
                        writer.WriteEndArray();
                    }
                    if (reactor.ValueKind == JsonValueKind.Object)
                    {
                        writer.WritePropertyName("reactor");
                        reactor.WriteTo(writer);
                    }
                    if (code.Length > 0) { writer.WriteString("csharp", code); }
                    if (changed is not null) { writer.WriteString("csharpChanged", changed); }
                    if (unavailable is not null) { writer.WriteString("csharpUnavailable", unavailable); }
                }));
                return 0;
            }

            DevToolsRender.WriteMarkupLine(Console, DevToolsRender.Header(handle, Describe(target, handle, cancellationToken)));
            var mounts = reactor.ValueKind == JsonValueKind.Object ? ReadString(reactor, "mounts") : "";
            var fileName = Path.GetFileName(file.Replace('/', '\\'));
            var location = column > 0 ? $"{fileName}:{line}:{column}" : $"{fileName}:{line}";
            var owner = component.Length > 0 ? $"<{component}> · " : "";
            var what = mounts.Length > 0 ? $"<{mounts}>" : element;
            DevToolsRender.WriteMarkupLine(Console,
                $"  {Markup.Escape(owner + location)} [grey](C#{(what.Length > 0 ? ", Reactor " + Markup.Escape(what) : "")})[/]");
            DevToolsRender.WriteMarkupLine(Console, $"  [grey]{Markup.Escape(path ?? file)}[/]");
            if (!exact)
            {
                DevToolsRender.WriteMarkupLine(Console,
                    "  [grey]This element has no Reactor call site of its own; this is the nearest Reactor element that contains it.[/]");
            }
            if (unavailable is not null)
            {
                DevToolsRender.WriteMarkupLine(Console, $"  [yellow]{Markup.Escape(unavailable)}[/]");
            }
            if (changed is not null)
            {
                DevToolsRender.WriteMarkupLine(Console, $"  [yellow]{Markup.Escape(changed)}[/]");
            }
            if (code.Length > 0)
            {
                Console.WriteLine();
                foreach (var codeLine in code.Replace("\r\n", "\n").Split('\n'))
                {
                    DevToolsRender.WriteMarkupLine(Console, $"  [grey]{Markup.Escape(codeLine)}[/]");
                }
            }
            return 0;
        }

        // A file the build wrote under an obj folder: source generator output, which is on disk only when emitted.
        internal static bool IsGenerated(string file) =>
            file.Replace('\\', '/').Split('/').Contains("obj", StringComparer.OrdinalIgnoreCase);

        // The call site no longer matches the file on disk: it was edited after the app was built.
        internal static string DescribeChangedCSharp(string file, int line, int lineCount, string element)
        {
            var call = element.Length > 0 ? $"the {element} call" : "the call";
            return lineCount > 0 && line > lineCount
                ? $"{file} changed since the app was built: it now has {lineCount} lines, and {call} was on line {line}. " +
                  "Rebuild and run the app (or apply the edit with Hot Reload) to see where it is now."
                : $"{file} changed since the app was built, and line {line} no longer holds {call}; the code below is what " +
                  "that line holds now. Rebuild and run the app (or apply the edit with Hot Reload) to see where it is now.";
        }

        private static int ReadInt(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

        // A project-relative C# path (`Pages/Main.cs`), as Reactor publishes it; not rooted and not a /_/ deterministic one.
        internal static bool IsRelative(string file) =>
            file.Length > 0 && !Path.IsPathRooted(file) && !file.StartsWith("/_", StringComparison.Ordinal);

        internal static string? FindRelativeSource(string file, string? start = null) => ResolveRelativeSource(file, start, out _);

        /// <summary>
        /// The file a project-relative path names, found from <paramref name="start"/> (the current folder): Reactor
        /// records a path relative to the project that compiled it, which is the project at or above the folder or one
        /// it references. Null when no project is found there, or when more than one of those projects has the file
        /// (listed in <paramref name="candidates"/>): a same-named file in another project is not this one. A path
        /// relative to the repository root is looked for in the folders above the project.
        /// </summary>
        internal static string? ResolveRelativeSource(string file, string? start, out List<string> candidates)
        {
            candidates = [];
            if (!IsRelative(file) || file.Contains(':'))
            {
                return null;
            }
            var project = FindProjectFolder(start ?? Directory.GetCurrentDirectory());
            if (project is null)
            {
                return null;
            }
            var relative = file.Replace('/', Path.DirectorySeparatorChar);
            var climbs = relative.Split(Path.DirectorySeparatorChar).Contains("..");
            var bound = CommentStoreLocator.Resolve(project) is { Kind: CommentStoreRootKind.RepositoryRoot } repository
                ? repository.Root
                : Path.GetDirectoryName(project) ?? project;
            var folders = new List<string> { project };
            folders.AddRange(ReactorBuildFacts.ProjectDirectories(project,
                ReactorBuildFacts.ReadProjectReferences(Path.Combine(project, "obj", "project.assets.json"))));
            foreach (var folder in folders)
            {
                var candidate = Path.GetFullPath(Path.Combine(folder, relative));
                if (File.Exists(candidate) && (!climbs || IsUnder(bound, candidate)) &&
                    !candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                {
                    candidates.Add(candidate);
                }
            }
            if (candidates.Count == 1)
            {
                return candidates[0];
            }
            if (candidates.Count > 1 || climbs)
            {
                return null;
            }
            for (var folder = Directory.GetParent(project); folder is not null; folder = folder.Parent)
            {
                var candidate = Path.Combine(folder.FullName, relative);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            return null;

            static bool IsUnder(string root, string path) =>
                path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        // The nearest folder at or above `start` with exactly one C# project file.
        private static string? FindProjectFolder(string start)
        {
            for (var folder = new DirectoryInfo(start); folder is not null; folder = folder.Parent)
            {
                try
                {
                    if (folder.Exists && folder.EnumerateFiles("*.csproj").Take(2).Count() == 1)
                    {
                        return folder.FullName;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return null;
                }
            }
            return null;
        }

        // Why the C# at a call site cannot be shown, and what to do about it.
        internal static string DescribeMissingCSharp(string file, IReadOnlyList<string> candidates, bool projectKnown, string component)
        {
            if (candidates.Count > 1)
            {
                var owner = component.Length > 0 ? $" Open the one in the project that defines <{component}>." : "";
                return $"The build recorded {file} relative to the project that compiled it, and more than one of this app's " +
                    $"projects has that file: {string.Join(", ", candidates)}. DevTools cannot tell which one created this element, " +
                    $"so it shows no code.{owner}";
            }
            if (IsGenerated(file))
            {
                return $"This call site is in code a source generator produced during the build ({Path.GetFileName(file.Replace('/', '\\'))}). " +
                    "The build keeps that code only in the app's PDB, not on disk. To read it, set " +
                    "<EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles> in the project that compiles it and rebuild.";
            }
            if (!IsRelative(file))
            {
                return "This file is not on this machine at the path the build recorded. Build the app here to read the code.";
            }
            return projectKnown
                ? $"The build recorded {file} relative to the project that compiled it, and DevTools did not find it in this app's " +
                  "project or the projects it references. If the file moved, rebuild and run the app again."
                : $"The build recorded {file} relative to the project that compiled it. Run this command from the app's project " +
                  "folder, or launch the app with `winapp run` and DevTools on so DevTools knows its projects.";
        }

        private static string DescribeAuthoredState(string state, string file) => state switch
        {
            "noFile" => $"Not confirmed: {file} could not be opened from here.",
            "noSourceInfo" => "Not confirmed: the runtime reported no XAML source text for this element.",
            "stale" => $"Not confirmed: {file} changed since the app was built. Rebuild and relaunch the app (winapp run) to confirm the declaration.",
            "unverifiedBuild" => $"Not confirmed: DevTools could not confirm that {file} matches the running build. " +
                "Rebuild and run again (without --no-build); if this persists, check the original XAML before editing.",
            _ => $"Not confirmed ({state}).",
        };

        private static string ReadString(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
    }
}
