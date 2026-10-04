// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Globalization;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Services;
using WinApp.Cli.Services.DevTools;

if (args[0] == "guest-comment-relay")
{
    return await WinApp.Cli.Program.RunAsync(args);
}
if (args[0] == "app")
{
    using var close = EventWaitHandle.OpenExisting(args[1]);
    return close.WaitOne(TimeSpan.FromSeconds(45)) ? 7 : 93;
}
if (args[0] == "tree")
{
    using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
    {
        ArgumentList = { "app", args[2] },
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    })!;
    using var current = Process.GetCurrentProcess();
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
    {
        Root = new GuestProcessStart(current.Id, current.StartTime.ToUniversalTime().Ticks),
        Child = new GuestProcessStart(child.Id, child.StartTime.ToUniversalTime().Ticks),
    }));
    using var close = EventWaitHandle.OpenExisting(args[1]);
    return close.WaitOne(TimeSpan.FromSeconds(45))
        ? args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 7 : 93;
}
if (args[0] == "relay-exit")
{
    string Value(string name) => args[Array.IndexOf(args, name) + 1];
    using var release = EventWaitHandle.OpenExisting(args[1]);
    using var stdout = Console.OpenStandardOutput();
    var request = new GuestCommentRequest(Value("--binding"), Value("--target"), Value("--epoch"),
        new(int.Parse(Value("--pid"), CultureInfo.InvariantCulture), long.Parse(Value("--start"), CultureInfo.InvariantCulture)),
        Guid.NewGuid().ToString("N"), "", "", null);
    await GuestCommentFrames.WriteAsync(stdout, new GuestCommentEnvelope("ready", request),
        GuestCommentsJsonContext.Default.GuestCommentEnvelope, CancellationToken.None);
    return release.WaitOne(TimeSpan.FromSeconds(30)) ? int.Parse(args[2], CultureInfo.InvariantCulture) : 93;
}

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
using var started = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
{
    ArgumentList = { "app", args[1] },
    UseShellExecute = false,
    CreateNoWindow = true,
})!;
using ILaunchedProcess app = args.Length > 2
    ? new DelayedExit(started, args[2], args[3]) : new LaunchedProcess(started);
using var input = Console.OpenStandardInput();
using var output = Console.OpenStandardOutput();
try
{
    return await GuestInspectedAppLifetime.RunAsync(app,
        new GuestProcessStart(started.Id, started.StartTime.ToUniversalTime().Ticks),
        new Inspection(), input, output, true, timeout.Token);
}
catch (OperationCanceledException)
{
    return 99;
}
catch (IOException)
{
    return 98;
}

internal sealed class Inspection : IDevToolsService
{
    public Task<DevToolsConnection> ConnectAsync(uint targetPid, bool showOverlay,
        DevToolsAccess requestedAccess, CancellationToken cancellationToken) =>
        Task.FromResult(DevToolsConnection.Ok(1, showOverlay));
}

// Delay notification, not actual process exit, so control deterministically wins Task.WhenAny.
internal sealed class DelayedExit(Process process, string resumeName, string observedName) : ILaunchedProcess
{
    private readonly LaunchedProcess _inner = new(process);
    private readonly EventWaitHandle _resume = EventWaitHandle.OpenExisting(resumeName);
    private readonly EventWaitHandle _observed = EventWaitHandle.OpenExisting(observedName);
    public uint ProcessId => _inner.ProcessId;
    public long? StartTicksUtc => _inner.StartTicksUtc;
    public bool HasExited
    {
        get
        {
            var exited = _inner.HasExited;
            if (exited)
            {
                _observed.Set();
            }
            return exited;
        }
    }
    public string? PackageFamilyName => null;
    public string? ApplicationUserModelId => null;
    public string? ExecutablePath => _inner.ExecutablePath;
    public int ExitCode => _inner.ExitCode;
    public void Kill() => _inner.Kill();
    public void KillProcessOnly() => _inner.KillProcessOnly();
    public bool RequestClose() => _inner.RequestClose();
    public async Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        await _inner.WaitForExitAsync(cancellationToken);
        await Task.Run(() => WaitHandle.WaitAny([_resume, cancellationToken.WaitHandle]), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }
    public void Dispose()
    {
        _resume.Dispose();
        _observed.Dispose();
        _inner.Dispose();
    }
}
