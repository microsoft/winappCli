// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Microsoft.WindowsAppSDK.Analyzers.Rules;

/// <summary>
/// Detects private, parameterless <c>async void</c> methods that are not used
/// as delegates. These are not event handlers, and an exception after an
/// <c>await</c> terminates a WinUI application.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AsyncVoidAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticIds.ParameterlessAsyncVoid,
        "Non-event async method returns void",
        "'{0}' is not an event handler but returns void; return Task and await it (or explicitly discard the Task) so exceptions do not terminate the app",
        DiagnosticCategories.Runtime,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Private parameterless non-event methods should return Task. " +
                     "Exceptions from async void methods are posted to the WinUI synchronization context and can terminate the process.",
        helpLinkUri: HelpLinks.For(DiagnosticIds.ParameterlessAsyncVoid),
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(startContext =>
        {
            var candidates = new ConcurrentDictionary<IMethodSymbol, MethodDeclarationSyntax>(
                SymbolEqualityComparer.Default);
            var delegateMethods = new ConcurrentDictionary<IMethodSymbol, byte>(
                SymbolEqualityComparer.Default);

            startContext.RegisterSyntaxNodeAction(
                syntaxContext => CollectCandidate(syntaxContext, candidates),
                SyntaxKind.MethodDeclaration);
            startContext.RegisterOperationAction(
                operationContext => CollectDelegateMethod(operationContext, delegateMethods),
                OperationKind.DelegateCreation);
            startContext.RegisterCompilationEndAction(
                compilationContext => ReportDiagnostics(
                    compilationContext,
                    candidates,
                    delegateMethods));
        });
    }

    private static void CollectCandidate(
        SyntaxNodeAnalysisContext context,
        ConcurrentDictionary<IMethodSymbol, MethodDeclarationSyntax> candidates)
    {
        var method = (MethodDeclarationSyntax)context.Node;
        if (!method.Modifiers.Any(SyntaxKind.AsyncKeyword) ||
            method.ParameterList.Parameters.Count != 0 ||
            method.ReturnType is not PredefinedTypeSyntax returnType ||
            !returnType.Keyword.IsKind(SyntaxKind.VoidKeyword))
        {
            return;
        }

        var methodSymbol = context.SemanticModel.GetDeclaredSymbol(method, context.CancellationToken);
        if (methodSymbol?.DeclaredAccessibility != Accessibility.Private ||
            methodSymbol.ExplicitInterfaceImplementations.Length != 0)
        {
            return;
        }

        candidates.TryAdd(methodSymbol.OriginalDefinition, method);
    }

    private static void CollectDelegateMethod(
        OperationAnalysisContext context,
        ConcurrentDictionary<IMethodSymbol, byte> delegateMethods)
    {
        var delegateCreation = (IDelegateCreationOperation)context.Operation;
        if (delegateCreation.Target is IMethodReferenceOperation methodReference)
        {
            delegateMethods.TryAdd(methodReference.Method.OriginalDefinition, 0);
        }
    }

    private static void ReportDiagnostics(
        CompilationAnalysisContext context,
        ConcurrentDictionary<IMethodSymbol, MethodDeclarationSyntax> candidates,
        ConcurrentDictionary<IMethodSymbol, byte> delegateMethods)
    {
        foreach (var candidate in candidates)
        {
            if (!delegateMethods.ContainsKey(candidate.Key))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    Rule,
                    candidate.Value.ReturnType.GetLocation(),
                    candidate.Value.Identifier.ValueText));
            }
        }
    }
}
