// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace WinApp.Cli.Services.ApiSearch;

/// <summary>
/// Parses .NET XML documentation files (the same descriptions Visual Studio
/// IntelliSense shows) and merges their summaries onto parsed metadata types.
/// </summary>
internal static partial class XmlDocParser
{
    /// <summary>
    /// Parses a .NET XML documentation file and returns a dictionary mapping
    /// member doc IDs (e.g., "T:Namespace.Type", "P:Namespace.Type.Property")
    /// to their summary text.
    /// </summary>
    public static Dictionary<string, string> ParseFile(string xmlPath)
    {
        var docs = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var reader = XmlReader.Create(xmlPath, new XmlReaderSettings
            {
                IgnoreComments = true,
                // Whitespace is content here: the only thing separating two adjacent
                // references is a whitespace-only text node, and dropping it renders
                // "Alpha Beta" as "AlphaBeta". Runs of it are collapsed after the
                // markup has been flattened.
                IgnoreWhitespace = false,
                DtdProcessing = DtdProcessing.Ignore
            });

            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Name == "member")
                {
                    string? name = reader.GetAttribute("name");
                    if (name == null)
                    {
                        continue;
                    }

                    XElement element;
                    using (XmlReader memberReader = reader.ReadSubtree())
                    {
                        element = XElement.Load(memberReader);
                    }
                    string? summary = ExtractSummary(element);
                    if (summary != null)
                    {
                        docs[name] = summary;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            // Malformed or unreadable XML docs are non-fatal: descriptions are an
            // enrichment on top of the metadata, so skip this file silently rather
            // than writing to stderr (which would corrupt --json output).
        }
        return docs;
    }

    private static string? ExtractSummary(XElement member)
    {
        XElement? summary = member.Element("summary");
        if (summary is null)
        {
            return null;
        }

        var builder = new StringBuilder();
        AppendNodes(summary, builder);
        string text = WhitespaceRegex().Replace(builder.ToString(), " ").Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>
    /// Flattens documentation markup to the prose a caller reads.
    /// <para>
    /// The markup carries meaning in attributes, not just in text: <c>&lt;see
    /// langword="null"/&gt;</c> *is* the word "null", and deleting the element deletes the
    /// answer while leaving a fluent sentence behind — "or to use the default encoder"
    /// reads as though nothing is missing. So every element is mapped explicitly rather
    /// than stripped.
    /// </para>
    /// </summary>
    private static void AppendNodes(XElement element, StringBuilder builder)
    {
        foreach (XNode node in element.Nodes())
        {
            switch (node)
            {
                case XText text:
                    builder.Append(text.Value);
                    break;
                case XElement child:
                    AppendElement(child, builder);
                    break;
            }
        }
    }

    private static void AppendElement(XElement element, StringBuilder builder)
    {
        // A paragraph or line break is a word boundary; without one the surrounding
        // words run together once whitespace is collapsed.
        bool isBlock = element.Name.LocalName is "para" or "br" or "p";
        if (isBlock)
        {
            builder.Append(' ');
        }

        // Explicit content wins over any attribute: <see cref="X">custom text</see>
        // documents itself.
        if (element.Nodes().Any())
        {
            AppendNodes(element, builder);
        }
        else if (element.Attribute("langword")?.Value is { Length: > 0 } langword)
        {
            builder.Append(langword);
        }
        else if (element.Attribute("cref")?.Value is { Length: > 0 } cref)
        {
            builder.Append(ShortCrefName(cref));
        }
        else if (element.Attribute("name")?.Value is { Length: > 0 } name)
        {
            // paramref and typeparamref name the thing they refer to.
            builder.Append(name);
        }

        if (isBlock)
        {
            builder.Append(' ');
        }
    }

    /// <summary>
    /// The readable name in a documentation reference: <c>T:System.Text.Json.JsonElement</c>
    /// becomes <c>JsonElement</c>.
    /// </summary>
    private static string ShortCrefName(string cref)
    {
        // A doc ID is prefixed with its member kind ("T:", "M:", "P:"...).
        string name = cref.Length > 2 && cref[1] == ':' ? cref[2..] : cref;

        // Method references carry a parameter list, whose own dotted type names would
        // otherwise be mistaken for the trailing segment.
        int parameters = name.IndexOf('(', StringComparison.Ordinal);
        if (parameters >= 0)
        {
            name = name[..parameters];
        }

        int dot = name.LastIndexOf('.');
        return dot >= 0 ? name[(dot + 1)..] : name;
    }

    /// <summary>
    /// Merges XML doc descriptions into parsed type/member data.
    /// </summary>
    public static void MergeDescriptions(List<WinMdTypeInfo> types, Dictionary<string, string> docs)
    {
        foreach (var type in types)
        {
            string typeKey = "T:" + type.FullName;
            if (docs.TryGetValue(typeKey, out string? typeDesc))
            {
                type.Description = typeDesc;
            }

            foreach (var member in type.Members)
            {
                string? memberKey = GetMemberDocKey(type.FullName, member);
                if (memberKey != null && docs.TryGetValue(memberKey, out string? memberDesc))
                {
                    member.Description = memberDesc;
                }
            }
        }
    }

    private static string? GetMemberDocKey(string typeFullName, WinMdMemberInfo member)
    {
        return member.Kind switch
        {
            MemberKind.Property => "P:" + typeFullName + "." + member.Name,
            MemberKind.Event => "E:" + typeFullName + "." + member.Name,
            MemberKind.Field => "F:" + typeFullName + "." + member.Name,
            MemberKind.Method => BuildMethodDocKey(typeFullName, member),
            _ => null
        };
    }

    /// <summary>
    /// Builds the documentation ID for a method.
    /// <para>
    /// The parameter types come from <see cref="WinMdMemberInfo.DocParameterTypes"/>
    /// rather than the displayed signature, because a documentation file spells types the
    /// way the compiler emits them — <c>System.Double</c>, not <c>Double</c> — and a key
    /// built from display names matches nothing for any method taking a primitive.
    /// </para>
    /// </summary>
    private static string BuildMethodDocKey(string typeFullName, WinMdMemberInfo member)
    {
        string key = "M:" + typeFullName + "." + member.Name;
        if (member.GenericParameterCount > 0)
        {
            key += "``" + member.GenericParameterCount.ToString(CultureInfo.InvariantCulture);
        }
        if (member.DocParameterTypes is { Count: > 0 } docTypes)
        {
            key += "(" + string.Join(",", docTypes) + ")";
        }
        return key;
    }

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();
}
