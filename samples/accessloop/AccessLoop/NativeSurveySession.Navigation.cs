using System.Text.Json;

namespace AccessLoop;

public sealed partial class NativeSurveySession
{
    private const int MaxNavigationNodes = 2000;
    private const int MaxSearchResults = 100;

    private static AuditImageRect? ElementBounds(JsonElement element)
    {
        var values = new List<double>(4);
        foreach (var field in new[] { "x", "y", "width", "height" })
        {
            if (!element.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.Number
                || !value.TryGetDouble(out var number) || !double.IsFinite(number) || Math.Abs(number) > 10_000_000)
                return null;
            values.Add(number);
        }
        return values[2] > 0 && values[3] > 0 ? new(values[0], values[1], values[2], values[3]) : null;
    }

    private sealed record NavigationNode(
        string? Id, string? Name, string? ClassName, string Type, string? Selector,
        bool Enabled, bool Offscreen, bool Invokable, bool? Expanded, NavigationNode? Parent)
    {
        public bool IsUnder(NavigationNode root)
        {
            for (var parent = Parent; parent is not null; parent = parent.Parent)
                if (ReferenceEquals(parent, root))
                    return true;
            return false;
        }
    }

    private sealed class NavigationTree
    {
        public IReadOnlyList<NavigationNode> Items { get; }
        public IReadOnlyList<SurveyPageDefinition> Pages { get; }
        public IReadOnlyList<SurveyExcluded> Excluded { get; }
        public IReadOnlyDictionary<string, bool> Groups { get; }
        public AuditImageRect? PaneBounds { get; }
        private readonly Dictionary<string, NavigationNode> byId;

        public NavigationTree(JsonElement response, SurveyProfile profile, AuditTarget target)
        {
            var windows = Array(response, "windows");
            Require(windows.GetArrayLength() == 1 && Integer(windows[0], "hwnd") == target.WindowHandle,
                "Navigation inspection must contain exactly the originally selected window.");
            if (Present(windows[0], "processId"))
                Require(Integer(windows[0], "processId") == target.ProcessId, "Navigation inspection belongs to a different process.");
            Require(Bool(response, "interactive") != true && Bool(response, "hideDisabled") != true
                && Bool(response, "hideOffscreen") != true, "Filtered navigation cannot establish scope.");
            var roots = Array(windows[0], "elements");
            Require(roots.GetArrayLength() == 1 && Text(roots[0], "automationId") == profile.NavigationPaneId,
                $"Inspection did not resolve the exact '{profile.NavigationPaneId}' root; a whole-window fallback is refused.");
            PaneBounds = ElementBounds(roots[0]);
            var nodes = new List<NavigationNode>();
            Add(roots[0], null, 0);
            if (windows[0].TryGetProperty("elementCount", out _))
                Require(Integer(windows[0], "elementCount") == nodes.Count, "The navigation node count is inconsistent.");
            var menu = Exact(profile.MenuRootId);
            Require(menu.IsUnder(nodes[0]), "The menu must be a descendant of the approved navigation pane.");
            _ = Exact(profile.NavigationPaneId);
            Items = nodes.Where(node => node.ClassName == profile.ItemClassName).ToArray();
            byId = new(StringComparer.Ordinal);
            foreach (var item in Items)
            {
                Require(IsIdentifier(item.Id) && item.Type == "ListItem" && !string.IsNullOrWhiteSpace(item.Name)
                    && item.Name.Length <= 256 && !item.Name.Any(char.IsControl)
                    && SafeSelector(item.Selector), "A navigation entry has a missing or malformed ID, name, type or selector.");
                _ = Exact(item.Id!);
                Require(nodes.Count(node => node.Selector == item.Selector) == 1, "A navigation selector is ambiguous.");
                Require(byId.TryAdd(item.Id!, item), "Duplicate navigation IDs are not safe to invoke.");
                if (!item.IsUnder(menu) || profile.ExcludedIds.Contains(item.Id!, StringComparer.Ordinal))
                    continue;
                if (item.Expanded is not null)
                    Require(profile.GroupIds.Contains(item.Id!, StringComparer.Ordinal),
                        $"Unexpected navigation group '{item.Id}' changes the approved scope.");
                for (var parent = item.Parent; parent is not null; parent = parent.Parent)
                {
                    if (parent.ClassName == profile.ItemClassName)
                        Require(parent.Expanded is not null && profile.GroupIds.Contains(parent.Id!, StringComparer.Ordinal),
                            $"Navigation '{item.Id}' has an undeclared or non-group navigation ancestor.");
                }

            }
            foreach (var listItem in nodes.Where(node => node.IsUnder(menu) && node.Type == "ListItem"))
                Require(listItem.ClassName == profile.ItemClassName, "An unexpected ListItem class appeared in the navigation menu.");
            var groups = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var id in profile.GroupIds)
            {
                Require(byId.TryGetValue(id, out var group) && group.IsUnder(menu) && group.Expanded is not null,
                    $"Mandatory navigation group '{id}' is missing or is no longer a supported group.");
                groups.Add(id, group!.Expanded!.Value);
            }
            Groups = groups;
            Pages = Items.Where(node => node.IsUnder(menu) && node.Expanded is null
                    && !profile.ExcludedIds.Contains(node.Id!, StringComparer.Ordinal))
                .Select(node => new SurveyPageDefinition(node.Id!, node.Name!, Ancestors(node, profile))).ToArray();
            Require(Pages.Count <= profile.MaxPages, "The navigation page budget was exceeded.");
            foreach (var page in Pages)
                Require(byId[page.Id].Invokable, $"Navigation '{page.Id}' has no supported invokable pattern.");
            Excluded = Items.Where(node => !node.IsUnder(menu) || profile.ExcludedIds.Contains(node.Id!, StringComparer.Ordinal))
                .Select(node => new SurveyExcluded(node.Id!, node.Name!, profile.ExcludedIds.Contains(node.Id!, StringComparer.Ordinal)
                    ? "Excluded by the navigation profile." : "Outside the approved navigation menu.")).ToArray();

