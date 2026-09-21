// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Controls;

using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Shared snippet-truncation helpers used by every scenario fetcher
/// (<see cref="ToolkitFetcher"/>, …). These used to
/// be copy-pasted per fetcher and had already drifted (only one copy stripped XML
/// comments; only one balanced C# braces), so they live here as the single source
/// of truth. The final safety net for malformed output is
/// <see cref="ScenarioSanitizer"/>, which drops anything these still can't repair.
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

    /// <summary>Strips XML comments (including a trailing unterminated one left by a
    /// truncation cut) so generic-type text like <c>ObservableCollection&lt;T&gt;</c>
    /// inside a comment isn't mistaken for a real element.</summary>
    [GeneratedRegex(@"<!--[\s\S]*?(?:-->|$)")]
    private static partial Regex XmlCommentRegex();

    /// <summary>Truncate XAML at a safe <c>&gt;</c> boundary, appending closing tags
    /// for any elements left open. Comments are ignored when counting tags so text
    /// inside them can't inject bogus closers.</summary>
    public static string TruncateXaml(string xaml, int maxChars)
    {
        bool needsTruncate = xaml.Length > maxChars;
        string head;
        if (needsTruncate)
        {
            // Find a safe '>' boundary
            int cut = maxChars;
            while (cut > 0)
            {
                cut = xaml.LastIndexOf('>', cut - 1);
                if (cut < 0) return "";
                cut += 1;
                int lastLt = xaml.LastIndexOf('<', cut - 1);
                int lastGt = xaml.LastIndexOf('>', cut - 1);
                if (lastLt < lastGt) break;
                cut = lastLt;
            }
            if (cut <= 0) return "";
            head = xaml.Substring(0, cut);
        }
        else
        {
            head = xaml;
        }

        // Count open/close tags. Ignore anything inside XML comments so that generic-type
        // text like "ObservableCollection<CustomDataObject>" inside an explanatory
        // <!-- ... --> comment isn't mistaken for a real element (which would otherwise
        // append a bogus </CustomDataObject>). A trailing unterminated comment (possible
        // after a truncation cut) is stripped to end-of-string too.
        var scanText = XmlCommentRegex().Replace(head, "");
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

        // If balanced and not truncated, return original
        if (!needsTruncate && stack.Count == 0 && !sawMismatch) return xaml;

        var sb = new StringBuilder(head.TrimEnd());
        while (stack.Count > 0) sb.Append("</").Append(stack.Pop()).Append('>');
        if (needsTruncate) sb.Append("\n<!-- ...truncated -->");
        return sb.ToString();
    }

    /// <summary>Truncate C# code at a brace-balanced boundary.
    /// Walks forward tracking depth (skipping strings/chars/comments/verbatim) and
    /// prefers the most recent depth=0 cut. When none exists in the prefix, cuts at
    /// the last newline and appends synthetic <c>}</c> braces equal to the open depth
    /// so agents can copy the snippet without the build breaking on unbalanced braces.</summary>
    public static string TruncateCode(string code, int maxChars, string marker)
    {
        if (code.Length <= maxChars) return code;

        if (code.Contains('{'))
        {
            int depth = 0, lastZeroPos = -1, finalDepth = 0;
            bool inStr = false, inChr = false, inLine = false, inBlk = false, inVerb = false;
            int lastBeforeMax = 0;
            // Track the last newline boundary reached at a known brace depth, so that
            // when we have to cut back to a line we append exactly the closers that
            // line needs — measured AT the cut, not at maxChars (braces between the cut
            // and maxChars would otherwise skew the closer count and emit broken C#).
            int safeCutPos = 0, depthAtSafeCut = 0;
            for (int i = 0; i < code.Length && i < maxChars; i++)
            {
                char c = code[i]; char prev = i > 0 ? code[i - 1] : '\0';
                if (inLine) { if (c == '\n') { inLine = false; safeCutPos = i + 1; depthAtSafeCut = depth; } continue; }
                if (inBlk)  { if (c == '/' && prev == '*') inBlk = false; continue; }
                if (inStr)
                {
                    if (inVerb) { if (c == '"' && (i + 1 >= code.Length || code[i + 1] != '"')) { inStr = false; inVerb = false; } else if (c == '"') i++; }
                    else if (c == '"' && prev != '\\') inStr = false;
                    continue;
                }
                if (inChr) { if (c == '\'' && prev != '\\') inChr = false; continue; }
                if (c == '/' && i + 1 < code.Length && code[i + 1] == '/') { inLine = true; continue; }
                if (c == '/' && i + 1 < code.Length && code[i + 1] == '*') { inBlk = true; continue; }
                if (c == '@' && i + 1 < code.Length && code[i + 1] == '"') { inStr = true; inVerb = true; i++; continue; }
                if (c == '"') { inStr = true; continue; }
                if (c == '\'') { inChr = true; continue; }
                if (c == '{') depth++;
                else if (c == '}') { depth--; if (depth == 0) lastZeroPos = i + 1; }
                else if (c == '\n') { safeCutPos = i + 1; depthAtSafeCut = depth; }
                lastBeforeMax = i + 1;
                finalDepth = depth;
            }
            if (lastZeroPos > 0)
                return code.Substring(0, lastZeroPos).TrimEnd() + "\n" + marker;
            // No clean top-level close within the cap. Prefer cutting at the last
            // recorded newline boundary and closing to the depth measured there.
            if (safeCutPos > 0)
            {
                var prefixSafe = code.Substring(0, safeCutPos).TrimEnd();
                var closersSafe = depthAtSafeCut > 0 ? "\n" + new string('}', depthAtSafeCut) : "";
                return prefixSafe + closersSafe + "\n" + marker;
            }
            // No newline boundary at all (single very long line): fall back to the raw
            // prefix with the depth measured at that same cut point.
            var prefix = code.Substring(0, Math.Min(lastBeforeMax, code.Length)).TrimEnd();
            var closers = finalDepth > 0 ? "\n" + new string('}', finalDepth) : "";
            return prefix + closers + "\n" + marker;
        }

        int lineCut = code.LastIndexOf('\n', maxChars - 1);
        if (lineCut < 0) lineCut = maxChars;
        return code.Substring(0, lineCut).TrimEnd() + "\n" + marker;
    }
}
