// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.RegularExpressions;
using WinApp.Cli.Commands;
using WinApp.Cli.Services.DevTools.Comments;

namespace WinApp.Cli.Services.DevTools;

/// <summary>
/// Names a Reactor component's hooks from its C# source when Reactor recorded none (DevTools attached after the app
/// started), so a hook assigned to a password variable is redacted like a named one. The Use* calls of the render body
/// are matched to the live hooks by count and kind, in order. Only straight-line render code is read: a hook in a
/// branch, a loop, a lambda or an argument, a custom hook, an early return, or any count or kind mismatch leaves every
/// name unknown. The same rule as the native engine's DevToolsCSharpSource::HookNames.
/// </summary>
internal static partial class ReactorHookSource
{
    /// <summary>The <c>nameSource</c> of a hook name read from the C# text rather than recorded by Reactor.</summary>
    internal const string NameSource = "csharp-source-text";

    private static readonly Dictionary<string, string> Kinds = new(StringComparer.Ordinal)
    {
        ["UseState"] = "state",
        ["UseReducer"] = "reducer",
        ["UseRef"] = "ref",
        ["UseMemo"] = "memo",
        ["UseCallback"] = "memo",
        ["UseEffect"] = "effect",
        ["UseContext"] = "context",
        ["UsePersisted"] = "persisted",
        ["UseNavigationLifecycle"] = "navigationLifecycle",
    };

    // What can leave the render body before a later hook runs.
    private static readonly HashSet<string> Exits = new(StringComparer.Ordinal) { "return", "throw", "yield", "goto" };

    // A block after `if (...)` and the like is a statement inside the body, not the body.
    private static readonly HashSet<string> BlockHeads = new(StringComparer.Ordinal)
    {
        "if", "for", "foreach", "while", "using", "lock", "switch", "catch", "fixed", "when",
    };

    private static readonly HashSet<string> BlockWords = new(StringComparer.Ordinal)
    {
        "else", "try", "finally", "do", "checked", "unchecked", "unsafe",
    };

    /// <summary>
    /// One name per hook (null for a hook assigned to nothing, such as <c>UseEffect(...)</c>), or null when the source
    /// does not say for certain. <paramref name="line"/> and <paramref name="column"/> (1-based; 0 when unknown) are
    /// where the render method's body starts, as the PDB records it; <paramref name="kinds"/> are the live hooks' kinds.
    /// </summary>
    internal static IReadOnlyList<string?>? Names(IReadOnlyList<string> lines, int line, int column, IReadOnlyList<string> kinds)
    {
        if (line < 1 || line > lines.Count || kinds.Count == 0)
        {
            return null;
        }
        var masked = CSharpSourceText.Mask(lines);
        var text = string.Join('\n', masked);
        var offset = 0;
        for (var i = 0; i < line - 1; i++)
        {
            offset += masked[i].Length + 1;
        }
        var row = masked[line - 1];
        var at = column > 0 ? Math.Min(column - 1, row.Length) : row.Length - row.TrimStart().Length;
        var open = BodyOpen(text, offset + at);
        var close = open < 0 ? -1 : Matching(text, open);
        if (close < 0)
        {
            return null;
        }
        var calls = Calls(text, open + 1, close);
        if (calls is null || calls.Count != kinds.Count)
        {
            return null;
        }
        for (var i = 0; i < calls.Count; i++)
        {
            if (!string.Equals(calls[i].Kind, kinds[i], StringComparison.Ordinal))
            {
                return null;
            }
        }
        return [.. calls.Select(call => call.Name)];
    }

