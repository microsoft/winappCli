// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;

namespace WinApp.DevTools.Managed;

/// <summary>
/// What DevTools treats as a secret, the same rule as the native engine and the CLI: a property (or a Reactor prop,
/// hook or context) whose name ends in "Password". Its value is shown as <see cref="Redacted"/> and never written.
/// A value whose type holds such a member (<see cref="HoldsSecret(Type?)"/>) is treated the same way, because its
/// text can print the password: a record's <c>ToString</c> prints every public member.
/// </summary>
internal static class DevToolsSecrets
{
    internal const string Redacted = "<redacted>";

    // A record prints the records it holds, and they print theirs; deeper than this is not followed.
    private const int MaxDepth = 8;
    private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

    private static readonly ConcurrentDictionary<Type, bool> Types = new();
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "bool", "byte", "sbyte", "char", "short", "ushort", "int", "uint", "long", "ulong", "float", "double", "decimal",
        "string", "object", "nint", "nuint",
    };
    private static readonly object IndexLock = new();
    private static readonly Dictionary<string, List<Type>> TypesByName = new(StringComparer.Ordinal);
    private static readonly HashSet<Assembly> Indexed = [];

    // A member whose name ends in Password inside a record's printed braces ("Login { User = ada, Password = hunter2 }"),
    // anywhere in the text: a tuple, a KeyValuePair or a type's own ToString can print a record inside its own text.
    private static readonly Regex SecretMember =
        new(@"\{\s[^{}]*?\w*password\s=\s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static bool IsSecretName(string? name) =>
        name is not null && name.EndsWith("Password", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a value's text, printed the way a record prints itself, shows a member whose name ends in "Password".
    /// This catches what <see cref="HoldsSecret(Type?)"/> can't see: a derived record, or one held as <c>object</c>,
    /// prints the members of its runtime type, and a tuple or wrapper prints the record it holds. A string's own text
    /// (a string value, or Reactor's quoted text of one) is never scanned.
    /// </summary>
    internal static bool PrintsSecret(object? value, string? text) =>
        value is not string && text is not null && !text.StartsWith('"') && SecretMember.IsMatch(text);

    /// <summary>Forgets what was learned about types, after .NET Hot Reload changed them.</summary>
    internal static void ClearCache()
    {
        Types.Clear();
        lock (IndexLock)
        {
            TypesByName.Clear();
            Indexed.Clear();
        }
    }

    /// <summary>
    /// Whether a value of <paramref name="type"/> can print a password: the type, an item or type argument of it, or
    /// a record (any type with its own <c>ToString</c>) it holds in a public field or property, has a public field or
    /// property whose name ends in "Password". <c>Login { User = ada, Password = hunter2 }</c> and a
    /// <c>List&lt;Login&gt;</c> both do. Cached per type.
    /// </summary>
    internal static bool HoldsSecret(Type? type) =>
        type is not null && Types.GetOrAdd(type, static t => Scan(t, 0, []));

    /// <summary>
    /// <see cref="HoldsSecret(Type?)"/> for a type Reactor names as text (<c>Login</c>, <c>List&lt;Login&gt;</c>,
    /// <c>Login?</c>): true when any loaded type of a name it mentions holds a secret.
    /// </summary>
    internal static bool HoldsSecret(string? typeName)
    {
        if (string.IsNullOrEmpty(typeName))
        {
            return false;
        }
        foreach (var name in Regex.Split(typeName, @"\W+"))
        {
            if (name.Length > 0 && !Keywords.Contains(name) && TypesNamed(name).Any(t => HoldsSecret(t)))
            {
                return true;
            }
        }
        return false;
    }

    private static bool Scan(Type type, int depth, HashSet<Type> seen)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (depth > MaxDepth || type.IsPrimitive || type.IsEnum || type.IsPointer || type.IsByRef || type == typeof(string) ||
            type == typeof(object) || typeof(Delegate).IsAssignableFrom(type) || !seen.Add(type))
        {
            return false;
        }
        if (type.HasElementType)
        {
            return type.GetElementType() is { } element && Scan(element, depth + 1, seen);
        }
        try
        {
            if (type.IsGenericType && type.GetGenericArguments().Any(argument => Scan(argument, depth + 1, seen)))
            {
                return true;
            }
            var members = type.GetProperties(PublicInstance).Where(p => p.GetIndexParameters().Length == 0)
                .Select(p => (p.Name, Type: p.PropertyType))
                .Concat(type.GetFields(PublicInstance).Select(f => (f.Name, Type: f.FieldType)))
                .ToList();
            return members.Any(member => IsSecretName(member.Name)) ||
                members.Any(member => PrintsItself(member.Type) && Scan(member.Type, depth + 1, seen));
        }
        catch (Exception ex) when (ex is TypeLoadException or FileNotFoundException or FileLoadException or NotSupportedException)
        {
            return false;
        }
    }

    // A type whose ToString is its own (a record, a tuple, an app's override) can print its members; object's prints
    // only the type name. Collections are followed through their type arguments.
    private static bool PrintsItself(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.HasElementType || type.IsGenericType)
        {
            return true;
        }
        var declaring = type.GetMethod(nameof(ToString), PublicInstance, Type.EmptyTypes)?.DeclaringType;
        return declaring is not null && declaring != typeof(object) && declaring != typeof(ValueType);
    }

    /// <summary>
    /// Whether an assembly is part of .NET itself, by name. Not by folder: a self-contained app's own assemblies sit in
    /// the runtime directory too.
    /// </summary>
    internal static bool IsDotNetAssembly(string? name) =>
        name is "mscorlib" or "netstandard" or "System" || (name?.StartsWith("System.", StringComparison.Ordinal) ?? false);

    // Every loaded type with this name (generic arity dropped), from assemblies that are not part of .NET itself.
    private static List<Type> TypesNamed(string name)
    {
        lock (IndexLock)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!Indexed.Add(assembly) || assembly.IsDynamic || IsDotNetAssembly(assembly.GetName().Name))
                {
                    continue;
                }
                Type?[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                foreach (var type in types)
                {
                    if (type is null)
                    {
                        continue;
                    }
                    var key = ReactorApi.TypeName(type);
                    if (!TypesByName.TryGetValue(key, out var list))
                    {
                        TypesByName[key] = list = [];
                    }
                    list.Add(type);
                }
            }
            return TypesByName.TryGetValue(name, out var found) ? [.. found] : [];
        }
    }
}
