using System.Diagnostics;
using System.Text;

namespace AccessLoop;

public sealed record CommandResult(int ExitCode, string Stdout, string Stderr);

public static class ProcessCommand
{
    public static async Task<CommandResult> RunAsync(
        string executable, IReadOnlyList<string> arguments, string workingDirectory,
        TimeSpan timeout, CancellationToken cancellationToken, int maximumOutputCharacters = 8 * 1024 * 1024)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (maximumOutputCharacters < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumOutputCharacters));
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException($"Could not start {executable}.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var stdout = ReadBoundedAsync(process.StandardOutput, maximumOutputCharacters, deadline.Token);
        var stderr = ReadBoundedAsync(process.StandardError, maximumOutputCharacters, deadline.Token);
        try
        {
            var pending = new List<Task> { stdout, stderr, process.WaitForExitAsync(deadline.Token) };
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending);
                await completed;
                pending.Remove(completed);
            }
            return new CommandResult(process.ExitCode, await stdout, await stderr);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException)
        {
            deadline.Cancel();
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            // Observe both stream tasks after terminating only the child command.
            try { await Task.WhenAll(stdout, stderr); }
            catch (Exception readError) when (readError is OperationCanceledException or IOException or InvalidDataException) { }
            if (ex is OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException($"{Path.GetFileName(executable)} exceeded {timeout.TotalSeconds:0} seconds.");
            }
            throw;
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximumCharacters, CancellationToken token)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), token);
            if (count == 0)
                return result.ToString();
            if (count > maximumCharacters - result.Length)
                throw new InvalidDataException($"Command output exceeded the {maximumCharacters}-character capture limit.");
            result.Append(buffer, 0, count);
        }
    }
}
