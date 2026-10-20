// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Microsoft.WindowsAppSDK.Analyzers.Rules;

/// <summary>
/// Detects custom range-virtualized collections that replace the cache used by
/// <c>RangesChanged</c> before raising a reset without repopulating that cache.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class VirtualizedCollectionResetAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticIds.VirtualizedResetDropsCache,
        "Virtualized reset does not repopulate the rebuilt cache",
        "'{0}' replaces range cache '{1}' before Reset, but WinUI 3 may not call RangesChanged again when Count is unchanged; retain the tracked ranges and call {1}.UpdateRanges(...) after raising Reset",
        DiagnosticCategories.Runtime,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A custom IItemsRangeInfo source must repopulate a replacement range cache after a collection reset. " +
                     "WinUI 3 can retain the same visible range without invoking RangesChanged again.",
        helpLinkUri: HelpLinks.For(DiagnosticIds.VirtualizedResetDropsCache));

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeMethod, SyntaxKind.MethodDeclaration);
    }

    private static void AnalyzeMethod(SyntaxNodeAnalysisContext context)
    {
        var method = (MethodDeclarationSyntax)context.Node;
        var descendants = method.DescendantNodes(node =>
            node is not AnonymousFunctionExpressionSyntax and
            not LocalFunctionStatementSyntax);
        var resetAccesses = descendants
            .OfType<MemberAccessExpressionSyntax>()
            .Where(node => IsCollectionResetArgument(context, node))
            .ToArray();
        if (resetAccesses.Length == 0)
        {
            return;
        }

        var methodSymbol = context.SemanticModel.GetDeclaredSymbol(method, context.CancellationToken);
        var containingType = methodSymbol?.ContainingType;
        if (containingType is null || !ImplementsItemsRangeInfo(containingType))
        {
            return;
        }

        var rangeState = GetRangesChangedState(context, containingType);
        if (rangeState.CacheFields.Count == 0)
        {
            return;
        }

        foreach (var assignment in method.DescendantNodes(node =>
                     node is not AnonymousFunctionExpressionSyntax and
                     not LocalFunctionStatementSyntax)
                 .OfType<AssignmentExpressionSyntax>())
        {
            if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) ||
                !CreatesNewInstance(assignment.Right) ||
                context.SemanticModel.GetSymbolInfo(assignment.Left, context.CancellationToken).Symbol is not IFieldSymbol field ||
                !rangeState.CacheFields.Contains(field, SymbolEqualityComparer.Default))
            {
                continue;
            }

            var resetAccess = resetAccesses.FirstOrDefault(reset =>
                IsLaterInSameBlock(assignment, reset));
            if (resetAccess is null ||
                ReplaysRangesAfterReset(
                    context,
                    method,
                    resetAccess,
                    field,
                    rangeState.RetainedRangeMembers))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                Rule,
                resetAccess.GetLocation(),
                methodSymbol!.Name,
                field.Name));
        }
    }

    private static bool IsCollectionResetArgument(
        SyntaxNodeAnalysisContext context,
        MemberAccessExpressionSyntax memberAccess)
    {
        if (memberAccess.Name.Identifier.ValueText != "Reset")
        {
            return false;
        }

        var symbol = context.SemanticModel.GetSymbolInfo(memberAccess, context.CancellationToken).Symbol;
        if (symbol?.ContainingType?.ToDisplayString() !=
            "System.Collections.Specialized.NotifyCollectionChangedAction")
        {
            return false;
        }

        var creation = memberAccess.Ancestors().OfType<BaseObjectCreationExpressionSyntax>().FirstOrDefault();
        if (creation is null ||
            context.SemanticModel.GetTypeInfo(creation, context.CancellationToken)
                .Type?.ToDisplayString() !=
            "System.Collections.Specialized.NotifyCollectionChangedEventArgs")
        {
            return false;
        }

        var invocation = creation.Ancestors().OfType<InvocationExpressionSyntax>().FirstOrDefault(
            candidate => candidate.ArgumentList.Span.Contains(creation.Span));
        var invokedMethod = invocation is null
            ? null
            : context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken)
                .Symbol as IMethodSymbol;
        return invokedMethod?.MethodKind == MethodKind.DelegateInvoke &&
               invokedMethod.ContainingType.ToDisplayString() ==
               "System.Collections.Specialized.NotifyCollectionChangedEventHandler";
    }

    private static bool ImplementsItemsRangeInfo(INamedTypeSymbol type) =>
        type.AllInterfaces.Any(item => item.ToDisplayString() is
            "Microsoft.UI.Xaml.Data.IItemsRangeInfo" or
            "Windows.UI.Xaml.Data.IItemsRangeInfo");

    private static (HashSet<IFieldSymbol> CacheFields, HashSet<ISymbol> RetainedRangeMembers)
        GetRangesChangedState(
        SyntaxNodeAnalysisContext context,
        INamedTypeSymbol containingType)
    {
        var cacheFields = new HashSet<IFieldSymbol>(SymbolEqualityComparer.Default);
        var retainedRangeMembers = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var rangeInterface = containingType.AllInterfaces.First(item => item.ToDisplayString() is
            "Microsoft.UI.Xaml.Data.IItemsRangeInfo" or
            "Windows.UI.Xaml.Data.IItemsRangeInfo");
        var methods = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var interfaceMethod in rangeInterface.GetMembers("RangesChanged"))
        {
            if (containingType.FindImplementationForInterfaceMember(interfaceMethod) is
                IMethodSymbol implementation)
            {
                methods.Add(implementation);
            }
        }

        foreach (var method in containingType.GetMembers("RangesChanged").OfType<IMethodSymbol>())
        {
            methods.Add(method);
        }

        foreach (var method in methods)
        {
            foreach (var syntaxReference in method.DeclaringSyntaxReferences)
            {
                var syntax = syntaxReference.GetSyntax(context.CancellationToken);
                var semanticModel = context.Compilation.GetSemanticModel(syntax.SyntaxTree);
                foreach (var invocation in syntax.DescendantNodes(node =>
                             node is not AnonymousFunctionExpressionSyntax and
                             not LocalFunctionStatementSyntax)
                         .OfType<InvocationExpressionSyntax>())
                {
                    if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess ||
                        memberAccess.Name.Identifier.ValueText != "UpdateRanges")
                    {
                        continue;
                    }

                    if (semanticModel.GetSymbolInfo(
                            memberAccess.Expression,
                            context.CancellationToken).Symbol is IFieldSymbol field)
                    {
                        cacheFields.Add(field);
                    }
                }

                foreach (var assignment in syntax.DescendantNodes(node =>
                             node is not AnonymousFunctionExpressionSyntax and
                             not LocalFunctionStatementSyntax)
                         .OfType<AssignmentExpressionSyntax>()
                         .Where(node => node.IsKind(SyntaxKind.SimpleAssignmentExpression)))
                {
                    var retainedMember = semanticModel.GetSymbolInfo(
                        assignment.Left,
                        context.CancellationToken).Symbol;
                    if (retainedMember is IFieldSymbol or IPropertySymbol &&
                        assignment.Right.DescendantNodesAndSelf().Any(node =>
                            semanticModel.GetSymbolInfo(node, context.CancellationToken).Symbol is
                                IParameterSymbol parameter &&
                            SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol, method)))
                    {
                        retainedRangeMembers.Add(retainedMember);
                    }
                }
            }
        }

        return (cacheFields, retainedRangeMembers);
    }

    private static bool CreatesNewInstance(ExpressionSyntax expression) =>
        expression is ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax;

    private static bool ReplaysRangesAfterReset(
        SyntaxNodeAnalysisContext context,
        MethodDeclarationSyntax method,
        MemberAccessExpressionSyntax resetAccess,
        IFieldSymbol field,
        HashSet<ISymbol> retainedRangeMembers)
    {
        foreach (var invocation in method.DescendantNodes(node =>
                         node is not AnonymousFunctionExpressionSyntax and
                         not LocalFunctionStatementSyntax)
                     .OfType<InvocationExpressionSyntax>()
                         .Where(node => IsGuaranteedLaterInSameBlock(resetAccess, node)))
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess ||
                memberAccess.Name.Identifier.ValueText != "UpdateRanges")
            {
                continue;
            }

            var receiver = context.SemanticModel
                .GetSymbolInfo(memberAccess.Expression, context.CancellationToken)
                .Symbol;
            if (SymbolEqualityComparer.Default.Equals(receiver, field) &&
                invocation.ArgumentList.Arguments.Count > 0 &&
                invocation.ArgumentList.Arguments[0].Expression.DescendantNodesAndSelf().Any(node =>
                    context.SemanticModel.GetSymbolInfo(node, context.CancellationToken).Symbol is
                        { } argumentMember &&
                    retainedRangeMembers.Contains(argumentMember, SymbolEqualityComparer.Default)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLaterInSameBlock(
        SyntaxNode earlier,
        SyntaxNode later)
    {
        var earlierStatement = earlier.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault();
        var laterStatement = later.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault();
        if (earlierStatement is null ||
            laterStatement is null ||
            earlierStatement.Parent is not BlockSyntax block ||
            laterStatement.Parent != block)
        {
            return false;
        }

        return block.Statements.IndexOf(laterStatement) >
               block.Statements.IndexOf(earlierStatement);
    }

    private static bool IsGuaranteedLaterInSameBlock(
        SyntaxNode earlier,
        SyntaxNode later)
    {
        if (!IsLaterInSameBlock(earlier, later))
        {
            return false;
        }

        var earlierStatement = earlier.AncestorsAndSelf().OfType<StatementSyntax>().First();
        var laterStatement = later.AncestorsAndSelf().OfType<StatementSyntax>().First();
        var block = (BlockSyntax)earlierStatement.Parent!;
        var earlierIndex = block.Statements.IndexOf(earlierStatement);
        var laterIndex = block.Statements.IndexOf(laterStatement);
        return !block.Statements
            .Skip(earlierIndex + 1)
            .Take(laterIndex - earlierIndex - 1)
            .Any(ContainsPotentialExit);
    }

    private static bool ContainsPotentialExit(StatementSyntax statement) =>
        statement.DescendantNodesAndSelf(node =>
                node is not AnonymousFunctionExpressionSyntax and
                not LocalFunctionStatementSyntax)
            .Any(node => node is
                ReturnStatementSyntax or
                ThrowStatementSyntax or
                ThrowExpressionSyntax or
                GotoStatementSyntax);
}
