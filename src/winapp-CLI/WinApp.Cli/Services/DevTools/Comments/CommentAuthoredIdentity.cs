// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services.DevTools.Comments;

internal static class CommentAuthoredIdentity
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private const string Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private const int MaximumDeclarationLength = 64 * 1024;

    internal static IReadOnlyList<XamlCoordinateMap.SourceDeclaration> Read(string path, string sourceRoot)
    {
        if (PathSafety.HasReparsePointOnPath(path, sourceRoot))
        {
            throw new IOException("The authored comment source is redirected or unavailable.");
        }
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > GuestSourceSnapshot.MaximumFileBytes)
        {
            throw new InvalidDataException("The authored comment source exceeds the source snapshot limit.");
        }
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        try
        {
            // Check UTF-32 before UTF-16, whose little-endian preamble is a prefix.
            Encoding[] encodings =
            [
                new UTF32Encoding(false, true, true), new UTF32Encoding(true, true, true),
                new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true),
                new UTF8Encoding(true, true),
            ];
            foreach (var encoding in encodings)
            {
                var preamble = encoding.GetPreamble();
                if (bytes.AsSpan().StartsWith(preamble))
                {
                    return XamlCoordinateMap.SourceDeclarations(
                        encoding.GetString(bytes, preamble.Length, bytes.Length - preamble.Length));
                }
            }
            return XamlCoordinateMap.SourceDeclarations(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("The authored comment source has an invalid text encoding.", ex);
        }
    }

    internal static CommentAuthoredAnchor Capture(string projectRoot, string sourceFile,
        XamlCoordinateMap.SourceDeclaration declaration, string capturedMarkup, bool uniqueInstance)
    {
        if (capturedMarkup.Length > MaximumDeclarationLength || declaration.Text.Length > MaximumDeclarationLength)
        {
            throw new InvalidDataException("The authored comment declaration exceeds 64 KiB.");
        }
        if (Signature(ParseOpeningTag(capturedMarkup, declaration.Element)) != Signature(declaration.Element))
        {
            throw new InvalidDataException("The authored declaration changed while the comment was being captured. Capture it again.");
        }
        var captured = new CommentAuthoredAnchor
        {
            ProjectRoot = Path.GetFullPath(projectRoot),
            SourceFile = sourceFile,
            Declaration = DevToolsSecrets.RedactXaml(declaration.Text),
            Signature = Signature(declaration.Element),
            Type = TypeIdentity(declaration.Element),
            Scope = Scope(declaration.Element),
            Structure = Structure(declaration.Element),
            Templated = declaration.Element.AncestorsAndSelf().Any(element =>
                element.Name.NamespaceName == Presentation &&
                element.Name.LocalName is "DataTemplate" or "ControlTemplate"),
            UniqueInstance = uniqueInstance,
        };
        Validate(captured);
        return captured;
    }

    internal static void Validate(CommentAuthoredAnchor anchor)
    {
        if (string.IsNullOrEmpty(anchor.Declaration) || anchor.Declaration.Length > MaximumDeclarationLength ||
            string.IsNullOrEmpty(anchor.Type) || anchor.Type.Length > MaximumDeclarationLength ||
            anchor.Scope is null || anchor.Scope.Length > MaximumDeclarationLength ||
            anchor.Structure is null || anchor.Structure.Length > MaximumDeclarationLength ||
            anchor.UniquenessReason?.Length > 128 ||
            anchor.Signature is not { Length: 64 } || !anchor.Signature.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("The authored comment identity is incomplete or exceeds the 64 KiB field limit.");
        }
    }

    internal static bool MatchesType(XElement element, string? type)
        => string.IsNullOrWhiteSpace(type) || element.Name.LocalName == type.Split('.').Last() ||
            ((string?)element.Attribute(Xaml + "Class"))?.Split('.').Last() == type.Split('.').Last();

    internal static string? Name(XElement element) =>
        (string?)element.Attribute(Xaml + "Name") ?? (string?)element.Attribute("Name");

    internal static string TypeIdentity(XElement element) => element.Attribute(Xaml + "Class") is { } owner
        ? "class:" + owner.Value : element.Name.ToString();

    internal static string? AutomationId(XElement element) =>
        (string?)element.Attribute("AutomationProperties.AutomationId") ?? (string?)element.Attribute("AutomationId");

    internal static string Signature(XElement element)
    {
        var key = new StringBuilder();
        Append(key, element.Name.ToString());
        foreach (var attribute in element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration)
            .OrderBy(attribute => attribute.Name.ToString(), StringComparer.Ordinal))
        {
            Append(key, attribute.Name.ToString());
            Append(key, DevToolsSecrets.AttributeValue(attribute));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToString())));
    }

    internal static string Scope(XElement element)
    {
        var scope = new StringBuilder();
        foreach (var parent in element.Ancestors().Reverse())
        {
            Append(scope, parent.Name.ToString());
            Append(scope, (string?)parent.Attribute(Xaml + "Class") ?? "");
            Append(scope, Name(parent) ?? "");
            Append(scope, AutomationId(parent) ?? "");
        }
        return scope.ToString();
    }

    internal static string Structure(XElement element)
    {
        var path = new StringBuilder();
        foreach (var node in element.AncestorsAndSelf().Reverse())
        {
            Append(path, node.Name.ToString());
            Append(path, (node.Parent?.Elements(node.Name).TakeWhile(sibling => !ReferenceEquals(sibling, node)).Count() ?? 0)
                .ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return path.ToString();
    }

    private static void Append(StringBuilder value, string field) => value.Append(field.Length).Append(':').Append(field);

    /// <summary>How much of the captured declaration survives in <paramref name="element"/>: unchanged attributes count double.</summary>
    internal static int Similarity(XElement captured, XElement element)
    {
        var score = 0;
        foreach (var attribute in captured.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration))
        {
            if (element.Attribute(attribute.Name) is { } current)
            {
                score += DevToolsSecrets.AttributeValue(current) == DevToolsSecrets.AttributeValue(attribute) ? 2 : 1;
            }
        }
        return score;
    }

    internal static XElement ParseOpeningTag(string markup, XElement context)
    {
        var names = new NameTable();
        var namespaces = new XmlNamespaceManager(names);
        foreach (var element in context.AncestorsAndSelf().Reverse())
        {
            foreach (var attribute in element.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
            {
                namespaces.AddNamespace(attribute.Name.LocalName == "xmlns" ? "" : attribute.Name.LocalName, attribute.Value);
            }
        }
        var opening = DevToolsSecrets.EscapeForXml(markup).Trim();
        if (!opening.EndsWith('>'))
        {
            throw new InvalidDataException("The captured authored declaration is incomplete.");
        }
        if (!opening.EndsWith("/>", StringComparison.Ordinal))
        {
            opening = opening[..^1] + "/>";
        }
        using var text = new StringReader(opening);
        using var reader = XmlReader.Create(text, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            ConformanceLevel = ConformanceLevel.Fragment,
            MaxCharactersInDocument = MaximumDeclarationLength,
        }, new XmlParserContext(names, namespaces, null, XmlSpace.None));
        reader.MoveToContent();
        var result = (XElement)XNode.ReadFrom(reader);
        if (reader.MoveToContent() != XmlNodeType.None)
        {
            throw new InvalidDataException("The captured authored declaration contains multiple elements.");
        }
        return result;
    }
}
