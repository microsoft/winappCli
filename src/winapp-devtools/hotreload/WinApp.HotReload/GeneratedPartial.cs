// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace WinApp.HotReload;

/// <summary>One XAML file with an x:Class, and what live reload has emitted for it so far.</summary>
internal sealed class XamlComponent(string xamlPath, string uri)
{
    /// <summary>Where the markup is now; it moves with a renamed folder, while <see cref="Uri"/> stays what the class was built with.</summary>
    public string XamlPath { get; set; } = xamlPath;
    public string Uri { get; } = uri;
    public string? ClassName => Latest?.ClassName;
    public XamlTransformResult? Latest { get; set; }
    public bool Pending { get; set; }

    /// <summary>Event handlers the markup names that don't exist yet; the markup is sent again once one does.</summary>
    public int MissingHandlers { get; set; }

    /// <summary>Fields the XAML compiler declared when the app was built. They stay in the running type forever.</summary>
    public HashSet<string> FrozenFields { get; } = new(StringComparer.Ordinal);

    /// <summary>True when the XAML compiler generated InitializeComponent at build time.</summary>
    public bool HasCompiledPart { get; set; }

    /// <summary>Fields live reload has added. Removing a field is a rude edit, so they are never removed.</summary>
    public Dictionary<string, string> EmittedFields { get; } = new(StringComparer.Ordinal);

    public bool ReplayDisabled { get; set; }

    /// <summary>
    /// Top-level statements of the constructor body the replay leaves out (by index): ones only a constructor may run,
    /// such as assigning a get-only property.
    /// </summary>
    public HashSet<int> SkippedStatements { get; } = [];

    /// <summary>The constructor <see cref="SkippedStatements"/> were worked out for: they hold while it is unchanged.</summary>
    public string? SkippedFor { get; set; }

    /// <summary>Once emitted for a new class, 'sealed' stays: unsealing a loaded type is a rude edit.</summary>
    public bool Sealed { get; set; }

