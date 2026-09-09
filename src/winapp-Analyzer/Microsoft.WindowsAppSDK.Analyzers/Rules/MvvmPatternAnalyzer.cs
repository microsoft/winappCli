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
/// <see cref="DiagnosticIds.OldMvvmSyntax"/> — old field-backed
/// <c>[ObservableProperty]</c> from CommunityToolkit.Mvvm 8.2. Should use
/// partial-property syntax (8.3+) to avoid AOT warning <c>MVVMTK0045</c>.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MvvmPatternAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor OldSyntaxRule = new(
        DiagnosticIds.OldMvvmSyntax,
        "Old field-backed [ObservableProperty]",
        "Field-backed [ObservableProperty] is the old CommunityToolkit.Mvvm 8.2 pattern — use partial property: [ObservableProperty] public partial {0} {1} {{ get; set; }}",
        DiagnosticCategories.Mvvm,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The field-backed [ObservableProperty] generates AOT compatibility warnings (MVVMTK0045). " +
                     "Use the partial property syntax available in CommunityToolkit.Mvvm 8.3+.",
        helpLinkUri: HelpLinks.For(DiagnosticIds.OldMvvmSyntax));

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(OldSyntaxRule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeFieldDeclaration, SyntaxKind.FieldDeclaration);
    }

    private static void AnalyzeFieldDeclaration(SyntaxNodeAnalysisContext context)
    {
        var fieldDecl = (FieldDeclarationSyntax)context.Node;

        var hasAttribute = false;
        foreach (var attrList in fieldDecl.AttributeLists)
        {
            foreach (var attr in attrList.Attributes)
            {
                var name = attr.Name.ToString();
                if (name == "ObservableProperty" || name == "ObservablePropertyAttribute")
                {
                    hasAttribute = true;
                    break;
                }
            }
            if (hasAttribute) break;
        }

        if (!hasAttribute) return;

        var variable = fieldDecl.Declaration.Variables.FirstOrDefault();
        if (variable == null) return;

        var fieldName = variable.Identifier.Text;
        var typeName = fieldDecl.Declaration.Type.ToString();

        var propName = fieldName.TrimStart('_');
        if (propName.Length > 0)
        {
            propName = char.ToUpperInvariant(propName[0]) + propName.Substring(1);
        }

        context.ReportDiagnostic(Diagnostic.Create(OldSyntaxRule, fieldDecl.GetLocation(), typeName, propName));
    }
}
