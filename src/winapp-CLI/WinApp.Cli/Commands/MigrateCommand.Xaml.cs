// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using System.Xml;
using System.Xml.Linq;
using WinApp.Cli.Models;

namespace WinApp.Cli.Commands;

internal partial class MigrateCommand
{
    public partial class Handler
    {
        private static int RewriteVirtualizingStackPanels(string targetRoot, MigrationReport report)
        {
            var changedFiles = 0;
            var residualLocations = new List<MigrationLocation>();
            var hasGlobalImplicitPanelStyle = EnumerateFiles(targetRoot)
                .Where(path => path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                .Any(ContainsImplicitVirtualizingStackPanelStyle);

            foreach (var file in EnumerateFiles(targetRoot).Where(path =>
                path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)))
            {
                var relativePath = NormalizePath(Path.GetRelativePath(targetRoot, file));
                EncodedTextFile sourceFile;
                try
                {
                    sourceFile = ReadTextFile(file);
                }
                catch (DecoderFallbackException)
                {
                    if (ContainsAsciiBytes(file, "VirtualizingStackPanel"))
                    {
                        residualLocations.Add(new MigrationLocation
                        {
                            Path = relativePath,
                            Line = 1
                        });
                    }
                    continue;
                }
                var original = sourceFile.Content;
                var tokens = ScanXamlElementTokens(original);
                if (!tokens.Any(token =>
                    string.Equals(token.LocalName, "VirtualizingStackPanel", StringComparison.Ordinal)))
                {
                    continue;
                }

                XDocument document;
                try
                {
                    document = XDocument.Parse(
                        original,
                        LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
                }
                catch (XmlException)
                {
                    residualLocations.AddRange(tokens
                        .Where(token => string.Equals(
                            token.LocalName,
                            "VirtualizingStackPanel",
                            StringComparison.Ordinal))
                        .Select(token => new MigrationLocation
                        {
                            Path = relativePath,
                            Line = GetLineNumber(original, token.NameStart)
                        }));
                    continue;
                }

                var elements = document.Root?.DescendantsAndSelf().ToList() ?? [];
                if (elements.Count != tokens.Count)
                {
                    residualLocations.AddRange(tokens
                        .Where(token => string.Equals(
                            token.LocalName,
                            "VirtualizingStackPanel",
                            StringComparison.Ordinal))
                        .Select(token => new MigrationLocation
                        {
                            Path = relativePath,
                            Line = GetLineNumber(original, token.NameStart)
                        }));
                    continue;
                }

                var replacements = new List<(int Start, int Length)>();
                for (var index = 0; index < elements.Count; index++)
                {
                    var element = elements[index];
                    if (!string.Equals(
                        element.Name.LocalName,
                        "VirtualizingStackPanel",
                        StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!CanRewriteVirtualizingStackPanel(element, hasGlobalImplicitPanelStyle))
                    {
                        residualLocations.Add(new MigrationLocation
                        {
                            Path = relativePath,
                            Line = ((IXmlLineInfo)element).LineNumber
                        });
                        continue;
                    }

                    var token = tokens[index];
                    replacements.Add((
                        token.NameStart + token.QualifiedName.Length - token.LocalName.Length,
                        token.LocalName.Length));
                    if (token.EndNameStart is int endNameStart)
                    {
                        replacements.Add((
                            endNameStart + token.EndQualifiedNameLength - token.LocalName.Length,
                            token.LocalName.Length));
                    }
                }

                if (replacements.Count == 0)
                {
                    continue;
                }

                var updated = new StringBuilder(original);
                foreach (var replacement in replacements.OrderByDescending(item => item.Start))
                {
                    updated.Remove(replacement.Start, replacement.Length);
                    updated.Insert(replacement.Start, "ItemsStackPanel");
                }
                WriteTextFile(file, updated.ToString(), sourceFile.Encoding);
                changedFiles++;
            }

            if (residualLocations.Count > 0)
            {
                report.Todos.Add(new MigrationTodo
                {
                    Id = "UWMIG010",
                    Category = "xaml-items-panel",
                    Priority = "required",
                    Summary = "Replace remaining VirtualizingStackPanel elements with a WinUI-compatible items panel",
                    Reason = "Only a sole, empty VirtualizingStackPanel child of ItemsPanelTemplate with attributes known to be supported by ItemsStackPanel is converted automatically. Review other contexts and translate VirtualizingStackPanel-specific properties or attached virtualization settings before replacing the element.",
                    Locations = residualLocations
                        .DistinctBy(location => (location.Path, location.Line))
                        .ToList()
                });
            }

            Console.Out.WriteLine(
                $"    Replaced safe VirtualizingStackPanel elements in {changedFiles} .xaml file(s); " +
                $"{residualLocations.Count} occurrence(s) require review");
            return changedFiles;
        }

        private static bool CanRewriteVirtualizingStackPanel(
            XElement element,
            bool hasImplicitPanelStyle)
        {
            var parent = element.Parent;
            if (hasImplicitPanelStyle
                || !IsKnownXamlControlsNamespace(element.Name.NamespaceName)
                || parent is null
                || !IsKnownXamlControlsNamespace(parent.Name.NamespaceName)
                || !string.Equals(parent.Name.LocalName, "ItemsPanelTemplate", StringComparison.Ordinal)
                || parent.Elements().Count() != 1
                || element.Elements().Any())
            {
                return false;
            }

            foreach (var attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration)
                {
                    continue;
                }

                if (attribute.Name.NamespaceName.Length == 0
                    && ItemsStackPanelCompatibleAttributes.Contains(attribute.Name.LocalName))
                {
                    continue;
                }

                if (attribute.Name.NamespaceName == XamlLanguageNamespace
                    && CompatibleXamlDirectives.Contains(attribute.Name.LocalName))
                {
                    continue;
                }

                if (attribute.Name.NamespaceName == XNamespace.Xml.NamespaceName
                    && attribute.Name.LocalName is "lang" or "space")
                {
                    continue;
                }

                return false;
            }

            return true;
        }

        private static bool HasImplicitVirtualizingStackPanelStyle(XDocument document) =>
            document
                .Descendants()
                .Where(element => string.Equals(
                    element.Name.LocalName,
                    "Style",
                    StringComparison.Ordinal))
                .Any(style =>
                    style.Attribute(XName.Get("Key", XamlLanguageNamespace)) is null
                    && style.Attribute("TargetType") is { Value: var targetType }
                    && IsVirtualizingStackPanelTypeName(targetType));

        private static bool ContainsImplicitVirtualizingStackPanelStyle(string path)
        {
            try
            {
                return HasImplicitVirtualizingStackPanelStyle(
                    XDocument.Parse(ReadTextFile(path).Content));
            }
            catch (Exception exception) when (
                exception is XmlException or DecoderFallbackException)
            {
                return false;
            }
        }

        private static bool ContainsAsciiBytes(string path, string value)
        {
            var bytes = File.ReadAllBytes(path);
            var valueBytes = Encoding.ASCII.GetBytes(value);
            return bytes.AsSpan().IndexOf(valueBytes) >= 0;
        }

        private static bool IsVirtualizingStackPanelTypeName(string value)
        {
            var typeName = value.Trim();
            if (typeName.StartsWith("{x:Type ", StringComparison.Ordinal)
                && typeName.EndsWith('}'))
            {
                typeName = typeName[8..^1].Trim();
            }

            return string.Equals(
                typeName[(typeName.LastIndexOf(':') + 1)..],
                "VirtualizingStackPanel",
                StringComparison.Ordinal);
        }

        private static bool IsKnownXamlControlsNamespace(string namespaceName) =>
            namespaceName is PresentationXamlNamespace or WinUiControlsNamespace;

        private static List<XamlElementToken> ScanXamlElementTokens(string text)
        {
            var tokens = new List<XamlElementToken>();
            var openElements = new Stack<XamlElementToken>();

            for (var index = 0; index < text.Length;)
            {
                if (text[index] != '<')
                {
                    index++;
                    continue;
                }

                if (text.AsSpan(index).StartsWith("<!--", StringComparison.Ordinal))
                {
                    var end = text.IndexOf("-->", index + 4, StringComparison.Ordinal);
                    index = end < 0 ? text.Length : end + 3;
                    continue;
                }
                if (text.AsSpan(index).StartsWith("<![CDATA[", StringComparison.Ordinal))
                {
                    var end = text.IndexOf("]]>", index + 9, StringComparison.Ordinal);
                    index = end < 0 ? text.Length : end + 3;
                    continue;
                }
                if (text.AsSpan(index).StartsWith("<?", StringComparison.Ordinal))
                {
                    var end = text.IndexOf("?>", index + 2, StringComparison.Ordinal);
                    index = end < 0 ? text.Length : end + 2;
                    continue;
                }
                if (text.AsSpan(index).StartsWith("<!", StringComparison.Ordinal))
                {
                    index = FindXamlTagEnd(text, index + 2) + 1;
                    continue;
                }

                var cursor = index + 1;
                var isEndElement = cursor < text.Length && text[cursor] == '/';
                if (isEndElement)
                {
                    cursor++;
                }
                while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
                {
                    cursor++;
                }

                var nameStart = cursor;
                while (cursor < text.Length
                    && !char.IsWhiteSpace(text[cursor])
                    && text[cursor] is not '/' and not '>')
                {
                    cursor++;
                }
                if (cursor == nameStart)
                {
                    index++;
                    continue;
                }

                var qualifiedName = text[nameStart..cursor];
                var localName = qualifiedName[(qualifiedName.LastIndexOf(':') + 1)..];
                var tagEnd = FindXamlTagEnd(text, cursor);
                if (isEndElement)
                {
                    if (openElements.Count > 0)
                    {
                        var openElement = openElements.Pop();
                        openElement.EndNameStart = nameStart;
                        openElement.EndQualifiedNameLength = qualifiedName.Length;
                    }
                    index = tagEnd + 1;
                    continue;
                }

                var token = new XamlElementToken
                {
                    NameStart = nameStart,
                    QualifiedName = qualifiedName,
                    LocalName = localName
                };
                tokens.Add(token);

                var beforeClose = tagEnd - 1;
                while (beforeClose >= cursor && char.IsWhiteSpace(text[beforeClose]))
                {
                    beforeClose--;
                }
                if (beforeClose < cursor || text[beforeClose] != '/')
                {
                    openElements.Push(token);
                }
                index = tagEnd + 1;
            }

            return tokens;
        }

        private static int FindXamlTagEnd(string text, int start)
        {
            var quote = '\0';
            for (var index = start; index < text.Length; index++)
            {
                var current = text[index];
                if (quote != '\0')
                {
                    if (current == quote)
                    {
                        quote = '\0';
                    }
                    continue;
                }

                if (current is '"' or '\'')
                {
                    quote = current;
                }
                else if (current == '>')
                {
                    return index;
                }
            }
            return text.Length - 1;
        }

        private static int GetLineNumber(string text, int position)
        {
            var line = 1;
            for (var index = 0; index < position; index++)
            {
                if (text[index] == '\n')
                {
                    line++;
                }
            }
            return line;
        }

        private static EncodedTextFile ReadTextFile(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var encoding = DetectEncoding(bytes, out var preambleLength);
            return new EncodedTextFile(
                encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength),
                encoding);
        }

