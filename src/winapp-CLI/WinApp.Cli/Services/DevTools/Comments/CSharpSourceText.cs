// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services.DevTools.Comments;

/// <summary>A factory call on one C# line: <c>Button(</c> at column 13 (1-based, the identifier's first character).</summary>
internal sealed record CSharpCallSite(int Column, string Name);

/// <summary>
/// Reads C# text without a compiler (the CLI is Native AOT): masks comments and literals so a call inside a string is
/// not a call, finds the factory calls on a line (<c>Button("+1", ...)</c>), and finds the span of a component's
/// declaration so a moved call site is looked for where that component renders.
/// </summary>
internal static class CSharpSourceText
{
    private const string Redacted = "<redacted>";
    private const int MaximumLineTextLength = 512;

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "return", "await", "yield", "else", "case", "in", "is", "throw", "when", "and", "or", "not", "new",
    };

    internal static string[] ReadLines(string path, string root)
    {
        if (PathSafety.HasReparsePointOnPath(path, root))
        {
            throw new IOException("The C# comment source is redirected or unavailable.");
        }
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > GuestSourceSnapshot.MaximumFileBytes)
        {
            throw new InvalidDataException("The C# comment source exceeds the source snapshot limit.");
        }
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        string text;
        try
        {
            Encoding[] encodings =
            [
                new UTF32Encoding(false, true, true), new UTF32Encoding(true, true, true),
                new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true),
                new UTF8Encoding(true, true),
            ];
            text = null!;
            foreach (var encoding in encodings)
            {
                var preamble = encoding.GetPreamble();
                if (bytes.AsSpan().StartsWith(preamble))
                {
                    text = encoding.GetString(bytes, preamble.Length, bytes.Length - preamble.Length);
                    break;
                }
            }
            text ??= new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("The C# comment source has an invalid text encoding.", ex);
        }
        return SplitLines(text);
    }

    internal static string[] SplitLines(string text) =>
        text.Split('\n').Select(line => line.EndsWith('\r') ? line[..^1] : line).ToArray();

    /// <summary>
    /// The lines with comment text and literal contents replaced by spaces, so columns still line up. Quotes stay so a
    /// literal still separates tokens.
    /// </summary>
    internal static string[] Mask(IReadOnlyList<string> lines)
    {
        var masked = new string[lines.Count];
        var blockComment = false;
        var rawQuotes = 0;
        var verbatim = false;
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index].ToCharArray();
            var i = 0;
            var n = line.Length;
            while (i < n)
            {
                if (blockComment)
                {
                    var end = IndexOf(line, "*/", i);
                    var stop = end < 0 ? n : end;
                    Blank(line, i, stop);
                    if (end < 0) { i = n; continue; }
                    blockComment = false;
                    Blank(line, end, end + 2);
                    i = end + 2;
                    continue;
                }
                if (rawQuotes > 0)
                {
                    var end = IndexOf(line, new string('"', rawQuotes), i);
                    Blank(line, i, end < 0 ? n : end);
                    if (end < 0) { i = n; continue; }
                    i = end + rawQuotes;
                    rawQuotes = 0;
                    continue;
                }
                if (verbatim)
                {
                    while (i < n)
                    {
                        if (line[i] == '"')
                        {
                            if (i + 1 < n && line[i + 1] == '"') { line[i] = ' '; line[i + 1] = ' '; i += 2; continue; }
                            verbatim = false;
                            i++;
                            break;
                        }
                        line[i++] = ' ';
                    }
                    continue;
                }
                var c = line[i];
                if (c == '/' && i + 1 < n && line[i + 1] == '/')
                {
                    Blank(line, i, n);
                    break;
                }
                if (c == '/' && i + 1 < n && line[i + 1] == '*')
                {
                    blockComment = true;
                    Blank(line, i, i + 2);
                    i += 2;
                    continue;
                }
                if (c == '\'')
                {
                    var j = i + 1;
                    while (j < n && line[j] != '\'') { if (line[j] == '\\') { line[j++] = ' '; } if (j < n) { line[j++] = ' '; } }
                    i = j + 1;
                    continue;
                }
                if (c == '"' || ((c == '@' || c == '$') && i + 1 < n && (line[i + 1] is '"' or '@' or '$')))
                {
                    var j = i;
                    var isVerbatim = false;
                    while (j < n && line[j] is '@' or '$') { if (line[j] == '@') { isVerbatim = true; } j++; }
                    var quotes = 0;
                    while (j + quotes < n && line[j + quotes] == '"') { quotes++; }
                    if (quotes >= 3) { rawQuotes = quotes; i = j + quotes; continue; }
                    if (isVerbatim) { verbatim = true; i = j + 1; continue; }
                    i = j + 1;
                    while (i < n && line[i] != '"') { if (line[i] == '\\') { line[i++] = ' '; } if (i < n) { line[i++] = ' '; } }
                    i++;
                    continue;
                }
                i++;
            }
            masked[index] = new string(line);
        }
        return masked;
    }

    private static int IndexOf(char[] line, string value, int from)
    {
        for (var i = from; i + value.Length <= line.Length; i++)
        {
            if (string.Equals(new string(line, i, value.Length), value, StringComparison.Ordinal))
            {
                return i;
            }
        }
        return -1;
    }

    private static void Blank(char[] line, int from, int to)
    {
        for (var i = from; i < to && i < line.Length; i++)
        {
            line[i] = ' ';
        }
    }

    /// <summary>
    /// The factory calls on a masked line: an unqualified PascalCase name followed by <c>(</c> (optionally with type
    /// arguments), which is how Reactor elements and components are created. Modifiers (<c>.Width(</c>), qualified
    /// calls, constructors and declarations are not call sites.
    /// </summary>
    internal static IReadOnlyList<CSharpCallSite> CallSites(string maskedLine)
    {
        var sites = new List<CSharpCallSite>();
        var n = maskedLine.Length;
        for (var i = 0; i < n; i++)
        {
            if (!char.IsUpper(maskedLine[i]) || (i > 0 && IsIdentifier(maskedLine[i - 1])))
            {
                continue;
            }
            var end = i;
            while (end < n && IsIdentifier(maskedLine[end])) { end++; }
            var name = maskedLine[i..end];
            // Hooks (`UseState(0)`) are calls but create no element.
            if (name.Length > 3 && name.StartsWith("Use", StringComparison.Ordinal) && char.IsUpper(name[3]))
            {
                i = end - 1;
                continue;
            }
            var next = SkipSpace(maskedLine, end);
            if (next < n && maskedLine[next] == '<')
            {
                var depth = 0;
                var k = next;
                for (; k < n; k++)
                {
                    if (maskedLine[k] == '<') { depth++; }
                    else if (maskedLine[k] == '>' && --depth == 0) { break; }
                    else if (maskedLine[k] is '(' or ';' or '{' or '=') { k = n; break; }
                }
                next = k < n ? SkipSpace(maskedLine, k + 1) : n;
            }
            if (next >= n || maskedLine[next] != '(')
            {
                i = end - 1;
                continue;
            }
            var before = i - 1;
            while (before >= 0 && maskedLine[before] is ' ' or '\t') { before--; }
            var callable = before < 0 || maskedLine[before] is '(' or ',' or '=' or '[' or '{' or ':' or '?' or '>' or '!' or '&' or '|' or '+' or ';';
            if (before >= 0 && maskedLine[before] == '>' && (before == 0 || maskedLine[before - 1] != '='))
            {
                // `List<Element> Render(` declares a method; `=> Button(` calls one.
                callable = false;
            }
            if (before >= 0 && IsIdentifier(maskedLine[before]))
            {
                var start = before;
                while (start > 0 && IsIdentifier(maskedLine[start - 1])) { start--; }
                callable = Keywords.Contains(maskedLine[start..(before + 1)]) && maskedLine[start..(before + 1)] != "new";
            }
            if (callable)
            {
                sites.Add(new CSharpCallSite(i + 1, name));
            }
            i = end - 1;
        }
        return sites;
    }

    private static int SkipSpace(string line, int from)
    {
        while (from < line.Length && line[from] is ' ' or '\t') { from++; }
        return from;
    }

    private static bool IsIdentifier(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>The identifier that starts at <paramref name="column"/> (1-based), or null.</summary>
    internal static string? TokenAt(string line, int column)
    {
        if (column < 1 || column > line.Length || !IsIdentifier(line[column - 1]) ||
            (column > 1 && IsIdentifier(line[column - 2])))
        {
            return null;
        }
        var end = column - 1;
        while (end < line.Length && IsIdentifier(line[end])) { end++; }
        return line[(column - 1)..end];
    }

    /// <summary>Trimmed, with runs of whitespace collapsed, so re-indenting a line does not change it.</summary>
    internal static string Normalize(string line)
    {
        var builder = new StringBuilder(line.Length);
        var space = false;
        foreach (var c in line.Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }
            if (space && builder.Length > 0)
            {
                builder.Append(' ');
            }
            space = false;
            builder.Append(c);
        }
        return builder.ToString();
    }

    /// <summary>The text an anchor stores: normalized, without string values when the line mentions a password.</summary>
    internal static string StoredText(string line)
    {
        var text = Normalize(Redact(line));
        return text.Length > MaximumLineTextLength ? text[..MaximumLineTextLength] : text;
    }

    /// <summary>What a stored line is compared by. Hashing the redacted text keeps a password out of the hash too.</summary>
    internal static string LineHash(string line) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(Redact(line)))));

    internal static bool MentionsPassword(string code) => code.Contains("password", StringComparison.OrdinalIgnoreCase);

    /// <summary>Code that mentions a password shows no string literal values (<c>PasswordBox("&lt;redacted&gt;")</c>).</summary>
    internal static string Redact(string code) => MentionsPassword(code) ? RedactLiterals(code, _ => true) : code;

    /// <summary>
    /// Code as DevTools shows or stores it: <see cref="Redact(string)"/>, and the string literals a password parameter
    /// receives (<c>new Login("bob", "&lt;redacted&gt;")</c> for <c>record Login(string User, string Password)</c>)
    /// when <paramref name="declarations"/> declares the called method, constructor or record.
    /// </summary>
    internal static string Redact(string code, CSharpDeclarations declarations)
    {
        if (MentionsPassword(code))
        {
            return Redact(code);
        }
        var lines = SplitLines(code);
        var ranges = CSharpArguments.SecretRanges(string.Join('\n', Mask(lines)), declarations);
        return ranges.Count == 0 ? code : RedactLiterals(string.Join('\n', lines), at => Inside(ranges, at));
    }

    /// <summary>
    /// The lines of a file with the string literals a password parameter receives redacted, reading each call whole
    /// (its arguments may continue on later lines). Store and compare a line through <see cref="StoredText"/> and
    /// <see cref="LineHash"/> of these lines, so a comment's line is matched by the text it was stored with.
    /// </summary>
    internal static string[] RedactLines(IReadOnlyList<string> lines, CSharpDeclarations declarations)
    {
        var result = lines.ToArray();
        var ranges = CSharpArguments.SecretRanges(string.Join('\n', Mask(lines)), declarations);
        var offset = 0;
        for (var index = 0; index < result.Length && ranges.Count > 0; index++)
        {
            var start = offset;
            var end = offset + result[index].Length;
            if (ranges.Any(range => range.Start < end && range.End > start))
            {
                result[index] = RedactLiterals(result[index], at => Inside(ranges, start + at));
            }
            offset = end + 1;
        }
        return result;
    }

    private static bool Inside(List<(int Start, int End)> ranges, int at) => ranges.Any(range => at >= range.Start && at < range.End);

    // Replaces the value of each string literal whose opening quote `redact` selects with <redacted>.
    private static string RedactLiterals(string code, Func<int, bool> redact)
    {
        var output = new StringBuilder(code.Length);
        var i = 0;
        var n = code.Length;
        while (i < n)
        {
            var c = code[i];
            if (c == '/' && i + 1 < n && code[i + 1] == '/')
            {
                var lineEnd = code.IndexOf('\n', i);
                var stop = lineEnd < 0 ? n : lineEnd;
                output.Append(code, i, stop - i);
                i = stop;
                continue;
            }
            if (c == '"' || ((c == '@' || c == '$') && i + 1 < n && code[i + 1] is '"' or '@' or '$'))
            {
                var j = i;
                var verbatim = false;
                while (j < n && code[j] is '@' or '$') { if (code[j] == '@') { verbatim = true; } j++; }
                var quotes = 0;
                while (j + quotes < n && code[j + quotes] == '"') { quotes++; }
                if (quotes == 2 && !verbatim)
                {
                    output.Append(code, i, j + 2 - i);
                    i = j + 2;
                    continue;
                }
                var open = quotes >= 3 ? quotes : 1;
                var k = j + open;
                if (open >= 3)
                {
                    var end = code.IndexOf(new string('"', open), k, StringComparison.Ordinal);
                    k = end < 0 ? n : end;
                }
                else
                {
                    while (k < n)
                    {
                        if (code[k] == '"')
                        {
                            if (verbatim && k + 1 < n && code[k + 1] == '"') { k += 2; continue; }
                            break;
                        }
                        if (!verbatim && code[k] == '\n') { break; }
                        if (!verbatim && code[k] == '\\') { k++; }
                        k++;
                    }
                }
                if (!redact(i))
                {
                    var stop = k < n ? Math.Min(n, k + open) : n;
                    output.Append(code, i, stop - i);
                    i = stop;
                    continue;
                }
                output.Append(code, i, j + open - i);
                output.Append(Redacted);
                if (k < n && code[k] == '"')
                {
                    output.Append(code, k, Math.Min(open, n - k));
                    i = k + open;
                }
                else
                {
                    i = k;
                }
                continue;
            }
            output.Append(c);
            i++;
        }
        return output.ToString();
    }

    /// <summary>
    /// The 1-based line span of the type or method named <paramref name="name"/> (a component class, or a render
    /// function), from its declaration to its closing brace. Null when the file declares no such member once.
    /// </summary>
    internal static (int Start, int End)? DeclarationSpan(IReadOnlyList<string> masked, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }
        var simple = name.Split('.', '+').Last();
        var tick = simple.IndexOf('`');
        if (tick >= 0) { simple = simple[..tick]; }
        // A type wins over its own constructor; a render function is a method.
        var types = new List<(int Line, int Column)>();
        var methods = new List<(int Line, int Column)>();
        for (var index = 0; index < masked.Count; index++)
        {
            var line = masked[index];
            var at = FindWord(line, simple);
            if (at >= 0 && DeclarationKind(line, at, simple.Length) is { } kind)
            {
                (kind == 'T' ? types : methods).Add((index, at + simple.Length));
            }
        }
        var chosen = types.Count == 1 ? types[0] : types.Count == 0 && methods.Count == 1 ? methods[0] : ((int, int)?)null;
        return chosen is { } start ? (start.Item1 + 1, SpanEnd(masked, start.Item1, start.Item2)) : null;
    }

    /// <summary>The innermost class, record or struct whose body contains <paramref name="line"/> (1-based).</summary>
    internal static string? EnclosingType(IReadOnlyList<string> masked, int line)
    {
        string? best = null;
        var bestStart = 0;
        for (var index = 0; index < line && index < masked.Count; index++)
        {
            var text = masked[index];
            foreach (var keyword in new[] { "class", "record", "struct" })
            {
                var at = FindWord(text, keyword);
                if (at < 0)
                {
                    continue;
                }
                var start = SkipSpace(text, at + keyword.Length);
                var end = start;
                while (end < text.Length && IsIdentifier(text[end])) { end++; }
                if (end == start || !char.IsLetter(text[start]) && text[start] != '_')
                {
                    continue;
                }
                if (SpanEnd(masked, index, end) >= line && index + 1 >= bestStart)
                {
                    best = text[start..end];
                    bestStart = index + 1;
                }
            }
        }
        return best;
    }

    private static int FindWord(string line, string word)
    {
        for (var at = line.IndexOf(word, StringComparison.Ordinal); at >= 0; at = line.IndexOf(word, at + 1, StringComparison.Ordinal))
        {
            var end = at + word.Length;
            if ((at == 0 || !IsIdentifier(line[at - 1])) && (end >= line.Length || !IsIdentifier(line[end])))
            {
                return at;
            }
        }
        return -1;
    }

    // 'T' for `class Counter`, `record Counter(`, `struct Counter`; 'M' for a method `Element Counter(`.
    private static char? DeclarationKind(string line, int at, int length)
    {
        var before = line[..at].TrimEnd();
        if (before.EndsWith("class", StringComparison.Ordinal) || before.EndsWith("record", StringComparison.Ordinal) ||
            before.EndsWith("struct", StringComparison.Ordinal) || before.EndsWith("interface", StringComparison.Ordinal))
        {
            return 'T';
        }
        var after = SkipSpace(line, at + length);
        if (after >= line.Length || line[after] is not ('(' or '<') || before.Length == 0)
        {
            return null;
        }
        var last = before[^1];
        if (!IsIdentifier(last) && last is not ('>' or ']' or '?'))
        {
            return null;
        }
        var start = before.Length - 1;
        while (start > 0 && IsIdentifier(before[start - 1])) { start--; }
        return Keywords.Contains(before[start..]) ? null : 'M';
    }

    private static int SpanEnd(IReadOnlyList<string> masked, int startLine, int startColumn)
    {
        var depth = 0;
        var opened = false;
        for (var index = startLine; index < masked.Count; index++)
        {
            var line = masked[index];
            for (var i = index == startLine ? startColumn : 0; i < line.Length; i++)
            {
                if (line[i] == '{') { depth++; opened = true; }
                else if (line[i] == '}' && --depth <= 0 && opened) { return index + 1; }
                else if (line[i] == ';' && !opened && depth == 0) { return index + 1; }
            }
            // An expression-bodied member (`=> VStack(...)`) ends at its semicolon; parentheses count too.
            if (!opened && line.Contains("=>", StringComparison.Ordinal))
            {
                var end = ExpressionEnd(masked, index);
                if (end > 0) { return end; }
            }
        }
        return masked.Count;
    }

    private static int ExpressionEnd(IReadOnlyList<string> masked, int startLine)
    {
        var depth = 0;
        for (var index = startLine; index < masked.Count; index++)
        {
            foreach (var c in masked[index])
            {
                if (c is '(' or '[' or '{') { depth++; }
                else if (c is ')' or ']' or '}') { depth--; }
                else if (c == ';' && depth <= 0) { return index + 1; }
            }
        }
        return -1;
    }
}
