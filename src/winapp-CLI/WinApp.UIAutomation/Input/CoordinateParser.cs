// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Shared parser for screen-coordinate tokens in the <c>x,y</c> form reported by <c>ui inspect</c>.
/// </summary>
public static class CoordinateParser
{
    /// <summary>
    /// Parses an <c>x,y</c> coordinate token into a <see cref="PointerPoint"/>.
    /// </summary>
    /// <param name="value">Coordinate token to parse.</param>
    /// <param name="point">Parsed point when the method returns <see langword="true"/>; otherwise the default point.</param>
    /// <returns><see langword="true"/> when both coordinates parse as integers; otherwise <see langword="false"/>.</returns>
    public static bool TryParsePoint(string? value, out PointerPoint point)
    {
        point = default;
        if (TryParsePoint(value, out int x, out int y))
        {
            point = new PointerPoint(x, y);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Parses an <c>x,y</c> coordinate token into integer coordinates.
    /// </summary>
    /// <param name="value">Coordinate token to parse.</param>
    /// <param name="x">Parsed X coordinate when the method returns <see langword="true"/>; otherwise 0.</param>
    /// <param name="y">Parsed Y coordinate when the method returns <see langword="true"/>; otherwise 0.</param>
    /// <returns><see langword="true"/> when both coordinates parse as integers; otherwise <see langword="false"/>.</returns>
    public static bool TryParsePoint(string? value, out int x, out int y)
    {
        x = 0;
        y = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return false;
        }

        return int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out x)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out y);
    }

    /// <summary>
    /// Whether a token that failed to parse as a point was nonetheless meant as coordinates: it has a
    /// comma and its first field is an integer.
    /// </summary>
    public static bool LooksLikeCoordinates(string token)
    {
        var parts = token.Split(',');
        return parts.Length >= 2
            && int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
    }
}
