// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace WinApp.Cli.Services.DevTools.Comments;

/// <summary>One parameter of a C# declaration: <c>string UserPassword</c>.</summary>
internal sealed record CSharpParameter(string Name, string Type, bool IsParams, bool HasDefault, bool IsThis);

/// <summary>
/// A C# method, constructor or record primary constructor, by name: <c>T</c> for a type's primary constructor
/// (<c>record Login(string User, string Password)</c>), <c>C</c> for a constructor, <c>M</c> for a method.
/// </summary>
internal sealed record CSharpDeclaration(string Name, char Kind, IReadOnlyList<string> TypeParameters, IReadOnlyList<CSharpParameter> Parameters);

/// <summary>
/// The parameter lists a project's C# declares, read as text (the CLI is Native AOT, so no compiler): the project folder
/// of a source file and the projects it references. Used to tell which parameter a positional argument is, so a string
/// passed to a password parameter can be redacted.
/// </summary>
internal sealed partial class CSharpDeclarations
{
    private const int MaxFiles = 4096;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(5);
    private static readonly ConcurrentDictionary<string, (DateTime Built, CSharpDeclarations Index)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] SkippedFolders = ["bin", "obj", ".git", ".vs", "node_modules", "packages", "AppPackages"];

    private static readonly HashSet<string> TypeKeywords = new(StringComparer.Ordinal) { "record", "class", "struct", "interface" };

    private static readonly HashSet<string> Modifiers = new(StringComparer.Ordinal)
    {
        "public", "private", "protected", "internal", "static", "extern", "unsafe", "file",
    };

    // Words a call can follow; `Foo(` after one is a call or a statement, not a declaration.
    private static readonly HashSet<string> NotTypes = new(StringComparer.Ordinal)
    {
        "return", "new", "await", "else", "case", "in", "is", "throw", "when", "and", "or", "not", "yield", "using", "lock", "if",
        "while", "for", "foreach", "switch", "catch", "fixed", "typeof", "sizeof", "nameof", "default", "checked", "unchecked",
        "out", "ref", "as", "from", "select", "where", "let", "join", "on", "equals", "by", "group", "into", "orderby", "operator",
        "this", "base", "goto", "do", "var", "with", "stackalloc", "params", "get", "set", "init", "add", "remove",
        "readonly", "const", "event", "implicit", "explicit", "abstract", "virtual", "override", "sealed", "async", "partial",
        "required", "volatile", "delegate",
    };

    private readonly Dictionary<string, List<CSharpDeclaration>> _byName = new(StringComparer.Ordinal);

    /// <summary>No declarations: every argument keeps the rule that applies without them.</summary>
    internal static CSharpDeclarations None { get; } = new();

    internal IReadOnlyList<CSharpDeclaration> Find(string name) => _byName.TryGetValue(name, out var list) ? list : [];

    /// <summary>Declarations read from C# source texts (tests, and one file when there is no project).</summary>
    internal static CSharpDeclarations FromText(params string[] sources)
    {
        var index = new CSharpDeclarations();
        foreach (var source in sources)
        {
            index.Add(CSharpSourceText.SplitLines(source));
        }
        return index;
    }

    /// <summary>
    /// The declarations of the project <paramref name="sourceFile"/> belongs to (the nearest folder above it with a
    /// <c>.csproj</c>) and of the projects that project references; the file alone when it is in no project. Files
    /// that cannot be read are skipped. Cached for a few seconds, so one command reads a project once.
    /// </summary>
    internal static CSharpDeclarations ForSource(string? sourceFile)
    {
        if (string.IsNullOrEmpty(sourceFile) || !Path.IsPathFullyQualified(sourceFile) || !File.Exists(sourceFile))
        {
            return None;
        }
        var project = ProjectFile(sourceFile);
        var key = project ?? Path.GetFullPath(sourceFile);
        if (Cache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.Built < CacheLifetime)
        {
            return cached.Index;
        }
        var index = new CSharpDeclarations();
        var files = new List<string>();
        if (project is null)
        {
            files.Add(sourceFile);
        }
        else
        {
            foreach (var folder in ProjectFolders(project))
            {
                files.AddRange(SourceFiles(folder, MaxFiles - files.Count));
            }
        }
        foreach (var file in files)
        {
            try
            {
                index.Add(CSharpSourceText.ReadLines(file, Path.GetDirectoryName(file)!));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // An unreadable file declares nothing DevTools can use.
            }
        }
        Cache[key] = (DateTime.UtcNow, index);
        return index;
    }

    private static string? ProjectFile(string sourceFile)
    {
        for (var folder = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!); folder is not null; folder = folder.Parent)
        {
            try
            {
                var projects = folder.GetFiles("*.csproj");
                if (projects.Length > 0)
                {
                    return projects.Length == 1 ? projects[0].FullName : null;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
        return null;
    }

    // The project's folder, then each referenced project's folder (one level; analyzers excluded).
    private static List<string> ProjectFolders(string project)
    {
        var folder = Path.GetDirectoryName(project)!;
        var folders = new List<string> { folder };
        try
        {
            var document = XDocument.Load(project);
            foreach (var reference in document.Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
            {
                if (reference.Attribute("Include")?.Value is not { Length: > 0 } include || include.Contains("$(", StringComparison.Ordinal) ||
                    string.Equals(reference.Attribute("OutputItemType")?.Value, "Analyzer", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(reference.Attribute("ReferenceOutputAssembly")?.Value, "false", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var path = Path.GetFullPath(Path.Combine(folder, include.Replace('/', Path.DirectorySeparatorChar)));
                if (File.Exists(path) && Path.GetDirectoryName(path) is { } referenced &&
                    !folders.Any(known => referenced.StartsWith(known + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(referenced, known, StringComparison.OrdinalIgnoreCase)))
                {
                    folders.Add(referenced);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or ArgumentException or NotSupportedException)
        {
            // A project file that cannot be read references nothing.
        }
        return folders;
    }

    private static List<string> SourceFiles(string folder, int limit)
    {
        if (limit <= 0)
        {
            return [];
        }
        try
        {
            return Directory.EnumerateFiles(folder, "*.cs",
                    new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true })
                .Where(path => !Path.GetRelativePath(folder, path).Split('\\', '/').SkipLast(1)
                    .Any(part => SkippedFolders.Contains(part, StringComparer.OrdinalIgnoreCase)))
                .Take(limit).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Adds the declarations of one file.</summary>
    internal void Add(IReadOnlyList<string> lines)
    {
        var text = string.Join('\n', CSharpSourceText.Mask(lines));
        for (var i = 0; i < text.Length; i++)
        {
            if (!IsIdentifierStart(text[i]) || (i > 0 && IsIdentifier(text[i - 1])))
            {
                continue;
            }
            var end = i;
            while (end < text.Length && IsIdentifier(text[end])) { end++; }
            if (Declaration(text, i, end) is { } declaration)
            {
                if (!_byName.TryGetValue(declaration.Name, out var list))
                {
                    _byName[declaration.Name] = list = [];
                }
                list.Add(declaration);
            }
            i = end - 1;
        }
    }

    // `Name(params)` or `Name<T>(params)` at [start, end) when it declares something.
    private static CSharpDeclaration? Declaration(string text, int start, int end)
    {
        var name = text[start..end];
        if (NotTypes.Contains(name) || TypeKeywords.Contains(name) || Modifiers.Contains(name))
        {
            return null;
        }
        var at = SkipSpace(text, end);
        IReadOnlyList<string> typeParameters = [];
        if (at < text.Length && text[at] == '<')
        {
            var close = text.IndexOf('>', at);
            if (close < 0)
            {
                return null;
            }
            var list = text[(at + 1)..close].Split(',').Select(p => p.Trim()).Select(p => p.StartsWith("in ", StringComparison.Ordinal) ||
                p.StartsWith("out ", StringComparison.Ordinal) ? p[(p.IndexOf(' ') + 1)..].Trim() : p).ToList();
            if (list.Any(p => p.Length == 0 || !p.All(IsIdentifier) || !IsIdentifierStart(p[0])))
            {
                return null;
            }
            typeParameters = list;
            at = SkipSpace(text, close + 1);
        }
        if (at >= text.Length || text[at] != '(' || Kind(text, start) is not { } kind)
        {
            return null;
        }
        var closeParen = MatchParen(text, at);
        if (closeParen < 0)
        {
            return null;
        }
        var after = SkipSpace(text, closeParen + 1);
        var follows = after >= text.Length || text[after] is '{' or ':' or ';' ||
            (text[after] == '=' && after + 1 < text.Length && text[after + 1] == '>') ||
            (string.CompareOrdinal(text, after, "where", 0, 5) == 0 && (after + 5 >= text.Length || !IsIdentifier(text[after + 5])));
        if (!follows || Parameters(text[(at + 1)..closeParen]) is not { } parameters)
        {
            return null;
        }
        return new CSharpDeclaration(name, kind, typeParameters, parameters);
    }

    // What the word before a name says about it: a type keyword, a modifier (a constructor) or a return type (a method).
    private static char? Kind(string text, int start)
    {
        var p = SkipSpaceBack(text, start - 1);
        if (p < 0 || text[p] is ';' or '{' or '}')
        {
            return 'C';
        }
        var c = text[p];
        if (c == ']')
        {
            return p > 0 && SkipSpaceBack(text, p - 1) is var q and >= 0 && text[q] is '[' or ',' ? 'M' : 'C';
        }
        if (c == '>')
        {
            return p > 0 && text[p - 1] == '=' ? null : 'M';
        }
        if (c == '?')
        {
            return p > 0 && (IsIdentifier(text[p - 1]) || text[p - 1] is '>' or ']') ? 'M' : null;
        }
        if (!IsIdentifier(c))
        {
            return null;
        }
        var wordStart = p;
        while (wordStart > 0 && IsIdentifier(text[wordStart - 1])) { wordStart--; }
        var word = text[wordStart..(p + 1)];
        if (wordStart > 0 && text[wordStart - 1] == '.')
        {
            // `System.String Name(`: a qualified return type.
            return 'M';
        }
        return TypeKeywords.Contains(word) ? 'T' : Modifiers.Contains(word) ? 'C' : NotTypes.Contains(word) && word != "partial" ? null : 'M';
    }

    // The parameters of a declaration, or null when the list is not one (`Foo(a, b)` is a call).
    internal static IReadOnlyList<CSharpParameter>? Parameters(string list)
    {
        if (string.IsNullOrWhiteSpace(list))
        {
            return [];
        }
        var parameters = new List<CSharpParameter>();
        foreach (var match in SplitTopLevel(list).Select(part => ParameterPattern().Match(part)))
        {
            if (!match.Success || NotTypes.Contains(match.Groups["type"].Value))
            {
                return null;
            }
            var modifiers = match.Groups["mods"].Value;
            parameters.Add(new CSharpParameter(match.Groups["name"].Value, match.Groups["type"].Value.Replace(" ", "", StringComparison.Ordinal),
                modifiers.Contains("params", StringComparison.Ordinal), match.Groups["default"].Success,
                modifiers.Contains("this", StringComparison.Ordinal)));
        }
        return parameters;
    }

    [GeneratedRegex(@"^\s*(?:\[[^\]]*\]\s*)*(?<mods>(?:(?:this|params|ref|out|in|scoped|readonly)\s+)*)(?<type>[A-Za-z_][\w.]*(?:\s*<[^()]*>)?\??(?:\s*\[[\s,]*\])*\??|\([^()]*\)\??)\s+@?(?<name>[A-Za-z_]\w*)\s*(?<default>=.*)?$", RegexOptions.Singleline)]
    private static partial Regex ParameterPattern();

    // Splits at commas outside (), [], {} and <> (a declaration has no comparisons).
    private static IEnumerable<string> SplitTopLevel(string list)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < list.Length; i++)
        {
            switch (list[i])
            {
                case '(' or '[' or '{' or '<': depth++; break;
                case ')' or ']' or '}' or '>': depth--; break;
                case ',' when depth == 0:
                    yield return list[start..i];
                    start = i + 1;
                    break;
            }
        }
        yield return list[start..];
    }

    private static int MatchParen(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '(') { depth++; }
            else if (text[i] == ')' && --depth == 0) { return i; }
            else if (text[i] is ';' or '{' or '}') { return -1; }
        }
        return -1;
    }

    private static int SkipSpace(string text, int from)
    {
        while (from < text.Length && char.IsWhiteSpace(text[from])) { from++; }
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
