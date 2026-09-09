// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Microsoft.WindowsAppSDK.Analyzers.Rules;

/// <summary>
/// Cross-file analysis: correlates XAML declarations with C# code-behind.
/// <list type="bullet">
///   <item><see cref="DiagnosticIds.WebView2NoInitXaml"/>     — &lt;WebView2&gt; in XAML but no EnsureCoreWebView2Async in code-behind.</item>
///   <item><see cref="DiagnosticIds.TabViewRawContentXaml"/>  — &lt;TabView&gt; in XAML with raw content assignment in code-behind.</item>
/// </list>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class XamlCodeBehindAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor WebView2NoInitRule = new(
        DiagnosticIds.WebView2NoInitXaml,
        "WebView2 in XAML without initialization in code-behind",
        "XAML declares <WebView2> but code-behind '{0}' has no EnsureCoreWebView2Async() call — NavigateToString will silently fail",
        DiagnosticCategories.Interop,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: HelpLinks.For(DiagnosticIds.WebView2NoInitXaml),
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    private static readonly DiagnosticDescriptor TabViewRawContentRule = new(
        DiagnosticIds.TabViewRawContentXaml,
        "TabView in XAML with raw content in code-behind",
        "XAML declares <TabView> but code-behind assigns raw control as tab content — TabView doesn't stretch child controls. Use Frame+Page per tab",
        DiagnosticCategories.Runtime,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: HelpLinks.For(DiagnosticIds.TabViewRawContentXaml),
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(WebView2NoInitRule, TabViewRawContentRule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationAction(AnalyzeCompilation);
    }

    private static void AnalyzeCompilation(CompilationAnalysisContext context)
    {
        var xamlData = new Dictionary<string, XamlInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in context.Options.AdditionalFiles)
        {
            if (!file.Path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) continue;

            var text = file.GetText(context.CancellationToken);
            if (text == null) continue;

            XDocument? doc;
            try { doc = XDocument.Parse(text.ToString()); } catch { continue; }

            var elements = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in doc.Descendants()) elements.Add(e.Name.LocalName);

            xamlData[Path.GetFileNameWithoutExtension(file.Path)] = new XamlInfo
            {
                HasWebView2 = elements.Contains("WebView2"),
                HasTabView = elements.Contains("TabView"),
                XamlPath = file.Path
            };
        }

        if (xamlData.Count == 0) return;

        foreach (var tree in context.Compilation.SyntaxTrees)
        {
            var filePath = tree.FilePath;
            if (string.IsNullOrEmpty(filePath)) continue;
            if (!filePath.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase)) continue;

            var fileName = Path.GetFileName(filePath);
            var xamlName = fileName.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase)
                ? fileName.Substring(0, fileName.Length - 8)
                : fileName;

            if (!xamlData.TryGetValue(xamlName, out var info)) continue;

            var root = tree.GetRoot(context.CancellationToken);
            var semanticModel = context.Compilation.GetSemanticModel(tree);

            if (info.HasWebView2)
            {
                var hasInit = root.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .Any(inv => inv.Expression.ToString().Contains("EnsureCoreWebView2Async"));
                var hasInitEvent = root.DescendantNodes()
                    .OfType<MemberAccessExpressionSyntax>()
                    .Any(ma => ma.Name.Identifier.Text == "CoreWebView2Initialized");

                if (!hasInit && !hasInitEvent)
                {
                    var classDecl = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
                    if (classDecl != null)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            WebView2NoInitRule,
                            classDecl.Identifier.GetLocation(),
                            classDecl.Identifier.Text));
                    }
                }
            }

            if (info.HasTabView)
            {
                var contentAssignments = root.DescendantNodes()
                    .OfType<AssignmentExpressionSyntax>()
                    .Where(a => a.Left is MemberAccessExpressionSyntax ma &&
                                ma.Name.Identifier.Text == "Content" &&
                                a.Right is ObjectCreationExpressionSyntax);

                foreach (var assignment in contentAssignments)
                {
                    var creation = (ObjectCreationExpressionSyntax)assignment.Right;
                    if (creation.Type.ToString() == "Frame") continue;

                    var leftType = semanticModel.GetTypeInfo(
                        ((MemberAccessExpressionSyntax)assignment.Left).Expression).Type;

                    var isTabViewItem = false;
                    if (leftType != null)
                    {
                        var current = leftType;
                        while (current != null)
                        {
                            if (current.ToDisplayString().Contains("TabViewItem"))
                            {
                                isTabViewItem = true;
                                break;
                            }
                            current = current.BaseType;
                        }
                    }
                    else
                    {
                        var varName = ((MemberAccessExpressionSyntax)assignment.Left)
                            .Expression.ToString().ToLowerInvariant();
                        isTabViewItem = varName.Contains("tab");
                    }

                    if (isTabViewItem)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            TabViewRawContentRule, assignment.GetLocation()));
                    }
                }
            }
        }
    }

    private sealed class XamlInfo
    {
        public bool HasWebView2 { get; set; }
        public bool HasTabView { get; set; }
        public string XamlPath { get; set; } = "";
    }
}
