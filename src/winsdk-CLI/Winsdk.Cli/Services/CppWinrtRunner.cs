using System.Diagnostics;
using System.Text;

namespace Winsdk.Cli;

internal static class CppWinrtRunner
{
    public static async Task RunInlineAsync(string cppwinrtExe, IEnumerable<string> winmdInputs, string outputDir, bool verbose, CancellationToken cancellationToken = default)
    {
        var inputArgs = string.Join(" ", winmdInputs.Select(p => $"-input \"{p}\""));
        var args = $"-input sdk+ {inputArgs} -optimize -output \"{outputDir}\"";
        if (verbose) args += " -verbose";

        if (verbose)
        {
            Console.WriteLine($"cppwinrt: {cppwinrtExe} {args}");
        }

        var psi = new ProcessStartInfo
        {
            FileName = cppwinrtExe,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var p = Process.Start(psi)!;
        var so = await p.StandardOutput.ReadToEndAsync(cancellationToken);
        var se = await p.StandardError.ReadToEndAsync(cancellationToken);
        await p.WaitForExitAsync(cancellationToken);

        if (verbose)
        {
            if (!string.IsNullOrWhiteSpace(so)) Console.WriteLine(so);
            if (!string.IsNullOrWhiteSpace(se)) Console.WriteLine(se);
        }

        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException("cppwinrt execution failed");
        }
    }

    // CMake-aligned runner: generate a response file and invoke cppwinrt with @rsp
    public static async Task RunWithRspAsync(string cppwinrtExe, IEnumerable<string> winmdInputs, string outputDir, string workingDirectory, bool verbose, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDir);
        var rspPath = Path.Combine(outputDir, ".cppwinrt.rsp");

        var sb = new StringBuilder();
        sb.AppendLine("-input sdk+");
        foreach (var winmd in winmdInputs)
        {
            sb.AppendLine($"-input \"{winmd}\"");
        }
        sb.AppendLine("-optimize");
        sb.AppendLine($"-output \"{outputDir}\"");
        if (verbose) sb.AppendLine("-verbose");

        await File.WriteAllTextAsync(rspPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);

        if (verbose)
        {
            Console.WriteLine($"cppwinrt: {cppwinrtExe} @{rspPath}");
        }

        var psi = new ProcessStartInfo
        {
            FileName = cppwinrtExe,
            Arguments = $"@{rspPath}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

        using var p = Process.Start(psi)!;
        var so = await p.StandardOutput.ReadToEndAsync(cancellationToken);
        var se = await p.StandardError.ReadToEndAsync(cancellationToken);
        await p.WaitForExitAsync(cancellationToken);

        if (verbose)
        {
            if (!string.IsNullOrWhiteSpace(so)) Console.WriteLine(so);
            if (!string.IsNullOrWhiteSpace(se)) Console.WriteLine(se);
        }

        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException("cppwinrt execution failed");
        }
    }
}
