// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.DevTools;

/// <summary>How <c>winapp run</c> starts DevTools. Headless attaches DevTools without showing the toolbar.</summary>
public enum DevToolsMode
{
    On,
    Off,
    Headless,
}

/// <summary>Why a run got its DevTools mode. Recorded in telemetry by name.</summary>
public enum DevToolsModeSource
{
    Explicit,
    Setting,
    Default,
    Ci,
    IncompatibleOption,
    NotWinUI,
}

/// <summary>What DevTools did in a run that used it. Recorded in telemetry by name.</summary>
public enum DevToolsOutcome
{
    Attached,
    FellBack,
    Failed,
}

internal sealed record DevToolsResolution(DevToolsMode Mode, DevToolsModeSource Source)
{
    public bool Enabled => Mode != DevToolsMode.Off;

    public bool ShowToolbar => Mode == DevToolsMode.On;

    /// <summary>A mode the user didn't ask for on this command line must never turn a working run into an error.</summary>
    public bool FailOpen => Source is DevToolsModeSource.Setting or DevToolsModeSource.Default;

    /// <summary>The run's mode, first match wins: the flag, CI, an option DevTools can't work with, a non-WinUI
    /// or non-project run, the user's default, then on.</summary>
    public static DevToolsResolution Resolve(DevToolsMode? requested, bool ci, bool incompatibleOption, bool winUIProject,
        DevToolsMode? setting) =>
        requested is { } mode ? new(mode, DevToolsModeSource.Explicit)
        : ci ? new(DevToolsMode.Off, DevToolsModeSource.Ci)
        : incompatibleOption ? new(DevToolsMode.Off, DevToolsModeSource.IncompatibleOption)
        : !winUIProject ? new(DevToolsMode.Off, DevToolsModeSource.NotWinUI)
        : setting is { } saved ? new(saved, DevToolsModeSource.Setting)
        : new(DevToolsMode.On, DevToolsModeSource.Default);

    /// <summary>CI systems set <c>CI</c>; an empty value, <c>false</c> or <c>0</c> means it isn't one.</summary>
    public static bool IsCi(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim() is not ("0" or "false" or "False" or "FALSE");
}

/// <summary>
/// The user's default DevTools mode, one file in the per-user state folder. The DevTools toolbar reads and writes
/// the same file (DevToolsSettings.h), so its "When the app starts" menu items and
/// <c>winapp devtools default</c> change the same setting.
/// </summary>
internal static class DevToolsDefaultSetting
{
    public static string FilePath(string? stateDirectory = null) =>
        Path.Combine(stateDirectory ?? WinappDirectoryService.GetUserStateDirectory(), "devtools-DefaultMode.setting");

    /// <summary>The saved mode, or null when none is saved or the file holds something else.</summary>
    public static DevToolsMode? Read(string? stateDirectory = null)
    {
        try
        {
            var text = File.ReadAllText(FilePath(stateDirectory)).Trim();
            return Enum.TryParse<DevToolsMode>(text, ignoreCase: true, out var mode) && Enum.IsDefined(mode) &&
                !int.TryParse(text, out _) ? mode : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    public static void Write(DevToolsMode mode, string? stateDirectory = null)
    {
        var path = FilePath(stateDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, mode.ToString().ToLowerInvariant());
    }
}