    /// <summary>x:Bind function methods emitted so far. They are never removed (deleting a method is a rude edit).</summary>
    public Dictionary<string, XBindFunction> EmittedFunctions { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Static fields added (with an initializer) after the type was loaded: the static constructor won't run again,
    /// so a generated method assigns them once. Field name -> (initializer, is reference type).
    /// </summary>
    public Dictionary<string, (string Initializer, bool IsReference)> StaticInits { get; } = new(StringComparer.Ordinal);

    /// <summary>Functions whose expression doesn't compile yet, with the compiler's reason. They return UnsetValue.</summary>
    public Dictionary<string, string> InvalidFunctions { get; } = new(StringComparer.Ordinal);

    public IEnumerable<string> CurrentNames => Latest?.Names.Select(n => n.Name).Where(n => !n.StartsWith(XamlTransform.GeneratedNamePrefix, StringComparison.Ordinal)) ?? [];

    public IEnumerable<string> StaleNames => FrozenFields.Concat(EmittedFields.Keys).Distinct().Except(CurrentNames).Concat(RemovedByCompiler).Distinct();

    /// <summary>On the compiler pipeline: x:Name fields the XAML compiler no longer generates for this component.</summary>
    public HashSet<string> RemovedByCompiler { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Source for the per-component partial class that stands in for XAML-compiler output: fields for every x:Name,
/// InitializeComponent for classes created during the session, and a replay of the constructor body used to
/// re-initialize live instances in place.
/// </summary>
internal static class GeneratedPartial
{
    public const string ReplayMethod = "__WinAppHotReload_Construct";
    public const string ResetMethod = "__WinAppHotReload_Reset";
    public const string InitPrefix = "__WinAppHotReload_Init_";

    /// <summary>A method that runs a static field's initializer once its type's static constructor already ran.</summary>
    public static string StaticInitializer(string field, string initializer, bool isReference) =>
        $"internal static void {InitPrefix}{field}() {{ " +
        // The static constructor may have run with the new initializer after all (the type wasn't loaded yet).
        (isReference ? $"if ((object){field} is not null) return; " : "") +
        $"global::System.Runtime.CompilerServices.Unsafe.AsRef(in {field}) = {initializer}; }}";

    public static string Create(XamlComponent component, IReadOnlyList<ClassDeclarationSyntax> userParts, bool canReplay, bool markup = true)
    {
        var className = component.ClassName!;
        var dot = className.LastIndexOf('.');
        var ns = dot < 0 ? null : className[..dot];
        var name = className[(dot + 1)..];
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated>winapp run --watch</auto-generated>");
        builder.AppendLine("#pragma warning disable");
        foreach (var directive in userParts.SelectMany(UsingsFor).Distinct())
        {
            builder.AppendLine(directive);
        }
        if (ns is not null)
        {
            builder.Append("namespace ").Append(ns).AppendLine(";");
        }
        if (!component.HasCompiledPart)
        {
            // A class created during the session: match the WinUI item templates ('public sealed partial class')
            // before the code-behind exists, so writing it isn't a visibility or modifier change.
            if (!userParts.Any(part => part.Modifiers.Any(m => m.Kind() is SyntaxKind.PublicKeyword or SyntaxKind.InternalKeyword or SyntaxKind.PrivateKeyword or SyntaxKind.ProtectedKeyword)))
            {
                builder.Append("public ");
            }
            component.Sealed |= userParts.Count == 0;
            if (component.Sealed)
            {
                builder.Append("sealed ");
            }
        }
        builder.Append("partial class ").Append(name);
        if (!component.HasCompiledPart && component.Latest?.RootTypeName is { } rootType)
        {
            builder.Append(" : global::").Append(rootType);
        }
        builder.AppendLine().AppendLine("{");
        // Without markup (the XAML compiler generates the fields, InitializeComponent and x:Bind code), only the replay.
        foreach (var named in markup ? component.Latest?.Names ?? [] : [])
        {
            if (!named.Name.StartsWith(XamlTransform.GeneratedNamePrefix, StringComparison.Ordinal) && !component.FrozenFields.Contains(named.Name))
            {
                component.EmittedFields[named.Name] = named.TypeName ?? "Microsoft.UI.Xaml.DependencyObject";
            }
        }
        foreach (var (field, type) in component.EmittedFields.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            builder.Append("    private global::").Append(type).Append(' ').Append(field).AppendLine(";");
        }
        if (markup && !component.HasCompiledPart)
        {
            builder.AppendLine("    private bool _contentLoaded;");
            builder.AppendLine("    public void InitializeComponent()");
            builder.AppendLine("    {");
            builder.AppendLine("        if (_contentLoaded) return;");
            builder.AppendLine("        _contentLoaded = true;");
            builder.Append("        global::Microsoft.UI.Xaml.Application.LoadComponent(this, new global::System.Uri(\"").Append(component.Uri)
                .AppendLine("\"), global::Microsoft.UI.Xaml.Controls.Primitives.ComponentResourceLocation.Application);");
            builder.AppendLine("    }");
        }
        foreach (var function in markup ? component.Latest?.Functions ?? [] : [])
        {
            component.EmittedFunctions[function.Method] = function;
        }
        foreach (var function in component.EmittedFunctions.Values.OrderBy(f => f.Method, StringComparer.Ordinal))
        {
            builder.Append("    internal static object ").Append(function.Method).AppendLine("(object __source)");
            builder.AppendLine("    {");
            if (component.InvalidFunctions.ContainsKey(function.Method))
            {
                builder.AppendLine("        return global::Microsoft.UI.Xaml.DependencyProperty.UnsetValue;");
            }
            else
            {
                builder.Append("        var __s = (global::").Append(function.SourceType).AppendLine(")__source;");
                builder.Append("        return (object)(").Append(function.Expression).AppendLine(");");
            }
            builder.AppendLine("    }");
            if (function.BindBack is { } back)
            {
                builder.Append("    internal static void ").Append(function.Method).AppendLine("_Back(object __source, object __value)");
                builder.AppendLine("    {");
                if (!component.InvalidFunctions.ContainsKey(function.Method))
                {
                    builder.Append("        var __s = (global::").Append(function.SourceType).AppendLine(")__source;");
                    builder.Append("        ").AppendLine(back);
                }
                builder.AppendLine("    }");
            }
        }
        foreach (var (field, (initializer, isReference)) in component.StaticInits.OrderBy(i => i.Key, StringComparer.Ordinal))
        {
            builder.Append("    ").AppendLine(StaticInitializer(field, initializer, isReference));
        }
        builder.Append("    internal void ").Append(ReplayMethod).AppendLine("()");
        builder.AppendLine("    {");
        var constructor = ReplayedConstructor(userParts);
        // A constructor that takes arguments (MainWindow(object? arg = null), or one a DI container calls) can't get
        // them again: it replays without the statements that use them.
        HashSet<int> skipped = constructor is null ? [.. component.SkippedStatements] : [.. component.SkippedStatements, .. ArgumentStatements(constructor)];
        if (constructor is not null)
        {
            if (canReplay && constructor.Body is { } body && skipped.Count == 0)
            {
                // Everything between the braces: a closing #endif belongs to the brace, not the last statement.
                builder.AppendLine(body.SyntaxTree.GetText().ToString(Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(body.OpenBraceToken.Span.End, body.CloseBraceToken.SpanStart)));
            }
            else if (canReplay && constructor.Body is { } partial)
            {
                // Statement by statement, a left-out one as an empty statement (so the others keep their positions) that
                // keeps its trivia (#if/#endif).
                for (var i = 0; i < partial.Statements.Count; i++)
                {
                    var statement = partial.Statements[i];
                    builder.Append(skipped.Contains(i)
                        ? statement.GetLeadingTrivia().ToFullString() + ";" + statement.GetTrailingTrivia().ToFullString()
                        : statement.ToFullString());
                }
                builder.AppendLine(partial.CloseBraceToken.LeadingTrivia.ToFullString());
            }
            else if (canReplay && constructor.ExpressionBody is { } expression && constructor.ParameterList.Parameters.Count == 0)
            {
                builder.Append("        ").Append(expression.Expression.ToString()).AppendLine(";");
            }
            else if (constructor.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(call => call.Expression.ToString() is "InitializeComponent" or "this.InitializeComponent"))
            {
                builder.AppendLine("        InitializeComponent();");
            }
        }
        builder.AppendLine("    }");
        // Collections the replayed constructor fills (BarItems.Add(...), there or in a method of the class it calls) go back
        // to their initial value first (their initializer, or null), so it builds them as it did at launch instead of adding
        // the same items again. A new object rather than a cleared one: the old one may still be shown by the UI the
        // reload replaces, which a change would reach.
        builder.Append("    internal string[] ").Append(ResetMethod).AppendLine("()");
        builder.AppendLine("    {");
        builder.AppendLine("        var __counts = new global::System.Collections.Generic.List<string>();");
        if (constructor is not null && canReplay && (constructor.Body is not null || constructor.ExpressionBody is not null))
        {
            var parts = userParts.ToList();
            foreach (var filled in FilledCollections(constructor, parts, skipped))
            {
                // How many items it had, so the agent can say when items added while the app ran were dropped.
                builder.Append("        __counts.Add(\"").Append(filled).Append("|\" + (((object)this.").Append(filled)
                    .AppendLine(" as global::System.Collections.ICollection)?.Count ?? 0));");
                builder.Append("        ").AppendLine(ResetStatement(filled, parts));
            }
        }
        builder.AppendLine("        return __counts.ToArray();");
        builder.AppendLine("    }");
        builder.AppendLine("}");
        return builder.ToString();
    }

    /// <summary>A statement that puts a member back to the value it had before the constructor ran.</summary>
    internal static string ResetStatement(string name, IReadOnlyList<ClassDeclarationSyntax> parts)
    {
        foreach (var member in parts.SelectMany(p => p.Members))
        {
            switch (member)
            {
                case FieldDeclarationSyntax field when field.Declaration.Variables.FirstOrDefault(v => v.Identifier.ValueText == name) is { } variable:
                {
                    var value = variable.Initializer?.Value.ToString() ?? "default!";
                    // A readonly field is set as its initializer sets it.
                    return field.Modifiers.Any(SyntaxKind.ReadOnlyKeyword)
                        ? $"global::System.Runtime.CompilerServices.Unsafe.AsRef(in this.{name}) = {value};"
                        : $"this.{name} = {value};";
                }
                case PropertyDeclarationSyntax property when property.Identifier.ValueText == name &&
                    property.AccessorList?.Accessors is { } accessors && accessors.All(a => a.Body is null && a.ExpressionBody is null):
                {
                    // An auto-property: through its setter, or its backing field when it has none.
                    var value = property.Initializer?.Value.ToString() ?? "default!";
                    return accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration))
                        ? $"this.{name} = {value};"
                        : $"{{ {property.Type} __value = {value}; GetType().GetField(\"<{name}>k__BackingField\", global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)?.SetValue(this, __value); }}";
                }
            }
        }
        // A property with a body: what it returns is emptied instead.
        return $"if ((object)this.{name} is global::System.Collections.IList {{ IsFixedSize: false }} __list_{name}) __list_{name}.Clear();";
    }

    private static readonly HashSet<string> AddMethods = new(StringComparer.Ordinal) { "Add", "AddRange", "Insert", "InsertRange" };

    /// <summary>
    /// Instance fields and readable properties of the class whose collection the constructor adds to (statements a replay
    /// leaves out aside), directly or in methods of the class it calls (BarItems.Add in InitializeData()).
    /// </summary>
    public static List<string> FilledCollections(ConstructorDeclarationSyntax constructor, IReadOnlyList<ClassDeclarationSyntax> parts, IReadOnlySet<int> skipped)
    {
        var members = parts.SelectMany(p => p.Members).Where(m => !m.Modifiers.Any(SyntaxKind.StaticKeyword) && !m.Modifiers.Any(SyntaxKind.ConstKeyword))
            .SelectMany(m => m switch
            {
                FieldDeclarationSyntax field => field.Declaration.Variables.Select(v => v.Identifier.ValueText),
                PropertyDeclarationSyntax { ExpressionBody: not null } property => [property.Identifier.ValueText],
                PropertyDeclarationSyntax { AccessorList: { } accessors } property when accessors.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) => [property.Identifier.ValueText],
                _ => [],
            }).ToHashSet(StringComparer.Ordinal);
        var methods = parts.SelectMany(p => p.Members).OfType<MethodDeclarationSyntax>().Where(m => !m.Modifiers.Any(SyntaxKind.StaticKeyword)).ToList();
        var result = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        static string? Name(ExpressionSyntax expression) => expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: IdentifierNameSyntax identifier } => identifier.Identifier.ValueText,
            _ => null,
        };
        void Scan(IEnumerable<SyntaxNode> nodes, int depth)
        {
            foreach (var call in nodes.SelectMany(n => n.DescendantNodesAndSelf()).OfType<InvocationExpressionSyntax>())
            {
                if (call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: var method } access && AddMethods.Contains(method) &&
                    Name(access.Expression) is { } target && members.Contains(target) && !result.Contains(target))
                {
                    result.Add(target);
                }
                else if (depth < 3 && Name(call.Expression) is { } callee && visited.Add(callee))
                {
                    Scan(methods.Where(m => m.Identifier.ValueText == callee).SelectMany(m => (IEnumerable<SyntaxNode>?)m.Body?.Statements ?? (m.ExpressionBody is { } e ? [e] : [])), depth + 1);
                }
            }
        }
        IEnumerable<SyntaxNode> replayed = constructor.Body is { } body
            ? body.Statements.Where((_, i) => !skipped.Contains(i))
            : constructor.ExpressionBody is { } expression ? [expression] : [];
        Scan(replayed, 0);
        return result;
    }

    /// <summary>Top-level statements of the constructor body (by index) that use one of its parameters.</summary>
    public static HashSet<int> ArgumentStatements(ConstructorDeclarationSyntax constructor)
    {
        var parameters = constructor.ParameterList.Parameters.Select(p => p.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);
        var result = new HashSet<int>();
        if (parameters.Count == 0 || constructor.Body is not { } body)
        {
            return result;
        }
        for (var i = 0; i < body.Statements.Count; i++)
        {
            if (body.Statements[i].DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any(id => parameters.Contains(id.Identifier.ValueText)))
            {
                result.Add(i);
            }
        }
        return result;
    }

    /// <summary>The constructor a reload replays: the parameterless one, else the one that loads the markup.</summary>
    public static ConstructorDeclarationSyntax? ReplayedConstructor(IEnumerable<ClassDeclarationSyntax> userParts)
    {
        var constructors = userParts.SelectMany(part => part.Members.OfType<ConstructorDeclarationSyntax>()).Where(ctor => !ctor.Modifiers.Any(SyntaxKind.StaticKeyword)).ToList();
        return constructors.FirstOrDefault(ctor => ctor.ParameterList.Parameters.Count == 0)
            ?? constructors.FirstOrDefault(ctor => ctor.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(call => call.Expression.ToString() is "InitializeComponent" or "this.InitializeComponent"))
            ?? constructors.FirstOrDefault();
    }

    private static IEnumerable<string> UsingsFor(ClassDeclarationSyntax part)
    {
        var unit = part.SyntaxTree.GetCompilationUnitRoot();
        return unit.Usings.Concat(part.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().SelectMany(n => n.Usings))
            .Where(u => u.GlobalKeyword.IsKind(SyntaxKind.None))
            .Select(u => u.ToString());
    }

    public static string? NamespaceOf(TypeDeclarationSyntax declaration)
    {
        var parts = declaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => n.Name.ToString()).ToList();
        return parts.Count == 0 ? null : string.Join('.', parts);
    }
}

