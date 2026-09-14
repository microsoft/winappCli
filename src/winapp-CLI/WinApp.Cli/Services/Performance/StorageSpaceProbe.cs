// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Performance;

internal interface IStorageSpaceProbe
{
    long GetAvailableBytes(string path);
}

internal sealed class StorageSpaceProbe : IStorageSpaceProbe
{
    public long GetAvailableBytes(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path))
            ?? throw new IOException($"Cannot resolve the volume for '{path}'.");
        return new DriveInfo(root).AvailableFreeSpace;
    }
}
