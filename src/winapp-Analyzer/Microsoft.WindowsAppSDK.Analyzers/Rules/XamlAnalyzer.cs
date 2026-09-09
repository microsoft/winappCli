// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.WindowsAppSDK.Analyzers.Rules;

/// <summary>
/// Analyzes XAML files (via <c>AdditionalFiles</c>) for common WinUI 3 pitfalls:
/// <list type="bullet">
///   <item><see cref="DiagnosticIds.XBindNestedNoFallback"/> — nested x:Bind without FallbackValue.</item>
///   <item><see cref="DiagnosticIds.XBindMissingMode"/>      — x:Bind without Mode= (defaults to OneTime).</item>
///   <item><see cref="DiagnosticIds.NullConverter"/>         — Converter={x:Null} crashes at runtime.</item>
///   <item><see cref="DiagnosticIds.MissingAutomationId"/>   — interactive control missing AutomationId.</item>
///   <item><see cref="DiagnosticIds.UwpOnlyXamlControl"/>    — UWP-only control (Pivot/Hub/…) with no WinUI 3 equivalent.</item>
/// </list>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class XamlAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor NestedXBindRule = new(
        DiagnosticIds.XBindNestedNoFallback,
        "Nested x:Bind without FallbackValue",
        "Nested x:Bind path '{0}' will crash if any segment is null at startup — add FallbackValue or use a flat ViewModel property",
        DiagnosticCategories.Runtime,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: HelpLinks.For(DiagnosticIds.XBindNestedNoFallback),
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    private static readonly DiagnosticDescriptor MissingAutomationIdRule = new(
        DiagnosticIds.MissingAutomationId,
        "Interactive control missing AutomationId",
        "<{0}> has no AutomationProperties.AutomationId — UI automation targeting will be unreliable",
        DiagnosticCategories.Runtime,
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        helpLinkUri: HelpLinks.For(DiagnosticIds.MissingAutomationId),
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    private static readonly DiagnosticDescriptor NullConverterRule = new(
        DiagnosticIds.NullConverter,
        "Converter={x:Null} crashes at runtime",
        "Converter={{x:Null}} is not a valid converter — it crashes with 'Resource Dictionary Key can only be String-typed'. Use an x:Bind function instead",
        DiagnosticCategories.Runtime,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: HelpLinks.For(DiagnosticIds.NullConverter),
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    private static readonly DiagnosticDescriptor UwpOnlyControlRule = new(
        DiagnosticIds.UwpOnlyXamlControl,
        "UWP-only XAML control",
        "<{0}> is a UWP-only XAML control with no direct WinUI 3 equivalent — {1}",
        DiagnosticCategories.Compatibility,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: HelpLinks.For(DiagnosticIds.UwpOnlyXamlControl),
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    private static readonly DiagnosticDescriptor XBindNoModeRule = new(
        DiagnosticIds.XBindMissingMode,
        "x:Bind without Mode",
        "x:Bind defaults to OneTime — UI will not update after initial load. Add Mode=OneWay or Mode=TwoWay",
        DiagnosticCategories.Runtime,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: HelpLinks.For(DiagnosticIds.XBindMissingMode),
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(NestedXBindRule, MissingAutomationIdRule, XBindNoModeRule, NullConverterRule, UwpOnlyControlRule);

    /// <summary>
    /// UWP XAML controls that have no direct WinUI 3 equivalent. Detected by local element
    /// name (namespace-agnostic) because migrating source is still authored in the UWP XAML
    /// namespace. Value = short migration guidance surfaced in the diagnostic message.
    /// </summary>
    private static readonly Dictionary<string, string> UwpOnlyControls = new(StringComparer.Ordinal)
    {
        ["Pivot"] = "use NavigationView, TabView, or SelectorBar",
        ["PivotItem"] = "migrate the parent Pivot to NavigationView/TabView items",
        ["Hub"] = "use NavigationView or a custom scrolling layout",
        ["HubSection"] = "migrate the parent Hub to a custom layout",
        ["VirtualizingStackPanel"] = "use ItemsStackPanel or ItemsRepeater",
    };

    private static readonly HashSet<string> InteractiveControls = new(StringComparer.OrdinalIgnoreCase)
    {
        "Button", "RepeatButton", "ToggleButton", "HyperlinkButton", "DropDownButton", "SplitButton", "ToggleSplitButton",
        "TextBox", "RichEditBox", "PasswordBox", "NumberBox", "AutoSuggestBox",
        "ComboBox", "CheckBox", "RadioButton", "ToggleSwitch", "Slider", "RatingControl",
        "ListView", "GridView", "TreeView",
        "NavigationViewItem", "TabViewItem", "MenuBarItem", "MenuFlyoutItem",
        "CalendarDatePicker", "DatePicker", "TimePicker", "ColorPicker"
    };

    private static readonly Regex XBindRegex = new(@"\{x:Bind\s+([^}]+)\}", RegexOptions.Compiled);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationAction(AnalyzeCompilation);
    }

    private static void AnalyzeCompilation(CompilationAnalysisContext context)
    {
        foreach (var file in context.Options.AdditionalFiles)
        {
            if (!file.Path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) continue;

            var fileName = Path.GetFileName(file.Path);
            if (fileName.Equals("App.xaml", StringComparison.OrdinalIgnoreCase)) continue;

            var text = file.GetText(context.CancellationToken);
            if (text == null) continue;

            AnalyzeXamlFile(context, file, text.ToString(), text);
        }
    }

    private static void AnalyzeXamlFile(
        CompilationAnalysisContext context,
        AdditionalText file,
        string content,
        SourceText sourceText)
    {
        XDocument? doc;
        try { doc = XDocument.Parse(content, LoadOptions.SetLineInfo); }
        catch { return; }

        foreach (var element in doc.Descendants())
        {
            var localName = element.Name.LocalName;

            if (UwpOnlyControls.TryGetValue(localName, out var guidance))
            {
                var location = CreateLocation(file, sourceText, element);
                context.ReportDiagnostic(Diagnostic.Create(UwpOnlyControlRule, location, localName, guidance));
            }

            if (InteractiveControls.Contains(localName))
            {
                var hasAutomationId = element.Attributes().Any(a =>
                    (a.Name.LocalName == "AutomationId" && a.Name.NamespaceName.Contains("AutomationProperties")) ||
                    a.ToString().Contains("AutomationProperties.AutomationId"));

                if (!hasAutomationId)
                {
                    var location = CreateLocation(file, sourceText, element);
                    context.ReportDiagnostic(Diagnostic.Create(MissingAutomationIdRule, location, localName));
                }
            }

            foreach (var attr in element.Attributes())
            {
                var value = attr.Value;

                if (value.Contains("Converter={x:Null}") || value.Contains("Converter=\"{x:Null}\""))
                {
                    var location = CreateLocation(file, sourceText, element);
                    context.ReportDiagnostic(Diagnostic.Create(NullConverterRule, location));
                }

                foreach (Match match in XBindRegex.Matches(value))
                {
                    var bindExpr = match.Groups[1].Value.Trim();

                    if (!bindExpr.Contains("Mode=") && !IsEventHandler(bindExpr))
                    {
                        var attrName = attr.Name.LocalName;
                        var isCommand = attrName == "Command" || attrName.EndsWith("Command");
                        if (!bindExpr.Contains("Converter") && !isCommand && bindExpr.Contains("."))
                        {
                            var location = CreateLocation(file, sourceText, element);
                            context.ReportDiagnostic(Diagnostic.Create(XBindNoModeRule, location));
                        }
                    }

                    var bindPath = bindExpr.Split(',')[0].Trim();
                    if (!bindPath.Contains("("))
                    {
                        var segments = bindPath.Split('.');
                        if (segments.Length >= 3 && !bindExpr.Contains("FallbackValue"))
                        {
                            var location = CreateLocation(file, sourceText, element);
                            context.ReportDiagnostic(Diagnostic.Create(NestedXBindRule, location, bindPath));
                        }
                    }
                }
            }
        }
    }

    private static bool IsEventHandler(string bindExpr) =>
        !bindExpr.Contains(".") && !bindExpr.Contains(",");

    private static Location CreateLocation(AdditionalText file, SourceText sourceText, XElement element)
    {
        var lineInfo = (System.Xml.IXmlLineInfo)element;
        if (lineInfo.HasLineInfo())
        {
            var line = lineInfo.LineNumber - 1;
            var col = lineInfo.LinePosition - 1;
            if (line >= 0 && line < sourceText.Lines.Count)
            {
                var textLine = sourceText.Lines[line];
                var start = textLine.Start + Math.Min(col, textLine.Span.Length);
                var end = Math.Min(start + element.Name.LocalName.Length + 1, textLine.End);
                var span = TextSpan.FromBounds(start, end);
                return Location.Create(file.Path, span, sourceText.Lines.GetLinePositionSpan(span));
            }
        }
        return Location.Create(file.Path, TextSpan.FromBounds(0, 0),
            new LinePositionSpan(LinePosition.Zero, LinePosition.Zero));
    }
}
