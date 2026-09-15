// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using WinApp.Cli.Services;

namespace WinApp.Cli.ExecutionTargets.WindowsSandbox;

/// <summary>
/// The <c>wsb.exe</c> failures winapp has to tell apart, and how to recognise them.
/// </summary>
/// <remarks>
/// <c>wsb</c> reports COM failures as HRESULTs, sometimes as the process exit code and sometimes
/// only in its own diagnostic text. Two of them mean something specific enough that treating them
/// as a generic start failure would send the user somewhere useless: one says a Sandbox may have
/// been created despite the error, the other says one is already running.
/// </remarks>
internal static class WsbHResult
{
    /// <summary>Key under which a recognised HRESULT is reported in a failure envelope.</summary>
    internal const string ContextKey = "hresult";

    /// <summary>
    /// <c>ERROR_FILE_NOT_FOUND</c>. Observed from <c>wsb start</c> <em>after</em> it has already
    /// created a listed instance, so it never means "nothing happened".
    /// </summary>
    internal const int FileNotFound = unchecked((int)0x80070002);

    /// <summary>
    /// <c>CO_E_APPSINGLEUSE</c>. The Sandbox singleton is already in use, which is a reuse
    /// situation rather than a broken host.
    /// </summary>
    internal const int AppSingleUse = unchecked((int)0x800401F6);

    /// <summary>
    /// <c>ERROR_NO_SUCH_LOGON_SESSION</c>. The guest has no interactive login session, so nothing
    /// can be run as <c>ExistingLogin</c> until a client connects.
    /// </summary>
    /// <remarks>
    /// Measured, not assumed: on a Sandbox started by <c>wsb start</c> with no client,
    /// <c>wsb exec --run-as ExistingLogin</c> exits with this HRESULT and writes
    /// "A specified logon session does not exist" to standard error. After <c>wsb connect</c> the
    /// same command succeeds. That difference is the only cheap way to tell whether a guest already
    /// has a usable interactive session.
    /// </remarks>
    internal const int NoSuchLogonSession = unchecked((int)0x80070520);

    /// <summary>Formats an HRESULT the way it is reported in context and in wsb's own output.</summary>
    internal static string Format(int hresult) =>
        "0x" + hresult.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>
    /// Extracts the HRESULT a <c>wsb</c> invocation failed with, when it reported one.
    /// </summary>
    /// <remarks>
    /// The exit code is checked first because it is unambiguous when present. Output is scanned
    /// second and only for a complete eight-digit hexadecimal value with the failure bit set, so an
    /// ordinary number in a message cannot be mistaken for a status code.
    /// </remarks>
    internal static int? Extract(ProcessRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.ExitCode < 0)
        {
            return result.ExitCode;
        }

        return Scan(result.StandardError) ?? Scan(result.StandardOutput);
    }

    private static int? Scan(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        for (var index = text.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
             index >= 0;
             index = text.IndexOf("0x", index + 2, StringComparison.OrdinalIgnoreCase))
        {
            var digits = text.AsSpan(index + 2);
            if (digits.Length < 8)
            {
                continue;
            }

            digits = digits[..8];

            if (!uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }

            // Only a failure HRESULT counts. A plain eight-digit hexadecimal number in a message --
            // an identifier, an offset -- is not a status code and must not be classified as one.
            if ((value & 0x80000000u) == 0)
            {
                continue;
            }

            return unchecked((int)value);
        }

        return null;
    }
}
