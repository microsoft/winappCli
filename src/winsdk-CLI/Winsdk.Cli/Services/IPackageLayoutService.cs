// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace Winsdk.Cli.Services;

internal interface IPackageLayoutService
{
    public void CopyIncludesFromPackages(DirectoryInfo pkgsDir, DirectoryInfo includeOut);
    public void CopyLibsAllArch(DirectoryInfo pkgsDir, DirectoryInfo libRoot);
    public void CopyRuntimesAllArch(DirectoryInfo pkgsDir, DirectoryInfo binRoot);
    public IEnumerable<FileInfo> FindWinmds(DirectoryInfo pkgsDir, Dictionary<string, string> usedVersions);
}
