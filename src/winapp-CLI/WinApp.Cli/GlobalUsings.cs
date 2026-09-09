// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

// The UI Automation engine ships as its own package and lives in a single namespace. The CLI
// consumes it everywhere the `ui` verbs are implemented, so it is imported globally rather than
// repeated in every command and helper file.
global using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;
global using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Recording;
global using WinApp.Cli.Services.InteractiveDesktop;
