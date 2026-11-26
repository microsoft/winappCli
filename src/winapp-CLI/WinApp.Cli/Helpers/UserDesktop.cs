// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Threading.Channels;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.StationsAndDesktops;

namespace WinApp.Cli.Helpers;

/// <summary>
/// The desktop the signed-in user is looking at, for a winapp that runs on a different one.
/// </summary>
/// <remarks>
/// <para>
/// Some agent tools run every command on a private desktop of their own (Google Antigravity names
/// them <c>exebox-…</c>). Windows a command opens there are invisible to the user, and window
/// lookups such as <c>FindWindowEx</c>, UI Automation and <c>GetForegroundWindow</c> only see that
/// private desktop. The next command, on another private desktop, then cannot find the window the
/// previous one opened.
/// </para>
/// <para>
/// The user's desktop is always <c>Default</c> in <c>WinSta0</c>. The input desktop is not used,
/// because while the workstation is locked it is the sign-in screen, where nothing must be opened.
/// </para>
/// </remarks>
internal static partial class UserDesktop
{
    /// <summary>The name of the user's interactive desktop.</summary>
    internal const string Name = "Default";

    /// <summary>The <c>lpDesktop</c> value that starts a process on the user's desktop.</summary>
    internal const string LaunchTarget = @"WinSta0\Default";

    private static readonly Lazy<string?> CurrentName = new(ReadCurrentDesktopName);
    private static readonly Lazy<DesktopThread?> Thread = new(DesktopThread.TryStart);

    /// <summary>
    /// Whether this process runs on a desktop other than the user's, so windows it opens or looks
    /// for there are not the ones the user sees.
    /// </summary>
    public static bool IsElsewhere => IsHidden(CurrentName.Value);

    /// <summary>The name of the desktop this process runs on, or null when it cannot be read.</summary>
    public static string? Current => CurrentName.Value;

    /// <summary>
    /// The one-line warning for a local <c>run</c> or <c>ui</c> command on a desktop the user cannot see,
    /// or null when this process runs on the user's desktop.
    /// </summary>
    public static string? HiddenDesktopWarning(bool uiCommand) => WarningFor(CurrentName.Value, uiCommand);

    /// <summary>Whether a desktop with this name is hidden from the user. Unknown is not hidden.</summary>
    internal static bool IsHidden(string? desktopName) =>
        desktopName is not null && !string.Equals(desktopName, Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>The warning for a process on <paramref name="desktopName"/>, or null when none is needed.</summary>
    /// <remarks>
    /// The two commands fail in opposite directions there: <c>run</c> opens an app the user cannot
    /// see, and <c>ui</c> cannot see the user's apps.
    /// </remarks>
    internal static string? WarningFor(string? desktopName, bool uiCommand) => !IsHidden(desktopName)
        ? null
        : uiCommand
            ? $"⚠️ winapp is running on a hidden desktop ({desktopName}) created by your agent tool, so it can't see " +
              "windows on your desktop. To test an app you can see, launch it with 'winapp run --on sandbox' and use --on sandbox here."
            : $"⚠️ winapp is running on a hidden desktop ({desktopName}) created by your agent tool. " +
              "Apps it launches locally won't be visible to you; use --on sandbox to see them.";

    /// <summary>
    /// Runs <paramref name="action"/> on a thread attached to the user's desktop when this process
    /// is elsewhere, and inline otherwise.
    /// </summary>
    /// <remarks>
    /// Window searches, UI Automation and placement all act on the calling thread's desktop, so the
    /// whole operation is moved rather than only the lookup. When the user's desktop cannot be
    /// opened, the action runs inline and sees this process's own desktop, exactly as before.
    /// </remarks>
    public static T Run<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return IsElsewhere && Thread.Value is { } thread ? thread.Invoke(action) : action();
    }

    /// <inheritdoc cref="Run{T}(Func{T})"/>
    public static void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _ = Run(() =>
        {
            action();
            return true;
        });
    }

    private static unsafe string? ReadCurrentDesktopName()
    {
        try
        {
            var desktop = PInvoke.GetThreadDesktop(PInvoke.GetCurrentThreadId());
            if (desktop.IsNull)
            {
                return null;
            }

            const int BufferChars = 256;
            char* buffer = stackalloc char[BufferChars];
            uint needed = 0;
            return PInvoke.GetUserObjectInformation(
                    new HANDLE(desktop.Value),
                    USER_OBJECT_INFORMATION_INDEX.UOI_NAME,
                    buffer,
                    sizeof(char) * BufferChars,
                    &needed)
                ? new string(buffer)
                : null;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }

    /// <summary>One background thread attached to the user's desktop, serving queued work.</summary>
    private sealed partial class DesktopThread
    {
        private readonly Channel<Action> _work =
            Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });

        private DesktopThread()
        {
        }

        public static DesktopThread? TryStart()
        {
            var result = new DesktopThread();
            var attached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new System.Threading.Thread(() => result.Serve(attached))
            {
                IsBackground = true,
                Name = "winapp user desktop",
            };
            thread.Start();
            return attached.Task.GetAwaiter().GetResult() ? result : null;
        }

        public T Invoke<T>(Func<T> action)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _work.Writer.TryWrite(() =>
            {
                try
                {
                    completion.SetResult(action());
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            });
            return completion.Task.GetAwaiter().GetResult();
        }

        private void Serve(TaskCompletionSource<bool> attached)
        {
            // SetThreadDesktop only succeeds on a thread that owns no windows or hooks yet, which is
            // why this is a fresh thread rather than the caller's.
            const uint DesktopReadObjects = 0x0001;
            const uint DesktopEnumerate = 0x0040;
            const uint DesktopWriteObjects = 0x0080;
            const uint DesktopCreateWindow = 0x0002;
            var desktop = OpenDesktop(Name, 0, false, DesktopReadObjects | DesktopEnumerate | DesktopWriteObjects | DesktopCreateWindow);
            if (desktop == 0 || !SetThreadDesktop(desktop))
            {
                attached.SetResult(false);
                return;
            }

            attached.SetResult(true);
            // Blocking this dedicated thread is the point: the work must run on it, never on a
            // thread-pool continuation attached to a different desktop.
            while (_work.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (_work.Reader.TryRead(out var item))
                {
                    item();
                }
            }
        }

        [LibraryImport("user32.dll", EntryPoint = "OpenDesktopW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        private static partial nint OpenDesktop(string name, uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetThreadDesktop(nint desktop);
    }
}
