// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinApp.Cli.Services.DevTools.Comments;

namespace WinApp.Cli.Services.DevTools;

internal enum CaptureStatus
{
    Ok,

    NoAgent,

    /// <summary>The tap answered but nothing has been picked yet (user must click Pick and select an element).</summary>
    NoSelection,
    Failed,
}

internal sealed class CapturedElement
{
    public string? SourceRoot { get; init; }

    /// <summary>Where the app's comments are stored: <see cref="SourceRoot"/>, or the folder an app without one was launched from.</summary>
    public string? CommentRoot { get; init; }
    public string? Handle { get; init; }

    public string? Type { get; init; }

    public string? Name { get; init; }

    public string? AutomationId { get; init; }

    public string? Content { get; init; }

    public string? SourceUri { get; init; }

    public string? SourceFile { get; init; }

    public int? Line { get; init; }

    public int? Column { get; init; }
    public string? SourceProvenance { get; init; }
    public string? SourceEvidence { get; init; }
    public int? RawLine { get; init; }
    public int? RawColumn { get; init; }
    public CommentAuthoredAnchor? Authored { get; init; }

    /// <summary>
    /// The tap's opaque source-qualified anchor for placing a marker on a later run.
    /// Empty when source/parent context cannot be established; null when the tap didn't answer.
    /// </summary>
    public string? ElementPath { get; init; }

    public CommentStyleContext? Style { get; init; }

    /// <summary>Title of the window that showed the element.</summary>
    public string? Window { get; init; }

    public List<CommentBrushContext>? Brushes { get; init; }
}

internal sealed class CaptureResult
{
    public required CaptureStatus Status { get; init; }

    public CapturedElement? Element { get; init; }
    public DevToolsProtocolError? Error { get; init; }
}

internal static class CommentSelectionCapture
{
    public static CaptureResult Capture(uint pid, CancellationToken cancellationToken = default)
    {
        var tap = new VisualTreeTap(pid);
        var selection = tap.PollSelection(cancellationToken);
        if (!selection.Ok)
        {
            return Failed(selection.Error!);
        }
        using var doc = selection.TryParseResult();
        if (doc?.RootElement is not { ValueKind: JsonValueKind.Object } root ||
            !root.TryGetProperty("handle", out var value) || value.ValueKind != JsonValueKind.String)
        {
            return Failed(new(-32603, "internal", "The selection response has no element handle."));
        }
        var handle = value.GetString();
        if (string.IsNullOrEmpty(handle) || handle == "0")
        {
            return new CaptureResult { Status = CaptureStatus.NoSelection };
        }

        return CaptureHandle(tap, handle, cancellationToken);
    }

    public static CaptureResult CaptureElement(uint pid, string handleOrName, CancellationToken cancellationToken = default)
    {
        var tap = new VisualTreeTap(pid);

        var resolved = DevToolsSelector.Resolve(tap, handleOrName, cancellationToken);
        if (!resolved.Ok)
        {
            return Failed(resolved.RemoteError ?? new(-32602, "selector", resolved.Error ?? "The element selector could not be resolved."));
        }
        if (resolved.Warning is not null)
        {
            return Failed(new(-32602, "ambiguous-selector", resolved.Warning + " Pass an exact handle or identity selector to capture."));
        }
        return CaptureHandle(tap, resolved.Handle!, cancellationToken);
    }

