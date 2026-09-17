// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Controls;

internal static class Synonyms
{
    /// <summary>
    /// Multi-word phrases that should be merged into a single token before
    /// running synonym expansion. Keys must be lowercase. Match is exact-substring.
    /// </summary>
    public static readonly Dictionary<string, string> Phrases = new(StringComparer.OrdinalIgnoreCase)
    {
        // Cross-framework / multi-word terms agents may use
        ["data grid"]              = "datagrid",
        ["data-grid"]              = "datagrid",
        ["pull to refresh"]        = "pulltorefresh",
        ["pull-to-refresh"]        = "pulltorefresh",
        ["infinite scroll"]        = "infinite",
        ["lazy load"]              = "lazy",
        ["lazy loading"]           = "lazy",
        ["floating action button"] = "fab",
        ["context menu"]           = "contextmenu",
        ["right click menu"]       = "contextmenu",
        ["right-click menu"]       = "contextmenu",
        ["nav bar"]                = "navbar",
        ["tab bar"]                = "tabbar",
        ["status bar"]             = "infobar",
        ["text area"]              = "textarea",
        ["text field"]             = "textbox",
        ["text input"]             = "textbox",
        ["date picker"]            = "datepicker",
        ["time picker"]            = "timepicker",
        ["color picker"]           = "colorpicker",
        ["file picker"]            = "filepicker",
        ["folder picker"]          = "filepicker",
        ["info bar"]               = "infobar",
        ["search box"]             = "searchbox",
        ["search bar"]             = "searchbox",
        ["scroll view"]            = "scrollview",
        ["scroll viewer"]          = "scrollviewer",
        ["check box"]              = "checkbox",
        ["radio button"]           = "radio",
        ["radio group"]            = "radiogroup",
        ["combo box"]              = "combobox",
        ["progress bar"]           = "progressbar",
        ["progress ring"]          = "progressring",
        ["loading indicator"]      = "loading",
        ["split button"]           = "splitbutton",
        ["split view"]             = "splitview",
        ["dropdown button"]        = "dropdownbutton",
        ["icon button"]            = "iconbutton",
        ["app bar"]                = "commandbar",
        ["tab view"]               = "tabview",
        ["tree view"]              = "treeview",
        // Accessibility / screen-reader / automation
        ["screen reader"]          = "screenreader",
        ["screen-reader"]          = "screenreader",
        ["automation properties"]  = "automationproperties",
        ["automation property"]    = "automationproperties",
        ["automation id"]          = "automationid",
        ["automation name"]        = "automationproperties",
        ["accessible name"]        = "automationproperties",
        ["alt text"]               = "automationproperties",
        ["aria label"]             = "automationproperties",
        ["aria-label"]             = "automationproperties",
        ["live region"]            = "liveregion",
        ["list view"]              = "listview",
        ["grid view"]              = "gridview",
        // "image grid" / "image gallery" is the natural phrasing for a photo grid, but
        // both words name controls of their own (Image, Grid), so a plain lexical match
        // lands there. Merge the phrase into a token that routes to the collection
        // controls — see the image-grid entries in Map.
        ["image grid"]             = "photogrid",
        ["image gallery"]          = "photogrid",
        ["photo grid"]             = "photogrid",
        ["picture grid"]           = "photogrid",
        ["thumbnail grid"]         = "photogrid",
        ["web view"]               = "webview",
        ["map view"]               = "map",
        ["image view"]             = "image",
        ["video player"]           = "video",
        ["audio player"]           = "audio",
        ["media player"]           = "player",
        ["calendar view"]          = "calendar",
        ["number box"]             = "stepper",
        ["numeric input"]          = "stepper",
        ["password box"]           = "password",
        ["rich text"]              = "richeditbox",
        ["plain text"]             = "textbox",
        ["title bar"]              = "titlebar",
        ["status indicator"]       = "infobadge",
        ["badge count"]            = "badge",
        ["share sheet"]            = "share",
        ["action sheet"]           = "actionsheet",
        ["bottom sheet"]           = "contentdialog",
        ["app notification"]       = "appnotification",
        ["push notification"]      = "appnotification",
        ["system tray icon"]       = "systemtray",
        ["system tray"]            = "systemtray",
        ["jump list"]              = "jumplist",
        ["recent files"]           = "recent",
        ["drag and drop"]          = "dragdrop",
        ["drag drop"]              = "dragdrop",
    };

