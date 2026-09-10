// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Microsoft.WindowsAppSDK.Analyzers.Rules;

/// <summary>
/// UWP → WinUI 3 API compatibility rules:
/// <list type="bullet">
///   <item><see cref="DiagnosticIds.UwpXamlNamespace"/>  — <c>using Windows.UI.Xaml</c> (use <c>Microsoft.UI.Xaml</c>).</item>
///   <item><see cref="DiagnosticIds.WindowCurrent"/>     — <c>Window.Current</c> / <c>Application.Current.Window</c> (UWP-only).</item>
///   <item><see cref="DiagnosticIds.CoreDispatcher"/>    — <c>CoreDispatcher</c> and <c>DependencyObject.Dispatcher</c> (null in WinUI 3; use <c>DispatcherQueue</c>).</item>
///   <item><see cref="DiagnosticIds.GetForCurrentView"/> — <c>GetForCurrentView()</c> (use HWND-based interop).</item>
/// </list>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UwpApiAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor UwpNamespaceRule = new(
        DiagnosticIds.UwpXamlNamespace,
        "UWP XAML namespace used",
        "Windows.UI.Xaml is the UWP namespace — use Microsoft.UI.Xaml for WinUI 3",
        DiagnosticCategories.Compatibility,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: HelpLinks.For(DiagnosticIds.UwpXamlNamespace));

    private static readonly DiagnosticDescriptor WindowCurrentRule = new(
        DiagnosticIds.WindowCurrent,
        "Window.Current is UWP-only",
        "Window.Current does not exist in WinUI 3 desktop apps — store the Window reference in App.xaml.cs",
        DiagnosticCategories.Compatibility,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: HelpLinks.For(DiagnosticIds.WindowCurrent));

    private static readonly DiagnosticDescriptor CoreDispatcherRule = new(
        DiagnosticIds.CoreDispatcher,
        "CoreDispatcher / Dispatcher is UWP-only",
        "CoreDispatcher is UWP-only and DependencyObject.Dispatcher is null in WinUI 3 (accessing its members throws NullReferenceException at launch) — use DispatcherQueue instead",
        DiagnosticCategories.Compatibility,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: HelpLinks.For(DiagnosticIds.CoreDispatcher));

    private static readonly DiagnosticDescriptor GetForCurrentViewRule = new(
        DiagnosticIds.GetForCurrentView,
        "GetForCurrentView is UWP-only",
        "{0}.GetForCurrentView() is UWP-only — use HWND-based COM interop in WinUI 3",
        DiagnosticCategories.Compatibility,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: HelpLinks.For(DiagnosticIds.GetForCurrentView));

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(UwpNamespaceRule, WindowCurrentRule, CoreDispatcherRule, GetForCurrentViewRule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterSyntaxNodeAction(AnalyzeUsingDirective, SyntaxKind.UsingDirective);
        context.RegisterSyntaxNodeAction(AnalyzeMemberAccess, SyntaxKind.SimpleMemberAccessExpression);
        context.RegisterSyntaxNodeAction(AnalyzeIdentifier, SyntaxKind.IdentifierName);
    }

    private static void AnalyzeUsingDirective(SyntaxNodeAnalysisContext context)
    {
        var usingDirective = (UsingDirectiveSyntax)context.Node;
        var name = usingDirective.Name?.ToString();
        if (name != null && name.StartsWith("Windows.UI.Xaml"))
        {
            context.ReportDiagnostic(Diagnostic.Create(UwpNamespaceRule, usingDirective.GetLocation()));
        }
    }

    private static void AnalyzeMemberAccess(SyntaxNodeAnalysisContext context)
    {
        var memberAccess = (MemberAccessExpressionSyntax)context.Node;
        var memberName = memberAccess.Name.Identifier.Text;

        // Window.Current
        if (memberName == "Current")
        {
            var expr = memberAccess.Expression.ToString();
            if (expr == "Window" || expr == "Application.Current.Window")
            {
                var symbol = context.SemanticModel.GetSymbolInfo(memberAccess.Expression).Symbol;
                var typeStr = symbol?.ToDisplayString() ?? expr;
                if (Allowlists.WindowCurrentSafeTypes.Contains(typeStr)) return;
                if (typeStr.Contains("Window") || expr == "Window")
                {
                    context.ReportDiagnostic(Diagnostic.Create(WindowCurrentRule, memberAccess.GetLocation()));
                }
            }
        }

        // GetForCurrentView()
        if (memberName == "GetForCurrentView")
        {
            var callerSymbol = context.SemanticModel.GetSymbolInfo(memberAccess.Expression).Symbol;
            var callerType = callerSymbol?.ToDisplayString() ?? memberAccess.Expression.ToString();
            // Allowlist e.g. ConnectedAnimationService.GetForCurrentView() — still valid in WinUI 3.
            var simpleName = callerType.Contains(".") ? callerType.Substring(callerType.LastIndexOf('.') + 1) : callerType;
            if (Allowlists.GetForCurrentViewSafeTypes.Contains(simpleName) ||
                Allowlists.GetForCurrentViewSafeTypes.Contains(callerType))
            {
                return;
            }
            context.ReportDiagnostic(Diagnostic.Create(
                GetForCurrentViewRule,
                memberAccess.GetLocation(),
                MigrationTiers.StartupCrashProperties,
                memberAccess.Expression.ToString()));
        }

        // DependencyObject.Dispatcher (and CoreWindow.Dispatcher) return Windows.UI.Core.CoreDispatcher,
        // which is null in WinUI 3 desktop apps. The property still compiles, so member access such as
        // `Dispatcher.HasThreadAccess` or `this.Dispatcher.RunAsync(...)` is a build-clean, run-fail launch
        // crash (NullReferenceException). Flag any member access whose target is that UWP Dispatcher property.
        var targetExpr = memberAccess.Expression;
        var targetSymbol = context.SemanticModel.GetSymbolInfo(targetExpr).Symbol;
        bool isUwpDispatcher = targetSymbol is not null
            ? IsUwpDispatcherProperty(targetSymbol)
            // Loose-source fallback (no WinUI metadata, e.g. the driver over raw source): the
            // symbol won't resolve, so match syntactically. A member access on a `Dispatcher` target
            // (`Dispatcher`, `this.Dispatcher`, `x.Dispatcher`) is the UWP DependencyObject/CoreWindow
            // Dispatcher — WinUI 3 has no valid `Dispatcher` property (it exposes `DispatcherQueue`).
            : RightmostName(targetExpr) == "Dispatcher";
        if (isUwpDispatcher)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                CoreDispatcherRule,
                targetExpr.GetLocation(),
                MigrationTiers.StartupCrashProperties));
        }
    }

    /// <summary>
    /// True when <paramref name="symbol"/> is a <c>Dispatcher</c> property that returns a
    /// <c>Windows.UI.Core.CoreDispatcher</c> — i.e. <c>DependencyObject.Dispatcher</c> or
    /// <c>CoreWindow.Dispatcher</c>, both of which are <c>null</c> in WinUI 3 desktop apps.
    /// </summary>
    private static bool IsUwpDispatcherProperty(ISymbol? symbol) =>
        symbol is IPropertySymbol { Name: "Dispatcher" } prop &&
        prop.Type.Name == "CoreDispatcher";

    /// <summary>Rightmost identifier of an expression: <c>Dispatcher</c>, <c>this.Dispatcher</c> and
    /// <c>x.Dispatcher</c> all yield <c>"Dispatcher"</c>; <c>DispatcherQueue</c> yields <c>"DispatcherQueue"</c>.</summary>
    private static string? RightmostName(ExpressionSyntax expr) => expr switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
        _ => null
    };

    private static void AnalyzeIdentifier(SyntaxNodeAnalysisContext context)
    {
        var identifier = (IdentifierNameSyntax)context.Node;
        if (identifier.Identifier.Text != "CoreDispatcher") return;

        if (identifier.Parent is MemberAccessExpressionSyntax ma && ma.Expression == identifier) return;

        var symbol = context.SemanticModel.GetSymbolInfo(identifier).Symbol;
        if (symbol != null)
        {
            var ns = symbol.ContainingNamespace?.ToDisplayString() ?? "";
            if (ns.StartsWith("Windows.UI.Core") || symbol.ToDisplayString().Contains("CoreDispatcher"))
            {
                context.ReportDiagnostic(Diagnostic.Create(CoreDispatcherRule, identifier.GetLocation()));
            }
        }
        else if (identifier.Parent is BaseTypeSyntax || identifier.Parent is TypeSyntax)
        {
            // Unresolved CoreDispatcher reference — likely UWP type.
            context.ReportDiagnostic(Diagnostic.Create(CoreDispatcherRule, identifier.GetLocation()));
        }
    }
}
