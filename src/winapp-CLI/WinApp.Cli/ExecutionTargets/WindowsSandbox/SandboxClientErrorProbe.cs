// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace WinApp.Cli.ExecutionTargets.WindowsSandbox;

/// <summary>Recognizes the Sandbox client's terminal error page without reading localized messages.</summary>
internal static class SandboxClientErrorProbe
{
    internal sealed record Control(
        string ClassName, string Name, int ControlType, string AutomationId,
        bool IsControlElement = true);

    internal static bool IsTerminalError(IReadOnlyList<Control> controls, bool complete) =>
        complete &&
        controls.Count == 4 &&
        controls.All(control => control.AutomationId.Length == 0) &&
        controls.Count(control => control is
        {
            ClassName: "TextBlock", Name: "\uE783", ControlType: 50020, IsControlElement: false,
        }) == 1 &&
        controls.Count(control => control is
        {
            ClassName: "TextBlock", ControlType: 50020, IsControlElement: true,
        }) == 2 &&
        controls.Count(control => control is
        {
            ClassName: "Button", ControlType: 50000, IsControlElement: true,
        }) == 1;

    internal static SandboxClientSurface Classify(
        IReadOnlyList<Control> shell, IReadOnlyList<Control> page, bool renderer)
    {
        var renderHosts = shell.Count(control => control.ClassName == "AtlAxWin");
        var knownShell = shell.Count == 4 + renderHosts &&
            shell.Count(control => control.ClassName == "InputNonClientPointerSource") == 1 &&
            shell.Count(control => control.ClassName == "ReunionWindowingCaptionControls") == 1 &&
            shell.Count(control => control.ClassName == "Microsoft.UI.Content.DesktopChildSiteBridge") == 1 &&
            shell.Count(control => control is { ClassName: "", AutomationId: "TitleBar" }) == 1;
        if (!knownShell)
        {
            return SandboxClientSurface.Unknown;
        }

        // Sandbox 0.8.107.0's ShowErrorAsync replaces the remote-session page with
        // this raw XAML dialog. Its critical glyph is fixed, not localized. A new
        // shell layout, warning, partial tree, or missing renderer is not proof.
        if (IsTerminalError(page, complete: true))
        {
            return renderHosts == 0 && !renderer
                ? SandboxClientSurface.TerminalError
                : SandboxClientSurface.Unknown;
        }

        return renderHosts == 1 && renderer && page.Count == 3 &&
            page.Count(control => control is { ClassName: "Image", AutomationId: "TitleBarIcon" }) == 1 &&
            page.Count(control => control is { ClassName: "TextBlock", AutomationId: "TitleTextBlock" }) == 1 &&
            page.Count(control => control is { ClassName: "Button", AutomationId: "SandboxOptions" }) == 1
                ? SandboxClientSurface.Session
                : SandboxClientSurface.Unknown;
    }

    internal static SandboxClientSurface Inspect(SandboxClientWindow client) => Inspect(client, ReadSurface);

    internal static SandboxClientSurface Inspect(
        SandboxClientWindow client,
        Func<SandboxClientWindow, SandboxClientSurface> readSurface)
    {
        try
        {
            return client.StartTicksUtc > 0 ? readSurface(client) : SandboxClientSurface.Unknown;
        }
        catch (Exception ex) when (ex is COMException or TimeoutException or UnauthorizedAccessException or ArgumentException)
        {
            Trace.TraceWarning($"Could not inspect Sandbox client {client.ProcessId} for a terminal error (0x{ex.HResult:X8}); retaining it as an unknown client.");
            return SandboxClientSurface.Unknown;
        }
    }

    internal static bool IsExpectedRoot([NotNullWhen(true)] IUIAutomationElement? root, int processId) =>
        root is not null &&
        root.get_CurrentProcessId() == processId &&
        Read(root.get_CurrentClassName()) == "WinUIDesktopWin32WindowClass";

