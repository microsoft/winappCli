// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.RegularExpressions;

namespace WinApp.Cli.Services.DevTools.Comments;

/// <summary>
/// Finds the arguments of C# calls that a password parameter receives, without a compiler: a positional argument is
/// matched to its parameter through the declaration of the called method, constructor or record
/// (<see cref="CSharpDeclarations"/>, by name, generic arity and argument count); a named argument
/// (<c>userPassword: "..."</c>) and an object initializer member (<c>Password = "..."</c>) are matched by their name.
/// A parameter whose name ends in "Password" is a secret, the rule XAML properties follow. When the declaration is not
/// found, or its overloads disagree, the argument is left alone.
/// </summary>
internal static partial class CSharpArguments
{
    // Keywords followed by a parenthesis that are not calls.
    private static readonly HashSet<string> NotCalls = new(StringComparer.Ordinal)
    {
        "if", "while", "for", "foreach", "switch", "catch", "using", "lock", "fixed", "typeof", "sizeof", "nameof", "default",
        "checked", "unchecked", "return", "when", "await", "throw", "is", "and", "or", "not", "in", "out", "ref", "var", "stackalloc",
    };

    /// <summary>The <c>[Start, End)</c> ranges of <paramref name="masked"/> C# whose string literals are secrets.</summary>
    internal static List<(int Start, int End)> SecretRanges(string masked, CSharpDeclarations declarations)
    {
        var ranges = new List<(int Start, int End)>();
        if (masked.Contains('"', StringComparison.Ordinal))
        {
            new Walker(masked, declarations, ranges).Walk(0, masked.Length, null);
        }
        return ranges;
    }

    private sealed record TypeRef(string Name, IReadOnlyList<string>? Arguments);

    private sealed record Argument(int Start, int ValueStart, int End, string? Name);

    private sealed class Walker(string text, CSharpDeclarations declarations, List<(int Start, int End)> ranges)
    {
        // Walks [start, end) for calls. `expected` is the type the range is passed as, for a target-typed `new(...)`.
        public void Walk(int start, int end, TypeRef? expected)
        {
            var first = SkipSpace(start, end);
            var i = start;
            while (i < end)
            {
                if (!IsIdentifierStart(text[i]) || (i > 0 && IsIdentifier(text[i - 1])))
                {
                    i++;
                    continue;
                }
                var wordEnd = WordEnd(i, end);
                var word = text[i..wordEnd];
                if (word == "new")
                {
                    i = New(wordEnd, end, i == first ? expected : null, i);
                    continue;
                }
                if (word == "with" && SkipSpace(wordEnd, end) is var brace && brace < end && text[brace] == '{')
                {
                    i = Initializer(brace, end);
                    continue;
                }
                var at = SkipSpace(wordEnd, end);
                IReadOnlyList<string>? typeArguments = null;
                if (at < end && text[at] == '<' && GenericEnd(at, end) is var close and > 0)
                {
                    typeArguments = SplitTopLevel(at + 1, close).Select(a => text[a.Start..a.End].Trim()).ToList();
                    at = SkipSpace(close + 1, end);
                }
                if (at < end && text[at] == '(' && !NotCalls.Contains(word))
                {
                    var before = SkipSpaceBack(i - 1);
                    i = Call(new TypeRef(word, typeArguments), member: before >= 0 && text[before] == '.', at, end, constructor: false);
                    continue;
                }
                i = wordEnd;
            }
        }

        // After `new`: `new T(...)`, `new(...)`, `new T { ... }`, `new { ... }`, or an array.
        private int New(int wordEnd, int end, TypeRef? expected, int newAt)
        {
            var at = SkipSpace(wordEnd, end);
            if (at >= end)
            {
                return end;
            }
            if (text[at] == '(')
            {
                var next = Call(expected ?? DeclaredType(newAt), member: false, at, end, constructor: true);
                return InitializerAfter(next, end);
            }
            if (text[at] == '{')
            {
                return Initializer(at, end);
            }
            if (!IsIdentifierStart(text[at]))
            {
                return at;
            }
            var typeEnd = at;
            while (typeEnd < end && (IsIdentifier(text[typeEnd]) || text[typeEnd] == '.')) { typeEnd++; }
            var name = text[at..typeEnd].Split('.').Last();
            var after = SkipSpace(typeEnd, end);
            IReadOnlyList<string>? typeArguments = null;
            if (after < end && text[after] == '<' && GenericEnd(after, end) is var close and > 0)
            {
                typeArguments = SplitTopLevel(after + 1, close).Select(a => text[a.Start..a.End].Trim()).ToList();
                after = SkipSpace(close + 1, end);
            }
            if (after < end && text[after] == '(')
            {
                return InitializerAfter(Call(new TypeRef(name, typeArguments), member: false, after, end, constructor: true), end);
            }
            return after < end && text[after] == '{' ? Initializer(after, end) : after;
        }

