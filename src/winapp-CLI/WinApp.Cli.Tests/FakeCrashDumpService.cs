// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Fake crash dump service that records calls without writing actual dumps.
/// </summary>
internal class FakeCrashDumpService : ICrashDumpService
{
    public List<(uint ProcessId, uint ThreadId)> WriteCalls { get; } = [];
    public List<(int Code, nuint Address, bool HasContext)> SavedRecords { get; } = [];
    public List<(int Code, nuint Address, nuint[]? Parameters)> CrashRecords { get; } = [];
    public string? FakeDumpPath { get; set; }
    public List<string> AnalyzeCalls { get; } = [];

    public string? WriteMiniDump(uint processId,
        byte[]? savedContext, uint savedThreadId,
        int savedExceptionCode, nuint savedExceptionAddress,
        int crashExceptionCode = 0, nuint crashExceptionAddress = 0,
        nuint[]? crashExceptionParameters = null)
    {
        WriteCalls.Add((processId, savedThreadId));
        SavedRecords.Add((savedExceptionCode, savedExceptionAddress, savedContext != null));
        CrashRecords.Add((crashExceptionCode, crashExceptionAddress, crashExceptionParameters));
        return FakeDumpPath;
    }

    public Task AnalyzeDumpAsync(string dumpPath, string logPath, bool useSymbols = false, IReadOnlyList<string>? symbolSearchPaths = null)
    {
        AnalyzeCalls.Add(dumpPath);
        return Task.CompletedTask;
    }
}
