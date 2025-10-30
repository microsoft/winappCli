// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Winsdk.Cli.Models;

namespace Winsdk.Cli.Services;

internal interface IConfigService
{
    FileInfo ConfigPath { get; set; }
    bool Exists();
    WinsdkConfig Load();
    void Save(WinsdkConfig cfg);
}
