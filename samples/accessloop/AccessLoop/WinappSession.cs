using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace AccessLoop;

public interface IAppSession : IAsyncDisposable
{
    int ProcessId { get; }
    Task<JsonElement> UiAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

public interface IAppLauncher
{
    Task<IAppSession> LaunchAsync(Scenario scenario, string evidenceDirectory, CancellationToken cancellationToken);
}

public sealed class WinappLauncher(string executable) : IAppLauncher
{
    public Task<IAppSession> LaunchAsync(
        Scenario scenario, string evidenceDirectory, CancellationToken cancellationToken) =>
        LaunchProjectAsync(scenario.Project, scenario.TimeoutSeconds, evidenceDirectory, cancellationToken);

    public async Task<IAppSession> LaunchProjectAsync(
        string project, int timeoutSeconds, string evidenceDirectory, CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        var result = await ProcessCommand.RunAsync(executable,
            ["run", project, "--detach", "--json"],
            Path.GetDirectoryName(project)!, TimeSpan.FromMinutes(5), cancellationToken);
        var root = ParseResult(result, "run");
        var pid = root.GetProperty("ProcessId").GetInt32();
        var process = Process.GetProcessById(pid);
        // A singleton app may return an existing user's process. Never claim or close it.
        if (process.StartTime.ToUniversalTime() < started)
        {
            process.Dispose();
            throw new InvalidOperationException("winapp returned a pre-existing process; close your demo and retry.");
        }
        var session = new WinappSession(executable, process, project, timeoutSeconds, evidenceDirectory);
        try
        {
            File.WriteAllText(Path.Combine(evidenceDirectory, "launch.stdout.json"), result.Stdout);
            File.WriteAllText(Path.Combine(evidenceDirectory, "launch.stderr.txt"), result.Stderr);
            var ready = Stopwatch.StartNew();
            while (true)
            {
                process.Refresh();
                if (process.HasExited)
                    throw new InvalidOperationException("The demo exited before creating a window.");
                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    session.PinWindow(process.MainWindowHandle);
                    return session;
                }
                if (ready.Elapsed > TimeSpan.FromSeconds(timeoutSeconds))
                    throw new TimeoutException("The demo did not create a window before the readiness deadline.");
                await Task.Delay(100, cancellationToken);
            }
        }
        catch (Exception ex) when (ScenarioRunner.IsExecutionFailure(ex))
        {
            await session.DisposeAsync();
            throw;
        }
    }

    internal static JsonElement ParseResult(CommandResult result, string command)
    {
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"winapp {command} failed ({result.ExitCode}). {result.Stdout.Trim()} {result.Stderr.Trim()}");
        using var document = JsonDocument.Parse(result.Stdout);
        var root = document.RootElement;
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name.Equals("error", StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind != JsonValueKind.Null)
                throw new InvalidOperationException($"winapp {command}: {property.Value}");
        }
        return root.Clone();
    }
}

internal sealed class WinappSession(
    string executable, Process process, string project, int timeoutSeconds, string evidenceDirectory) : IAppSession
{
    private int sequence;
    private AuditTarget? target;
    public int ProcessId => process.Id;

    internal void PinWindow(nint windowHandle)
    {
        if (target is not null)
            throw new InvalidOperationException("The owned application window is already pinned.");
        target = new(process.Id, (long)windowHandle, process.ProcessName, process.StartTime.ToUniversalTime(), null, null);
        AuditWindowIdentity.Verify(target);
    }

    public async Task<JsonElement> UiAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (process.HasExited)
            throw new InvalidOperationException("The owned application exited before the scenario completed.");
        if (target is null)
            throw new InvalidOperationException("No owned window was pinned before interaction.");
        AuditWindowIdentity.Verify(target);
        var command = new List<string> { "ui" };
        command.AddRange(arguments);
        command.AddRange(["--window", target.WindowHandle.ToString(CultureInfo.InvariantCulture), "--json"]);
        var result = await ProcessCommand.RunAsync(executable, command,
            Path.GetDirectoryName(project)!,
            TimeSpan.FromSeconds(timeoutSeconds + 10), cancellationToken);
        AuditWindowIdentity.Verify(target);
        var prefix = Path.Combine(evidenceDirectory, $"{++sequence:D3}-{arguments[0]}");
        File.WriteAllText(prefix + ".json", result.Stdout);
        if (!string.IsNullOrWhiteSpace(result.Stderr))
            File.WriteAllText(prefix + ".stderr.txt", result.Stderr);
        return WinappLauncher.ParseResult(result, arguments[0]);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (process.HasExited)
                return;
            process.CloseMainWindow();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(deadline.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                using var killDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(killDeadline.Token);
            }
        }
        finally
        {
            process.Dispose();
        }
    }
}