    /// <summary>
    /// Algorithmic suffix-stripping stemmer for query words. Yields candidate base
    /// forms (without removing the original token from the query). Handles -s, -ed,
    /// -ing, and -able/-ible inflections including doubled-consonant cases (dropped →
    /// drop, dragging → drag) and silent-e cases (closing → close, themed → theme).
    /// The -able/-ible rule matters because agents describe intent adjectivally
    /// ("swipeable rows", "resizable panes") while the corpus indexes the verb.
    ///
    /// We emit multiple candidates per word to avoid false stems (e.g. "scrolled"
    /// stems to BOTH "scroll" and "scrol" — the wrong one matches nothing in the
    /// corpus, the right one matches; BM25 handles the rest). This is much smaller
    /// and easier to maintain than the previous hand-curated 60+ entry Stems dict.
    /// </summary>
    public static IEnumerable<string> Stem(string word)
    {
        if (string.IsNullOrEmpty(word)) yield break;

        if (word.Length > 5 && word.EndsWith("ing", StringComparison.Ordinal))
        {
            var b = word[..^3];                  // editing → edit, scrolling → scroll
            yield return b;
            yield return b + "e";                // closing → close, sharing → share
            if (b.Length >= 2 && b[^1] == b[^2]) // dragging → dragg → drag
                yield return b[..^1];
        }
        else if (word.Length > 4 && word.EndsWith("ed", StringComparison.Ordinal))
        {
            var b = word[..^2];                  // edited → edit, scrolled → scroll
            yield return b;
            yield return word[..^1];             // themed → theme
            if (b.Length >= 2 && b[^1] == b[^2]) // dropped → dropp → drop
                yield return b[..^1];
        }
        else if (word.Length > 7
                 && (word.EndsWith("able", StringComparison.Ordinal) || word.EndsWith("ible", StringComparison.Ordinal)))
        {
            var b = word[..^4];                  // swipeable → swipe, scrollable → scroll
            yield return b;
            yield return b + "e";                // resizable → resize, collapsible → collapse
            if (b.Length >= 2 && b[^1] == b[^2]) // draggable → dragg → drag
                yield return b[..^1];
        }
        else if (word.Length > 3 && word.EndsWith("s", StringComparison.Ordinal)
                 && !word.EndsWith("ss", StringComparison.Ordinal))
        {
            yield return word[..^1];             // tabs → tab, folders → folder
            if (word.Length > 4 && word.EndsWith("es", StringComparison.Ordinal))
                yield return word[..^2];         // boxes → box
        }
    }

