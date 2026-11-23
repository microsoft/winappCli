// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Helpers;

internal static class WinUIOutputDetector
{
    internal static bool MayContainWinUI3(DirectoryInfo folder)
    {
        try
        {
            // Recipes can reference files outside this folder.
            if (folder.EnumerateFiles("*.build.appxrecipe", SearchOption.TopDirectoryOnly).Any())
            {
                return true;
            }

            // Native WinUI apps can embed XAML in a resource index. Only check the input
            // folder: winapp also generates AppX/resources.pri when staging non-WinUI apps.
            if (folder.EnumerateFiles("*.pri", SearchOption.TopDirectoryOnly).Any())
            {
                return true;
            }

            return folder.EnumerateFiles("*", SearchOption.AllDirectories).Any(file =>
                file.Name.Equals("Microsoft.WinUI.dll", StringComparison.OrdinalIgnoreCase) ||
                file.Name.Equals("Microsoft.UI.Xaml.dll", StringComparison.OrdinalIgnoreCase) ||
                file.Name.Equals("Microsoft.WindowsAppRuntime.Bootstrap.dll", StringComparison.OrdinalIgnoreCase) ||
                file.Extension.Equals(".xbf", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An incomplete scan cannot establish that WinUI is absent.
            return true;
        }
    }
}
