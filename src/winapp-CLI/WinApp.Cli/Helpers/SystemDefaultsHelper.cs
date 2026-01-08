// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Text.RegularExpressions;

namespace WinApp.Cli.Helpers;

internal static partial class SystemDefaultsHelper
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    public static string GetDefaultPackageName(DirectoryInfo dir)
    {
        var folder = dir.Name;
        var normalized = WhitespaceRegex().Replace(folder.Trim(), "-").ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? "app" : normalized;
    }

    public static string GetDefaultPublisherCN()
    {
        var user = Environment.UserName;
        if (string.IsNullOrWhiteSpace(user))
        {
            user = "Developer";
        }

        return $"CN={user}";
    }

    public static string GetDefaultDescription()
    {
        return "My Application";
    }
}
