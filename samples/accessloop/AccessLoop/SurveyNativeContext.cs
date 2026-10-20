using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

[assembly: InternalsVisibleTo("AccessLoop.Tests")]

namespace AccessLoop;

internal sealed record SurveyNativeWindow(
    bool Visible, bool Minimized, bool InteractiveWindowStation,
    string? InputDesktop, string? CurrentDesktop, string? TargetDesktop,
    int Width, int Height, uint Dpi);

internal interface ISurveyNativeContext
{
    SurveyNativeWindow ReadWindow(AuditTarget target);
    SurveyContext ReadContext(AuditTarget target, SurveyNativeWindow window);
}

internal sealed class SurveyNativeContext : ISurveyNativeContext
{
    private readonly Dictionary<string, FileHash> hashes = new(StringComparer.OrdinalIgnoreCase);

    public SurveyNativeWindow ReadWindow(AuditTarget target)
    {
        if (!OperatingSystem.IsWindows())
            throw new SurveyBlockedException("input-desktop-unavailable", "Existing-app navigation requires an interactive Windows desktop.", false);
        var hwnd = checked((nint)target.WindowHandle);
        var thread = GetWindowThreadProcessId(hwnd, out _);
        var station = GetProcessWindowStation();
        var interactive = GetUserObjectFlags(station, 1, out var flags, Marshal.SizeOf<UserObjectFlags>(), out _)
            && (flags.Flags & 1) != 0;
        // Request access only. Never switch desktops, unlock the session, or require existing foreground focus.
        var desktop = OpenInputDesktop(0, false, 0x0001 | 0x0080);
        string? inputName;
        try { inputName = ObjectName(desktop); }
        finally
        {
            if (desktop != 0)
                _ = CloseDesktop(desktop);
        }

        // DWM's extended frame bounds are physical pixels, unlike DPI-virtualized GetWindowRect.
        var rectKnown = DwmGetWindowAttribute(hwnd, 9, out var bounds, Marshal.SizeOf<WindowRect>()) == 0;
        return new(IsWindowVisible(hwnd), IsIconic(hwnd), interactive, inputName,
            ObjectName(GetThreadDesktop(GetCurrentThreadId())), ObjectName(GetThreadDesktop(thread)),
            rectKnown ? checked(bounds.Right - bounds.Left) : 0,
            rectKnown ? checked(bounds.Bottom - bounds.Top) : 0, GetDpiForWindow(hwnd));
    }

    internal static void ValidateWindow(SurveyNativeWindow window)
    {
        if (window.Minimized)
            throw new SurveyBlockedException("window-minimized", "Restore the selected window manually before resuming navigation.", true);
        if (!window.Visible)
            throw new SurveyBlockedException("window-not-visible", "The selected window is not visible. Make it visible manually before resuming.", true);
        if (!window.InteractiveWindowStation || window.InputDesktop != "Default"
            || window.CurrentDesktop != "Default" || window.TargetDesktop != "Default")
            throw new SurveyBlockedException("input-desktop-unavailable",
                "Navigation requires the selected window and this runner on the input-capable Default desktop. No desktop switch or unlock was attempted.", true);
        if (window.Width <= 0 || window.Height <= 0 || window.Dpi == 0)
            throw new SurveyBlockedException("window-metrics-unavailable",
                "The selected window's physical dimensions or DPI could not be established.", true);
    }

    public SurveyContext ReadContext(AuditTarget target, SurveyNativeWindow window)
    {
        if (!OperatingSystem.IsWindows())
            throw new SurveyBlockedException("environment-unavailable", "Installed-app metadata requires Windows.", false);
        using var process = Process.GetProcessById(target.ProcessId);
        var executable = process.MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable))
            throw new SurveyBlockedException("application-identity-unavailable", "The selected process's executable path is unavailable.", false);
        executable = Path.GetFullPath(executable);
        var hash = HashFile(executable);
        var productVersion = FileVersionInfo.GetVersionInfo(executable).ProductVersion;
        if (string.IsNullOrWhiteSpace(productVersion))
            throw new SurveyBlockedException("application-identity-unavailable", "The selected executable has no readable ProductVersion.", false);
        if (HashFile(executable) != hash)
            throw new SurveyBlockedException("application-changed", "The installed executable changed while its metadata was being read.", false);
        var companion = Path.Combine(Path.GetDirectoryName(executable)!, target.ProcessName + ".dll");
        string? companionHash = null;
        var companionExists = false;
        try
        {
            _ = File.GetAttributes(companion);
            companionExists = true;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        if (companionExists)
            companionHash = HashFile(companion);

        var highContrast = new HighContrast { Size = (uint)Marshal.SizeOf<HighContrast>() };
        if (!SystemParametersInfo(0x0042, highContrast.Size, ref highContrast, 0))
            throw new SurveyBlockedException("environment-unavailable", "The system high-contrast state could not be read.", true);
        bool? lightTheme = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", writable: false);
            lightTheme = key?.GetValue("AppsUseLightTheme", null, RegistryValueOptions.DoNotExpandEnvironmentNames) switch
            {
                0 => false,
                1 => true,
                _ => null
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { }

        return new(new(target.ProcessName, executable, hash, productVersion, companionHash),
            new(window.Width, window.Height, window.Dpi, Environment.OSVersion.VersionString,
                CultureInfo.CurrentUICulture.Name, (highContrast.Flags & 1) != 0, lightTheme));
    }

    internal string HashFile(string path)
    {
        path = Path.GetFullPath(path);
        var before = Stamp(path);
        if (hashes.TryGetValue(path, out var cached) && cached.Length == before.Length && cached.LastWriteUtc == before.LastWriteUtc)
            return cached.Hash;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        if (before != Stamp(path) || stream.Length != before.Length)
            throw new SurveyBlockedException("application-changed", "An installed binary changed while its fingerprint was being read.", false);
        hashes[path] = new(before.Length, before.LastWriteUtc, hash);
        return hash;
    }

    private static (long Length, DateTime LastWriteUtc) Stamp(string path)
    {
        var file = new FileInfo(path);
        return (file.Length, file.LastWriteTimeUtc);
    }

    private static string? ObjectName(nint handle)
    {
        if (handle == 0)
            return null;
        var name = new StringBuilder(256);
        return GetUserObjectInformation(handle, 2, name, name.Capacity * sizeof(char), out _) ? name.ToString() : null;
    }

    private sealed record FileHash(long Length, DateTime LastWriteUtc, string Hash);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UserObjectFlags
    {
        public int Inherit, Reserved;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrast
    {
        public uint Size, Flags;
        public nint DefaultScheme;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out WindowRect value, int size);
    [DllImport("user32.dll")]
    private static extern nint GetProcessWindowStation();
    [DllImport("user32.dll")]
    private static extern nint GetThreadDesktop(uint threadId);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]
    private static extern nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder value, int size, out int needed);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectFlags(nint handle, int index, out UserObjectFlags value, int size, out int needed);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint size, ref HighContrast value, uint flags);
}
