// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

/// <summary>
/// The window-selection rules behind windowed-popup capture (issue #646), without a desktop.
/// </summary>
[TestClass]
public class WindowedPopupHostSelectionTests
{
    private const nint Owner = 100;
    private const int AppPid = 7;

    private sealed class Desktop
    {
        public Dictionary<nint, nint> Owners { get; } = [];
        public Dictionary<nint, int> Pids { get; } = new() { [Owner] = AppPid };
        public HashSet<nint> Hidden { get; } = [];
        public HashSet<nint> Hosts { get; } = [];
        public List<nint> HostQueries { get; } = [];

        public nint Add(nint hwnd, nint owner, int pid = AppPid, bool hosts = false)
        {
            Owners[hwnd] = owner;
            Pids[hwnd] = pid;
            if (hosts)
            {
                Hosts.Add(hwnd);
            }

            return hwnd;
        }

        public nint Select() => UiAutomationService.SelectWindowedPopupHost(
            Owner,
            Pids.Keys.ToList(),
            hwnd => !Hidden.Contains(hwnd),
            hwnd => Owners.GetValueOrDefault(hwnd),
            hwnd => Pids.GetValueOrDefault(hwnd),
            hwnd =>
            {
                HostQueries.Add(hwnd);
                return Hosts.Contains(hwnd);
            });
    }

    [TestMethod]
    public void OwnedSameProcessWindowHostingTheElement_IsSelected()
    {
        var desktop = new Desktop();
        desktop.Add(200, Owner);
        var popup = desktop.Add(300, Owner, hosts: true);

        Assert.AreEqual(popup, desktop.Select());
    }

    [TestMethod]
    public void WindowOwnedThroughAnotherPopup_IsSelected()
    {
        var desktop = new Desktop();
        desktop.Add(200, Owner);
        var submenu = desktop.Add(300, 200, hosts: true);

        Assert.AreEqual(submenu, desktop.Select());
    }

    [TestMethod]
    public void NoWindowHostsTheElement_KeepsTheOwner()
    {
        var desktop = new Desktop();
        desktop.Add(200, Owner);
        desktop.Add(300, Owner);

        Assert.AreEqual(0, desktop.Select());
    }

    [TestMethod]
    public void OverlappingWindowFromAnotherProcess_IsNeverQueried()
    {
        var desktop = new Desktop();
        desktop.Add(200, Owner, pid: AppPid + 1, hosts: true);

        Assert.AreEqual(0, desktop.Select());
        CollectionAssert.DoesNotContain(desktop.HostQueries, (nint)200);
    }

    [TestMethod]
    public void UnownedWindowOfTheSameProcess_IsNeverQueried()
    {
        var desktop = new Desktop();
        desktop.Add(200, 0, hosts: true);
        desktop.Add(300, 999, hosts: true);

        Assert.AreEqual(0, desktop.Select());
        Assert.AreEqual(0, desktop.HostQueries.Count);
    }

    [TestMethod]
    public void HiddenWindow_IsNeverQueried()
    {
        var desktop = new Desktop();
        desktop.Add(200, Owner, hosts: true);
        desktop.Hidden.Add(200);

        Assert.AreEqual(0, desktop.Select());
        Assert.AreEqual(0, desktop.HostQueries.Count);
    }

    [TestMethod]
    public void TwoWindowsHostTheElement_IsAmbiguous()
    {
        var desktop = new Desktop();
        desktop.Add(200, Owner, hosts: true);
        desktop.Add(300, Owner, hosts: true);

        Assert.AreEqual(0, desktop.Select());
    }

    [TestMethod]
    public void CyclicOwnerChain_Terminates()
    {
        var desktop = new Desktop();
        desktop.Add(200, 300, hosts: true);
        desktop.Add(300, 200, hosts: true);

        Assert.AreEqual(0, desktop.Select());
    }

    [TestMethod]
    public void OwnerWithoutAProcess_SelectsNothing()
    {
        var desktop = new Desktop();
        desktop.Pids[Owner] = 0;
        desktop.Add(200, Owner, pid: 0, hosts: true);

        Assert.AreEqual(0, desktop.Select());
        Assert.AreEqual(0, desktop.HostQueries.Count);
    }
}