        private int InitializerAfter(int next, int end)
        {
            var brace = SkipSpace(next, end);
            return brace < end && text[brace] == '{' ? Initializer(brace, end) : next;
        }

        // `{ Name = value, ... }`: a member whose name is a password's has its value redacted.
        private int Initializer(int open, int end)
        {
            var close = Matching(open, end, '{', '}');
            foreach (var (start, stop) in SplitTopLevel(open + 1, close))
            {
                var value = start;
                if (MemberAssignment().Match(text, start, stop - start) is { Success: true } member)
                {
                    value = start + member.Length;
                    if (DevToolsSecrets.IsSecretName(member.Groups["name"].Value))
                    {
                        ranges.Add((value, stop));
                    }
                }
                Walk(value, stop, null);
            }
            return Math.Min(close + 1, end);
        }

        // A call's argument list at `open`: each argument a password parameter receives is a secret range. Returns
        // where the scan continues.
        private int Call(TypeRef? callee, bool member, int open, int end, bool constructor)
        {
            var close = Matching(open, end, '(', ')');
            var arguments = SplitTopLevel(open + 1, close).Select(Argument).Where(a => a is not null).Select(a => a!).ToList();
            if (arguments.Count == 1 && arguments[0].Name is null && string.IsNullOrWhiteSpace(text[arguments[0].Start..arguments[0].End]))
            {
                arguments.Clear();
            }
            var candidates = callee is null ? [] : Candidates(callee, member, constructor, arguments);
            var position = 0;
            foreach (var argument in arguments)
            {
                var (secret, type) = Resolve(candidates, callee, argument.Name, argument.Name is null ? position++ : -1);
                if (secret || (argument.Name is not null && DevToolsSecrets.IsSecretName(argument.Name)))
                {
                    ranges.Add((argument.ValueStart, argument.End));
                }
                Walk(argument.ValueStart, argument.End, type);
            }
            return Math.Min(close + 1, end);
        }

        private Argument? Argument((int Start, int End) span)
        {
            var match = NamedArgument().Match(text, span.Start, span.End - span.Start);
            return match.Success
                ? new Argument(span.Start, span.Start + match.Length, span.End, match.Groups["name"].Value)
                : new Argument(span.Start, span.Start, span.End, null);
        }

        // The declarations a call can be: by name and kind, type-argument count, and the arguments it passes.
        private List<(CSharpDeclaration Declaration, IReadOnlyList<CSharpParameter> Parameters)> Candidates(
            TypeRef callee, bool member, bool constructor, List<Argument> arguments)
        {
            var positional = arguments.Count(a => a.Name is null);
            var named = arguments.Where(a => a.Name is not null).Select(a => a.Name!).ToList();
            var result = new List<(CSharpDeclaration, IReadOnlyList<CSharpParameter>)>();
            foreach (var declaration in declarations.Find(callee.Name))
            {
                if (constructor ? declaration.Kind == 'M' : declaration.Kind != 'M')
                {
                    continue;
                }
                if (callee.Arguments is { } typeArguments && declaration.Kind != 'C' && declaration.TypeParameters.Count != typeArguments.Count)
                {
                    continue;
                }
                IReadOnlyList<CSharpParameter> parameters = member && declaration.Parameters is [{ IsThis: true }, ..]
                    ? [.. declaration.Parameters.Skip(1)]
                    : declaration.Parameters;
                var hasParams = parameters is [.., { IsParams: true }];
                var required = parameters.Count(p => !p.HasDefault && !p.IsParams);
                if (named.Any(name => parameters.All(p => p.Name != name)) ||
                    (!hasParams && positional + named.Count > parameters.Count) || positional + named.Count < required)
                {
                    continue;
                }
                result.Add((declaration, parameters));
            }
            return result;
        }

        // Whether every candidate's parameter for this argument is a password, and the type they agree it has.
        private static (bool Secret, TypeRef? Type) Resolve(
            List<(CSharpDeclaration Declaration, IReadOnlyList<CSharpParameter> Parameters)> candidates, TypeRef? callee, string? name, int position)
        {
            if (candidates.Count == 0)
            {
                return (false, null);
            }
            var secret = true;
            string? type = null;
            var typeAgreed = true;
            foreach (var (declaration, parameters) in candidates)
            {
                var parameter = name is not null ? parameters.FirstOrDefault(p => p.Name == name)
                    : position < parameters.Count && !parameters[position].IsParams ? parameters[position]
                    : parameters is [.., { IsParams: true } last] ? last : null;
                if (parameter is null)
                {
                    return (false, null);
                }
                secret &= DevToolsSecrets.IsSecretName(parameter.Name);
                var parameterType = Substitute(parameter.Type, declaration, callee);
                if (type is null) { type = parameterType; }
                else if (type != parameterType) { typeAgreed = false; }
            }
            return (secret, typeAgreed && type is not null ? Parse(type) : null);
        }