            NavigationNode Exact(string id)
            {
                var matches = nodes.Where(node => node.Id == id).ToArray();
                Require(matches.Length == 1, $"Expected one exact navigation AutomationId '{id}'; found {matches.Length}.");
                return matches[0];
            }

            void Add(JsonElement value, NavigationNode? parent, int depth)
            {
                Require(depth <= profile.TreeDepth && nodes.Count < MaxNavigationNodes && Bool(value, "hasMoreChildren") != true,
                    "The navigation tree is truncated or exceeds its depth/node budget.");
                var type = RequiredText(value, "type");
                // WinUI SplitViewPane exposes Window as its UIA role without being a separate HWND.
                Require(type != "Window" || (parent is null && Text(value, "automationId") == profile.NavigationPaneId),
                    "An additional window appeared inside the navigation pane.");
                if (value.TryGetProperty("windowHandle", out var handle) && handle.ValueKind != JsonValueKind.Null)
                    Require(Integer(value, "windowHandle") == target.WindowHandle, "A navigation node belongs to a different window.");
                var navigation = Text(value, "className") == profile.ItemClassName;
                bool? expanded = null;
                if (navigation)
                {
                    Require(!Present(value, "toggleState") && !Present(value, "value"), "Setting toggle/value controls are not navigation entries.");
                    expanded = Text(value, "expandState") switch
                    {
                        null => null,
                        "expanded" => true,
                        "collapsed" => false,
                        _ => throw Blocked("profile-mismatch", "A navigation group has an unknown expansion state.", false)
                    };
                    Require(Bool(value, "isEnabled") is not null && Bool(value, "isOffscreen") is not null,
                        "Navigation visibility or enabled state is unknown.");
                }
                var node = new NavigationNode(Text(value, "automationId"), navigation ? Text(value, "name") : null,
                    Text(value, "className"), type, Text(value, "selector"), Bool(value, "isEnabled") == true,
                    Bool(value, "isOffscreen") != false, Bool(value, "isInvokable") == true, expanded, parent);
                nodes.Add(node);
                if (Present(value, "children"))
                    foreach (var child in Array(value, "children").EnumerateArray())
                        Add(child, node, depth + 1);
            }
        }

