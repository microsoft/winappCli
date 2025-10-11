using System.CommandLine;
using System.CommandLine.Invocation;
using Winsdk.Cli.Services;

namespace Winsdk.Cli.Commands;

internal class ToolCommand : Command
{
    public static Option<bool> QuietOption { get; }

    static ToolCommand()
    {
        QuietOption = new Option<bool>("--quiet", "-q")
        {
            Description = "Suppress progress messages during auto-installation"
        };
    }
    public ToolCommand() : base("tool", "Run a build tool command with Windows SDK paths")
    {
        Aliases.Add("run-buildtool");
        this.TreatUnmatchedTokensAsErrors = false;
        Options.Add(QuietOption);
    }

    public class Handler(IBuildToolsService buildToolsService) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var args = parseResult.UnmatchedTokens.ToArray();
            var quiet = parseResult.GetValue(QuietOption);

            if (args.Length == 0)
            {
                Console.Error.WriteLine("No build tool command specified.");
                Console.Error.WriteLine($"Usage: winsdk tool [--quiet] <command> [args...]");
                Console.Error.WriteLine($"Example: winsdk tool makeappx.exe pack /o /d \"./msix\" /nv /p \"./dist/app.msix\"");
                return 1;
            }

            var toolName = args[0];
            var toolArgs = args.Skip(1).ToArray();

            // First, try to find the tool in existing installation
            var toolPath = buildToolsService.GetBuildToolPath(toolName);
            if (toolPath == null && !toolName.EndsWith(".exe"))
            {
                toolPath = buildToolsService.GetBuildToolPath(toolName + ".exe");
            }

            // If tool not found, ensure BuildTools are installed
            if (toolPath == null)
            {
                var binPath = await buildToolsService.EnsureBuildToolsAsync(quiet: quiet, cancellationToken: cancellationToken);
                if (binPath == null)
                {
                    Console.Error.WriteLine($"Could not install or find Windows SDK Build Tools.");
                    return 1;
                }

                // Try again after installation
                toolPath = buildToolsService.GetBuildToolPath(toolName);
                if (toolPath == null && !toolName.EndsWith(".exe"))
                {
                    toolPath = buildToolsService.GetBuildToolPath(toolName + ".exe");
                }
            }

            if (toolPath == null)
            {
                Console.Error.WriteLine($"Could not find '{toolName}' in the Windows SDK Build Tools.");
                Console.Error.WriteLine($"Usage: winsdk tool [--quiet] <command> [args...]");
                Console.Error.WriteLine($"Example: winsdk tool makeappx.exe pack /o /d \"./msix\" /nv /p \"./dist/app.msix\"");
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
                await process.WaitForExitAsync(cancellationToken);
                return process.ExitCode;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error executing '{toolName}': {ex.Message}");
                return 1;
            }
        }
    }
}