    private static CaptureResult CaptureHandle(VisualTreeTap tap, string handle, CancellationToken cancellationToken)
    {
        try
        {
            var tree = tap.RequestEnumerate(null, null, cancellationToken: cancellationToken).RequireResult();
            var node = FindNode(tree, handle);
            if (node is null)
            {
                return Failed(new(-32000, "not-found", "The selected element is no longer in the visual tree."));
            }
            var propsJson = tap.RequestProperties(handle, cancellationToken).RequireResult();
            var content = ExtractContent(propsJson);
            var sourceJson = tap.RequestSource(handle, cancellationToken).RequireResult();
            var (uri, file, line, column) = ParseSource(sourceJson);
            var source = JsonSerializer.Deserialize(sourceJson, TapWireJson.Context.TapElementSource);
            var anchorResponse = tap.GetElementAnchor(handle, cancellationToken);
            var elementPath = ReadStringResult(anchorResponse, "anchor");
            using var anchorJson = anchorResponse.TryParseResult();
            var unique = anchorJson is not null && anchorJson.RootElement.TryGetProperty("unique", out var uniqueness) &&
                uniqueness.ValueKind == JsonValueKind.True;
            var rootResponse = tap.GetSourceRoot(cancellationToken);
            var sourceRoot = ReadStringResult(rootResponse, "sourceRoot");
            file = uri is null ? file : Comments.CommentAnchorResolver.RelativeSourcePath(uri, sourceRoot);
            CommentAuthoredAnchor? authored = null;
            string? automationId = null;
            var selfGivenName = false;
            if (source?.AuthoredState is "available" or "likely" && !string.IsNullOrEmpty(source.Xaml) &&
                !string.IsNullOrEmpty(sourceRoot) && file is not null && line is > 0 && column is > 0)
            {
                var sourceAnchor = new CommentAnchor { SourceFile = file, SourceUri = uri };
                var path = CommentAnchorResolver.ResolveKnownSourcePath(sourceRoot, sourceAnchor) ??
                    throw new InvalidDataException("The captured source does not identify one accessible project file.");
                var mapped = source.CoordinateProvenance is "disk-matched-unique-declaration" or "likely-source-line";
                var candidates = CommentAuthoredIdentity.Read(path, sourceRoot)
                    .Where(item => (mapped ? item.Line == line && item.Column == column : item.Line <= line && item.EndLine >= line) &&
                        CommentAuthoredIdentity.MatchesType(item.Element, ShortType(node.Type))).ToArray();
                var declarations = candidates.Where(item => (CommentAuthoredIdentity.Name(item.Element) ?? "") == (node.Name ?? "")).ToArray();
                // The app verified this declaration. A usage declares no name when the runtime name is one the type
                // gives itself (x:Name on its own x:Class root, such as a UserControl's RootPanel).
                if (declarations.Length == 0 && !string.IsNullOrEmpty(node.Name))
                {
                    declarations = candidates.Where(item => CommentAuthoredIdentity.Name(item.Element) is null).ToArray();
                }
                if (declarations.Length != 1)
                {
                    throw new InvalidDataException("The captured authored location changed or no longer identifies one declaration.");
                }
                // Identity follows the declaration: a self-given runtime name is not part of where to edit.
                selfGivenName = CommentAuthoredIdentity.Name(declarations[0].Element) is null && !string.IsNullOrEmpty(node.Name);
                authored = CommentAuthoredIdentity.Capture(sourceRoot, Path.GetRelativePath(sourceRoot, path),
                    declarations[0], source.Xaml, unique);
                authored.UniquenessReason = anchorJson!.RootElement.TryGetProperty("uniquenessReason", out var reason) &&
                    reason.ValueKind == JsonValueKind.String ? reason.GetString() : unique ? "unique" : "unverified";
                CommentAuthoredIdentity.Validate(authored);
                var declaredId = Nullify(CommentAuthoredIdentity.AutomationId(declarations[0].Element));
                if (declaredId is not null && !declaredId.StartsWith('{')) { automationId = declaredId; }
            }
            var (style, brushes) = CommentElementContext.Read(propsJson, Nullify(sourceRoot));
            // Without a declaration, the live AutomationId is the element's most stable searchable identity.
            if (authored is null && !selfGivenName)
            {
                automationId = ReadLiveAutomationId(tap, handle, cancellationToken);
            }
            var window = ReadWindowTitle(tap, tree, handle, cancellationToken);
            return new CaptureResult
            {
                Status = CaptureStatus.Ok,
                Element = new CapturedElement
                {
                    Handle = handle,
                    Type = ShortType(node.Type),
                    // Identity follows the declaration: a name the type gives itself is shared by every usage.
                    Name = selfGivenName ? null : Nullify(node.Name),
                    AutomationId = automationId,
                    Content = content,
                    SourceUri = uri,
                    SourceFile = file,
                    Line = line,
                    Column = column,
                    SourceProvenance = source?.CoordinateProvenance,
                    SourceEvidence = source?.SourceEvidence,
                    RawLine = source?.LineNumber,
                    RawColumn = source?.ColumnNumber,
                    Authored = authored,
                    ElementPath = Nullify(elementPath),
                    SourceRoot = Nullify(sourceRoot),
                    CommentRoot = Nullify(ReadCommentRoot(rootResponse)),
                    Style = style,
                    Brushes = brushes,
                    Window = window,
                },
            };
        }
        catch (DevToolsProtocolException ex)
        {
            return Failed(ex.Error);
        }
        catch (JsonException ex)
        {
            return Failed(new(-32700, "parse-error", ex.Message));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return Failed(new(-32000, "source-unavailable", ex.Message));
        }
    }

