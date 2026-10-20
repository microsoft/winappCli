// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.System.StationsAndDesktops;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

internal sealed class NativeUiAuditObservationScope : IDisposable
{
    private readonly Process _process;
    private readonly nint _processHandle;
    private readonly uint _windowThread;
    private bool _disposed;

    internal HWND Hwnd { get; }
    internal int ProcessId { get; }
    internal long WindowHandle => (nint)Hwnd;

    internal unsafe NativeUiAuditObservationScope(UiTarget target)
    {
        Hwnd = new HWND(checked((nint)target.WindowHandle));
        uint pid = 0;
        _windowThread = PInvoke.GetWindowThreadProcessId(Hwnd, &pid);
        if (Hwnd.IsNull || _windowThread == 0 || pid == 0 ||
            (target.ProcessId != 0 && target.ProcessId != pid) ||
            PInvoke.GetAncestor(Hwnd, GET_ANCESTOR_FLAGS.GA_ROOT) != Hwnd ||
            !PInvoke.IsWindowVisible(Hwnd))
        {
            throw new UiAuditObservationException("invalid_target",
                "Select one visible top-level application window with --window or --app <PID>.");
        }

        ProcessId = checked((int)pid);
        try
        {
            _process = Process.GetProcessById(ProcessId);
        }
        catch
        {
            throw new UiAuditObservationException("target_unavailable",
                "Windows could not verify the original selected process.");
        }
        try
        {
            // Holding the original process handle rejects a later process with a recycled PID.
            _processHandle = _process.Handle;
            if (!TargetExists)
            {
                throw new UiAuditObservationException("target_closed", "The selected process is no longer available.");
            }
        }
        catch (UiAuditObservationException)
        {
            _process.Dispose();
            throw;
        }
        catch
        {
            _process.Dispose();
            throw new UiAuditObservationException("target_unavailable",
                "Windows could not retain a handle to the original selected process. Check access permissions.");
        }
    }

    internal unsafe bool TargetExists
    {
        get
        {
            if (_disposed || PInvoke.WaitForSingleObject(new HANDLE(_processHandle), 0) != WAIT_EVENT.WAIT_TIMEOUT)
            {
                return false;
            }
            uint pid = 0;
            return PInvoke.GetWindowThreadProcessId(Hwnd, &pid) == _windowThread && pid == ProcessId
                && PInvoke.GetAncestor(Hwnd, GET_ANCESTOR_FLAGS.GA_ROOT) == Hwnd
                && PInvoke.IsWindowVisible(Hwnd);
        }
    }

    internal bool IsForeground => TargetExists && PInvoke.GetForegroundWindow() == Hwnd;

    internal unsafe bool OwnsWindow(HWND hwnd)
    {
        uint pid = 0;
        return !hwnd.IsNull && PInvoke.GetWindowThreadProcessId(hwnd, &pid) != 0
            && Matches(ProcessId, WindowHandle, pid, (nint)PInvoke.GetAncestor(hwnd, GET_ANCESTOR_FLAGS.GA_ROOT));
    }

    internal static bool Matches(int processId, long root, uint candidateProcessId, long candidateRoot)
        => processId > 0 && root > 0 && candidateProcessId == processId && candidateRoot == root;

    internal static unsafe bool HasInteractiveDesktop()
    {
        if (PInvoke.GetForegroundWindow().IsNull)
        {
            return false;
        }
        var desktop = PInvoke.OpenInputDesktop(0, false, DESKTOP_ACCESS_FLAGS.DESKTOP_READOBJECTS);
        if (desktop.IsNull)
        {
            return false;
        }
        try
        {
            var current = PInvoke.GetThreadDesktop(PInvoke.GetCurrentThreadId());
            Span<char> inputName = stackalloc char[256];
            Span<char> currentName = stackalloc char[256];
            return ReadName(desktop, inputName, out var inputLength)
                && ReadName(current, currentName, out var currentLength)
                && inputName[..inputLength].SequenceEqual(currentName[..currentLength]);
        }
        finally
        {
            PInvoke.CloseDesktop(desktop);
        }
    }

    private static unsafe bool ReadName(HDESK desktop, Span<char> name, out int length)
    {
        uint bytes = 0;
        fixed (char* buffer = name)
        {
            var read = PInvoke.GetUserObjectInformation(new HANDLE((nint)desktop),
                USER_OBJECT_INFORMATION_INDEX.UOI_NAME, buffer, checked((uint)(name.Length * sizeof(char))), &bytes);
            length = (int)(bytes / sizeof(char));
            return read && length > 1 && length <= name.Length;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _process.Dispose();
        }
    }
}

internal sealed class NativeUiAuditObservationCallContext : IDisposable
{
    private readonly CancellationTokenRegistration _cancellation;
    private bool _disposed;

    internal unsafe NativeUiAuditObservationCallContext(CancellationToken cancellationToken)
    {
        PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED).ThrowOnFailure();
        try
        {
            PInvoke.CoEnableCallCancellation(null).ThrowOnFailure();
            var threadId = PInvoke.GetCurrentThreadId();
            _cancellation = cancellationToken.Register(() => PInvoke.CoCancelCall(threadId, 0));
        }
        catch
        {
            PInvoke.CoUninitialize();
            throw new UiAuditObservationException("com_cancellation_unavailable",
                "Cancellable UI Automation calls are unavailable on this desktop; observation did not start.");
        }
    }

    public unsafe void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _cancellation.Dispose();
            PInvoke.CoDisableCallCancellation(null);
            PInvoke.CoUninitialize();
        }
    }
}
