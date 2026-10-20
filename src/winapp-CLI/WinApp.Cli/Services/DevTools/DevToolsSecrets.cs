// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace WinApp.Cli.Services.DevTools;

/// <summary>
/// Keeps secret values out of authored XAML that DevTools shows or stores. Same rule as the in-app agent: a property
/// whose name ends in "Password" holds a secret. Markup extensions ({x:Bind ...}) name a source, not a value, and stay.
/// </summary>
internal static partial class DevToolsSecrets
{
    internal const string Redacted = "<redacted>";

    internal static bool IsSecretName(string name) => name.EndsWith("Password", StringComparison.OrdinalIgnoreCase);

    internal static string AttributeValue(XAttribute attribute) =>
        IsSecretName(attribute.Name.LocalName) && !attribute.Value.StartsWith('{') ? Redacted : attribute.Value;

    /// <summary>Replaces secret attribute and property-element values in raw XAML text, keeping the names.</summary>
    internal static string RedactXaml(string xaml)
    {
        var redacted = SecretAttribute().Replace(xaml, match => match.Groups["value"].Value.StartsWith('{')
            ? match.Value
            : match.Groups["head"].Value + Redacted + match.Groups["quote"].Value);
        return SecretPropertyElement().Replace(redacted, match => match.Groups["open"].Value + Redacted + match.Groups["close"].Value);
    }

    /// <summary>Makes redacted XAML text parseable as XML again; '&lt;' is not allowed inside an attribute value.</summary>
    internal static string EscapeForXml(string xaml) => xaml
        .Replace("\"" + Redacted + "\"", "\"&lt;redacted&gt;\"", StringComparison.Ordinal)
        .Replace("'" + Redacted + "'", "'&lt;redacted&gt;'", StringComparison.Ordinal);

    [GeneratedRegex(@"(?<head>(?<=\s)[\w.:]*Password\s*=\s*(?<quote>[""']))(?<value>.*?)\k<quote>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex SecretAttribute();

    [GeneratedRegex(@"(?<open><(?<tag>[\w:]+\.[\w]*Password)(\s[^>]*)?>)(?<value>[^<]*)(?<close></\k<tag>\s*>)", RegexOptions.IgnoreCase)]
    private static partial Regex SecretPropertyElement();
}