        // A parameter typed by a type parameter (`TProps props`) has the call's type argument (`Component<C, Props>`).
        private static string Substitute(string type, CSharpDeclaration declaration, TypeRef? callee)
        {
            var bare = type.TrimEnd('?');
            var index = declaration.TypeParameters.ToList().IndexOf(bare);
            return index >= 0 && callee?.Arguments is { } arguments && index < arguments.Count ? arguments[index] : type;
        }

        private static TypeRef? Parse(string type)
        {
            var match = TypeName().Match(type.Trim());
            if (!match.Success)
            {
                return null;
            }
            var arguments = match.Groups["args"].Success
                ? SplitTypes(match.Groups["args"].Value)
                : null;
            return new TypeRef(match.Groups["name"].Value.Split('.').Last(), arguments);
        }

        private static List<string> SplitTypes(string list)
        {
            var parts = new List<string>();
            var depth = 0;
            var start = 0;
            for (var i = 0; i < list.Length; i++)
            {
                if (list[i] is '<' or '(' or '[') { depth++; }
                else if (list[i] is '>' or ')' or ']') { depth--; }
                else if (list[i] == ',' && depth == 0) { parts.Add(list[start..i].Trim()); start = i + 1; }
            }
            parts.Add(list[start..].Trim());
            return parts;
        }

        // `Login2Props props = new(...)`: the declared type a target-typed `new` creates.
        private TypeRef? DeclaredType(int newAt)
        {
            var from = Math.Max(0, newAt - 256);
            var match = DeclaredTypePattern().Match(text[from..newAt]);
            return match.Success && match.Groups["type"].Value is not ("var" or "return" or "await" or "else") ? Parse(match.Groups["type"].Value) : null;
        }

        // The end of `<...>` at `open` when it is a type-argument list (followed by `(`), or -1 for a comparison.
        private int GenericEnd(int open, int end)
        {
            var depth = 0;
            for (var i = open; i < end; i++)
            {
                var c = text[i];
                if (c == '<') { depth++; }
                else if (c == '>')
                {
                    if (--depth == 0)
                    {
                        var next = SkipSpace(i + 1, end);
                        return next < end && text[next] is '(' or ')' or ',' or '{' or '[' or ';' or '.' ? i : -1;
                    }
                }
                else if (!(IsIdentifier(c) || char.IsWhiteSpace(c) || c is ',' or '.' or '?' or '[' or ']'))
                {
                    return -1;
                }
            }
            return -1;
        }

        // Splits [start, end) at commas outside brackets and type-argument lists.
        private List<(int Start, int End)> SplitTopLevel(int start, int end)
        {
            var parts = new List<(int, int)>();
            var depth = 0;
            var from = start;
            for (var i = start; i < end; i++)
            {
                var c = text[i];
                if (c == '<' && i > start && IsIdentifier(text[SkipSpaceBack(i - 1)]) && GenericEnd(i, end) is var close and > 0)
                {
                    i = close;
                    continue;
                }
                if (c is '(' or '[' or '{') { depth++; }
                else if (c is ')' or ']' or '}') { depth--; }
                else if (c == ',' && depth == 0)
                {
                    parts.Add((from, i));
                    from = i + 1;
                }
            }
            parts.Add((from, end));
            return parts;
        }

        // The bracket closing the one at `open`, or `end` when the text stops first (a statement cut short).
        private int Matching(int open, int end, char opening, char closing)
        {
            var depth = 0;
            for (var i = open; i < end; i++)
            {
                if (text[i] == opening) { depth++; }
                else if (text[i] == closing && --depth == 0) { return i; }
            }
            return end;
        }

        private int WordEnd(int start, int end)
        {
            while (start < end && IsIdentifier(text[start])) { start++; }
            return start;
        }

        private int SkipSpace(int from, int end)
        {
            while (from < end && char.IsWhiteSpace(text[from])) { from++; }
            return from;
        }

        private int SkipSpaceBack(int from)
        {
            while (from > 0 && char.IsWhiteSpace(text[from])) { from--; }
            return from;
        }
    }

    [GeneratedRegex(@"\G\s*@?(?<name>[A-Za-z_]\w*)\s*:(?!:)")]
    private static partial Regex NamedArgument();

    [GeneratedRegex(@"\G\s*@?(?<name>[A-Za-z_]\w*)\s*=(?![=>])")]
    private static partial Regex MemberAssignment();

    [GeneratedRegex(@"(?<type>[A-Za-z_][\w.]*(?:<[^;{}()=]*>)?\??)\s+@?[A-Za-z_]\w*\s*=\s*$")]
    private static partial Regex DeclaredTypePattern();

    [GeneratedRegex(@"^(?<name>[A-Za-z_][\w.]*)(?:\s*<(?<args>.*)>)?\??$", RegexOptions.Singleline)]
    private static partial Regex TypeName();

    private static bool IsIdentifier(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';
}
