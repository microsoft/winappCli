// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Microsoft.WindowsAppSDK.Analyzers.Rules;

/// <summary>
/// Detects usage of removed ONNX Runtime GenAI APIs (continuous-decoding change in v0.6.0+):
/// <list type="bullet">
///   <item><see cref="DiagnosticIds.GenAiSetInputSequences"/>   — <c>SetInputSequences</c> → <c>AppendTokenSequences</c>.</item>
///   <item><see cref="DiagnosticIds.GenAiComputeLogits"/>       — <c>ComputeLogits</c> → remove (handled by <c>GenerateNextToken</c>).</item>
///   <item><see cref="DiagnosticIds.GenAiTokenizerStreamCtor"/> — <c>new TokenizerStream(…)</c> → <c>tokenizer.CreateStream()</c>.</item>
/// </list>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class GenAiApiAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor SetInputSequencesRule = new(
        DiagnosticIds.GenAiSetInputSequences,
        "Removed GenAI API: SetInputSequences",
        "SetInputSequences was removed in OnnxRuntimeGenAI 0.6.0 — use generator.AppendTokenSequences(sequences) instead (input goes on Generator, not GeneratorParams)",
        DiagnosticCategories.Interop,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: HelpLinks.For(DiagnosticIds.GenAiSetInputSequences));

    private static readonly DiagnosticDescriptor ComputeLogitsRule = new(
        DiagnosticIds.GenAiComputeLogits,
        "Removed GenAI API: ComputeLogits",
        "ComputeLogits was removed in OnnxRuntimeGenAI 0.6.0 — remove this call, GenerateNextToken() handles logits internally",
        DiagnosticCategories.Interop,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: HelpLinks.For(DiagnosticIds.GenAiComputeLogits));

    private static readonly DiagnosticDescriptor TokenizerStreamCtorRule = new(
        DiagnosticIds.GenAiTokenizerStreamCtor,
        "Removed GenAI API: TokenizerStream constructor",
        "new TokenizerStream(tokenizer) was removed in OnnxRuntimeGenAI 0.6.0 — use tokenizer.CreateStream() instead",
        DiagnosticCategories.Interop,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: HelpLinks.For(DiagnosticIds.GenAiTokenizerStreamCtor));

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(SetInputSequencesRule, ComputeLogitsRule, TokenizerStreamCtorRule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeObjectCreation, SyntaxKind.ObjectCreationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        var methodName = invocation.Expression switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.Text,
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            _ => null
        };
        if (methodName == null) return;

        if (methodName == "SetInputSequences")
        {
            context.ReportDiagnostic(Diagnostic.Create(SetInputSequencesRule, invocation.GetLocation()));
        }
        else if (methodName == "ComputeLogits")
        {
            context.ReportDiagnostic(Diagnostic.Create(ComputeLogitsRule, invocation.GetLocation()));
        }
    }

    private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext context)
    {
        var creation = (ObjectCreationExpressionSyntax)context.Node;
        var typeName = creation.Type.ToString();
        if (typeName == "TokenizerStream" || typeName.EndsWith(".TokenizerStream"))
        {
            context.ReportDiagnostic(Diagnostic.Create(TokenizerStreamCtorRule, creation.GetLocation()));
        }
    }
}
