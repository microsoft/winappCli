// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// Writes a minidump for a crashed process and analyzes it using ClrMD
/// for managed exceptions and DbgEng for native stack traces.
/// </summary>
internal interface ICrashDumpService
{
    /// <summary>
    /// Writes a minidump of the specified process and returns the dump file path.
    /// Must be called while the process is still alive (e.g., after a second-chance
    /// exception before continuing with <c>DBG_EXCEPTION_NOT_HANDLED</c>).
    /// </summary>
    /// <param name="processId">The ID of the process to dump.</param>
    /// <param name="savedContext">
    /// Context of the crashing thread — captured at an earlier critical first-chance exception whose
    /// frame is still live at the crash, otherwise at the crash itself — or null.
    /// </param>
    /// <param name="savedThreadId">Thread ID of the crashing thread.</param>
    /// <param name="savedExceptionCode">Exception code matching <paramref name="savedContext"/>.</param>
    /// <param name="savedExceptionAddress">Exception address matching <paramref name="savedContext"/>.</param>
    /// <param name="crashExceptionCode">
    /// Exception code of the terminating (second-chance) exception, or 0. When this is a stowed
    /// exception (<c>0xC000027B</c>) and <paramref name="crashExceptionParameters"/> are supplied,
    /// the dump's exception record carries those parameters so WinUI stowed-exception triage
    /// (<c>!xamlstowed</c>) can locate the stowed-exception array. The thread still uses
    /// <paramref name="savedContext"/>, which is the first-chance context only when the caller found
    /// that frame still live.
    /// </param>
    /// <param name="crashExceptionAddress">Address of the terminating (second-chance) exception, or 0.</param>
    /// <param name="crashExceptionParameters">
    /// The terminating exception's parameters (<c>EXCEPTION_RECORD.ExceptionInformation</c>); for a
    /// stowed exception, element 0 is the stowed-exception array pointer and element 1 is the count.
    /// </param>
    /// <returns>The full path to the dump file, or <c>null</c> if the dump failed.</returns>
    string? WriteMiniDump(uint processId,
        byte[]? savedContext, uint savedThreadId,
        int savedExceptionCode, nuint savedExceptionAddress,
        int crashExceptionCode = 0, nuint crashExceptionAddress = 0,
        nuint[]? crashExceptionParameters = null);

    /// <summary>
    /// Analyzes a minidump and prints a crash summary to the console.
    /// Uses ClrMD for managed exceptions; falls back to DbgEng for native stack traces.
    /// Full analysis output is appended to the log file for detailed investigation.
    /// </summary>
    /// <param name="dumpPath">Path to the minidump file.</param>
    /// <param name="logPath">Path to the debug log file where full analysis is appended.</param>
    /// <param name="useSymbols">When true, downloads symbols from Microsoft Symbol Server for richer native analysis.</param>
    /// <param name="symbolSearchPaths">Additional directories to search for PDB files (e.g., the build output folder). Used to resolve source file and line numbers in managed stack traces.</param>
    Task AnalyzeDumpAsync(string dumpPath, string logPath, bool useSymbols = false, IReadOnlyList<string>? symbolSearchPaths = null);
}
