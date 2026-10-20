// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

internal sealed class NativeUiAuditTabsService : IUiAuditTabsService
{
    public Task<UiAuditTabsResult> CaptureAsync(long windowHandle, UiAuditTabsOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(windowHandle, options);
        var budget = new UiAuditSnapshotBudget(new() { TimeoutMs = options.TimeoutMs }, cancellationToken);
        return NativeUiAuditSnapshotService.RunOnMta(() =>
        {
            try
            {
                using var tree = new NativeUiAuditTabsTree(windowHandle, options, budget.Check);
                return new UiAuditTabsCapture<global::Windows.Win32.UI.Accessibility.IUIAutomationElement>(
                    tree, windowHandle, options, budget.Check, Thread.Sleep).Run();
            }
            catch (UiAuditSnapshotStoppedException ex)
            {
                return Failed(windowHandle, options, ex.Code);
            }
            catch (UiAuditSnapshotException ex)
            {
                return Failed(windowHandle, options, ex.Code);
            }
            catch (Exception ex) when (NativeUiAuditSnapshotReader.IsProviderFailure(ex)
                || ex is System.ComponentModel.Win32Exception)
            {
                return Failed(windowHandle, options, "uia_target_unavailable");
            }
        });
    }

    internal static void Validate(long windowHandle, UiAuditTabsOptions options)
    {
        if (windowHandle <= 0 || (nint.Size == 4 && windowHandle > int.MaxValue)
            || options.MaxDepth is < 1 or > 50 || options.MaxElements is < 1 or > 10000 || options.TimeoutMs is < 1000 or > 120000)
        {
            throw new ArgumentException("An exact positive HWND, depth 1-50, max elements 1-10000 and timeout 1000-120000 ms are required.");
        }
        var identities = new[] { options.Select, options.Container, options.RootRuntimeId };
        if (identities.Any(value => value is not null) || options.AcceptNavigation)
        {
            if (!options.AcceptNavigation || identities.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 1200))
            {
                throw new ArgumentException("--select, --container, --root-runtime-id and --accept-navigation must be supplied together.");
            }
        }
    }

    private static UiAuditTabsResult Failed(long window, UiAuditTabsOptions options, string issue) => new()
    {
        WindowHandle = window, MaxDepth = options.MaxDepth, MaxElements = options.MaxElements,
        Issues = [issue], Selection = options.Select is null ? null : new(options.Container!, options.Select, "blocked"),
    };
}
