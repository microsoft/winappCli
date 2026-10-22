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
    public override string ShortDescription => "Read the XAML or Reactor C# line an element was declared at";

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
            var noSource = !hasSource &&
                DevToolsAppAuthored.Parse(target.Tap!.RequestAppAuthored(cancellationToken).ResultJson) is { SourceInstrumented: false }
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
                if (described is { Present: true, IsReactor: false })
                {
                    noSourceToken = ReactorDescription.UnavailableToken;
                    noSource = described.ExplainUnavailable();
                }
                else if (described?.ExplainMissingSource() is { } missing)
                {
                    noSourceToken = ReactorDescription.NoSourceMapToken;
                    noSource = missing;
                }
                else if (described is { IsReactor: true } && described.ByHandle.TryGetValue(handle, out var reactorNode) &&
                    reactorNode.File is null)
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
                    var path = onDisk && Path.IsPathFullyQualified(file) ? file
                        : DevToolsJson.ProjectPath(DevToolsJson.SourceRoot(target.Tap!, cancellationToken), file) ?? FindRelativeSource(file);
                    if (path is not null) { writer.WriteString("path", path); }
                    writer.WriteNumber("line", line);
                    if (column > 0) { writer.WriteNumber("column", column); }
                    if (compiled.Length > 0) { writer.WriteString("compiledFileName", compiled); }
                    if (reactor.ValueKind == JsonValueKind.Object)
                    {
                        writer.WritePropertyName("reactor");
                        reactor.WriteTo(writer);
                    }
                    if (code.Length > 0) { writer.WriteString("csharp", code); }
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
            DevToolsRender.WriteMarkupLine(Console, $"  [grey]{Markup.Escape(file)}[/]");
            if (!exact)
            {
                DevToolsRender.WriteMarkupLine(Console,
                    "  [grey]This element has no Reactor call site of its own; this is the nearest Reactor element that contains it.[/]");
            }
            if (!onDisk)
            {
                // Reactor records C# paths relative to the project. A late attach does not know the project, so look
                // for the file from where the command runs; the line shows where it is.
                if (FindRelativeSource(file) is { } local)
                {
                    DevToolsRender.WriteMarkupLine(Console, $"  [grey]Found here: {Markup.Escape(local)}[/]");
                    if (code.Length == 0 && line > 0 && File.ReadLines(local).Skip(line - 1).FirstOrDefault() is { } text)
                    {
                        code = CSharpSourceText.Redact(text.Trim());
                    }
                }
                else
                {
                    DevToolsRender.WriteMarkupLine(Console, IsRelative(file)
                        ? $"  [grey]The build recorded {Markup.Escape(file)} relative to its project. Run this command from the project folder, or launch the app with `winapp run` and DevTools on so DevTools knows where it is.[/]"
                        : "  [grey]This file is not on this machine at the path the build recorded. Build the app here to read the code.[/]");
                }
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

        private static int ReadInt(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

        // A project-relative C# path (`Pages/Main.cs`), as Reactor publishes it; not rooted and not a /_/ deterministic one.
        internal static bool IsRelative(string file) =>
            file.Length > 0 && !Path.IsPathRooted(file) && !file.StartsWith("/_", StringComparison.Ordinal);

        // The relative file under the current folder or one above it, without leaving those folders.
        internal static string? FindRelativeSource(string file, string? start = null)
        {
            if (!IsRelative(file) || file.Split('/', '\\').Any(segment => segment == ".."))
            {
                return null;
            }
            for (var folder = new DirectoryInfo(start ?? Directory.GetCurrentDirectory()); folder is not null; folder = folder.Parent)
            {
                var candidate = Path.Combine(folder.FullName, file.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            return null;
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
