// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;

namespace WinApp.Cli.Commands;

internal class UiCommand : Command, IShortDescription
{
    public string ShortDescription => "Inspect and interact with running Windows app UIs";

    public UiCommand(
        UiStatusCommand statusCommand,
        UiInspectCommand inspectCommand,
        UiSearchCommand searchCommand,
        UiGetPropertyCommand getPropertyCommand,
        UiGetValueCommand getValueCommand,
        UiScreenshotCommand screenshotCommand,
        UiRecordCommand recordCommand,
        UiRecordActionsCommand recordActionsCommand,
        UiReplayCommand replayCommand,
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
        UiGetFocusedCommand getFocusedCommand)
        : base("ui", "Inspect and interact with any running Windows app using UI Automation (UIA). " +
               "Works with WPF, WinForms, Win32, Electron, and WinUI 3 apps.")
    {
        Subcommands.Add(statusCommand);
        Subcommands.Add(inspectCommand);
        Subcommands.Add(searchCommand);
        Subcommands.Add(getPropertyCommand);
        Subcommands.Add(getValueCommand);
        Subcommands.Add(screenshotCommand);
        Subcommands.Add(recordCommand);
        Subcommands.Add(recordActionsCommand);
        Subcommands.Add(replayCommand);
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
    }
}