        private static Encoding DetectEncoding(byte[] bytes, out int preambleLength)
        {
            Encoding[] encodings =
            [
                new UTF32Encoding(bigEndian: true, byteOrderMark: true),
                new UTF32Encoding(bigEndian: false, byteOrderMark: true),
                new UnicodeEncoding(bigEndian: true, byteOrderMark: true),
                new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)
            ];

            foreach (var encoding in encodings)
            {
                var preamble = encoding.GetPreamble();
                if (bytes.AsSpan().StartsWith(preamble))
                {
                    preambleLength = preamble.Length;
                    return encoding;
                }
            }

            var prefix = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 256));
            var declaredEncoding =
                XmlEncodingDeclaration().Match(prefix).Groups["name"].Value;
            if (declaredEncoding.Length > 0
                && !declaredEncoding.Equals("utf-8", StringComparison.OrdinalIgnoreCase)
                && !declaredEncoding.Equals("us-ascii", StringComparison.OrdinalIgnoreCase))
            {
                throw new DecoderFallbackException(
                    $"BOM-less XML declares unsupported encoding '{declaredEncoding}'.");
            }

            preambleLength = 0;
            return new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true);
        }

        private static void WriteTextFile(string path, string content, Encoding encoding)
        {
            var preamble = encoding.GetPreamble();
            var contentBytes = encoding.GetBytes(content);
            var output = new byte[preamble.Length + contentBytes.Length];
            preamble.CopyTo(output, 0);
            contentBytes.CopyTo(output, preamble.Length);
            File.WriteAllBytes(path, output);
        }

        private sealed record EncodedTextFile(string Content, Encoding Encoding);

        private sealed class XamlElementToken
        {
            public required int NameStart { get; init; }
            public required string QualifiedName { get; init; }
            public required string LocalName { get; init; }
            public int? EndNameStart { get; set; }
            public int EndQualifiedNameLength { get; set; }
        }
    }
}