    /// <summary>
    /// The component with the hook names its definition's C# gives, when Reactor recorded none, and the answer JSON with
    /// them: a hook named as a password is redacted and not settable. Null when nothing could be named.
    /// </summary>
    internal static (ReactorComponentState State, string Json)? Apply(ReactorComponentState state, string json, string? start = null)
    {
        if (state.Hooks.Count == 0 || state.Hooks.Any(h => h.Name is not null) || state.Definition is not { Line: > 0 } definition ||
            FindFile(definition.File, start) is not { } path)
        {
            return null;
        }
        IReadOnlyList<string?>? names;
        try
        {
            var lines = CSharpSourceText.ReadLines(path, Path.GetDirectoryName(path)!);
            names = Names(lines, definition.Line, definition.Column, [.. state.Hooks.Select(h => h.Kind)]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
        if (names is null || names.All(name => name is null))
        {
            return null;
        }

        var hooks = state.Hooks.Select((hook, i) => names[i] is not { } name ? hook : DevToolsSecrets.IsSecretName(name)
            ? hook with { Value = hook.Value is null ? null : DevToolsSecrets.Redacted, Redacted = true, Editable = false, Name = name, NameSource = NameSource }
            : hook with { Name = name, NameSource = NameSource }).ToList();
        var secretContexts = hooks.Where(h => h.Redacted && h.Kind == "context").Select(h => h.Index).ToHashSet();
        var contexts = state.Contexts.Select(c => secretContexts.Contains(c.Hook) ? c with { Value = DevToolsSecrets.Redacted, Redacted = true } : c).ToList();
        var named = state with { Hooks = hooks, Contexts = contexts };
        return (named, Rewrite(json, hooks, secretContexts));
    }

    // The definition's file on this machine: as recorded, or a project-relative path under the current folder.
    private static string? FindFile(string file, string? start)
    {
        if (Path.IsPathFullyQualified(file))
        {
            return file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && File.Exists(file) ? file : null;
        }
        return DevToolsGetSourceCommand.Handler.FindRelativeSource(file, start);
    }

    private static string Rewrite(string json, IReadOnlyList<ReactorHookInfo> hooks, HashSet<int> secretContexts)
    {
        using var doc = JsonDocument.Parse(json, TapWireJson.DocumentOptions);
        return DevToolsJson.Serialize(writer =>
        {
            foreach (var member in doc.RootElement.EnumerateObject())
            {
                if (member.Name == "hooks" && member.Value.ValueKind == JsonValueKind.Array)
                {
                    writer.WriteStartArray("hooks");
                    foreach (var element in member.Value.EnumerateArray())
                    {
                        var index = element.ValueKind == JsonValueKind.Object && element.TryGetProperty("index", out var i) && i.TryGetInt32(out var n) ? n : -1;
                        var hook = hooks.FirstOrDefault(h => h.Index == index);
                        if (hook?.NameSource is null)
                        {
                            element.WriteTo(writer);
                            continue;
                        }
                        writer.WriteStartObject();
                        foreach (var field in element.EnumerateObject())
                        {
                            if (field.Name is "name" or "nameSource" || (hook.Redacted && field.Name is "value" or "redacted" or "editable"))
                            {
                                continue;
                            }
                            field.WriteTo(writer);
                        }
                        writer.WriteString("name", hook.Name);
                        writer.WriteString("nameSource", NameSource);
                        if (hook.Redacted)
                        {
                            if (hook.Value is not null) { writer.WriteString("value", DevToolsSecrets.Redacted); }
                            writer.WriteBoolean("redacted", true);
                            if (element.TryGetProperty("editable", out _)) { writer.WriteBoolean("editable", false); }
                        }
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                else if (member.Name == "contexts" && member.Value.ValueKind == JsonValueKind.Array && secretContexts.Count > 0)
                {
                    writer.WriteStartArray("contexts");
                    foreach (var element in member.Value.EnumerateArray())
                    {
                        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("hook", out var h) || !h.TryGetInt32(out var hook) ||
                            !secretContexts.Contains(hook))
                        {
                            element.WriteTo(writer);
                            continue;
                        }
                        writer.WriteStartObject();
                        foreach (var field in element.EnumerateObject().Where(field => field.Name is not ("value" or "redacted")))
                        {
                            field.WriteTo(writer);
                        }
                        writer.WriteString("value", DevToolsSecrets.Redacted);
                        writer.WriteBoolean("redacted", true);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                else
                {
                    member.WriteTo(writer);
                }
            }
        });
    }

    // The `{` of the method or lambda body that contains `position`: the innermost block there, or the one around it
    // when that block belongs to a statement (`if (...) {`). -1 for a type, namespace or initializer block.
    private static int BodyOpen(string text, int position)
    {
        var open = position < text.Length && text[position] == '{' ? position : Enclosing(text, position - 1);
        while (open >= 0)
        {
            switch (Owner(text, open))
            {
                case 'B':
                    return open;
                case 'S':
                    open = Enclosing(text, open - 1);
                    break;
                default:
                    return -1;
            }
        }
        return -1;
    }

    private static int Enclosing(string text, int from)
    {
        var depth = 0;
        for (var i = Math.Min(from, text.Length - 1); i >= 0; i--)
        {
            if (text[i] == '}')
            {
                depth++;
            }
            else if (text[i] == '{')
            {
                if (depth == 0) { return i; }
                depth--;
            }
        }
        return -1;
    }

    // 'B' for a method, local function or lambda body; 'S' for a statement's block; '?' for anything else.
    private static char Owner(string text, int open)
    {
        var i = SkipSpaceBack(text, open - 1);
        if (i >= 1 && text[i] == '>' && text[i - 1] == '=')
        {
            return 'B';
        }
        if (i >= 0 && text[i] == ')')
        {
            var paren = MatchingBack(text, i);
            if (paren < 0) { return '?'; }
            var end = SkipSpaceBack(text, paren - 1);
            if (end >= 0 && text[end] == '>')
            {
                end = SkipSpaceBack(text, MatchingAngleBack(text, end) - 1);
            }
            var word = WordEndingAt(text, end);
            return word is null ? '?' : BlockHeads.Contains(word) ? 'S' : 'B';
        }
        if (i < 0 || text[i] is ';' or '{' or '}')
        {
            return 'S';
        }
        return WordEndingAt(text, i) is { } before && BlockWords.Contains(before) ? 'S' : '?';
    }

    private sealed record HookCall(string Kind, string? Name);

    // The hook calls of a render body in order, or null when they are not all straight-line statements.
    private static List<HookCall>? Calls(string text, int start, int end)
    {
        var calls = new List<HookCall>();
        var braces = new Stack<bool>(); // true for a lambda's block
        var parens = 0;
        var statement = start;
        var exited = false;
        for (var i = start; i < end; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '(' or '[':
                    parens++;
                    continue;
                case ')' or ']':
                    parens--;
                    continue;
                case '{':
                    var before = SkipSpaceBack(text, i - 1);
                    braces.Push(before >= 1 && text[before] == '>' && text[before - 1] == '=');
                    continue;
                case '}':
                    if (braces.Count > 0) { braces.Pop(); }
                    if (braces.Count == 0 && parens == 0) { statement = i + 1; }
                    continue;
                case ';':
                    if (braces.Count == 0 && parens == 0) { statement = i + 1; }
                    continue;
            }
            if (!IsIdentifierStart(c) || (i > start && IsIdentifier(text[i - 1])))
            {
                continue;
            }
            var wordEnd = i;
            while (wordEnd < end && IsIdentifier(text[wordEnd])) { wordEnd++; }
            var word = text[i..wordEnd];
            if (Exits.Contains(word) && parens == 0 && !braces.Contains(true))
            {
                exited = true;
            }
            if (word.Length > 3 && word.StartsWith("Use", StringComparison.Ordinal) && char.IsUpper(word[3]) &&
                IsCall(text, wordEnd, end))
            {
                if (exited || parens != 0 || braces.Count != 0 || !Kinds.TryGetValue(word, out var kind))
                {
                    return null;
                }
                var receiver = Receiver(text, i, statement);
                if (receiver < 0 || !TryName(text[statement..receiver], out var name))
                {
                    return null;
                }
                calls.Add(new HookCall(kind, name));
            }
            i = wordEnd - 1;
        }
        return calls;
    }

    // `UseState(` or `UseState<int>(`, not a method group or a declaration's name in a type.
    private static bool IsCall(string text, int wordEnd, int end)
    {
        var next = SkipSpace(text, wordEnd, end);
        if (next < end && text[next] == '<')
        {
            var depth = 0;
            for (; next < end; next++)
            {
                if (text[next] == '<') { depth++; }
                else if (text[next] == '>' && --depth == 0) { break; }
                else if (text[next] is ';' or '{' or '}' or '=') { return false; }
            }
            next = SkipSpace(text, next + 1, end);
        }
        return next < end && text[next] == '(';
    }

    // Where the hook's call starts, including a `ctx.` or `this.` receiver; -1 for any other qualifier or a declaration.
    private static int Receiver(string text, int call, int statement)
    {
        var before = SkipSpaceBack(text, call - 1);
        if (before < statement || text[before] != '.')
        {
            return before >= statement && IsIdentifier(text[before]) ? -1 : call;
        }
        var end = SkipSpaceBack(text, before - 1);
        var start = end;
        while (start > statement && IsIdentifier(text[start - 1])) { start--; }
        if (end < statement || start > end || !IsIdentifierStart(text[start]))
        {
            return -1;
        }
        var outer = SkipSpaceBack(text, start - 1);
        // `ctx.` and `this.`: a parameter, local or the component itself. A PascalCase qualifier is a type's method.
        return outer >= statement && (text[outer] == '.' || IsIdentifier(text[outer])) || char.IsUpper(text[start]) ? -1 : start;
    }

    // The text before a hook call says what it is assigned to: nothing, `var name =`, `Type name =`, `name =`, or a
    // deconstruction `var (name, setName) =`. Anything else (a condition, an operator) is not straight-line.
    private static bool TryName(string prefix, out string? name)
    {
        name = null;
        var text = prefix.Trim();
        if (text.Length == 0)
        {
            return true;
        }
        if (Assignment().Match(text) is { Success: true } assignment)
        {
            var type = assignment.Groups["type"].Value;
            if (Exits.Contains(type) || BlockHeads.Contains(type) || BlockWords.Contains(type))
            {
                return false;
            }
            name = assignment.Groups["name"].Value;
            return true;
        }
        if (Deconstruction().Match(text) is { Success: true } deconstruction)
        {
            var first = deconstruction.Groups["list"].Value.Split(',')[0].Trim();
            var last = first.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
            name = last is "_" or "" ? null : last.TrimStart('@');
            return last.Length == 0 || IsIdentifierStart(last.TrimStart('@')[0]);
        }
        return false;
    }

    [GeneratedRegex(@"^(?:(?<type>[A-Za-z_][\w.]*(?:<[^;{}()=]*>)?(?:\[\])*\??)\s+)?(?:this\s*\.\s*)?@?(?<name>[A-Za-z_]\w*)\s*=$")]
    private static partial Regex Assignment();

    [GeneratedRegex(@"^(?:var\s*)?\((?<list>[^()]*)\)\s*=$")]
    private static partial Regex Deconstruction();

    private static int Matching(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{') { depth++; }
            else if (text[i] == '}' && --depth == 0) { return i; }
        }
        return -1;
    }

    private static int MatchingBack(string text, int close)
    {
        var depth = 0;
        for (var i = close; i >= 0; i--)
        {
            if (text[i] == ')') { depth++; }
            else if (text[i] == '(' && --depth == 0) { return i; }
        }
        return -1;
    }

    private static int MatchingAngleBack(string text, int close)
    {
        var depth = 0;
        for (var i = close; i >= 0; i--)
        {
            if (text[i] == '>') { depth++; }
            else if (text[i] == '<' && --depth == 0) { return i; }
            else if (text[i] is ';' or '{' or '}') { return -1; }
        }
        return -1;
    }

    private static string? WordEndingAt(string text, int end)
    {
        if (end < 0 || !IsIdentifier(text[end]))
        {
            return null;
        }
        var start = end;
        while (start > 0 && IsIdentifier(text[start - 1])) { start--; }
        return text[start..(end + 1)];
    }

    private static int SkipSpace(string text, int from, int end)
    {
        while (from < end && char.IsWhiteSpace(text[from])) { from++; }
        return from;
    }

    private static int SkipSpaceBack(string text, int from)
    {
        while (from >= 0 && char.IsWhiteSpace(text[from])) { from--; }
        return from;
    }

    private static bool IsIdentifier(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';
}
