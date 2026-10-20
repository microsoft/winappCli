// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.InteropServices.Marshalling;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

internal sealed class NativeUiAuditSnapshotService : IUiAuditSnapshotService
{
    public Task<UiAuditSnapshotResult> CaptureAsync(UiTarget target, UiAuditSnapshotOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        // UiTarget is mutable. Nothing on the worker may re-read the caller's instance.
        var selected = new UiTarget { ProcessId = target.ProcessId, WindowHandle = target.WindowHandle, IsExplicitWindow = true };
        ValidateTarget(selected);
        cancellationToken.ThrowIfCancellationRequested();
        var capturedAt = DateTimeOffset.UtcNow;
        var budget = new UiAuditSnapshotBudget(options, cancellationToken);
        return RunOnMta(() => Capture(selected, options, budget, capturedAt));
    }

    internal static void ValidateOptions(UiAuditSnapshotOptions options)
    {
        if (options.MaxDepth is < 1 or > 50 || options.MaxElements is < 1 or > 10000
            || options.MaxTextRunsPerNode is < 1 or > 32 || options.TimeoutMs is < 1 or > 120000)
        {
            throw new UiAuditSnapshotException("invalid_arguments",
                "Snapshot depth must be 1–50, max elements 1–10000, text runs per node 1–32, and timeout 1–120000 milliseconds.");
        }
    }

    internal static void ValidateTarget(UiTarget target)
    {
        if (target.ProcessId <= 0 || target.WindowHandle <= 0 || (nint.Size == 4 && target.WindowHandle > int.MaxValue))
        {
            throw new UiAuditSnapshotException("invalid_target", "A snapshot requires an exact nonzero HWND and a positive, matching process ID.");
        }
    }

    private static UiAuditSnapshotResult Capture(UiTarget target, UiAuditSnapshotOptions options,
        UiAuditSnapshotBudget budget, DateTimeOffset capturedAt)
    {
        var state = new UiAuditSnapshotState(options, budget);
        state.Issue(state.Issues, "privacy_notice",
            "Accessible names can contain user data. Password or indeterminate-privacy names and text details are withheld; text content and Value properties are never collected.",
            partial: false);

        NativeUiAuditSnapshotReader reader;
        try
        {
            reader = new NativeUiAuditSnapshotReader(target, state);
        }
        catch (UiAuditSnapshotStoppedException ex)
        {
            throw new UiAuditSnapshotException(ex.Code, "Capture stopped before the selected UIA root could be verified.");
        }
        catch (Exception ex) when (NativeUiAuditSnapshotReader.IsProviderFailure(ex))
        {
            throw new UiAuditSnapshotException("uia_unavailable", "UI Automation could not verify the selected window. No other window was inspected.");
        }

        using (reader)
        {
            var recheckRoot = true;
            try
            {
                UiAuditSnapshotTraversal.Walk(reader.Root, reader, state);
            }
            catch (UiAuditSnapshotStoppedException ex)
            {
                state.Issue(state.Issues, ex.Code, ex.Message);
                recheckRoot = false;
            }
            catch (UiAuditSnapshotException ex)
            {
                state.Issue(state.Issues, ex.Code, ex.Message);
            }
            catch (Exception ex) when (NativeUiAuditSnapshotReader.IsProviderFailure(ex))
            {
                state.Issue(state.Issues, "tree_read_failed", NativeUiAuditSnapshotReader.FailureMessage("Raw-tree navigation", ex));
            }

            try
            {
                reader.VerifyNativeTarget();
                if (recheckRoot)
                {
                    reader.VerifyFinalRoot();
                }
                else
                {
                    state.Issue(state.Issues, "root_identity_not_rechecked",
                        "The native HWND and process were rechecked, but cancellation or the deadline prevented a final UIA-root identity check.");
                }
            }
            catch (UiAuditSnapshotStoppedException)
            {
                state.Issue(state.Issues, "root_identity_not_rechecked", "The cooperative bound prevented a final UIA-root identity check.");
            }
            catch (UiAuditSnapshotException ex)
            {
                state.Issue(state.Issues, ex.Code, ex.Message);
            }
            catch (Exception ex) when (NativeUiAuditSnapshotReader.IsProviderFailure(ex))
            {
                state.Issue(state.Issues, "root_identity_not_rechecked", NativeUiAuditSnapshotReader.FailureMessage("Final UIA-root identity", ex));
            }

            return new UiAuditSnapshotResult
            {
                CapturedAtUtc = capturedAt,
                ProcessId = target.ProcessId,
                WindowHandle = target.WindowHandle,
                RootRuntimeId = reader.InitialRuntimeId,
                Title = state.Nodes.FirstOrDefault()?.Name,
                Status = state.Partial ? "partial" : "captured",
                MaxDepth = options.MaxDepth,
                MaxElements = options.MaxElements,
                DepthLimitReached = state.DepthLimitReached,
                ElementLimitReached = state.ElementLimitReached,
                Nodes = state.Nodes.ToArray(),
                Issues = state.Issues.ToArray(),
            };
        }
    }

