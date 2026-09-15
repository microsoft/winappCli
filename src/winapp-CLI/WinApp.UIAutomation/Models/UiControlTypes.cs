// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>Official UIA control type names and the two supported framework aliases.</summary>
public static class UiControlTypes
{
    // UIA assigns these contiguous IDs, from Button (50000) through AppBar (50040).
    private static readonly string[] Names =
    [
        "Button", "Calendar", "CheckBox", "ComboBox", "Edit", "Hyperlink", "Image",
        "ListItem", "List", "Menu", "MenuBar", "MenuItem", "ProgressBar", "RadioButton",
        "ScrollBar", "Slider", "Spinner", "StatusBar", "Tab", "TabItem", "Text",
        "ToolBar", "ToolTip", "Tree", "TreeItem", "Custom", "Group", "Thumb",
        "DataGrid", "DataItem", "Document", "SplitButton", "Window", "Pane", "Header",
        "HeaderItem", "Table", "TitleBar", "Separator", "SemanticZoom", "AppBar",
    ];

    /// <summary>Resolve an official name, TextBox, or TextBlock to its UIA ID; zero means invalid.</summary>
    public static int GetId(string name)
    {
        if (string.Equals(name, "TextBox", StringComparison.OrdinalIgnoreCase)) { return 50004; }
        if (string.Equals(name, "TextBlock", StringComparison.OrdinalIgnoreCase)) { return 50020; }
        for (var i = 0; i < Names.Length; i++)
        {
            if (string.Equals(name, Names[i], StringComparison.OrdinalIgnoreCase)) { return 50000 + i; }
        }
        return 0;
    }

    /// <summary>Return the canonical name for a UIA control type ID.</summary>
    public static string GetName(int id) =>
        id >= 50000 && id < 50000 + Names.Length ? Names[id - 50000] : $"Unknown({id})";
}
