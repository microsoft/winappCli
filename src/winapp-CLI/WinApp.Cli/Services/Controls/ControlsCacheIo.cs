// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Controls;

/// <summary>
/// Timestamp helper for the per-user find-ui cache. Atomic writes go through the
/// shared <see cref="WinApp.Cli.Helpers.PathSafety"/> helper; only the "o"-format
/// timestamp read is find-ui-specific and lives here.
/// </summary>
internal static class ControlsCacheIo
{
    /// <summary>
    /// Parse a round-trip ("o" format) timestamp from <paramref name="path"/>, returning a
    /// UTC <see cref="DateTime"/> or <c>null</c> if the file is missing/unreadable/unparseable.
    /// </summary>
    public static DateTime? ReadTimestamp(string path)
    {
        try
        {
            var text = File.ReadAllText(path).Trim();
            if (DateTime.TryParse(
                text,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var dt))
            {
                return dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
            }
            return null;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (Exception ex) when (!CacheStorage.IsStorageFailure(ex)) { return null; }
    }
}
