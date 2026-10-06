// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace WinApp.Cli.Services.DevTools;

/// <summary>
/// The pieces of <c>resources copy-style</c> ("Edit a Copy"): find a Style's XAML (in the app, or in WinUI's
/// <c>generic.xaml</c>), copy its text under a new key with the namespace prefixes it needs, and compute the text
/// edits that add it to a resource dictionary and point an element at it.
/// </summary>
/// <remarks>
/// Files are edited by splicing text at positions read from the XML parser, never by re-serializing the document,
/// so the rest of the user's file keeps its formatting, comments, line endings and encoding.
/// </remarks>
internal static class XamlStyleCopier
{
    internal const int MaximumGenericBytes = 32 * 1024 * 1024;
    private const int MaximumPackageSearchDepth = 6;
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>A text file as read from disk: its text, whether it had a UTF-8 BOM, and its newline style.</summary>
    internal sealed record SourceText(string Text, bool Bom)
    {
        public string Newline => Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    }

    /// <summary>Replace <see cref="Length"/> characters at <see cref="Offset"/> with <see cref="Text"/>.</summary>
    internal sealed record Splice(int Offset, int Length, string Text);

    /// <summary>Where a copied Style goes in a dictionary file.</summary>
    /// <param name="Line">The 1-based line the Style's start tag lands on.</param>
    /// <param name="Before">Text written before the Style (a new <c>Owner.Resources</c> element, or a line break).</param>
    /// <param name="After">Text written after it.</param>
    internal sealed record Insertion(int Offset, string Indent, string Before, string After, int Line, string Container);

    /// <summary>The edit that points an element at the copy, and the Style value it replaces, if any.</summary>
    internal sealed record StyleAttributeEdit(Splice Splice, string? Previous);

    /// <summary>A located Style: its element (with line info), and the text of the file it came from.</summary>
    internal sealed record FoundStyle(XElement Style, string Text, string? FollowedFrom)
    {
        public string? Key => (string?)Style.Attribute(Xaml + "Key");

        public string? TargetType => (string?)Style.Attribute("TargetType");

        public int Line => ((IXmlLineInfo)Style).LineNumber;
    }

    /// <summary>WinUI's <c>generic.xaml</c> for the version the project restored, and that version.</summary>
    internal sealed record GenericXaml(string Path, string Package, string Version);