    /// <summary>
    /// Manual exceptions to the algorithmic stemmer: words whose base form cannot be
    /// derived by suffix stripping (e.g. "paginated" → "page", not "paginat").
    /// Keep this list small — most morphology is handled by <see cref="Stem"/>.
    /// </summary>
    public static readonly Dictionary<string, string[]> StemExceptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["paginated"] = ["page"],
        ["paginating"] = ["page"],
        ["paging"] = ["page"],
        ["pages"] = ["page"],
        ["tabbed"] = ["tab", "tabs"],   // need both forms; "tabs" is in Map
    };

    /// <summary>
    /// Append single-token equivalents for known multi-word phrases AND base forms
    /// for inflected words, KEEPING the original words. So "file picker" becomes
    /// tokens {file, picker, filepicker} and "tabbed editor" becomes {tabbed, editor,
    /// tab, tabs, edit} — BM25 can match on either form. Synonym expansion still runs
    /// after this step, so "tabs" → "tabview" via Map.
    /// </summary>
    public static string Preprocess(string query)
    {
        var lower = query.ToLowerInvariant();
        var sb = new System.Text.StringBuilder(lower);
        foreach (var (phrase, replacement) in Phrases)
        {
            int searchFrom = 0;
            while (true)
            {
                int idx = lower.IndexOf(phrase, searchFrom, StringComparison.Ordinal);
                if (idx < 0) break;
                searchFrom = idx + phrase.Length;
                // Word-boundary check
                bool leftOk = idx == 0 || !IsWordChar(lower[idx - 1]);
                bool rightOk = searchFrom == lower.Length || !IsWordChar(lower[searchFrom]);
                if (!leftOk || !rightOk) continue;
                // Append the merged token instead of replacing — keeps both forms searchable
                sb.Append(' ').Append(replacement);
            }
        }

        // Stemming: append base forms for inflected query words. Whole-word match on
        // the lowercased original (we don't restem the appended phrase tokens — those
        // are already canonical). Manual exceptions take precedence over the
        // algorithmic stemmer for irregular cases (paginated → page, tabbed → tabs).
        foreach (var word in lower.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (StemExceptions.TryGetValue(word, out var bases))
            {
                foreach (var b in bases) sb.Append(' ').Append(b);
            }
            else
            {
                foreach (var b in Stem(word)) sb.Append(' ').Append(b);
            }
        }

        return sb.ToString();
    }

    private static readonly char[] WordSeparators = [' ', '\t', '\n', '\r', ',', '.', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\''];

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// Maps common cross-platform/cross-framework UI terms to WinUI 3 control names.
    /// Used by SearchEngine to expand queries before BM25 scoring so agents
    /// familiar with HTML/React/WPF/iOS terminology can still find the right controls.
    /// All keys and values are lowercase. Lookup is case-insensitive.
    /// </summary>
    public static readonly Dictionary<string, string[]> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        // ─── Tables / lists ───
        ["datagrid"]        = ["listview", "table", "rows", "columns"],
        ["table"]           = ["listview", "datagrid", "rows", "columns"],
        ["spreadsheet"]     = ["listview", "datagrid", "rows"],

        // ─── Dialogs / popups ───
        ["modal"]           = ["contentdialog", "dialog"],
        ["popup"]           = ["flyout", "dialog", "teachingtip"],
        ["prompt"]          = ["contentdialog"],
        ["actionsheet"]     = ["contentdialog", "menuflyout"],
        ["snackbar"]        = ["infobar", "teachingtip"],
        ["tooltip"]         = ["tooltipservice", "teachingtip"],

        // ─── Navigation ───
        ["sidebar"]         = ["navigationview", "splitview", "navigation"],
        ["navbar"]          = ["navigationview", "menubar"],
        ["drawer"]          = ["navigationview", "splitview"],
        ["breadcrumbs"]     = ["breadcrumbbar"],
        ["crumbs"]          = ["breadcrumbbar"],

        // ─── Tabs ───
        ["tabbar"]          = ["selectorbar", "segmented"],

        // ─── Inputs ───
        ["select"]          = ["combobox"],
        ["picker"]          = ["combobox", "calendarpicker", "datepicker", "timepicker"],
        ["stepper"]         = ["numberbox"],
        ["radiogroup"]      = ["radiobuttons"],
        ["chip"]            = ["tokenizingtextbox", "token"],
        ["chips"]           = ["tokenizingtextbox", "token"],
        ["pill"]            = ["tokenizingtextbox", "segmented"],
        ["searchbox"]       = ["autosuggestbox"],
        ["typeahead"]       = ["autosuggestbox"],
        ["mention"]         = ["richsuggestbox"],
        ["hashtag"]         = ["richsuggestbox"],

        // ─── Text ───
        ["textarea"]        = ["textbox", "richeditbox"],
        ["multiline"]       = ["textbox", "richeditbox"],
        ["wysiwyg"]         = ["richeditbox"],
        ["heading"]         = ["textblock"],
        ["link"]            = ["hyperlinkbutton", "hyperlink"],

        // ─── Layout ───
        ["flex"]            = ["stackpanel", "wrappanel", "grid"],
        ["flexbox"]         = ["stackpanel", "wrappanel"],
        ["container"]       = ["border", "grid", "stackpanel"],
        ["divider"]         = ["appbarseparator", "menubarseparator", "rectangle"],
        ["resizable"]       = ["gridsplitter", "contentsizer"],
        ["wrap"]            = ["wrappanel", "wraplayout"],
        ["masonry"]         = ["staggeredpanel", "staggeredlayout"],

        // ─── Image grids / galleries ───
        // Upstream's own curated keywords already tag ItemsRepeater, GridView and ItemsView
        // with "image"/"gallery"/"grid" (see Data/gallery-tags.json), but "Image" and "Grid"
        // are controls in their own right, so an exact control-name match outranks those tags
        // and a photo-grid query lands on the single-image and layout-panel samples instead.
        // Routing the vocabulary here is what puts the collection controls in front of the
        // ranker; the samples it reaches are upstream's.
        ["photo"]           = ["itemsrepeater", "gridview", "itemsview"],
        ["photos"]          = ["itemsrepeater", "gridview", "itemsview"],
        ["gallery"]         = ["itemsrepeater", "gridview", "itemsview"],
        ["uniformgrid"]     = ["itemsrepeater", "gridview", "itemsview"],
        ["photogrid"]       = ["itemsrepeater", "gridview", "itemsview"],

        // ─── Scrolling / virtualization ───
        ["scrollview"]      = ["scrollviewer"],
        ["lazy"]            = ["listview", "itemsrepeater"],
        ["infinite"]        = ["incrementalloadingcollection"],

        // ─── Gestures ───
        ["swipe"]           = ["swipecontrol", "swipeitem"],

        // ─── Tree / hierarchy ───
        ["folder"]          = ["treeview"],
        ["hierarchy"]       = ["treeview"],
        ["outline"]         = ["treeview"],

        // ─── Progress / loading ───
        ["loading"]         = ["progressring", "progressbar"],
        ["loader"]          = ["progressring", "progressbar"],

        // ─── Commands / buttons ───
        ["fab"]             = ["button"],
        ["iconbutton"]      = ["button", "appbarbutton"],
        ["ribbon"]          = ["tabbedcommandbar"],
        ["contextmenu"]     = ["menuflyout"],
        ["rightclick"]      = ["menuflyout"],

        // ─── Media ───
        ["audio"]           = ["mediaplayerelement"],
        ["iframe"]          = ["webview2"],
        ["cropping"]        = ["imagecropper"],
        // A thumbnail is usually one cell of a grid, so keep Image but also offer the
        // controls that lay them out — see the image-grid note above.
        ["thumbnail"]       = ["image", "itemsrepeater", "gridview", "itemsview"],
        ["thumbnails"]      = ["image", "itemsrepeater", "gridview", "itemsview"],

        // ─── Date / time / color ───

        // ─── Settings / forms ───
        ["form"]            = ["settingscard", "stackpanel"],
        ["preferences"]     = ["settingscard", "settingsexpander"],
        ["options"]         = ["settingscard", "combobox", "radiobuttons"],

        // ─── System / shell ───
        ["taskbar"]         = ["jumplist", "appbadge"],
        ["recent"]          = ["jumplist"],
        ["tray"]            = ["systemtray"],
        ["systemtray"]      = ["system", "tray", "icon", "minimize"],
        ["share"]           = ["sharecontract"],
        ["filepicker"]      = ["file", "picker"],
        ["openfile"]        = ["filepicker"],
        ["savefile"]        = ["filepicker"],
        ["dragdrop"]        = ["drag", "drop"],

        // ─── Abbreviations ───
        ["btn"]             = ["button"],
        ["txt"]             = ["textbox", "textblock"],
        ["img"]             = ["image"],
        ["lv"]              = ["listview"],
        ["gv"]              = ["gridview"],

        // ─── Plurals → singular alias to actual control ───
        ["buttons"]         = ["button"],
        ["lists"]           = ["listview"],
        ["dialogs"]         = ["contentdialog"],

        // ─── Accessibility / screen reader / automation ───
        // Maps the various ways an agent may phrase a11y needs to the gallery
        // sample controls that demonstrate them. Both `accessibilityscreenreader`
        // (the controlId, indexed as one token) and `screenreader` / `accessibility`
        // (separate tag tokens after CamelCase split) are useful targets.
        ["a11y"]                 = ["accessibility", "screenreader", "accessibilityscreenreader"],
        ["accessibility"]        = ["accessibilityscreenreader", "accessibilitykeyboard", "screenreader", "accessible"],
        ["accessible"]           = ["accessibility", "screenreader", "accessibilityscreenreader"],
        ["screenreader"]         = ["accessibilityscreenreader", "accessibility", "screen", "reader"],
        ["narrator"]             = ["accessibilityscreenreader", "screenreader", "accessibility"],
        ["automationproperties"] = ["accessibilityscreenreader", "screenreader", "accessibility", "automation", "name"],
        ["automationid"]         = ["accessibilityscreenreader", "screenreader", "accessibility", "automation"],
        ["liveregion"]           = ["accessibilityscreenreader", "screenreader", "accessibility"],
    };

    /// <summary>
    /// Expand a tokenized query by appending synonyms for each known term.
    /// Returns the original tokens plus any synonym tokens.
    ///
    /// Compound-suffix guard: if the query already contains a more-specific compound
    /// ending with the current token (e.g. "file picker" → tokens [file, picker, filepicker]),
    /// we skip synonym expansion for the bare token. The compound form already pins
    /// the user's intent; expanding the bare suffix would pull in unrelated controls
    /// (e.g. "picker" → ComboBox/DatePicker/TimePicker, which all dilute "file picker"
    /// when the user clearly wants the file picker pattern).
    /// </summary>
    public static string[] Expand(string[] queryWords)
    {
        var result = new List<string>(queryWords);
        var seen = new HashSet<string>(queryWords, StringComparer.OrdinalIgnoreCase);
        var queryWordSet = new HashSet<string>(queryWords, StringComparer.OrdinalIgnoreCase);
        foreach (var w in queryWords)
        {
            if (Map.TryGetValue(w, out var syns))
            {
                bool hasCompoundParent = queryWordSet.Any(other =>
                    other.Length > w.Length && other.EndsWith(w, StringComparison.OrdinalIgnoreCase));
                if (hasCompoundParent) continue;

                foreach (var s in syns)
                {
                    if (seen.Add(s)) result.Add(s);
                }
            }
        }
        return result.ToArray();
    }
}