    private static CaptureResult Failed(DevToolsProtocolError error) => new() { Status = CaptureStatus.Failed, Error = error };

    internal static string ReadStringResult(DevToolsProtocolResponse response, string field)
    {
        using var doc = JsonDocument.Parse(response.RequireResult(), TapWireJson.DocumentOptions);
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new DevToolsProtocolException(new(-32603, "internal", $"The DevTools response has no '{field}' string."));
        }
        return value.GetString()!;
    }

    /// <summary>
    /// Where the app's comments live: its project folder, or, for an app launched without one, the folder it was
    /// launched from. Empty when the app knows neither.
    /// </summary>
    internal static string ReadCommentRoot(DevToolsProtocolResponse response)
    {
        var sourceRoot = ReadStringResult(response, "sourceRoot");
        if (sourceRoot.Length > 0)
        {
            return sourceRoot;
        }
        using var doc = JsonDocument.Parse(response.ResultJson!, TapWireJson.DocumentOptions);
        return doc.RootElement.TryGetProperty("commentRoot", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : "";
    }

    // Best effort: identity extras never block a comment.
    private static string? ReadLiveAutomationId(VisualTreeTap tap, string handle, CancellationToken cancellationToken)
    {
        var (_, facts, _) = DevToolsPreviews.Fetch(tap, [handle], cancellationToken);
        var id = Nullify(facts.GetValueOrDefault(handle)?.AutomationId);
        return id is null || id.StartsWith('{') ? null : id;
    }

    /// <summary>The title of the window (or popup) whose content holds the element, as the reviewer saw it.</summary>
    internal static string? ReadWindowTitle(VisualTreeTap tap, string? enumerateJson, string handle, CancellationToken cancellationToken)
    {
        using var surfaces = tap.Request("Surface.list", cancellationToken: cancellationToken).TryParseResult();
        if (surfaces?.RootElement is not { ValueKind: JsonValueKind.Object } root ||
            !root.TryGetProperty("surfaces", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var surface in list.EnumerateArray())
        {
            if (surface.ValueKind == JsonValueKind.Object &&
                surface.TryGetProperty("rootHandle", out var rootHandle) && rootHandle.ValueKind == JsonValueKind.String &&
                surface.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
                Nullify(name.GetString()) is { } title)
            {
                titles.TryAdd(rootHandle.GetString()!, title);
            }
        }
        foreach (var ancestor in AncestorsOf(enumerateJson, handle))
        {
            if (titles.TryGetValue(ancestor, out var title))
            {
                return title;
            }
        }
        return null;
    }

    /// <summary>The element's handle followed by its ancestors', nearest first; empty when it is not in the tree.</summary>
    internal static IReadOnlyList<string> AncestorsOf(string? enumerateJson, string handle)
    {
        var path = new List<string>();
        bool Walk(VisualTreeNode node)
        {
            path.Add(node.Handle);
            if (node.Handle == handle || node.Children.Any(Walk))
            {
                return true;
            }
            path.RemoveAt(path.Count - 1);
            return false;
        }
        foreach (var root in VisualTreeNode.ParseForest(enumerateJson) ?? [])
        {
            if (Walk(root))
            {
                path.Reverse();
                return path;
            }
        }
        return [];
    }

    internal static string? FindHandleByName(string? enumerateJson, string name)
    {
        var matches = Flatten(enumerateJson).Where(n => string.Equals(n.Name, name, StringComparison.Ordinal)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0].Handle : null;
    }

    internal static VisualTreeNode? FindNode(string? enumerateJson, string handle)
    {
        foreach (var n in Flatten(enumerateJson))
        {
            if (n.Handle == handle)
            {
                return n;
            }
        }

        return null;
    }

    private static IEnumerable<VisualTreeNode> Flatten(string? enumerateJson)
    {
        var roots = VisualTreeNode.ParseForest(enumerateJson);
        if (roots is null)
        {
            throw new JsonException("The agent returned a malformed visual tree.");
        }

        var stack = new Stack<VisualTreeNode>(roots);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;

            foreach (var c in n.Children)
            {
                stack.Push(c);
            }
        }
    }

    /// <summary>
    /// The best content snippet for fuzzy re-anchoring: the element's <c>Text</c>, else <c>Content</c>. Object-
    /// valued content (rendered <c>{TypeName}</c> by the tap) and empty values are rejected — they can't match
    /// a literal in source.
    /// </summary>
    internal static string? ExtractContent(string? propsJson)
    {
        if (string.IsNullOrWhiteSpace(propsJson))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(propsJson, TapWireJson.DocumentOptions);
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("props", out var props) || props.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("The property response has no property array.");
        }
        var reply = JsonSerializer.Deserialize(propsJson, TapWireJson.Context.TapPropsReply);
        if (reply?.Props is null || DevToolsPropertyRow.Parse(propsJson) is null)
        {
            throw new JsonException("The property response contains invalid property rows.");
        }

        return PickContent(reply, "Text") ?? PickContent(reply, "Content");
    }

    private static string? PickContent(TapPropsReply reply, string name)
    {
        foreach (var p in reply.Props)
        {
            if (!string.Equals(p.Name, name, StringComparison.Ordinal))
            {
                continue;
            }

            var v = p.Value?.Trim();
            if (string.IsNullOrEmpty(v))
            {
                return null;
            }

            // "{Grid}" / "{Binding}"-style placeholders are not literals present in source — skip them.
            if (v.Length >= 2 && v[0] == '{' && v[^1] == '}')
            {
                return null;
            }

            return v;
        }

        return null;
    }

    internal static (string? Uri, string? File, int? Line, int? Column) ParseSource(string? sourceJson)
    {
        if (string.IsNullOrWhiteSpace(sourceJson))
        {
            return (null, null, null, null);
        }

        using var doc = JsonDocument.Parse(sourceJson, TapWireJson.DocumentOptions);
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("fileName", out var fileName) || fileName.ValueKind != JsonValueKind.String)
        {
            throw new JsonException("The source response has no fileName string.");
        }
        var src = JsonSerializer.Deserialize(sourceJson, TapWireJson.Context.TapElementSource);
        if (src?.AuthoredState == "likely" &&
            (src.CoordinateProvenance != "likely-source-line" || string.IsNullOrWhiteSpace(src.SourceEvidence)) ||
            src?.CoordinateProvenance == "likely-source-line" && src.AuthoredState != "likely")
        {
            throw new JsonException("The likely source response has inconsistent attribution evidence.");
        }
        var uri = Nullify(src?.FileName);
        if (uri is null)
        {
            return (null, null, null, null);
        }

        var leaf = Comments.CommentAnchorResolver.RelativeSourcePath(uri, null);

        var line = src!.LineNumber > 0 ? src.LineNumber : (int?)null;
        var column = src.ColumnNumber > 0 ? src.ColumnNumber : (int?)null;
        if (src.AuthoredState == "available" && src.CoordinateProvenance == "disk-matched-unique-declaration" ||
            src.AuthoredState == "likely" && src.CoordinateProvenance == "likely-source-line")
        {
            if (src.AuthoredLineNumber <= 0 || src.AuthoredColumnNumber <= 0 || string.IsNullOrEmpty(src.AuthoredFileName))
            {
                throw new JsonException("The attributed source response has no authored declaration coordinates.");
            }
            string authored;
            try { authored = ExecutionTargets.Orchestration.GuestCommentBinding.ValidateRelativeSource(src.AuthoredFileName); }
            catch (InvalidOperationException ex) { throw new JsonException("The authored source path is invalid.", ex); }
            // A linked resource URI names the compiled resource, not the authored file. Do not let it
            // override the verified file when resolving this new note. Existing notes are never rewritten.
            return (string.Equals(leaf, authored, StringComparison.OrdinalIgnoreCase) ? uri : null,
                authored, src.AuthoredLineNumber, src.AuthoredColumnNumber);
        }
        return (uri, Nullify(leaf), line, column);
    }

    private static string? Nullify(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    internal static string? ShortType(string? type)
    {
        var t = Nullify(type);
        if (t is null)
        {
            return null;
        }

        var dot = t.LastIndexOf('.');
        return dot >= 0 && dot < t.Length - 1 ? t[(dot + 1)..] : t;
    }
}
