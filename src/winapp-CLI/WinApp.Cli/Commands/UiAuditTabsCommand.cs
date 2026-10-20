// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Commands;

internal sealed class UiAuditTabsCommand : Command, IShortDescription
{
    internal static readonly Option<int> DepthOption = new("--depth", "-d")
    {
        Description = "Inclusive raw-tree depth (1-50); root depth is zero.",
        DefaultValueFactory = _ => 20,
    };
    internal static readonly Option<int> MaxElementsOption = new("--max-elements")
    {
        Description = "Maximum examined elements (1-10000). Truncated or unreadable discovery cannot authorize selection.",
        DefaultValueFactory = _ => 2000,
    };
    internal static readonly Option<int> TimeoutOption = new("--timeout", "-t")
    {
        Description = "Cooperative timeout in milliseconds (1000-120000). A blocking provider requires an external process deadline.",
        DefaultValueFactory = _ => 15000,
    };
    internal static readonly Option<string?> SelectOption = new("--select") { Description = "Exact TabItem runtime ID from audit-tabs; never a selector or accessible name." };
    internal static readonly Option<string?> ContainerOption = new("--container") { Description = "Exact nearest standard Tab group's runtime ID, not an inner ListView's ID." };
    internal static readonly Option<string?> RootRuntimeIdOption = new("--root-runtime-id") { Description = "Exact expected root runtime ID from the same window's discovery." };
    internal static readonly Option<bool> AcceptNavigationOption = new("--accept-navigation") { Description = "Explicitly approve SelectionItem.Select on the exact requested item; requires all three runtime IDs." };

    public string ShortDescription => "Discover standard tabs or explicitly select one exact realized TabItem";

    public UiAuditTabsCommand() : base("audit-tabs",
        "Read standard Tab/TabItem structure in one exact top-level --window without reading names, values or text. " +
        "Read-only by default. Selection requires --select, --container, --root-runtime-id and --accept-navigation together. " +
        "Only enabled, visible, realized, single-selection standard tabs with verified selection-container ownership are supported. " +
        "Modal/pop-up and visible owned windows block selection. There is no focus, click, keyboard, scroll or ancestor fallback. " +
        "Successful selection requires two readbacks at least 50 ms apart. Discover again after selection before using nested groups. " +
        "treeSignature hashes observed structural UIA state for comparing fresh read-only snapshots; it is not semantic or content-loaded readiness. " +
        "Exit 0 means captured; partial/blocked results exit nonzero and retain the typed --json result.")
    {
        Options.Add(SharedUiOptions.WindowOption);
        Options.Add(DepthOption);
        Options.Add(MaxElementsOption);
        Options.Add(TimeoutOption);
        Options.Add(SelectOption);
        Options.Add(ContainerOption);
        Options.Add(RootRuntimeIdOption);
        Options.Add(AcceptNavigationOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public sealed class Handler(IUiAuditTabsService service) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);
            var options = new UiAuditTabsOptions
            {
                MaxDepth = parseResult.GetValue(DepthOption), MaxElements = parseResult.GetValue(MaxElementsOption),
                TimeoutMs = parseResult.GetValue(TimeoutOption), Select = parseResult.GetValue(SelectOption),
                Container = parseResult.GetValue(ContainerOption), RootRuntimeId = parseResult.GetValue(RootRuntimeIdOption),
                AcceptNavigation = parseResult.GetValue(AcceptNavigationOption),
            };
            var identities = new[] { options.Select, options.Container, options.RootRuntimeId };
            string? error = window is null or <= 0 ? "An exact positive --window HWND from 'winapp ui list-windows' is required."
                : options.MaxDepth is < 1 or > 50 || options.MaxElements is < 1 or > 10000 || options.TimeoutMs is < 1000 or > 120000
                    ? "--depth must be 1-50, --max-elements 1-10000, and --timeout 1000-120000 milliseconds."
                : (identities.Any(value => value is not null) || options.AcceptNavigation)
                    && (!options.AcceptNavigation || identities.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 1200))
                    ? "--select, --container, --root-runtime-id and --accept-navigation must be supplied together." : null;
            if (error is not null)
            {
                if (json) { UiJsonError.Emit(true, UiJsonError.CodeInvalidArguments, error, errorOut: parseResult.InvocationConfiguration.Error); }
                else { parseResult.InvocationConfiguration.Error.WriteLine(error); }
                return 1;
            }
            UiAuditTabsResult result;
            try
            {
                result = await service.CaptureAsync(window!.Value, options, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                result = Failure("cancelled");
            }
            catch (Exception ex) when (ex is UiAuditSnapshotException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                result = Failure(ex is UiAuditSnapshotException audit ? audit.Code : "uia_target_unavailable");
            }
            if (json)
            {
                parseResult.InvocationConfiguration.Output.WriteLine(JsonSerializer.Serialize(result, UiJsonContext.Default.UiAuditTabsResult));
            }
            else
            {
                if (!parseResult.GetValue(WinAppRootCommand.QuietOption))
                {
                    parseResult.InvocationConfiguration.Output.WriteLine(
                        $"{result.Status}: {result.Containers.Count} standard Tab groups in HWND {result.WindowHandle}.");
                }
                foreach (var issue in result.Issues) { parseResult.InvocationConfiguration.Error.WriteLine(issue); }
            }
            return result.Status == "captured" ? 0 : 1;

            UiAuditTabsResult Failure(string issue) => new()
            {
                WindowHandle = window!.Value, MaxDepth = options.MaxDepth, MaxElements = options.MaxElements,
                Issues = [issue], Selection = options.Select is null ? null : new(options.Container!, options.Select, "blocked"),
            };
        }
    }
}