    private static SandboxClientSurface ReadSurface(SandboxClientWindow client)
    {
        var clock = Stopwatch.StartNew();
        var automation = CUIAutomation8.CreateInstance<IUIAutomation2>();
        automation.put_ConnectionTimeout(100);
        automation.put_TransactionTimeout(100);
        var root = automation.ElementFromHandle(new HWND(client.Handle));
        if (!IsExpectedRoot(root, client.ProcessId))
        {
            return SandboxClientSurface.Unknown;
        }

            var walker = automation.get_RawViewWalker();
        var cache = automation.CreateCacheRequest();
        cache.put_TreeScope(TreeScope.TreeScope_Element);
        cache.AddProperty(UIA_PROPERTY_ID.UIA_ClassNamePropertyId);
        cache.AddProperty(UIA_PROPERTY_ID.UIA_NamePropertyId);
        cache.AddProperty(UIA_PROPERTY_ID.UIA_ControlTypePropertyId);
        cache.AddProperty(UIA_PROPERTY_ID.UIA_AutomationIdPropertyId);
        cache.AddProperty(UIA_PROPERTY_ID.UIA_IsControlElementPropertyId);
        var count = 0;
        var children = Children(root);
        var renderHosts = children.Where(child => child.Control.ClassName == "AtlAxWin").ToArray();
        var sites = children.Where(child =>
            child.Control.ClassName == "Microsoft.UI.Content.DesktopChildSiteBridge").ToArray();
        if (sites.Length != 1)
        {
            return SandboxClientSurface.Unknown;
        }

        var siteChildren = Children(sites[0].Element);
        if (siteChildren.Count != 1 || siteChildren[0].Control.ClassName != "InputSiteWindowClass")
        {
            return SandboxClientSurface.Unknown;
        }

        var content = Children(siteChildren[0].Element).Select(child => child.Control).ToArray();
        var classes = new HashSet<string>(StringComparer.Ordinal);
        if (renderHosts.Length == 1)
        {
            CollectRenderer(renderHosts[0].Element, depth: 0);
        }

        return Classify([.. children.Select(child => child.Control)], content,
            renderer: classes.Contains("IHWindowClass") && classes.Contains("OPWindowClass"));

        List<(IUIAutomationElement Element, Control Control)> Children(IUIAutomationElement parent)
        {
            var result = new List<(IUIAutomationElement, Control)>();
            CheckBudget();
            var element = walker.GetFirstChildElementBuildCache(parent, cache);
            while (element is not null)
            {
                CheckBudget();
                if (++count > 48)
                {
                    throw new TimeoutException("Sandbox viewer tree exceeded the node limit.");
                }

                result.Add((element, new Control(
                    Read(element.get_CachedClassName()),
                    Read(element.get_CachedName()),
                    (int)element.get_CachedControlType(),
                    Read(element.get_CachedAutomationId()),
                    element.get_CachedIsControlElement())));
                CheckBudget();
                element = walker.GetNextSiblingElementBuildCache(element, cache);
            }

            CheckBudget();
            return result;
        }

        void CollectRenderer(IUIAutomationElement parent, int depth)
        {
            if (depth >= 5)
            {
                throw new TimeoutException("Sandbox renderer tree exceeded the depth limit.");
            }

            foreach (var child in Children(parent))
            {
                classes.Add(child.Control.ClassName);
                if (child.Control.ClassName is not ("IHWindowClass" or "OPWindowClass"))
                {
                    CollectRenderer(child.Element, depth + 1);
                }
            }
        }

        void CheckBudget()
        {
            if (clock.ElapsedMilliseconds > 300)
            {
                throw new TimeoutException("Sandbox viewer inspection exceeded its time budget.");
            }
        }
    }

    private static unsafe string Read(BSTR value)
    {
        try
        {
            return value.ToString() ?? "";
        }
        finally
        {
            Marshal.FreeBSTR((nint)value.Value);
        }
    }
}
