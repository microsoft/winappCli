// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;

namespace WinApp.Cli.Helpers;

internal static class RunFailure
{
    /// <param name="error">The staging or launch failure.</param>
    /// <param name="runningFrom">PIDs of processes whose image is the file that could not be written, if any.</param>
    internal static string Describe(Exception error, IReadOnlyList<int>? runningFrom = null)
    {
        var code = error is Win32Exception native ? native.NativeErrorCode
            : ((uint)error.HResult & 0xffff0000u) == 0x80070000u ? error.HResult & 0xffff : 0;
        if (code is 5 or 32 or 33 && runningFrom is { Count: > 0 })
        {
            return $"The app is still running from this build (PID {string.Join(", ", runningFrom)}), so its files cannot be replaced. " +
                $"Close it, then run again. (Win32 {code}, HRESULT 0x{error.HResult:X8}).";
        }
        var guidance = code switch
        {
            5 => "Access was denied. Check permissions on the app's input and staging files. " +
                "If a previous app instance is still running, close it explicitly before retrying; access denied alone does not identify a lock owner.",
            32 or 33 => "A required file is in use. Close the app using that file and retry; no lock owner has been identified.",
            2 => "A required file was not found. Check the selected app and build output.",
            3 => "A required directory was not found. Check the selected app and output paths.",
            _ => null,
        };
        if (guidance is null) { return error.Message; }
        // Release builds intentionally use BCL resource keys; keep useful custom path context instead.
        var detail = error.Message;
        var resourceKey = detail.StartsWith("UnauthorizedAccess_", StringComparison.Ordinal) ||
            detail.StartsWith("IO_", StringComparison.Ordinal) ||
            detail.StartsWith("Arg_", StringComparison.Ordinal);
        var context = resourceKey ? string.Empty : $" {detail}";
        return $"{guidance} (Win32 {code}, HRESULT 0x{error.HResult:X8}).{context}";
    }

    /// <summary>PIDs of processes running the app last registered from <paramref name="layout"/>.</summary>
    internal static IReadOnlyList<int> ProcessesRunningFromLayout(DirectoryInfo layout)
    {
        try
        {
            var manifest = Path.Combine(layout.FullName, "AppxManifest.xml");
            if (!File.Exists(manifest) ||
                Services.AppxManifestDocument.Load(manifest).ApplicationExecutable is not { Length: > 0 } executable ||
                executable.Contains('$'))
            {
                return [];
            }
            var root = Path.GetFullPath(layout.FullName).TrimEnd('\\') + '\\';
            var image = Path.GetFullPath(Path.Join(root, executable));
            return image.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? ProcessesRunningFrom(image) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or System.Xml.XmlException)
        {
            return [];
        }
    }

    internal static bool HasExited(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>PIDs of this user's processes whose executable is <paramref name="imagePath"/>.</summary>
    internal static IReadOnlyList<int> ProcessesRunningFrom(string imagePath)
    {
        var target = Path.GetFullPath(imagePath);
        var pids = new List<int>();
        foreach (var process in System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(target)))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(new Services.LaunchedProcess(process).ExecutablePath, target, StringComparison.OrdinalIgnoreCase))
                    {
                        pids.Add(process.Id);
                    }
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    // Another user's or an elevated process: it cannot be this user's staged app.
                }
            }
        }
        pids.Sort();
        return pids;
    }
}
