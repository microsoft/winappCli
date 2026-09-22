// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

[TestClass]
[DoNotParallelize]
public class FocusedElementTests
{
    private const int TargetPid = 123;
    private const long TargetHwnd = 456;
    private readonly UiAutomationService _service = new(NullLogger<UiAutomationService>.Instance, new UiSelectorParser());

    [TestCleanup]
    public void Cleanup()
    {
        UiAutomationService.ResetNativeSeams();
        SystemUiQuery.ResetNativeSeams();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MissingIdentity_VerifiedNativeAncestor_ReturnsFocusedChild(bool explicitWindow)
    {
        InstallTree(0, 0, TargetPid, TargetHwnd);

        var result = await _service.GetFocusedElementAsync(Target(explicitWindow), CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual("focused-child", result.AutomationId);
    }

    [TestMethod]
    public async Task MatchingOwnPid_ProcessTarget_DoesNotRequireAncestor()
    {
        InstallTree(TargetPid, 0, TargetPid, TargetHwnd);
        UiAutomationService.s_getRawViewWalker = _ => throw new AssertFailedException("No ancestry needed");
        Assert.IsNotNull(await _service.GetFocusedElementAsync(Target(false), CancellationToken.None));
    }

    [TestMethod]
    [DataRow(0, 999, 456L, false)]
    [DataRow(0, 999, 456L, true)]
    [DataRow(999, 123, 456L, false)]
    [DataRow(999, 123, 456L, true)]
    [DataRow(123, 123, 789L, true)]
    [DataRow(0, 123, 789L, true)]
    public async Task ForeignProcessOrExplicitWindow_ReturnsNull(int ownPid, int nativePid, long root, bool explicitWindow)
    {
        InstallTree(ownPid, 0, nativePid, root);
        Assert.IsNull(await _service.GetFocusedElementAsync(Target(explicitWindow), CancellationToken.None));
    }

    [TestMethod]
    public async Task OwnedPopup_ProcessTargetAcceptsButExplicitOwnerDoesNot()
    {
        // GA_ROOT is the popup itself, not its owner (GA_ROOTOWNER).
        InstallTree(0, 0, TargetPid, 789);
        Assert.IsNotNull(await _service.GetFocusedElementAsync(Target(false), CancellationToken.None));
        Assert.IsNull(await _service.GetFocusedElementAsync(Target(true), CancellationToken.None));
    }

    [TestMethod]
    public async Task MissingIdentity_NoNativeAncestor_DoesNotGrantMembership()
    {
        InstallTree(0, 0, TargetPid, TargetHwnd, ancestorHwnd: 0);
        Assert.IsNull(await _service.GetFocusedElementAsync(Target(false), CancellationToken.None));
    }

    [TestMethod]
    public async Task ForeignAncestorPid_CannotBeOverriddenByNativeWindow()
    {
        InstallTree(0, 999, TargetPid, TargetHwnd);
        Assert.IsNull(await _service.GetFocusedElementAsync(Target(false), CancellationToken.None));
    }

    [TestMethod]
    public async Task UnknownTargetPid_DoesNotMatchMissingIdentity()
    {
        InstallTree(0, 0, TargetPid, TargetHwnd);
        var target = Target(false);
        target.ProcessId = 0;
        Assert.IsNull(await _service.GetFocusedElementAsync(target, CancellationToken.None));
    }

    [TestMethod]
    [DataRow("focus")]
    [DataRow("pid")]
    [DataRow("walker")]
    [DataRow("parent")]
    [DataRow("hwnd")]
    [DataRow("ancestor-pid")]
    public async Task ProviderFailure_PropagatesInsteadOfReportingNoFocus(string failureAt)
    {
        var failure = new COMException("provider failed", unchecked((int)0x80040201));
        InstallTree(0, 0, TargetPid, TargetHwnd, failureAt: failureAt, failure: failure);

        var actual = await Assert.ThrowsExactlyAsync<COMException>(
            () => _service.GetFocusedElementAsync(Target(false), CancellationToken.None));
        Assert.AreSame(failure, actual);
    }

    [TestMethod]
    [DataRow(0, 456L)]
    [DataRow(123, 0L)]
    public async Task UnavailableNativeWindow_ThrowsRatherThanReportingNoFocus(int pid, long root)
    {
        InstallTree(0, 0, pid, root);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => _service.GetFocusedElementAsync(Target(true), CancellationToken.None));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(999)]
    public async Task RootWindowOwnership_MustStillMatch(int rootPid)
    {
        InstallTree(0, 0, TargetPid, TargetHwnd);
        SystemUiQuery.s_getProcessIdForWindow = hwnd => hwnd == TargetHwnd ? (uint)rootPid : TargetPid;
        if (rootPid == 0)
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => _service.GetFocusedElementAsync(Target(true), CancellationToken.None));
        }
        else
        {
            Assert.IsNull(await _service.GetFocusedElementAsync(Target(true), CancellationToken.None));
        }
    }

    [TestMethod]
    public async Task ForeignNativeAncestor_StopsBeforeAnyHigherAncestor()
    {
        InstallTree(0, 0, 999, 789, cycle: true);
        var reads = 0;
        SystemUiQuery.s_getProcessIdForWindow = _ =>
        {
            reads++;
            return reads == 1 ? 999u : TargetPid;
        };
        Assert.IsNull(await _service.GetFocusedElementAsync(Target(false), CancellationToken.None));
        Assert.AreEqual(1, reads);
    }

    [TestMethod]
    public async Task CyclicProvider_IsBounded()
    {
        InstallTree(0, 0, TargetPid, TargetHwnd, ancestorHwnd: 0, cycle: true);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => _service.GetFocusedElementAsync(Target(false), CancellationToken.None));
    }

    [TestMethod]
    public async Task NoFocusedElement_ReturnsNull()
    {
        UiAutomationService.s_getFocusedElement = _ => null;
        Assert.IsNull(await _service.GetFocusedElementAsync(Target(false), CancellationToken.None));
    }

    [TestMethod]
    public async Task Cancellation_DuringTraversal_Propagates()
    {
        InstallTree(0, 0, TargetPid, TargetHwnd);
        using var cts = new CancellationTokenSource();
        var walker = UiAutomationService.s_getRawViewWalker;
        UiAutomationService.s_getRawViewWalker = service =>
        {
            cts.Cancel();
            return walker(service);
        };
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => _service.GetFocusedElementAsync(Target(false), cts.Token));
    }

    private static UiTarget Target(bool explicitWindow) => new()
    {
        ProcessId = TargetPid,
        WindowHandle = TargetHwnd,
        IsExplicitWindow = explicitWindow,
    };

    private static void InstallTree(int ownPid, int ancestorPid, int nativePid, long root,
        long ancestorHwnd = 111, string? failureAt = null, COMException? failure = null, bool cycle = false)
    {
        void Fail(string at)
        {
            if (failureAt == at) { throw failure!; }
        }
        var child = Proxy<IUIAutomationElement>((method, _) =>
        {
            if (method.Name == "get_CurrentNativeWindowHandle") { Fail("hwnd"); return new HWND(0); }
            return method.Name switch
            {
                "get_CurrentBoundingRectangle" => new RECT(),
                "get_CurrentControlType" => UIA_CONTROLTYPE_ID.UIA_EditControlTypeId,
                "get_CurrentIsEnabled" => new BOOL(true),
                "get_CurrentIsOffscreen" => new BOOL(false),
                _ => throw new COMException("Optional pattern is unavailable"),
            };
        });
        var ancestor = Proxy<IUIAutomationElement>((method, _) =>
            method.Name == "get_CurrentNativeWindowHandle" ? new HWND((nint)ancestorHwnd)
                : throw new AssertFailedException(method.Name));
        UiAutomationService.s_getFocusedElement = _ => { Fail("focus"); return child; };
        UiAutomationService.s_getElementProcessId = element =>
        {
            Fail(ReferenceEquals(element, child) ? "pid" : "ancestor-pid");
            return ReferenceEquals(element, child) ? ownPid : ancestorPid;
        };
        UiAutomationService.s_getRawViewWalker = _ =>
        {
            Fail("walker");
            return Proxy<IUIAutomationTreeWalker>((method, args) =>
            {
                Assert.AreEqual("GetParentElement", method.Name);
                Fail("parent");
                return ReferenceEquals(args![0], child) || cycle ? ancestor : null;
            });
        };
        SystemUiQuery.s_getProcessIdForWindow = _ => (uint)nativePid;
        SystemUiQuery.s_getRootWindow = _ => root;
        UiAutomationService.s_getCurrentBstr = (_, property) =>
            new BSTR(Marshal.StringToBSTR(property == UIA_PROPERTY_ID.UIA_AutomationIdPropertyId ? "focused-child" : ""));
    }

    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, NativeProxy>();
        ((NativeProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    private class NativeProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
    }
}
