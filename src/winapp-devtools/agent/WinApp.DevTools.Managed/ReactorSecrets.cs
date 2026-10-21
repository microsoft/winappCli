// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;

namespace WinApp.DevTools.Managed;

/// <summary>
/// What DevTools treats as a secret in a Reactor app, and how it reads values without running more app code than it
/// must. Reactor reports values unredacted, so this is the only redaction: it applies to the agent's own formatting
/// (Reactor versions without text diagnostics) and to the text <c>DescribeComponent</c> and
/// <c>GetAppliedProperties</c> return.
/// </summary>
internal static partial class ReactorSecrets
{
    // A member, context or type whose name ends with one of these holds a secret. A bare "Key" does not.
    private static readonly string[] Suffixes =
        ["Password", "Secret", "Credential", "Token", "ApiKey", "PrivateKey", "ConnectionString"];

    /// <summary>Whether a member or context name says it holds a secret (<c>ApiPassword</c>, <c>AuthTokens</c>).</summary>
    internal static bool IsSecretName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }
        static bool Ends(string text) => Suffixes.Any(suffix => text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        return Ends(name) || (name.Length > 1 && (name[^1] is 's' or 'S') && Ends(name[..^1]));
    }

    /// <summary>
    /// Whether a value of this type is a secret: a <c>SecureString</c>, a type named like one, or a nullable, array,
    /// generic or collection of one (<c>List&lt;ApiToken&gt;</c>).
    /// </summary>
    internal static bool IsSecretType(Type? type) => IsSecretType(type, 0);

    private static bool IsSecretType(Type? type, int depth)
    {
        if (type is null || depth > 4 || type == typeof(string))
        {
            return false;
        }
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.FullName == "System.Security.SecureString")
        {
            return true;
        }
        var name = type.Name;
        if (name.IndexOf('`') is var tick and > 0)
        {
            name = name[..tick];
        }
        if (!type.IsGenericParameter && IsSecretName(name))
        {
            return true;
        }
        if (type.IsArray && IsSecretType(type.GetElementType(), depth + 1))
        {
            return true;
        }
        if (type.IsGenericType && type.GetGenericArguments().Any(argument => IsSecretType(argument, depth + 1)))
        {
            return true;
        }
        // A collection type of its own (class Tokens : List<AccessToken>) names its element through IEnumerable<T>.
        foreach (var contract in SafeInterfaces(type))
        {
            if (contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IEnumerable<>) &&
                IsSecretType(contract.GetGenericArguments()[0], depth + 1))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Whether a type as C# spells it in Reactor's text diagnostics (<c>SecureString</c>, <c>List&lt;ApiToken&gt;</c>,
    /// <c>AuthToken?</c>) names a secret: the same rule as <see cref="IsSecretType(Type?)"/>, applied to each name in it.
    /// </summary>
    internal static bool IsSecretTypeName(string? type) =>
        !string.IsNullOrEmpty(type) && TypeNamePart().Matches(type).Any(part => part.Value == "SecureString" || IsSecretName(part.Value));

    [GeneratedRegex("[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant)]
    private static partial Regex TypeNamePart();

    private static Type[] SafeInterfaces(Type type)
    {
        try { return type.GetInterfaces(); }
        catch (Exception ex) when (ex is TypeLoadException or NotSupportedException) { return []; }
    }

    /// <summary>
    /// Whether text carries a labelled secret: <c>AccessToken=abc</c>, a connection string with a password,
    /// <c>apiKey: abc</c>, or a record's <c>ToString</c> listing <c>Password = abc</c>. A bare label with no value
    /// (<c>Password:</c>) is not one.
    /// </summary>
    internal static bool HasLabelledSecret(string? text) => text is { Length: > 0 } && LabelledSecret().IsMatch(text);

    [GeneratedRegex("""
        (?<![A-Za-z0-9])[A-Za-z0-9_.\-]*(?:password|passwd|pwd|secret|credentials?|token|api[_\-]?key|private[_\-]?key|connection[_\-]?string|account[_\-]?key|access[_\-]?key)["']?\s*[:=]\s*["']?(?=[^\s,;}\])"'])
        """, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LabelledSecret();

    /// <summary>
    /// A value's <c>ToString</c>, contained: when it throws, <c>TaskItem (ToString() threw InvalidOperationException)</c>.
    /// </summary>
    internal static string SafeText(object value, Func<object, string?> text)
    {
        try { return text(value) ?? ""; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var error = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
            return $"{ReactorComponents.FriendlyTypeName(value.GetType())} (ToString() threw {error.GetType().Name})";
        }
    }

    /// <summary>
    /// How many items a collection holds, without enumerating it (which could run app code or never end):
    /// <c>ICollection.Count</c> or a public <c>int Count</c> with a public getter; null when neither answers.
    /// </summary>
    internal static int? Count(object value)
    {
        try
        {
            if (value is ICollection collection)
            {
                return collection.Count;
            }
            var getter = value.GetType().GetProperty("Count", BindingFlags.Public | BindingFlags.Instance, null, typeof(int), Type.EmptyTypes, null)
                ?.GetGetMethod(nonPublic: false);
            return getter?.Invoke(value, null) as int?;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }
}
