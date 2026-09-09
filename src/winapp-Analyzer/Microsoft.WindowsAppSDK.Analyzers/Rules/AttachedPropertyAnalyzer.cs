// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Microsoft.WindowsAppSDK.Analyzers.Rules;

/// <summary>
/// <see cref="DiagnosticIds.AttachedPropertyInitializer"/> — object-initializer
/// syntax used for WinUI attached properties in code-behind. Doesn't compile.
/// Must use static methods, e.g. <c>AutomationProperties.SetAutomationId(btn, "...")</c>.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AttachedPropertyAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticIds.AttachedPropertyInitializer,
        "Attached property object initializer",
        "'{0}' is an attached property — cannot use object initializer syntax. Use {0}.Set{1}(element, value) instead",
        DiagnosticCategories.Runtime,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "WinUI attached properties (AutomationProperties, Grid, Canvas, ToolTipService, etc.) " +
                     "must be set via static methods, not object initializer syntax.",
        helpLinkUri: HelpLinks.For(DiagnosticIds.AttachedPropertyInitializer));

    private static readonly HashSet<string> AttachedPropertyTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AutomationProperties",
        "ToolTipService",
        "ScrollViewer",
        "Canvas",
        "Grid",
        "RelativePanel",
        "VariableSizedWrapGrid"
    };

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeObjectInitializer, SyntaxKind.SimpleAssignmentExpression);
    }

    private static void AnalyzeObjectInitializer(SyntaxNodeAnalysisContext context)
    {
        var assignment = (AssignmentExpressionSyntax)context.Node;
        if (assignment.Parent is not InitializerExpressionSyntax) return;

        var leftName = assignment.Left.ToString();
        if (!AttachedPropertyTypes.Contains(leftName)) return;

        if (assignment.Right is InitializerExpressionSyntax or
            ObjectCreationExpressionSyntax { Initializer: not null })
        {
            var suggestedProp = "AutomationId";
            if (assignment.Right is InitializerExpressionSyntax nested)
            {
                var firstAssign = nested.Expressions.OfType<AssignmentExpressionSyntax>().FirstOrDefault();
                if (firstAssign != null) suggestedProp = firstAssign.Left.ToString();
            }

            context.ReportDiagnostic(Diagnostic.Create(Rule, assignment.GetLocation(), leftName, suggestedProp));
        }
    }
}
