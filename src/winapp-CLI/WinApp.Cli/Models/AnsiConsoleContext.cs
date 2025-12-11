// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Spectre.Console;

namespace WinApp.Cli.Models;

internal record AnsiConsoleContext(IAnsiConsole AnsiConsole, IAnsiConsole NonExclusiveAnsiConsole);
