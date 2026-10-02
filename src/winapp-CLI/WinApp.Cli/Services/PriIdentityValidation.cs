// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services;

/// <summary>
/// Validates the supported single-map MakePri detailed format, then compares its entire graph.
/// Only identity authorities, numeric serialization references, map checksums, and sibling order
/// outside a Decision may change. Expanded qualifier-set order inside a Decision is semantic.
/// </summary>
internal static class PriIdentityValidation
{
    internal sealed record Snapshot(string Graph, IReadOnlyDictionary<string, string> PathHashes);

    internal static async Task<Snapshot> ReadAsync(
        string dumpPath,
        DirectoryInfo layout,
        string expectedPackageName,
        string? unsupportedAuthority,
        CancellationToken cancellationToken)
    {
        using var reader = XmlReader.Create(dumpPath, new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 128 * 1024 * 1024
        });
        var document = await XDocument.LoadAsync(reader, LoadOptions.PreserveWhitespace, cancellationToken);
        var root = document.Root ?? throw Invalid("the detailed dump is empty");
        if (root.Name != "PriInfo")
        {
            throw Invalid("unknown detailed-dump format");
        }
        foreach (var element in root.DescendantsAndSelf())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (element.Ancestors().Take(65).Count() > 64)
            {
                throw Invalid("resource nesting exceeds the supported depth");
            }
            ValidateShape(element);
        }

        var header = root.Element("PriHeader")!;
        var environment = header.Element("WindowsEnvironment")!;
        _ = Number(environment, "checksum");
        if ((string?)environment.Attribute("name") != "WinCore"
            || (string?)environment.Attribute("version") != "1.2"
            || (string?)header.Element("TargetOS")!.Attribute("version") != "10.0.0"
            || header.Element("AutoMerge")!.Value.Trim() != "false"
            || header.Element("IsDeploymentMergeable")!.Value.Trim() != "true"
            || header.Element("ReverseMap")!.Value.Trim() != "false")
        {
            throw Invalid("unsupported PRI environment, merge, or reverse-map format");
        }

        var map = root.Element("ResourceMap")!;
        if ((string?)map.Attribute("name") != expectedPackageName
            || (string?)map.Attribute("uniqueName") != $"ms-appx://{expectedPackageName}/"
            || (string?)map.Attribute("primary") != "true"
            || (string?)map.Attribute("version") != "1.0"
            || (string?)map.Element("VersionInfo")!.Attribute("version") != "1.0")
        {
            throw Invalid($"expected one primary resource map for '{expectedPackageName}' with version 1.0; split or other maps are unsupported");
        }
        var resources = map.Descendants("NamedResource").ToArray();
        var version = map.Element("VersionInfo")!;
        if (!long.TryParse((string?)version.Attribute("checksum"), NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out _))
        {
            throw Invalid("invalid resource-map checksum");
        }
        if (Number(version, "numItems") != resources.Length
            || Number(version, "numScopes") != map.Descendants("ResourceMapSubtree").Count() + 1)
        {
            throw Invalid("resource-map item or scope counts do not match the detailed graph");
        }
        var prefix = $"ms-resource://{expectedPackageName}/";
        var uris = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resource in resources)
        {
            var uri = (string)resource.Attribute("uri")!;
            var segments = resource.Ancestors("ResourceMapSubtree").Reverse()
                .Select(scope => (string)scope.Attribute("name")!).Append((string)resource.Attribute("name")!);
            if (uri != prefix + string.Join("/", segments) || !uris.Add(uri))
            {
                throw Invalid($"resource '{uri}' has an unexpected authority, scope, or duplicate name");
            }
        }
        ValidateExpandedReferences(root);

        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var candidate in map.Descendants("Candidate"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var type = (string)candidate.Attribute("type")!;
            if (type == "String" && unsupportedAuthority is not null)
            {
                try
                {
                    DevelopmentIdentityHelper.ValidateResourceReference(candidate.Element("Value")!.Value, unsupportedAuthority);
                }
                catch (InvalidOperationException ex)
                {
                    throw new InvalidDataException(
                        $"Cannot prove PRI identity fidelity for string resource '{candidate.Parent!.Attribute("uri")!.Value}': {ex.Message}", ex);
                }
            }
            if (type == "Path")
            {
                var path = candidate.Element("Value")!.Value;
                if (!hashes.ContainsKey(path))
                {
                    var fullPath = ValidateCandidatePath(path, layout);
                    await using var file = File.OpenRead(fullPath);
                    hashes.Add(path, Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken)));
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new Snapshot(Normalize(root, expectedPackageName), hashes);
    }

    internal static void EnsureEquivalent(Snapshot before, Snapshot after)
    {
        if (!string.Equals(before.Graph, after.Graph, StringComparison.Ordinal))
        {
            throw Invalid("reindexing changed the resource graph beyond its identity (resource names, values, types, qualifiers, decisions, or metadata)");
        }
        if (before.PathHashes.Count != after.PathHashes.Count
            || before.PathHashes.Any(pair => !after.PathHashes.TryGetValue(pair.Key, out var hash) || hash != pair.Value))
        {
            throw Invalid("a resource candidate's file payload changed during reindexing");
        }
    }

    private static string ValidateCandidatePath(string path, DirectoryInfo layout)
    {
        var components = path.Split(['\\', '/']);
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)
            || components.Any(part => part.Length == 0 || part is "." or ".."
                || part.EndsWith(' ') || part.EndsWith('.') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            || path.Contains("original.pri", StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid($"resource candidate path '{path}' is not a supported layout-relative payload path");
        }
        var fullPath = Path.GetFullPath(Path.Combine(layout.FullName, path));
        if (PathSafety.HasReparsePointOnPath(fullPath, layout.FullName)
            || string.Equals(fullPath, Path.Join(layout.FullName, "resources.pri"), StringComparison.OrdinalIgnoreCase)
            || !File.Exists(fullPath))
        {
            throw Invalid($"resource candidate path '{path}' must refer to an existing file inside the layout, without links or temporary PRI input dependencies");
        }
        return fullPath;
    }

    private static void ValidateShape(XElement element)
    {
        var (attributes, children) = element.Name.LocalName switch
        {
            "PriInfo" => ("", "PriHeader QualifierInfo ResourceMap"),
            "PriHeader" => ("", "WindowsEnvironment AutoMerge IsDeploymentMergeable TargetOS ReverseMap"),
            "WindowsEnvironment" => ("name version checksum", ""),
            "AutoMerge" or "IsDeploymentMergeable" or "ReverseMap" => ("", ""),
            "TargetOS" => ("version", ""),
            "QualifierInfo" => ("", "Qualifiers QualifierSets Decisions"),
            "Qualifiers" => ("", "Qualifier"),
            "QualifierSets" => ("", "QualifierSet"),
            "Decisions" => ("", "Decision"),
            "Qualifier" => ("name value priority scoreAsDefault index", ""),
            "QualifierSet" => ("index", "Qualifier"),
            "Decision" => ("index", "QualifierSet"),
            "ResourceMap" => ("name primary uniqueName version", "VersionInfo ResourceMapSubtree NamedResource"),
            "VersionInfo" => ("version checksum numScopes numItems", ""),
            "ResourceMapSubtree" => ("index name", "ResourceMapSubtree NamedResource"),
            "NamedResource" => ("name index uri", "Decision Candidate"),
            "Candidate" => ("type", "QualifierSet Value Base64Value"),
            "Value" or "Base64Value" => ("", ""),
            _ => throw Invalid($"unknown detailed-dump element '{element.Name}'")
        };
        var requiredAttributes = attributes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var allowedChildren = children.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (element.Name.Namespace != XNamespace.None
            || element.Attributes().Count() != requiredAttributes.Length
            || requiredAttributes.Any(name => element.Attribute(name) is null)
            || element.Elements().Any(child => child.Name.Namespace != XNamespace.None
                || !allowedChildren.Contains(child.Name.LocalName, StringComparer.Ordinal))
            || element.Nodes().Any(node => node is not XElement and not XText and not XComment))
        {
            throw Invalid($"unknown or incomplete detailed-dump structure at '{element.Name}'");
        }
        var isText = element.Name.LocalName is "Value" or "Base64Value" or "AutoMerge" or "IsDeploymentMergeable" or "ReverseMap";
        if (!isText && element.Nodes().OfType<XText>().Any(text => !string.IsNullOrWhiteSpace(text.Value)))
        {
            throw Invalid($"unexpected text at '{element.Name}'");
        }
        if (element.Attribute("index") is not null)
        {
            _ = Number(element, "index");
        }
        if (element.Name.LocalName is "PriInfo" or "PriHeader" or "QualifierInfo")
        {
            foreach (var child in allowedChildren)
            {
                RequireOne(element, child);
            }
        }
        switch (element.Name.LocalName)
        {
            case "ResourceMap":
                RequireOne(element, "VersionInfo");
                break;
            case "NamedResource":
                RequireOne(element, "Decision");
                if (!element.Elements("Candidate").Any())
                {
                    throw Invalid("a named resource has no candidates");
                }
                break;
            case "Decision":
                if (!element.Elements().Any())
                {
                    throw Invalid("a decision has no expanded qualifier sets");
                }
                break;
            case "Candidate":
                RequireOne(element, "QualifierSet");
                var type = (string)element.Attribute("type")!;
                var valueElement = type switch
                {
                    "String" or "Path" => "Value",
                    "EmbeddedData" => "Base64Value",
                    _ => throw Invalid($"unsupported candidate type '{type}'")
                };
                RequireOne(element, valueElement);
                if (element.Elements().Count() != 2)
                {
                    throw Invalid("a candidate has conflicting payloads");
                }
                break;
            case "Base64Value":
                try
                {
                    _ = Convert.FromBase64String(element.Value);
                }
                catch (FormatException ex)
                {
                    throw new InvalidDataException("Cannot prove PRI identity fidelity: invalid embedded Base64 payload.", ex);
                }
                break;
            case "Qualifier":
                _ = Number(element, "priority");
                if (!decimal.TryParse((string?)element.Attribute("scoreAsDefault"), NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out var score) || score is < 0 or > 1)
                {
                    throw Invalid("a qualifier has an invalid default score");
                }
                break;
        }
        if (element.Name.LocalName is "ResourceMap" or "ResourceMapSubtree")
        {
            var names = element.Elements().Where(child => child.Attribute("name") is not null)
                .Select(child => (string)child.Attribute("name")!).ToArray();
            if (names.Any(string.IsNullOrEmpty) || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
            {
                throw Invalid("a resource scope has empty or duplicate names");
            }
        }
    }

    private static void ValidateExpandedReferences(XElement root)
    {
        foreach (var kind in new[] { "Qualifier", "QualifierSet", "Decision" })
        {
            var definitions = new Dictionary<uint, string>();
            foreach (var element in root.Descendants(kind))
            {
                var index = Number(element, "index");
                var value = Normalize(element, "");
                if (definitions.TryGetValue(index, out var previous) && previous != value)
                {
                    throw Invalid($"conflicting expanded {kind} definitions at index {index}");
                }
                definitions[index] = value;
            }
        }
    }

    private static void RequireOne(XElement parent, string name)
    {
        if (parent.Elements(name).Count() != 1)
        {
            throw Invalid($"expected exactly one '{name}' in '{parent.Name}'; multi-map and split graphs are unsupported");
        }
    }

    private static uint Number(XElement element, string name)
    {
        if (!uint.TryParse((string?)element.Attribute(name), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            throw Invalid($"invalid numeric '{name}' on '{element.Name}'");
        }
        return value;
    }

    private static string Normalize(XElement element, string packageName) =>
        NormalizeElement(element, packageName).ToString(SaveOptions.DisableFormatting);

    private static XElement NormalizeElement(XElement element, string packageName)
    {
        var normalized = new XElement(element.Name);
        foreach (var attribute in element.Attributes().OrderBy(attribute => attribute.Name.LocalName, StringComparer.Ordinal))
        {
            if (attribute.Name == "index" || (element.Name == "VersionInfo" && attribute.Name == "checksum"))
            {
                continue;
            }
            var value = attribute.Value;
            if (element.Name == "ResourceMap" && attribute.Name == "name")
            {
                value = "{identity}";
            }
            else if (element.Name == "ResourceMap" && attribute.Name == "uniqueName")
            {
                value = "ms-appx://{identity}/";
            }
            else if (element.Name == "NamedResource" && attribute.Name == "uri")
            {
                value = "ms-resource://{identity}/" + value[$"ms-resource://{packageName}/".Length..];
            }
            normalized.Add(new XAttribute(attribute.Name, value));
        }
        var text = string.Concat(element.Nodes().OfType<XText>().Select(node => node.Value));
        if (element.Name == "Base64Value")
        {
            var bytes = Convert.FromBase64String(text);
            text = $"{bytes.Length.ToString(CultureInfo.InvariantCulture)}:{Convert.ToHexString(SHA256.HashData(bytes))}";
        }
        else if (element.Name != "Value")
        {
            text = text.Trim();
        }
        normalized.Add(text);
        IEnumerable<XElement> children = element.Elements().Select(child => NormalizeElement(child, packageName));
        if (element.Name != "Decision")
        {
            children = children.OrderBy(child => child.ToString(SaveOptions.DisableFormatting), StringComparer.Ordinal);
        }
        foreach (var child in children)
        {
            normalized.Add(child);
        }
        return normalized;
    }

    private static InvalidDataException Invalid(string reason) => new($"Cannot prove PRI identity fidelity: {reason}.");
}
