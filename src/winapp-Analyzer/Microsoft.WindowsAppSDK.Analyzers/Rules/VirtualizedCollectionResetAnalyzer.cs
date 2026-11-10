// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

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
        "'{0}' replaces range cache '{1}' before Reset, but WinUI 3 may not call RangesChanged again when Count is unchanged; retain the visible and tracked ranges and call {1}.UpdateRanges(...) after raising Reset",
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
        var methodSymbol = context.SemanticModel.GetDeclaredSymbol(method, context.CancellationToken);
        var containingType = methodSymbol?.ContainingType;
        if (containingType is null || !ImplementsItemsRangeInfo(containingType))
        {
            return;
        }

        var descendants = method.DescendantNodes(node =>
            node is not AnonymousFunctionExpressionSyntax and
            not LocalFunctionStatementSyntax);
        var resetAccesses = descendants
            .OfType<InvocationExpressionSyntax>()
            .Select(node => GetRaisedResetArgument(context, node, containingType))
            .Where(node => node is not null)
            .Cast<ExpressionSyntax>()
            .ToArray();
        if (resetAccesses.Length == 0)
        {
            return;
        }

        var rangeState = GetRangesChangedState(context, containingType);
        if (rangeState.CacheContracts.Count == 0)
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
                !TryGetCurrentInstanceMember(
                    context.SemanticModel,
                    assignment.Left,
                    context.CancellationToken,
                    out var assignedMember) ||
                !rangeState.CacheContracts.ContainsKey(assignedMember))
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
                    assignedMember,
                    rangeState))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                Rule,
                resetAccess.GetLocation(),
                methodSymbol!.Name,
                assignedMember.Name));
        }
    }

    private static ExpressionSyntax? GetRaisedResetArgument(
        SyntaxNodeAnalysisContext context,
        InvocationExpressionSyntax invocation,
        INamedTypeSymbol containingType)
    {
        if (context.SemanticModel.GetOperation(
                invocation,
                context.CancellationToken) is not IInvocationOperation invocationOperation)
        {
            return null;
        }

        var invokedMethod = invocationOperation.TargetMethod;
        if (invokedMethod?.MethodKind == MethodKind.DelegateInvoke &&
            invokedMethod.ContainingType.ToDisplayString() ==
                "System.Collections.Specialized.NotifyCollectionChangedEventHandler" &&
            IsCurrentInstanceDelegateInvocation(
                context.SemanticModel,
                invocation,
                context.CancellationToken))
        {
            return GetRaisedResetExpression(context, invocation, invocationOperation);
        }

        return invokedMethod is
        {
            Name: "OnCollectionChanged",
            MethodKind: MethodKind.Ordinary,
            ReturnsVoid: true,
            Parameters.Length: 1,
        } &&
            invokedMethod.Parameters[0].Type.ToDisplayString() ==
                "System.Collections.Specialized.NotifyCollectionChangedEventArgs" &&
        IsDeclaredOnTypeOrBase(containingType, invokedMethod.ContainingType) &&
        IsCurrentInstance(invocationOperation.Instance)
            ? GetRaisedResetExpression(context, invocation, invocationOperation)
            : null;
    }

    private static bool IsCurrentInstanceDelegateInvocation(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        System.Threading.CancellationToken cancellationToken)
    {
        ExpressionSyntax delegateExpression = invocation.Expression switch
        {
            MemberBindingExpressionSyntax
                when invocation.Parent is ConditionalAccessExpressionSyntax conditionalAccess =>
                conditionalAccess.Expression,
            MemberAccessExpressionSyntax memberAccess
                when memberAccess.Name.Identifier.ValueText == "Invoke" =>
                memberAccess.Expression,
            _ => invocation.Expression,
        };

        return TryGetCurrentInstanceMember(
            semanticModel,
            delegateExpression,
            cancellationToken,
            out _);
    }

    private static ExpressionSyntax? GetRaisedResetExpression(
        SyntaxNodeAnalysisContext context,
        InvocationExpressionSyntax invocation,
        IInvocationOperation invocationOperation)
    {
        var eventArgsArgument = invocationOperation.Arguments.FirstOrDefault(argument =>
            argument.Parameter?.Type.ToDisplayString() ==
                "System.Collections.Specialized.NotifyCollectionChangedEventArgs");
        if (eventArgsArgument?.Syntax is not ArgumentSyntax argument ||
            !IsResetEventArgsValue(context, argument.Expression, invocation))
        {
            return null;
        }

        return argument.Expression;
    }

    private static bool IsResetEventArgsValue(
        SyntaxNodeAnalysisContext context,
        ExpressionSyntax expression,
        InvocationExpressionSyntax invocation)
    {
        if (expression is BaseObjectCreationExpressionSyntax creation)
        {
            return IsResetEventArgsCreation(context, creation);
        }

        if (context.SemanticModel.GetOperation(
                expression,
                context.CancellationToken) is not ILocalReferenceOperation localReference ||
            invocation.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault() is not
                { Parent: BlockSyntax block } invocationStatement)
        {
            return false;
        }

        var invocationIndex = block.Statements.IndexOf(invocationStatement);
        for (var index = invocationIndex - 1; index >= 0; index--)
        {
            var statement = block.Statements[index];
            if (!ContainsWriteToLocal(
                    context.SemanticModel,
                    statement,
                    localReference.Local,
                    context.CancellationToken))
            {
                continue;
            }

            ExpressionSyntax? assignedValue = statement switch
            {
                LocalDeclarationStatementSyntax declaration =>
                    declaration.Declaration.Variables
                        .FirstOrDefault(variable => SymbolEqualityComparer.Default.Equals(
                            context.SemanticModel.GetDeclaredSymbol(
                                variable,
                                context.CancellationToken),
                            localReference.Local))
                        ?.Initializer?.Value,
                ExpressionStatementSyntax
                {
                    Expression: AssignmentExpressionSyntax assignment,
                } when assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) &&
                       IsLocalReference(
                           context.SemanticModel,
                           assignment.Left,
                           localReference.Local,
                           context.CancellationToken) =>
                    assignment.Right,
                _ => null,
            };

            return assignedValue is BaseObjectCreationExpressionSyntax assignedCreation &&
                   IsResetEventArgsCreation(context, assignedCreation);
        }

        return false;
    }

    private static bool IsResetEventArgsCreation(
        SyntaxNodeAnalysisContext context,
        BaseObjectCreationExpressionSyntax creation)
    {
        if (context.SemanticModel.GetTypeInfo(creation, context.CancellationToken)
                .Type?.ToDisplayString() !=
            "System.Collections.Specialized.NotifyCollectionChangedEventArgs")
        {
            return false;
        }

        return creation.ArgumentList?.Arguments
            .Select(argument => argument.Expression)
            .Any(expression =>
                context.SemanticModel.GetSymbolInfo(expression, context.CancellationToken).Symbol is
                    IFieldSymbol
                    {
                        Name: "Reset",
                        ContainingType: { } actionType,
                    } &&
                actionType.ToDisplayString() ==
                    "System.Collections.Specialized.NotifyCollectionChangedAction") == true;
    }

    private static bool ContainsWriteToLocal(
        SemanticModel semanticModel,
        StatementSyntax statement,
        ILocalSymbol local,
        System.Threading.CancellationToken cancellationToken) =>
        statement.DescendantNodesAndSelf(node =>
                node is not AnonymousFunctionExpressionSyntax and
                not LocalFunctionStatementSyntax)
            .Any(node => node switch
            {
                VariableDeclaratorSyntax variable => SymbolEqualityComparer.Default.Equals(
                    semanticModel.GetDeclaredSymbol(variable, cancellationToken),
                    local),
                AssignmentExpressionSyntax assignment =>
                    IsLocalReference(
                        semanticModel,
                        assignment.Left,
                        local,
                        cancellationToken),
                PrefixUnaryExpressionSyntax prefix
                    when prefix.IsKind(SyntaxKind.PreIncrementExpression) ||
                         prefix.IsKind(SyntaxKind.PreDecrementExpression) =>
                    IsLocalReference(
                        semanticModel,
                        prefix.Operand,
                        local,
                        cancellationToken),
                PostfixUnaryExpressionSyntax postfix
                    when postfix.IsKind(SyntaxKind.PostIncrementExpression) ||
                         postfix.IsKind(SyntaxKind.PostDecrementExpression) =>
                    IsLocalReference(
                        semanticModel,
                        postfix.Operand,
                        local,
                        cancellationToken),
                ArgumentSyntax argument
                    when !argument.RefKindKeyword.IsKind(SyntaxKind.None) =>
                    IsLocalReference(
                        semanticModel,
                        argument.Expression,
                        local,
                        cancellationToken),
                _ => false,
            });

    private static bool IsLocalReference(
        SemanticModel semanticModel,
        ExpressionSyntax expression,
        ILocalSymbol local,
        System.Threading.CancellationToken cancellationToken) =>
        semanticModel.GetOperation(expression, cancellationToken) is
            ILocalReferenceOperation localReference &&
        SymbolEqualityComparer.Default.Equals(localReference.Local, local);

    private static bool ImplementsItemsRangeInfo(INamedTypeSymbol type) =>
        type.AllInterfaces.Any(item => item.ToDisplayString() is
            "Microsoft.UI.Xaml.Data.IItemsRangeInfo" or
            "Windows.UI.Xaml.Data.IItemsRangeInfo");

    private static bool IsDeclaredOnTypeOrBase(
        INamedTypeSymbol containingType,
        INamedTypeSymbol declaringType)
    {
        for (var type = containingType; type is not null; type = type.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(type, declaringType))
            {
                return true;
            }
        }

        return false;
    }

    private static RangesChangedState GetRangesChangedState(
        SyntaxNodeAnalysisContext context,
        INamedTypeSymbol containingType)
    {
        var cacheContracts =
            new Dictionary<ISymbol, List<ReplayContract>>(SymbolEqualityComparer.Default);
        var retainedMembersByParameter =
            new Dictionary<IParameterSymbol, HashSet<ISymbol>>(SymbolEqualityComparer.Default);
        var parametersByRetainedMember =
            new Dictionary<ISymbol, HashSet<IParameterSymbol>>(SymbolEqualityComparer.Default);
        var retentionAssignmentsByMember =
            new Dictionary<ISymbol, AssignmentExpressionSyntax>(SymbolEqualityComparer.Default);
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
                foreach (var assignment in syntax.DescendantNodes(node =>
                             node is not AnonymousFunctionExpressionSyntax and
                             not LocalFunctionStatementSyntax)
                         .OfType<AssignmentExpressionSyntax>()
                         .Where(node => node.IsKind(SyntaxKind.SimpleAssignmentExpression)))
                {
                    if (!TryGetReliableRetainedParameter(
                            semanticModel,
                            syntax,
                            assignment,
                            method,
                            context.CancellationToken,
                            out var retainedMember,
                            out var parameter))
                    {
                        continue;
                    }

                    if (!retainedMembersByParameter.TryGetValue(
                            parameter,
                            out var retainedMembers))
                    {
                        retainedMembers =
                            new HashSet<ISymbol>(SymbolEqualityComparer.Default);
                        retainedMembersByParameter.Add(parameter, retainedMembers);
                    }

                    retainedMembers.Add(retainedMember);

                    if (!parametersByRetainedMember.TryGetValue(
                            retainedMember,
                            out var retainedParameters))
                    {
                        retainedParameters =
                            new HashSet<IParameterSymbol>(SymbolEqualityComparer.Default);
                        parametersByRetainedMember.Add(retainedMember, retainedParameters);
                    }

                    retainedParameters.Add(parameter);
                    retentionAssignmentsByMember[retainedMember] = assignment;
                }

                foreach (var invocation in syntax.DescendantNodes(node =>
                             node is not AnonymousFunctionExpressionSyntax and
                             not LocalFunctionStatementSyntax)
                         .OfType<InvocationExpressionSyntax>())
                {
                    if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess ||
                        memberAccess.Name.Identifier.ValueText != "UpdateRanges" ||
                        !TryGetCurrentInstanceMember(
                            semanticModel,
                            memberAccess.Expression,
                            context.CancellationToken,
                            out var cacheMember) ||
                        semanticModel.GetOperation(
                            invocation,
                            context.CancellationToken) is not IInvocationOperation invocationOperation)
                    {
                        continue;
                    }

                    var argumentParametersByOrdinal =
                        new Dictionary<int, HashSet<IParameterSymbol>>();
                    foreach (var argument in invocationOperation.Arguments)
                    {
                        if (argument.Parameter is null)
                        {
                            continue;
                        }

                        var argumentParameters = GetContractParameters(
                            argument.Value,
                            method,
                            parametersByRetainedMember,
                            retentionAssignmentsByMember);
                        if (argumentParameters.Count > 0)
                        {
                            argumentParametersByOrdinal[argument.Parameter.Ordinal] =
                                argumentParameters;
                        }
                    }

                    if (argumentParametersByOrdinal.Count == 0)
                    {
                        continue;
                    }

                    if (!cacheContracts.TryGetValue(cacheMember, out var contracts))
                    {
                        contracts = new List<ReplayContract>();
                        cacheContracts.Add(cacheMember, contracts);
                    }

                    contracts.Add(new ReplayContract(
                        invocationOperation.TargetMethod,
                        argumentParametersByOrdinal));
                }
            }
        }

        return new RangesChangedState(cacheContracts, retainedMembersByParameter);
    }

    private static bool TryGetReliableRetainedParameter(
        SemanticModel semanticModel,
        SyntaxNode methodSyntax,
        AssignmentExpressionSyntax assignment,
        IMethodSymbol method,
        System.Threading.CancellationToken cancellationToken,
        out ISymbol retainedMember,
        out IParameterSymbol parameter)
    {
        retainedMember = null!;
        parameter = null!;
        if (assignment.Parent is not ExpressionStatementSyntax assignmentStatement ||
            assignmentStatement.Parent is not BlockSyntax assignmentBlock ||
            methodSyntax is not MethodDeclarationSyntax { Body: { } methodBody } ||
            assignmentBlock != methodBody ||
            !TryGetCurrentInstanceMember(
                semanticModel,
                assignment.Left,
                cancellationToken,
                out retainedMember) ||
            semanticModel.GetOperation(
                assignment.Right,
                cancellationToken) is not { } assignedValue ||
            !TryGetDirectParameter(assignedValue, method, out parameter))
        {
            return false;
        }

        if (methodBody.Statements
                .Take(methodBody.Statements.IndexOf(assignmentStatement))
                .Any(ContainsPotentialExit))
        {
            return false;
        }

        var retainedMemberValue = retainedMember;
        return !methodSyntax.DescendantNodes(node =>
                    node is not AnonymousFunctionExpressionSyntax and
                    not LocalFunctionStatementSyntax)
                .OfType<AssignmentExpressionSyntax>()
                .Any(other => other != assignment &&
                              TryGetCurrentInstanceMember(
                                  semanticModel,
                                  other.Left,
                                  cancellationToken,
                                  out var otherMember) &&
                              SymbolEqualityComparer.Default.Equals(
                                  otherMember,
                                  retainedMemberValue));
    }

    private static bool TryGetDirectParameter(
        IOperation operation,
        IMethodSymbol method,
        out IParameterSymbol parameter)
    {
        while (operation is IConversionOperation conversion)
        {
            operation = conversion.Operand;
        }

        if (operation is IParameterReferenceOperation { Parameter: { } referencedParameter } &&
            SymbolEqualityComparer.Default.Equals(referencedParameter.ContainingSymbol, method))
        {
            parameter = referencedParameter;
            return true;
        }

        parameter = null!;
        return false;
    }

    private static HashSet<IParameterSymbol> GetReferencedParameters(
        IOperation operation,
        IMethodSymbol method)
    {
        var parameters = new HashSet<IParameterSymbol>(SymbolEqualityComparer.Default);
        foreach (var descendant in DescendantsAndSelf(operation))
        {
            if (descendant is IParameterReferenceOperation { Parameter: { } parameter } &&
                SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol, method))
            {
                parameters.Add(parameter);
            }
        }

        return parameters;
    }

    private static HashSet<IParameterSymbol> GetContractParameters(
        IOperation operation,
        IMethodSymbol method,
        Dictionary<ISymbol, HashSet<IParameterSymbol>> parametersByRetainedMember,
        Dictionary<ISymbol, AssignmentExpressionSyntax> retentionAssignmentsByMember)
    {
        var parameters = GetReferencedParameters(operation, method);
        foreach (var descendant in DescendantsAndSelf(operation))
        {
            ISymbol? retainedMember = descendant switch
            {
                IFieldReferenceOperation fieldReference
                    when IsCurrentInstance(fieldReference.Instance) => fieldReference.Field,
                IPropertyReferenceOperation propertyReference
                    when IsCurrentInstance(propertyReference.Instance) => propertyReference.Property,
                _ => null,
            };
            if (retainedMember is not null &&
                parametersByRetainedMember.TryGetValue(
                    retainedMember,
                    out var retainedParameters) &&
                retentionAssignmentsByMember.TryGetValue(
                    retainedMember,
                    out var retentionAssignment) &&
                IsGuaranteedLaterInSameBlock(retentionAssignment, operation.Syntax))
            {
                parameters.UnionWith(retainedParameters);
            }
        }

        return parameters;
    }

    private static IEnumerable<IOperation> DescendantsAndSelf(IOperation operation)
    {
        yield return operation;
        foreach (var child in operation.ChildOperations)
        {
            foreach (var descendant in DescendantsAndSelf(child))
            {
                yield return descendant;
            }
        }
    }

    private static bool TryGetCurrentInstanceMember(
        SemanticModel semanticModel,
        ExpressionSyntax expression,
        System.Threading.CancellationToken cancellationToken,
        out ISymbol member)
    {
        var operation = semanticModel.GetOperation(expression, cancellationToken);
        if (operation is not null)
        {
            operation = UnwrapBenignOperation(operation);
        }

        switch (operation)
        {
            case IFieldReferenceOperation fieldReference
                when !fieldReference.Field.IsStatic &&
                     IsCurrentInstance(fieldReference.Instance):
                member = fieldReference.Field;
                return true;
            case IPropertyReferenceOperation propertyReference
                when !propertyReference.Property.IsStatic &&
                     IsCurrentInstance(propertyReference.Instance):
                member = propertyReference.Property;
                return true;
            case IEventReferenceOperation eventReference
                when !eventReference.Event.IsStatic &&
                     IsCurrentInstance(eventReference.Instance):
                member = eventReference.Event;
                return true;
            default:
                member = null!;
                return false;
        }
    }

    private static bool IsCurrentInstance(IOperation? operation)
    {
        if (operation is null)
        {
            return false;
        }

        return UnwrapBenignOperation(operation) is IInstanceReferenceOperation
        {
            ReferenceKind: InstanceReferenceKind.ContainingTypeInstance,
        };
    }

    private static IOperation UnwrapBenignOperation(IOperation operation)
    {
        while (true)
        {
            switch (operation)
            {
                case IConversionOperation { OperatorMethod: null } conversion:
                    operation = conversion.Operand;
                    break;
                case IParenthesizedOperation parenthesized:
                    operation = parenthesized.Operand;
                    break;
                default:
                    return operation;
            }
        }
    }

    private static bool CreatesNewInstance(ExpressionSyntax expression) =>
        expression is ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax;

    private static bool ReplaysRangesAfterReset(
        SyntaxNodeAnalysisContext context,
        MethodDeclarationSyntax method,
        ExpressionSyntax resetAccess,
        ISymbol cacheMember,
        RangesChangedState rangeState)
    {
        foreach (var invocation in method.DescendantNodes(node =>
                         node is not AnonymousFunctionExpressionSyntax and
                         not LocalFunctionStatementSyntax)
                     .OfType<InvocationExpressionSyntax>()
                         .Where(node => IsGuaranteedLaterInSameBlock(resetAccess, node)))
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess ||
                memberAccess.Name.Identifier.ValueText != "UpdateRanges" ||
                !TryGetCurrentInstanceMember(
                    context.SemanticModel,
                    memberAccess.Expression,
                    context.CancellationToken,
                    out var receiver) ||
                !SymbolEqualityComparer.Default.Equals(receiver, cacheMember) ||
                context.SemanticModel.GetOperation(
                    invocation,
                    context.CancellationToken) is not IInvocationOperation invocationOperation)
            {
                continue;
            }

            if (rangeState.CacheContracts[cacheMember].Any(contract =>
                    ReplaysContract(
                        context.SemanticModel,
                        invocationOperation,
                        contract,
                        rangeState,
                        context.CancellationToken)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ReplaysContract(
        SemanticModel semanticModel,
        IInvocationOperation invocation,
        ReplayContract contract,
        RangesChangedState rangeState,
        System.Threading.CancellationToken cancellationToken)
    {
        if (!SymbolEqualityComparer.Default.Equals(
                invocation.TargetMethod.OriginalDefinition,
                contract.TargetMethod.OriginalDefinition))
        {
            return false;
        }

        foreach (var contractArgument in contract.ArgumentParametersByOrdinal)
        {
            var replayArgument = invocation.Arguments.FirstOrDefault(argument =>
                argument.Parameter?.Ordinal == contractArgument.Key);
            if (replayArgument is null)
            {
                return false;
            }

            foreach (var parameter in contractArgument.Value)
            {
                if (!rangeState.RetainedMembersByParameter.TryGetValue(
                        parameter,
                        out var retainedMembers) ||
                    !TryGetDirectCurrentInstanceMember(
                        replayArgument.Value,
                        out var replayMember) ||
                    !retainedMembers.Contains(replayMember) ||
                    IsMemberWrittenBefore(
                        semanticModel,
                        invocation.Syntax,
                        replayMember,
                        cancellationToken))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool TryGetDirectCurrentInstanceMember(
        IOperation operation,
        out ISymbol member)
    {
        operation = UnwrapBenignOperation(operation);

        switch (operation)
        {
            case IFieldReferenceOperation fieldReference
                when IsCurrentInstance(fieldReference.Instance):
                member = fieldReference.Field;
                return true;
            case IPropertyReferenceOperation propertyReference
                when IsCurrentInstance(propertyReference.Instance):
                member = propertyReference.Property;
                return true;
            default:
                member = null!;
                return false;
        }
    }

    private static bool IsMemberWrittenBefore(
        SemanticModel semanticModel,
        SyntaxNode replay,
        ISymbol member,
        System.Threading.CancellationToken cancellationToken)
    {
        var method = replay.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        return method?.DescendantNodes(node =>
                    node is not AnonymousFunctionExpressionSyntax and
                    not LocalFunctionStatementSyntax)
                .OfType<AssignmentExpressionSyntax>()
                .Any(assignment =>
                    assignment.SpanStart < replay.SpanStart &&
                    TryGetCurrentInstanceMember(
                        semanticModel,
                        assignment.Left,
                        cancellationToken,
                        out var assignedMember) &&
                    SymbolEqualityComparer.Default.Equals(assignedMember, member)) == true;
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
            .Any(node => node switch
            {
                ReturnStatementSyntax or
                ThrowStatementSyntax or
                ThrowExpressionSyntax or
                GotoStatementSyntax or
                YieldStatementSyntax => true,
                BreakStatementSyntax breakStatement =>
                    BranchTargetLiesOutsideStatement(
                        breakStatement,
                        statement,
                        canTargetSwitch: true),
                ContinueStatementSyntax continueStatement =>
                    BranchTargetLiesOutsideStatement(
                        continueStatement,
                        statement,
                        canTargetSwitch: false),
                _ => false,
            });

    private static bool BranchTargetLiesOutsideStatement(
        StatementSyntax branch,
        StatementSyntax statement,
        bool canTargetSwitch)
    {
        var target = branch.Ancestors()
            .OfType<StatementSyntax>()
            .FirstOrDefault(candidate =>
                candidate is
                    ForStatementSyntax or
                    ForEachStatementSyntax or
                    ForEachVariableStatementSyntax or
                    WhileStatementSyntax or
                    DoStatementSyntax ||
                (canTargetSwitch && candidate is SwitchStatementSyntax));

        return target is null ||
               (target != statement && !target.Ancestors().Contains(statement));
    }

    private sealed class RangesChangedState
    {
        public RangesChangedState(
            Dictionary<ISymbol, List<ReplayContract>> cacheContracts,
            Dictionary<IParameterSymbol, HashSet<ISymbol>> retainedMembersByParameter)
        {
            CacheContracts = cacheContracts;
            RetainedMembersByParameter = retainedMembersByParameter;
        }

        public Dictionary<ISymbol, List<ReplayContract>> CacheContracts { get; }

        public Dictionary<IParameterSymbol, HashSet<ISymbol>> RetainedMembersByParameter { get; }
    }

    private sealed class ReplayContract
    {
        public ReplayContract(
            IMethodSymbol targetMethod,
            Dictionary<int, HashSet<IParameterSymbol>> argumentParametersByOrdinal)
        {
            TargetMethod = targetMethod;
            ArgumentParametersByOrdinal = argumentParametersByOrdinal;
        }

        public IMethodSymbol TargetMethod { get; }

        public Dictionary<int, HashSet<IParameterSymbol>> ArgumentParametersByOrdinal { get; }
    }
}
