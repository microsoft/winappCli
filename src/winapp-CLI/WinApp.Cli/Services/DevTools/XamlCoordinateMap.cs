// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Services.DevTools;

// Structural pairing never establishes a running image's identity or changes a stored anchor.
internal sealed class XamlCoordinateMap
{
    internal sealed record BuildProof(
        string ProjectPath, string ConfigurationIdentity, string IntermediateRoot, string CompilerIdentity,
        string SourceRelativePath, string ResourcePath, string? Link,
        string GeneratedPath, string XbfPath, string SourceHash, string GeneratedHash, string XbfHash,
        long SourceWriteTicks, long SavedSourceWriteTicks,
        string ResourceMapName = "", string ComponentResourceLocation = "");

    internal sealed record Hit(int RawLine, int RawColumn, int AuthoredLine, int AuthoredColumn,
        string Declaration, bool Advisory, string Provenance);

    // Source-side declaration span; the compiler preserves lines, so the emitted element starts and ends on the same lines.
    internal sealed record Element(int Line, int EndLine, int Column, string Type,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RuntimeClass,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ParentLine = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ParentType = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ParentName = null);

    private sealed record ElementSpan(XElement Element, int Start, int End);
    internal sealed record SourceDeclaration(XElement Element, string Text, int Line, int Column, int EndLine);
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XName DataTemplate = XName.Get("DataTemplate", "http://schemas.microsoft.com/winfx/2006/xaml/presentation");
    private readonly BuildProof _proof;
    private readonly string _source;
    private readonly List<ElementSpan> _originalElements;
    private readonly int[] _sourceLines;

    internal IReadOnlyList<Element> Elements => _originalElements.Select(original =>
    {
        var authored = Position(_sourceLines, original.Start);
        return new Element(authored.Line, Position(_sourceLines, original.End).Line, authored.Column,
            original.Element.Name.LocalName, NameOf(original.Element),
            (string?)original.Element.Attribute(Xaml + "Class"));
    }).ToArray();

    internal static IReadOnlyList<Element> SourceElements(byte[] bytes)
    {
        var source = Decode(bytes);
        var lines = LineStarts(source);
        return Parse(source, lines).Select(span =>
        {
            var start = Position(lines, span.Start);
            var parent = span.Element.Parent;
            return new Element(start.Line, Position(lines, span.End).Line, start.Column,
                span.Element.Name.LocalName, NameOf(span.Element),
                (string?)span.Element.Attribute(Xaml + "Class"),
                parent is null ? null : ((IXmlLineInfo)parent).LineNumber,
                parent?.Name.LocalName, parent is null ? null : NameOf(parent));
        }).ToArray();
    }

    internal static IReadOnlyList<SourceDeclaration> SourceDeclarations(byte[] bytes)
        => SourceDeclarations(Decode(bytes));

    internal static IReadOnlyList<SourceDeclaration> SourceDeclarations(string source)
    {
        var lines = LineStarts(source);
        return Parse(source, lines).Select(span =>
        {
            var start = Position(lines, span.Start);
            return new SourceDeclaration(span.Element, source[span.Start..(span.End + 1)], start.Line, start.Column,
                Position(lines, span.End).Line);
        }).ToArray();
    }

    private XamlCoordinateMap(BuildProof proof, string source, List<ElementSpan> originalElements)
    {
        _proof = proof;
        _source = source;
        _originalElements = originalElements;
        _sourceLines = LineStarts(source);
    }

    internal const string StaleBuild = "The build output does not match the XAML sources. Rebuild (run without --no-build) to get source locations.";

