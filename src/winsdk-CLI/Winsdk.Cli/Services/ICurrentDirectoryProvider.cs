// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace Winsdk.Cli.Services;

internal interface ICurrentDirectoryProvider
{
    string GetCurrentDirectory();
    DirectoryInfo GetCurrentDirectoryInfo();
}
