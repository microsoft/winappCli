// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Microsoft.WindowsAppSDK.Analyzers.Rules;

/// <summary>
/// <see cref="DiagnosticIds.TabViewRawContent"/> — raw control assigned as
/// <c>TabViewItem.Content</c>. <c>TabView</c> does not stretch child controls
/// vertically; the recommended pattern is <c>Frame.Navigate(typeof(Page))</c>.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TabViewContentAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticIds.TabViewRawContent,
        "Raw control as TabView content",
        "TabView does not stretch child controls vertically — '{0}' will render at ~50px height. Use Frame.Navigate(typeof(Page)) per tab.",
        DiagnosticCategories.Runtime,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "TabViewItem.Content should use a Frame navigating to Page types, not raw controls like TextBox or Grid. " +
                     "TabView's internal ContentPresenter uses StackPanel-like sizing that doesn't propagate stretch alignment.",
        helpLinkUri: HelpLinks.For(DiagnosticIds.TabViewRawContent));

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    private static readonly string[] ProblematicControls =
    {
        "TextBox", "RichEditBox", "WebView2", "Grid", "StackPanel",
        "ScrollViewer", "ListView", "TreeView", "Border"
    };

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeAssignment, SyntaxKind.SimpleAssignmentExpression);
    }

    private static void AnalyzeAssignment(SyntaxNodeAnalysisContext context)
    {
        var assignment = (AssignmentExpressionSyntax)context.Node;

        if (assignment.Left is not MemberAccessExpressionSyntax memberAccess) return;
        if (memberAccess.Name.Identifier.Text != "Content") return;

        if (assignment.Right is not ObjectCreationExpressionSyntax creation) return;
        var createdTypeName = GetTypeName(creation.Type);
        if (createdTypeName == null) return;

        if (!ProblematicControls.Any(c => createdTypeName.Contains(c))) return;

        var leftSymbol = context.SemanticModel.GetTypeInfo(memberAccess.Expression).Type;
        if (leftSymbol == null)
        {
            // Can't resolve type — fall back to name heuristic.
            var varName = memberAccess.Expression.ToString().ToLowerInvariant();
            if (!varName.Contains("tab")) return;
        }
        else
        {
            if (!IsTabViewItemType(leftSymbol)) return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, assignment.GetLocation(), createdTypeName));
    }

    private static bool IsTabViewItemType(ITypeSymbol type)
    {
        var current = type;
        while (current != null)
        {
            if (current.ToDisplayString().Contains("TabViewItem")) return true;
            current = current.BaseType;
        }
        return false;
    }

    private static string? GetTypeName(TypeSyntax type) => type switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        QualifiedNameSyntax qn => qn.Right.Identifier.Text,
        _ => type.ToString()
    };
}
