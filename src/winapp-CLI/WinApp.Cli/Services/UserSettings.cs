// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Services;

/// <summary>A per-user setting <c>winapp config</c> knows: its allowed values, built-in default and storage file.</summary>
internal sealed record ConfigKey(string Name, string Description, IReadOnlyList<string> Values, string Default, string FileName)
{
    // Telemetry records an argument through ToString, so this is what it sees: the key's name, never free text.
    public override string ToString() => Name;

    /// <summary>The allowed value spelled as stored, or null when <paramref name="value"/> isn't one.</summary>
    public string? Normalize(string value) =>
        Values.FirstOrDefault(allowed => string.Equals(allowed, value.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>A value accepted for a <see cref="ConfigKey"/>, in its stored spelling.</summary>
internal sealed record ConfigValue(string Value)
{
    public override string ToString() => Value;
}

/// <summary>A setting's current value, and whether the user set it or it is the built-in default.</summary>
internal sealed record ConfigEntry(ConfigKey Key, string Value, bool IsSet)
{
    public string Source => IsSet ? "setting" : "default";
}

/// <summary>
/// The per-user settings <c>winapp config</c> shows and changes, one file each in the per-user state folder. They
/// are separate from a project's winapp.yaml. Adding a setting is one entry in <see cref="Keys"/>.
/// </summary>
internal sealed class UserSettings
{
    /// <summary>How <c>winapp run</c> starts DevTools for a WinUI project. The DevTools toolbar's "When the app
    /// starts" menu reads and writes the same file (DevToolsSettings.h).</summary>
    public static readonly ConfigKey RunDevTools = new("run.devtools",
        "How winapp run starts DevTools for a WinUI project when --devtools isn't given",
        ["on", "off", "headless"], "on", "devtools-DefaultMode.setting");

    public static IReadOnlyList<ConfigKey> Keys { get; } = [RunDevTools];

    public static ConfigKey? Find(string name) =>
        Keys.FirstOrDefault(key => string.Equals(key.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Overrides the per-user state folder; tests point it at a temporary one.</summary>
    internal string? StateDirectory { get; set; }

    public string FilePath(ConfigKey key) =>
        Path.Combine(StateDirectory ?? WinappDirectoryService.GetUserStateDirectory(), key.FileName);

    /// <summary>The saved value, or the default when nothing valid is saved.</summary>
    public ConfigEntry Get(ConfigKey key)
    {
        try
        {
            if (key.Normalize(File.ReadAllText(FilePath(key))) is { } saved)
            {
                return new(key, saved, IsSet: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return new(key, key.Default, IsSet: false);
    }

    public void Set(ConfigKey key, ConfigValue value)
    {
        var path = FilePath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, value.Value);
    }

    public void Unset(ConfigKey key)
    {
        // File.Delete tolerates a missing file but not a missing folder, which a fresh profile doesn't have yet.
        var path = FilePath(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>The user's <c>run.devtools</c> mode, or null when they haven't set one.</summary>
    public DevToolsMode? ReadDevToolsMode() =>
        Get(RunDevTools) is { IsSet: true } entry ? Enum.Parse<DevToolsMode>(entry.Value, ignoreCase: true) : null;
}
