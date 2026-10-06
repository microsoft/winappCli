// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using System.Xml;
using System.Xml.Linq;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Services.DevTools;

/// <summary>
/// A static index of an app's XAML resources: where each <c>x:Key</c> is defined (file, line, and
/// <c>ThemeDictionaries</c> branch), and every <c>Style</c> with its setters. Files are keyed by their
/// project-relative path with <c>/</c> separators, the same form the agent reports as <c>authoredFileName</c>.
/// </summary>
/// <remarks>
/// A definition is <see cref="Definition.AppScope">app-scoped</see> when its file is <c>App.xaml</c>, a
/// <c>Themes/Generic.xaml</c>, or a dictionary those merge (transitively). Any other definition lives in a page's
/// or control's own resources and only applies to elements declared in that file.
/// </remarks>
internal sealed class XamlResourceIndex
{
    internal const int MaximumFiles = GuestSourceSnapshot.MaximumFiles;
    private const int MaximumDirectoryDepth = 16;
    private const int MaximumValueLength = 80;
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly string[] SkippedDirectories = ["bin", "obj", "AppPackages", "node_modules", "packages"];

    /// <param name="Theme">The <c>ThemeDictionaries</c> branch (Light, Dark, Default, HighContrast…), or null for a plain entry.</param>
    /// <param name="Value">A short literal summary (the element text, or a brush's Color), when there is one.</param>
    /// <param name="AliasOf">The key a <c>&lt;StaticResource x:Key="A" ResourceKey="B"/&gt;</c> entry forwards to.</param>
    internal sealed record Definition(
        string Key, string File, int Line, string? Theme, bool AppScope, string Type, string? Value, string? AliasOf);

    /// <param name="Value">The <c>Value</c> attribute, or <c>&lt;Type&gt;</c> for the <c>&lt;Setter.Value&gt;</c> element form.</param>
    internal sealed record Setter(string Property, string? Value, int Line);

    /// <param name="Key">The <c>x:Key</c>, or null for an implicit style (applied by <see cref="TargetType"/>).</param>
    /// <param name="BasedOn">The key named by <c>BasedOn="{StaticResource K}"</c>.</param>
    internal sealed record StyleEntry(
        string File, int Line, string? Key, string? TargetType, string? BasedOn, bool AppScope, IReadOnlyList<Setter> Setters);

    private readonly Dictionary<string, List<Definition>> definitions;
    private readonly Dictionary<string, List<StyleEntry>> stylesByFile;

