// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;

namespace WinApp.Cli.Commands;

internal class CacheCommand : Command
{
    public CacheCommand(CacheGetPathCommand cacheGetPathCommand, CacheMoveCommand cacheMoveCommand, CacheClearCommand cacheClearCommand)
        : base("cache", "Manage the packages cache location")
    {
        Subcommands.Add(cacheGetPathCommand);
        Subcommands.Add(cacheMoveCommand);
        Subcommands.Add(cacheClearCommand);
    }
}
