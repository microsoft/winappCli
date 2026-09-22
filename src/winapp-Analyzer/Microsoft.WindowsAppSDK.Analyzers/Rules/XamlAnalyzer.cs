// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
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
        "Nested x:Bind path '{0}' has no FallbackValue — if an intermediate segment is null the target may render empty or not update; add FallbackValue or bind a flat ViewModel property to make the null case explicit",
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

    /// <summary>
    /// Framework event names used when the element type is unavailable from the compilation.
    /// Custom and third-party events are identified from their Roslyn symbols.
    /// </summary>
    private static readonly HashSet<string> EventAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Click", "Tapped", "DoubleTapped", "RightTapped", "Holding",
        "ContextRequested", "ContextCanceled",
        "PointerPressed", "PointerReleased", "PointerMoved", "PointerEntered", "PointerExited",
        "PointerCanceled", "PointerCaptureLost", "PointerWheelChanged",
        "KeyDown", "KeyUp", "PreviewKeyDown", "PreviewKeyUp", "CharacterReceived",
        "GotFocus", "LostFocus", "GettingFocus", "LosingFocus",
        "Loaded", "Unloaded", "Loading", "SizeChanged", "LayoutUpdated",
        "DragStarting", "DropCompleted", "Drop", "DragOver", "DragEnter", "DragLeave",
        "DragItemsStarting", "DragItemsCompleted",
        "ManipulationStarting", "ManipulationStarted", "ManipulationDelta",
        "ManipulationInertiaStarting", "ManipulationCompleted",
        "SelectionChanged", "TextChanged", "TextChanging", "BeforeTextChanging", "PasswordChanged",
        "QuerySubmitted", "TextSubmitted", "SuggestionChosen",
        "Checked", "Unchecked", "Indeterminate", "Toggled",
        "ValueChanged", "DateChanged", "TimeChanged", "ItemClick", "ItemInvoked", "Expanding", "Collapsed",
        "Navigated", "Navigating", "NavigationFailed", "NavigationStopped",
        "Opened", "Closed", "Opening", "Closing"
    };

    private const string PresentationNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private const string XamlLanguageNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
    private const string UsingNamespacePrefix = "using:";

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

                foreach (var bindExpr in FindXBindExpressions(value))
                {
                    var (bindPath, bindArgs) = ParseBinding(bindExpr);

                    var attrName = attr.Name.LocalName;
                    var isEvent = IsEventBindingTarget(context.Compilation, element, attrName);
                    var isCommand = attrName == "Command" || attrName.EndsWith("Command", StringComparison.Ordinal);
                    var hasExplicitMode = bindArgs.ContainsKey("Mode");
                    var hasConverter = bindArgs.ContainsKey("Converter");

                    // WUI2011 (missing Mode) applies to property bindings only. Skip events,
                    // commands and converter bindings, honor an explicit Mode= (any whitespace),
                    // and honor the nearest inherited x:DefaultBindMode.
                    if (!hasExplicitMode && !hasConverter && !isCommand && !isEvent &&
                        !HasInheritedDefaultBindMode(element))
                    {
                        var location = CreateLocation(file, sourceText, element);
                        context.ReportDiagnostic(Diagnostic.Create(XBindNoModeRule, location));
                    }

                    if (!bindPath.Contains("("))
                    {
                        var segments = bindPath.Split('.');
                        if (segments.Length >= 3 && !bindArgs.ContainsKey("FallbackValue"))
                        {
                            var location = CreateLocation(file, sourceText, element);
                            context.ReportDiagnostic(Diagnostic.Create(NestedXBindRule, location, bindPath));
                        }
                    }
                }
            }
        }
    }

    private static bool HasInheritedDefaultBindMode(XElement element)
    {
        for (var e = element; e != null; e = e.Parent)
        {
            if (e.Attributes().Any(a =>
                a.Name.LocalName == "DefaultBindMode" &&
                a.Name.NamespaceName == XamlLanguageNamespace))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsEventBindingTarget(
        Compilation compilation,
        XElement element,
        string attributeName)
    {
        var resolvedElementType = false;
        foreach (var resolvedType in GetElementTypeNames(element)
            .Select(typeName => compilation.GetTypeByMetadataName(typeName)))
        {
            if (resolvedType == null)
            {
                continue;
            }

            resolvedElementType = true;
            for (var type = resolvedType; type != null; type = type.BaseType)
            {
                if (type.GetMembers(attributeName).Any(member => member.Kind == SymbolKind.Event))
                {
                    return true;
                }
            }
        }

        return !resolvedElementType && EventAttributes.Contains(attributeName);
    }

    private static IEnumerable<string> FindXBindExpressions(string value)
    {
        const string prefix = "{x:Bind";
        var searchStart = 0;

        while (searchStart < value.Length)
        {
            var bindStart = value.IndexOf(prefix, searchStart, StringComparison.Ordinal);
            if (bindStart < 0)
            {
                yield break;
            }

            var expressionStart = bindStart + prefix.Length;
            if (expressionStart >= value.Length || !char.IsWhiteSpace(value[expressionStart]))
            {
                searchStart = expressionStart;
                continue;
            }

            while (expressionStart < value.Length && char.IsWhiteSpace(value[expressionStart]))
            {
                expressionStart++;
            }

            var braceDepth = 1;
            var quote = '\0';
            for (var i = expressionStart; i < value.Length; i++)
            {
                var c = value[i];
                var escaped = IsCaretEscaped(value, i);
                if (quote != '\0')
                {
                    quote = c == quote && !escaped ? '\0' : quote;
                }
                else if ((c == '\'' || c == '"') && !escaped)
                {
                    quote = c;
                }
                else if (c == '{' && !escaped)
                {
                    braceDepth++;
                }
                else if (c == '}' && !escaped && --braceDepth == 0)
                {
                    yield return value.Substring(expressionStart, i - expressionStart).Trim();
                    searchStart = i + 1;
                    break;
                }
            }

            if (braceDepth > 0)
            {
                yield break;
            }
        }
    }

    private static IEnumerable<string> GetElementTypeNames(XElement element)
    {
        var namespaceName = element.Name.NamespaceName;
        var localName = element.Name.LocalName;

        if (namespaceName.StartsWith(UsingNamespacePrefix, StringComparison.Ordinal))
        {
            yield return namespaceName.Substring(UsingNamespacePrefix.Length) + "." + localName;
        }
        else if (namespaceName == PresentationNamespace)
        {
            yield return "Microsoft.UI.Xaml.Controls." + localName;
            yield return "Microsoft.UI.Xaml.Controls.Primitives." + localName;
            yield return "Microsoft.UI.Xaml." + localName;
            yield return "Windows.UI.Xaml.Controls." + localName;
            yield return "Windows.UI.Xaml.Controls.Primitives." + localName;
            yield return "Windows.UI.Xaml." + localName;
        }
    }

    /// <summary>
    /// Splits an <c>{x:Bind}</c> expression into its path and named arguments, tolerant of any
    /// whitespace around <c>,</c> and <c>=</c> (so <c>Mode = OneWay</c> is recognized). Commas
    /// inside function-call parentheses or nested markup extensions are not treated as separators.
    /// </summary>
    private static (string path, Dictionary<string, string> args) ParseBinding(string bindExpr)
    {
        var parts = SplitTopLevel(bindExpr);
        var path = string.Empty;
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var trimmedPart in parts.Select(part => part.Trim()))
        {
            var eq = IndexOfTopLevelEquals(trimmedPart);
            if (eq > 0)
            {
                var key = trimmedPart.Substring(0, eq).Trim();
                var value = trimmedPart.Substring(eq + 1).Trim();
                args[key] = value;
                if (key.Equals("Path", StringComparison.OrdinalIgnoreCase))
                {
                    path = value;
                }
            }
            else if (path.Length == 0)
            {
                path = trimmedPart;
            }
        }
        return (path, args);
    }

    private static List<string> SplitTopLevel(string expr)
    {
        var result = new List<string>();
        var parenthesisDepth = 0;
        var braceDepth = 0;
        var start = 0;
        var quote = '\0';
        for (var i = 0; i < expr.Length; i++)
        {
            var c = expr[i];
            var escaped = IsCaretEscaped(expr, i);
            if (quote != '\0')
            {
                quote = c == quote && !escaped ? '\0' : quote;
            }
            else if ((c == '\'' || c == '"') && !escaped)
            {
                quote = c;
            }
            else if (c == '(' && !escaped)
            {
                parenthesisDepth++;
            }
            else if (c == ')' && !escaped && parenthesisDepth > 0)
            {
                parenthesisDepth--;
            }
            else if (c == '{' && !escaped)
            {
                braceDepth++;
            }
            else if (c == '}' && !escaped && braceDepth > 0)
            {
                braceDepth--;
            }
            else if (c == ',' && !escaped && parenthesisDepth == 0 && braceDepth == 0)
            {
                result.Add(expr.Substring(start, i - start));
                start = i + 1;
            }
        }
        result.Add(expr.Substring(start));
        return result;
    }

    private static int IndexOfTopLevelEquals(string value)
    {
        var parenthesisDepth = 0;
        var braceDepth = 0;
        var quote = '\0';
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var escaped = IsCaretEscaped(value, i);
            if (quote != '\0')
            {
                quote = c == quote && !escaped ? '\0' : quote;
            }
            else if ((c == '\'' || c == '"') && !escaped)
            {
                quote = c;
            }
            else if (c == '(' && !escaped)
            {
                parenthesisDepth++;
            }
            else if (c == ')' && !escaped && parenthesisDepth > 0)
            {
                parenthesisDepth--;
            }
            else if (c == '{' && !escaped)
            {
                braceDepth++;
            }
            else if (c == '}' && !escaped && braceDepth > 0)
            {
                braceDepth--;
            }
            else if (c == '=' && !escaped && parenthesisDepth == 0 && braceDepth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsCaretEscaped(string value, int index)
    {
        var caretCount = 0;
        for (var i = index - 1; i >= 0 && value[i] == '^'; i--)
        {
            caretCount++;
        }
        return caretCount % 2 != 0;
    }

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
