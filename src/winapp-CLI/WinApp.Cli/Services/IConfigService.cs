// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

internal interface IConfigService
{
    FileInfo ConfigPath { get; set; }
    bool Exists();
    WinappConfig Load();
    void Save(WinappConfig cfg);
}
