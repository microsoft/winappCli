using System.Text;

namespace Winsdk.Cli;

internal sealed class ConfigService
{
    private readonly string _configPath;
    public ConfigService(string workingDir)
    {
        _configPath = Path.Combine(workingDir, "winsdk.yaml");
    }

    public bool Exists() => File.Exists(_configPath);

    public WinsdkConfig Load()
    {
        if (!Exists()) return new WinsdkConfig();
        var text = File.ReadAllText(_configPath);
        return Parse(text);
    }

    public void Save(WinsdkConfig cfg)
    {
        var yaml = Stringify(cfg);
        File.WriteAllText(_configPath, yaml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static WinsdkConfig Parse(string yaml)
    {
        var cfg = new WinsdkConfig();
        using var sr = new StringReader(yaml);
        string? line;
        string? currentName = null;
        while ((line = sr.ReadLine()) != null)
        {
            var t = line.Trim();
            if (t.StartsWith('#') || t.Length == 0) continue;
            if (t.Equals("packages:", StringComparison.OrdinalIgnoreCase)) continue;

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

    private static string Stringify(WinsdkConfig cfg)
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
