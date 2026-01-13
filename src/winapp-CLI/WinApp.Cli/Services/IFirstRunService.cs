// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

internal interface IFirstRunService
{
    public bool CheckAndDisplayFirstRunNotice();
}
