using System.CommandLine;
using System.CommandLine.Invocation;
using Winsdk.Cli.Services;

namespace Winsdk.Cli.Commands;

internal class GetWinsdkPathCommand : Command
{
    public static Option<bool> GlobalOption { get; }

    static GetWinsdkPathCommand()
    {
        GlobalOption = new Option<bool>("--global")
        {
            Description = "Get the global .winsdk directory instead of local"
        };
    }

    public GetWinsdkPathCommand() : base("get-winsdk-path", "Get the path to the .winsdk directory (local by default, global with --global)")
    {
        Options.Add(GlobalOption);
    }

    public class Handler(IWinsdkDirectoryService winsdkDirectoryService) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var verbose = parseResult.GetValue(WinSdkRootCommand.VerboseOption);
            var global = parseResult.GetValue(GlobalOption);

            try
            {
                string winsdkDir;
                string directoryType;
                
                if (global)
                {
                    // Get the global .winsdk directory
                    winsdkDir = winsdkDirectoryService.GetGlobalWinsdkDirectory();
                    directoryType = "Global";
                }
                else
                {
                    // Get the local .winsdk directory
                    winsdkDir = winsdkDirectoryService.GetLocalWinsdkDirectory(Directory.GetCurrentDirectory());
                    directoryType = "Local";
                }
                
                if (string.IsNullOrEmpty(winsdkDir))
                {
                    if (verbose)
                    {
                        Console.Error.WriteLine($"❌ {directoryType} .winsdk directory path could not be determined");
                    }
                    return Task.FromResult(1);
                }

                // For global directories, check if they exist
                if (global && !Directory.Exists(winsdkDir))
                {
                    if (verbose)
                    {
                        Console.Error.WriteLine($"❌ {directoryType} .winsdk directory not found: {winsdkDir}");
                        Console.Error.WriteLine($"   Make sure to run 'winsdk init' first");
                    }
                    return Task.FromResult(1);
                }

                // Output just the path for easy consumption by scripts
                Console.WriteLine(winsdkDir);
                
                if (verbose)
                {
                    var exists = Directory.Exists(winsdkDir);
                    var status = exists ? "exists" : "does not exist";
                    Console.Error.WriteLine($"📂 {directoryType} .winsdk directory: {winsdkDir} ({status})");
                }

                return Task.FromResult(0);
            }
            catch (Exception ex)
            {
                if (verbose)
                {
                    Console.Error.WriteLine($"❌ Error getting {(global ? "global" : "local")} winsdk directory: {ex.Message}");
                }
                else
                {
                    Console.Error.WriteLine($"{(global ? "Global" : "Local")} winsdk directory not found");
                }
                return Task.FromResult(1);
            }
        }
    }
}