    internal static XamlCoordinateMap Create(GuestSourceManifest snapshot, BuildProof proof,
        byte[] sourceBytes, byte[] generatedBytes, byte[] xbfBytes)
    {
        if (string.IsNullOrWhiteSpace(proof.ConfigurationIdentity) || string.IsNullOrWhiteSpace(proof.CompilerIdentity) ||
            !Path.IsPathFullyQualified(proof.ProjectPath) || !Path.IsPathFullyQualified(proof.IntermediateRoot) ||
            !string.Equals(snapshot.ProjectPath, proof.ProjectPath, StringComparison.OrdinalIgnoreCase) ||
            proof.SourceWriteTicks <= 0 || proof.SourceWriteTicks != proof.SavedSourceWriteTicks ||
            proof.ResourceMapName.Length != 0 || proof.ComponentResourceLocation.Length != 0)
        {
            throw new InvalidDataException(StaleBuild);
        }
        var sourcePath = GuestCommentBinding.ValidateRelativeSource(proof.SourceRelativePath);
        var resource = GuestCommentBinding.ValidateRelativeSource(proof.ResourcePath);
        var apparent = GuestCommentBinding.ValidateRelativeSource(proof.Link ?? sourcePath);
        if (!resource.Equals(apparent, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(Path.Combine(proof.IntermediateRoot, resource)).Equals(proof.GeneratedPath, StringComparison.OrdinalIgnoreCase) ||
            !Path.ChangeExtension(proof.GeneratedPath, ".xbf").Equals(proof.XbfPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Resource/link identity does not match the selected compiler outputs.");
        }
        var admitted = snapshot.Files.Where(file => file.RelativePath.Equals(sourcePath, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (admitted.Length != 1 || admitted[0].Length != sourceBytes.Length ||
            !admitted[0].Sha256.Equals(proof.SourceHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Source is not uniquely admitted by the selected source snapshot.");
        }
        VerifyBytes(sourceBytes, proof.SourceHash);
        VerifyBytes(generatedBytes, proof.GeneratedHash);
        VerifyBytes(xbfBytes, proof.XbfHash);
        XbfSourceFingerprint.Verify(sourceBytes, xbfBytes);
        var source = Decode(sourceBytes);
        var generated = Decode(generatedBytes);
        var sourceLines = LineStarts(source);
        var generatedLines = LineStarts(generated);
        var originals = Parse(source, sourceLines);
        var emitted = Parse(generated, generatedLines);
        if (originals.Count != emitted.Count)
        {
            throw new InvalidDataException("Compiler rewrite changed element structure.");
        }

        var normalizedSource = new StringBuilder(source);
        var removals = new List<(int Start, int Length)>();
        var connectionIds = new HashSet<int>();
        for (var i = 0; i < originals.Count; i++)
        {
            var original = originals[i].Element;
            var rewritten = emitted[i].Element;
            if (original.Name != rewritten.Name ||
                original.Ancestors().Count() != rewritten.Ancestors().Count() ||
                Position(sourceLines, originals[i].Start).Line != Position(generatedLines, emitted[i].Start).Line ||
                Position(sourceLines, originals[i].End).Line != Position(generatedLines, emitted[i].End).Line)
            {
                throw new InvalidDataException("Compiler elements do not pair structurally.");
            }
            var connection = rewritten.Attribute(Xaml + "ConnectionId");
            var inserted = connection is not null && original.Attribute(Xaml + "ConnectionId") is null;
            var erased = original.Attributes().Where(attribute => rewritten.Attribute(attribute.Name) is null).ToArray();
            foreach (var attribute in erased)
            {
                // The compiler removes x:Bind expressions and, on elements it connects, event-handler wiring. It also
                // blanks a typed template's x:DataType and the compile-time directives x:DefaultBindMode and x:Phase.
                // Any other removal means the source is not what was compiled.
                if (!IsBinding(attribute) && !(inserted && IsEventHandler(attribute)) &&
                    !(attribute.Name == Xaml + "DataType" && original.Name == DataTemplate) &&
                    attribute.Name != Xaml + "DefaultBindMode" && attribute.Name != Xaml + "Phase")
                {
                    throw new InvalidDataException($"Unsupported compiler rewrite of '{attribute.Name.LocalName}' on <{original.Name.LocalName}> at line {((IXmlLineInfo)attribute).LineNumber}.");
                }
            }
            var deferred = original.Attribute(Xaml + "Load") is { } load && IsBinding(load) ? load : null;
            if (deferred is not null && (string?)rewritten.Attribute(Xaml + "Load") != "False")
            {
                throw new InvalidDataException($"Unsupported compiler rewrite of 'x:Load' on <{original.Name.LocalName}> at line {((IXmlLineInfo)deferred).LineNumber}.");
            }
            if (inserted)
            {
                var classRoot = original.Parent is null && original.Attribute(Xaml + "Class") is { Value.Length: > 0 };
                // A typed template's root is connected so the compiler can bind each item.
                var templateRoot = original.Parent is { } template && template.Name == DataTemplate && template.Attribute(Xaml + "DataType") is not null;
                // A named element (x:Name, or Name on a FrameworkElement) is connected so code-behind gets its field.
                var named = NameOf(original) is not null;
                if ((erased.Length == 0 && deferred is null && !named && !classRoot && !templateRoot) ||
                    original.GetNamespaceOfPrefix("x") != Xaml ||
                    !int.TryParse(connection!.Value, out var id) || id <= 0 || !connectionIds.Add(id))
                {
                    throw new InvalidDataException($"Unexplained compiler connection insertion on <{original.Name.LocalName}> at line {((IXmlLineInfo)original).LineNumber}.");
                }
                var start = Offset(generatedLines, (IXmlLineInfo)connection);
                var end = AttributeEnd(generated, start);
                if (start == 0 || generated[start - 1] != ' ' ||
                    generated.AsSpan(start, end - start).ContainsAny('\r', '\n'))
                {
                    throw new InvalidDataException("Unknown connection insertion shape.");
                }
                removals.Add((start - 1, end - start + 1));
            }
            else if (erased.Any(IsBinding))
            {
                throw new InvalidDataException("Missing compiler connection metadata for x:Bind.");
            }
            foreach (var attribute in erased)
            {
                Blank(attribute);
            }
            if (deferred is not null)
            {
                // x:Load="{x:Bind ...}" is compiled to x:Load="False" padded to the same width.
                var start = Offset(sourceLines, (IXmlLineInfo)deferred);
                var end = AttributeEnd(source, start);
                const string compiled = "x:Load=\"False\"";
                if (end - start < compiled.Length || source.AsSpan(start, end - start).ContainsAny('\r', '\n'))
                {
                    throw new InvalidDataException("Unknown x:Load rewrite shape.");
                }
                normalizedSource.Remove(start, end - start).Insert(start, compiled.PadRight(end - start));
            }

            void Blank(XAttribute attribute)
            {
                var start = Offset(sourceLines, (IXmlLineInfo)attribute);
                var end = AttributeEnd(source, start);
                for (var position = start; position < end; position++)
                {
                    if (source[position] is not ('\r' or '\n'))
                    {
                        normalizedSource[position] = ' ';
                    }
                }
            }
        }
        var normalizedGenerated = new StringBuilder(generated);
        foreach (var (start, length) in removals.OrderByDescending(removal => removal.Start))
        {
            normalizedGenerated.Remove(start, length);
        }
        var expected = normalizedSource.ToString();
        var actual = normalizedGenerated.ToString();
        // The compiler writes CRLF whatever the source uses, and may append line breaks at end of file; neither
        // moves a line, so compare the text with line endings normalized.
        if (actual.ReplaceLineEndings("\n").TrimEnd('\n') != expected.ReplaceLineEndings("\n").TrimEnd('\n'))
        {
            throw new InvalidDataException("Unknown rewrite or changed authored source.");
        }
        return new(proof, source, originals);
    }

    internal Hit Resolve(BuildProof currentProof, string resourcePath, int line, int column, string type, string? name)
    {
        if (currentProof != _proof || resourcePath != _proof.ResourcePath || string.IsNullOrWhiteSpace(type))
        {
            throw new InvalidDataException("The requested source/build identity differs from the mapping.");
        }
        if (line <= 0 || column <= 0)
        {
            throw new InvalidDataException("An exact emitted coordinate is required.");
        }
        if (line > _sourceLines.Length)
        {
            throw new InvalidDataException("Line is outside the source.");
        }
        // ConnectionId widths need not describe the running image. Only the preserved line is used.
        var candidates = _originalElements.Where(element =>
            Position(_sourceLines, element.Start).Line <= line && Position(_sourceLines, element.End).Line >= line).ToArray();
        if (candidates.Length != 1)
        {
            throw new InvalidDataException("The source line does not identify one authored opening tag.");
        }
        var original = candidates[0];
        if ((original.Element.Name.LocalName != type.Split('.').Last() &&
            (string?)original.Element.Attribute(Xaml + "Class") != type) ||
            (NameOf(original.Element) ?? "") != (name ?? ""))
        {
            throw new InvalidDataException("Mapped declaration disagrees with element identity.");
        }
        var info = (IXmlLineInfo)original.Element;
        // No live-image/capture identity is supplied by this offline contract, including for legacy notes.
        return new(line, column, info.LineNumber, info.LinePosition - 1,
            _source[original.Start..(original.End + 1)], true, "unique-source-line");
    }

    // x:Name, or the Name property, which names a FrameworkElement the same way.
    private static string? NameOf(XElement element) => (string?)element.Attribute(Xaml + "Name") ?? (string?)element.Attribute("Name");

    private static bool IsBinding(XAttribute attribute) =>
        attribute.Value.StartsWith("{x:Bind ", StringComparison.Ordinal) || attribute.Value.StartsWith("{x:Bind}", StringComparison.Ordinal);

    // A handler attribute names a code-behind method: a plain identifier on a non-directive attribute.
    private static bool IsEventHandler(XAttribute attribute) =>
        attribute.Name.Namespace == XNamespace.None && !attribute.IsNamespaceDeclaration &&
        attribute.Value.Length is > 0 and <= 512 && (char.IsLetter(attribute.Value[0]) || attribute.Value[0] == '_') &&
        attribute.Value.All(character => char.IsLetterOrDigit(character) || character == '_');

    private static void VerifyBytes(byte[] bytes, string expected)
    {
        if (bytes.Length == 0 || bytes.Length > GuestSourceSnapshot.MaximumFileBytes ||
            !Convert.ToHexString(SHA256.HashData(bytes)).Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Artifact bytes differ from the selected build or exceed the bound.");
        }
    }

    private static string Decode(byte[] bytes)
    {
        var offset = bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
        return new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);
    }

    private static List<ElementSpan> Parse(string text, int[] lines)
    {
        using var input = new StringReader(text);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = GuestSourceSnapshot.MaximumFileBytes,
        });
        var document = XDocument.Load(reader, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
        var elements = document.Descendants().Take(XamlSourceCoordinates.MaximumElements + 1).ToArray();
        if (elements.Length > XamlSourceCoordinates.MaximumElements || elements.Any(element => element.Ancestors().Take(129).Count() > 128))
        {
            throw new InvalidDataException("The XAML coordinate table exceeds its element or nesting limit.");
        }
        return elements.Select(element =>
        {
            var start = Offset(lines, (IXmlLineInfo)element) - 1;
            var end = start;
            char quote = '\0';
            for (; end < text.Length; end++)
            {
                var character = text[end];
                if (quote != '\0') { if (character == quote) { quote = '\0'; } }
                else if (character is '"' or '\'') { quote = character; }
                else if (character == '>') { break; }
            }
            if (start < 0 || text[start] != '<' || end == text.Length)
            {
                throw new InvalidDataException("Unterminated opening tag.");
            }
            return new ElementSpan(element, start, end);
        }).ToList();
    }

    private static int AttributeEnd(string text, int start)
    {
        var equals = text.IndexOf('=', start);
        var quote = equals + 1;
        while (quote < text.Length && char.IsWhiteSpace(text[quote])) { quote++; }
        if (equals < 0 || quote >= text.Length || text[quote] is not ('"' or '\''))
        {
            throw new InvalidDataException("Invalid attribute span.");
        }
        var end = text.IndexOf(text[quote], quote + 1);
        return end < 0 ? throw new InvalidDataException("Unterminated attribute.") : end + 1;
    }

    private static int Offset(int[] lines, IXmlLineInfo info) => lines[info.LineNumber - 1] + info.LinePosition - 1;

    private static (int Line, int Column) Position(int[] lines, int offset)
    {
        var index = Array.BinarySearch(lines, offset);
        if (index < 0) { index = ~index - 1; }
        return (index + 1, offset - lines[index] + 1);
    }

    // Line breaks as XML counts them: CRLF, LF, or a lone CR.
    private static int[] LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\n' || (text[index] == '\r' && (index + 1 == text.Length || text[index + 1] != '\n')))
            {
                starts.Add(index + 1);
            }
        }
        return starts.ToArray();
    }
}
