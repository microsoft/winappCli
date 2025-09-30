using System.Diagnostics;

namespace Winsdk.Cli.Services;

/// <summary>
/// Service for executing PowerShell commands
/// </summary>
internal class PowerShellService
{
    /// <summary>
    /// Runs a PowerShell command and returns the exit code and output
    /// </summary>
    /// <param name="command">The PowerShell command to run</param>
    /// <param name="elevated">Whether to run with elevated privileges (UAC prompt)</param>
    /// <param name="environmentVariables">Optional dictionary of environment variables to set/override</param>
    /// <param name="verbose">Enable verbose logging</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Tuple containing (exitCode, stdout)</returns>
    public async Task<(int exitCode, string output)> RunCommandAsync(
        string command,
        bool elevated = false,
        Dictionary<string, string>? environmentVariables = null,
        bool verbose = false,
        CancellationToken cancellationToken = default)
    {
        if (verbose)
        {
            var elevatedText = elevated ? "elevated " : "";
            Console.WriteLine($"Running {elevatedText}PowerShell: {command}");
            if (elevated)
            {
                Console.WriteLine("UAC prompt may appear...");
            }
        }

        // Build a safe, profile-less, non-interactive PowerShell invocation
        static string ToEncodedCommand(string s)
        {
            var bytes = System.Text.Encoding.Unicode.GetBytes(s); // UTF-16LE
            return Convert.ToBase64String(bytes);
        }
        var encoded = ToEncodedCommand(command);

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            UseShellExecute = elevated, // Required for elevation, must be true for Verb=runas
            RedirectStandardOutput = !elevated, // Always redirect when not elevated so we can capture output
            RedirectStandardError = !elevated,
            RedirectStandardInput = !elevated, // close stdin so PS never waits for input
            CreateNoWindow = !elevated,
            WindowStyle = elevated ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden
        };

        // Apply custom environment variables if provided
        if (environmentVariables is not null)
        {
            foreach (var kvp in environmentVariables)
            {
                psi.Environment[kvp.Key] = kvp.Value;
            }
        }

        // Always clear PSModulePath to prevent PowerShell Core module conflicts when calling Windows PowerShell
        // This fixes the issue where calling powershell.exe from PowerShell Core causes module loading errors
        if (!psi.Environment.ContainsKey("PSModulePath"))
        {
            psi.Environment["PSModulePath"] = "";
        }

        if (elevated)
        {
            psi.Verb = "runas"; // This triggers UAC elevation
        }

        using var process = Process.Start(psi);
        if (process == null)
        {
            return (-1, "Failed to start PowerShell process");
        }

        string stdOut = string.Empty, stdErr = string.Empty;

        if (!elevated)
        {
            // Read both streams concurrently to avoid deadlocks
            var outTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errTask = process.StandardError.ReadToEndAsync(cancellationToken);

            // Close stdin immediately; we won’t provide input
            await process.StandardInput.FlushAsync(cancellationToken);
            process.StandardInput.Close();

            await Task.WhenAll(outTask, errTask);
            stdOut = outTask.Result;
            stdErr = errTask.Result;
        }

        await process.WaitForExitAsync(cancellationToken);

        if (verbose)
        {
            if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(stdErr))
            {
                Console.WriteLine($"PowerShell error: {stdErr}");
            }
            else if (!string.IsNullOrWhiteSpace(stdOut))
            {
                Console.WriteLine($"PowerShell output: {stdOut.Trim()}");
            }
        }

        // For elevated commands, exit codes may not be reliable, so we return 0 if no exception occurred
        var exitCode = elevated ? (process.ExitCode == 0 ? 0 : process.ExitCode) : process.ExitCode;
        
        return (exitCode, stdOut);
    }
}