// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Text.Json;
using Spectre.Console;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Commands;

internal class DevToolsGetLayoutCommand : DevToolsLiveCommand, IHelpExamples
{
    public override string ShortDescription => "Read an element's measured/arranged layout";

    public IReadOnlyList<string> Examples { get; } =
    [
        "winapp devtools get-layout <selector> -a <app>",
    ];

    public static Argument<string?> SelectorArgument { get; } = new("selector")
    {
        Description = "Element to read: the selector printed in brackets, an x:Name, or a handle.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    public DevToolsGetLayoutCommand()
        : base("get-layout", "Read an element's size, position, and parent layout.")
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

            var response = target.Tap!.RequestLayout(handle, cancellationToken);
            if (!response.Ok)
            {
                return Task.FromResult(Fail(json, target.Pid, response.Error!));
            }

            if (json)
            {
                WriteJson(DevToolsJson.Result(target.Pid, response.ResultJson, w =>
                {
                    WriteIdentity(w, target, handle, cancellationToken);
                    WriteWarning(w);
                }));
                return Task.FromResult(0);
            }

            using var doc = response.TryParseResult();
            if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Task.FromResult(Fail(json, target.Pid, "The DevTools agent returned an unreadable layout."));
            }

            var root = doc.RootElement;
            DevToolsRender.WriteMarkupLine(Console, DevToolsRender.Header(handle, Describe(target, handle, cancellationToken)));
            WriteSize(root, "desired", "desired");
            WriteSize(root, "actual", "actual");
            WriteSize(root, "render", "render");
            WritePoint(root, "offset", "offset");
            WritePoint(root, "inParent", "in parent");

            if (root.TryGetProperty("parent", out var parent) && parent.ValueKind == JsonValueKind.Object)
            {
                var type = DevToolsFormat.ShortTypeName(ReadString(parent, "type"));
                var index = parent.TryGetProperty("childIndex", out var ci) && ci.TryGetInt32(out var childIndex)
                    && parent.TryGetProperty("childCount", out var cc) && cc.TryGetInt32(out var childCount)
                        ? $" (child {childIndex + 1} of {childCount})"
                        : string.Empty;
                DevToolsRender.WriteMarkupLine(Console, $"  parent: {Markup.Escape(type)}{Markup.Escape(index)}");
                WriteIfPresent(parent, "orientation", "    orientation");
                WriteIfPresent(parent, "spacing", "    spacing");
                WriteIfPresent(parent, "padding", "    padding");
                if (parent.TryGetProperty("handle", out var ph) && ph.ValueKind == JsonValueKind.String
                    && ph.GetString() is string parentHandle && parentHandle.Length > 0 && parentHandle != "0")
                {
                    DevToolsRender.WriteMarkupLine(Console, $"    {DevToolsRender.Handle(parentHandle)}");
                }
            }

            if (root.TryGetProperty("grid", out var grid) && grid.ValueKind == JsonValueKind.Object)
            {
                var cells = new List<string>();
                foreach (var field in new[] { "row", "column", "rowSpan", "columnSpan" })
                {
                    if (grid.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String
                        && v.GetString() is string s && s.Length > 0)
                    {
                        cells.Add($"{field} {s}");
                    }
                }

                if (cells.Count > 0)
                {
                    DevToolsRender.WriteMarkupLine(Console, $"  grid: {Markup.Escape(string.Join(", ", cells))}");
                }
            }

            return Task.FromResult(0);
        }

        private void WriteSize(JsonElement root, string field, string label)
        {
            if (!root.TryGetProperty(field, out var size) || size.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            DevToolsRender.WriteMarkupLine(Console, $"  {label}: {Number(size, "w")} x {Number(size, "h")}");
        }

        private void WritePoint(JsonElement root, string field, string label)
        {
            if (!root.TryGetProperty(field, out var point) || point.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            DevToolsRender.WriteMarkupLine(Console, $"  {label}: {Number(point, "x")}, {Number(point, "y")}");
        }

        private void WriteIfPresent(JsonElement element, string field, string label)
        {
            if (element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
                && value.GetString() is string text && text.Length > 0)
            {
                DevToolsRender.WriteMarkupLine(Console, $"{label}: {Markup.Escape(text)}");
            }
        }

        private static string Number(JsonElement element, string field) =>
            element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number
                ? value.GetDouble().ToString("0.##", CultureInfo.InvariantCulture)
                : "?";

        private static string ReadString(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
    }
}
