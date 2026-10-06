// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;
using Spectre.Console;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Commands;

/// <summary>
/// <c>winapp devtools resources copy-style &lt;selector&gt;</c>: "Edit a Copy" of the Style an element uses. Copies the
/// app Style, or WinUI's own (from the restored package's <c>generic.xaml</c>), into App.xaml under a new key and
/// points the element at it, so its setters and ControlTemplate can be edited in the project.
/// </summary>
internal class DevToolsResourcesCopyStyleCommand : DevToolsLiveCommand, IHelpExamples
{
    public override string ShortDescription => "Copy an element's Style into your XAML to edit it";

    public IReadOnlyList<string> Examples { get; } =
    [
        "winapp devtools resources copy-style <selector> -a <app>",
        "winapp devtools resources copy-style <selector> --key MyButtonStyle --write -a <app>",
        "winapp devtools resources copy-style <selector> --all-of-type --into Styles/Buttons.xaml --write -a <app>",
    ];

    public static Argument<string> SelectorArgument { get; } = new("selector")
    {
        Description = "Element whose Style to copy: the selector printed in brackets, an x:Name, an AutomationId, or a handle.",
    };

    public static Option<string?> KeyOption { get; } = new("--key")
    {
        Description = "x:Key for the copy. Defaults to <Type>Style1 (the first unused number).",
    };

    public static Option<bool> AllOfTypeOption { get; } = new("--all-of-type")
    {
        Description = "Make the copy an implicit Style (no x:Key) that applies to every element of its type in scope, " +
            "instead of setting Style on this element.",
    };

    public static Option<string?> IntoOption { get; } = new("--into")
    {
        Description = "XAML file to add the copy to, relative to the project folder. Defaults to App.xaml.",
    };

    public static Option<bool> WriteOption { get; } = new("--write")
    {
        Description = "Write the files. Without it, copy-style shows what it would change.",
    };

    public DevToolsResourcesCopyStyleCommand()
        : base("copy-style", "Copy the Style an element uses into your XAML so you can edit its setters and template, " +
            "like \"Edit a Copy\" in the Visual Studio designer. Copies the app's Style, or WinUI's own default style, " +
            "into App.xaml under a new key and sets Style on the element. Shows the changes unless --write is passed; " +
            "rebuild and restart the app to see the copy.")
    {
        Arguments.Add(SelectorArgument);
        Options.Add(KeyOption);
        Options.Add(AllOfTypeOption);
        Options.Add(IntoOption);
        Options.Add(WriteOption);
    }

    /// <summary>Where the copied Style came from.</summary>
    /// <param name="DefinedIn"><c>app</c> or <c>winui</c>.</param>
    /// <param name="Key">The copied Style's key, or null for an implicit app Style.</param>
    /// <param name="DefaultStyle">It is WinUI's default (implicit) style for the type.</param>
    internal sealed record CopySource(
        string DefinedIn, string? Key, bool DefaultStyle, string TargetType, string File, string? Path, int Line, string? Version);

    /// <summary>One file edit in the plan.</summary>
    /// <param name="Kind"><c>addStyle</c> or <c>setStyle</c>.</param>
    internal sealed record PlannedEdit(string Kind, string File, string Path, int Line, string Text, string? Previous, int Lines);

