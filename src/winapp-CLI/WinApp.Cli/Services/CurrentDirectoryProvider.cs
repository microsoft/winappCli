// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

internal class CurrentDirectoryProvider(string overrideDirectory) : ICurrentDirectoryProvider
{
    public string GetCurrentDirectory()
    {
        return overrideDirectory;
    }

    public DirectoryInfo GetCurrentDirectoryInfo()
    {
        return new DirectoryInfo(GetCurrentDirectory());
    }
}
