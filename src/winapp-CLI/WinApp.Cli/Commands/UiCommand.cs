// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;

namespace WinApp.Cli.Commands;

internal class UiCommand : Command, IShortDescription, ITargetAwareCommand, ICompactHelpGroup
{
    public string ShortDescription => "Inspect and interact with running Windows app UIs";

    internal const string GoldenPath =
        "Drive any running Windows app through UI Automation (WinUI 3, WPF, WinForms, Win32, UWP, Electron).\n" +
        "\n" +
        "  winapp ui inspect -a <app> --interactive           see what you can act on\n" +
        "  winapp ui invoke <selector> -a <app>               press buttons, menu items, tabs, toggles\n" +
        "  winapp ui set-value <selector> \"<text>\" -a <app>   fill text boxes and documents\n" +
        "  winapp ui get-value <selector> -a <app>            check the result\n" +
        "\n" +
        "  -a <app>     Process name, window title, or PID. Targets the app's active window,\n" +
        "               including an open dialog. It prints the window's -w <hwnd>; use that\n" +
        "               if it picked the wrong window.\n" +
        "  <selector>   A visible label (\"Save as\"), an AutomationId (stable), or a slug from\n" +
        "               inspect (changes when the element is recreated). If a label matches\n" +
        "               several elements, narrow it: \"Save\" --type Button, or --root <selector>.\n" +
        "  After an action changes the UI (a dialog opens, a page loads), inspect again.\n" +
        "\n" +
        "Changing a WinUI app's properties or text live? See 'winapp devtools --help'.";

    /// <summary>Command-list categories for <c>winapp ui --help</c>, in display order.</summary>
    internal static readonly (string Category, Type[] CommandTypes)[] HelpCategories =
    [
        ("Discover", [typeof(UiInspectCommand), typeof(UiSearchCommand), typeof(UiListWindowsCommand), typeof(UiGetFocusedCommand), typeof(UiStatusCommand)]),
        ("Act", [typeof(UiInvokeCommand), typeof(UiSetValueCommand), typeof(UiSendKeysCommand), typeof(UiClickCommand), typeof(UiFocusCommand), typeof(UiScrollCommand), typeof(UiScrollIntoViewCommand)]),
        ("Read and wait", [typeof(UiGetValueCommand), typeof(UiGetPropertyCommand), typeof(UiWaitForCommand)]),
        ("Capture", [typeof(UiScreenshotCommand), typeof(UiRecordCommand)]),
        ("Gestures", [typeof(UiHoverCommand), typeof(UiDragCommand), typeof(UiTouchCommand), typeof(UiPenCommand)]),
        ("Workflow coordination", [typeof(UiYieldCommand)]),
    ];

    public UiCommand(
        UiStatusCommand statusCommand,
        UiInspectCommand inspectCommand,
        UiSearchCommand searchCommand,
        UiGetPropertyCommand getPropertyCommand,
        UiGetValueCommand getValueCommand,
        UiScreenshotCommand screenshotCommand,
        UiRecordCommand recordCommand,
        UiInvokeCommand invokeCommand,
        UiClickCommand clickCommand,
        UiDragCommand dragCommand,
        UiTouchCommand touchCommand,
        UiPenCommand penCommand,
        UiHoverCommand hoverCommand,
        UiSendKeysCommand sendKeysCommand,
        UiSetValueCommand setValueCommand,
        UiFocusCommand focusCommand,
        UiScrollIntoViewCommand scrollIntoViewCommand,
        UiScrollCommand scrollCommand,
        UiWaitForCommand waitForCommand,
        UiListWindowsCommand listWindowsCommand,
        UiGetFocusedCommand getFocusedCommand,
        UiYieldCommand yieldCommand)
        : base("ui", GoldenPath)
    {
        // Not recursive: every verb adds its own --json. The group accepts it so that an unknown
        // command ('winapp ui dump --json') can still report its error as JSON.
        Options.Add(WinAppRootCommand.JsonOption);

        Subcommands.Add(statusCommand);
        Subcommands.Add(inspectCommand);
        Subcommands.Add(searchCommand);
        Subcommands.Add(getPropertyCommand);
        Subcommands.Add(getValueCommand);
        Subcommands.Add(screenshotCommand);
        Subcommands.Add(recordCommand);
        Subcommands.Add(invokeCommand);
        Subcommands.Add(clickCommand);
        Subcommands.Add(dragCommand);
        Subcommands.Add(touchCommand);
        Subcommands.Add(penCommand);
        Subcommands.Add(hoverCommand);
        Subcommands.Add(sendKeysCommand);
        Subcommands.Add(setValueCommand);
        Subcommands.Add(focusCommand);
        Subcommands.Add(scrollIntoViewCommand);
        Subcommands.Add(scrollCommand);
        Subcommands.Add(waitForCommand);
        Subcommands.Add(listWindowsCommand);
        Subcommands.Add(getFocusedCommand);
        Subcommands.Add(yieldCommand);
    }

    IReadOnlyList<(string Category, Type[] CommandTypes)> ICompactHelpGroup.Categories => HelpCategories;

    IReadOnlyList<(string Name, string Description)> ICompactHelpGroup.GroupOptions { get; } =
    [
        ("--on <target>", "Run on 'sandbox' (Windows Sandbox) or 'local' (default)"),
        ("-h, --help", "Show help"),
    ];

    string ICompactHelpGroup.TargetUsage => "(-a <app> | -w <hwnd>)";

    IReadOnlyDictionary<string, string> ICompactHelpGroup.Synonyms { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["dump"] = "inspect",
        ["snapshot"] = "inspect",
        ["elements"] = "inspect",
        ["query"] = "search",
        ["locate"] = "search",
        ["type"] = "send-keys",
        ["keys"] = "send-keys",
        ["read"] = "get-value",
        ["text"] = "get-value",
        ["windows"] = "list-windows",
        ["wait"] = "wait-for",
        ["press"] = "invoke",
        ["activate"] = "invoke",
    };

    IReadOnlyList<string> ICompactHelpGroup.CommonCommands { get; } =
        ["status", "list-windows", "inspect", "search", "invoke", "set-value", "send-keys", "get-value", "wait-for"];
}