    internal static Task<T> RunOnMta<T>(Func<T> operation)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            var initialized = false;
            T result = default!;
            Exception? failure = null;
            try
            {
                unsafe
                {
                    PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED).ThrowOnFailure();
                }
                initialized = true;
                result = operation();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                if (initialized)
                {
                    PInvoke.CoUninitialize();
                }
            }
            if (failure is OperationCanceledException cancelled)
            {
                completion.SetCanceled(cancelled.CancellationToken);
            }
            else if (failure is not null)
            {
                completion.SetException(failure);
            }
            else
            {
                completion.SetResult(result);
            }
        })
        {
            IsBackground = true,
            Name = "winapp UI audit snapshot",
        };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
        return completion.Task;
    }
}

internal sealed class NativeUiAuditSnapshotScope : IDisposable
{
    private readonly Process _process;
    private readonly nint _processHandle;
    private readonly uint _windowThread;
    internal HWND Hwnd { get; }
    internal int ProcessId { get; }

    internal unsafe NativeUiAuditSnapshotScope(UiTarget target)
    {
        NativeUiAuditSnapshotService.ValidateTarget(target);
        Hwnd = new HWND(checked((nint)target.WindowHandle));
        ProcessId = target.ProcessId;
        uint actualPid = 0;
        _windowThread = PInvoke.GetWindowThreadProcessId(Hwnd, &actualPid);
        if (!PInvoke.IsWindow(Hwnd) || _windowThread == 0)
        {
            throw new UiAuditSnapshotException("invalid_target", "The selected HWND does not identify an existing window.");
        }
        if (actualPid != ProcessId)
        {
            throw new UiAuditSnapshotException("target_mismatch", "The selected HWND is not owned by the supplied process ID.");
        }
        if (PInvoke.GetAncestor(Hwnd, GET_ANCESTOR_FLAGS.GA_ROOT) != Hwnd)
        {
            throw new UiAuditSnapshotException("invalid_target", "Select a top-level window HWND, not an individual child control's HWND.");
        }
        try
        {
            _process = Process.GetProcessById(ProcessId);
        }
        catch (ArgumentException)
        {
            throw new UiAuditSnapshotException("invalid_target", "The selected process is no longer available.");
        }
        try
        {
            // Holding the original process handle prevents a reused PID from becoming a new target.
            _processHandle = _process.Handle;
            Verify();
        }
        catch
        {
            _process.Dispose();
            throw new UiAuditSnapshotException("target_unavailable", "The original window and process identity could not be verified.");
        }
    }

    internal unsafe void Verify()
    {
        uint actualPid = 0;
        if (!PInvoke.IsWindow(Hwnd) || PInvoke.GetWindowThreadProcessId(Hwnd, &actualPid) != _windowThread
            || actualPid != ProcessId || PInvoke.GetAncestor(Hwnd, GET_ANCESTOR_FLAGS.GA_ROOT) != Hwnd
            || PInvoke.WaitForSingleObject(new HANDLE(_processHandle), 0) != WAIT_EVENT.WAIT_TIMEOUT)
        {
            throw new UiAuditSnapshotException("target_changed", "The original window or process no longer matches the selected target.");
        }
    }

    public void Dispose() => _process.Dispose();
}

internal sealed class NativeUiAuditSnapshotComScope(params object?[] borrowed) : IDisposable
{
    private readonly List<object> _objects = [];
    private readonly HashSet<object> _seen = new(ReferenceEqualityComparer.Instance);
    private readonly NativeUiAuditSnapshotComScope? _borrowedScope;

    internal NativeUiAuditSnapshotComScope(NativeUiAuditSnapshotComScope borrowedScope) : this([])
    {
        _borrowedScope = borrowedScope;
    }

    internal T? Add<T>(T? value) where T : class
    {
        if (value is not null && !Borrows(value) && _seen.Add(value))
        {
            _objects.Add(value);
        }
        return value;
    }

    private bool Borrows(object value) => borrowed.Any(item => ReferenceEquals(item, value))
        || (_borrowedScope is not null && (_borrowedScope._seen.Contains(value) || _borrowedScope.Borrows(value)));

    internal static void Release(object? value)
    {
        if (value is ComObject wrapper)
        {
            wrapper.FinalRelease();
        }
        else if (value is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    public void Dispose()
    {
        for (var index = _objects.Count - 1; index >= 0; index--)
        {
            Release(_objects[index]);
        }
        _objects.Clear();
        _seen.Clear();
    }
}
