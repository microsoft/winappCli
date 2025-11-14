// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

internal interface IDevModeService
{
    public int EnsureWin11DevMode();
    public bool IsEnabled();
}
