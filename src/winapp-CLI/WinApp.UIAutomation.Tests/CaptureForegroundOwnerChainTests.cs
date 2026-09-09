// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Windows.Win32.Foundation;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

/// <summary>
/// The looser foreground predicate live-screen capture uses.
/// </summary>
/// <remarks>
/// Injection and capture want different answers to "is the foreground close enough to my target".
/// For injection a dialog in front is fatal — the keystrokes would land in the dialog. For capture it
/// is the normal case and the whole point: a modal dialog the target owns is sitting on the pixels
/// being read, which is why <c>--capture-screen</c> exists. An owned dialog is its own
/// <c>GA_ROOT</c>, so the strict predicate can never see the relationship; only the owner chain can.
/// </remarks>
[TestClass]
[DoNotParallelize] // ForegroundGuard's native seams are static.
public class CaptureForegroundOwnerChainTests
{
    private static readonly HWND s_mainWindow = new(0x1000);
    private static readonly HWND s_ownedDialog = new(0x2000);
    private static readonly HWND s_nestedDialog = new(0x3000);
    private static readonly HWND s_strangerWindow = new(0x9000);

    [TestCleanup]
    public void Cleanup() => ForegroundGuard.ResetNativeSeams();

    /// <summary>Every window is its own root, which is how real top-level windows behave.</summary>
    private static void ArrangeTopLevelRoots() => ForegroundGuard.s_getRootAncestor = h => h;

    private static void ArrangeOwners(Dictionary<nint, HWND> owners)
        => ForegroundGuard.s_getOwner = h => owners.TryGetValue((nint)h, out var owner) ? owner : new HWND(0);

    [TestMethod]
    public void AnOwnedDialogInFrontIsCapturableForItsOwner()
    {
        // The reported regression: `ui screenshot -w <main> --capture-screen` while the app's own
        // modal dialog holds the foreground. The strict predicate says no, because the dialog is its
        // own root; capture must say yes, because the dialog is the overlay being captured.
        ArrangeTopLevelRoots();
        ForegroundGuard.s_getForegroundWindow = () => s_ownedDialog;
        ArrangeOwners(new() { [(nint)s_ownedDialog] = s_mainWindow });

        Assert.IsFalse(ForegroundGuard.ForegroundBelongsTo((nint)s_mainWindow),
            "the strict predicate cannot see an owner relationship — that is why capture needs its own");
        Assert.IsTrue(ForegroundGuard.ForegroundIsCapturableFor((nint)s_mainWindow));
    }

    [TestMethod]
    public void AnOwnerChainSeveralHopsDeepStillResolves()
    {
        // A file picker owned by a document window owned by the main frame.
        ArrangeTopLevelRoots();
        ForegroundGuard.s_getForegroundWindow = () => s_nestedDialog;
        ArrangeOwners(new()
        {
            [(nint)s_nestedDialog] = s_ownedDialog,
            [(nint)s_ownedDialog] = s_mainWindow,
        });

        Assert.IsTrue(ForegroundGuard.ForegroundIsCapturableFor((nint)s_mainWindow));
    }

    [TestMethod]
    public void AnUnrelatedForegroundWindowIsStillRefused()
    {
        // The case the guard exists for. Capturing another app's pixels and labelling them as the
        // target's is worse than failing, because the caller cannot tell.
        ArrangeTopLevelRoots();
        ForegroundGuard.s_getForegroundWindow = () => s_strangerWindow;
        ArrangeOwners([]);

        Assert.IsFalse(ForegroundGuard.ForegroundIsCapturableFor((nint)s_mainWindow));
    }

    [TestMethod]
    public void ADialogOwnedByAnUnrelatedWindowIsStillRefused()
    {
        // An owner chain that leads somewhere else is not evidence of anything.
        ArrangeTopLevelRoots();
        ForegroundGuard.s_getForegroundWindow = () => s_ownedDialog;
        ArrangeOwners(new() { [(nint)s_ownedDialog] = s_strangerWindow });

        Assert.IsFalse(ForegroundGuard.ForegroundIsCapturableFor((nint)s_mainWindow));
    }