    private XamlResourceIndex(
        IReadOnlyList<Definition> definitions, IReadOnlyList<StyleEntry> styles, IReadOnlyList<string> skipped, bool truncated)
    {
        Definitions = definitions;
        Styles = styles;
        SkippedFiles = skipped;
        Truncated = truncated;
        this.definitions = definitions.GroupBy(d => d.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        stylesByFile = styles.GroupBy(s => s.File, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<Definition> Definitions { get; }

    public IReadOnlyList<StyleEntry> Styles { get; }

    /// <summary>Files that could not be read or parsed. They contribute nothing, so their keys look undefined.</summary>
    public IReadOnlyList<string> SkippedFiles { get; }

    /// <summary>The project held more XAML files than <see cref="MaximumFiles"/>; the rest were not indexed.</summary>
    public bool Truncated { get; }

    /// <summary>Every definition of <paramref name="key"/> in the app (keys are case-sensitive, as in XAML).</summary>
    public IReadOnlyList<Definition> Find(string key) =>
        definitions.TryGetValue(key, out var found) ? found : [];

    /// <summary>The <c>Style</c> declared at <paramref name="line"/> of <paramref name="file"/>, if any.</summary>
    public StyleEntry? StyleAt(string file, int line) =>
        stylesByFile.TryGetValue(NormalizeFile(file), out var styles) ? styles.FirstOrDefault(s => s.Line == line) : null;

    /// <summary>The keyed styles named <paramref name="key"/>, nearest first: same file, then app scope.</summary>
    public IReadOnlyList<StyleEntry> StylesWithKey(string key, string? nearFile) => Styles
        .Where(s => s.Key == key)
        .OrderBy(s => nearFile is not null && string.Equals(s.File, NormalizeFile(nearFile), StringComparison.OrdinalIgnoreCase) ? 0 : s.AppScope ? 1 : 2)
        .ToList();

    /// <summary>Indexes every <c>*.xaml</c> under <paramref name="root"/>, skipping build output folders.</summary>
    public static XamlResourceIndex Build(string root)
    {
        var sources = new List<(string File, string? Text)>();
        var truncated = false;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MaxRecursionDepth = MaximumDirectoryDepth,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        };
        foreach (var path in Directory.EnumerateFiles(root, "*.xaml", options))
        {
            var relative = NormalizeFile(Path.GetRelativePath(root, path));
            if (relative.Split('/').SkipLast(1).Any(d => d.StartsWith('.') ||
                SkippedDirectories.Contains(d, StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }
            if (sources.Count == MaximumFiles)
            {
                truncated = true;
                break;
            }
            sources.Add((relative, Read(path)));
        }
        return FromSources(sources, truncated);
    }

    /// <summary>Indexes in-memory sources; <c>Text</c> null marks a file that could not be read.</summary>
    internal static XamlResourceIndex FromSources(IEnumerable<(string File, string? Text)> sources, bool truncated = false)
    {
        var documents = new Dictionary<string, XDocument>(StringComparer.OrdinalIgnoreCase);
        var skipped = new List<string>();
        foreach (var (file, text) in sources)
        {
            var normalized = NormalizeFile(file);
            if (text is not null && Parse(text) is { } document)
            {
                documents[normalized] = document;
            }
            else
            {
                skipped.Add(normalized);
            }
        }

        var appFiles = AppScopeFiles(documents);
        var found = new List<Definition>();
        var styles = new List<StyleEntry>();
        foreach (var (file, document) in documents)
        {
            var appScope = appFiles.Contains(file);
            foreach (var element in document.Descendants())
            {
                if (element.Name.LocalName == "Style")
                {
                    styles.Add(ReadStyle(file, element, appScope));
                }
                if ((string?)element.Attribute(Xaml + "Key") is { Length: > 0 } key && InDictionary(element))
                {
                    found.Add(new Definition(key, file, LineOf(element), ThemeOf(element), appScope,
                        element.Name.LocalName, Summarize(element), AliasOf(element)));
                }
            }
        }
        return new XamlResourceIndex(found, styles, skipped, truncated);
    }

    /// <summary>
    /// Reads a <c>{ThemeResource K}</c> or <c>{StaticResource K}</c> reference (also the <c>ResourceKey=K</c> form).
    /// Returns null for anything else: literals, bindings, and other markup extensions.
    /// </summary>
    internal static (string Kind, string Key)? ResourceReference(string? markup)
    {
        var text = markup?.Trim();
        if (text is null || text.Length < 3 || text[0] != '{' || text[^1] != '}' || text.StartsWith("{}", StringComparison.Ordinal))
        {
            return null;
        }
        var inner = text[1..^1].Trim();
        var space = inner.IndexOfAny([' ', '\t', '\r', '\n']);
        if (space < 0)
        {
            return null;
        }
        var kind = inner[..space] switch
        {
            "ThemeResource" => "themeResource",
            "StaticResource" => "staticResource",
            _ => null,
        };
        var key = inner[space..].Trim();
        if (key.StartsWith("ResourceKey", StringComparison.Ordinal) && key.IndexOf('=') is var equals and > 0 &&
            key[..equals].Trim() == "ResourceKey")
        {
            key = key[(equals + 1)..].Trim();
        }
        return kind is null || key.Length == 0 || key.Contains(',') || key.Contains('{') ? null : (kind, key);
    }

    /// <summary>Project-relative path with <c>/</c> separators and no leading <c>./</c>.</summary>
    internal static string NormalizeFile(string file)
    {
        var normalized = file.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }
        return normalized.TrimStart('/');
    }

    private static string? Read(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > GuestSourceSnapshot.MaximumFileBytes)
            {
                return null;
            }
            var bytes = File.ReadAllBytes(path);
            var offset = bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
            return new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return null;
        }
    }

    private static XDocument? Parse(string text)
    {
        try
        {
            using var input = new StringReader(text);
            using var reader = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = GuestSourceSnapshot.MaximumFileBytes,
            });
            return XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    // App.xaml and Themes/Generic.xaml, plus every dictionary they merge, transitively.
    private static HashSet<string> AppScopeFiles(Dictionary<string, XDocument> documents)
    {
        var scope = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(documents
            .Where(d => d.Value.Root?.Name.LocalName == "Application" ||
                string.Equals(d.Key, "Themes/Generic.xaml", StringComparison.OrdinalIgnoreCase))
            .Select(d => d.Key));
        while (pending.TryDequeue(out var file))
        {
            if (!scope.Add(file))
            {
                continue;
            }
            foreach (var merged in documents[file].Descendants()
                .Where(e => e.Parent?.Name.LocalName.EndsWith(".MergedDictionaries", StringComparison.Ordinal) == true))
            {
                var target = (string?)merged.Attribute("Source") is { Length: > 0 } source
                    ? ResolveSource(source, file)
                    : merged.Name.LocalName == "ResourceDictionary" ? null : ClassFile(documents, merged.Name.LocalName);
                if (target is not null && documents.ContainsKey(target))
                {
                    pending.Enqueue(target);
                }
            }
        }
        return scope;
    }

    // A code-behind dictionary merged as <local:ItemTemplates/>: the file whose root declares x:Class="…ItemTemplates".
    private static string? ClassFile(Dictionary<string, XDocument> documents, string typeName) => documents
        .Where(d => (string?)d.Value.Root?.Attribute(Xaml + "Class") is { } cls &&
            (cls == typeName || cls.EndsWith("." + typeName, StringComparison.Ordinal)))
        .Select(d => d.Key)
        .FirstOrDefault();

    private static string? ResolveSource(string source, string fromFile)
    {
        string path;
        if (source.StartsWith("ms-appx:///", StringComparison.OrdinalIgnoreCase))
        {
            path = source["ms-appx:///".Length..];
        }
        else if (source.StartsWith('/'))
        {
            path = source[1..];
        }
        else if (source.Contains(':'))
        {
            return null;
        }
        else
        {
            var directory = fromFile.LastIndexOf('/') is var slash and >= 0 ? fromFile[..(slash + 1)] : string.Empty;
            path = directory + source;
        }
        var parts = new List<string>();
        foreach (var part in path.Replace('\\', '/').Split('/'))
        {
            if (part is "" or ".")
            {
                continue;
            }
            if (part == "..")
            {
                if (parts.Count == 0)
                {
                    return null;
                }
                parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(part);
        }
        return string.Join('/', parts);
    }

    private static StyleEntry ReadStyle(string file, XElement style, bool appScope)
    {
        var setters = style.Elements()
            .Concat(style.Elements().Where(e => e.Name.LocalName == "Style.Setters").SelectMany(e => e.Elements()))
            .Where(e => e.Name.LocalName == "Setter" && (string?)e.Attribute("Property") is { Length: > 0 })
            .Select(e => new Setter(
                (string)e.Attribute("Property")!,
                (string?)e.Attribute("Value") ?? ElementValue(e),
                LineOf(e)))
            .ToList();
        return new StyleEntry(file, LineOf(style), (string?)style.Attribute(Xaml + "Key"),
            (string?)style.Attribute("TargetType"),
            ResourceReference((string?)style.Attribute("BasedOn"))?.Key,
            appScope, setters);
    }

    private static string? ElementValue(XElement setter) =>
        setter.Elements().FirstOrDefault(e => e.Name.LocalName == "Setter.Value")?.Elements().FirstOrDefault() is { } value
            ? $"<{value.Name.LocalName}>"
            : null;

    // x:Key is only meaningful inside a dictionary: a ResourceDictionary, or an Owner.Resources property element.
    // The keyed branches directly under ThemeDictionaries are containers, not resources.
    private static bool InDictionary(XElement element) =>
        element.Parent is { } parent &&
        !parent.Name.LocalName.EndsWith(".ThemeDictionaries", StringComparison.Ordinal) &&
        !parent.Name.LocalName.EndsWith(".MergedDictionaries", StringComparison.Ordinal) &&
        (parent.Name.LocalName == "ResourceDictionary" || parent.Name.LocalName.EndsWith(".Resources", StringComparison.Ordinal));

    private static string? ThemeOf(XElement element) => element.Ancestors()
        .FirstOrDefault(a => a.Parent?.Name.LocalName.EndsWith(".ThemeDictionaries", StringComparison.Ordinal) == true)
        ?.Attribute(Xaml + "Key")?.Value;

    private static string? AliasOf(XElement element) =>
        element.Name.LocalName == "StaticResource" && (string?)element.Attribute("ResourceKey") is { Length: > 0 } key ? key : null;

    private static string? Summarize(XElement element)
    {
        string? value = null;
        if (!element.HasElements && element.Value.Trim() is { Length: > 0 } text)
        {
            value = text;
        }
        else if ((string?)element.Attribute("Color") is { Length: > 0 } color)
        {
            value = color;
        }
        return value is null ? null : value.Length <= MaximumValueLength ? value : value[..(MaximumValueLength - 1)] + "…";
    }

    private static int LineOf(XElement element) => ((IXmlLineInfo)element).LineNumber;
}
