// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;

namespace WinApp.Cli.Services.DevTools;

/// <summary>
/// Builds the <c>--json</c> payloads for the agent-facing <c>winapp devtools</c> commands with
/// <see cref="Utf8JsonWriter"/> — reflection-free and AOT-safe, and able to copy a DevTools result through
/// VERBATIM rather than round-tripping it through a DTO that would quietly drop any field the CLI does not
/// model. Every payload carries <c>ok</c> and the target <c>pid</c>; a failure carries the DevTools
/// <c>{code, token, message}</c> whole.
/// </summary>
internal static class DevToolsJson
{
    public static string Serialize(Action<Utf8JsonWriter> writeBody)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, NewLine = "\n", MaxDepth = TapWireJson.MaxDepth,
            // XAML travels in these payloads; "<Button Content=\"Save\" />" should read as XAML, not \u003C escapes.
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writeBody(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string Result(int pid, string? resultJson, Action<Utf8JsonWriter>? extra = null) =>
        Result(pid, true, resultJson, extra);

    /// <summary>
    /// A command whose payload is the DevTools result copied through unchanged, with the verdict supplied by the
    /// caller. A command whose human path calls an outcome a failure MUST pass <c>ok: false</c> here — a
    /// <c>--json</c> consumer that sees <c>ok:true</c> for the same run has no usable signal at all.
    /// </summary>
    public static string Result(int pid, bool ok, string? resultJson, Action<Utf8JsonWriter>? extra = null) =>
        Serialize(writer =>
        {
            writer.WriteBoolean("ok", ok);
            writer.WriteNumber("processId", pid);
            extra?.Invoke(writer);
            writer.WritePropertyName("result");
            WriteRaw(writer, resultJson);
        });

    public static string Error(int pid, DevToolsProtocolError error) =>
        Serialize(writer =>
        {
            writer.WriteBoolean("ok", false);
            writer.WriteNumber("processId", pid);
            WriteError(writer, error);
        });

    public static string Error(int pid, string message, string token = "cli") =>
        Error(pid, new DevToolsProtocolError(0, token, message));

    /// <summary>
    /// Writes the <c>error</c> member. It is ALWAYS an object of <c>{code, token, message}</c>, whatever
    /// produced the failure.
    /// <para>
    /// A semantic outcome — nothing matched, no source recorded, a binding that could not be diagnosed — is
    /// just as much a failure as a protocol refusal, and emitting one as a bare string while the other is an
    /// object means an agent that branches on <c>error.token</c> throws on the ordinary no-result case. CLI-side
    /// failures carry <c>code: 0</c>, because no JSON-RPC exchange assigned them one; the token is the stable
    /// thing to branch on.
    /// </para>
    /// </summary>
    public static void WriteError(Utf8JsonWriter writer, DevToolsProtocolError error)
    {
        writer.WriteStartObject("error");
        writer.WriteNumber("code", error.Code);
        writer.WriteString("token", error.Token);
        writer.WriteString("message", error.Message);
        writer.WriteEndObject();
    }

    public static void WriteError(Utf8JsonWriter writer, string token, string message) =>
        WriteError(writer, new DevToolsProtocolError(0, token, message));

    public static void WriteRaw(Utf8JsonWriter writer, string? rawJson)
    {
        if (string.IsNullOrEmpty(rawJson))
        {
            writer.WriteNullValue();
            return;
        }

        using var doc = JsonDocument.Parse(rawJson, TapWireJson.DocumentOptions);
        doc.RootElement.WriteTo(writer);
    }

    /// <summary>
    /// An element's XAML file (project-relative), its confirmed declaration lines, and its AutomationId: the same names
    /// get-source uses. Lines are written only for a declaration the build map confirms.
    /// </summary>
    public static void WriteLocation(Utf8JsonWriter writer, string? file, DevToolsElementFacts? facts)
    {
        if (file is not null)
        {
            writer.WriteString("file", file);
        }
        if (facts?.Line > 0)
        {
            writer.WriteNumber("line", facts.Line);
            writer.WriteNumber("endLine", facts.EndLine);
            writer.WriteNumber("column", facts.Column);
        }
        if (facts?.AutomationId is { } automationId)
        {
            writer.WriteString("automationId", automationId);
        }
    }

    /// <summary>The app's project folder as the agent reports it, or null when it is unknown.</summary>
    public static string? SourceRoot(VisualTreeTap tap, CancellationToken cancellationToken)
    {
        try
        {
            var root = CommentSelectionCapture.ReadStringResult(tap.GetSourceRoot(cancellationToken), "sourceRoot");
            return root.Length == 0 ? null : root;
        }
        catch (Exception ex) when (ex is DevToolsProtocolException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The absolute path of a project file, when the project folder is known and the file exists.</summary>
    public static string? ProjectPath(string? sourceRoot, string file)
    {
        try
        {
            var path = sourceRoot is null ? null : Path.GetFullPath(Path.Combine(sourceRoot, file));
            return path is not null && File.Exists(path) ? path : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Whether a property row (or one of its children) names a source file in its chain.</summary>
    public static bool HasChainFile(JsonElement row) =>
        (row.TryGetProperty("chain", out var chain) && chain.ValueKind == JsonValueKind.Array &&
            chain.EnumerateArray().Any(entry => entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("file", out _))) ||
        (row.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array &&
            children.EnumerateArray().Any(child => child.ValueKind == JsonValueKind.Object && HasChainFile(child)));

    /// <summary>
    /// Copies a property row's wire fields, rewriting each <c>chain</c> entry's runtime position into the location
    /// vocabulary the other commands use: project-relative <c>file</c>, <c>path</c>, the declaration's start
    /// <c>line</c> when confirmed, and the raw position under <c>runtime</c>.
    /// </summary>
    public static void WritePropertyFields(Utf8JsonWriter writer, JsonElement row, string? sourceRoot)
    {
        foreach (var field in row.EnumerateObject())
        {
            if (field.Name == "chain" && field.Value.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray("chain");
                foreach (var entry in field.Value.EnumerateArray())
                {
                    WriteChainEntry(writer, entry, sourceRoot);
                }
                writer.WriteEndArray();
            }
            else if (field.Name == "children" && field.Value.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray("children");
                foreach (var child in field.Value.EnumerateArray())
                {
                    writer.WriteStartObject();
                    WritePropertyFields(writer, child, sourceRoot);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            else
            {
                field.WriteTo(writer);
            }
        }
    }

    private static void WriteChainEntry(Utf8JsonWriter writer, JsonElement entry, string? sourceRoot)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            entry.WriteTo(writer);
            return;
        }
        writer.WriteStartObject();
        foreach (var field in entry.EnumerateObject())
        {
            if (field.Name is not ("file" or "line" or "authoredFileName" or "authoredLineNumber"))
            {
                field.WriteTo(writer);
            }
        }
        if (entry.TryGetProperty("file", out var raw) && raw.ValueKind == JsonValueKind.String &&
            raw.GetString() is { Length: > 0 } runtimeFile)
        {
            var authoredLine = entry.TryGetProperty("authoredLineNumber", out var a) && a.ValueKind == JsonValueKind.Number &&
                a.TryGetInt32(out var n) ? n : 0;
            var file = authoredLine > 0 && entry.TryGetProperty("authoredFileName", out var f) && f.ValueKind == JsonValueKind.String &&
                f.GetString() is { Length: > 0 } authored
                ? authored
                : DevToolsFormat.ShortFileName(runtimeFile) ?? runtimeFile;
            writer.WriteString("file", file);
            if (ProjectPath(sourceRoot, file) is { } path)
            {
                writer.WriteString("path", path);
            }
            if (authoredLine > 0)
            {
                writer.WriteNumber("line", authoredLine);
            }
            writer.WriteStartObject("runtime");
            writer.WriteString("file", runtimeFile);
            if (entry.TryGetProperty("line", out var line) && line.ValueKind == JsonValueKind.Number && line.TryGetInt32(out var runtimeLine))
            {
                writer.WriteNumber("line", runtimeLine);
            }
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    public static void WriteNode(Utf8JsonWriter writer, VisualTreeNode node)
    {
        writer.WriteStartObject();
        // BOTH identities, always. `selector` is what a human reads and retypes; `handle` is the exact
        // protocol identity every DevTools verb accepts and the escape hatch when a selector has gone stale.
        // A payload carrying only one of them forces the caller to choose between readable and precise.
        writer.WriteString("selector", node.Selector);
        writer.WriteString("handle", node.Handle);
        if (node.Id is not null)
        {
            writer.WriteString("id", node.Id);
        }

        if (node.Name.Length > 0)
        {
            writer.WriteString("name", node.Name);
            writer.WriteBoolean("uniqueName", node.UniqueName);
        }

        writer.WriteString("type", node.Type);
        WriteLocation(writer, node.ShortFile, node.Facts);

        writer.WriteNumber("childCount", node.ChildCount);
        if (node.HiddenChildren > 0)
        {
            writer.WriteNumber("hiddenChildren", node.HiddenChildren);
        }

        if (node.Preview.Count > 0)
        {
            writer.WriteStartArray("preview");
            foreach (var value in node.Preview)
            {
                writer.WriteStartObject();
                writer.WriteString("name", value.Name);
                writer.WriteString("value", value.Value);
                writer.WriteString("valueState", value.ValueState);
                writer.WriteString("valueType", value.ValueType);
                writer.WriteString("bindingState", "unknown");
                writer.WriteBoolean("truncated", value.Truncated);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        writer.WriteStartArray("children");
        foreach (var child in node.Children)
        {
            WriteNode(writer, child);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
