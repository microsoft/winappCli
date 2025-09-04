using System.CommandLine;

namespace Winsdk.Cli.Commands;

internal class ToolCommand : Command
{
    public ToolCommand() : base("tool", "Run a build tool command with Windows SDK paths")
    {
        Aliases.Add("run-buildtool");
        this.TreatUnmatchedTokensAsErrors = false;

        SetAction(async (parseResult, ct) =>
        {
            var args = parseResult.UnmatchedTokens.ToArray();
            if (args.Length == 0)
            {
                Console.Error.WriteLine("No build tool command specified.");
                return 1;
            }
            var toolName = args[0];
            var toolArgs = args.Skip(1).ToArray();
            var toolPath = BuildToolsService.GetBuildToolPath(toolName);
            if (toolPath == null)
            {
                Console.Error.WriteLine($"Could not find '{toolName}' in the Windows SDK Build Tools.");
                return 1;
            }
            var processStartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = toolPath,
                Arguments = string.Join(" ", toolArgs.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            try
            {
                using var process = System.Diagnostics.Process.Start(processStartInfo);
                if (process == null)
                {
                    Console.Error.WriteLine($"Failed to start process for '{toolName}'.");
                    return 1;
                }
                process.OutputDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                        Console.Out.WriteLine(e.Data);
                };
                process.ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                        Console.Error.WriteLine(e.Data);
                };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                await process.WaitForExitAsync();
                return process.ExitCode;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error executing '{toolName}': {ex.Message}");
                return 1;
            }
        });
    }
}