// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;

namespace WinApp.Cli.Commands;

internal class UiCommand : Command, IShortDescription, ITargetAwareCommand, ICompactHelpGroup
{
    public string ShortDescription => "Inspect and interact with running Windows app UIs";

    internal const string Summary =
        "Drive any running Windows app through UI Automation (WinUI 3, WPF, WinForms, Win32, UWP, Electron).";

    /// <summary>Preformatted block shown under <see cref="Summary"/> in <c>winapp ui --help</c> only, so
    /// it stays out of the command's description (and the CLI schema).</summary>
    internal const string GoldenPath =
        "\n" +
        "  winapp ui inspect -a <app> --interactive           see available actions\n" +
        "  winapp ui invoke <selector> -a <app>               press a control\n" +
        "  winapp ui set-value <selector> \"<text>\" -a <app>   enter text\n" +
        "  winapp ui get-value <selector> -a <app>            check the result\n" +
        "\n" +
        "  -a <app>     Process name, window title, or PID; targets its active window.\n" +
        "               Includes open dialogs. Use -w <hwnd> for one exact window.\n" +
        "  <selector>   A label, stable AutomationId, or inspect slug. Slugs change when\n" +
        "               elements are recreated. Narrow duplicate labels with --type Button\n" +
        "               or --root <selector>. Inspect again after the UI changes.\n" +
        "\n" +
        "Changing a WinUI app's properties or text live? See 'winapp devtools --help'.";

    /// <summary>Command-list categories for <c>winapp ui --help</c>, in display order.</summary>
    internal static readonly (string Category, Type[] CommandTypes)[] HelpCategories =
    [
        ("Discover", [typeof(UiInspectCommand), typeof(UiSearchCommand), typeof(UiListWindowsCommand), typeof(UiGetFocusedCommand), typeof(UiStatusCommand)]),
        ("Act", [typeof(UiInvokeCommand), typeof(UiSetValueCommand), typeof(UiSendKeysCommand), typeof(UiClickCommand), typeof(UiFocusCommand), typeof(UiScrollCommand), typeof(UiScrollIntoViewCommand), typeof(UiExpandCollapseCommand)]),
        ("Read and wait", [typeof(UiGetValueCommand), typeof(UiGetPropertyCommand), typeof(UiWaitForCommand)]),
        ("Capture", [typeof(UiScreenshotCommand), typeof(UiRecordCommand)]),
        ("Accessibility", [typeof(UiAuditCommand), typeof(UiAuditSnapshotCommand), typeof(UiAuditTabsCommand), typeof(UiAuditObserveCommand), typeof(UiAuditScreenshotCommand), typeof(UiAccessibilityCommand)]),
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
        UiAuditCommand auditCommand,
        UiAuditSnapshotCommand auditSnapshotCommand,
        UiAuditTabsCommand auditTabsCommand,
        UiAuditObserveCommand auditObserveCommand,
        UiAuditScreenshotCommand auditScreenshotCommand,
        UiAccessibilityCommand accessibilityCommand,
        UiInvokeCommand invokeCommand,
        UiExpandCollapseCommand expandCollapseCommand,
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
        : base("ui", Summary)
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
        Subcommands.Add(auditCommand);
        Subcommands.Add(auditSnapshotCommand);
        Subcommands.Add(auditTabsCommand);
        Subcommands.Add(auditObserveCommand);
        Subcommands.Add(auditScreenshotCommand);
        Subcommands.Add(accessibilityCommand);
        Subcommands.Add(invokeCommand);
        Subcommands.Add(expandCollapseCommand);
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

    string ICompactHelpGroup.GoldenPath => GoldenPath;

    IReadOnlyList<(string Category, Type[] CommandTypes)> ICompactHelpGroup.Categories => HelpCategories;

    IReadOnlyList<(string Name, string Description)> ICompactHelpGroup.GroupOptions { get; } =
    [
        ("--on <target>", "Run on 'sandbox' (Windows Sandbox) or 'local' (default)"),
        ("--expect-sandbox <guid>", "Use only this Sandbox ID with --on sandbox"),
        ("-h, --help", "Show help"),
    ];

    string ICompactHelpGroup.TargetUsage => "(-a <app> | -w <hwnd>)";

    IReadOnlyDictionary<string, string> ICompactHelpGroup.Synonyms { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["dump"] = "inspect",
        ["tree"] = "inspect",
        ["snapshot"] = "inspect",
        ["elements"] = "inspect",
        ["find"] = "search",
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
