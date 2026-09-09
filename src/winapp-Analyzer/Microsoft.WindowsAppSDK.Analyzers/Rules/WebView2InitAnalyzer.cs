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
/// <see cref="DiagnosticIds.WebView2NoInit"/> — WebView2 method calls
/// (<c>NavigateToString</c>, <c>CoreWebView2</c> access) without a corresponding
/// <c>EnsureCoreWebView2Async()</c> call in the same class.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class WebView2InitAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticIds.WebView2NoInit,
        "WebView2 used without initialization",
        "'{0}' called but no EnsureCoreWebView2Async() found in this class — WebView2 will silently fail",
        DiagnosticCategories.Interop,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "WebView2.NavigateToString() and CoreWebView2 property access silently fail if " +
                     "EnsureCoreWebView2Async() hasn't completed. Always await initialization first.",
        helpLinkUri: HelpLinks.For(DiagnosticIds.WebView2NoInit));

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    private static readonly string[] WebView2Methods =
    {
        "NavigateToString", "Navigate", "PostWebMessageAsString",
        "PostWebMessageAsJson", "ExecuteScriptAsync"
    };

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeClass, SyntaxKind.ClassDeclaration);
    }

    private static void AnalyzeClass(SyntaxNodeAnalysisContext context)
    {
        var classDecl = (ClassDeclarationSyntax)context.Node;

        var hasInit = classDecl.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Any(inv => inv.Expression.ToString().Contains("EnsureCoreWebView2Async"));
        if (hasInit) return;

        var hasInitEvent = classDecl.DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .Any(ma => ma.Name.Identifier.Text == "CoreWebView2Initialized");
        if (hasInitEvent) return;

        var webViewCalls = classDecl.DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .Where(ma => WebView2Methods.Contains(ma.Name.Identifier.Text) ||
                         ma.Name.Identifier.Text == "CoreWebView2");

        foreach (var call in webViewCalls)
        {
            var exprType = context.SemanticModel.GetTypeInfo(call.Expression).Type;
            if (exprType != null)
            {
                var typeName = exprType.ToDisplayString();
                if (!typeName.Contains("WebView2") && !typeName.Contains("CoreWebView2")) continue;
            }
            else
            {
                var exprText = call.Expression.ToString().ToLowerInvariant();
                if (!exprText.Contains("webview") && !exprText.Contains("corewebview")) continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(Rule, call.GetLocation(), call.Name.Identifier.Text));
            break; // One diagnostic per class is enough.
        }
    }
}
