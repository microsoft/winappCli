// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;
using Spectre.Console;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Commands;

internal class DevToolsGetSourceCommand : DevToolsLiveCommand, IHelpExamples
{
    public override string ShortDescription => "Read the XAML file and line an element was declared at";

    public IReadOnlyList<string> Examples { get; } =
    [
        "winapp devtools get-source <selector> -a <app>",
    ];

    public static Argument<string?> SelectorArgument { get; } = new("selector")
    {
        Description = "Element to read: the selector printed in brackets, an x:Name, or a handle.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    public DevToolsGetSourceCommand()
        : base("get-source", "Find an element's XAML declaration, when source information is available.")
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
            var line = root.TryGetProperty("lineNumber", out var l) && l.TryGetInt32(out var lineNumber) ? lineNumber : 0;
            var column = root.TryGetProperty("columnNumber", out var c) && c.TryGetInt32(out var col) ? col : 0;
            var authoredState = ReadString(root, "authoredState");
            var authoredLine = root.TryGetProperty("authoredLineNumber", out var al) && al.TryGetInt32(out var authoredLineNumber) ? authoredLineNumber : 0;
            var authoredColumn = root.TryGetProperty("authoredColumnNumber", out var ac) && ac.TryGetInt32(out var authoredCol) ? authoredCol : 0;
            var coordinateProvenance = ReadString(root, "coordinateProvenance");
            var authoredFile = ReadString(root, "authoredFileName");
            var sourceEvidence = ReadString(root, "sourceEvidence");
            var xaml = ReadString(root, "xaml");

            // ONE verdict for both shapes: no source record is a failure in the human path, and `--json` used
            // to report ok:true with fileName:"" and exit 0 for exactly that.
            var hasSource = file.Length > 0;
            const string NoSource =
                "No XAML source is recorded for this element. Framework, template or generated elements " +
                "may have no app-authored declaration.";

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
                        writer.WriteString("fileName", file);
                        writer.WriteNumber("lineNumber", line);
                        writer.WriteNumber("columnNumber", column);
                    }

                    if (authoredState.Length > 0)
                    {
                        writer.WriteString("authoredState", authoredState);
                    }
                    if (sourceEvidence.Length > 0) { writer.WriteString("sourceEvidence", sourceEvidence); }
                    if (authoredLine > 0 && authoredColumn > 0 && coordinateProvenance.Length > 0)
                    {
                        writer.WriteNumber("authoredLineNumber", authoredLine);
                        writer.WriteNumber("authoredColumnNumber", authoredColumn);
                        writer.WriteString("coordinateProvenance", coordinateProvenance);
                        if (authoredFile.Length > 0) { writer.WriteString("authoredFileName", authoredFile); }
                    }

                    if (xaml.Length > 0)
                    {
                        writer.WriteString("xaml", xaml);
                    }
                    else if (hasSource && authoredState.Length > 0 && authoredState != "available")
                    {
                        writer.WriteString("xamlUnavailable", DescribeAuthoredState(authoredState, ShortFile(file)));
                    }

                    if (!hasSource)
                    {
                        DevToolsJson.WriteError(writer, "source-unavailable", NoSource);
                    }
                }));
                return Task.FromResult(hasSource ? 0 : 1);
            }

            DevToolsRender.WriteMarkupLine(Console, DevToolsRender.Header(handle, Describe(target, handle, cancellationToken)));
            if (!hasSource)
            {
                Console.MarkupLineInterpolated($"  [yellow]{NoSource}[/]");
                return Task.FromResult(1);
            }

            var shortFile = ShortFile(file);
            DevToolsRender.WriteMarkupLine(Console, $"  {Markup.Escape(shortFile)}:{line}:{column}");
            if (authoredLine > 0 && authoredColumn > 0 && coordinateProvenance.Length > 0)
            {
                var tier = authoredState == "likely" ? "likely source; confirmation required for comments" : "disk-matched";
                DevToolsRender.WriteMarkupLine(Console, $"  Authored declaration: {Markup.Escape(authoredFile.Length > 0 ? authoredFile : shortFile)}:{authoredLine}:{authoredColumn} ({tier})");
                if (sourceEvidence.Length > 0) { DevToolsRender.WriteMarkupLine(Console, $"  [grey]{Markup.Escape(sourceEvidence)}[/]"); }
                Console.MarkupLine("  [grey]The running app may retain different XAML after a rebuild or hot reload.[/]");
            }

            // authoredState says whether the AUTHORED MARKUP could be read, which is a different question from
            // whether the file/line is known. Conflating them would let a missing `xaml` block read as "this
            // element declares nothing", so each state gets its own sentence.
            if (xaml.Length > 0)
            {
                Console.WriteLine();
                foreach (var xamlLine in xaml.Replace("\r\n", "\n").Split('\n'))
                {
                    DevToolsRender.WriteMarkupLine(Console, $"  [grey]{Markup.Escape(xamlLine)}[/]");
                }
            }
            else if (authoredState.Length > 0 && authoredState != "available")
            {
                DevToolsRender.WriteMarkupLine(Console, $"  [grey]{Markup.Escape(DescribeAuthoredState(authoredState, shortFile))}[/]");
            }

            return Task.FromResult(0);
        }

        private static string ShortFile(string file) => DevToolsFormat.ShortFileName(file) ?? file;

        private static string DescribeAuthoredState(string state, string file) => state switch
        {
            "noFile" => $"The authored markup is not shown: {file} could not be opened from here.",
            "noSourceInfo" => "The authored markup is not shown: the runtime reported no XAML source text for this element.",
            "stale" => $"The authored markup is not shown: {file} no longer matches the inspected build.",
            "unverifiedBuild" => $"The authored markup is not shown: DevTools could not confirm that {file} matches the running build. " +
                "Rebuild and run again (without --no-build); if this persists, check the original XAML before editing.",
            _ => $"The authored markup is not shown ({state}).",
        };

        private static string ReadString(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
    }
}