    public class Handler(
        IDevToolsTargetResolver resolver,
        IAnsiConsole ansiConsole) : LiveHandler(resolver, ansiConsole)
    {
        protected override async Task<int> RunAsync(
            DevToolsTarget target,
            ParseResult parseResult,
            bool json,
            CancellationToken cancellationToken)
        {
            var write = parseResult.GetValue(WriteOption);
            var allOfType = parseResult.GetValue(AllOfTypeOption);
            var requestedKey = parseResult.GetValue(KeyOption)?.Trim();
            var into = parseResult.GetValue(IntoOption)?.Trim();
            if (allOfType && requestedKey is not null)
            {
                return Fail(json, target.Pid, "--key and --all-of-type can't be combined: an implicit Style has no key.");
            }
            if (requestedKey is not null && !XamlStyleCopier.IsValidKey(requestedKey))
            {
                return Fail(json, target.Pid, $"'{requestedKey}' is not a usable key. Use letters, digits, '_', '.', or '-'.");
            }

            var handle = RequireHandle(target, parseResult.GetValue(SelectorArgument), json,
                "Run `winapp devtools inspect` to list them.", out var exitCode, cancellationToken, forMutation: write);
            if (handle is null)
            {
                return exitCode;
            }
            if (Describe(target, handle, cancellationToken) is not { } node)
            {
                return Fail(json, target.Pid, "The element is no longer in the visual tree. Run `winapp devtools inspect` again.");
            }

            var sourceRoot = DevToolsJson.SourceRoot(target.Tap!, cancellationToken);
            if (sourceRoot is null || !Directory.Exists(sourceRoot))
            {
                return Fail(json, target.Pid, "copy-style edits the app's XAML, so it needs the project folder. " +
                    "Start the app with `winapp run <project folder> --devtools`.");
            }
            var index = XamlResourceIndex.Build(sourceRoot);

            var response = target.Tap!.RequestProperties(handle, cancellationToken);
            if (!response.Ok)
            {
                return Fail(json, target.Pid, response.Error!);
            }
            using var doc = response.TryParseResult();
            if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("props", out var props) || props.ValueKind != JsonValueKind.Array)
            {
                return Fail(json, target.Pid, "The DevTools agent returned an unreadable property list.");
            }
            DevToolsPropertyRow? styleRow = null;
            ExplainLocation? appStyleAt = null;
            foreach (var row in props.EnumerateArray())
            {
                var parsed = DevToolsPropertyRow.FromElement(row);
                styleRow ??= parsed?.Name == "Style" ? parsed : null;
                appStyleAt ??= DevToolsResourceExplainer.AppStyleLocation(row);
            }

            var explicitKey = styleRow is { Value.Length: > 0, AuthoredKey: { } key } ? key : null;
            var element = ReadElementSource(target.Tap!, handle, cancellationToken);
            if (explicitKey is null && styleRow is { Value.Length: > 0, ValueSource: "Local" })
            {
                return Fail(json, target.Pid, element.File is null
                    ? "This element is part of a control's template, which sets its Style. Copy the Style of the control " +
                        "that owns the template instead (`winapp devtools inspect` shows its parents)."
                    : $"This element's Style is set by {styleRow.Authored ?? "code"}, not by a resource key, so there is no Style in XAML to copy.");
            }
            if (element.File is null && !allOfType)
            {
                return Fail(json, target.Pid, $"This {node.ShortType} is not declared in the app's XAML (it is part of a control's template, " +
                    "or created in code), so there is nowhere to set its Style. Copy the Style of the control that owns it, " +
                    $"or pass --all-of-type to restyle every {node.ShortType}.");
            }

            var notes = new List<string>();

            // The Style to copy: the app's (named by the element, or applied implicitly), else WinUI's own.
            XamlStyleCopier.FoundStyle? found = null;
            CopySource? source = null;
            var appStyle = explicitKey is not null
                ? index.StylesWithKey(explicitKey, element.File) is [var first, ..] ? first : null
                : appStyleAt is not null ? index.StyleAt(appStyleAt.File, appStyleAt.Line) : null;
            if (appStyle is not null)
            {
                var path = Path.Combine(sourceRoot, appStyle.File);
                if (XamlStyleCopier.ReadText(path, GuestSourceSnapshot.MaximumFileBytes) is { } text &&
                    XamlStyleCopier.Parse(text.Text, GuestSourceSnapshot.MaximumFileBytes) is { } parsed &&
                    XamlStyleCopier.StyleAt(parsed, appStyle.Line) is { } style)
                {
                    found = new XamlStyleCopier.FoundStyle(style, text.Text, null);
                    source = new CopySource("app", appStyle.Key, false,
                        XamlStyleCopier.ShortType(appStyle.TargetType) ?? node.ShortType, appStyle.File, path, appStyle.Line, null);
                }
            }
            else
            {
                if (XamlStyleCopier.FindGenericXaml(sourceRoot) is not { } generic)
                {
                    return Fail(json, target.Pid, "Could not find WinUI's generic.xaml for this project. " +
                        "Restore or build the project (it reads the WinUI package from obj/project.assets.json) and try again.");
                }
                if (XamlStyleCopier.ReadText(generic.Path, XamlStyleCopier.MaximumGenericBytes) is { } text &&
                    XamlStyleCopier.Parse(text.Text, XamlStyleCopier.MaximumGenericBytes) is { } parsed)
                {
                    found = XamlStyleCopier.FindWinUIStyle(parsed, text.Text, explicitKey, node.ShortType);
                }
                if (found is null)
                {
                    return Fail(json, target.Pid, explicitKey is not null
                        ? $"The Style '{explicitKey}' is not defined in the app or in WinUI's generic.xaml ({generic.Version})."
                        : $"{node.ShortType} has no default style in WinUI's generic.xaml ({generic.Version}) or in the app.");
                }
                source = new CopySource("winui", found.Key, explicitKey is null,
                    XamlStyleCopier.ShortType(found.TargetType) ?? node.ShortType, "generic.xaml", generic.Path, found.Line,
                    $"{generic.Package} {generic.Version}");
            }
            if (found is null || source is null)
            {
                return Fail(json, target.Pid, $"The Style at {appStyle!.File}:{appStyle.Line} could not be read. Save the file and try again.");
            }

            // Where the copy goes, and under which key.
            string intoFile;
            if (into is { Length: > 0 })
            {
                var full = Path.GetFullPath(Path.IsPathRooted(into) ? into : Path.Combine(sourceRoot, into));
                if (!File.Exists(full) || !full.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                {
                    return Fail(json, target.Pid, $"--into: {into} is not an existing .xaml file in the project.");
                }
                intoFile = XamlResourceIndex.NormalizeFile(Path.GetRelativePath(sourceRoot, full));
                if (intoFile.StartsWith("../", StringComparison.Ordinal))
                {
                    return Fail(json, target.Pid, $"--into: {into} is outside the project folder {sourceRoot}.");
                }
            }
            else if (index.ApplicationFile is { } app)
            {
                intoFile = app;
            }
            else
            {
                return Fail(json, target.Pid, "No App.xaml was found in the project. Pass --into <file> to choose where the copy goes.");
            }

            // An implicit Style applies only to elements of exactly its TargetType, so a copy of a Style written for a base
            // class (TargetType="Control") is retargeted to this element's type.
            string? retarget = null;
            if (allOfType && source.TargetType != node.ShortType)
            {
                if (!node.Type.StartsWith("Microsoft.UI.Xaml.", StringComparison.Ordinal))
                {
                    return Fail(json, target.Pid, $"The Style targets {source.TargetType}, and an implicit copy would have to target " +
                        $"{node.Type}. Copy it with a key instead of --all-of-type.");
                }
                retarget = node.ShortType;
                notes.Add($"The copy targets {retarget} instead of {source.TargetType}, because an implicit Style applies only to its exact TargetType.");
            }
            var copyType = retarget ?? source.TargetType;
            var newKey = allOfType ? null : requestedKey ?? XamlStyleCopier.UniqueKey(node.ShortType, k => index.Find(k).Count > 0);
            if (requestedKey is not null && index.Find(requestedKey) is [var taken, ..])
            {
                return Fail(json, target.Pid, $"The key {requestedKey} is already defined at {taken.File}:{taken.Line}. Pick another --key.");
            }
            if (allOfType && index.Styles.FirstOrDefault(s => s.Key is null &&
                string.Equals(s.File, intoFile, StringComparison.OrdinalIgnoreCase) &&
                XamlStyleCopier.ShortType(s.TargetType) == copyType) is { } existing)
            {
                return Fail(json, target.Pid, $"{intoFile}:{existing.Line} already has an implicit Style for {copyType}. " +
                    "Edit that one, or copy with a key instead of --all-of-type.");
            }

            var files = new Dictionary<string, (XamlStyleCopier.SourceText Text, System.Xml.Linq.XDocument Document, List<XamlStyleCopier.Splice> Splices)>(
                StringComparer.OrdinalIgnoreCase);
            (XamlStyleCopier.SourceText, System.Xml.Linq.XDocument, List<XamlStyleCopier.Splice>)? Load(string file)
            {
                if (files.TryGetValue(file, out var loaded))
                {
                    return loaded;
                }
                if (XamlStyleCopier.ReadText(Path.Combine(sourceRoot, file), GuestSourceSnapshot.MaximumFileBytes) is not { } text ||
                    XamlStyleCopier.Parse(text.Text, GuestSourceSnapshot.MaximumFileBytes) is not { Root: not null } parsed)
                {
                    return null;
                }
                return files[file] = (text, parsed, []);
            }

            if (Load(intoFile) is not { } intoSource)
            {
                return Fail(json, target.Pid, $"{intoFile} could not be read as XAML. Fix it and try again.");
            }
            var (intoText, intoDocument, intoSplices) = intoSource;
            var (insertion, insertError) = XamlStyleCopier.FindInsertion(intoText, intoDocument);
            if (insertion is null)
            {
                return Fail(json, target.Pid, $"{intoFile}: {insertError}");
            }
            var block = XamlStyleCopier.Render(found, newKey, XamlStyleCopier.RootNamespaces(intoDocument), insertion.Indent, retarget);
            var addSplice = XamlStyleCopier.Insert(insertion, block, intoText.Newline);
            intoSplices.Add(addSplice);

            // Point the element at the copy, when its declaration is confirmed in the current source.
            XamlStyleCopier.Splice? elementSplice = null;
            string? previous = null;
            string? elementNote = null;
            var reference = $"Style=\"{{StaticResource {newKey}}}\"";
            if (allOfType)
            {
                if (explicitKey is not null)
                {
                    notes.Add($"This element sets Style=\"{{StaticResource {explicitKey}}}\", so an implicit Style does not apply to it. " +
                        "Remove that attribute to use the copy.");
                }
            }
            else if (element.Confirmed && Load(element.File!) is { } elementSource)
            {
                var (elementText, elementDocument, elementSplices) = elementSource;
                var (edit, error) = XamlStyleCopier.SetStyleAttribute(elementText, elementDocument, element.Line, node.ShortType, newKey!);
                if (edit is null)
                {
                    elementNote = error;
                }
                else
                {
                    elementSplice = edit.Splice;
                    previous = edit.Previous;
                    elementSplices.Add(edit.Splice);
                }
            }
            else
            {
                elementNote = element.Reason;
            }
            if (elementNote is not null)
            {
                notes.Add($"Set {reference} on the element yourself: {elementNote}");
            }

            if (!index.IsAppScope(intoFile) && !string.Equals(intoFile, element.File, StringComparison.OrdinalIgnoreCase))
            {
                notes.Add($"{intoFile} is not App.xaml or a dictionary merged into it, so the element may not find the copy. " +
                    "Merge it into App.xaml's MergedDictionaries, or copy into App.xaml.");
            }
            if (source.DefinedIn == "winui" && CountResourceReferences(found.Style) is var used and > 0)
            {
                notes.Add($"The copy uses {used} WinUI resources (brushes, sizes); they still come from WinUI. If you only need other " +
                    "colors or sizes, override those keys instead (`winapp devtools resources explain`), which keeps future WinUI fixes.");
            }

            // Verify every edited file still parses before anything is written.
            var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (file, (text, _, splices)) in files.Where(f => f.Value.Splices.Count > 0))
            {
                var updated = XamlStyleCopier.Apply(text.Text, splices);
                if (XamlStyleCopier.Parse(updated, GuestSourceSnapshot.MaximumFileBytes) is null)
                {
                    return Fail(json, target.Pid, $"Copying would leave {file} unreadable as XML; nothing was written. Check the file for unusual formatting.");
                }
                results[file] = updated;
            }

            var edits = new List<PlannedEdit>
            {
                new("addStyle", intoFile, Path.Combine(sourceRoot, intoFile),
                    XamlStyleCopier.LineAfter(intoText.Text, intoSplices, addSplice.Offset) +
                        (insertion.Line - XamlStyleCopier.LineOf(intoText.Text, insertion.Offset)),
                    block, null, block.Count(c => c == '\n') + 1),
            };
            if (elementSplice is not null)
            {
                var (elementText, _, elementSplices) = files[element.File!];
                edits.Add(new("setStyle", element.File!, Path.Combine(sourceRoot, element.File!),
                    XamlStyleCopier.LineAfter(elementText.Text, elementSplices, elementSplice.Offset), reference, previous, 1));
            }

            if (write)
            {
                foreach (var (file, updated) in results)
                {
                    var original = files[file].Text;
                    await AtomicFile.WriteAllBytesAsync(Path.Combine(sourceRoot, file),
                        XamlStyleCopier.Encode(original with { Text = updated }), cancellationToken);
                }
            }

            if (json)
            {
                WriteJson(DevToolsJson.Serialize(writer =>
                {
                    writer.WriteBoolean("ok", true);
                    writer.WriteNumber("processId", target.Pid);
                    WriteIdentity(writer, target, handle, cancellationToken);
                    WriteWarning(writer);
                    writer.WriteBoolean("written", write);
                    writer.WriteStartObject("source");
                    writer.WriteString("definedIn", source.DefinedIn);
                    WriteOptional(writer, "key", source.Key);
                    writer.WriteBoolean("defaultStyle", source.DefaultStyle);
                    writer.WriteString("targetType", source.TargetType);
                    writer.WriteString("file", source.File);
                    WriteOptional(writer, "path", source.Path);
                    writer.WriteNumber("line", source.Line);
                    WriteOptional(writer, "package", source.Version);
                    writer.WriteEndObject();
                    if (newKey is null)
                    {
                        writer.WriteNull("key");
                    }
                    else
                    {
                        writer.WriteString("key", newKey);
                    }
                    writer.WriteBoolean("implicit", allOfType);
                    writer.WriteString("targetType", copyType);
                    writer.WriteStartArray("edits");
                    foreach (var edit in edits)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("kind", edit.Kind);
                        writer.WriteString("file", edit.File);
                        writer.WriteString("path", edit.Path);
                        writer.WriteNumber("line", edit.Line);
                        WriteOptional(writer, "previous", edit.Previous);
                        writer.WriteString("text", edit.Text);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    writer.WriteStartArray("notes");
                    foreach (var note in notes)
                    {
                        writer.WriteStringValue(note);
                    }
                    writer.WriteEndArray();
                }));
                return 0;
            }

            DevToolsRender.WriteMarkupLine(Console, DevToolsRender.Header(handle, node));
            foreach (var line in Render(source, newKey, edits, notes, write))
            {
                DevToolsRender.WriteMarkupLine(Console, line);
            }
            return 0;
        }
    }

    private const int PreviewLines = 8;

    internal static IEnumerable<string> Render(
        CopySource source, string? newKey, IReadOnlyList<PlannedEdit> edits, IReadOnlyList<string> notes, bool written)
    {
        yield return "Copies " + Markup.Escape(Describe(source));
        foreach (var edit in edits)
        {
            var at = Markup.Escape($"{edit.File}:{edit.Line}");
            if (edit.Kind == "addStyle")
            {
                var name = newKey ?? "implicit Style (no key)";
                yield return $"  [green]+[/] {at}  {Markup.Escape(name)} [grey]({edit.Lines} lines)[/]";
                var lines = edit.Text.Split('\n');
                foreach (var line in lines.Take(PreviewLines))
                {
                    yield return $"      [grey]{Markup.Escape(line)}[/]";
                }
                if (lines.Length > PreviewLines)
                {
                    yield return $"      [grey]… {lines.Length - PreviewLines} more lines[/]";
                }
            }
            else
            {
                var replaces = edit.Previous is { } p ? $" [grey](was Style=\"{Markup.Escape(p)}\")[/]" : string.Empty;
                yield return $"  [yellow]~[/] {at}  {Markup.Escape(edit.Text)} on the element{replaces}";
            }
        }
        yield return string.Empty;
        yield return written
            ? "Written. Rebuild and restart the app to use the copy; edit its Setters and ControlTemplate in " +
                Markup.Escape(edits[0].File) + "."
            : "Preview only; nothing was written. Run again with --write to apply.";
        foreach (var note in notes)
        {
            yield return $"[grey]{Markup.Escape(note)}[/]";
        }
    }

    internal static string Describe(CopySource source) => source switch
    {
        { DefinedIn: "app", Key: { } key } => $"{key} from {source.File}:{source.Line}",
        { DefinedIn: "app" } => $"the implicit Style for {source.TargetType} from {source.File}:{source.Line}",
        { DefaultStyle: true } => $"the WinUI default style for {source.TargetType}" +
            (source.Key is { } k ? $" ({k})" : string.Empty) + $", from {source.Version} generic.xaml:{source.Line}",
        _ => $"WinUI's {source.Key}, from {source.Version} generic.xaml:{source.Line}",
    };

    private static int CountResourceReferences(System.Xml.Linq.XElement style) => style.DescendantsAndSelf()
        .SelectMany(e => e.Attributes())
        .Select(a => XamlResourceIndex.ResourceReference(a.Value)?.Key)
        .Where(k => k is not null)
        .Distinct(StringComparer.Ordinal)
        .Count();

    private sealed record ElementSource(string? File, int Line, bool Confirmed, string Reason);

    // The element's declaration, usable for an edit only when the agent matched it to the file on disk and the file
    // is unchanged since the build; otherwise the line may point at something else.
    private static ElementSource ReadElementSource(VisualTreeTap tap, string handle, CancellationToken cancellationToken)
    {
        var response = tap.RequestSource(handle, cancellationToken);
        using var doc = response.Ok ? response.TryParseResult() : null;
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return new ElementSource(null, 0, false, "DevTools could not read where it is declared.");
        }
        var root = doc.RootElement;
        var file = String(root, "authoredFileName") is { Length: > 0 } f ? XamlResourceIndex.NormalizeFile(f) : null;
        var line = root.TryGetProperty("authoredLineNumber", out var l) && l.TryGetInt32(out var n) ? n : 0;
        var state = String(root, "authoredState");
        var provenance = String(root, "coordinateProvenance");
        if (file is null || line <= 0)
        {
            return new ElementSource(null, 0, false, "it has no XAML declaration in the app (it may come from a template or code).");
        }
        if (state == "stale")
        {
            return new ElementSource(file, line, false, $"{file} changed since the app was built. Rebuild, then run copy-style again.");
        }
        if (state != "available" || provenance != "disk-matched-unique-declaration")
        {
            return new ElementSource(file, line, false, $"its declaration in {file} is not confirmed for this build.");
        }
        return new ElementSource(file, line, true, string.Empty);
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }
}
