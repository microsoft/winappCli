// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Win32;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Locates the Windows 10+ SDK install root (<c>Windows Kits\10</c>) the way the SDK's own tooling and
/// cswinrt do: <c>HKLM\SOFTWARE\Microsoft\Windows Kits\Installed Roots\KitsRoot10</c>, checked in both the
/// 32-bit and native registry views, so an SDK installed to a non-default drive is found.
/// </summary>
internal static class WindowsKitsRoot
{
    /// <summary>
    /// The registered <c>KitsRoot10</c>, or null when no Windows SDK is registered.
    /// </summary>
    public static string? GetRegistered()
    {
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows Kits\Installed Roots");
                if (key?.GetValue("KitsRoot10") is string root && !string.IsNullOrWhiteSpace(root))
                {
                    return root;
                }
            }
            catch (Exception)
            {
                // Registry view unavailable/inaccessible → treat as not registered for this view.
            }
        }

        return null;
    }

    /// <summary>
    /// The registered <c>KitsRoot10</c>, falling back to <c>%ProgramFiles(x86)%\Windows Kits\10</c>.
    /// </summary>
    public static string Resolve() =>
        GetRegistered()
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10");
}
