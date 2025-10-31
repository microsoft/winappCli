// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

internal sealed class ConfigService : IConfigService
{
    public FileInfo ConfigPath { get; set; }

    public ConfigService(ICurrentDirectoryProvider currentDirectoryProvider)
    {
        var workingDir = currentDirectoryProvider.GetCurrentDirectory();
        ConfigPath = new FileInfo(Path.Combine(workingDir, "winapp.yaml"));
    }

    public bool Exists()
    {
        ConfigPath.Refresh();
        return ConfigPath.Exists;
    }

    public WinappConfig Load()
    {
        if (!Exists())
        {
            return new WinappConfig();
        }

        var text = File.ReadAllText(ConfigPath.FullName);
        return Parse(text);
    }

    public void Save(WinappConfig cfg)
    {
        var yaml = Stringify(cfg);
        File.WriteAllText(ConfigPath.FullName, yaml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        ConfigPath.Refresh();
    }

    private static WinappConfig Parse(string yaml)
    {
        var cfg = new WinappConfig();
        using var sr = new StringReader(yaml);
        string? line;
        string? currentName = null;
        while ((line = sr.ReadLine()) != null)
        {
            var t = line.Trim();
            if (t.StartsWith('#') || t.Length == 0)
            {
                continue;
            }

            if (t.Equals("packages:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (t.StartsWith("- name:", StringComparison.OrdinalIgnoreCase))
            {
                currentName = t.Substring("- name:".Length).Trim().Trim('"', '\'');
            }
            else if (t.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
            {
                currentName = t.Substring("name:".Length).Trim().Trim('"', '\'');
            }
            else if (t.StartsWith("version:", StringComparison.OrdinalIgnoreCase) && currentName is not null)
            {
                var version = t.Substring("version:".Length).Trim().Trim('"', '\'');
                cfg.Packages.Add(new PackagePin { Name = currentName, Version = version });
                currentName = null;
            }
        }
        return cfg;
    }

    private static string Stringify(WinappConfig cfg)
    {
        var sb = new StringBuilder();
        sb.AppendLine("packages:");
        foreach (var p in cfg.Packages)
        {
            sb.AppendLine($"  - name: {p.Name}");
            sb.AppendLine($"    version: {p.Version}");
        }
        return sb.ToString();
    }
}