    [TestMethod]
    public void ACyclicOwnerChainTerminates()
    {
        // A corrupted or hostile chain must not spin. The hop bound is what makes the walk total.
        ArrangeTopLevelRoots();
        ForegroundGuard.s_getForegroundWindow = () => s_ownedDialog;
        ArrangeOwners(new()
        {
            [(nint)s_ownedDialog] = s_strangerWindow,
            [(nint)s_strangerWindow] = s_ownedDialog,
        });

        Assert.IsFalse(ForegroundGuard.ForegroundIsCapturableFor((nint)s_mainWindow));
    }

    [TestMethod]
    public void NoForegroundWindowIsStillRefused()
    {
        // A locked or secure desktop. Nothing to capture, and nothing to prove ownership against.
        ArrangeTopLevelRoots();
        ForegroundGuard.s_getForegroundWindow = () => new HWND(0);
        ArrangeOwners([]);

        Assert.IsFalse(ForegroundGuard.ForegroundIsCapturableFor((nint)s_mainWindow));
    }

    [TestMethod]
    public void TheTargetItselfInFrontIsStillCapturable()
    {
        // The ordinary case must not regress: no owner walk needed.
        ArrangeTopLevelRoots();
        ForegroundGuard.s_getForegroundWindow = () => s_mainWindow;
        ArrangeOwners([]);

        Assert.IsTrue(ForegroundGuard.ForegroundIsCapturableFor((nint)s_mainWindow));
    }

    [TestMethod]
    public void AChildTargetWhoseRootOwnsTheDialogIsCapturable()
    {
        // The target HWND resolved from an element is often a child/host window. The dialog is owned
        // by that child's top-level root, not by the child itself, so the walk has to accept the root.
        var childTarget = new HWND(0x1100);
        ForegroundGuard.s_getRootAncestor = h => h == childTarget ? s_mainWindow : h;
        ForegroundGuard.s_getForegroundWindow = () => s_ownedDialog;
        ArrangeOwners(new() { [(nint)s_ownedDialog] = s_mainWindow });

        Assert.IsTrue(ForegroundGuard.ForegroundIsCapturableFor((nint)childTarget));
    }

    [TestMethod]
    public void AZeroTargetIsRefused()
    {
        // No resolvable window means there is nothing to prove a relationship against, so "can't
        // verify" has to read as no — same as the strict predicate.
        ArrangeTopLevelRoots();
        ForegroundGuard.s_getForegroundWindow = () => s_ownedDialog;
        ArrangeOwners(new() { [(nint)s_ownedDialog] = s_mainWindow });

        Assert.IsFalse(ForegroundGuard.ForegroundIsCapturableFor(0));
    }

    [TestMethod]
    public void AForegroundWindowWithNoOwnerAtAllIsRefused()
    {
        // GW_OWNER returns null for an unowned top-level window; the walk must stop rather than treat
        // "no owner" as a match.
        ArrangeTopLevelRoots();
        ForegroundGuard.s_getForegroundWindow = () => s_strangerWindow;
        ForegroundGuard.s_getOwner = _ => new HWND(0);

        Assert.IsFalse(ForegroundGuard.ForegroundIsCapturableFor((nint)s_mainWindow));
    }

    [TestMethod]
    public void AChainLongerThanTheBoundIsRefused()
    {
        // The bound is what makes the walk total. A chain that only reaches the target beyond it is
        // refused rather than followed forever.
        ArrangeTopLevelRoots();
        ForegroundGuard.s_getForegroundWindow = () => new HWND(1);
        // 1 -> 2 -> 3 -> ... -> 20, with the target parked at the far end.
        ForegroundGuard.s_getOwner = h => (nint)h < 20 ? new HWND((nint)h + 1) : s_mainWindow;

        Assert.IsFalse(ForegroundGuard.ForegroundIsCapturableFor((nint)s_mainWindow));
    }
}
