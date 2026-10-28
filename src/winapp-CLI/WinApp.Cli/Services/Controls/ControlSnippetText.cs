// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Controls;

using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Shared snippet-repair helpers used by every scenario source
/// (<see cref="GalleryProvider"/>, <see cref="ToolkitProvider"/>, …). These used to
/// be copy-pasted per fetcher and had already drifted (only one copy stripped XML
/// comments), so they live here as the single source of truth. Snippets are emitted
/// whole — nothing here shortens a sample. The final safety net for malformed output
/// is <see cref="ScenarioSanitizer"/>, which drops anything these still can't repair.
/// </summary>
internal static partial class ControlSnippetText
{
    /// <summary>
    /// Attributes shaped like an event hookup with a bare method name
    /// (<c>Click="OnCardClicked"</c>), as opposed to a binding (<c>{x:Bind Cmd}</c>), an
    /// attached property (<c>Grid.Row</c>) or an <c>x:</c> directive. Group 1 is the event,
    /// group 2 the method. Event names are an open set, so this matches on shape (WinUI
    /// names events as verbs or past/progressive forms) rather than a list. It is only
    /// used to annotate output, so a stray match costs a reader one sentence.
    /// </summary>
    [GeneratedRegex(
        @"\s(?=[A-Z])((?:[A-Za-z]*?)(?:Click|Focus|Drop|DragOver|Requested|Indeterminate|ed|ing))=""([A-Za-z_]\w*)""")]
    private static partial Regex EventHandlerAttrRegex();

    /// <summary>
    /// Event hookups in <paramref name="xaml"/> whose handler method the emitted
    /// <paramref name="csharp"/> does not declare, as <c>Event="Method"</c> strings. The
    /// attribute is left in the markup — it is what the sample teaches — so callers
    /// disclose these instead: the reader supplies the method.
    /// </summary>
    public static string[] FindUndefinedEventHandlers(string? xaml, string? csharp)
    {
        if (string.IsNullOrEmpty(xaml))
        {
            return [];
        }

        var found = new List<string>();
        foreach (Match m in EventHandlerAttrRegex().Matches(xaml))
        {
            var method = m.Groups[2].Value;
            // Event names are matched on shape, so also require the value to look like a method
            // name (Control_Event, OnEvent, or one that repeats the event) to keep
            // TextWrapping="Wrap" and the like out of the annotation.
            var eventName = m.Groups[1].Value;
            var looksLikeMethod = method.Contains('_')
                || (method.StartsWith("On", StringComparison.Ordinal) && method.Length > 2 && char.IsUpper(method[2]))
                || method.Contains(eventName, StringComparison.Ordinal);
            if (!looksLikeMethod)
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(csharp) &&
                Regex.IsMatch(csharp, $@"\b(?:void|Task)\s+{Regex.Escape(method)}\s*\("))
            {
                continue;
            }
            var entry = $"{m.Groups[1].Value}=\"{method}\"";
            if (!found.Contains(entry))
            {
                found.Add(entry);
            }
        }
        return [.. found];
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

    /// <summary>Extensions that make the placeholder an image rather than a generic asset. The
    /// extension itself is always preserved: a <c>MediaPlayerElement</c> pointed at a .png is a
    /// different kind of broken than one pointed at a file the user has yet to add.</summary>
    private static readonly string[] ImageExtensions =
        [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico"];

    /// <summary>
    /// Name one asset path after the kind of file it pointed at, preserving
    /// <paramref name="prefix"/> verbatim — the forms are not interchangeable, since
    /// <c>AppWindow.SetIcon</c> takes a package-relative path rather than a URI.
    /// </summary>
    /// <remarks>Each source matches its own asset layout — Gallery files sit under
    /// <c>Assets/SampleMedia</c> and <c>Assets/Tiles</c>, Toolkit's directly under
    /// <c>Assets/</c> — but what the replacement is named is one rule, so it lives in one
    /// place and the two cannot drift into labelling the same missing file differently.</remarks>
    public static string AssetPlaceholder(string prefix, string path)
    {
        var extension = Path.GetExtension(path);
        var name = ImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            ? "YourImage"
            : "YourAsset";
        return prefix + name + extension;
    }
}