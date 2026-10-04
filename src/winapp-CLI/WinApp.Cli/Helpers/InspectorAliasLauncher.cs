// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Helpers;

internal enum InspectorAliasLaunchStatus
{
    Launched,
    AliasUnavailable,
    OwnerUnverifiable,
    WrongTarget,
    LaunchFailed,
    Exited,
}

internal sealed record InspectorAliasLaunchResult(
    InspectorAliasLaunchStatus Status,
    ILaunchedProcess? Process,
    string? Error = null,
    uint? ProcessId = null,
    int? ExitCode = null);

/// <summary>Inspector-only target checks; normal console runs must not require a surviving process.</summary>
internal sealed class InspectorAliasLauncher(IAppLauncherService launcher)
{
    internal Func<string, bool> ProxyExists { get; set; } = File.Exists;
    internal Func<string, ExecutionAliasResolver.AliasTarget?> ReadTarget { get; set; } = ExecutionAliasResolver.TryReadAliasTarget;

    internal async Task<InspectorAliasLaunchResult> LaunchAsync(
        InspectorAlias alias,
        string? arguments = null,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        LaunchStdioMode stdioMode = LaunchStdioMode.Inherit,
        TimeSpan? settleTime = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settleBudget = settleTime ?? TimeSpan.FromSeconds(2);
        ArgumentOutOfRangeException.ThrowIfLessThan(settleBudget, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(settleBudget.TotalMilliseconds, uint.MaxValue - 1d);
        var proxy = ExecutionAliasResolver.ResolveAliasPath(alias.AliasName);
        if (alias.Error is not null || alias.Target is null || proxy is null || !ProxyExists(proxy.FullName))
        {
            return new(InspectorAliasLaunchStatus.AliasUnavailable, null,
                alias.Error ?? "The inspector execution alias is missing or invalid. Check App execution aliases in Windows Settings after registering the package.");
        }

        var owner = ReadTarget(proxy.FullName);
        if (owner is null)
        {
            return new(InspectorAliasLaunchStatus.OwnerUnverifiable, null, "The execution alias owner could not be verified; refusing to launch it.");
        }
        if (!owner.MatchesApplication(alias.Target))
        {
            return new(InspectorAliasLaunchStatus.WrongTarget, null, "The execution alias does not target the requested application; refusing to launch it.");
        }

        ILaunchedProcess? process = null;
        var transferOwnership = false;
        try
        {
            process = launcher.LaunchExecutable(proxy.FullName, arguments, workingDirectory, stdioMode, environment);
            using var settle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            settle.CancelAfter(settleBudget);
            try
            {
                await process.WaitForExitAsync(settle.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The settle budget expired. Identity is checked against this same owned process below.
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                return Exited(process);
            }

            var actual = new ExecutionAliasResolver.AliasTarget(
                process.PackageFamilyName ?? "", process.ApplicationUserModelId, process.ExecutablePath);
            if (!actual.MatchesApplication(alias.Target))
            {
                return new(InspectorAliasLaunchStatus.WrongTarget, null,
                    "The launched process's package and executable could not be confirmed as the requested target.", process.ProcessId);
            }
            if (process.HasExited)
            {
                return Exited(process);
            }

            transferOwnership = true;
            return new(InspectorAliasLaunchStatus.Launched, process, ProcessId: process.ProcessId);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new(InspectorAliasLaunchStatus.LaunchFailed, null, $"Inspector launch failed: {RunFailure.Describe(ex)}");
        }
        finally
        {
            if (!transferOwnership)
            {
                process?.Dispose();
            }
        }
    }

    private static InspectorAliasLaunchResult Exited(ILaunchedProcess process) =>
        new(InspectorAliasLaunchStatus.Exited, null,
            "The app exited right after launch, before DevTools could inspect it.",
            process.ProcessId, process.ExitCode);
}
