// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;

namespace WinApp.Cli.Helpers;

internal static class RunFailure
{
    internal static string Describe(Exception error)
    {
        var code = error is Win32Exception native ? native.NativeErrorCode
            : ((uint)error.HResult & 0xffff0000u) == 0x80070000u ? error.HResult & 0xffff : 0;
        var guidance = code switch
        {
            5 => "Access was denied. Check permissions on the app's input and staging files. " +
                "If a previous app instance is still running, close it explicitly before retrying; access denied alone does not identify a lock owner.",
            32 or 33 => "A required file is in use. Close the app using that file and retry; no lock owner has been identified.",
            2 => "A required file was not found. Check the selected app and build output.",
            3 => "A required directory was not found. Check the selected app and output paths.",
            _ => null,
        };
        if (guidance is null) { return error.Message; }
        // Release builds intentionally use BCL resource keys; keep useful custom path context instead.
        var detail = error.Message;
        var resourceKey = detail.StartsWith("UnauthorizedAccess_", StringComparison.Ordinal) ||
            detail.StartsWith("IO_", StringComparison.Ordinal) ||
            detail.StartsWith("Arg_", StringComparison.Ordinal);
        var context = resourceKey ? string.Empty : $" {detail}";
        return $"{guidance} (Win32 {code}, HRESULT 0x{error.HResult:X8}).{context}";
    }
}