/// <summary>Detects edits that need live instances to be rebuilt: anything beyond a method body change.</summary>
internal static class StructuralChange
{
    public static Dictionary<string, string> Signatures(SyntaxNode? root)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root is null)
        {
            return result;
        }
        foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            var name = string.Join('.', type.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().Reverse().Select(t => t.Identifier.ValueText));
            var ns = type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => n.Name.ToString()).ToList();
            if (ns.Count > 0)
            {
                name = string.Join('.', ns) + "." + name;
            }
            var stripped = new BodyStripper().Visit(type)!;
            var signature = string.Concat(stripped.DescendantTokens().Select(t => t.Text));
            result[name] = result.TryGetValue(name, out var existing) ? existing + "|" + signature : signature;
        }
        return result;
    }

    /// <summary>Per "Type|Property": the getter's body, for properties with one (computed properties).</summary>
    public static Dictionary<string, string> Getters(SyntaxNode? root)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in root?.DescendantNodes().OfType<PropertyDeclarationSyntax>() ?? [])
        {
            var getter = property.AccessorList?.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
            var body = property.ExpressionBody?.Expression.ToString() ?? getter?.Body?.ToString() ?? getter?.ExpressionBody?.Expression.ToString();
            if (body is not null && property.Parent is TypeDeclarationSyntax type)
            {
                result[TypeName(type) + "|" + property.Identifier.ValueText] = body;
            }
        }
        return result;
    }

    /// <summary>"Type|Method" bodies of methods, with whether their type is a value converter.</summary>
    public static Dictionary<string, (string Body, bool Converter)> Methods(SyntaxNode? root)
    {
        var result = new Dictionary<string, (string, bool)>(StringComparer.Ordinal);
        foreach (var method in root?.DescendantNodes().OfType<MethodDeclarationSyntax>() ?? [])
        {
            if (method.Parent is TypeDeclarationSyntax type)
            {
                var converter = type.BaseList?.Types.Any(t => t.Type.ToString().EndsWith("IValueConverter", StringComparison.Ordinal)) == true;
                result[TypeName(type) + "|" + method.Identifier.ValueText] = ((method.Body?.ToString() ?? method.ExpressionBody?.Expression.ToString() ?? "") + method.ParameterList, converter);
            }
        }
        return result;
    }

    /// <summary>Per type: its instance constructors and initialized instance fields and properties.</summary>
    public static Dictionary<string, string> Construction(SyntaxNode? root) => Collect(root, includeConstructors: true);

    public static bool InitializersChanged(SyntaxNode? before, SyntaxNode? after, string type) =>
        Collect(before, includeConstructors: false).GetValueOrDefault(type) != Collect(after, includeConstructors: false).GetValueOrDefault(type);

    private static Dictionary<string, string> Collect(SyntaxNode? root, bool includeConstructors)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var type in root?.DescendantNodes().OfType<TypeDeclarationSyntax>() ?? [])
        {
            var parts = type.Members.Where(member => !member.Modifiers.Any(SyntaxKind.StaticKeyword) && member switch
            {
                ConstructorDeclarationSyntax => includeConstructors,
                FieldDeclarationSyntax field => field.Declaration.Variables.Any(v => v.Initializer is not null),
                PropertyDeclarationSyntax property => property.Initializer is not null,
                _ => false,
            }).Select(member => member.WithoutTrivia().ToString());
            var name = TypeName(type);
            result[name] = result.TryGetValue(name, out var existing) ? existing + "|" + string.Join("|", parts) : string.Join("|", parts);
        }
        return result;
    }

    private static string TypeName(TypeDeclarationSyntax type)
    {
        var name = string.Join('.', type.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().Reverse().Select(t => t.Identifier.ValueText));
        var ns = type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => n.Name.ToString()).ToList();
        return ns.Count > 0 ? string.Join('.', ns) + "." + name : name;
    }

    public static HashSet<(string TypeMetadataName, string Member)> ExplicitImplementations(SyntaxNode? root)
    {
        var result = new HashSet<(string, string)>();
        foreach (var member in root?.DescendantNodes().OfType<MemberDeclarationSyntax>() ?? [])
        {
            var specifier = member switch
            {
                MethodDeclarationSyntax m => m.ExplicitInterfaceSpecifier,
                BasePropertyDeclarationSyntax p => p.ExplicitInterfaceSpecifier,
                _ => null,
            };
            if (specifier is null || member.Parent is not TypeDeclarationSyntax type)
            {
                continue;
            }
            var ns = string.Join('.', type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => n.Name.ToString()));
            var path = string.Join('+', type.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().Reverse()
                .Select(t => t is TypeDeclarationSyntax { TypeParameterList: { } list } ? $"{t.Identifier.ValueText}`{list.Parameters.Count}" : t.Identifier.ValueText));
            var signature = new BodyStripper().Visit(member)!;
            result.Add((ns.Length == 0 ? path : ns + "." + path, string.Concat(signature.DescendantTokens().Select(t => t.Text))));
        }
        return result;
    }

    private sealed class BodyStripper : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) => node.WithBody(null).WithExpressionBody(null);
        public override SyntaxNode? VisitAccessorDeclaration(AccessorDeclarationSyntax node) => node.WithBody(null).WithExpressionBody(null);
        public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node) => base.VisitPropertyDeclaration(node.WithExpressionBody(null));
        public override SyntaxNode? VisitOperatorDeclaration(OperatorDeclarationSyntax node) => node.WithBody(null).WithExpressionBody(null);
    }
}
