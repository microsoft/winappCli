// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

extern alias winappcli;

using System.Runtime.InteropServices;
using System.Windows.Forms;
using WinApp.Cli.ExecutionTargets.WindowsSandbox;
using WinApp.Cli.Helpers;
using CliHwnd = winappcli::Windows.Win32.Foundation.HWND;

namespace WinApp.Cli.Tests;

/// <summary>
/// Where the Sandbox client window goes in the z-order when winapp places it behind the user's
/// window.
/// </summary>
/// <remarks>
/// Windows makes a window inserted after an always-on-top window always-on-top too, so placing the
/// client "behind" a pinned terminal used to leave the Sandbox window permanently above every
/// normal window.
/// </remarks>
[TestClass]
public class WindowsSandboxWindowPlacementTests
{
    private const nint Client = 0x100;
    private const nint Foreground = 0x200;

    [TestMethod]
    public void AnOrdinaryForegroundWindow_IsSatBehind()
    {
        Assert.AreEqual(
            Foreground,
            WindowsSandboxWindowController.ChooseWindowToSitBehind(Client, Foreground, previousForegroundExists: true, previousForegroundIsTopmost: false));
    }

    [TestMethod]
    public void AnAlwaysOnTopForegroundWindow_LeavesTheZOrderAlone()
    {
        Assert.AreEqual(
            0,
            WindowsSandboxWindowController.ChooseWindowToSitBehind(Client, Foreground, previousForegroundExists: true, previousForegroundIsTopmost: true));
    }

    [TestMethod]
    [DataRow(0L, true, DisplayName = "no foreground window")]
    [DataRow((long)Foreground, false, DisplayName = "foreground window already closed")]
    [DataRow((long)Client, true, DisplayName = "the client itself is foreground")]
    public void NoUsableForegroundWindow_LeavesTheZOrderAlone(long previousForeground, bool exists)
    {
        Assert.AreEqual(
            0,
            WindowsSandboxWindowController.ChooseWindowToSitBehind(Client, (nint)previousForeground, exists, previousForegroundIsTopmost: false));
    }

    /// <summary>
    /// With real windows: placing a window behind an always-on-top window must not make it
    /// always-on-top.
    /// </summary>
    [TestMethod]
    [DoNotParallelize] // Shows two real windows, one of them always-on-top.
    [Timeout(30_000, CooperativeCancellation = true)]
    public void PlacingBehindAnAlwaysOnTopWindow_DoesNotMakeTheClientAlwaysOnTop()
    {
        using var windows = new TwoWindows();

        if (!windows.Run(() => IsTopmost(windows.PinnedHandle)))
        {
            // Windows only lets a process pin a window it can activate; a locked-down test desktop may refuse.
            Assert.Inconclusive("This desktop would not make a test window always-on-top.");
        }

        Assert.IsFalse(windows.Run(() => IsTopmost(windows.ClientHandle)), "arrange failed: the client must start as a normal window");

        windows.Run(() => WindowsSandboxWindowController.PlaceBehindForeground(
            new CliHwnd(windows.ClientHandle),
            new CliHwnd(windows.PinnedHandle),
            new DesktopForegroundService()));

        Assert.IsFalse(
            windows.Run(() => IsTopmost(windows.ClientHandle)),
            "The Sandbox window must not become always-on-top just because the user's window is.");
    }

    private const int GwlExStyle = -20;
    private const int WsExTopmost = 0x8;

    private static readonly nint HwndTopmost = -1;
    private const uint SwpNoMoveNoSizeNoActivate = 0x0002 | 0x0001 | 0x0010;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint hwnd, int index);

    private static bool IsTopmost(nint hwnd) => (GetWindowLong(hwnd, GwlExStyle) & WsExTopmost) != 0;

    /// <summary>
    /// Two small WinForms windows on their own STA thread: an always-on-top window the user is working
    /// in, and a non-activating stand-in for the Sandbox client.
    /// </summary>
    private sealed class TwoWindows : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new();
        private Form _pinned = null!;
        private QuietForm _client = null!;

        public TwoWindows()
        {
            _thread = new Thread(() =>
            {
                // Shown normally, so it is the active, pinned window the user was working in.
                _pinned = new Form { Text = "winapp test: always on top", StartPosition = FormStartPosition.Manual, Left = 40, Top = 40, Width = 240, Height = 120 };
                _client = new QuietForm { Text = "winapp test: Sandbox stand-in", Left = 80, Top = 80 };
                _pinned.Show();
                SetWindowPos(_pinned.Handle, HwndTopmost, 0, 0, 0, 0, SwpNoMoveNoSizeNoActivate);
                _client.Show();
                PinnedHandle = _pinned.Handle;
                ClientHandle = _client.Handle;
                _ready.Set();
                Application.Run();
            });
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.IsBackground = true;
            _thread.Start();

            if (!_ready.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("The test windows did not appear.");
            }
        }

        public nint PinnedHandle { get; private set; }

        public nint ClientHandle { get; private set; }

        public T Run<T>(Func<T> action) => (T)_pinned.Invoke(action);

        public void Run(Action action) => _pinned.Invoke(action);

        public void Dispose()
        {
            try
            {
                _pinned.Invoke(() =>
                {
                    _client.Close();
                    _pinned.Close();
                    Application.ExitThread();
                });
                _thread.Join(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                // Already gone.
            }

            // Closing a shown form already disposes it; these only make that explicit.
            _client.Dispose();
            _pinned.Dispose();
            _ready.Dispose();
        }
    }

    /// <summary>A small window that never takes focus from the developer running the tests.</summary>
    private sealed class QuietForm : Form
    {
        public QuietForm()
        {
            StartPosition = FormStartPosition.Manual;
            Width = 240;
            Height = 120;
        }

        protected override bool ShowWithoutActivation => true;
    }
}
