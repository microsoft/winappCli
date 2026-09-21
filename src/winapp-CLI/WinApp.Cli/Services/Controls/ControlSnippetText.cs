// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Controls;

using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Shared snippet-repair helpers used by every scenario fetcher
/// (<see cref="GalleryFetcher"/>, <see cref="ToolkitFetcher"/>, …). These used to
/// be copy-pasted per fetcher and had already drifted (only one copy stripped XML
/// comments), so they live here as the single source of truth. Snippets are emitted
/// whole — nothing here shortens a sample. The final safety net for malformed output
/// is <see cref="ScenarioSanitizer"/>, which drops anything these still can't repair.
/// </summary>
internal static partial class ControlSnippetText
{
    /// <summary>
    /// Event-handler attributes whose value is a bare method name (e.g.
    /// <c>Click="OnCardClicked"</c>) — as opposed to a command binding
    /// (<c>Click="{x:Bind Cmd}"</c>), which starts with '{' and is never matched.
    /// Group 2 is the referenced method name. The list covers the routed/typed
    /// events that appear across the Gallery + Toolkit corpora.
    /// </summary>
    [GeneratedRegex(
        @"\s+(?:Click|Tapped|DoubleTapped|RightTapped|Holding|Checked|Unchecked|Indeterminate|Toggled|" +
        @"SelectionChanged|TextChanged|ValueChanged|Loaded|Unloaded|Loading|SizeChanged|" +
        @"GotFocus|LostFocus|PointerEntered|PointerExited|PointerPressed|PointerReleased|PointerMoved|" +
        @"ItemClick|ItemInvoked|Invoked|Expanding|Collapsed|Opened|Closed|Closing|QuerySubmitted|" +
        @"SuggestionChosen|TextSubmitted|DragItemsStarting|DropCompleted|ContextRequested|Completed)" +
        @"=""([A-Za-z_]\w*)""")]
    private static partial Regex EventHandlerAttrRegex();

    /// <summary>
    /// Remove event-handler attributes from <paramref name="xaml"/> whose referenced
    /// method is NOT defined in the emitted <paramref name="csharp"/> — so a pasted
    /// snippet never wires an event to a handler that isn't there (a XAML-compiler
    /// "handler not found" error). Handlers whose method IS present in the C# are kept
    /// (e.g. TabView's Add/Close handlers), as are command bindings (<c>{x:Bind}</c>).
    /// When <paramref name="csharp"/> is null/empty, every bare-method handler is
    /// stripped. This is the corpus-boundary mitigation for the upstream "handler lives
    /// in shared page code-behind we don't fetch" gap (see issues #703 / #704).
    /// </summary>
    public static string StripUnbackedEventHandlers(string xaml, string? csharp)
    {
        if (string.IsNullOrEmpty(xaml))
        {
            return xaml;
        }

        return EventHandlerAttrRegex().Replace(xaml, m =>
        {
            var method = m.Groups[1].Value;
            // Keep the handler only if the emitted C# actually declares the method
            // (a `void`/`Task` declaration by that name — not merely a call to it).
            if (!string.IsNullOrWhiteSpace(csharp) &&
                Regex.IsMatch(csharp, $@"\b(?:void|Task)\s+{Regex.Escape(method)}\s*\("))
            {
                return m.Value;
            }
            return "";
        });
    }

    /// <summary>Matches a single XML start/end/self-closing tag: group 1 = leading
    /// <c>/</c> (end tag), group 2 = (possibly prefixed) element name, group 4 =
    /// trailing <c>/</c> (self-closing).</summary>
    [GeneratedRegex(@"<(/?)([A-Za-z_][\w:.\-]*)\b([^>]*?)(/?)>")]
    private static partial Regex AnyTagRegex();

    /// <summary>Strips XML comments (including a trailing unterminated one) so generic-type
    /// text like <c>ObservableCollection&lt;T&gt;</c> inside a comment isn't mistaken for a
    /// real element.</summary>
    [GeneratedRegex(@"<!--[\s\S]*?(?:-->|$)")]
    private static partial Regex XmlCommentRegex();


    /// <summary>Append closing tags for any elements the snippet leaves open, so a
    /// fragment that cleaning left unbalanced still parses. Comments are ignored when
    /// counting tags so text inside them can't inject bogus closers. Nothing is removed:
    /// the snippet is emitted at its full upstream length.</summary>
    public static string CloseUnbalancedTags(string xaml)
    {
        // Count open/close tags. Ignore anything inside XML comments so that generic-type
        // text like "ObservableCollection<CustomDataObject>" inside an explanatory
        // <!-- ... --> comment isn't mistaken for a real element (which would otherwise
        // append a bogus </CustomDataObject>).
        var scanText = XmlCommentRegex().Replace(xaml, "");
        var stack = new Stack<string>();
        bool sawMismatch = false;
        foreach (Match m in AnyTagRegex().Matches(scanText))
        {
            bool isClose = m.Groups[1].Value == "/";
            bool isSelf = m.Groups[4].Value == "/";
            string name = m.Groups[2].Value;
            if (isSelf) continue;
            if (isClose)
            {
                if (stack.Count > 0 && stack.Peek() == name) stack.Pop();
                else sawMismatch = true;
            }
            else
            {
                stack.Push(name);
            }
        }

        if (stack.Count == 0 && !sawMismatch) return xaml;

        var sb = new StringBuilder(xaml.TrimEnd());
        while (stack.Count > 0) sb.Append("</").Append(stack.Pop()).Append('>');
        return sb.ToString();
    }
}