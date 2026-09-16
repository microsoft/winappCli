// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Stops a child process from inheriting this process's standard handles.
/// </summary>
/// <remarks>
/// <para>
/// When winapp is run by a script or an SDK that captures its output, winapp's own standard output
/// and error are pipe handles owned by that caller. The caller reads until end of stream, and end of
/// stream arrives only when the <em>last</em> handle to the write end closes — not when winapp
/// exits.
/// </para>
/// <para>
/// That matters because a Sandbox run deliberately leaves processes running after winapp returns:
/// the client window, and the <c>wsb exec</c> that hosts the persistent guest agent. Those outlive
/// winapp by design, so if they hold a duplicate of the caller's pipe the caller hangs on
/// <c>ReadToEnd</c> long after the command finished — the command looks finished and the terminal
/// looks frozen.
/// </para>
/// <para>
/// <b>Redirecting the child's own streams does not fix this.</b> .NET calls <c>CreateProcess</c>
/// with <c>bInheritHandles: true</c> whenever it redirects anything, so every inheritable handle in
/// this process — including the caller's pipes — is duplicated into the child regardless of what
/// <c>STARTF_USESTDHANDLES</c> points at. The duplicate is never used, and keeps the pipe open all
/// the same. Clearing <c>HANDLE_FLAG_INHERIT</c> for the duration of the launch is what actually
/// prevents the duplicate from being made.
/// </para>
/// <para>
/// The suppression is scoped as tightly as possible. Standard handles must stay inheritable for
/// ordinary children that write straight through to winapp's console — <c>winapp tool</c>, MSBuild,
/// and every pass-through build step — so this is applied around one launch and reversed
/// immediately, rather than set once at startup.
/// </para>
/// </remarks>
internal static partial class StandardHandleInheritance
{
    private const int StdInputHandle = -10;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;

    private const uint HandleFlagInherit = 0x00000001;

    /// <summary>
    /// Clears inheritance on the standard handles until the returned scope is disposed.
    /// </summary>
    /// <remarks>
    /// Only handles that were inheritable are restored, so a handle the host had already marked
    /// non-inheritable is left exactly as it was found. Every step is best effort: failing to adjust
    /// a handle must not fail the command, because the consequence is a caller that waits longer for
    /// end of stream rather than a broken run.
    /// </remarks>
    public static IDisposable Suppress() => new Scope();

    private sealed class Scope : IDisposable
    {
        /// <summary>
        /// Serializes suppression against other launches in this process.
        /// </summary>
        /// <remarks>
        /// Handle inheritance is process-global, so a scope open on one thread would otherwise strip
        /// inheritance from a child another thread is starting at that moment — which for a
        /// pass-through child means losing its output. The agent launch is fire-and-forget, so
        /// overlapping launches are possible in principle. <see cref="Monitor"/> is reentrant, so a
        /// nested scope on the same thread is safe.
        /// </remarks>
        private static readonly object Gate = new();

        private readonly List<nint> _restore = [];
        private readonly bool _held;
        private bool _disposed;

        public Scope()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            Monitor.Enter(Gate);
            _held = true;

            foreach (var id in (int[])[StdInputHandle, StdOutputHandle, StdErrorHandle])
            {
                try
                {
                    var handle = GetStdHandle(id);

                    if (handle == 0 || handle == -1)
                    {
                        continue;
                    }

                    if (!GetHandleInformation(handle, out var flags) || (flags & HandleFlagInherit) == 0)
                    {
                        // Already non-inheritable, or unknowable. Either way there is nothing to
                        // clear and nothing to put back.
                        continue;
                    }

                    if (SetHandleInformation(handle, HandleFlagInherit, 0))
                    {
                        _restore.Add(handle);
                    }
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
                {
                    return;
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                foreach (var handle in _restore)
                {
                    try
                    {
                        SetHandleInformation(handle, HandleFlagInherit, HandleFlagInherit);
                    }
                    catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
                    {
                        break;
                    }
                }

                _restore.Clear();
            }
            finally
            {
                if (_held)
                {
                    Monitor.Exit(Gate);
                }
            }
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GetStdHandle(int nStdHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetHandleInformation(nint hObject, out uint lpdwFlags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetHandleInformation(nint hObject, uint dwMask, uint dwFlags);
}
