// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Microsoft.WindowsAppSDK.Analyzers.Rules;

/// <summary>
/// Data-driven UWP → Windows App SDK API mapping analyzer. Reports:
/// <list type="bullet">
///   <item><see cref="DiagnosticIds.ApiMappingMatch"/>     — UWP API used; WinAppSDK equivalent exists.</item>
///   <item><see cref="DiagnosticIds.ApiMappingNoEquiv"/>   — UWP API used; not yet supported in WinAppSDK.</item>
///   <item><see cref="DiagnosticIds.FeatureMappingHint"/>  — Heads-up about a feature-area shift.</item>
/// </list>
///
/// All three diagnostics are gated by <see cref="ProjectContext.Detect"/> — they only fire
/// in projects detected as migrating from UWP. Greenfield WinUI 3 projects see nothing.
/// This is the single biggest false-positive guard in the analyzer.
///
/// Detection is fully semantic: we resolve the symbol via <see cref="SemanticModel"/> and
/// match the containing-type/member display string against <see cref="ApiMappings.ByQualifiedName"/>.
/// Identifier-only matches are explicitly disallowed.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ApiMappingAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor MappingMatchRule = new(
        DiagnosticIds.ApiMappingMatch,
        "UWP API has Windows App SDK equivalent",
        "{0} → {1}",
        DiagnosticCategories.Migration,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Migrating from UWP to Windows App SDK: this UWP API has a documented WinAppSDK equivalent. Replace per the Microsoft Learn API mapping table.",
        helpLinkUri: HelpLinks.For(DiagnosticIds.ApiMappingMatch));

    private static readonly DiagnosticDescriptor MappingNoEquivRule = new(
        DiagnosticIds.ApiMappingNoEquiv,
        "UWP API has no Windows App SDK equivalent",
        "{0} is not supported in Windows App SDK ({1})",
        DiagnosticCategories.Migration,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "This UWP API has no direct equivalent in Windows App SDK. You may need to redesign the affected functionality before migrating.",
        helpLinkUri: HelpLinks.For(DiagnosticIds.ApiMappingNoEquiv));

    private static readonly DiagnosticDescriptor FeatureHintRule = new(
        DiagnosticIds.FeatureMappingHint,
        "UWP feature area has Windows App SDK migration guidance",
        "{0} ({1}): {2}",
        DiagnosticCategories.Migration,
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Migration hint based on Microsoft Learn feature mapping table. Informational only.",
        helpLinkUri: HelpLinks.For(DiagnosticIds.FeatureMappingHint));

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(MappingMatchRule, MappingNoEquivRule, FeatureHintRule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            // Single context detection per compilation.
            var kind = ProjectContext.Detect(start.Compilation, start.Options);
            if (kind != ProjectKind.MigratingFromUwp)
            {
                // Skip registration entirely — zero overhead for greenfield projects.
                return;
            }

            start.RegisterSyntaxNodeAction(AnalyzeMemberAccess, Microsoft.CodeAnalysis.CSharp.SyntaxKind.SimpleMemberAccessExpression);
            start.RegisterSyntaxNodeAction(AnalyzeUsing, Microsoft.CodeAnalysis.CSharp.SyntaxKind.UsingDirective);
        });
    }

    private static void AnalyzeMemberAccess(SyntaxNodeAnalysisContext context)
    {
        var node = (MemberAccessExpressionSyntax)context.Node;
        // Only analyze the outermost member access in a chain (e.g. on
        // `Windows.Graphics.Printing.PrintManager.GetForCurrentView()` we report once on
        // the GetForCurrentView access, not on every namespace-qualifier segment).
        if (node.Parent is MemberAccessExpressionSyntax) return;

        var symbol = context.SemanticModel.GetSymbolInfo(node, context.CancellationToken).Symbol;
        if (symbol == null) return;

        // Build candidate keys: full member, then containing type.
        var memberKey = symbol.ContainingType is { } ct
            ? $"{ct.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "")}.{symbol.Name}"
            : symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "");
        var typeKey = symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "");

        if (TryReport(context, node.GetLocation(), memberKey)) return;
        if (typeKey != null) TryReport(context, node.GetLocation(), typeKey);
    }

    private static void AnalyzeUsing(SyntaxNodeAnalysisContext context)
    {
        var node = (UsingDirectiveSyntax)context.Node;
        var ns = node.Name?.ToString();
        if (string.IsNullOrEmpty(ns)) return;
        TryReport(context, node.GetLocation(), ns!);

        // Feature-mapping fallback (Info-level): emit at most one per file per prefix.
        foreach (var feature in FeatureMappings.All)
        {
            if (ns!.StartsWith(feature.UwpNamespacePrefix, System.StringComparison.Ordinal))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    FeatureHintRule, node.GetLocation(),
                    feature.Sensitive ? MigrationTiers.SensitiveProperties : null,
                    feature.UwpNamespacePrefix, feature.Area, feature.Note));
                break;
            }
        }
    }

    private static bool TryReport(SyntaxNodeAnalysisContext context, Location location, string key)
    {
        if (!ApiMappings.ByQualifiedName.TryGetValue(key, out var mapping)) return false;
        var properties = mapping.StartupCrash ? MigrationTiers.StartupCrashProperties : null;
        if (mapping.WinAppSdkReplacement != null)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                MappingMatchRule, location, properties, key, mapping.WinAppSdkReplacement));
        }
        else
        {
            context.ReportDiagnostic(Diagnostic.Create(
                MappingNoEquivRule, location, properties, key, mapping.LearnAnchor ?? "see migration guide"));
        }
        return true;
    }
}