    internal static SourceText? ReadText(string path, int maximumBytes)
    {
        try
        {
            if (new FileInfo(path).Length > maximumBytes)
            {
                return null;
            }
            var bytes = File.ReadAllBytes(path);
            var bom = bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF });
            var offset = bom ? 3 : 0;
            return new SourceText(new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset), bom);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return null;
        }
    }

    internal static byte[] Encode(SourceText source) =>
        source.Bom ? [.. Encoding.UTF8.GetPreamble(), .. new UTF8Encoding(false).GetBytes(source.Text)]
            : new UTF8Encoding(false).GetBytes(source.Text);

    internal static XDocument? Parse(string text, int maximumCharacters)
    {
        try
        {
            using var input = new StringReader(text);
            using var reader = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = maximumCharacters,
            });
            return XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// Finds the <c>generic.xaml</c> of the WinUI package the project restored, from <c>obj/**/project.assets.json</c>.
    /// Reading the restored version (not the newest one installed) keeps the copy identical to the style the app runs.
    /// </summary>
    internal static GenericXaml? FindGenericXaml(string sourceRoot)
    {
        var obj = Path.Combine(sourceRoot, "obj");
        if (!Directory.Exists(obj))
        {
            return null;
        }
        var assets = Directory.EnumerateFiles(obj, "project.assets.json", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = 3,
            IgnoreInaccessible = true,
        }).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        return assets is null ? null : FromAssets(assets);
    }

    internal static GenericXaml? FromAssets(string assetsPath)
    {
        try
        {
            using var stream = File.OpenRead(assetsPath);
            using var json = JsonDocument.Parse(stream);
            var root = json.RootElement;
            if (!root.TryGetProperty("libraries", out var libraries) || libraries.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("packageFolders", out var folders) || folders.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            // WinUI ships in its own package since Windows App SDK 1.8; before that it was inside the main one.
            foreach (var package in new[] { "Microsoft.WindowsAppSDK.WinUI", "Microsoft.WindowsAppSDK" })
            {
                var library = libraries.EnumerateObject()
                    .FirstOrDefault(l => l.Name.StartsWith(package + "/", StringComparison.OrdinalIgnoreCase));
                if (library.Value.ValueKind != JsonValueKind.Object ||
                    !library.Value.TryGetProperty("path", out var relative) || relative.GetString() is not { Length: > 0 } packagePath)
                {
                    continue;
                }
                foreach (var folder in folders.EnumerateObject())
                {
                    var directory = Path.GetFullPath(Path.Combine(folder.Name, packagePath));
                    if (Directory.Exists(directory) && FindInPackage(directory) is { } generic)
                    {
                        return new GenericXaml(generic, package, library.Name[(package.Length + 1)..]);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        return null;
    }

    // The native runtime's copy (lib/native/Microsoft.UI/Themes) when present, else any Themes/generic.xaml.
    private static string? FindInPackage(string directory)
    {
        var candidates = Directory.EnumerateFiles(directory, "generic.xaml", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = MaximumPackageSearchDepth,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
        }).Where(p => string.Equals(Path.GetFileName(Path.GetDirectoryName(p)), "Themes", StringComparison.OrdinalIgnoreCase)).ToList();
        return candidates
            .OrderBy(p => p.Contains($"{Path.DirectorySeparatorChar}Microsoft.UI{Path.DirectorySeparatorChar}Themes", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(p => p.Contains($"{Path.DirectorySeparatorChar}native{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .FirstOrDefault();
    }

    /// <summary>The Style declared at <paramref name="line"/> of a document, if any.</summary>
    internal static XElement? StyleAt(XDocument document, int line) =>
        document.Descendants().FirstOrDefault(e => e.Name.LocalName == "Style" && ((IXmlLineInfo)e).LineNumber == line);

    /// <summary>
    /// Finds a WinUI style: by key, or the implicit (default) style for <paramref name="shortType"/>. A default style
    /// that is only <c>&lt;Style TargetType="Button" BasedOn="{StaticResource DefaultButtonStyle}"/&gt;</c> is followed to
    /// the keyed style that holds the setters, since copying the empty wrapper would copy nothing.
    /// </summary>
    internal static FoundStyle? FindWinUIStyle(XDocument generic, string text, string? key, string shortType)
    {
        var styles = generic.Descendants().Where(e => e.Name.LocalName == "Style").ToList();
        XElement? style;
        if (key is not null)
        {
            style = styles.FirstOrDefault(s => (string?)s.Attribute(Xaml + "Key") == key);
            return style is null ? null : new FoundStyle(style, text, null);
        }
        style = styles.FirstOrDefault(s => s.Attribute(Xaml + "Key") is null && ShortType((string?)s.Attribute("TargetType")) == shortType);
        if (style is null)
        {
            return null;
        }
        if (!style.HasElements && XamlResourceIndex.ResourceReference((string?)style.Attribute("BasedOn"))?.Key is { } baseKey &&
            styles.FirstOrDefault(s => (string?)s.Attribute(Xaml + "Key") == baseKey) is { } based)
        {
            return new FoundStyle(based, text, baseKey);
        }
        return new FoundStyle(style, text, null);
    }

    internal static string? ShortType(string? type)
    {
        if (type is null)
        {
            return null;
        }
        var name = type.LastIndexOf('.') is var dot and >= 0 ? type[(dot + 1)..] : type;
        return name.IndexOf(':') is var colon and >= 0 ? name[(colon + 1)..] : name;
    }

    /// <summary><c>ButtonStyle1</c>, <c>ButtonStyle2</c>…: the first <c>&lt;Type&gt;Style&lt;n&gt;</c> not already taken.</summary>
    internal static string UniqueKey(string shortType, Func<string, bool> taken)
    {
        for (var n = 1; ; n++)
        {
            var key = $"{shortType}Style{n}";
            if (!taken(key))
            {
                return key;
            }
        }
    }

    /// <summary>A key that can stand in <c>x:Key="…"</c> and <c>{StaticResource …}</c> without escaping.</summary>
    internal static bool IsValidKey(string key) =>
        key.Length > 0 && key.All(c => char.IsLetterOrDigit(c) || c is '_' or '.' or '-');

    /// <summary>
    /// The copy's XAML, indented by <paramref name="indent"/>, with lines separated by <c>\n</c>. The start tag is
    /// rewritten (new key, or none for an implicit copy; namespace prefixes the copy uses that the destination does
    /// not declare); the content is the original text, re-indented.
    /// </summary>
    /// <param name="destinationNamespaces">Prefix → namespace declared on the destination file's root ("" is the default).</param>
    internal static string Render(
        FoundStyle source, string? newKey, IReadOnlyDictionary<string, string> destinationNamespaces, string indent, string? targetType = null)
    {
        var style = source.Style;
        var text = source.Text;
        var starts = LineStarts(text);
        var span = ElementSpan(text, starts, style) ??
            throw new InvalidOperationException("The Style's end tag could not be located.");
        var openStart = span.Start;
        var lineStart = starts[((IXmlLineInfo)style).LineNumber - 1];
        var baseIndent = text[lineStart..openStart];
        if (baseIndent.Any(c => !char.IsWhiteSpace(c)))
        {
            baseIndent = string.Empty;
        }

        var namespaces = NeededNamespaces(style);
        if (newKey is not null)
        {
            namespaces["x"] = Xaml.NamespaceName;
        }

        var builder = new StringBuilder();
        builder.Append(indent).Append('<').Append(QualifiedName(style, style.Name));
        if (newKey is not null)
        {
            builder.Append(" x:Key=\"").Append(newKey).Append('"');
        }
        foreach (var attribute in style.Attributes().Where(a => !a.IsNamespaceDeclaration && a.Name != Xaml + "Key"))
        {
            var value = targetType is not null && attribute.Name == "TargetType" ? targetType : attribute.Value;
            builder.Append(' ').Append(QualifiedName(style, attribute.Name)).Append("=\"").Append(Escape(value)).Append('"');
        }
        foreach (var (prefix, uri) in namespaces.OrderBy(n => n.Key, StringComparer.Ordinal))
        {
            if (!destinationNamespaces.TryGetValue(prefix, out var declared) || declared != uri)
            {
                builder.Append(prefix.Length == 0 ? " xmlns" : " xmlns:" + prefix).Append("=\"").Append(Escape(uri)).Append('"');
            }
        }

        if (span.SelfClosing)
        {
            return builder.Append(" />").ToString();
        }
        builder.Append('>');
        var body = text[(span.OpenEnd + 1)..span.CloseStart].Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = body.Split('\n');
        builder.Append(lines[0]);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            var strip = 0;
            while (strip < baseIndent.Length && strip < line.Length && char.IsWhiteSpace(line[strip]))
            {
                strip++;
            }
            var rest = line[strip..];
            builder.Append('\n');
            if (rest.Length > 0 || i == lines.Length - 1)
            {
                builder.Append(indent).Append(rest);
            }
        }
        return builder.Append("</").Append(QualifiedName(style, style.Name)).Append('>').ToString();
    }

    /// <summary>The namespace declarations on a document's root, prefix → namespace ("" for the default).</summary>
    internal static Dictionary<string, string> RootNamespaces(XDocument document) =>
        document.Root?.Attributes().Where(a => a.IsNamespaceDeclaration)
            .ToDictionary(a => a.Name.Namespace == XNamespace.None ? string.Empty : a.Name.LocalName, a => a.Value, StringComparer.Ordinal)
        ?? new Dictionary<string, string>(StringComparer.Ordinal);

    // Every in-scope prefix the copy uses: in element and attribute names, and in attribute values, where XAML
    // names types and attached properties as text (TargetType="primitives:X", Property="controls:Y.Z").
    private static Dictionary<string, string> NeededNamespaces(XElement style)
    {
        var inScope = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var declaration in style.AncestorsAndSelf().SelectMany(e => e.Attributes()).Where(a => a.IsNamespaceDeclaration))
        {
            inScope.TryAdd(declaration.Name.Namespace == XNamespace.None ? string.Empty : declaration.Name.LocalName, declaration.Value);
        }

        var needed = new Dictionary<string, string>(StringComparer.Ordinal);
        void Use(string prefix, string uri)
        {
            if (inScope.TryGetValue(prefix, out var declared) && declared == uri)
            {
                needed[prefix] = uri;
            }
        }
        foreach (var element in style.DescendantsAndSelf())
        {
            if (element.Name.Namespace != XNamespace.None)
            {
                Use(element.GetPrefixOfNamespace(element.Name.Namespace) ?? string.Empty, element.Name.NamespaceName);
            }
            // The Style's own x:Key is replaced, so it does not count as a use of the x prefix.
            foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration && !(element == style && a.Name == Xaml + "Key")))
            {
                if (attribute.Name.Namespace != XNamespace.None && element.GetPrefixOfNamespace(attribute.Name.Namespace) is { } prefix)
                {
                    Use(prefix, attribute.Name.NamespaceName);
                }
                foreach (var (candidate, uri) in inScope.Where(n => n.Key.Length > 0))
                {
                    if (UsesPrefix(attribute.Value, candidate))
                    {
                        Use(candidate, uri);
                    }
                }
            }
        }
        return needed;
    }

    // "primitives:" as a whole token: not the tail of a longer name.
    private static bool UsesPrefix(string value, string prefix)
    {
        var token = prefix + ":";
        for (var at = value.IndexOf(token, StringComparison.Ordinal); at >= 0; at = value.IndexOf(token, at + 1, StringComparison.Ordinal))
        {
            if (at == 0 || !(char.IsLetterOrDigit(value[at - 1]) || value[at - 1] is '_' or '.' or '-' or ':' or '/'))
            {
                return true;
            }
        }
        return false;
    }

    private static string QualifiedName(XElement scope, XName name)
    {
        if (name.Namespace == XNamespace.None)
        {
            return name.LocalName;
        }
        var prefix = scope.GetPrefixOfNamespace(name.Namespace);
        return prefix is null ? name.LocalName : $"{prefix}:{name.LocalName}";
    }

    private static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace("\"", "&quot;");

    /// <summary>
    /// Where a Style goes in <paramref name="document"/>: at the end of a root <c>ResourceDictionary</c>; else at the
    /// end of the root's <c>Owner.Resources</c> (or of the one <c>ResourceDictionary</c> it holds); else in a new
    /// <c>Owner.Resources</c> added as the root's first child. Returns an error message when none of those fit.
    /// </summary>
    internal static (Insertion? Insertion, string? Error) FindInsertion(SourceText source, XDocument document)
    {
        var root = document.Root!;
        var text = source.Text;
        var starts = LineStarts(text);
        var nl = source.Newline;
        XElement container;
        if (root.Name.LocalName == "ResourceDictionary")
        {
            container = root;
        }
        else if (root.Elements().FirstOrDefault(e => e.Name.LocalName == root.Name.LocalName + ".Resources") is { } resources)
        {
            var children = resources.Elements().ToList();
            if (children is [var only] && only.Attribute(Xaml + "Key") is null)
            {
                if (only.Name.LocalName != "ResourceDictionary")
                {
                    return (null, $"{resources.Name.LocalName} holds a single {only.Name.LocalName}. Wrap it in a <ResourceDictionary> " +
                        "(as its MergedDictionaries) so other resources can be added, or pass --into a ResourceDictionary file.");
                }
                container = only;
            }
            else
            {
                container = resources;
            }
        }
        else
        {
            var rootSpan = ElementSpan(text, starts, root);
            if (rootSpan is null || rootSpan.SelfClosing)
            {
                return (null, $"<{root.Name.LocalName}> has no content to add resources to.");
            }
            var childIndent = root.Elements().FirstOrDefault() is { } first ? IndentOf(text, starts, first) : null;
            childIndent ??= IndentOf(text, starts, root) + Unit(IndentOf(text, starts, root) ?? string.Empty);
            var owner = QualifiedName(root, root.Name);
            var before = $"\n{childIndent}<{owner}.Resources>\n";
            var after = $"\n{childIndent}</{owner}.Resources>";
            var line = LineAt(starts, rootSpan.OpenEnd) + 3;
            return (new Insertion(rootSpan.OpenEnd + 1, childIndent + Unit(childIndent), before, after, line, $"{root.Name.LocalName}.Resources"), null);
        }

        var span = ElementSpan(text, starts, container);
        if (span is null || span.SelfClosing)
        {
            return (null, $"<{container.Name.LocalName}> is empty-tagged; give it an end tag first.");
        }
        var containerIndent = IndentOf(text, starts, container) ?? string.Empty;
        var indent = container.Elements().LastOrDefault() is { } last && IndentOf(text, starts, last) is { } lastIndent
            ? lastIndent
            : containerIndent + Unit(containerIndent);
        var closeLine = LineAt(starts, span.CloseStart);
        var closeLineStart = starts[closeLine];
        var ownLine = text[closeLineStart..span.CloseStart].All(char.IsWhiteSpace);
        return ownLine
            ? (new Insertion(closeLineStart, indent, string.Empty, "\n", closeLine + 1, container.Name.LocalName), null)
            : (new Insertion(span.CloseStart, indent, "\n", "\n" + containerIndent, closeLine + 2, container.Name.LocalName), null);
    }

    /// <summary>The splice that adds <paramref name="block"/> (from <see cref="Render"/>) at an insertion point.</summary>
    internal static Splice Insert(Insertion insertion, string block, string newline) =>
        new(insertion.Offset, 0, (insertion.Before + block + insertion.After).Replace("\n", newline));

    /// <summary>
    /// Sets <c>Style="{StaticResource key}"</c> on the element whose start tag begins on <paramref name="line"/>:
    /// replacing an existing <c>Style</c> attribute, or appending one after the last attribute (on its own line when
    /// the attributes are one per line).
    /// </summary>
    internal static (StyleAttributeEdit? Edit, string? Error) SetStyleAttribute(
        SourceText source, XDocument document, int line, string localName, string key)
    {
        var text = source.Text;
        var starts = LineStarts(text);
        var candidates = document.Descendants()
            .Where(e => ((IXmlLineInfo)e).LineNumber == line && e.Name.LocalName == localName).ToList();
        if (candidates is not [var element])
        {
            return (null, candidates.Count == 0
                ? $"No <{localName}> starts on line {line}; the file may have changed since the app was built."
                : $"More than one <{localName}> starts on line {line}.");
        }
        if (element.Elements().Any(e => e.Name.LocalName == localName + ".Style"))
        {
            return (null, $"The element sets its Style with a <{localName}.Style> element; replace it by hand.");
        }

        var value = $"{{StaticResource {key}}}";
        if (element.Attribute("Style") is { } existing)
        {
            var (start, end) = AttributeSpan(text, starts, existing);
            return (new StyleAttributeEdit(new Splice(start, end - start, $"Style=\"{value}\""), existing.Value), null);
        }

        var span = ElementSpan(text, starts, element)!;
        var attributes = element.Attributes().Select(a => AttributeSpan(text, starts, a)).OrderBy(s => s.End).ToList();
        if (attributes.Count == 0)
        {
            var nameEnd = span.Start + 1 + QualifiedName(element, element.Name).Length;
            return (new StyleAttributeEdit(new Splice(nameEnd, 0, $" Style=\"{value}\""), null), null);
        }
        var lastStart = attributes[^1].Start;
        var lastLine = LineAt(starts, lastStart);
        var leading = text[starts[lastLine]..lastStart];
        var onePerLine = lastLine != LineAt(starts, span.Start) && leading.All(char.IsWhiteSpace);
        var insertion = onePerLine ? source.Newline + leading + $"Style=\"{value}\"" : $" Style=\"{value}\"";
        return (new StyleAttributeEdit(new Splice(attributes[^1].End, 0, insertion), null), null);
    }

    /// <summary>Applies non-overlapping splices to <paramref name="text"/>.</summary>
    internal static string Apply(string text, IEnumerable<Splice> splices)
    {
        var builder = new StringBuilder(text);
        foreach (var splice in splices.OrderByDescending(s => s.Offset).ThenByDescending(s => s.Length))
        {
            builder.Remove(splice.Offset, splice.Length).Insert(splice.Offset, splice.Text);
        }
        return builder.ToString();
    }

    /// <summary>The 1-based line that <paramref name="offset"/> falls on after <paramref name="splices"/> are applied.</summary>
    internal static int LineAfter(string text, IEnumerable<Splice> splices, int offset)
    {
        var shift = splices.Where(s => s.Offset < offset).Sum(s => CountLines(s.Text) - CountLines(text.Substring(s.Offset, s.Length)));
        return LineAt(LineStarts(text), offset) + 1 + shift;
    }

    internal static int LineOf(string text, int offset) => LineAt(LineStarts(text), offset) + 1;

    private static int CountLines(string text) => text.Count(c => c == '\n');

    private sealed record Span(int Start, int OpenEnd, int CloseStart, int End, bool SelfClosing);

    // The element's text span: '<' of its start tag, the start tag's '>', '<' of its end tag, and the end.
    // XElement keeps the start tag's position only, so the end tag comes from a reader pass to the same element.
    private static Span? ElementSpan(string text, int[] starts, XElement element)
    {
        var info = (IXmlLineInfo)element;
        var start = Offset(starts, info.LineNumber, info.LinePosition) - 1;
        var openEnd = TagEnd(text, start);
        if (openEnd < 0)
        {
            return null;
        }
        if (text[openEnd - 1] == '/')
        {
            return new Span(start, openEnd, openEnd, openEnd + 1, true);
        }

        using var input = new StringReader(text);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumGenericBytes,
        });
        var lineInfo = (IXmlLineInfo)reader;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || lineInfo.LineNumber != info.LineNumber || lineInfo.LinePosition != info.LinePosition)
            {
                continue;
            }
            var depth = reader.Depth;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                {
                    var closeStart = Offset(starts, lineInfo.LineNumber, lineInfo.LinePosition) - 2;
                    var closeEnd = TagEnd(text, closeStart);
                    return closeEnd < 0 ? null : new Span(start, openEnd, closeStart, closeEnd + 1, false);
                }
            }
            return null;
        }
        return null;
    }

    // The '>' that ends the tag starting at '<' at <paramref name="start"/>, skipping quoted attribute values.
    private static int TagEnd(string text, int start)
    {
        var quote = '\0';
        for (var i = start + 1; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '>')
            {
                return i;
            }
        }
        return -1;
    }

    // From the attribute's name to just past its closing quote.
    private static (int Start, int End) AttributeSpan(string text, int[] starts, XAttribute attribute)
    {
        var info = (IXmlLineInfo)attribute;
        var start = Offset(starts, info.LineNumber, info.LinePosition);
        var equals = text.IndexOf('=', start);
        var open = equals + 1;
        while (open < text.Length && text[open] is not ('"' or '\''))
        {
            open++;
        }
        var close = text.IndexOf(text[open], open + 1);
        return (start, close + 1);
    }

    // The whitespace before an element on its line, or null when other text precedes it there.
    private static string? IndentOf(string text, int[] starts, XElement element)
    {
        var info = (IXmlLineInfo)element;
        var lineStart = starts[info.LineNumber - 1];
        var prefix = text[lineStart..(Offset(starts, info.LineNumber, info.LinePosition) - 1)];
        return prefix.All(c => c is ' ' or '\t') ? prefix : null;
    }

    private static string Unit(string indent) => indent.Contains('\t') ? "\t" : "    ";

    // Offsets where each line starts. CRLF, CR and LF each end a line, as the XML parser counts them.
    private static int[] LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
                starts.Add(i + 1);
            }
            else if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }
        return [.. starts];
    }

    private static int Offset(int[] starts, int line, int position) => starts[line - 1] + position - 1;

    // 0-based line index containing offset.
    private static int LineAt(int[] starts, int offset)
    {
        var index = Array.BinarySearch(starts, offset);
        return index >= 0 ? index : ~index - 1;
    }
}