        public NavigationNode Leaf(SurveyPageDefinition page)
        {
            Require(Pages.Any(current => SamePage(current, page)), $"Page '{page.Id}' is not the same approved leaf under the menu.");
            return byId[page.Id];
        }

        public NavigationNode Group(string id)
        {
            Require(Groups.ContainsKey(id), $"'{id}' is not an approved navigation group.");
            return byId[id];
        }

        public bool HiddenByGroup(SurveyPageDefinition page) => page.GroupIds.Any(id => !Groups[id]);

        private static IReadOnlyList<string> Ancestors(NavigationNode node, SurveyProfile profile)
        {
            var path = new List<string>();
            for (var parent = node.Parent; parent is not null; parent = parent.Parent)
                if (parent.ClassName == profile.ItemClassName)
                    path.Add(parent.Id!);
            path.Reverse();
            return path.ToArray();
        }
    }

    private static bool SamePage(SurveyPageDefinition left, SurveyPageDefinition right) =>
        left.Id == right.Id && left.Name == right.Name && left.GroupIds.SequenceEqual(right.GroupIds, StringComparer.Ordinal);

    private void MatchInventory(NavigationTree tree, bool complete)
    {
        if (inventory is null)
            return;
        foreach (var page in tree.Pages)
            Require(approvedPages.TryGetValue(page.Id, out var approved) && SamePage(page, approved),
                $"Page '{page.Id}' is new or changed from the frozen inventory.");
        foreach (var page in inventory.Pages)
            Require(tree.Pages.Any(current => SamePage(page, current)) || (!complete && tree.HiddenByGroup(page)),
                $"Frozen page '{page.Id}' is missing; resume cannot silently add or drop pages.");
        var observed = tree.Items.Select(node => node.Id!).ToHashSet(StringComparer.Ordinal);
        Require(observed.IsSubsetOf(inventory.NavigationIds), "Navigation IDs changed from the frozen inventory.");
        if (complete)
        {
            Require(observed.SetEquals(inventory.NavigationIds), "The complete navigation ID inventory changed.");
            Require(tree.Excluded.Count == inventory.Excluded.Count && tree.Excluded.All(item => inventory.Excluded.Contains(item)),
                "The excluded navigation entries changed.");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw Blocked("profile-mismatch", message, false);
    }

    private static SurveyBlockedException Blocked(string code, string message, bool canResume) => new(code, message, canResume);
    private static bool Present(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind != JsonValueKind.Null;
    private static string? Text(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
            return null;
        Require(property.ValueKind == JsonValueKind.String, $"Native metadata '{name}' is not text.");
        return property.GetString();
    }
    private static string RequiredText(JsonElement value, string name) =>
        Text(value, name) ?? throw Blocked("profile-mismatch", $"Native metadata '{name}' is missing.", false);
    private static bool? Bool(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
            return null;
        Require(property.ValueKind is JsonValueKind.True or JsonValueKind.False, $"Native metadata '{name}' is not a boolean.");
        return property.GetBoolean();
    }
    private static long Integer(JsonElement value, string name)
    {
        Require(value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out _), $"Native metadata '{name}' is not an integer.");
        return property.GetInt64();
    }
    private static JsonElement Array(JsonElement value, string name) => Member(value, name, JsonValueKind.Array);
    private static JsonElement Member(JsonElement value, string name, JsonValueKind kind)
    {
        Require(value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property)
            && property.ValueKind == kind, $"Native metadata '{name}' is missing or malformed.");
        return value.GetProperty(name);
    }
    private static bool PropertyBoolean(JsonElement properties, string name)
    {
        Require(bool.TryParse(Text(properties, name), out var result), $"Navigation property '{name}' is unavailable.");
        return result;
    }
    private static bool IsIdentifier(string? text) => text is { Length: > 0 and <= 256 } and not ("." or "..")
        && !text.StartsWith('-') && text.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-');
    private static bool SafeSelector(string? text) => text is { Length: > 0 and <= 512 }
        && !text.StartsWith('-') && !text.Any(char.IsControl);
}
