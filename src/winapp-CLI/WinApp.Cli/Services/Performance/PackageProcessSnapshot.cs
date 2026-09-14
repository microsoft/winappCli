// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Diagnostics;
using Windows.Win32.Foundation;

namespace WinApp.Cli.Services.Performance;

internal interface IPackageProcessSnapshot
{
    IReadOnlyList<ProcessIdentity> Capture(string packageFamilyName);
}

/// <summary>
/// Takes a best-effort user-mode snapshot of process generations carrying one package identity.
/// Snapshot misses are reported later as coverage limits; this service never infers package
/// membership from executable names or parent PIDs.
/// </summary>
internal sealed class PackageProcessSnapshot : IPackageProcessSnapshot
{
    public IReadOnlyList<ProcessIdentity> Capture(string packageFamilyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageFamilyName);

        var matches = new List<ProcessIdentity>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var familyName = TryGetPackageFamilyName(process);
                    if (!string.Equals(familyName, packageFamilyName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    matches.Add(new(
                        process.Id,
                        process.StartTime.ToUniversalTime().Ticks));
                }
                catch (InvalidOperationException)
                {
                    // The process exited while the snapshot was being captured.
                }
                catch (Win32Exception)
                {
                    // Access to this process is unavailable; it cannot be positively admitted.
                }
            }
        }

        return matches;
    }

    internal static string? TryGetPackageFamilyName(Process process)
    {
        uint length = 0;
        var result = global::Windows.Win32.PInvoke.GetPackageFamilyName(
            process.SafeHandle,
            ref length,
            []);

        if (result == WIN32_ERROR.APPMODEL_ERROR_NO_PACKAGE)
        {
            return null;
        }

        if (result != WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER || length == 0)
        {
            throw new Win32Exception(unchecked((int)result));
        }

        var buffer = new char[length];
        result = global::Windows.Win32.PInvoke.GetPackageFamilyName(
            process.SafeHandle,
            ref length,
            buffer);
        if (result != WIN32_ERROR.ERROR_SUCCESS)
        {
            throw new Win32Exception(unchecked((int)result));
        }

        var terminator = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, terminator >= 0 ? terminator : buffer.Length);
    }
}
