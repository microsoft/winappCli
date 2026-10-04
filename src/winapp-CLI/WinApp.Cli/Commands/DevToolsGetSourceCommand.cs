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
            const string NoSource =
                "No XAML source is recorded for this element. Framework, template or generated elements " +
                "may have no app-authored declaration.";
            var confirmed = authoredLine > 0 && authoredColumn > 0 && provenance.Length > 0;
            var projectFile = confirmed && ReadString(root, "authoredFileName") is { Length: > 0 } authoredFile
                ? authoredFile
                : DevToolsFormat.ShortFileName(file) ?? file;
            var endLine = Math.Max(authoredLine, authoredEndLine);
            var path = hasSource ? ProjectPath(target, projectFile, cancellationToken) : null;

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

        // The absolute path of a project file, when the app's project is known and the file exists.
        private static string? ProjectPath(DevToolsTarget target, string file, CancellationToken cancellationToken)
        {
            try
            {
                var root = CommentSelectionCapture.ReadStringResult(target.Tap!.GetSourceRoot(cancellationToken), "sourceRoot");
                var path = root.Length == 0 ? null : Path.GetFullPath(Path.Combine(root, file));
                return path is not null && File.Exists(path) ? path : null;
            }
            catch (Exception ex) when (ex is DevToolsProtocolException or JsonException or ArgumentException or IOException)
            {
                return null;
            }
        }

        private static int ReadInt(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

        private static string DescribeAuthoredState(string state, string file) => state switch
        {
            "noFile" => $"Not confirmed: {file} could not be opened from here.",
            "noSourceInfo" => "Not confirmed: the runtime reported no XAML source text for this element.",
            "stale" => $"Not confirmed: {file} changed since the app was built. Rebuild to confirm the declaration.",
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
