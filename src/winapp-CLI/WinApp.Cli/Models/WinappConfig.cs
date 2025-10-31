// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Models;

internal sealed class WinappConfig
{
    public List<PackagePin> Packages { get; set; } = new();

    public string? GetVersion(string name)
        => Packages.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Version;

    public void SetVersion(string name, string version)
    {
        var existing = Packages.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            Packages.Add(new PackagePin { Name = name, Version = version });
        }
        else
        {
            existing.Version = version;
        }
    }
}
